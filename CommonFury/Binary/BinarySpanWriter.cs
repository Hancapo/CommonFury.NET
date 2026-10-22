using System.Numerics;

namespace CommonFury.Binary;

/// <summary>Writes into caller-owned storage. Overflow throws; already written bytes are not rolled back.</summary>
public ref struct BinarySpanWriter
{
    private readonly Span<byte> data;
    public ByteOrder ByteOrder { get; }
    public int Position { get; private set; }
    public int Remaining => this.data.Length - Position;

    public BinarySpanWriter(Span<byte> data, ByteOrder byteOrder = ByteOrder.Little)
    {
        if (byteOrder is not ByteOrder.Big and not ByteOrder.Little)
            throw new ArgumentOutOfRangeException(nameof(byteOrder));
        this.data = data;
        ByteOrder = byteOrder;
        Position = 0;
    }

    private Span<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
            throw new ArgumentException("Destination buffer is too small.");
        Span<byte> result = this.data.Slice(Position, count);
        Position += count;
        return result;
    }
    public void WriteBytes(ReadOnlySpan<byte> data) => data.CopyTo(Take(data.Length));
    /// <summary>The position may equal the destination length.</summary>
    public void Seek(int position)
    {
        if (position < 0 || position > this.data.Length)
            throw new ArgumentOutOfRangeException(nameof(position));
        Position = position;
    }
    public void WriteZeros(int count) => Take(count).Clear();
    public void WriteByte(byte value) => Take(1)[0] = value;
    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));
    public void WriteUInt16(ushort value) => Endian.Put16(Take(2), value, ByteOrder);
    public void WriteInt16(short value) => WriteUInt16(unchecked((ushort)value));
    public void WriteUInt32(uint value) => Endian.Put32(Take(4), value, ByteOrder);
    public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));
    public void WriteUInt64(ulong value) => Endian.Put64(Take(8), value, ByteOrder);
    public void WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));
    public void WriteHalf(Half value) => WriteUInt16(BitConverter.HalfToUInt16Bits(value));
    public void WriteSingle(float value) => WriteUInt32(BitConverter.SingleToUInt32Bits(value));
    public void WriteDouble(double value) => WriteUInt64(BitConverter.DoubleToUInt64Bits(value));
    public void WriteVector2(Vector2 value) { WriteSingle(value.X); WriteSingle(value.Y); }
    public void WriteVector3(Vector3 value) { WriteSingle(value.X); WriteSingle(value.Y); WriteSingle(value.Z); }
    public void WriteVector4(Vector4 value) { WriteSingle(value.X); WriteSingle(value.Y); WriteSingle(value.Z); WriteSingle(value.W); }
    public void WriteQuaternion(Quaternion value) { WriteSingle(value.X); WriteSingle(value.Y); WriteSingle(value.Z); WriteSingle(value.W); }
    /// <summary>Writes sixteen floats in row-major order.</summary>
    public void WriteMatrix4x4(Matrix4x4 value)
    {
        WriteSingle(value.M11);
        WriteSingle(value.M12);
        WriteSingle(value.M13);
        WriteSingle(value.M14);
        WriteSingle(value.M21);
        WriteSingle(value.M22);
        WriteSingle(value.M23);
        WriteSingle(value.M24);
        WriteSingle(value.M31);
        WriteSingle(value.M32);
        WriteSingle(value.M33);
        WriteSingle(value.M34);
        WriteSingle(value.M41);
        WriteSingle(value.M42);
        WriteSingle(value.M43);
        WriteSingle(value.M44);
    }
}
