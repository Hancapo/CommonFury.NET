using System.Numerics;
using System.Runtime.InteropServices;
using CommonFury.Numerics;

namespace CommonFury.Geometry;

/// <summary>Polygon operations in the XY plane; Z is carried along as height.</summary>
public static class PolygonMath
{
    // Ear-clipping vertex states; a nonnegative state is a vertex inside the ear.
    private const int Untested = -1, NotConvex = -2, Ear = -3;

    /// <summary>Tests an XY point against a convex boundary of either winding, edges included.</summary>
    public static bool ContainsConvexXY(ReadOnlySpan<Vector3> vertices, Vector2 point, double tolerance = 1e-9)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (vertices.Length < 3 || !VectorMath.IsFinite(point))
            return false;
        bool positive = false, negative = false;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 a = vertices[i], b = vertices[(i + 1) % vertices.Length];
            if (!VectorMath.IsFinite(a))
                return false;
            double cross = ((double)b.X - a.X) * ((double)point.Y - a.Y) - ((double)b.Y - a.Y) * ((double)point.X - a.X);
            positive |= cross > tolerance;
            negative |= cross < -tolerance;
            if (positive && negative)
                return false;
        }
        return positive || negative;
    }

    /// <summary>Signed XY area; counterclockwise is positive.</summary>
    public static double SignedAreaXY(ReadOnlySpan<Vector3> vertices)
    {
        double sum = 0;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 a = vertices[i], b = vertices[(i + 1) % vertices.Length];
            sum += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        return sum / 2;
    }

    /// <summary>Clips to an XY rectangle, interpolating Z at new intersections. Returns an empty array when less than
    /// a nondegenerate polygon remains.</summary>
    public static Vector3[] ClipToRectangleXY(ReadOnlySpan<Vector3> vertices, Aabb2 bounds, float tolerance = 1e-6f)
    {
        if (!float.IsFinite(tolerance) || tolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        foreach (var vertex in vertices)
            if (!VectorMath.IsFinite(vertex))
                throw new ArgumentOutOfRangeException(nameof(vertices));
        var polygon = new List<Vector3>(vertices.Length);
        polygon.AddRange(vertices);
        var output = new List<Vector3>(vertices.Length);
        // Planes in order: minimum X, maximum X, minimum Y, maximum Y.
        for (int plane = 0; plane < 4 && polygon.Count > 0; plane++)
        {
            bool alongX = plane < 2, minimum = (plane & 1) == 0;
            float value = alongX ? (minimum ? bounds.Minimum.X : bounds.Maximum.X) : (minimum ? bounds.Minimum.Y : bounds.Maximum.Y);
            output.Clear();
            ReadOnlySpan<Vector3> source = CollectionsMarshal.AsSpan(polygon);
            Vector3 previous = source[^1];
            bool previousInside = IsInside(previous, alongX, minimum, value, tolerance);
            foreach (Vector3 current in source)
            {
                bool currentInside = IsInside(current, alongX, minimum, value, tolerance);
                if (currentInside != previousInside)
                {
                    float difference = Component(current, alongX) - Component(previous, alongX);
                    Vector3 intersection = MathF.Abs(difference) <= tolerance ? previous : Vector3.Lerp(previous, current, (value - Component(previous, alongX)) / difference);
                    if (alongX)
                        intersection.X = value;
                    else
                        intersection.Y = value;
                    output.Add(intersection);
                }
                if (currentInside)
                    output.Add(current);
                previous = current;
                previousInside = currentInside;
            }
            (polygon, output) = (output, polygon);
        }
        // Drop vertices within tolerance of the last kept one, compacting in place.
        Span<Vector3> clean = CollectionsMarshal.AsSpan(polygon);
        int count = 0;
        foreach (Vector3 vertex in clean)
            if (count == 0 || Vector3.Distance(vertex, clean[count - 1]) > tolerance)
                clean[count++] = vertex;
        if (count > 1 && Vector3.Distance(clean[0], clean[count - 1]) <= tolerance)
            count--;
        clean = clean[..count];
        return count < 3 || Math.Abs(SignedAreaXY(clean)) <= tolerance ? [] : clean.ToArray();
    }

    private static float Component(Vector3 value, bool alongX) => alongX ? value.X : value.Y;

    private static bool IsInside(Vector3 value, bool alongX, bool minimum, float plane, float tolerance) =>
        minimum ? Component(value, alongX) >= plane - tolerance : Component(value, alongX) <= plane + tolerance;

    /// <summary>Ear-clipping triangulation of a simple polygon of either winding. Throws for a degenerate or
    /// self-intersecting boundary.</summary>
    public static IReadOnlyList<Vector3[]> TriangulateXY(ReadOnlySpan<Vector3> vertices, float tolerance = 1e-6f)
    {
        if (!float.IsFinite(tolerance) || tolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (vertices.Length < 3)
            throw new ArgumentException("Polygon requires at least three vertices.", nameof(vertices));
        foreach (var vertex in vertices)
            if (!VectorMath.IsFinite(vertex))
                throw new ArgumentOutOfRangeException(nameof(vertices));
        double area = SignedAreaXY(vertices);
        if (Math.Abs(area) <= tolerance)
            throw new ArgumentException("Polygon has no projected area.", nameof(vertices));
        int orientation = Math.Sign(area);
        int count = vertices.Length;
        // Remaining vertices form a ring in input order; head is the first of them.
        var previous = new int[count];
        var next = new int[count];
        var state = new int[count];
        var removed = new bool[count];
        for (int i = 0; i < count; i++)
        {
            previous[i] = i == 0 ? count - 1 : i - 1;
            next[i] = i == count - 1 ? 0 : i + 1;
        }
        Array.Fill(state, Untested);
        int head = 0;
        var result = new List<Vector3[]>(count - 2);
        for (int remaining = count; remaining > 3; remaining--)
        {
            // The first ear in ring order is clipped. A vertex stays a non-ear while its neighbors are unchanged
            // and, when another vertex lies in its triangle, while that vertex remains, so only those are retested.
            int ear = head;
            for (int step = 0; ; step++, ear = next[ear])
            {
                if (step == remaining)
                    throw new ArgumentException("Polygon cannot be triangulated; provide a simple nondegenerate boundary.", nameof(vertices));
                if (state[ear] == Untested || (state[ear] >= 0 && removed[state[ear]]))
                    state[ear] = EarState(vertices, previous[ear], ear, next[ear], next, head, orientation, tolerance);
                if (state[ear] == Ear)
                    break;
            }
            int before = previous[ear], after = next[ear];
            result.Add([vertices[before], vertices[ear], vertices[after]]);
            next[before] = after;
            previous[after] = before;
            removed[ear] = true;
            state[before] = state[after] = Untested;
            if (ear == head)
                head = after;
        }
        result.Add([vertices[head], vertices[next[head]], vertices[next[next[head]]]]);
        return result.AsReadOnly();
    }

    // Ear, NotConvex, or the first remaining vertex inside the triangle.
    private static int EarState(ReadOnlySpan<Vector3> vertices, int previous, int current, int following, int[] next, int head,
        int orientation, float tolerance)
    {
        Vector3 a = vertices[previous], b = vertices[current], c = vertices[following];
        if (Vector3.Cross(b - a, c - b).Z * orientation <= tolerance)
            return NotConvex;
        var triangle = new BarycentricTriangle(a, b, c);
        if (triangle.IsDegenerate)
            return Ear;
        int candidate = head;
        do
        {
            if (candidate != previous && candidate != current && candidate != following)
            {
                Vector3 p = vertices[candidate];
                if (triangle.Contains(new(p.X, p.Y), tolerance, out _, out _, out _))
                    return candidate;
            }
            candidate = next[candidate];
        } while (candidate != head);
        return Ear;
    }
}
