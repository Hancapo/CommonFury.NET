using System.Collections.Frozen;
using CommonFury.Binary;

namespace CommonFury.Pso;

/// <summary>Structure, enum or opaque schema definition.</summary>
public abstract class PsoSchemaType
{
    public uint NameHash { get; }
    private protected PsoSchemaType(uint nameHash) => NameHash = nameHash;
}

/// <summary>Structure schema: instance length, fields and header values kept for rewriting.</summary>
public sealed class PsoStruct : PsoSchemaType
{
    public int Length { get; }
    /// <summary>Header byte, including binary-map pair annotations.</summary>
    public byte ReservedByte { get; }
    public uint ReservedWord { get; }
    /// <summary>Fields and anonymous descriptors, in schema order.</summary>
    public IReadOnlyList<PsoEntry> Entries { get; }
    /// <summary>Copies the entries; at most 65535.</summary>
    public PsoStruct(uint nameHash, int length, IEnumerable<PsoEntry> entries, byte reservedByte = 0, uint reservedWord = 0) : base(nameHash)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentNullException.ThrowIfNull(entries);
        PsoEntry[] fields = entries.ToArray();
        if (fields.Length > ushort.MaxValue)
            throw new ArgumentException("Too many schema entries.", nameof(entries));
        Length = length;
        Entries = Array.AsReadOnly(fields);
        ReservedByte = reservedByte;
        ReservedWord = reservedWord;
    }
}

/// <summary>Enum schema stored as compact (8-byte) or wide (16-byte) records.</summary>
public sealed class PsoEnum : PsoSchemaType
{
    public byte ReservedByte { get; }
    /// <summary>True for 16-byte hash/reserved/Int64 records; false for 8-byte hash/Int32 records.</summary>
    public bool IsWide { get; }
    /// <summary>Members in source order, including aliases.</summary>
    public IReadOnlyList<PsoEnumEntry> Entries { get; }
    /// <summary>Copies the entries; at most 65535. Compact records reject values outside Int32 and nonzero reserved words.</summary>
    public PsoEnum(uint nameHash, IEnumerable<PsoEnumEntry> entries, byte reservedByte = 0, bool isWide = false) : base(nameHash)
    {
        ArgumentNullException.ThrowIfNull(entries);
        PsoEnumEntry[] values = entries.ToArray();
        if (values.Length > ushort.MaxValue)
            throw new ArgumentException("Too many enum entries.", nameof(entries));
        if (!isWide && values.Any(value => value.Value is < int.MinValue or > int.MaxValue || value.Reserved != 0))
            throw new ArgumentException("Compact enum records hold only Int32 values without a reserved word.", nameof(entries));
        Entries = Array.AsReadOnly(values);
        ReservedByte = reservedByte;
        IsWide = isWide;
    }
}

/// <summary>Unrecognized schema kind retained for inspection. Its offsets are unknown, so schema relocation is rejected.</summary>
public sealed class PsoOpaqueSchema : PsoSchemaType
{
    private readonly byte[] data;
    /// <summary>Definition bytes up to the next definition or the end of the schema.</summary>
    public ReadOnlySpan<byte> Data => this.data;
    internal PsoOpaqueSchema(uint nameHash, ReadOnlySpan<byte> data) : base(nameHash) => this.data = data.ToArray();
}

/// <summary>Set of PSO type definitions with unique name hashes.</summary>
public sealed class PsoSchema
{
    public IReadOnlyList<PsoSchemaType> Types { get; }
    public FrozenDictionary<uint, PsoSchemaType> ByHash { get; }
    /// <exception cref="ArgumentException">A definition is null or two share a name hash.</exception>
    public PsoSchema(IEnumerable<PsoSchemaType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        PsoSchemaType[] items = types.ToArray();
        if (items.Any(item => item is null))
            throw new ArgumentException("Schema types cannot be null.", nameof(types));
        if (items.Select(item => item.NameHash).Distinct().Count() != items.Length)
            throw new ArgumentException("Duplicate schema hashes.", nameof(types));
        Types = Array.AsReadOnly(items);
        ByHash = items.ToFrozenDictionary(item => item.NameHash);
    }

