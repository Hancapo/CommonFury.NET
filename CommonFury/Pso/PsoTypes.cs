using CommonFury.Binary;

namespace CommonFury.Pso;

/// <summary>Wire data type of a PSO schema field.</summary>
public enum PsoDataType : byte
{
    Bool = 0,
    SByte = 1,
    Byte = 2,
    Int16 = 3,
    UInt16 = 4,
    Int32 = 5,
    UInt32 = 6,
    Float = 7,
    Float2 = 8,
    /// <summary>Three floats; occupies 16 bytes.</summary>
    Float3 = 9,
    Float4 = 10,
    /// <summary>Text or hashed string, selected by subtype.</summary>
    String = 11,
    /// <summary>Inline or indirect structure, selected by subtype.</summary>
    Structure = 12,
    /// <summary>Inline or indirect array, selected by subtype; the preceding anonymous entry describes its elements.</summary>
    Array = 13,
    /// <summary>Signed 32-bit enumeration value.</summary>
    Enum = 14,
    /// <summary>32-bit flags; the bit pattern is kept as is.</summary>
    Flags = 15,
    /// <summary>Map; binary-map subtype 1 stores a sorted flag and an array of key/item pairs.</summary>
    Map = 16,
    /// <summary>16-byte aligned three-component vector (Vec3V).</summary>
    Float3Aligned = 20,
    /// <summary>16-byte aligned four-component vector (Vec4V).</summary>
    Float4Aligned = 21,
    Half = 30,
    Int64 = 32
}

/// <summary>Four-character identifiers of the PSO sections.</summary>
public static class PsoSectionId
{
    /// <summary>Structure data blocks.</summary>
    public const uint Psin = 0x5053494E;
    /// <summary>Block table.</summary>
    public const uint Pmap = 0x504D4150;
    /// <summary>Structure and enum schemas.</summary>
    public const uint Psch = 0x50534348;
    /// <summary>Schema signatures.</summary>
    public const uint Psig = 0x50534947;
    /// <summary>Encrypted debug strings.</summary>
    public const uint Stre = 0x53545245;
    /// <summary>Plaintext hash-name table of final builds.</summary>
    public const uint Strf = 0x53545246;
    /// <summary>Plaintext hash-name table of debug builds.</summary>
    public const uint Strs = 0x53545253;
    /// <summary>Terminal file size and checksum.</summary>
    public const uint Chks = 0x43484B53;
}

/// <summary>Number of low pointer bits that hold the block ID; also fixes the PMAP header size.</summary>
public enum PsoPointerLayout
{
    /// <summary>12-bit block ID, 20-bit offset, 16-byte PMAP header.</summary>
    BlockId12 = 12,
    /// <summary>20-bit block ID, 12-bit offset, 24-byte PMAP header. Offsets in blocks larger than 4 KiB wrap per page;
    /// the reader reconstructs them from traversal order.</summary>
    BlockId20 = 20
}

/// <summary>One-based block ID (zero is null) and byte offset within that block. Offsets reconstructed from wrapped pages
/// may exceed what the packed form can encode.</summary>
public readonly record struct PsoPointer
{
    public int BlockId { get; }
    public int Offset { get; }
    public bool IsNull => BlockId == 0;
    /// <summary>Accepts any nonnegative values; <see cref="Encode"/> checks the packed range.</summary>
    public PsoPointer(int blockId, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        BlockId = blockId;
        Offset = offset;
    }
    /// <exception cref="ArgumentOutOfRangeException">The block ID or offset does not fit <paramref name="layout"/>.</exception>
    public uint Encode(PsoPointerLayout layout = PsoPointerLayout.BlockId12)
    {
        int bits = Bits(layout);
        if ((uint)BlockId >= 1u << bits)
            throw new ArgumentOutOfRangeException(nameof(BlockId), "Block ID does not fit the pointer layout.");
        if ((uint)Offset >= 1u << (32 - bits))
            throw new ArgumentOutOfRangeException(nameof(Offset), "Offset does not fit the pointer layout.");
        return ((uint)Offset << bits) | (uint)BlockId;
    }
    public static PsoPointer Decode(uint word, PsoPointerLayout layout = PsoPointerLayout.BlockId12)
    {
        int bits = Bits(layout);
        return new((int)(word & ((1u << bits) - 1)), (int)(word >> bits));
    }
    internal static int Bits(PsoPointerLayout layout) => layout switch
    {
        PsoPointerLayout.BlockId12 => 12,
        PsoPointerLayout.BlockId20 => 20,
        _ => throw new ArgumentOutOfRangeException(nameof(layout))
    };
}

