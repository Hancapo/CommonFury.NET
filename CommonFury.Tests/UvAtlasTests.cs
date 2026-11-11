using System.Numerics;
using CommonFury.Geometry;
using Xunit;

namespace CommonFury.Tests;

public sealed class UvAtlasTests
{
    [Fact]
    public void CubeGetsOneChartPerFace()
    {
        var (positions, indices) = Cube();
        var atlas = UvAtlas.Generate(positions, indices, new(64, 64, 2, 0.5f));
        Assert.Equal(6, atlas.ChartCount);
        Assert.Equal(24, atlas.SourceVertices.Length);
        AssertValid(positions, indices, atlas, 64, 64, 2, 0.5f);
    }

    [Fact]
    public void CurvedSurfacesSplitIntoNonOverlappingCharts()
    {
        var (positions, indices) = Sphere(3);
        var atlas = UvAtlas.Generate(positions, indices, new(256, 128, 2, 0.6f));
        Assert.InRange(atlas.ChartCount, 8, 200);
        AssertValid(positions, indices, atlas, 256, 128, 2, 0.6f);
        var again = UvAtlas.Generate(positions, indices, new(256, 128, 2, 0.6f));
        Assert.Equal(atlas.Indices, again.Indices);
        Assert.Equal(atlas.Uvs, again.Uvs);
    }

    [Fact]
    public void ChartsThatWouldOverlapThemselvesAreSplit()
    {
        // Two turns of a gently sloped ramp: every face is nearly upward, so one chart would fold over itself in projection.
        var positions = new List<Vector3>();
        var indices = new List<int>();
        int steps = 96;
        for (int s = 0; s <= steps; s++)
        {
            float angle = s * 4 * MathF.PI / steps, rise = s * 0.02f;
            positions.Add(new(MathF.Cos(angle) * 1, MathF.Sin(angle) * 1, rise));
            positions.Add(new(MathF.Cos(angle) * 2, MathF.Sin(angle) * 2, rise));
        }
        for (int s = 0; s < steps; s++)
        {
            int i = s * 2;
            indices.AddRange([i, i + 1, i + 3, i, i + 3, i + 2]);
        }
        var atlas = UvAtlas.Generate(positions.ToArray(), indices.ToArray(), new(128, 128, 1, 1.2f));
        Assert.True(atlas.ChartCount >= 2);
        AssertValid([.. positions], [.. indices], atlas, 128, 128, 1, 1.2f);
    }

