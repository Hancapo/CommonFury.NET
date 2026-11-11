using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Geometry;

/// <summary>A point on a source triangle found by a <see cref="TriangleBvh"/> query.</summary>
/// <param name="Triangle">Triangle index: the position of its first index divided by three.</param>
/// <param name="Distance">Distance along the ray, or from the query point.</param>
/// <param name="Barycentric">Weights of the triangle's first, second and third corners; they sum to one.</param>
public readonly record struct TriangleHit(int Triangle, float Distance, Vector3 Barycentric);

/// <summary>A bounding-volume hierarchy over a triangle list for nearest ray hits and nearest surface points.</summary>
/// <remarks>The constructor copies the triangles. Queries do not modify the hierarchy, so one instance may be queried from many
/// threads. Both faces of a triangle are hit; triangles without area are never hit by rays. Ties go to the lower triangle index.</remarks>
public sealed class TriangleBvh
{
    private const int LeafSize = 4, Bins = 16;
    private readonly Node[] nodes;
    private readonly Vector3[] corner, edge1, edge2;
    private readonly int[] triangleOf;
    private readonly int depth;

    /// <summary>Builds the hierarchy with binned surface-area-heuristic splits.</summary>
    /// <exception cref="ArgumentException">An index is out of range or a referenced position is not finite.</exception>
    public TriangleBvh(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices)
    {
        MeshMath.ValidateTriangleIndices(indices, positions.Length);
        foreach (int index in indices)
            if (!VectorMath.IsFinite(positions[index]))
                throw new ArgumentException("Triangle positions must be finite.", nameof(positions));
        TriangleCount = indices.Length / 3;
        var bounds = new Box[TriangleCount];
        var centers = new Vector3[TriangleCount];
        var order = new int[TriangleCount];
        for (int t = 0; t < TriangleCount; t++)
        {
            Vector3 a = positions[indices[t * 3]], b = positions[indices[t * 3 + 1]], c = positions[indices[t * 3 + 2]];
            bounds[t] = new(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));
            centers[t] = (bounds[t].Min + bounds[t].Max) / 2;
            order[t] = t;
        }
        (nodes, depth) = TriangleCount == 0 ? ([], 0) : Build(order, bounds, centers);
        corner = new Vector3[TriangleCount];
        edge1 = new Vector3[TriangleCount];
        edge2 = new Vector3[TriangleCount];
        triangleOf = order;
        for (int i = 0; i < TriangleCount; i++)
        {
            int t = order[i];
            Vector3 a = positions[indices[t * 3]];
            corner[i] = a;
            edge1[i] = positions[indices[t * 3 + 1]] - a;
            edge2[i] = positions[indices[t * 3 + 2]] - a;
        }
    }

    public int TriangleCount { get; }

    /// <summary>Finds the nearest triangle a ray meets within <paramref name="maximumDistance"/> of its origin. Distances are in
    /// multiples of the direction's length, so a unit direction gives world distances.</summary>
    /// <exception cref="ArgumentException">The origin or direction is not finite, or the direction is zero.</exception>
    public bool Raycast(Vector3 origin, Vector3 direction, float maximumDistance, out TriangleHit hit)
    {
        if (!VectorMath.IsFinite(origin) || !VectorMath.IsFinite(direction) || direction == Vector3.Zero)
            throw new ArgumentException("Ray origin and direction must be finite and the direction nonzero.");
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDistance);
        hit = default;
        if (nodes.Length == 0)
            return false;
        var inverse = Vector3.One / direction;
        float best = maximumDistance;
        int bestTriangle = -1;
        Vector3 bestWeights = default;
        Span<int> stack = depth < 128 ? stackalloc int[depth + 2] : new int[depth + 2];
        int top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            ref readonly Node node = ref nodes[stack[--top]];
            if (!Slab(node, origin, inverse, best, out _))
                continue;
            if (node.Count == 0)
            {
                // The nearer child is popped first; the farther one is skipped later if a closer hit was found.
                int first = node.Start, second = node.Start + 1;
                bool hitFirst = Slab(nodes[first], origin, inverse, best, out float firstEntry);
                bool hitSecond = Slab(nodes[second], origin, inverse, best, out float secondEntry);
                if (hitFirst && hitSecond)
                {
                    if (secondEntry < firstEntry)
                        (first, second) = (second, first);
                    stack[top++] = second;
                    stack[top++] = first;
                }
                else if (hitFirst)
                    stack[top++] = first;
                else if (hitSecond)
                    stack[top++] = second;
                continue;
            }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                // Möller–Trumbore, accepting both faces.
                Vector3 p = Vector3.Cross(direction, edge2[i]);
                float determinant = Vector3.Dot(edge1[i], p);
                if (determinant == 0)
                    continue;
                float inverseDeterminant = 1 / determinant;
                Vector3 s = origin - corner[i];
                float u = Vector3.Dot(s, p) * inverseDeterminant;
                if (u < 0 || u > 1)
                    continue;
                Vector3 q = Vector3.Cross(s, edge1[i]);
                float v = Vector3.Dot(direction, q) * inverseDeterminant;
                if (v < 0 || u + v > 1)
                    continue;
                float distance = Vector3.Dot(edge2[i], q) * inverseDeterminant;
                if (distance < 0 || distance > best || distance == best && bestTriangle >= 0 && triangleOf[i] > bestTriangle)
                    continue;
                best = distance;
                bestTriangle = triangleOf[i];
                bestWeights = new(1 - u - v, u, v);
            }
        }
        if (bestTriangle < 0)
            return false;
        hit = new(bestTriangle, best, bestWeights);
        return true;
    }

    /// <summary>Finds the nearest point on any triangle within <paramref name="maximumDistance"/> of <paramref name="point"/>.</summary>
    /// <exception cref="ArgumentException">The point is not finite.</exception>
    public bool FindNearest(Vector3 point, float maximumDistance, out TriangleHit hit)
    {
        if (!VectorMath.IsFinite(point))
            throw new ArgumentException("Query point must be finite.", nameof(point));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDistance);
        hit = default;
        if (nodes.Length == 0)
            return false;
        float best = maximumDistance * maximumDistance;
        int bestTriangle = -1;
        Vector3 bestWeights = default;
        Span<int> stack = depth < 128 ? stackalloc int[depth + 2] : new int[depth + 2];
        int top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            ref readonly Node node = ref nodes[stack[--top]];
            if (DistanceSquared(node, point) > best)
                continue;
            if (node.Count == 0)
            {
                int first = node.Start, second = node.Start + 1;
                if (DistanceSquared(nodes[second], point) < DistanceSquared(nodes[first], point))
                    (first, second) = (second, first);
                stack[top++] = second;
                stack[top++] = first;
                continue;
            }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                Vector3 weights = ClosestPoint(point - corner[i], edge1[i], edge2[i]);
                float distance = Vector3.DistanceSquared(corner[i] + edge1[i] * weights.Y + edge2[i] * weights.Z, point);
                if (distance > best || distance == best && bestTriangle >= 0 && triangleOf[i] > bestTriangle)
                    continue;
                best = distance;
                bestTriangle = triangleOf[i];
                bestWeights = weights;
            }
        }
        if (bestTriangle < 0)
            return false;
        hit = new(bestTriangle, MathF.Sqrt(best), bestWeights);
        return true;
    }

    private readonly record struct Box(Vector3 Min, Vector3 Max)
    {
        public static Box Empty => new(new Vector3(float.MaxValue), new Vector3(float.MinValue));

        public Box Union(Box other) => new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

        public float HalfArea
        {
            get
            {
                Vector3 size = Vector3.Max(Max - Min, Vector3.Zero);
                return size.X * size.Y + size.Y * size.Z + size.Z * size.X;
            }
        }
    }

    // A branch (Count == 0) has children at Start and Start + 1; a leaf covers triangles Start..Start + Count in leaf order.
    private readonly record struct Node(Vector3 Min, Vector3 Max, int Start, int Count);

    private static (Node[] Nodes, int Depth) Build(int[] order, Box[] bounds, Vector3[] centers)
    {
        var nodes = new List<Node>(Math.Max(1, 2 * order.Length / LeafSize)) { default };
        var scratch = new int[order.Length];
        var work = new Stack<(int Slot, int Start, int End, int Depth)>();
        work.Push((0, 0, order.Length, 1));
        int maximumDepth = 1;
        Span<int> binCount = stackalloc int[Bins];
        Span<Box> binBox = stackalloc Box[Bins];
        Span<float> leftCost = stackalloc float[Bins];
        while (work.Count > 0)
        {
            var (slot, start, end, level) = work.Pop();
            maximumDepth = Math.Max(maximumDepth, level);
            Box box = Box.Empty, centerBox = Box.Empty;
            for (int i = start; i < end; i++)
            {
                box = box.Union(bounds[order[i]]);
                centerBox = centerBox.Union(new(centers[order[i]], centers[order[i]]));
            }
            int count = end - start;
            if (count <= LeafSize)
            {
                nodes[slot] = new(box.Min, box.Max, start, count);
                continue;
            }

            Vector3 extent = centerBox.Max - centerBox.Min;
            int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            float low = centerBox.Min[axis], width = extent[axis];
            int mid = -1;
            if (width > 0)
            {
                binCount.Clear();
                binBox.Fill(Box.Empty);
                for (int i = start; i < end; i++)
                {
                    int b = Bin(centers[order[i]][axis], low, width);
                    binCount[b]++;
                    binBox[b] = binBox[b].Union(bounds[order[i]]);
                }
                Box accumulated = Box.Empty;
                int accumulatedCount = 0;
                for (int b = 0; b < Bins - 1; b++)
                {
                    accumulated = accumulated.Union(binBox[b]);
                    accumulatedCount += binCount[b];
                    leftCost[b] = accumulated.HalfArea * accumulatedCount;
                }
                accumulated = Box.Empty;
                accumulatedCount = 0;
                float bestCost = float.MaxValue;
                int bestSplit = -1;
                for (int b = Bins - 1; b > 0; b--)
                {
                    accumulated = accumulated.Union(binBox[b]);
                    accumulatedCount += binCount[b];
                    float cost = leftCost[b - 1] + accumulated.HalfArea * accumulatedCount;
                    if (accumulatedCount < count && cost < bestCost)
                        (bestCost, bestSplit) = (cost, b);
                }
                if (bestSplit > 0)
                {
                    // A stable partition keeps the build deterministic.
                    int left = start, right = 0;
                    for (int i = start; i < end; i++)
                    {
                        if (Bin(centers[order[i]][axis], low, width) < bestSplit)
                            order[left++] = order[i];
                        else
                            scratch[right++] = order[i];
                    }
                    scratch.AsSpan(0, right).CopyTo(order.AsSpan(left));
                    mid = left;
                }
            }
            if (mid <= start || mid >= end)
            {
                // All centers coincide or fall in one bin: split at the median of the axis, ordered by center and index.
                order.AsSpan(start, count).Sort((x, y) => centers[x][axis].CompareTo(centers[y][axis]) is int c && c != 0 ? c : x.CompareTo(y));
                mid = start + count / 2;
            }
            int first = nodes.Count;
            nodes.Add(default);
            nodes.Add(default);
            nodes[slot] = new(box.Min, box.Max, first, 0);
            work.Push((first + 1, mid, end, level + 1));
            work.Push((first, start, mid, level + 1));
        }
        return ([.. nodes], maximumDepth);
    }

    private static int Bin(float value, float low, float width) => Math.Min(Bins - 1, (int)((value - low) / width * Bins));

    // Entry distance of a ray into a node's box, if it enters before `limit`. A zero direction component on a slab boundary yields
    // NaN, which compares false and keeps the box.
    private static bool Slab(in Node node, Vector3 origin, Vector3 inverse, float limit, out float entry)
    {
        Vector3 t0 = (node.Min - origin) * inverse, t1 = (node.Max - origin) * inverse;
        Vector3 near = Vector3.Min(t0, t1), far = Vector3.Max(t0, t1);
        entry = MathF.Max(0, MathF.Max(near.X, MathF.Max(near.Y, near.Z)));
        float exit = MathF.Min(limit, MathF.Min(far.X, MathF.Min(far.Y, far.Z)));
        return !(entry > exit);
    }

    private static float DistanceSquared(in Node node, Vector3 point) => Vector3.DistanceSquared(point, Vector3.Clamp(point, node.Min, node.Max));

    // Barycentric weights of the point nearest p on the triangle with edges ab and ac from its first corner, p relative to that corner.
    private static Vector3 ClosestPoint(Vector3 p, Vector3 ab, Vector3 ac)
    {
        float d1 = Vector3.Dot(ab, p), d2 = Vector3.Dot(ac, p);
        if (d1 <= 0 && d2 <= 0)
            return new(1, 0, 0);
        Vector3 bp = p - ab;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
            return new(0, 1, 0);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            float t = d1 / (d1 - d3);
            return new(1 - t, t, 0);
        }
        Vector3 cp = p - ac;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
            return new(0, 0, 1);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            float t = d2 / (d2 - d6);
            return new(1 - t, 0, t);
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            float t = (d4 - d3) / (d4 - d3 + (d5 - d6));
            return new(0, 1 - t, t);
        }
        float denominator = 1 / (va + vb + vc);
        float v = vb * denominator, w = vc * denominator;
        return new(1 - v - w, v, w);
    }
}
