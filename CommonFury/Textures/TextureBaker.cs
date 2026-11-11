using System.Numerics;
using CommonFury.Geometry;
using CommonFury.Numerics;

namespace CommonFury.Textures;

/// <summary>Atlas size, channel count and search settings for <see cref="TextureBaker.Bake"/>.</summary>
/// <param name="Width">Atlas width in texels.</param>
/// <param name="Height">Atlas height in texels.</param>
/// <param name="Channels">Floats the sampler writes per texel, 1–64.</param>
/// <param name="SearchDistance">How far, in position units, the source surface may lie from the target surface, on either side.</param>
/// <param name="Dilation">Rings of empty texels around baked texels that are filled with the mean of their filled neighbours.</param>
public readonly record struct TextureBakeOptions(int Width, int Height, int Channels, float SearchDistance, int Dilation);

/// <summary>Where one texel of the target surface found the source surface.</summary>
/// <param name="X">Texel column.</param>
/// <param name="Y">Texel row.</param>
/// <param name="Position">Point on the target surface at the texel center.</param>
/// <param name="Normal">Unit normal of the target surface there.</param>
/// <param name="TargetTriangle">Target triangle containing the texel center.</param>
/// <param name="Source">The source point: the first met by a ray along the normal from the outer edge of the search band, or the
/// nearest one when no ray meets the source. Its distance is measured from <paramref name="Position"/>.</param>
public readonly record struct TextureBakeSample(int X, int Y, Vector3 Position, Vector3 Normal, int TargetTriangle, TriangleHit Source);

/// <summary>Writes the channels of one texel from the source point it found.</summary>
public delegate void TextureBakeSampler(in TextureBakeSample sample, Span<float> texel);

/// <summary>A baked image: floats per texel in rows from the first row, matching <see cref="UvAtlasMesh.Uvs"/>.</summary>
public sealed class BakedTexture
{
    internal BakedTexture(int width, int height, int channels, float[] texels, bool[] baked)
    {
        Width = width;
        Height = height;
        Channels = channels;
        Texels = texels;
        Baked = baked;
    }

    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }

    /// <summary>Owned texels, <see cref="Channels"/> floats each, row-major. Texels that are neither baked nor dilated are zero.</summary>
    public float[] Texels { get; }

    /// <summary>Owned flags for texels whose center lies on the target surface and found the source; dilated texels are false.</summary>
    public bool[] Baked { get; }
}

