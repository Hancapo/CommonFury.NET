using System.Numerics;

namespace CommonFury.Numerics;

/// <summary>Signed 16-bit quantization of box coordinates around an explicit origin and positive per-axis step.</summary>
public static class BoundsQuantization
{
    /// <summary>Expands each box limit outward to the grid of the given positive scale.</summary>
    public static Aabb3 RoundOutward(Aabb3 bounds, float scale)
    {
        if (!float.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        Vector3 minimum = bounds.Minimum * scale, maximum = bounds.Maximum * scale;
        return new(new Vector3(MathF.Floor(minimum.X), MathF.Floor(minimum.Y), MathF.Floor(minimum.Z)) / scale,
            new Vector3(MathF.Ceiling(maximum.X), MathF.Ceiling(maximum.Y), MathF.Ceiling(maximum.Z)) / scale);
    }

    /// <summary>Per-axis step that spreads the box size over 65534 signed 16-bit steps; flat axes use 1/32767.</summary>
    public static Vector3 Step(Aabb3 bounds)
    {
        Vector3 size = bounds.Size;
        return new(size.X == 0 ? 1f / 32767 : size.X / 65534, size.Y == 0 ? 1f / 32767 : size.Y / 65534, size.Z == 0 ? 1f / 32767 : size.Z / 65534);
    }

    /// <summary>Clamps a scaled coordinate to the Int16 range, then floors minima and ceils maxima.</summary>
    public static short BoundCoordinate(float value, bool maximum)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        float clamped = Math.Clamp(value, short.MinValue, short.MaxValue);
        return (short)(maximum ? MathF.Ceiling(clamped) : MathF.Floor(clamped));
    }

    /// <summary>Tests containment after expanding <paramref name="outer"/> by the per-axis tolerance.</summary>
    public static bool Contains(Aabb3 outer, Aabb3 inner, Vector3 tolerance)
    {
        Vector3 minimum = outer.Minimum - tolerance, maximum = outer.Maximum + tolerance;
        return minimum.X <= inner.Minimum.X && minimum.Y <= inner.Minimum.Y && minimum.Z <= inner.Minimum.Z &&
            maximum.X >= inner.Maximum.X && maximum.Y >= inner.Maximum.Y && maximum.Z >= inner.Maximum.Z;
    }
}
