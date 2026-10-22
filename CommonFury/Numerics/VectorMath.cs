using System.Numerics;

namespace CommonFury.Numerics;

/// <summary>Component-wise functions, finite checks and normalization with explicit fallbacks.</summary>
public static class VectorMath
{
    /// <summary>Requires a nonempty span of finite points.</summary>
    public static Vector3 Mean(ReadOnlySpan<Vector3> points)
    {
        if (points.IsEmpty)
            throw new ArgumentException("At least one point is required.", nameof(points));
        double x = 0, y = 0, z = 0;
        foreach (Vector3 point in points)
        {
            if (!IsFinite(point))
                throw new ArgumentException("Points must be finite.", nameof(points));
            x += point.X;
            y += point.Y;
            z += point.Z;
        }
        return new((float)(x / points.Length), (float)(y / points.Length), (float)(z / points.Length));
    }

    public static Vector4 Acos(Vector4 value) => new(MathF.Acos(value.X), MathF.Acos(value.Y), MathF.Acos(value.Z), MathF.Acos(value.W));
    public static Vector4 Asin(Vector4 value) => new(MathF.Asin(value.X), MathF.Asin(value.Y), MathF.Asin(value.Z), MathF.Asin(value.W));
    public static Vector4 Atan(Vector4 value) => new(MathF.Atan(value.X), MathF.Atan(value.Y), MathF.Atan(value.Z), MathF.Atan(value.W));
    public static Vector4 Pow(Vector4 value, Vector4 exponent) => new(MathF.Pow(value.X, exponent.X), MathF.Pow(value.Y, exponent.Y), MathF.Pow(value.Z, exponent.Z), MathF.Pow(value.W, exponent.W));
    public static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    public static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    public static bool IsFinite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    public static bool IsFinite(Quaternion value) => IsFinite(new Vector4(value.X, value.Y, value.Z, value.W));
    public static bool IsFinite(Matrix4x4 value) => IsFinite(new Vector4(value.M11, value.M12, value.M13, value.M14))
        && IsFinite(new Vector4(value.M21, value.M22, value.M23, value.M24))
        && IsFinite(new Vector4(value.M31, value.M32, value.M33, value.M34))
        && IsFinite(new Vector4(value.M41, value.M42, value.M43, value.M44));

    /// <summary>Returns <paramref name="fallback"/> when the length is at most <paramref name="epsilon"/>.</summary>
    public static Vector2 NormalizeOr(Vector2 value, Vector2 fallback, float epsilon = 1e-8f)
    {
        RequireEpsilon(epsilon);
        if (!IsFinite(value) || !IsFinite(fallback))
            throw new ArgumentException("Vectors must be finite.");
        double length = Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y);
        return length <= epsilon ? fallback : new((float)(value.X / length), (float)(value.Y / length));
    }

    /// <inheritdoc cref="NormalizeOr(Vector2, Vector2, float)"/>
    public static Vector3 NormalizeOr(Vector3 value, Vector3 fallback, float epsilon = 1e-8f)
    {
        RequireEpsilon(epsilon);
        if (!IsFinite(value) || !IsFinite(fallback))
            throw new ArgumentException("Vectors must be finite.");
        double length = Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z);
        return length <= epsilon ? fallback : new((float)(value.X / length), (float)(value.Y / length), (float)(value.Z / length));
    }

    /// <summary>Greatest distance from <paramref name="center"/> to a point; zero for no points.</summary>
    public static float SphereRadius(Vector3 center, ReadOnlySpan<Vector3> points)
    {
        if (!IsFinite(center))
            throw new ArgumentException("Center must be finite.", nameof(center));
        float squared = 0;
        foreach (Vector3 point in points)
        {
            if (!IsFinite(point))
                throw new ArgumentException("Points must be finite.", nameof(points));
            squared = MathF.Max(squared, Vector3.DistanceSquared(center, point));
        }
        return MathF.Sqrt(squared);
    }

    public static void RequireEpsilon(float epsilon)
    {
        if (!float.IsFinite(epsilon) || epsilon < 0)
            throw new ArgumentOutOfRangeException(nameof(epsilon));
    }

    /// <summary>Requires equal lengths; exact in-place use is allowed, partial overlap is rejected.</summary>
    public static void RequireOutput<T>(ReadOnlySpan<T> input, Span<T> output) where T : struct
    {
        if (input.Length != output.Length)
            throw new ArgumentException("Input and output lengths must match.");
        if (input.Overlaps(output, out int offset) && offset != 0)
            throw new ArgumentException("Partially overlapping buffers are not supported; exact in-place operation is allowed.");
    }
}