/// <summary>Transfers surface data from a source mesh to the texture atlas of a target mesh, such as a simplified or remeshed
/// stand-in, by finding for every texel the source point the target surface stands for.</summary>
/// <remarks>Each texel whose center lies inside a target triangle casts a ray along the target normal from the outside of the
/// search band inward, and takes the first source triangle it meets; when no ray meets the source, it takes the nearest source
/// point within the search distance. The sampler decides what to write, so any material model, colour space or normal encoding
/// stays with the caller. Texels are visited one at a time in row order, so the sampler needs no synchronization.</remarks>
public static class TextureBaker
{
    /// <param name="positions">Target vertex positions.</param>
    /// <param name="normals">Target vertex normals, interpolated across triangles, or empty for face normals.</param>
    /// <param name="uvs">Target texture coordinates, 0–1 across the atlas with (0, 0) at the corner of its first row.</param>
    /// <param name="indices">Target triangle list.</param>
    /// <param name="source">The source surface.</param>
    /// <param name="options">Atlas and search settings.</param>
    /// <param name="sampler">Writes each baked texel.</param>
    /// <exception cref="ArgumentException">Lengths do not match, an index is out of range, or a value is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size, channel count, distance or dilation is out of range.</exception>
    public static BakedTexture Bake(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals, ReadOnlySpan<Vector2> uvs,
        ReadOnlySpan<int> indices, TriangleBvh source, TextureBakeOptions options, TextureBakeSampler sampler)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sampler);
        MeshMath.ValidateTriangleIndices(indices, positions.Length);
        if (uvs.Length != positions.Length || !normals.IsEmpty && normals.Length != positions.Length)
            throw new ArgumentException("Texture coordinates and normals must match the positions.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Width, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Height, nameof(options));
        if (options.Channels is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Channel count must be 1–64.");
        if (!(options.SearchDistance > 0) || float.IsPositiveInfinity(options.SearchDistance))
            throw new ArgumentOutOfRangeException(nameof(options), "Search distance must be positive and finite.");
        ArgumentOutOfRangeException.ThrowIfNegative(options.Dilation, nameof(options));
        for (int i = 0; i < positions.Length; i++)
            if (!VectorMath.IsFinite(positions[i]) || !VectorMath.IsFinite(uvs[i]) || !normals.IsEmpty && !VectorMath.IsFinite(normals[i]))
                throw new ArgumentException("Positions, normals and texture coordinates must be finite.");

        int width = options.Width, height = options.Height, channels = options.Channels;
        var texels = new float[checked(width * height * channels)];
        var baked = new bool[width * height];
        var owner = new int[width * height];
        Array.Fill(owner, -1);
        float search = options.SearchDistance;
        var scratch = new float[channels];
        for (int t = 0; t < indices.Length / 3; t++)
        {
            int ia = indices[t * 3], ib = indices[t * 3 + 1], ic = indices[t * 3 + 2];
            Vector3 a = positions[ia], b = positions[ib], c = positions[ic];
            var face = Vector3.Cross(b - a, c - a);
            if (face.LengthSquared() == 0)
                continue;
            face = Vector3.Normalize(face);
            var size = new Vector2(width, height);
            int triangle = t;
            Vector3 na = normals.IsEmpty ? face : normals[ia], nb = normals.IsEmpty ? face : normals[ib], nc = normals.IsEmpty ? face : normals[ic];
            Raster.Triangle(uvs[ia] * size, uvs[ib] * size, uvs[ic] * size, width, height, (x, y, weights) =>
            {
                int texel = y * width + x;
                // Overlapping texture coordinates keep the first triangle's texel.
                if (owner[texel] >= 0)
                    return;
                owner[texel] = triangle;
                var point = a * weights.X + b * weights.Y + c * weights.Z;
                var normal = na * weights.X + nb * weights.Y + nc * weights.Z;
                normal = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : face;
                // Distances are reported from the target point, whichever query found the source.
                TriangleHit hit;
                if (source.Raycast(point + normal * search, -normal, 2 * search, out var ray))
                    hit = ray with { Distance = MathF.Abs(ray.Distance - search) };
                else if (!source.FindNearest(point, search, out hit))
                    return;
                Array.Clear(scratch);
                sampler(new TextureBakeSample(x, y, point, normal, triangle, hit), scratch);
                for (int k = 0; k < channels; k++)
                    if (!float.IsFinite(scratch[k]))
                        throw new InvalidOperationException("The sampler wrote a value that is not finite.");
                scratch.CopyTo(texels, texel * channels);
                baked[texel] = true;
            });
        }
        Dilate(texels, baked, width, height, channels, options.Dilation);
        return new BakedTexture(width, height, channels, texels, baked);
    }

    // Each ring fills empty texels that touch a filled texel (8-neighbourhood) with the mean of those neighbours.
    private static void Dilate(float[] texels, bool[] baked, int width, int height, int channels, int rings)
    {
        var filled = (bool[])baked.Clone();
        var next = new List<int>();
        var sum = new float[channels];
        for (int ring = 0; ring < rings; ring++)
        {
            next.Clear();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    if (filled[y * width + x])
                        continue;
                    int count = 0;
                    Array.Clear(sum);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= width || ny >= height || !filled[ny * width + nx])
                                continue;
                            for (int k = 0; k < channels; k++)
                                sum[k] += texels[(ny * width + nx) * channels + k];
                            count++;
                        }
                    if (count == 0)
                        continue;
                    for (int k = 0; k < channels; k++)
                        texels[(y * width + x) * channels + k] = sum[k] / count;
                    next.Add(y * width + x);
                }
            if (next.Count == 0)
                break;
            foreach (int texel in next)
                filled[texel] = true;
        }
    }
}
