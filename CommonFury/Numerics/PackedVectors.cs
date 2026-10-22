using System.Numerics;

namespace CommonFury.Numerics;

/// <summary>GPU normalized packed vectors, with X in the least-significant bits. Encoding rounds to nearest even and
/// rejects components outside the normalized range.</summary>
public static class PackedVectors
{
    internal static uint Encode(float value, uint maximum)
    {
        if (!float.IsFinite(value) || value < 0 || value > 1)
            throw new ArgumentOutOfRangeException(nameof(value));
        return checked((uint)MathF.Round(value * maximum, MidpointRounding.ToEven));
    }

    public static uint EncodeUNorm8(Vector4 value) => Encode(value.X, 255) | (Encode(value.Y, 255) << 8) | (Encode(value.Z, 255) << 16) | (Encode(value.W, 255) << 24);
    /// <summary>Decodes to [0, 1]. For RGB colors, take XYZ; no color-space conversion is applied.</summary>
    public static Vector4 DecodeUNorm8(uint value) => new((value & 255) / 255f, ((value >> 8) & 255) / 255f, ((value >> 16) & 255) / 255f, (value >> 24) / 255f);
    public static uint EncodeUNorm1010102(Vector4 value) => Encode(value.X, 1023) | (Encode(value.Y, 1023) << 10) | (Encode(value.Z, 1023) << 20) | (Encode(value.W, 3) << 30);
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
