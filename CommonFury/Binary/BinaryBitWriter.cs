using System.Buffers.Binary;

namespace CommonFury.Binary;

/// <summary>Bounded least-significant-bit-first writer over borrowed storage. Writes replace existing bits.</summary>
public ref struct BinaryBitWriter
{
    private readonly Span<byte> data;
    public long Position { get; private set; }
    public long Length => (long)data.Length * 8;

    public BinaryBitWriter(Span<byte> data) { this.data = data; Position = 0; }
    /// <summary>Moves to a bit position; it may equal the destination length.</summary>
    public void Seek(long position)
    {
        if (position < 0 || position > Length)
            throw new ArgumentOutOfRangeException(nameof(position));
        Position = position;
    }
    /// <summary>Writes up to 32 bits and advances; rejects a value wider than <paramref name="bitCount"/>.</summary>
    public void WriteUInt32(uint value, int bitCount)
    {
        if (bitCount is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        if (bitCount == 0 ? value != 0 : bitCount < 32 && value >= (1u << bitCount))
            throw new ArgumentOutOfRangeException(nameof(value), "Value exceeds the requested field width.");
        if (Position > Length - bitCount)
            throw new ArgumentException("Bit field exceeds the destination.");
        long start = Position >> 3;
        if (start <= data.Length - 8)
        {
            Span<byte> bytes = data[(int)start..];
            int offset = (int)(Position & 7);
            ulong mask = ((1ul << bitCount) - 1) << offset;
            ulong word = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, (word & ~mask) | ((ulong)value << offset));
            Position += bitCount;
            return;
        }
        int shift = 0;
        while (bitCount > 0)
        {
            int offset = (int)(Position & 7);
            int width = Math.Min(bitCount, 8 - offset);
            int index = (int)(Position >> 3);
            data[index] = (byte)BitField32.Replace(data[index], offset, width, BitField32.Extract(value, shift, width));
            Position += width;
            shift += width;
            bitCount -= width;
        }
    }
}
