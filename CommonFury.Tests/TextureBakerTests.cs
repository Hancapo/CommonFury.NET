using System.Numerics;
using CommonFury.Geometry;
using CommonFury.Textures;
using Xunit;

namespace CommonFury.Tests;

public sealed class TextureBakerTests
{
    private static readonly Vector3[] Square = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)];
    private static readonly int[] SquareIndices = [0, 1, 2, 0, 2, 3];

    [Fact]
    public void TransfersTheSourcePointBelowEachTexel()
    {
        var source = new TriangleBvh(Square, SquareIndices);
        var (positions, uvs, indices) = Target(0.05f);
        var result = TextureBaker.Bake(positions, [], uvs, indices, source, new(64, 64, 2, 0.2f, 2), (in TextureBakeSample sample, Span<float> texel) =>
        {
            var p = SourcePoint(sample.Source);
            texel[0] = p.X;
            texel[1] = p.Y;
            Assert.Equal(0.05f, sample.Source.Distance, 4);
            Assert.True(Vector3.Distance(Vector3.UnitZ, sample.Normal) < 1e-5f);
        });
        int baked = 0;
        for (int i = 0; i < result.Baked.Length; i++)
        {
            if (!result.Baked[i])
                continue;
            baked++;
            // The texel's own target point, recovered from its row and column through the target UV mapping.
            var target = TargetPoint(positions, uvs, indices, i % 64, i / 64);
            Assert.Equal(target.X, result.Texels[i * 2], 4);
            Assert.Equal(target.Y, result.Texels[i * 2 + 1], 4);
        }
        // The target covers about density² texels; nearly all of them find the source.
        var atlasDensity = UvAtlas.Generate(positions, indices, new(64, 64, 2, 0.5f)).TexelsPerUnit;
        Assert.InRange(baked, 0.9 * atlasDensity * atlasDensity, 1.1 * atlasDensity * atlasDensity);
        int filled = Enumerable.Range(0, result.Baked.Length).Count(i => result.Texels[i * 2] != 0 || result.Texels[i * 2 + 1] != 0);
        Assert.True(filled > baked, "Dilation fills texels around the baked ones.");
    }

    [Fact]
    public void FallsBackToTheNearestPointWhenNoRayMeetsTheSource()
    {
        // The source lies beside the target, so rays along the normal miss it.
        var source = new TriangleBvh([.. Square.Select(p => p + new Vector3(2, 0, 0))], SquareIndices);
        var (positions, uvs, indices) = Target(0.05f);
        int samples = 0;
        TextureBaker.Bake(positions, [], uvs, indices, source, new(32, 32, 1, 3f, 0), (in TextureBakeSample sample, Span<float> texel) =>
        {
            samples++;
            float expected = MathF.Sqrt((2 - sample.Position.X) * (2 - sample.Position.X) + 0.05f * 0.05f);
            Assert.Equal(expected, sample.Source.Distance, 4);
            // The nearest source point is on its near edge, x = 2, which is x = 0 before the shift.
            Assert.Equal(0, SourcePoint(sample.Source).X, 4);
            texel[0] = 1;
        });
        Assert.True(samples > 0);
        var none = TextureBaker.Bake(positions, [], uvs, indices, source, new(32, 32, 1, 0.5f, 4), (in TextureBakeSample _, Span<float> _) => { });
        Assert.DoesNotContain(true, none.Baked);
        Assert.All(none.Texels, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ValidatesArguments()
    {
        var source = new TriangleBvh(Square, SquareIndices);
        var (positions, uvs, indices) = Target(0.05f);
        TextureBakeSampler nothing = (in TextureBakeSample _, Span<float> _) => { };
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureBaker.Bake(positions, [], uvs, indices, source, new(16, 16, 0, 1, 0), nothing));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureBaker.Bake(positions, [], uvs, indices, source, new(16, 16, 1, 0, 0), nothing));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureBaker.Bake(positions, [], uvs, indices, source, new(16, 16, 1, float.NaN, 0), nothing));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureBaker.Bake(positions, [], uvs, indices, source, new(0, 16, 1, 1, 0), nothing));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureBaker.Bake(positions, [], uvs, indices, source, new(16, 16, 1, 1, -1), nothing));
        Assert.Throws<ArgumentException>(() => TextureBaker.Bake(positions, [], uvs[..^1], indices, source, new(16, 16, 1, 1, 0), nothing));
        Assert.Throws<ArgumentException>(() => TextureBaker.Bake(positions, [Vector3.UnitZ], uvs, indices, source, new(16, 16, 1, 1, 0), nothing));
        Assert.Throws<InvalidOperationException>(() => TextureBaker.Bake(positions, [], uvs, indices, source, new(16, 16, 1, 1, 0),
            (in TextureBakeSample _, Span<float> texel) => texel[0] = float.NaN));
    }

    // The unit square lifted by `height`, with an atlas of its own.
    private static (Vector3[] Positions, Vector2[] Uvs, int[] Indices) Target(float height)
    {
        Vector3[] lifted = [.. Square.Select(p => p + new Vector3(0, 0, height))];
        var atlas = UvAtlas.Generate(lifted, SquareIndices, new(64, 64, 2, 0.5f));
        return ([.. atlas.SourceVertices.Select(v => lifted[v])], atlas.Uvs, atlas.Indices);
    }

    private static Vector3 SourcePoint(TriangleHit hit)
    {
        // Source triangle corners in the order the square lists them.
        var t = SquareIndices.AsSpan(hit.Triangle * 3, 3);
        return Square[t[0]] * hit.Barycentric.X + Square[t[1]] * hit.Barycentric.Y + Square[t[2]] * hit.Barycentric.Z;
    }

    // Inverts the target UV mapping at a texel center by brute force over its triangles.
    private static Vector3 TargetPoint(Vector3[] positions, Vector2[] uvs, int[] indices, int x, int y)
    {
        var p = new Vector2((x + 0.5f) / 64, (y + 0.5f) / 64);
        for (int t = 0; t < indices.Length; t += 3)
        {
            Vector2 a = uvs[indices[t]], b = uvs[indices[t + 1]], c = uvs[indices[t + 2]];
            float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            float wb = ((p.X - a.X) * (c.Y - a.Y) - (p.Y - a.Y) * (c.X - a.X)) / area;
            float wc = ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)) / area;
            if (wb >= -1e-4f && wc >= -1e-4f && wb + wc <= 1 + 1e-4f)
                return positions[indices[t]] * (1 - wb - wc) + positions[indices[t + 1]] * wb + positions[indices[t + 2]] * wc;
        }
        throw new InvalidOperationException("Texel center outside every target triangle.");
    }
}
