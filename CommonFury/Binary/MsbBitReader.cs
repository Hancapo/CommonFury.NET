namespace CommonFury.Binary;

/// <summary>Bounded most-significant-bit-first reader over borrowed input.</summary>
public ref struct MsbBitReader
{
    private readonly ReadOnlySpan<byte> data;
    public long Position { get; private set; }
    public long Remaining => (long)data.Length * 8 - Position;
    public MsbBitReader(ReadOnlySpan<byte> data) => this.data = data;
    /// <summary>Reads up to 32 bits and advances.</summary>
    public uint ReadUInt32(int width)
    {
        if (width is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (width > Remaining)
            throw new InvalidDataException("Packed bit field exceeds the bounded input.");
        uint value = 0;
        while (width != 0)
        {
            int available = 8 - (int)(Position & 7), take = Math.Min(width, available);
            value = (value << take) | BitField32.Extract(data[(int)(Position >> 3)], available - take, take);
            Position += take;
            width -= take;
        }
        return value;
    }
}