    /// <summary>Decodes a big-endian PSCH section. Enum records are wide when the definition is exactly 4 + 16 × count bytes
    /// long, otherwise compact. <paramref name="allowAliasedDefinitions"/> accepts distinct type hashes that share one
    /// definition offset; each then receives its own copy of the definition.</summary>
    public static PsoSchema Read(ReadOnlySpan<byte> data, bool allowAliasedDefinitions = false)
    {
        var reader = new BinarySpanReader(data, ByteOrder.Big);
        BinaryLayout.Require(reader.ReadUInt32() == PsoSectionId.Psch, "Expected PSCH.");
        BinaryLayout.Require(reader.ReadUInt32() == data.Length, "PSCH length mismatch.");
        uint count = reader.ReadUInt32();
        BinaryLayout.Require(count <= reader.Remaining / 8, "Truncated PSCH index.");
        var indexes = new (uint Hash, int Offset)[(int)count];
        var hashes = new HashSet<uint>();
        for (int i = 0; i < indexes.Length; i++)
        {
            indexes[i] = (reader.ReadUInt32(), reader.ReadInt32());
            BinaryLayout.Require(hashes.Add(indexes[i].Hash), "Duplicate PSCH type hash.");
            BinaryLayout.Require(indexes[i].Offset >= 12 + indexes.Length * 8 && indexes[i].Offset < data.Length, "PSCH type offset outside schema payload.");
        }
        int[] sortedOffsets = indexes.Select(x => x.Offset).Distinct().Order().ToArray();
        BinaryLayout.Require(allowAliasedDefinitions || sortedOffsets.Length == indexes.Length, "Aliased PSCH type offsets are unsupported.");
        var limits = new Dictionary<int, int>();
        for (int i = 0; i < sortedOffsets.Length; i++)
            limits[sortedOffsets[i]] = i + 1 < sortedOffsets.Length ? sortedOffsets[i + 1] : data.Length;
        var types = new List<PsoSchemaType>(indexes.Length);
        foreach (var (hash, offset) in indexes)
        {
            ReadOnlySpan<byte> definition = data[offset..limits[offset]];
            var item = new BinarySpanReader(definition, ByteOrder.Big);
            byte kind = item.ReadByte();
            if (kind is not 0 and not 1)
            { types.Add(new PsoOpaqueSchema(hash, definition)); continue; }
            byte reserved = item.ReadByte();
            ushort entryCount = item.ReadUInt16();
            if (kind == 0)
            {
                int length = item.ReadInt32();
                BinaryLayout.Require(length >= 0, "Negative PSCH structure length.");
                uint reservedWord = item.ReadUInt32();
                BinaryLayout.Require(entryCount <= item.Remaining / 12, "Truncated PSCH fields.");
                var entries = new PsoEntry[entryCount];
                for (int i = 0; i < entries.Length; i++)
                    entries[i] = new(item.ReadUInt32(), (PsoDataType)item.ReadByte(), item.ReadByte(), item.ReadUInt16(), item.ReadUInt32());
                types.Add(new PsoStruct(hash, length, entries, reserved, reservedWord));
            }
            else
            {
                // Distinguish record widths by the exact definition extent, never by guessed enum values.
                bool wide = entryCount != 0 && definition.Length == 4 + entryCount * 16;
                BinaryLayout.Require(entryCount <= item.Remaining / (wide ? 16 : 8), "Truncated PSCH enum values.");
                var entries = new PsoEnumEntry[entryCount];
                for (int i = 0; i < entries.Length; i++)
                {
                    uint name = item.ReadUInt32();
                    if (wide)
                    {
                        uint reservedWord = item.ReadUInt32();
                        entries[i] = new(name, item.ReadInt64(), reservedWord);
                    }
                    else
                        entries[i] = new(name, item.ReadInt32());
                }
                types.Add(new PsoEnum(hash, entries, reserved, wide));
            }
        }
        return new(types);
    }

    /// <summary>Canonical encoding without the original gaps or padding; <see cref="PsoFile.Write()"/> keeps those.</summary>
    public byte[] Write()
    {
        int header = checked(12 + Types.Count * 8), size = header;
        foreach (PsoSchemaType type in Types)
            size = checked(size + EncodedSize(type));
        byte[] bytes = new byte[size];
        var writer = new BinarySpanWriter(bytes, ByteOrder.Big);
        writer.WriteUInt32(PsoSectionId.Psch);
        writer.WriteUInt32((uint)size);
        writer.WriteUInt32((uint)Types.Count);
        int offset = header;
        foreach (PsoSchemaType type in Types)
        {
            writer.WriteUInt32(type.NameHash);
            writer.WriteInt32(offset);
            offset += EncodedSize(type);
        }
        foreach (PsoSchemaType type in Types)
        {
            if (type is PsoStruct structure)
            {
                writer.WriteByte(0);
                writer.WriteByte(structure.ReservedByte);
                writer.WriteUInt16((ushort)structure.Entries.Count);
                writer.WriteInt32(structure.Length);
                writer.WriteUInt32(structure.ReservedWord);
                foreach (PsoEntry entry in structure.Entries)
                {
                    writer.WriteUInt32(entry.NameHash);
                    writer.WriteByte((byte)entry.DataType);
                    writer.WriteByte(entry.Subtype);
                    writer.WriteUInt16(entry.DataOffset);
                    writer.WriteUInt32(entry.ReferenceKey);
                }
            }
            else if (type is PsoEnum enumeration)
            {
                writer.WriteByte(1);
                writer.WriteByte(enumeration.ReservedByte);
                writer.WriteUInt16((ushort)enumeration.Entries.Count);
                foreach (PsoEnumEntry entry in enumeration.Entries)
                {
                    writer.WriteUInt32(entry.NameHash);
                    if (enumeration.IsWide)
                    {
                        writer.WriteUInt32(entry.Reserved);
                        writer.WriteInt64(entry.Value);
                    }
                    else
                        writer.WriteInt32((int)entry.Value);
                }
            }
        }
        return bytes;
    }

