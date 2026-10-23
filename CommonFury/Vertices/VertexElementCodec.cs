using System.Numerics;
using CommonFury.Binary;
using CommonFury.Numerics;

namespace CommonFury.Vertices;

/// <summary>Decodes and encodes one vertex attribute value. Missing lanes decode as zero; encoding ignores undeclared lanes.</summary>
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
                if (!VectorMath.IsFinite(value) || Vector4.Min(value, Vector4.Zero) != Vector4.Zero || Vector4.Max(value, new(255)) != new Vector4(255)
                    || value.X != MathF.Truncate(value.X) || value.Y != MathF.Truncate(value.Y) || value.Z != MathF.Truncate(value.Z) || value.W != MathF.Truncate(value.W))
                    throw new ArgumentOutOfRangeException(nameof(value), "Unsigned byte components must be integers in [0,255].");
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
