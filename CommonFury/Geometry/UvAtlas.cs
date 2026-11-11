using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Geometry;

/// <summary>Size and charting settings for <see cref="UvAtlas.Generate"/>.</summary>
/// <param name="Width">Atlas width in texels.</param>
/// <param name="Height">Atlas height in texels.</param>
/// <param name="Padding">Empty texels kept around every chart, for filtering and mip levels.</param>
/// <param name="MaximumChartAngle">Largest angle, in radians below π/2, between a face normal and its chart's normal.</param>
public readonly record struct UvAtlasOptions(int Width, int Height, int Padding, float MaximumChartAngle);

/// <summary>A mesh with one new texture coordinate set: vertices are split where charts meet.</summary>
public sealed class UvAtlasMesh
{
    internal UvAtlasMesh(int[] sourceVertices, int[] indices, Vector2[] uvs, float texelsPerUnit, int chartCount)
    {
        SourceVertices = sourceVertices;
        Indices = indices;
        Uvs = uvs;
        TexelsPerUnit = texelsPerUnit;
        ChartCount = chartCount;
    }

    /// <summary>Owned map from each new vertex to the source vertex it copies.</summary>
    public int[] SourceVertices { get; }

    /// <summary>Owned triangle list into the new vertices, in source triangle order.</summary>
    public int[] Indices { get; }

    /// <summary>Owned coordinates per new vertex, 0–1 across the atlas, with (0, 0) at the corner of its first texel row.</summary>
    public Vector2[] Uvs { get; }

    /// <summary>Atlas texels per unit of length on every chart's plane. Faces tilted from their chart's plane by θ are foreshortened
    /// by cos θ across the tilt, which the chart angle bounds.</summary>
    public float TexelsPerUnit { get; }

    public int ChartCount { get; }
}

/// <summary>Generates non-overlapping texture coordinates for baking: faces are grouped into nearly planar charts, each chart is
/// projected onto its plane, and the charts are packed into the atlas at one scale.</summary>
/// <remarks>Faces join a chart across shared edges, matched by position, while their normals stay within the chart angle. A chart
/// whose projection would cover any texel twice is split, so no two surface points share a texel. Faces without area get a
/// chart of their own. The result is deterministic.</remarks>
public static class UvAtlas
{
    /// <exception cref="ArgumentException">An index is out of range or a position is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The atlas is too small for its padding, or the angle is out of range.</exception>
    public static UvAtlasMesh Generate(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, UvAtlasOptions options)
    {
        MeshMath.ValidateTriangleIndices(indices, positions.Length);
        foreach (int index in indices)
            if (!VectorMath.IsFinite(positions[index]))
                throw new ArgumentException("Positions must be finite.", nameof(positions));
        ArgumentOutOfRangeException.ThrowIfNegative(options.Padding);
        if (options.Width <= 2 * options.Padding || options.Height <= 2 * options.Padding)
            throw new ArgumentOutOfRangeException(nameof(options), "The atlas must be larger than twice its padding.");
        if (!(options.MaximumChartAngle >= 0 && options.MaximumChartAngle < MathF.PI / 2))
            throw new ArgumentOutOfRangeException(nameof(options), "The chart angle must lie in [0, π/2).");

        int faceCount = indices.Length / 3;
        var normals = new Vector3[faceCount];
        var areas = new float[faceCount];
        for (int f = 0; f < faceCount; f++)
        {
            var n = Vector3.Cross(positions[indices[f * 3 + 1]] - positions[indices[f * 3]], positions[indices[f * 3 + 2]] - positions[indices[f * 3]]);
            areas[f] = n.Length() / 2;
            normals[f] = areas[f] > 0 ? Vector3.Normalize(n) : Vector3.Zero;
        }
        var neighbours = Neighbours(positions, indices);
        var charts = Grow(normals, areas, neighbours, MathF.Cos(options.MaximumChartAngle));
        var flat = new List<Chart>();
        foreach (var faces in charts)
            flat.Add(Project(positions, indices, faces, normals, areas));

        // Choose the largest density whose packing fits, then split any chart that overlaps itself at that density and repeat.
        while (true)
        {
            float density = Fit(flat, options);
            var overlapping = flat.FindIndex(chart => Overlaps(chart, density));
            if (overlapping < 0)
                return Emit(indices, flat, density, options, positions.Length);
            var chart = flat[overlapping];
            flat.RemoveAt(overlapping);
            foreach (var half in Halve(chart, neighbours))
                flat.Insert(overlapping, Project(positions, indices, half, normals, areas));
        }
    }

