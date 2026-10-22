using CommonFury.Binary;

namespace CommonFury.Pso;

// Layout of the 64-bit PSO member encodings, shared by the reader and the writer.
internal static class PsoLayout
{
    // atBinaryMap: sorted flag, padding, then a 16-byte atArray of pairs at offset 8.
    internal const int MapStorageSize = 24;

    internal static bool IsInlineArray(PsoEntry entry) => entry.Subtype is 1 or 2 or 4;

    internal static bool IsPointerElement(PsoEntry entry) => entry.DataType == PsoDataType.Structure &&
        (entry.Subtype is 3 or 4 || entry.Subtype == 0 && entry.ReferenceKey == 0);

    internal static int ElementStride(PsoSchema schema, PsoEntry entry) => entry.DataType switch
    {
        PsoDataType.Structure when IsPointerElement(entry) => 8,
        PsoDataType.Structure when entry.Subtype == 0 => schema.ByHash.GetValueOrDefault(entry.ReferenceKey) is PsoStruct s ? s.Length : 0,
        PsoDataType.Bool or PsoDataType.Byte or PsoDataType.SByte => 1,
        PsoDataType.Int16 or PsoDataType.UInt16 or PsoDataType.Half => 2,
        PsoDataType.Int32 or PsoDataType.UInt32 or PsoDataType.Enum or PsoDataType.Flags or PsoDataType.Float => 4,
        PsoDataType.Int64 or PsoDataType.Float2 => 8,
        // Wire type 9 (Vector3) and wire type 20 (Vec3V) both occupy 16 bytes.
        PsoDataType.Float3 or PsoDataType.Float4 or PsoDataType.Float3Aligned or PsoDataType.Float4Aligned => 16,
        PsoDataType.String when entry.Subtype is 7 or 8 => 4,
        PsoDataType.String when entry.Subtype is 1 or 2 => 8,
        PsoDataType.String when entry.Subtype == 3 => 16,
        PsoDataType.String when entry.Subtype == 0 => (int)(entry.ReferenceKey >> 16),
        _ => 0
    };

    internal static int FixedArrayCountOffset(PsoEntry entry, int stride) =>
        BinaryLayout.Align(checked((int)(entry.ReferenceKey >> 16) * stride), 4);

    internal static int ArrayStorageSize(PsoEntry entry, int stride) => entry.Subtype switch
    {
        0 or 5 => 16,
        1 => checked(FixedArrayCountOffset(entry, stride) + 4),
        2 or 4 => checked((int)(entry.ReferenceKey >> 16) * stride),
        3 or 6 or 7 or 8 => 8,
        _ => throw new NotSupportedException($"Unsupported PSO array subtype {entry.Subtype}.")
    };

    internal static int ExternalCounterWidth(PsoEntry entry) => entry.Subtype switch
    {
        6 => 4,
        7 => 1,
        8 => 2,
        _ => 0
    };

    internal static int ArrayCount(PsoEntry entry, int stride, ReadOnlySpan<byte> structure, ReadOnlySpan<byte> field, PsoPointerLayout layout)
    {
        // The schema's count word is a capacity for fixed arrays, but a byte offset to the count member for subtypes 6–8.
        int capacity = (int)(entry.ReferenceKey >> 16);
        switch (entry.Subtype)
        {
            case 0:
                return PsoArrayHeader.Read(field, layout).Count;
            case 1:
                int count = new BinarySpanReader(BinaryLayout.Slice(field, FixedArrayCountOffset(entry, stride), 4), ByteOrder.Big).ReadInt32();
                BinaryLayout.Require(count >= 0 && count <= capacity, "PSO fixed array count exceeds its capacity.");
                return count;
            case 2:
            case 4:
                return capacity;
            case 3:
                return new BinarySpanReader(field, ByteOrder.Big).ReadUInt32() == 0 ? 0 : capacity;
            case 5:
                return ReadCount(BinaryLayout.Slice(field, 8, 4), 4);
            case 6:
            case 7:
            case 8:
                int width = ExternalCounterWidth(entry);
                return ReadCount(BinaryLayout.Slice(structure, capacity, width), width);
            default:
                throw new NotSupportedException($"Unsupported PSO array subtype {entry.Subtype}.");
        }
    }

    private static int ReadCount(ReadOnlySpan<byte> data, int width)
    {
        var reader = new BinarySpanReader(data, ByteOrder.Big);
        uint count = width switch { 1 => reader.ReadByte(), 2 => reader.ReadUInt16(), _ => reader.ReadUInt32() };
        BinaryLayout.Require(count <= int.MaxValue, "PSO array count exceeds the supported range.");
        return (int)count;
    }
}
