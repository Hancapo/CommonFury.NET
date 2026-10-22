using System.Numerics;
using System.Runtime.InteropServices;

namespace CommonFury.Numerics;

/// <summary>Finite center and nonnegative finite radius; the default is a zero-radius sphere at the origin.</summary>
public readonly record struct Sphere3
{
    /// <summary>Computed without overflow for large finite bounds; zero when empty.</summary>
    public Vector3 Center { get; }
    /// <summary>Half the diagonal, computed in double precision; zero when empty.</summary>
    public float Radius { get; }
    /// <summary>Rejects a nonfinite center or a negative or nonfinite radius.</summary>
    public Sphere3(Vector3 center, float radius)
    {
        if (!VectorMath.IsFinite(center) || !float.IsFinite(radius) || radius < 0)
            throw new ArgumentException("Sphere center and radius must be finite, with a nonnegative radius.");
        (Center, Radius) = (center, radius);
    }
    /// <summary>The sphere through the box corners.</summary>
    public static Sphere3 FromBounds(Aabb3 bounds) => new(bounds.Center, bounds.Radius);
    /// <summary>The smallest sphere about <paramref name="center"/> that contains every point.</summary>
    public static Sphere3 FromPoints(ReadOnlySpan<Vector3> points, Vector3 center)
    {
        if (!VectorMath.IsFinite(center))
            throw new ArgumentOutOfRangeException(nameof(center));
        double squared = 0;
        foreach (Vector3 point in points)
        {
            if (!VectorMath.IsFinite(point))
                throw new ArgumentOutOfRangeException(nameof(points));
            double x = (double)point.X - center.X, y = (double)point.Y - center.Y, z = (double)point.Z - center.Z;
            squared = Math.Max(squared, x * x + y * y + z * z);
        }
        return new(center, (float)Math.Sqrt(squared));
    }
}

/// <summary>Finite, ordered rectangle bounds.</summary>
public readonly record struct Aabb2
{
    public Vector2 Minimum { get; }
    public Vector2 Maximum { get; }
    /// <summary>Computed without overflow for large finite bounds.</summary>
    public Vector2 Center => Minimum * 0.5f + Maximum * 0.5f;
    public Vector2 Size => Maximum - Minimum;
    /// <summary>Rejects nonfinite or reversed bounds.</summary>
    public Aabb2(Vector2 minimum, Vector2 maximum)
    {
        if (!VectorMath.IsFinite(minimum) || !VectorMath.IsFinite(maximum) || minimum.X > maximum.X || minimum.Y > maximum.Y)
            throw new ArgumentException("Bounds must be finite and ordered.");
        Minimum = minimum;
        Maximum = maximum;
    }
    public static Aabb2 FromCenterSize(Vector2 center, Vector2 size) => new(center - size * 0.5f, center + size * 0.5f);
    public Aabb2 Merge(Aabb2 other) => new(Vector2.Min(Minimum, other.Minimum), Vector2.Max(Maximum, other.Maximum));
    /// <summary>Grows each side by <paramref name="padding"/>; nonpositive padding changes nothing.</summary>
    public Aabb2 Expand(float padding)
    {
        if (!float.IsFinite(padding))
            throw new ArgumentOutOfRangeException(nameof(padding));
        return padding <= 0 ? this : new(Minimum - new Vector2(padding), Maximum + new Vector2(padding));
    }
}

