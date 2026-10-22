using System.Buffers.Binary;

namespace CommonFury.Binary;

/// <summary>Extracts and replaces bounded bit fields within unsigned 32-bit words.</summary>
public static class BitField32
{
    /// <summary>Returns the field shifted down to bit zero.</summary>
    public static uint Extract(uint value, int offset, int width) => (value >> offset) & Mask(offset, width);
    /// <summary>Returns <paramref name="word"/> with one field replaced; rejects a value wider than the field.</summary>
    public static uint Replace(uint word, int offset, int width, uint value)
    {
        uint mask = Mask(offset, width);
        if (value > mask)
            throw new ArgumentOutOfRangeException(nameof(value), "Value exceeds the packed field width.");
        return (word & ~(mask << offset)) | (value << offset);
    }
    /// <summary>Reverses the low <paramref name="width"/> bits (1–32) into the low bits; higher input bits are ignored.</summary>
    public static uint ReverseLowBits(uint value, int width)
    {
        if (width is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(width));
        return BinaryPrimitives.ReverseEndianness(ReverseBitsInBytes(value)) >> (32 - width);
    }
    private static uint ReverseBitsInBytes(uint value)
    {
        value = (value & 0x55555555) << 1 | (value >> 1) & 0x55555555;
        value = (value & 0x33333333) << 2 | (value >> 2) & 0x33333333;
        return (value & 0x0F0F0F0F) << 4 | (value >> 4) & 0x0F0F0F0F;
    }
    private static uint Mask(int offset, int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, 31);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 32 - offset);
        return uint.MaxValue >> (32 - width);
    }
}
