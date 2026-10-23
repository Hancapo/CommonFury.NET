using CommonFury.Binary;

namespace CommonFury.Pso;

// Layout of the 64-bit PSO member encodings, shared by the reader and the writer.
internal static class PsoLayout
{
    // atBinaryMap: sorted flag, padding, then a 16-byte atArray of pairs at offset 8.
    internal const int MapStorageSize = 24;

    internal static bool IsPointerElement(PsoEntry entry) => entry.DataType == PsoDataType.Structure &&
        (entry.Subtype is 3 or 4 || entry.Subtype == 0 && entry.ReferenceKey == 0);

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
}
