using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Geometry;

/// <summary>Polygon operations in the XY plane; Z is carried along as height.</summary>
public static class PolygonMath
{
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
        var polygon = vertices.ToArray().ToList();
        foreach (var plane in new[] { (0, bounds.Minimum.X, true), (0, bounds.Maximum.X, false), (1, bounds.Minimum.Y, true), (1, bounds.Maximum.Y, false) })
        {
            if (polygon.Count == 0)
                break;
            var output = new List<Vector3>();
            Vector3 previous = polygon[^1];
            float Component(Vector3 v) => plane.Item1 == 0 ? v.X : v.Y;
            bool Inside(Vector3 v) => plane.Item3 ? Component(v) >= plane.Item2 - tolerance : Component(v) <= plane.Item2 + tolerance;
            bool previousInside = Inside(previous);
            foreach (var current in polygon)
            {
                bool currentInside = Inside(current);
                if (currentInside != previousInside)
                {
                    float difference = Component(current) - Component(previous);
                    Vector3 intersection = MathF.Abs(difference) <= tolerance ? previous : Vector3.Lerp(previous, current, (plane.Item2 - Component(previous)) / difference);
                    if (plane.Item1 == 0)
                        intersection.X = plane.Item2;
                    else
                        intersection.Y = plane.Item2;
                    output.Add(intersection);
                }
                if (currentInside)
                    output.Add(current);
                previous = current;
                previousInside = currentInside;
            }
            polygon = output;
        }
        var clean = new List<Vector3>();
        foreach (var vertex in polygon)
            if (clean.Count == 0 || Vector3.Distance(vertex, clean[^1]) > tolerance)
                clean.Add(vertex);
        if (clean.Count > 1 && Vector3.Distance(clean[0], clean[^1]) <= tolerance)
            clean.RemoveAt(clean.Count - 1);
        return clean.Count < 3 || Math.Abs(SignedAreaXY(clean.ToArray())) <= tolerance ? [] : clean.ToArray();
    }

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
        var remaining = Enumerable.Range(0, vertices.Length).ToList();
        var result = new List<Vector3[]>();
        while (remaining.Count > 3)
        {
            bool found = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int previous = remaining[(i + remaining.Count - 1) % remaining.Count], current = remaining[i], next = remaining[(i + 1) % remaining.Count];
                Vector3 a = vertices[previous], b = vertices[current], c = vertices[next];
                if (Vector3.Cross(b - a, c - b).Z * orientation <= tolerance)
                    continue;
                bool occupied = false;
                foreach (int candidate in remaining)
                {
                    if (candidate == previous || candidate == current || candidate == next)
                        continue;
                    Vector3 p = vertices[candidate];
                    if (HeightFieldMath.TryTriangleHeight(new(p.X, p.Y), a, b, c, out _, tolerance))
                    {
                        occupied = true;
                        break;
                    }
                }
                if (occupied)
                    continue;
                result.Add([a, b, c]);
                remaining.RemoveAt(i);
                found = true;
                break;
            }
            if (!found)
                throw new ArgumentException("Polygon cannot be triangulated; provide a simple nondegenerate boundary.", nameof(vertices));
        }
        result.Add([vertices[remaining[0]], vertices[remaining[1]], vertices[remaining[2]]]);
        return result.AsReadOnly();
    }
}
