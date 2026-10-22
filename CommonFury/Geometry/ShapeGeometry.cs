using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Geometry;

/// <summary>Areas and axis-aligned bounds of triangles, segments, cylinders, boxes and cones.</summary>
public static class ShapeGeometry
{
    public static float TriangleArea(Vector3 a, Vector3 b, Vector3 c) => Vector3.Cross(b - a, c - a).Length() * 0.5f;

    /// <summary>Bounds of a segment swept by a sphere of <paramref name="radius"/>.</summary>
    public static Aabb3 SegmentBounds(Vector3 start, Vector3 end, float radius) => new Aabb3(Vector3.Min(start, end), Vector3.Max(start, end)).Expand(radius);

    /// <summary>Exact bounds of a cylinder whose axis runs between the endpoints; a zero-length axis is treated as Y.</summary>
    public static Aabb3 CylinderBounds(Vector3 start, Vector3 end, float radius)
    {
        Vector3 direction = VectorMath.NormalizeOr(end - start, Vector3.UnitY);
        Vector3 square = Vector3.Max(Vector3.Zero, Vector3.One - direction * direction);
        Vector3 radial = new Vector3(MathF.Sqrt(square.X), MathF.Sqrt(square.Y), MathF.Sqrt(square.Z)) * radius;
        return new(Vector3.Min(start, end) - radial, Vector3.Max(start, end) + radial);
    }

    /// <summary>Bounds of an oriented box given four corners lying on opposite diagonals.</summary>
    public static Aabb3 OppositeDiagonalBoxBounds(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Vector3 center = (a + b + c + d) * 0.25f;
        Vector3 half = (Vector3.Abs(b + d - a - c) + Vector3.Abs(a + d - b - c) + Vector3.Abs(c + d - a - b)) * 0.25f;
        return new(center - half, center + half);
    }

    /// <summary>Bounds of a spherical cone, apex included. <paramref name="direction"/> must be a unit vector; it is not normalized.</summary>
    public static Aabb3 ConeBounds(Vector3 apex, Vector3 direction, float radius, float halfAngleRadians)
    {
        if (!VectorMath.IsFinite(apex) || !VectorMath.IsFinite(direction) || !float.IsFinite(radius) || radius < 0 ||
            !float.IsFinite(halfAngleRadians) || halfAngleRadians < 0)
            throw new ArgumentException("Invalid cone geometry.");
        Vector3 minimum = default, maximum = default;
        for (int axis = 0; axis < 3; axis++)
        {
            double angle = Math.Acos(Math.Clamp(direction[axis], -1, 1));
            minimum[axis] = apex[axis] + (float)(Math.Cos(Math.Clamp(angle + halfAngleRadians, Math.PI * 0.5, Math.PI)) * radius);
            maximum[axis] = apex[axis] + (float)(Math.Cos(Math.Clamp(angle - halfAngleRadians, 0, Math.PI * 0.5)) * radius);
        }
        return new(minimum, maximum);
    }
}
