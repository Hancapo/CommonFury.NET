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

        // Split every chart that overlaps itself, testing texel centers first at the area bound, which no packing exceeds, and then
        // at the largest density whose packing fits, until none does.
        while (Split(positions, indices, ref flat, Bound(flat, options), normals, areas, neighbours))
        {
        }
        float density = 0;
        do
            density = Fit(flat, options, density);
        while (Split(positions, indices, ref flat, density, normals, areas, neighbours));
        return Emit(indices, flat, density, options, positions.Length);
    }

    // Halves every chart that overlaps itself at a density into connected pieces. Returns whether any did.
    private static bool Split(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, ref List<Chart> charts, float density,
        Vector3[] normals, float[] areas, List<int>[] neighbours)
    {
        var next = new List<Chart>(charts.Count);
        foreach (var chart in charts)
        {
            if (!Overlaps(chart, density))
                next.Add(chart);
            else
                foreach (var half in Halve(chart, neighbours))
                    next.Add(Project(positions, indices, half, normals, areas));
        }
        bool split = next.Count != charts.Count;
        charts = next;
        return split;
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
        // Projected triangle area, in squared source units.
        public required double Area;
        // Placement in texels and a quarter turn, set by packing.
        public Vector2 Offset;
        public bool Rotated;
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
        double area = 0;
        for (int i = 0; i < corners.Length; i++)
            corners[i] -= min;
        for (int i = 0; i < corners.Length; i += 3)
        {
            Vector2 e1 = corners[i + 1] - corners[i], e2 = corners[i + 2] - corners[i];
            area += Math.Abs(e1.X * e2.Y - e1.Y * e2.X) / 2;
        }
        return new Chart { Faces = faces, Corners = corners, Size = max - min, Area = area };
    }

    // Skyline packing of chart shapes, largest first: each chart, at 0° or 90°, drops to the lowest place where its padded bottom
    // outline clears the tops of the charts below. Returns whether everything fit, placing the charts as it goes.
    private static bool Pack(List<Chart> charts, float density, UvAtlasOptions options)
    {
        int width = options.Width, height = options.Height, padding = options.Padding;
        var horizon = new int[width];
        var order = Enumerable.Range(0, charts.Count)
            .OrderByDescending(i => MathF.Max(charts[i].Size.X, charts[i].Size.Y)).ThenBy(i => i).ToArray();
        foreach (int i in order)
        {
            var chart = charts[i];
            int bestX = -1, bestY = int.MaxValue;
            bool bestRotated = false;
            Outline? bestOutline = null;
            var (plain, turned) = Outlines(chart, density, padding);
            foreach (var outline in (ReadOnlySpan<Outline>)[plain, turned])
            {
                if (outline.Width > width || outline.Height > height)
                    continue;
                for (int x = 0; x + outline.Width <= width; x++)
                {
                    // Stops as soon as this place cannot beat the best so far.
                    int y = 0, limit = Math.Min(bestY, height - outline.Height + 1);
                    for (int c = 0; c < outline.Width && y < limit; c++)
                        if (outline.Bottom[c] != int.MaxValue)
                            y = Math.Max(y, horizon[x + c] - outline.Bottom[c]);
                    if (y < limit)
                        (bestX, bestY, bestRotated, bestOutline) = (x, y, outline == turned, outline);
                }
            }
            if (bestOutline is null)
                return false;
            for (int c = 0; c < bestOutline.Width; c++)
                if (bestOutline.Top[c] >= 0)
                    horizon[bestX + c] = Math.Max(horizon[bestX + c], bestY + bestOutline.Top[c] + 1);
            chart.Rotated = bestRotated;
            chart.Offset = new(bestX + padding, bestY + padding);
        }
        return true;
    }

    // A chart footprint's profile in a box with `padding` texels on every side: per column, the lowest row of the footprint grown by
    // the padding (int.MaxValue where empty) and the highest row of the footprint itself (-1 where empty).
    private sealed class Outline
    {
        public required int Width, Height;
        public required int[] Bottom, Top;
    }

    // The texels a chart touches at a density, boundaries included, as outlines at 0° and at 90°. Turning maps the closed texel
    // (x, y) onto (shape height - 2 - y, x), the quarter turn Local applies, so the turned shape also needs row -1, which floors
    // never reach unturned.
    private static (Outline Plain, Outline Turned) Outlines(Chart chart, float density, int padding)
    {
        var (shapeWidth, shapeHeight) = Box(chart, density);
        // Rows -1 to shapeHeight - 1, stored one up.
        var touched = new bool[shapeWidth * (shapeHeight + 1)];
        for (int f = 0; f < chart.Faces.Count; f++)
        {
            Vector2 a = chart.Corners[f * 3] * density, b = chart.Corners[f * 3 + 1] * density, c = chart.Corners[f * 3 + 2] * density;
            Vector2 min = Vector2.Min(a, Vector2.Min(b, c)), max = Vector2.Max(a, Vector2.Max(b, c));
            for (int y = (int)MathF.Floor(min.Y) - 1; y <= (int)MathF.Floor(max.Y); y++)
                for (int x = (int)MathF.Floor(min.X); x <= (int)MathF.Floor(max.X); x++)
                    if (Touches(a, b, c, x, y))
                        touched[(Math.Clamp(y, -1, shapeHeight - 1) + 1) * shapeWidth + Math.Clamp(x, 0, shapeWidth - 1)] = true;
        }
        return (Profile(shapeWidth, shapeHeight, padding, (x, y) => touched[(y + 1) * shapeWidth + x]),
            Profile(shapeHeight, shapeWidth, padding, (x, y) => touched[(shapeHeight - 1 - x) * shapeWidth + y]));
    }

    private static Outline Profile(int shapeWidth, int shapeHeight, int padding, Func<int, int, bool> touched)
    {
        int width = shapeWidth + 2 * padding, height = shapeHeight + 2 * padding;
        var top = new int[width];
        var bottom = new int[width];
        Array.Fill(top, -1);
        Array.Fill(bottom, int.MaxValue);
        for (int x = 0; x < shapeWidth; x++)
        {
            int low = -1, high = -1;
            for (int y = 0; y < shapeHeight; y++)
                if (touched(x, y))
                {
                    if (low < 0)
                        low = y;
                    high = y;
                }
            if (low < 0)
                continue;
            top[x + padding] = high + padding;
            // Growing the footprint by the padding lowers the bottoms of nearby columns to this column's lowest row.
            for (int d = x; d <= x + 2 * padding; d++)
                bottom[d] = Math.Min(bottom[d], low);
        }
        return new Outline { Width = width, Height = height, Bottom = bottom, Top = top };
    }

    // Texel columns and rows of a chart's unturned box at a density.
    private static (int Width, int Height) Box(Chart chart, float density) =>
        ((int)MathF.Ceiling(chart.Size.X * density) + 1, (int)MathF.Ceiling(chart.Size.Y * density) + 1);

    // Whether triangle abc meets the closed texel square [x, x + 1] × [y, y + 1]: no separating axis among the square's axes and the
    // triangle's edge normals.
    private static bool Touches(Vector2 a, Vector2 b, Vector2 c, int x, int y)
    {
        Vector2 low = new(x, y), high = new(x + 1, y + 1);
        if (MathF.Max(a.X, MathF.Max(b.X, c.X)) < low.X || MathF.Min(a.X, MathF.Min(b.X, c.X)) > high.X
            || MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) < low.Y || MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) > high.Y)
            return false;
        foreach (var (from, to, other) in (ReadOnlySpan<(Vector2, Vector2, Vector2)>)[(a, b, c), (b, c, a), (c, a, b)])
        {
            var normal = new Vector2(to.Y - from.Y, from.X - to.X);
            float side = Vector2.Dot(normal, other - from);
            if (side == 0)
                continue;
            // The square lies wholly on the far side of this edge when even its nearest corner does.
            float nearest = MathF.Min(MathF.Min(Vector2.Dot(normal, low - from), Vector2.Dot(normal, new Vector2(high.X, low.Y) - from)),
                MathF.Min(Vector2.Dot(normal, new Vector2(low.X, high.Y) - from), Vector2.Dot(normal, high - from)));
            float farthest = MathF.Max(MathF.Max(Vector2.Dot(normal, low - from), Vector2.Dot(normal, new Vector2(high.X, low.Y) - from)),
                MathF.Max(Vector2.Dot(normal, new Vector2(low.X, high.Y) - from), Vector2.Dot(normal, high - from)));
            if (side > 0 ? farthest < 0 : nearest > 0)
                return false;
        }
        return true;
    }

    // A chart coordinate in texels within its box, turned a quarter so that the closed texel (x, y) lands on whole texel
    // (box height - 2 - y, x).
    private static Vector2 Local(Chart chart, Vector2 corner, float density, bool rotated)
    {
        var scaled = corner * density;
        return rotated ? new(Box(chart, density).Height - 1 - scaled.Y, scaled.X) : scaled;
    }

    // The largest density, to half a percent, at which the charts pack. The triangles' own area bounds it from above; a hint from an
    // earlier fit narrows the search.
    private static float Fit(List<Chart> charts, UvAtlasOptions options, float hint)
    {
        float high = Bound(charts, options), low = 0;
        if (hint > 0)
        {
            high = Math.Min(high, hint * 1.02f);
            if (Pack(charts, hint * 0.85f, options))
                low = hint * 0.85f;
        }
        for (int step = 0; step < 40 && high - low > high * 5e-3f; step++)
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

    // The density at which the charts' triangles alone would fill the atlas.
    private static float Bound(List<Chart> charts, UvAtlasOptions options) =>
        (float)Math.Sqrt(options.Width * (double)options.Height / Math.Max(charts.Sum(c => c.Area), 1e-12));

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
                        var texel = chart.Offset + Local(chart, chart.Corners[i * 3 + k], density, chart.Rotated);
                        uvs.Add(new(texel.X / options.Width, texel.Y / options.Height));
                    }
                    result[corner] = vertex;
                }
        }
        return new UvAtlasMesh([.. sourceVertices], result, [.. uvs], density, charts.Count);
    }
}
