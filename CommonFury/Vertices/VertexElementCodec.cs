using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using CommonFury.Binary;
using CommonFury.Numerics;

namespace CommonFury.Vertices;

/// <summary>Decodes and encodes vertex attribute values. Missing lanes decode as zero; encoding ignores undeclared lanes.</summary>
/// <remarks>Normalized formats quantize with nearest-even rounding and reject out-of-range or non-finite values.
/// Float and half formats store the value as given; range and finiteness policies belong to the caller.</remarks>
public static class VertexElementCodec
{
    /// <summary>Decodes one element from the start of <paramref name="data"/> into four components.</summary>
    /// <exception cref="InvalidDataException"><paramref name="data"/> is shorter than one element.</exception>
    public static Vector4 Read(ReadOnlySpan<byte> data, VertexElementFormat format, ByteOrder byteOrder = ByteOrder.Little)
    {
        if (byteOrder is not ByteOrder.Big and not ByteOrder.Little)
            throw new ArgumentOutOfRangeException(nameof(byteOrder));
        int size = VertexElementFormats.Size(format);
        BinaryLayout.Slice(data, 0, size);
        Vector4 result = default;
        Read(data, size, format, new Span<Vector4>(ref result), byteOrder);
        return result;
    }

    /// <summary>Encodes one element into the start of <paramref name="data"/> (little-endian), rejecting values the format cannot represent.</summary>
    /// <exception cref="ArgumentException"><paramref name="data"/> is shorter than one element; nothing is written.</exception>
    public static void Write(Span<byte> data, VertexElementFormat format, Vector4 value)
    {
        // No element exceeds 16 bytes, so longer buffers skip the size lookup; Encode rejects an unknown format itself.
        if (data.Length < 16 && data.Length < VertexElementFormats.Size(format))
            throw new ArgumentException("Destination buffer is too small.");
        Encode(data, format, value);
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
        for (int i = 0, o = 0; i < values.Length; i++, o += stride)
            Encode(data[o..], format, values[i]);
    }

