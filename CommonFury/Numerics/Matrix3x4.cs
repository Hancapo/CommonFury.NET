using System.Numerics;
using CommonFury.Binary;

namespace CommonFury.Numerics;

/// <summary>Twelve stored floats in three rows, with no implied axes, translation placement or multiplication convention.</summary>
public readonly record struct Matrix3x4(Vector4 Row0, Vector4 Row1, Vector4 Row2)
{
    /// <summary>Reads three rows of four floats.</summary>
    public static Matrix3x4 Read(ReadOnlySpan<byte> data, ByteOrder byteOrder = ByteOrder.Little)
    {
        var reader = new BinarySpanReader(data, byteOrder);
        return new(reader.ReadVector4(), reader.ReadVector4(), reader.ReadVector4());
    }
}
