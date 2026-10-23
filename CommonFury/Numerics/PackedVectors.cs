using System.Numerics;

namespace CommonFury.Numerics;

/// <summary>GPU normalized packed vectors, with X in the least-significant bits. Encoding rounds to nearest even and
/// rejects components outside the normalized range.</summary>
public static class PackedVectors
{
    /// <summary>Encodes a value in [0, 1] as an unsigned normalized integer in [0, <paramref name="maximum"/>], rounding
    /// <c>value × maximum</c> (computed in single precision) to nearest even.</summary>
    /// <remarks><paramref name="maximum"/> is the largest code, <c>2^bits − 1</c> for a bit field (255 for 8 bits, 65535 for
    /// 16 bits), from 1 to 2^24 − 1.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is non-finite or outside [0, 1], or
    /// <paramref name="maximum"/> is outside its range.</exception>
    public static uint EncodeUNorm(float value, uint maximum)
    {
        ArgumentOutOfRangeException.ThrowIfZero(maximum);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximum, (1u << 24) - 1);
        if (!float.IsFinite(value) || value < 0 || value > 1)
            throw new ArgumentOutOfRangeException(nameof(value));
        return (uint)MathF.Round(value * maximum, MidpointRounding.ToEven);
    }

    public static uint EncodeUNorm8(Vector4 value) => EncodeUNorm(value.X, 255) | (EncodeUNorm(value.Y, 255) << 8) | (EncodeUNorm(value.Z, 255) << 16) | (EncodeUNorm(value.W, 255) << 24);
    /// <summary>Decodes to [0, 1]. For RGB colors, take XYZ; no color-space conversion is applied.</summary>
    public static Vector4 DecodeUNorm8(uint value) => new((value & 255) / 255f, ((value >> 8) & 255) / 255f, ((value >> 16) & 255) / 255f, (value >> 24) / 255f);
    public static uint EncodeUNorm1010102(Vector4 value) => EncodeUNorm(value.X, 1023) | (EncodeUNorm(value.Y, 1023) << 10) | (EncodeUNorm(value.Z, 1023) << 20) | (EncodeUNorm(value.W, 3) << 30);
    public static Vector4 DecodeUNorm1010102(uint value) => new((value & 1023) / 1023f, ((value >> 10) & 1023) / 1023f, ((value >> 20) & 1023) / 1023f, (value >> 30) / 3f);

    private static uint Signed(float value)
    {
        if (!float.IsFinite(value) || value < -1 || value > 1)
            throw new ArgumentOutOfRangeException(nameof(value));
        return unchecked((byte)(sbyte)MathF.Round(value * 127, MidpointRounding.ToEven));
    }

    private static float DecodeSigned(uint value) => Math.Max(-1, unchecked((sbyte)value) / 127f);
    public static uint EncodeSNorm8(Vector4 value) => Signed(value.X) | (Signed(value.Y) << 8) | (Signed(value.Z) << 16) | (Signed(value.W) << 24);
    /// <summary>Decodes to [-1, 1]; -128 decodes to -1 like -127.</summary>
    public static Vector4 DecodeSNorm8(uint value) => new(DecodeSigned(value), DecodeSigned(value >> 8), DecodeSigned(value >> 16), DecodeSigned(value >> 24));
}
