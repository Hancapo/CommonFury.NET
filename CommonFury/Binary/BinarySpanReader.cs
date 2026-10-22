using System.Numerics;
using System.Text;

namespace CommonFury.Binary;

/// <summary>Checked synchronous reader borrowing its input. Returned slices share that input.</summary>
public ref struct BinarySpanReader
{
    private readonly ReadOnlySpan<byte> data;
    public ByteOrder ByteOrder { get; }
    public int Position { get; private set; }
    public int Remaining => this.data.Length - Position;

    public BinarySpanReader(ReadOnlySpan<byte> data, ByteOrder byteOrder = ByteOrder.Little)
    {
        if (byteOrder is not ByteOrder.Big and not ByteOrder.Little)
            throw new ArgumentOutOfRangeException(nameof(byteOrder));
        this.data = data;
        ByteOrder = byteOrder;
        Position = 0;
    }

    /// <summary>The returned slice shares the input storage.</summary>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ReadOnlySpan<byte> result = BinaryLayout.Slice(this.data, Position, count);
        Position += count;
        return result;
    }

    /// <summary>The position may equal the input length.</summary>
    public void Seek(int position)
    {
        BinaryLayout.Slice(this.data, position, 0);
        Position = position;
    }

    public byte ReadByte() => ReadBytes(1)[0];
    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());
    public ushort ReadUInt16() => Endian.U16(ReadBytes(2), ByteOrder);
    public short ReadInt16() => unchecked((short)ReadUInt16());
    public uint ReadUInt32() => Endian.U32(ReadBytes(4), ByteOrder);
    public int ReadInt32() => unchecked((int)ReadUInt32());
    public ulong ReadUInt64() => Endian.U64(ReadBytes(8), ByteOrder);
    public long ReadInt64() => unchecked((long)ReadUInt64());
    public Half ReadHalf() => BitConverter.UInt16BitsToHalf(ReadUInt16());
    public float ReadSingle() => BitConverter.UInt32BitsToSingle(ReadUInt32());
    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadUInt64());
    public Vector2 ReadVector2() => new(ReadSingle(), ReadSingle());
    public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());
    public Vector4 ReadVector4() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
    public Quaternion ReadQuaternion() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
    /// <summary>Reads sixteen floats in row-major order.</summary>
    public Matrix4x4 ReadMatrix4x4() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(),
        ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(),
        ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());

    /// <summary>Consumes a fixed-size field and decodes it up to its first NUL.</summary>
    public string ReadString(int byteCount, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        ReadOnlySpan<byte> bytes = ReadBytes(byteCount);
        int end = bytes.IndexOf((byte)0);
        return encoding.GetString(end < 0 ? bytes : bytes[..end]);
    }

    /// <summary>Consumes a fixed-size field and decodes every byte, keeping NULs and padding.</summary>
    public string ReadStringVerbatim(int byteCount, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return encoding.GetString(ReadBytes(byteCount));
    }
}