    // One element at the start of a buffer already known to hold it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Encode(Span<byte> data, VertexElementFormat format, Vector4 value)
    {
        switch (format)
        {
            case VertexElementFormat.Float:
                PutSingle(data, 0, value.X);
                break;
            case VertexElementFormat.Float2:
                PutSingle(data, 0, value.X);
                PutSingle(data, 4, value.Y);
                break;
            case VertexElementFormat.Float3:
                PutSingle(data, 0, value.X);
                PutSingle(data, 4, value.Y);
                PutSingle(data, 8, value.Z);
                break;
            case VertexElementFormat.Float4:
                PutSingle(data, 0, value.X);
                PutSingle(data, 4, value.Y);
                PutSingle(data, 8, value.Z);
                PutSingle(data, 12, value.W);
                break;
            case VertexElementFormat.Half:
                PutHalf(data, 0, value.X);
                break;
            case VertexElementFormat.Half2:
                PutHalf(data, 0, value.X);
                PutHalf(data, 2, value.Y);
                break;
            case VertexElementFormat.Half3:
                PutHalf(data, 0, value.X);
                PutHalf(data, 2, value.Y);
                PutHalf(data, 4, value.Z);
                break;
            case VertexElementFormat.Half4:
                PutHalf(data, 0, value.X);
                PutHalf(data, 2, value.Y);
                PutHalf(data, 4, value.Z);
                PutHalf(data, 6, value.W);
                break;
            case VertexElementFormat.Byte4:
                RequireByte4(value);
                data[0] = (byte)value.X;
                data[1] = (byte)value.Y;
                data[2] = (byte)value.Z;
                data[3] = (byte)value.W;
                break;
            case VertexElementFormat.UNorm8x4:
                BinaryPrimitives.WriteUInt32LittleEndian(data, PackedVectors.EncodeUNorm8(value));
                break;
            case VertexElementFormat.SNorm8x4:
                BinaryPrimitives.WriteUInt32LittleEndian(data, Signed(value.X) | (Signed(value.Y) << 8) | (Signed(value.Z) << 16) | (Signed(value.W) << 24));
                break;
            case VertexElementFormat.UNorm10x3A2:
                BinaryPrimitives.WriteUInt32LittleEndian(data, PackedVectors.EncodeUNorm1010102(value));
                break;
            case VertexElementFormat.BiasedUNorm10x3A2:
                BinaryPrimitives.WriteUInt32LittleEndian(data,
                    Biased(value.X, 1023) | (Biased(value.Y, 1023) << 10) | (Biased(value.Z, 1023) << 20) | (Biased(value.W, 3) << 30));
                break;
            case VertexElementFormat.UNorm16:
                BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)PackedVectors.EncodeUNorm(value.X, 65535));
                break;
            case VertexElementFormat.UNorm16x2:
                BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)PackedVectors.EncodeUNorm(value.X, 65535));
                BinaryPrimitives.WriteUInt16LittleEndian(data[2..], (ushort)PackedVectors.EncodeUNorm(value.Y, 65535));
                break;
            case VertexElementFormat.UNorm8x2:
                data[0] = (byte)PackedVectors.EncodeUNorm(value.X, 255);
                data[1] = (byte)PackedVectors.EncodeUNorm(value.Y, 255);
                break;
            case VertexElementFormat.Int16x2:
                BinaryPrimitives.WriteInt16LittleEndian(data, Integer16(value.X));
                BinaryPrimitives.WriteInt16LittleEndian(data[2..], Integer16(value.Y));
                break;
            case VertexElementFormat.Int16x4:
                BinaryPrimitives.WriteInt16LittleEndian(data, Integer16(value.X));
                BinaryPrimitives.WriteInt16LittleEndian(data[2..], Integer16(value.Y));
                BinaryPrimitives.WriteInt16LittleEndian(data[4..], Integer16(value.Z));
                BinaryPrimitives.WriteInt16LittleEndian(data[6..], Integer16(value.W));
                break;
            case VertexElementFormat.UNorm8:
                data[0] = (byte)PackedVectors.EncodeUNorm(value.X, 255);
                break;
            case VertexElementFormat.SNorm8:
                data[0] = (byte)Signed(value.X);
                break;
            case VertexElementFormat.SNorm8x2:
                BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)(Signed(value.X) | Signed(value.Y) << 8));
                break;
            case VertexElementFormat.SNorm16x2:
                BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)SignedNorm(value.X, 32767) & 0xFFFF | (uint)SignedNorm(value.Y, 32767) << 16);
                break;
            case VertexElementFormat.UNorm16x4:
                BinaryPrimitives.WriteUInt64LittleEndian(data, PackedVectors.EncodeUNorm(value.X, 65535) | (ulong)PackedVectors.EncodeUNorm(value.Y, 65535) << 16
                    | (ulong)PackedVectors.EncodeUNorm(value.Z, 65535) << 32 | (ulong)PackedVectors.EncodeUNorm(value.W, 65535) << 48);
                break;
            case VertexElementFormat.SNorm10x3A2:
                BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)SignedNorm(value.X, 511) & 1023 | ((uint)SignedNorm(value.Y, 511) & 1023) << 10
                    | ((uint)SignedNorm(value.Z, 511) & 1023) << 20 | (uint)SignedNorm(value.W, 1) << 30);
                break;
            case VertexElementFormat.UInt8:
                data[0] = Integer8(value.X);
                break;
            case VertexElementFormat.UInt8x2:
                BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)(Integer8(value.X) | Integer8(value.Y) << 8));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
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
            case VertexElementFormat.UNorm8:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(data[o] / 255f, 0, 0, 0);
                break;
            case VertexElementFormat.SNorm8:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(DecodeSigned(data[o]), 0, 0, 0);
                break;
            case VertexElementFormat.SNorm8x2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(DecodeSigned(data[o]), DecodeSigned(data[o + 1]), 0, 0);
                break;
            case VertexElementFormat.SNorm16x2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(Math.Max(-1, Int16(data, o) / 32767f), Math.Max(-1, Int16(data, o + 2) / 32767f), 0, 0);
                break;
            case VertexElementFormat.UNorm16x4:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(TOrder.U16(data[o..]) / 65535f, TOrder.U16(data[(o + 2)..]) / 65535f,
                        TOrder.U16(data[(o + 4)..]) / 65535f, TOrder.U16(data[(o + 6)..]) / 65535f);
                break;
            case VertexElementFormat.SNorm10x3A2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = DecodeSigned1010102(TOrder.U32(data[o..]));
                break;
            case VertexElementFormat.UInt8:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(data[o], 0, 0, 0);
                break;
            case VertexElementFormat.UInt8x2:
                for (int i = 0, o = 0; i < count; i++, o += stride)
                    destination[i] = new(data[o], data[o + 1], 0, 0);
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

    private static Vector4 DecodeSigned1010102(uint value) => new(
        Math.Max(-1, ((int)(value << 22) >> 22) / 511f), Math.Max(-1, ((int)(value << 12) >> 22) / 511f),
        Math.Max(-1, ((int)(value << 2) >> 22) / 511f), Math.Max(-1, (int)value >> 30));

    private static uint Signed(float value) => (uint)SignedNorm(value, 127) & 255;

    // Rounds a value in [-1,1] to the nearest code of a signed normalized field whose largest code is maximum.
    private static int SignedNorm(float value, int maximum)
    {
        if (!float.IsFinite(value) || value < -1 || value > 1)
            throw new ArgumentOutOfRangeException(nameof(value));
        return (int)MathF.Round(value * maximum, MidpointRounding.ToEven);
    }

    private static uint Biased(float value, int steps)
    {
        if (!float.IsFinite(value) || value < -1 || value > 1)
            throw new ArgumentOutOfRangeException(nameof(value), "Normalized vertex value is outside its encoded range.");
        return (uint)MathF.Round((value - -1) / (1 - -1) * steps, MidpointRounding.ToEven);
    }

    private static byte Integer8(float value)
    {
        if (!float.IsFinite(value) || value < 0 || value > 255 || value != MathF.Truncate(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Unsigned byte components must be integers in [0,255].");
        return (byte)value;
    }

    private static short Integer16(float value)
    {
        if (!float.IsFinite(value) || value < short.MinValue || value > short.MaxValue || value != MathF.Truncate(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        return (short)value;
    }
}