/// <summary>PSO array descriptor: element pointer and count.</summary>
public readonly record struct PsoArrayHeader(PsoPointer Pointer, ushort Count)
{
    /// <summary>Reads the big-endian descriptor.</summary>
    public static PsoArrayHeader Read(ReadOnlySpan<byte> data, PsoPointerLayout layout = PsoPointerLayout.BlockId12)
    {
        var reader = new BinarySpanReader(data, ByteOrder.Big);
        var pointer = PsoPointer.Decode(reader.ReadUInt32(), layout);
        reader.ReadUInt32();
        return new(pointer, reader.ReadUInt16());
    }
}

/// <summary>PMAP entry: a block's type hash, file offset, length and reserved word.</summary>
public readonly record struct PsoBlock(uint NameHash, int Offset, int Length, int Reserved = 0);
/// <summary>Schema field: name hash, type, subtype, offset within the structure and referenced schema key.</summary>
public readonly record struct PsoEntry(uint NameHash, PsoDataType DataType, byte Subtype, ushort DataOffset, uint ReferenceKey)
{
    /// <summary>Name hash of the anonymous entry that describes the following array's elements.</summary>
    public const uint ArrayInfoHash = 0x100;
    /// <summary>True for anonymous array-element and map key/item descriptors (name hashes 0x100–0xCFF).</summary>
    public bool IsAnonymous => NameHash is >= 256 and < 3328;
    /// <summary>True for array fields whose elements are stored inside the owning structure at <see cref="DataOffset"/>
    /// (subtypes 1, 2 and 4); other arrays store a pointer there.</summary>
    public bool IsInlineArray => DataType == PsoDataType.Array && Subtype is 1 or 2 or 4;
}
/// <summary>Enum member. Wide (16-byte) records hold a 64-bit value and a reserved word; compact (8-byte) records hold a
/// 32-bit value and no reserved word.</summary>
public readonly record struct PsoEnumEntry(uint NameHash, long Value, uint Reserved = 0);

/// <summary>Owns a complete encoded section, including its eight-byte header.</summary>
public sealed class PsoSection
{
    private readonly ArraySegment<byte> data;
    /// <summary>Four-character identifier, read big-endian.</summary>
    public uint Identifier { get; }
    public ReadOnlySpan<byte> Data => this.data;
    /// <summary>Copies the section; its header length must match <paramref name="data"/>.</summary>
    public PsoSection(ReadOnlySpan<byte> data)
    {
        Identifier = ReadIdentifier(data);
        this.data = data.ToArray();
    }

    // Takes ownership of a range that nothing else retains or changes.
    internal PsoSection(ArraySegment<byte> data)
    {
        Identifier = ReadIdentifier(data);
        this.data = data;
    }

    private static uint ReadIdentifier(ReadOnlySpan<byte> data)
    {
        var reader = new BinarySpanReader(data, ByteOrder.Big);
        uint identifier = reader.ReadUInt32();
        BinaryLayout.Require(reader.ReadUInt32() == data.Length && data.Length >= 8, "PSO section size does not match its header.");
        return identifier;
    }
}

/// <summary>Diagnostic codes reported by PSO reading and checksum validation.</summary>
public static class PsoDiagnosticCodes
{
    /// <summary>CHKS is not a single terminal 20-byte section.</summary>
    public const string ChecksumLayout = "pso.checksum.layout";
    public const string ChecksumMismatch = "pso.checksum.mismatch";
    public const string ChecksumMissing = "pso.checksum.missing";
    /// <summary>The file size recorded in CHKS does not match.</summary>
    public const string ChecksumSize = "pso.checksum.size";
    /// <summary>A field could not be decoded and was isolated (<see cref="PsoReadOptions.ContinueOnFieldErrors"/>).</summary>
    public const string FieldDecode = "pso.field.decode";
    /// <summary>The field's type or layout is not supported by the generic reader.</summary>
    public const string FieldUnsupported = "pso.field.unsupported";
    /// <summary>A pointer targets a block whose type has no structure schema.</summary>
    public const string PointerUnknownType = "pso.pointer.unknown_type";
    public const string SchemaMissing = "pso.schema.missing";
}
