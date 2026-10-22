using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CommonFury.Binary;

/// <summary>Selects big-endian or little-endian ordering for multi-byte values.</summary>
public enum ByteOrder
{
    Big,
    Little
}

/// <summary>Reads and writes scalars in an explicit byte order, independent of the host.</summary>
public static class Endian
{
    public static ushort U16(ReadOnlySpan<byte> data, ByteOrder order) => order == ByteOrder.Big
        ? BinaryPrimitives.ReadUInt16BigEndian(data) : BinaryPrimitives.ReadUInt16LittleEndian(data);
    public static uint U32(ReadOnlySpan<byte> data, ByteOrder order) => order == ByteOrder.Big
        ? BinaryPrimitives.ReadUInt32BigEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data);
    public static float F32(ReadOnlySpan<byte> data, ByteOrder order) => BitConverter.UInt32BitsToSingle(U32(data, order));
    public static ulong U64(ReadOnlySpan<byte> data, ByteOrder order) => order == ByteOrder.Big
        ? BinaryPrimitives.ReadUInt64BigEndian(data) : BinaryPrimitives.ReadUInt64LittleEndian(data);
    public static void Put16(Span<byte> data, ushort value, ByteOrder order)
    {
        if (order == ByteOrder.Big)
            BinaryPrimitives.WriteUInt16BigEndian(data, value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(data, value);
    }
    public static void Put32(Span<byte> data, uint value, ByteOrder order)
    {
        if (order == ByteOrder.Big)
            BinaryPrimitives.WriteUInt32BigEndian(data, value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(data, value);
    }
    public static void PutFloat(Span<byte> data, float value, ByteOrder order) =>
        Put32(data, BitConverter.SingleToUInt32Bits(value), order);
    public static void Put64(Span<byte> data, ulong value, ByteOrder order)
    {
        if (order == ByteOrder.Big)
            BinaryPrimitives.WriteUInt64BigEndian(data, value);
        else
            BinaryPrimitives.WriteUInt64LittleEndian(data, value);
    }

    /// <summary>Converts contiguous 1-, 2-, 4- or 8-byte scalars; <paramref name="source"/> must hold the whole destination.</summary>
    public static void Decode<T>(ReadOnlySpan<byte> source, Span<T> destination, ByteOrder order) where T : unmanaged
    {
        Span<byte> bytes = MemoryMarshal.AsBytes(destination);
        CopyWithOrder(source[..bytes.Length], bytes, Unsafe.SizeOf<T>(), order);
    }

    /// <summary>Converts contiguous 1-, 2-, 4- or 8-byte scalars; <paramref name="destination"/> must hold the whole input.</summary>
    public static void Encode<T>(ReadOnlySpan<T> source, Span<byte> destination, ByteOrder order) where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(source);
        CopyWithOrder(bytes, destination[..bytes.Length], Unsafe.SizeOf<T>(), order);
    }

    private static void CopyWithOrder(ReadOnlySpan<byte> source, Span<byte> destination, int size, ByteOrder order)
    {
        if (size == 1 || (order == ByteOrder.Little) == BitConverter.IsLittleEndian)
            source.CopyTo(destination);
        else if (size == 2)
        {
            BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<byte, ushort>(source),
                MemoryMarshal.Cast<byte, ushort>(destination));
        }
        else if (size == 4)
        {
            BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<byte, uint>(source),
                MemoryMarshal.Cast<byte, uint>(destination));
        }
        else if (size == 8)
        {
            BinaryPrimitives.ReverseEndianness(MemoryMarshal.Cast<byte, ulong>(source),
                MemoryMarshal.Cast<byte, ulong>(destination));
        }
        else
            throw new NotSupportedException($"Unsupported scalar size: {size}");
    }
}