    // Faces sharing an edge, matched by the edge's two positions.
    private static List<int>[] Neighbours(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices)
    {
        int faceCount = indices.Length / 3;
        var result = new List<int>[faceCount];
        var byEdge = new Dictionary<(Vector3, Vector3), List<int>>();
        for (int f = 0; f < faceCount; f++)
        {
            result[f] = [];
            for (int k = 0; k < 3; k++)
            {
                Vector3 a = positions[indices[f * 3 + k]], b = positions[indices[f * 3 + (k + 1) % 3]];
                var key = Order(a, b);
                if (!byEdge.TryGetValue(key, out var list))
                    byEdge[key] = list = [];
                list.Add(f);
            }
        }
        foreach (var list in byEdge.Values)
            foreach (int f in list)
                foreach (int g in list)
                    if (f != g && !result[f].Contains(g))
                        result[f].Add(g);
        return result;
    }

    private static (Vector3, Vector3) Order(Vector3 a, Vector3 b) =>
        a.X < b.X || a.X == b.X && (a.Y < b.Y || a.Y == b.Y && a.Z <= b.Z) ? (a, b) : (b, a);

    // Region growing from the largest unassigned face, breadth first, against the seed's normal and the chart's running mean.
    private static List<List<int>> Grow(Vector3[] normals, float[] areas, List<int>[] neighbours, float cosine)
    {
        int faceCount = normals.Length;
        var chartOf = new int[faceCount];
        Array.Fill(chartOf, -1);
        var seeds = Enumerable.Range(0, faceCount).OrderByDescending(f => areas[f]).ThenBy(f => f).ToArray();
        var charts = new List<List<int>>();
        var queue = new Queue<int>();
        foreach (int seed in seeds)
        {
            if (chartOf[seed] >= 0)
                continue;
            var faces = new List<int> { seed };
            chartOf[seed] = charts.Count;
            if (areas[seed] > 0)
            {
                Vector3 sum = normals[seed] * areas[seed];
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    int face = queue.Dequeue();
                    foreach (int next in neighbours[face])
                    {
                        if (chartOf[next] >= 0 || areas[next] == 0)
                            continue;
                        var mean = Vector3.Normalize(sum);
                        if (Vector3.Dot(normals[next], normals[seed]) < cosine || Vector3.Dot(normals[next], mean) < cosine)
                            continue;
                        chartOf[next] = charts.Count;
                        faces.Add(next);
                        sum += normals[next] * areas[next];
                        queue.Enqueue(next);
                    }
                }
            }
            charts.Add(faces);
        }
        return charts;
    }

    // Splits a chart's faces at the median of their projected centers along the chart's long axis, then into connected pieces, so
    // every chart stays connected. A single face never overlaps itself, so repeated halving ends.
    private static List<List<int>> Halve(Chart chart, List<int>[] neighbours)
    {
        bool alongX = chart.Size.X >= chart.Size.Y;
        var ordered = Enumerable.Range(0, chart.Faces.Count)
            .OrderBy(i =>
            {
                var center = (chart.Corners[i * 3] + chart.Corners[i * 3 + 1] + chart.Corners[i * 3 + 2]) / 3;
                return alongX ? center.X : center.Y;
            })
            .ThenBy(i => chart.Faces[i])
            .Select(i => chart.Faces[i])
            .ToList();
        int half = ordered.Count / 2;
        var pieces = new List<List<int>>();
        foreach (var part in (List<int>[])[ordered.GetRange(0, half), ordered.GetRange(half, ordered.Count - half)])
        {
            var remaining = new HashSet<int>(part);
            foreach (int start in part)
            {
                if (!remaining.Remove(start))
                    continue;
                var piece = new List<int> { start };
                for (int i = 0; i < piece.Count; i++)
                    foreach (int next in neighbours[piece[i]])
                        if (remaining.Remove(next))
                            piece.Add(next);
                pieces.Add(piece);
            }
        }
        return pieces;
    }

    private sealed class Chart
    {
        public required List<int> Faces;
        // Corner coordinates per face corner (three per face, in face order), in source units, with the minimum at the origin.
        public required Vector2[] Corners;
        public required Vector2 Size;
        // Placement in texels, set by packing.
        public Vector2 Offset;
    }

    private static Chart Project(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, List<int> faces, Vector3[] normals, float[] areas)
    {
        Vector3 sum = Vector3.Zero;
        foreach (int f in faces)
            sum += normals[f] * areas[f];
        var normal = sum.LengthSquared() > 0 ? Vector3.Normalize(sum) : Vector3.UnitZ;
        var helper = MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var u = Vector3.Normalize(Vector3.Cross(helper, normal));
        var v = Vector3.Cross(normal, u);
        var corners = new Vector2[faces.Count * 3];
        for (int i = 0; i < faces.Count; i++)
            for (int k = 0; k < 3; k++)
            {
                var p = positions[indices[faces[i] * 3 + k]];
                corners[i * 3 + k] = new(Vector3.Dot(p, u), Vector3.Dot(p, v));
            }
        // Rotate to the principal axes of the corners, which tightens the bounding box of elongated charts.
        var mean = Vector2.Zero;
        foreach (var c in corners)
            mean += c;
        mean /= corners.Length;
        float xx = 0, xy = 0, yy = 0;
        foreach (var c in corners)
        {
            var d = c - mean;
            xx += d.X * d.X;
            xy += d.X * d.Y;
            yy += d.Y * d.Y;
        }
        float angle = 0.5f * MathF.Atan2(2 * xy, xx - yy);
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i < corners.Length; i++)
        {
            var d = corners[i];
            corners[i] = new(cos * d.X + sin * d.Y, -sin * d.X + cos * d.Y);
            min = Vector2.Min(min, corners[i]);
            max = Vector2.Max(max, corners[i]);
        }
        for (int i = 0; i < corners.Length; i++)
            corners[i] -= min;
        return new Chart { Faces = faces, Corners = corners, Size = max - min };
    }

    // Shelf packing in order of decreasing height; returns whether everything fit, placing the charts as it goes.
    private static bool Pack(List<Chart> charts, float density, UvAtlasOptions options)
    {
        int width = options.Width, height = options.Height, padding = options.Padding;
        var order = Enumerable.Range(0, charts.Count)
            .OrderByDescending(i => MathF.Ceiling(charts[i].Size.Y * density)).ThenBy(i => i).ToArray();
        int x = padding, y = padding, shelf = 0;
        foreach (int i in order)
        {
            int w = (int)MathF.Ceiling(charts[i].Size.X * density) + 1, h = (int)MathF.Ceiling(charts[i].Size.Y * density) + 1;
            if (w + 2 * padding > width || h + 2 * padding > height)
                return false;
            if (x + w + padding > width)
            {
                x = padding;
                y += shelf + padding;
                shelf = 0;
            }
            if (y + h + padding > height)
                return false;
            charts[i].Offset = new(x, y);
            x += w + padding;
            shelf = Math.Max(shelf, h);
        }
        return true;
    }

    // The largest density, to one part in a thousand, at which the charts pack.
    private static float Fit(List<Chart> charts, UvAtlasOptions options)
    {
        double area = charts.Sum(c => (double)Math.Max(c.Size.X, 1e-9f) * Math.Max(c.Size.Y, 1e-9f));
        float high = (float)Math.Sqrt(options.Width * (double)options.Height / Math.Max(area, 1e-12)), low = 0;
        for (int step = 0; step < 40 && high - low > high * 1e-3f; step++)
        {
            float middle = (low + high) / 2;
            if (Pack(charts, middle, options))
                low = middle;
            else
                high = middle;
        }
        if (!Pack(charts, low, options))
            throw new ArgumentOutOfRangeException(nameof(options), "The charts do not fit the atlas with this padding.");
        return low;
    }

    // Whether any texel center of the chart, at the given density, lies inside two of its faces.
    private static bool Overlaps(Chart chart, float density)
    {
        int w = (int)MathF.Ceiling(chart.Size.X * density) + 1, h = (int)MathF.Ceiling(chart.Size.Y * density) + 1;
        if (chart.Faces.Count < 2 || (long)w * h > 64L * 1024 * 1024)
            return false;
        var covered = new bool[w * h];
        for (int i = 0; i < chart.Faces.Count; i++)
        {
            Vector2 a = chart.Corners[i * 3] * density, b = chart.Corners[i * 3 + 1] * density, c = chart.Corners[i * 3 + 2] * density;
            bool overlap = false;
            Raster.Triangle(a, b, c, w, h, (x, y, _) =>
            {
                ref bool cell = ref covered[y * w + x];
                overlap |= cell;
                cell = true;
            });
            if (overlap)
                return true;
        }
        return false;
    }

    private static UvAtlasMesh Emit(ReadOnlySpan<int> indices, List<Chart> charts, float density, UvAtlasOptions options, int vertexCount)
    {
        var sourceVertices = new List<int>();
        var uvs = new List<Vector2>();
        var result = new int[indices.Length];
        var local = new Dictionary<int, int>();
        foreach (var chart in charts)
        {
            local.Clear();
            for (int i = 0; i < chart.Faces.Count; i++)
                for (int k = 0; k < 3; k++)
                {
                    int corner = chart.Faces[i] * 3 + k, source = indices[corner];
                    if (!local.TryGetValue(source, out int vertex))
                    {
                        vertex = sourceVertices.Count;
                        local[source] = vertex;
                        sourceVertices.Add(source);
                        var texel = chart.Offset + chart.Corners[i * 3 + k] * density;
                        uvs.Add(new(texel.X / options.Width, texel.Y / options.Height));
                    }
                    result[corner] = vertex;
                }
        }
        return new UvAtlasMesh([.. sourceVertices], result, [.. uvs], density, charts.Count);
    }
}
