using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CommonFury.Binary;

/// <summary>Reads and writes arrays of primitive scalars in an explicit byte order.</summary>
public static class BinaryArrays
{
    /// <summary>Decodes scalars into <paramref name="destination"/>. A nonzero <paramref name="stride"/> spaces input elements. Input and output must not overlap.</summary>
    public static void Read<T>(ReadOnlySpan<byte> data, Span<T> destination, ByteOrder byteOrder, int stride = 0) where T : unmanaged
    {
        int size = ScalarSize<T>();
        if (byteOrder is not ByteOrder.Big and not ByteOrder.Little)
            throw new ArgumentOutOfRangeException(nameof(byteOrder));
        if (stride == 0)
            stride = size;
        if (stride < size)
            throw new ArgumentOutOfRangeException(nameof(stride));
        int required = destination.IsEmpty ? 0 : checked((destination.Length - 1) * stride + size);
        BinaryLayout.Slice(data, 0, required);
        if (data.Overlaps(MemoryMarshal.AsBytes(destination)))
            throw new ArgumentException("Input and output must not overlap.");
        if (stride == size)
            Endian.Decode(data, destination, byteOrder);
        else
            ReadStrided(data, destination, stride, (byteOrder == ByteOrder.Little) != BitConverter.IsLittleEndian);
    }

    /// <summary>Input and output must not overlap.</summary>
    public static void Write<T>(ReadOnlySpan<T> values, Span<byte> destination, ByteOrder byteOrder) where T : unmanaged
    {
        ScalarSize<T>();
        if (byteOrder is not ByteOrder.Big and not ByteOrder.Little)
            throw new ArgumentOutOfRangeException(nameof(byteOrder));
        if (destination.Overlaps(MemoryMarshal.AsBytes(values)))
            throw new ArgumentException("Input and output must not overlap.");
        Endian.Encode(values, destination, byteOrder);
    }

    private static void ReadStrided<T>(ReadOnlySpan<byte> data, Span<T> destination, int stride, bool swap) where T : unmanaged
    {
        switch (Unsafe.SizeOf<T>())
        {
            case 1:
            {
                Span<byte> output = MemoryMarshal.AsBytes(destination);
                for (int i = 0; i < output.Length; i++)
                    output[i] = data[i * stride];
                break;
            }
            case 2:
            {
                Span<ushort> output = MemoryMarshal.Cast<T, ushort>(destination);
                for (int i = 0; i < output.Length; i++)
                {
                    ushort value = MemoryMarshal.Read<ushort>(data[(i * stride)..]);
                    output[i] = swap ? BinaryPrimitives.ReverseEndianness(value) : value;
                }
                break;
            }
            case 4:
            {
                Span<uint> output = MemoryMarshal.Cast<T, uint>(destination);
                for (int i = 0; i < output.Length; i++)
                {
                    uint value = MemoryMarshal.Read<uint>(data[(i * stride)..]);
                    output[i] = swap ? BinaryPrimitives.ReverseEndianness(value) : value;
                }
                break;
            }
            default:
            {
                Span<ulong> output = MemoryMarshal.Cast<T, ulong>(destination);
                for (int i = 0; i < output.Length; i++)
                {
                    ulong value = MemoryMarshal.Read<ulong>(data[(i * stride)..]);
                    output[i] = swap ? BinaryPrimitives.ReverseEndianness(value) : value;
                }
                break;
            }
        }
    }

    private static int ScalarSize<T>() where T : unmanaged
    {
        if (typeof(T) == typeof(byte) || typeof(T) == typeof(sbyte) || typeof(T) == typeof(short) || typeof(T) == typeof(ushort)
            || typeof(T) == typeof(int) || typeof(T) == typeof(uint) || typeof(T) == typeof(long) || typeof(T) == typeof(ulong)
            || typeof(T) == typeof(Half) || typeof(T) == typeof(float) || typeof(T) == typeof(double))
            return Unsafe.SizeOf<T>();
        throw new NotSupportedException("BinaryArrays supports numeric primitive scalars only, not aggregate structs.");
    }
}
