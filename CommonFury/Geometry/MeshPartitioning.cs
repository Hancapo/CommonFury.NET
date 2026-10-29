using System.Numerics;

namespace CommonFury.Geometry;

/// <summary>A mesh partition: <see cref="VertexIndices"/> maps each local vertex to its source vertex, and
/// <see cref="Indices"/> are triangle indices local to the partition.</summary>
public sealed record MeshPartition(int[] VertexIndices, int[] Indices);

/// <summary>Converts strips and fans to triangle lists and splits meshes by vertex count.</summary>
public static class MeshPartitioning
{
    /// <summary>Expands a strip into triangles, alternating winding so every triangle keeps the strip's orientation.</summary>
    public static int[] TriangulateStrip(ReadOnlySpan<int> indices)
    {
        if (indices.Length < 3)
            throw new ArgumentException("Triangle strip needs at least three indices.");
        var triangles = new int[checked((indices.Length - 2) * 3)];
        for (int i = 0; i < indices.Length - 2; i++)
        {
            triangles[i * 3] = indices[i + (i & 1)];
            triangles[i * 3 + 1] = indices[i + 1 - (i & 1)];
            triangles[i * 3 + 2] = indices[i + 2];
        }
        return triangles;
    }

    /// <summary>Expands a fan into triangles sharing the first index, one per index after the second.</summary>
    public static int[] TriangulateFan(ReadOnlySpan<int> indices)
    {
        if (indices.Length < 3)
            throw new ArgumentException("Triangle fan needs at least three indices.");
        var triangles = new int[checked((indices.Length - 2) * 3)];
        for (int i = 1; i < indices.Length - 1; i++)
        {
            triangles[(i - 1) * 3] = indices[0];
            triangles[(i - 1) * 3 + 1] = indices[i];
            triangles[(i - 1) * 3 + 2] = indices[i + 1];
        }
        return triangles;
    }

    /// <summary>Fan triangles of a polygon boundary that may repeat indices: consecutive duplicates and a closing
    /// repeat of the first index produce no degenerate triangles. Fewer than three indices yield none. Convexity is
    /// not checked; use <see cref="PolygonMath.TriangulateXY"/> for concave outlines.</summary>
    public static int[] TriangulateFanSkippingRepeats(ReadOnlySpan<int> indices) => FanSkippingRepeats(indices);

    /// <inheritdoc cref="TriangulateFanSkippingRepeats(ReadOnlySpan{int})"/>
    public static int[] TriangulateFanSkippingRepeats(ReadOnlySpan<ushort> indices) => FanSkippingRepeats(indices);

    private static int[] FanSkippingRepeats<T>(ReadOnlySpan<T> indices) where T : IBinaryInteger<T>
    {
        if (indices.Length < 3)
            return [];
        var result = new List<int>();
        int start = int.CreateChecked(indices[0]), previous = start, pending = -1;
        // Each edge waits for the next distinct index, so a closing repeat of the start adds no degenerate triangle.
        for (int i = 1; i < indices.Length; i++)
        {
            int current = int.CreateChecked(indices[i]);
            if (current == previous)
                continue;
            if (pending >= 0)
                AddTriangle(result, start, pending, previous);
            pending = previous == start && pending < 0 ? -1 : previous;
            previous = current;
        }
        if (pending >= 0 && previous != start)
            AddTriangle(result, start, pending, previous);
        return result.ToArray();
    }

    private static void AddTriangle(List<int> triangles, int a, int b, int c)
    {
        triangles.Add(a);
        triangles.Add(b);
        triangles.Add(c);
    }

    /// <summary>Splits whole triangles into partitions of at most <paramref name="maximumVertices"/> vertices, in input order.
    /// A mesh that already fits is returned as one partition covering every vertex.</summary>
    public static IReadOnlyList<MeshPartition> Split(ReadOnlySpan<int> indices, int vertexCount, int maximumVertices)
    {
        MeshMath.ValidateTriangleIndices(indices, vertexCount);
        if (maximumVertices < 3)
            throw new ArgumentOutOfRangeException(nameof(maximumVertices));
        if (vertexCount <= maximumVertices)
            return [new(Enumerable.Range(0, vertexCount).ToArray(), indices.ToArray())];
        var results = new List<MeshPartition>();
        // Local index of each source vertex in the current partition, or -1.
        var remap = new int[vertexCount];
        Array.Fill(remap, -1);
        var vertices = new List<int>();
        var triangles = new List<int>();
        void Finish()
        {
            if (triangles.Count == 0)
                return;
            results.Add(new(vertices.ToArray(), triangles.ToArray()));
            foreach (int id in vertices)
                remap[id] = -1;
            vertices.Clear();
            triangles.Clear();
        }
        for (int i = 0; i < indices.Length; i += 3)
        {
            int missing = 0;
            for (int lane = 0; lane < 3; lane++)
                if (remap[indices[i + lane]] < 0 && (lane == 0 || indices[i + lane] != indices[i]) && (lane < 2 || indices[i + lane] != indices[i + 1]))
                    missing++;
            if (vertices.Count + missing > maximumVertices)
                Finish();
            for (int lane = 0; lane < 3; lane++)
            {
                int id = indices[i + lane];
                int local = remap[id];
                if (local < 0)
                {
                    local = vertices.Count;
                    remap[id] = local;
                    vertices.Add(id);
                }
                triangles.Add(local);
            }
        }
        Finish();
        return results.AsReadOnly();
    }
}
