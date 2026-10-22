using System.Runtime.CompilerServices;

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
        if (data.Overlaps(System.Runtime.InteropServices.MemoryMarshal.AsBytes(destination)))
            throw new ArgumentException("Input and output must not overlap.");
        if (stride == size)
            Endian.Decode(data, destination, byteOrder);
        else
            for (int i = 0; i < destination.Length; i++)
                Endian.Decode(data.Slice(i * stride, size), destination.Slice(i, 1), byteOrder);
    }

    /// <summary>Input and output must not overlap.</summary>
    public static void Write<T>(ReadOnlySpan<T> values, Span<byte> destination, ByteOrder byteOrder) where T : unmanaged
    {
        ScalarSize<T>();
        if (byteOrder is not ByteOrder.Big and not ByteOrder.Little)
            throw new ArgumentOutOfRangeException(nameof(byteOrder));
        if (destination.Overlaps(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values)))
            throw new ArgumentException("Input and output must not overlap.");
        Endian.Encode(values, destination, byteOrder);
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
