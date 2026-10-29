using System.Buffers.Binary;
using System.Numerics;
using CommonFury.Binary;
using CommonFury.Numerics;

namespace CommonFury.Vertices;

/// <summary>Decodes and encodes vertex attribute values. Missing lanes decode as zero; encoding ignores undeclared lanes.</summary>
/// <remarks>Normalized formats quantize with nearest-even rounding and reject out-of-range or non-finite values.
/// Float and half formats store the value as given; range and finiteness policies belong to the caller.</remarks>
public static class VertexElementCodec
{
    /// <summary>Decodes one element from the start of <paramref name="data"/> into four components.</summary>
    public static Vector4 Read(ReadOnlySpan<byte> data, VertexElementFormat format, ByteOrder byteOrder = ByteOrder.Little)
    {
        var reader = new BinarySpanReader(data, byteOrder);
        return format switch
        {
            VertexElementFormat.Float => new(reader.ReadSingle(), 0, 0, 0),
            VertexElementFormat.Float2 => new(reader.ReadVector2(), 0, 0),
            VertexElementFormat.Float3 => new(reader.ReadVector3(), 0),
            VertexElementFormat.Float4 => reader.ReadVector4(),
            VertexElementFormat.Half2 => new((float)reader.ReadHalf(), (float)reader.ReadHalf(), 0, 0),
            VertexElementFormat.Half => new((float)reader.ReadHalf(), 0, 0, 0),
            VertexElementFormat.Half3 => new((float)reader.ReadHalf(), (float)reader.ReadHalf(), (float)reader.ReadHalf(), 0),
            VertexElementFormat.Half4 => new((float)reader.ReadHalf(), (float)reader.ReadHalf(), (float)reader.ReadHalf(), (float)reader.ReadHalf()),
            VertexElementFormat.Byte4 => new(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()),
            VertexElementFormat.UNorm8x4 => DecodeUNorm8(Endian.U32(data, ByteOrder.Little)),
            VertexElementFormat.SNorm8x4 => DecodeSNorm8(Endian.U32(data, ByteOrder.Little)),
            VertexElementFormat.UNorm10x3A2 => DecodeUNorm1010102(reader.ReadUInt32()),
            VertexElementFormat.BiasedUNorm10x3A2 => DecodeBiased1010102(reader.ReadUInt32()),
            VertexElementFormat.UNorm16 => new(reader.ReadUInt16() / 65535f, 0, 0, 0),
            VertexElementFormat.UNorm16x2 => new(reader.ReadUInt16() / 65535f, reader.ReadUInt16() / 65535f, 0, 0),
            VertexElementFormat.UNorm8x2 => new(reader.ReadByte() / 255f, reader.ReadByte() / 255f, 0, 0),
            VertexElementFormat.Int16x2 => new(reader.ReadInt16(), reader.ReadInt16(), 0, 0),
            VertexElementFormat.Int16x4 => new(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16()),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    /// <summary>Encodes one element into the start of <paramref name="data"/> (little-endian), rejecting values the format cannot represent.</summary>
    public static void Write(Span<byte> data, VertexElementFormat format, Vector4 value)
    {
        var writer = new BinarySpanWriter(data);
        switch (format)
        {
            case VertexElementFormat.Float:
                writer.WriteSingle(value.X);
                break;
            case VertexElementFormat.Float2:
                writer.WriteVector2(new(value.X, value.Y));
                break;
            case VertexElementFormat.Float3:
                writer.WriteVector3(new(value.X, value.Y, value.Z));
                break;
            case VertexElementFormat.Float4:
                writer.WriteVector4(value);
                break;
            case VertexElementFormat.Half2:
                writer.WriteHalf((Half)value.X);
                writer.WriteHalf((Half)value.Y);
                break;
            case VertexElementFormat.Half:
                writer.WriteHalf((Half)value.X);
                break;
            case VertexElementFormat.Half3:
                writer.WriteHalf((Half)value.X);
                writer.WriteHalf((Half)value.Y);
                writer.WriteHalf((Half)value.Z);
                break;
            case VertexElementFormat.Half4:
                writer.WriteHalf((Half)value.X);
                writer.WriteHalf((Half)value.Y);
                writer.WriteHalf((Half)value.Z);
                writer.WriteHalf((Half)value.W);
                break;
            case VertexElementFormat.Byte4:
                RequireByte4(value);
                writer.WriteByte((byte)value.X);
                writer.WriteByte((byte)value.Y);
                writer.WriteByte((byte)value.Z);
                writer.WriteByte((byte)value.W);
                break;
            case VertexElementFormat.UNorm8x4:
                writer.WriteUInt32(PackedVectors.EncodeUNorm8(value));
                break;
            case VertexElementFormat.SNorm8x4:
                writer.WriteUInt32(Signed(value.X) | (Signed(value.Y) << 8) | (Signed(value.Z) << 16) | (Signed(value.W) << 24));
                break;
            case VertexElementFormat.UNorm10x3A2:
                writer.WriteUInt32(PackedVectors.EncodeUNorm1010102(value));
                break;
            case VertexElementFormat.BiasedUNorm10x3A2:
                writer.WriteUInt32(Biased(value.X, 1023) | (Biased(value.Y, 1023) << 10) | (Biased(value.Z, 1023) << 20) | (Biased(value.W, 3) << 30));
                break;
            case VertexElementFormat.UNorm16:
                writer.WriteUInt16((ushort)PackedVectors.EncodeUNorm(value.X, 65535));
                break;
            case VertexElementFormat.UNorm16x2:
                writer.WriteUInt16((ushort)PackedVectors.EncodeUNorm(value.X, 65535));
                writer.WriteUInt16((ushort)PackedVectors.EncodeUNorm(value.Y, 65535));
                break;
            case VertexElementFormat.UNorm8x2:
                writer.WriteByte((byte)PackedVectors.EncodeUNorm(value.X, 255));
                writer.WriteByte((byte)PackedVectors.EncodeUNorm(value.Y, 255));
                break;
            case VertexElementFormat.Int16x2:
                writer.WriteInt16(Integer16(value.X));
                writer.WriteInt16(Integer16(value.Y));
                break;
            case VertexElementFormat.Int16x4:
                writer.WriteInt16(Integer16(value.X));
                writer.WriteInt16(Integer16(value.Y));
                writer.WriteInt16(Integer16(value.Z));
                writer.WriteInt16(Integer16(value.W));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>Decodes <paramref name="destination"/>.Length elements spaced <paramref name="stride"/> bytes apart from the start of
    /// <paramref name="data"/>, each as <see cref="Read(ReadOnlySpan{byte}, VertexElementFormat, ByteOrder)"/> would. The input is
    /// borrowed and must not overlap <paramref name="destination"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The byte order or format is unknown, or <paramref name="stride"/> is shorter than an element.</exception>
    /// <exception cref="ArgumentException"><paramref name="data"/> does not hold every element. Nothing is written on failure.</exception>
    public static void Read(ReadOnlySpan<byte> data, int stride, VertexElementFormat format, Span<Vector4> destination, ByteOrder byteOrder = ByteOrder.Little)
    {
        if (byteOrder is not ByteOrder.Big and not ByteOrder.Little)
            throw new ArgumentOutOfRangeException(nameof(byteOrder));
        RequireLayout(data, stride, format, destination.Length);
        if (byteOrder == ByteOrder.Big)
            Decode<BigEndian>(data, stride, format, destination);
        else
            Decode<LittleEndian>(data, stride, format, destination);
    }

    /// <summary>Encodes <paramref name="values"/> little-endian into elements spaced <paramref name="stride"/> bytes apart from the start
    /// of <paramref name="data"/>, each as <see cref="Write(Span{byte}, VertexElementFormat, Vector4)"/> would. Bytes between elements
    /// are left unchanged. <paramref name="values"/> is borrowed and must not overlap <paramref name="data"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The format is unknown or <paramref name="stride"/> is shorter than an element,
    /// checked before writing; or a value cannot be represented, after the earlier elements (and earlier components of a
    /// per-component format such as <see cref="VertexElementFormat.Int16x4"/>) were written.</exception>
    /// <exception cref="ArgumentException"><paramref name="data"/> cannot hold every element; nothing is written.</exception>
    public static void Write(Span<byte> data, int stride, VertexElementFormat format, ReadOnlySpan<Vector4> values)
    {
        RequireLayout(data, stride, format, values.Length);
        int count = values.Length;
        switch (format)
        {
            case VertexElementFormat.Float:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    PutSingle(data, o, values[i].X);
                break;
            case VertexElementFormat.Float2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                {
                    Vector4 value = values[i];
                    PutSingle(data, o, value.X);
                    PutSingle(data, o + 4, value.Y);
                }
                break;
            case VertexElementFormat.Float3:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                {
                    Vector4 value = values[i];
                    PutSingle(data, o, value.X);
                    PutSingle(data, o + 4, value.Y);
                    PutSingle(data, o + 8, value.Z);
                }
                break;
            case VertexElementFormat.Float4:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                {
                    Vector4 value = values[i];
                    PutSingle(data, o, value.X);
                    PutSingle(data, o + 4, value.Y);
                    PutSingle(data, o + 8, value.Z);
                    PutSingle(data, o + 12, value.W);
                }
                break;
            case VertexElementFormat.Half:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    PutHalf(data, o, values[i].X);
                break;
            case VertexElementFormat.Half2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                {
                    Vector4 value = values[i];
                    PutHalf(data, o, value.X);
                    PutHalf(data, o + 2, value.Y);
                }
                break;
            case VertexElementFormat.Half3:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                {
                    Vector4 value = values[i];
                    PutHalf(data, o, value.X);
                    PutHalf(data, o + 2, value.Y);
                    PutHalf(data, o + 4, value.Z);
                }
                break;
            case VertexElementFormat.Half4:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                {
                    Vector4 value = values[i];
                    PutHalf(data, o, value.X);
                    PutHalf(data, o + 2, value.Y);
                    PutHalf(data, o + 4, value.Z);
                    PutHalf(data, o + 6, value.W);
                }
                break;
            case VertexElementFormat.Byte4:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                {
                    Vector4 value = values[i];
                    RequireByte4(value);
                    data[o] = (byte)value.X;
                    data[o + 1] = (byte)value.Y;
                    data[o + 2] = (byte)value.Z;
                    data[o + 3] = (byte)value.W;
                }
                break;
            case VertexElementFormat.UNorm8x4:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    BinaryPrimitives.WriteUInt32LittleEndian(data[o..], PackedVectors.EncodeUNorm8(values[i]));
                break;
            case VertexElementFormat.SNorm8x4:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                {
                    Vector4 value = values[i];
                    BinaryPrimitives.WriteUInt32LittleEndian(data[o..], Signed(value.X) | (Signed(value.Y) << 8) | (Signed(value.Z) << 16) | (Signed(value.W) << 24));
                }
                break;
            case VertexElementFormat.UNorm10x3A2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    BinaryPrimitives.WriteUInt32LittleEndian(data[o..], PackedVectors.EncodeUNorm1010102(values[i]));
                break;
            case VertexElementFormat.BiasedUNorm10x3A2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                {
                    Vector4 value = values[i];
                    BinaryPrimitives.WriteUInt32LittleEndian(data[o..],
                        Biased(value.X, 1023) | (Biased(value.Y, 1023) << 10) | (Biased(value.Z, 1023) << 20) | (Biased(value.W, 3) << 30));
                }
                break;
            case VertexElementFormat.UNorm16:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    BinaryPrimitives.WriteUInt16LittleEndian(data[o..], (ushort)PackedVectors.EncodeUNorm(values[i].X, 65535));
                break;
            case VertexElementFormat.UNorm16x2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                {
                    Vector4 value = values[i];
                    BinaryPrimitives.WriteUInt16LittleEndian(data[o..], (ushort)PackedVectors.EncodeUNorm(value.X, 65535));
                    BinaryPrimitives.WriteUInt16LittleEndian(data[(o + 2)..], (ushort)PackedVectors.EncodeUNorm(value.Y, 65535));
                }
                break;
            case VertexElementFormat.UNorm8x2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                {
                    Vector4 value = values[i];
                    data[o] = (byte)PackedVectors.EncodeUNorm(value.X, 255);
                    data[o + 1] = (byte)PackedVectors.EncodeUNorm(value.Y, 255);
                }
                break;
            case VertexElementFormat.Int16x2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                {
                    Vector4 value = values[i];
                    BinaryPrimitives.WriteInt16LittleEndian(data[o..], Integer16(value.X));
                    BinaryPrimitives.WriteInt16LittleEndian(data[(o + 2)..], Integer16(value.Y));
                }
                break;
            case VertexElementFormat.Int16x4:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                {
                    Vector4 value = values[i];
                    BinaryPrimitives.WriteInt16LittleEndian(data[o..], Integer16(value.X));
                    BinaryPrimitives.WriteInt16LittleEndian(data[(o + 2)..], Integer16(value.Y));
                    BinaryPrimitives.WriteInt16LittleEndian(data[(o + 4)..], Integer16(value.Z));
                    BinaryPrimitives.WriteInt16LittleEndian(data[(o + 6)..], Integer16(value.W));
                }
                break;
        }
    }

    private static void Decode<TOrder>(ReadOnlySpan<byte> data, int stride, VertexElementFormat format, Span<Vector4> destination)
        where TOrder : struct, IByteOrder
    {
        int count = destination.Length;
        switch (format)
        {
            case VertexElementFormat.Float:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(Single(data, o), 0, 0, 0);
                break;
            case VertexElementFormat.Float2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(Single(data, o), Single(data, o + 4), 0, 0);
                break;
            case VertexElementFormat.Float3:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(Single(data, o), Single(data, o + 4), Single(data, o + 8), 0);
                break;
            case VertexElementFormat.Float4:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(Single(data, o), Single(data, o + 4), Single(data, o + 8), Single(data, o + 12));
                break;
            case VertexElementFormat.Half:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(HalfValue(data, o), 0, 0, 0);
                break;
            case VertexElementFormat.Half2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(HalfValue(data, o), HalfValue(data, o + 2), 0, 0);
                break;
            case VertexElementFormat.Half3:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(HalfValue(data, o), HalfValue(data, o + 2), HalfValue(data, o + 4), 0);
                break;
            case VertexElementFormat.Half4:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(HalfValue(data, o), HalfValue(data, o + 2), HalfValue(data, o + 4), HalfValue(data, o + 6));
                break;
            case VertexElementFormat.Byte4:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(data[o], data[o + 1], data[o + 2], data[o + 3]);
                break;
            case VertexElementFormat.UNorm8x4:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = DecodeUNorm8(BinaryPrimitives.ReadUInt32LittleEndian(data[o..]));
                break;
            case VertexElementFormat.SNorm8x4:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = DecodeSNorm8(BinaryPrimitives.ReadUInt32LittleEndian(data[o..]));
                break;
            case VertexElementFormat.UNorm10x3A2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = DecodeUNorm1010102(TOrder.U32(data[o..]));
                break;
            case VertexElementFormat.BiasedUNorm10x3A2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = DecodeBiased1010102(TOrder.U32(data[o..]));
                break;
            case VertexElementFormat.UNorm16:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(TOrder.U16(data[o..]) / 65535f, 0, 0, 0);
                break;
            case VertexElementFormat.UNorm16x2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(TOrder.U16(data[o..]) / 65535f, TOrder.U16(data[(o + 2)..]) / 65535f, 0, 0);
                break;
            case VertexElementFormat.UNorm8x2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(data[o] / 255f, data[o + 1] / 255f, 0, 0);
                break;
            case VertexElementFormat.Int16x2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(Int16(data, o), Int16(data, o + 2), 0, 0);
                break;
            case VertexElementFormat.Int16x4:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(Int16(data, o), Int16(data, o + 2), Int16(data, o + 4), Int16(data, o + 6));
                break;
        }

        static float Single(ReadOnlySpan<byte> data, int offset) => BitConverter.UInt32BitsToSingle(TOrder.U32(data[offset..]));
        static float HalfValue(ReadOnlySpan<byte> data, int offset) => (float)BitConverter.UInt16BitsToHalf(TOrder.U16(data[offset..]));
        static short Int16(ReadOnlySpan<byte> data, int offset) => unchecked((short)TOrder.U16(data[offset..]));
    }

    // Every element offset then fits in an int; the bulk loops' offset may wrap only after stepping past the last element.
    private static void RequireLayout(ReadOnlySpan<byte> data, int stride, VertexElementFormat format, int count)
    {
        int size = VertexElementFormats.Size(format);
        if (stride < size)
            throw new ArgumentOutOfRangeException(nameof(stride), "Stride is shorter than one element.");
        if (count != 0 && (long)(count - 1) * stride + size > data.Length)
            throw new ArgumentException("Vertex buffer is too small for the requested elements.", nameof(data));
    }

    private static void PutSingle(Span<byte> data, int offset, float value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], BitConverter.SingleToUInt32Bits(value));

    private static void PutHalf(Span<byte> data, int offset, float value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data[offset..], BitConverter.HalfToUInt16Bits((Half)value));

    private static void RequireByte4(Vector4 value)
    {
        if (!VectorMath.IsFinite(value) || Vector4.Min(value, Vector4.Zero) != Vector4.Zero || Vector4.Max(value, new(255)) != new Vector4(255)
            || value.X != MathF.Truncate(value.X) || value.Y != MathF.Truncate(value.Y) || value.Z != MathF.Truncate(value.Z) || value.W != MathF.Truncate(value.W))
            throw new ArgumentOutOfRangeException(nameof(value), "Unsigned byte components must be integers in [0,255].");
    }

    private interface IByteOrder
    {
        static abstract ushort U16(ReadOnlySpan<byte> data);
        static abstract uint U32(ReadOnlySpan<byte> data);
    }

    private readonly struct LittleEndian : IByteOrder
    {
        public static ushort U16(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt16LittleEndian(data);
        public static uint U32(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    private readonly struct BigEndian : IByteOrder
    {
        public static ushort U16(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt16BigEndian(data);
        public static uint U32(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32BigEndian(data);
    }

    private static Vector4 DecodeUNorm8(uint value) => new((value & 255) / 255f, ((value >> 8) & 255) / 255f, ((value >> 16) & 255) / 255f, (value >> 24) / 255f);
    private static Vector4 DecodeSNorm8(uint value) => new(DecodeSigned(value), DecodeSigned(value >> 8), DecodeSigned(value >> 16), DecodeSigned(value >> 24));
    private static float DecodeSigned(uint value) => Math.Max(-1, unchecked((sbyte)value) / 127f);
    private static Vector4 DecodeUNorm1010102(uint value) => new((value & 1023) / 1023f, ((value >> 10) & 1023) / 1023f, ((value >> 20) & 1023) / 1023f, (value >> 30) / 3f);
    private static Vector4 DecodeBiased1010102(uint value) => new(
        (value & 1023) * (2f / 1023) - 1, (value >> 10 & 1023) * (2f / 1023) - 1,
        (value >> 20 & 1023) * (2f / 1023) - 1, (value >> 30) * (2f / 3) - 1);

    private static uint Signed(float value)
    {
        if (!float.IsFinite(value) || value < -1 || value > 1)
            throw new ArgumentOutOfRangeException(nameof(value));
        return unchecked((byte)(sbyte)MathF.Round(value * 127, MidpointRounding.ToEven));
    }

    private static uint Biased(float value, int steps)
    {
        if (!float.IsFinite(value) || value < -1 || value > 1)
            throw new ArgumentOutOfRangeException(nameof(value), "Normalized vertex value is outside its encoded range.");
        return (uint)MathF.Round((value - -1) / (1 - -1) * steps, MidpointRounding.ToEven);
    }

    private static short Integer16(float value)
    {
        if (!float.IsFinite(value) || value < short.MinValue || value > short.MaxValue || value != MathF.Truncate(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        return (short)value;
    }
}