/// <summary>Finite, ordered box bounds, or <see cref="Empty"/>. The default is a point at the origin.</summary>
public readonly record struct Aabb3
{
    /// <see cref="float.MaxValue"/> in every lane for <see cref="Empty"/>.
    public Vector3 Minimum { get; }
    /// <summary>-<see cref="float.MaxValue"/> in every lane for <see cref="Empty"/>.</summary>
    public Vector3 Maximum { get; }
    /// <summary>Merging with it returns the other box; transforming it keeps it empty.</summary>
    public static Aabb3 Empty { get; } = new(new(float.MaxValue), new(-float.MaxValue));
    public bool IsEmpty => Minimum.X > Maximum.X;
    /// <summary>Midpoint, computed without overflowing for large finite bounds; zero for an empty box.</summary>
    public Vector3 Center => Minimum * 0.5f + Maximum * 0.5f;
    /// <summary>Zero when empty.</summary>
    public Vector3 Size => IsEmpty ? Vector3.Zero : Maximum - Minimum;
    /// <summary>Half the diagonal length, computed in double precision; zero for an empty box.</summary>
    public float Radius
    {
        get
        {
            if (IsEmpty)
                return 0;
            // Widen before subtracting/squaring: a finite radius can have an unrepresentable float diameter or square.
            double x = ((double)Maximum.X - Minimum.X) * 0.5;
            double y = ((double)Maximum.Y - Minimum.Y) * 0.5;
            double z = ((double)Maximum.Z - Minimum.Z) * 0.5;
            return (float)Math.Sqrt(x * x + y * y + z * z);
        }
    }
    /// <summary>Rejects nonfinite or reversed bounds, except the <see cref="Empty"/> sentinel.</summary>
    public Aabb3(Vector3 minimum, Vector3 maximum)
    {
        bool empty = minimum == new Vector3(float.MaxValue) && maximum == new Vector3(-float.MaxValue);
        if (!empty && (!VectorMath.IsFinite(minimum) || !VectorMath.IsFinite(maximum) || minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z))
            throw new ArgumentException("Bounds must be finite and ordered.");
        Minimum = minimum;
        Maximum = maximum;
    }
    public static Aabb3 FromCenterSize(Vector3 center, Vector3 size) => new(center - size * 0.5f, center + size * 0.5f);
    /// <summary>Requires at least one point; all must be finite.</summary>
    public static Aabb3 FromPoints(ReadOnlySpan<Vector3> points)
    {
        if (points.IsEmpty)
            throw new ArgumentException("At least one point is required.", nameof(points));
        Vector3 minimum = points[0], maximum = minimum;
        int i = 0;
        // Setup and horizontal reduction cost more than the simple loop on tiny point sets.
        if (Vector.IsHardwareAccelerated && points.Length >= 64)
            (minimum, maximum, i) = ReducePointBatches(points);
        for (; i < points.Length; i++)
        {
            Vector3 point = points[i];
            if (!VectorMath.IsFinite(point))
                throw new ArgumentException("Points must be finite.", nameof(points));
            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
        }
        return new(minimum, maximum);
    }
    private static (Vector3 Minimum, Vector3 Maximum, int Count) ReducePointBatches(ReadOnlySpan<Vector3> points)
    {
        int width = Vector<float>.Count;
        int i = 0;
        Vector3 minimum = points[0], maximum = minimum;
        var min0 = new Vector<float>(float.MaxValue);
        var min1 = min0;
        var min2 = min0;
        var max0 = new Vector<float>(-float.MaxValue);
        var max1 = max0;
        var max2 = max0;
        // Three registers cover a whole batch of packed XYZ points without deinterleaving.
        for (; i <= points.Length - width; i += width)
        {
            ReadOnlySpan<float> values = MemoryMarshal.Cast<Vector3, float>(points.Slice(i, width));
            var a = new Vector<float>(values[..width]);
            var b = new Vector<float>(values.Slice(width, width));
            var c = new Vector<float>(values.Slice(width * 2, width));
            if (!SpanMath.IsFinite(a) || !SpanMath.IsFinite(b) || !SpanMath.IsFinite(c))
                throw new ArgumentException("Points must be finite.", nameof(points));
            min0 = Vector.Min(min0, a);
            min1 = Vector.Min(min1, b);
            min2 = Vector.Min(min2, c);
            max0 = Vector.Max(max0, a);
            max1 = Vector.Max(max1, b);
            max2 = Vector.Max(max2, c);
        }
        Span<float> extrema = stackalloc float[width * 6];
        min0.CopyTo(extrema);
        min1.CopyTo(extrema[width..]);
        min2.CopyTo(extrema[(width * 2)..]);
        max0.CopyTo(extrema[(width * 3)..]);
        max1.CopyTo(extrema[(width * 4)..]);
        max2.CopyTo(extrema[(width * 5)..]);
        Span<Vector3> reduced = MemoryMarshal.Cast<float, Vector3>(extrema);
        for (int lane = 0; lane < width; lane++)
        {
            minimum = Vector3.Min(minimum, reduced[lane]);
            maximum = Vector3.Max(maximum, reduced[lane + width]);
        }
        return (minimum, maximum, i);
    }

    public Aabb3 Merge(Aabb3 other) => new(Vector3.Min(Minimum, other.Minimum), Vector3.Max(Maximum, other.Maximum));
    /// <summary>Grows each side by <paramref name="padding"/>; empty boxes and nonpositive padding are unchanged.</summary>
    public Aabb3 Expand(float padding)
    {
        if (!float.IsFinite(padding))
            throw new ArgumentOutOfRangeException(nameof(padding));
        return IsEmpty || padding <= 0 ? this : new(Minimum - new Vector3(padding), Maximum + new Vector3(padding));
    }
    /// <summary>Bounds the eight transformed corners; an empty box stays empty.</summary>
    public Aabb3 Transform(Matrix4x4 transform)
    {
        if (IsEmpty)
            return this;
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < corners.Length; i++)
            corners[i] = new(
            (i & 1) == 0 ? Minimum.X : Maximum.X,
            (i & 2) == 0 ? Minimum.Y : Maximum.Y,
            (i & 4) == 0 ? Minimum.Z : Maximum.Z);
        TransformMath.Positions(corners, transform, corners);
        return FromPoints(corners);
    }
}
