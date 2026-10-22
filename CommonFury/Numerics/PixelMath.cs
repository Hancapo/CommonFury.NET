namespace CommonFury.Numerics;

/// <summary>Conversions between normalized channel values and integer channels.</summary>
public static class PixelMath
{
    /// <summary>Rounds to nearest and clamps to 0..255; NaN becomes zero.</summary>
    public static byte ToByte(float value)
    {
        if (!(value > 0))
            return 0;
        if (value >= 1)
            return 255;
        return (byte)(value * 255 + 0.5f);
    }

    /// <summary>Rescales a byte to 0..<paramref name="maximum"/>, rounding to nearest.</summary>
    public static uint Quantize(byte value, uint maximum) => ((uint)value * maximum + 127) / 255;
    /// <summary>Rescales 0..<paramref name="maximum"/> to a byte, rounding to nearest.</summary>
    public static byte Expand(uint value, uint maximum) => (byte)((value * 255 + maximum / 2) / maximum);
}