    [Fact]
    public void ValidatesArguments()
    {
        var (positions, indices) = Cube();
        Assert.Throws<ArgumentOutOfRangeException>(() => UvAtlas.Generate(positions, indices, new(4, 64, 2, 0.5f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => UvAtlas.Generate(positions, indices, new(64, 64, -1, 0.5f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => UvAtlas.Generate(positions, indices, new(64, 64, 1, 2f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => UvAtlas.Generate(positions, indices, new(64, 64, 1, float.NaN)));
        Assert.Throws<ArgumentException>(() => UvAtlas.Generate(positions, [0, 1], new(64, 64, 1, 0.5f)));
        // Six charts with 20 texels of padding cannot fit an 48 x 48 atlas.
        Assert.Throws<ArgumentOutOfRangeException>(() => UvAtlas.Generate(positions, indices, new(48, 48, 20, 0.5f)));
        Assert.Empty(UvAtlas.Generate(positions, [], new(8, 8, 1, 0.5f)).Indices);
    }

    private static void AssertValid(Vector3[] positions, int[] indices, UvAtlasMesh atlas, int width, int height, int padding, float angle)
    {
        Assert.Equal(indices.Length, atlas.Indices.Length);
        for (int corner = 0; corner < indices.Length; corner++)
            Assert.Equal(positions[indices[corner]], positions[atlas.SourceVertices[atlas.Indices[corner]]]);
        Assert.All(atlas.Uvs, uv => Assert.True(uv.X >= 0 && uv.X <= 1 && uv.Y >= 0 && uv.Y <= 1, $"{uv}"));

        // One scale everywhere: a face tilted from its chart plane by at most the chart angle keeps between cos(angle) and all
        // of the squared density per unit area.
        double density = atlas.TexelsPerUnit;
        var texel = atlas.Uvs.Select(uv => new Vector2(uv.X * width, uv.Y * height)).ToArray();
        for (int t = 0; t < indices.Length; t += 3)
        {
            Vector3 a = positions[indices[t]], b = positions[indices[t + 1]], c = positions[indices[t + 2]];
            double area = Vector3.Cross(b - a, c - a).Length() / 2;
            Vector2 ta = texel[atlas.Indices[t]], tb = texel[atlas.Indices[t + 1]], tc = texel[atlas.Indices[t + 2]];
            double texelArea = Math.Abs((tb.X - ta.X) * (tc.Y - ta.Y) - (tb.Y - ta.Y) * (tc.X - ta.X)) / 2;
            double full = area * density * density;
            Assert.InRange(texelArea, full * Math.Cos(angle) - 1e-3, full * (1 + 1e-4) + 1e-3);
        }

        // No texel center lies strictly inside two triangles.
        var count = new int[width * height];
        for (int t = 0; t < indices.Length; t += 3)
        {
            Vector2 a = texel[atlas.Indices[t]], b = texel[atlas.Indices[t + 1]], c = texel[atlas.Indices[t + 2]];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
                    if (d1 > 1e-4f && d2 > 1e-4f && d3 > 1e-4f || d1 < -1e-4f && d2 < -1e-4f && d3 < -1e-4f)
                        count[y * width + x]++;
                }
        }
        Assert.True(count.Max() <= 1, "Two triangles share a texel.");

        // Charts (connected through shared new vertices) stay at least the padding apart.
        var chartOf = Components(atlas.Indices, atlas.Uvs.Length);
        var boxes = new Dictionary<int, (Vector2 Min, Vector2 Max)>();
        for (int v = 0; v < texel.Length; v++)
        {
            int chart = chartOf[v];
            boxes[chart] = boxes.TryGetValue(chart, out var box) ? (Vector2.Min(box.Min, texel[v]), Vector2.Max(box.Max, texel[v])) : (texel[v], texel[v]);
        }
        var list = boxes.Values.ToList();
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
            {
                float gapX = MathF.Max(list[j].Min.X - list[i].Max.X, list[i].Min.X - list[j].Max.X);
                float gapY = MathF.Max(list[j].Min.Y - list[i].Max.Y, list[i].Min.Y - list[j].Max.Y);
                Assert.True(MathF.Max(gapX, gapY) >= padding, "Charts are closer than the padding.");
            }
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static int[] Components(int[] indices, int vertexCount)
    {
        var parent = Enumerable.Range(0, vertexCount).ToArray();
        int Find(int v) => parent[v] == v ? v : parent[v] = Find(parent[v]);
        for (int t = 0; t < indices.Length; t += 3)
        {
            parent[Find(indices[t + 1])] = Find(indices[t]);
            parent[Find(indices[t + 2])] = Find(indices[t]);
        }
        return [.. Enumerable.Range(0, vertexCount).Select(Find)];
    }

    private static (Vector3[] Positions, int[] Indices) Cube()
    {
        Vector3[] positions = [.. Enumerable.Range(0, 8).Select(i => new Vector3(i & 1, i >> 1 & 1, i >> 2 & 1))];
        int[] indices = [0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5];
        return (positions, indices);
    }

    private static (Vector3[] Positions, int[] Indices) Sphere(int levels)
    {
        var positions = new List<Vector3> { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        var indices = new List<int> { 0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4, 2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5 };
        for (int level = 0; level < levels; level++)
        {
            var midpoints = new Dictionary<(int, int), int>();
            int Midpoint(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                if (!midpoints.TryGetValue(key, out int index))
                {
                    index = positions.Count;
                    positions.Add(Vector3.Normalize(positions[a] + positions[b]));
                    midpoints[key] = index;
                }
                return index;
            }
            var next = new List<int>();
            for (int t = 0; t < indices.Count; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2], ab = Midpoint(a, b), bc = Midpoint(b, c), ca = Midpoint(c, a);
                next.AddRange([a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca]);
            }
            indices = next;
        }
        return ([.. positions], [.. indices]);
    }
}