    /// <summary>Returns the members of an Enum or Flags field's referenced enum whose value equals the field value, in
    /// declaration order and including aliases. A 32-bit field also matches its unsigned bit pattern, because wide records
    /// can store an unsigned constant that the field exposes as Int32. Flag combinations are not inferred.</summary>
    public IReadOnlyList<PsoEnumEntry> FindEnumMembers(PsoEntry field, long value)
    {
        if (field.DataType is not (PsoDataType.Enum or PsoDataType.Flags) || ByHash.GetValueOrDefault(field.ReferenceKey) is not PsoEnum enumeration)
            return [];
        long pattern = value is >= int.MinValue and <= int.MaxValue ? unchecked((uint)(int)value) : value;
        return Array.AsReadOnly(enumeration.Entries.Where(entry => entry.Value == value || entry.Value == pattern).ToArray());
    }

    /// <summary>Returns the bytes one value of <paramref name="entry"/> occupies, as a field or as an array element described
    /// by an ARRAYINFO entry; structure pointers take 8. Returns zero when the size is not fixed by the entry and this schema:
    /// arrays, maps, unknown subtypes, and inline structures this schema does not define.</summary>
    public int ElementStride(PsoEntry entry) => entry.DataType switch
    {
        PsoDataType.Structure when PsoLayout.IsPointerElement(entry) => 8,
        PsoDataType.Structure when entry.Subtype == 0 => ByHash.GetValueOrDefault(entry.ReferenceKey) is PsoStruct s ? s.Length : 0,
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

    /// <summary>Reads the element count of an array field from its owning structure's bytes, without checking the element
    /// storage. Fixed arrays (subtype 1) return their stored live count, other inline arrays their capacity, and pointer arrays
    /// the count in their header or counter member; a null fixed-length pointer array (subtype 3) counts zero.</summary>
    /// <param name="array">Array field of the structure.</param>
    /// <param name="element">ARRAYINFO entry that precedes <paramref name="array"/>.</param>
    /// <param name="structure">The owning structure's bytes, starting at its first byte.</param>
    /// <exception cref="ArgumentException"><paramref name="array"/> is not an array field.</exception>
    /// <exception cref="NotSupportedException">The array subtype is unknown, or a fixed array's element size is not fixed.</exception>
    /// <exception cref="InvalidDataException">The count lies outside <paramref name="structure"/>, exceeds the capacity of a
    /// fixed array, or exceeds <see cref="int.MaxValue"/>.</exception>
    public int ReadArrayCount(PsoEntry array, PsoEntry element, ReadOnlySpan<byte> structure)
    {
        if (array.DataType != PsoDataType.Array)
            throw new ArgumentException("Expected a PSO array field.", nameof(array));
        BinaryLayout.Require(array.DataOffset <= structure.Length, "PSO array field starts outside its structure.");
        ReadOnlySpan<byte> field = structure[array.DataOffset..];
        // The schema's count word is a capacity for fixed arrays, but a byte offset to the count member for subtypes 6–8.
        int capacity = (int)(array.ReferenceKey >> 16);
        switch (array.Subtype)
        {
            case 0:
                return PsoArrayHeader.Read(field).Count;
            case 1:
                int stride = ElementStride(element);
                if (stride <= 0 && capacity != 0)
                    throw new NotSupportedException("PSO array element size is unknown.");
                int count = new BinarySpanReader(BinaryLayout.Slice(field, PsoLayout.FixedArrayCountOffset(array, stride), 4), ByteOrder.Big).ReadInt32();
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
                int width = PsoLayout.ExternalCounterWidth(array);
                return ReadCount(BinaryLayout.Slice(structure, capacity, width), width);
            default:
                throw new NotSupportedException($"Unsupported PSO array subtype {array.Subtype}.");
        }
    }

    private static int ReadCount(ReadOnlySpan<byte> data, int width)
    {
        var reader = new BinarySpanReader(data, ByteOrder.Big);
        uint count = width switch { 1 => reader.ReadByte(), 2 => reader.ReadUInt16(), _ => reader.ReadUInt32() };
        BinaryLayout.Require(count <= int.MaxValue, "PSO array count exceeds the supported range.");
        return (int)count;
    }

    private static int EncodedSize(PsoSchemaType type) => type switch
    {
        PsoStruct s => checked(12 + s.Entries.Count * 12),
        PsoEnum e => checked(4 + e.Entries.Count * (e.IsWide ? 16 : 8)),
        _ => throw new NotSupportedException("Cannot relocate an unknown schema kind; preserve the original file instead.")
    };
}
