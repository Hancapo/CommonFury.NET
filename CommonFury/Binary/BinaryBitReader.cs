using System.Buffers.Binary;

namespace CommonFury.Binary;

/// <summary>Bounded least-significant-bit-first reader over borrowed input.</summary>
public ref struct BinaryBitReader
{
    private readonly ReadOnlySpan<byte> data;
    public long Position { get; private set; }
    public readonly long Length => (long)data.Length * 8;
    public long Remaining => Length - Position;

    public BinaryBitReader(ReadOnlySpan<byte> data) { this.data = data; Position = 0; }

    /// <summary>Moves to a bit position; it may equal the input length.</summary>
    public void Seek(long position)
    {
        if (position < 0 || position > Length)
            throw new InvalidDataException("Bit position exceeds the bounded input.");
        Position = position;
    }

    /// <summary>Reads up to 32 bits and advances.</summary>
    public uint ReadUInt32(int bitCount)
    {
        uint result = ReadUInt32At(Position, bitCount);
        Position += bitCount;
        return result;
    }

    /// <summary>Reads up to 32 bits at a bit position without moving.</summary>
    public readonly uint ReadUInt32At(long position, int bitCount)
    {
        if (bitCount is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        if (position < 0 || position > Length - bitCount)
            throw new InvalidDataException("Bit field exceeds the bounded input.");
        long start = position >> 3;
        if (start <= data.Length - 8)
        {
            // A field spans at most 39 bits from its first byte, so one 64-bit load covers it.
            ulong word = BinaryPrimitives.ReadUInt64LittleEndian(data[(int)start..]);
            return (uint)((word >> (int)(position & 7)) & ((1ul << bitCount) - 1));
        }
        uint result = 0;
        int shift = 0;
        while (bitCount > 0)
        {
            int offset = (int)(position & 7);
            int width = Math.Min(bitCount, 8 - offset);
            result |= BitField32.Extract(data[(int)(position >> 3)], offset, width) << shift;
            position += width;
            shift += width;
            bitCount -= width;
        }
        return result;
    }
}
