using CommonFury.Binary;
using CommonFury.Hashing;

namespace CommonFury.Pso;

internal sealed class PsoReader(PsoFile file, PsoReadOptions options)
{
    private readonly Dictionary<(uint Type, PsoPointer Pointer), PsoNode> nodes = [];
    private readonly List<Diagnostic> diagnostics = new();
    private readonly bool wide = file.PointerLayout == PsoPointerLayout.BlockId20;
    private int arrayElements, textBytes;

    public PsoDocument Read()
    {
        PsoBlock root = file.Block(file.RootBlockId);
        return new(file, Structure(root.NameHash, new(file.RootBlockId, 0), 0, true, null), new ValidationReport(diagnostics));
    }

    private PsoNode Structure(uint hash, PsoPointer pointer, int depth, bool reference, PageState? pages)
    {
        if (depth > options.MaximumDepth)
            throw new InvalidDataException("PSO nesting exceeds the configured limit.");
        if (nodes.TryGetValue((hash, pointer), out PsoNode? cached))
        {
            BinaryLayout.Require(reference || cached.IsComplete, "Recursive inline PSO structure.");
            return cached;
        }
        if (nodes.Count >= options.MaximumNodes)
            throw new InvalidDataException("PSO node count exceeds the configured limit.");
        var node = new PsoNode(hash, pointer);
        if (!file.Schema.ByHash.TryGetValue(hash, out PsoSchemaType? type) || type is not PsoStruct schema)
        {
            nodes.Add((hash, pointer), node);
            diagnostics.Add(new Diagnostic(PsoDiagnosticCodes.SchemaMissing, $"No structure schema for 0x{hash:X8}."));
            node.CompleteWith([]);
            return node;
        }
        ReadOnlySpan<byte> data = file.Slice(pointer, schema.Length);
        nodes.Add((hash, pointer), node);
        IReadOnlyList<PsoEntry> entries = schema.Entries;
        int fieldCount = 0;
        for (int i = 0; i < entries.Count; i++)
            if (!entries[i].IsAnonymous)
                fieldCount++;
        // Every non-anonymous entry yields exactly one field, so the array is filled completely.
        var fields = new PsoField[fieldCount];
        int next = 0;
        PsoEntry? arrayInfo = null;
        for (int i = 0; i < entries.Count; i++)
        {
            PsoEntry entry = entries[i];
            if (entry.IsAnonymous)
            { arrayInfo = entry; continue; }
            PsoValue value;
            if (!options.ContinueOnFieldErrors)
                value = Field(schema, entry, arrayInfo, pointer, data, depth, pages);
            else
            {
                try
                {
                    value = Field(schema, entry, arrayInfo, pointer, data, depth, pages);
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException)
                {
                    diagnostics.Add(new Diagnostic(PsoDiagnosticCodes.FieldDecode, error.Message,
                        $"Blocks[{pointer.BlockId}]+0x{pointer.Offset + entry.DataOffset:X}/hash_{entry.NameHash:X8}"));
                    value = new PsoMalformed(entry, error.Message);
                }
            }
            fields[next++] = new(entry, value);
        }
        node.CompleteWith(fields);
        return node;
    }

    private PsoValue Field(PsoStruct schema, PsoEntry entry, PsoEntry? arrayInfo, PsoPointer pointer, ReadOnlySpan<byte> data, int depth, PageState? pages)
    {
        BinaryLayout.Require(entry.DataOffset <= data.Length, "PSO field starts outside its structure.");
        ReadOnlySpan<byte> field = data[entry.DataOffset..];
        switch (entry.DataType)
        {
            case PsoDataType.Structure when entry.Subtype == 0:
                BinaryLayout.Require(file.Schema.ByHash.TryGetValue(entry.ReferenceKey, out PsoSchemaType? nested) && nested is PsoStruct,
                    "Inline structure has no known size.");
                BinaryLayout.Require(((PsoStruct)nested!).Length <= field.Length, "Inline structure exceeds its parent.");
                // Inline structures start a traversal without page history.
                return new PsoNodeValue(Structure(entry.ReferenceKey, Add(pointer, entry.DataOffset), depth + 1, false, null));
            case PsoDataType.Structure when entry.Subtype is 3 or 4:
                return PointerTarget(Unwrap(ReadPointer(field), pages), depth + 1, pages);
            case PsoDataType.Array:
                return Array(entry, arrayInfo, pointer, data, field, depth + 1, pages);
            case PsoDataType.Map when entry.Subtype == 1:
                return Map(schema, entry, field, depth + 1);
            case PsoDataType.String:
                return String(entry, field, pages);
            default:
                return Scalar(entry, field);
        }
    }

    private PsoValue PointerTarget(PsoPointer pointer, int depth, PageState? pages)
    {
        if (pointer.IsNull)
            return PsoNull.Instance;
        PsoBlock block = file.Block(pointer.BlockId);
        file.Slice(pointer, 0);
        if (block.NameHash == 1)
            return Text(pointer);
        if (file.Schema.ByHash.GetValueOrDefault(block.NameHash) is not PsoStruct)
        {
            diagnostics.Add(new Diagnostic(PsoDiagnosticCodes.PointerUnknownType, $"Pointer target 0x{block.NameHash:X8} has no structure schema."));
            return new PsoUnresolvedPointer(pointer, block.NameHash);
        }
        return new PsoNodeValue(Structure(block.NameHash, pointer, depth, true, pages));
    }

    private PsoPointer ReadPointer(ReadOnlySpan<byte> data)
    {
        var reader = new BinarySpanReader(data, ByteOrder.Big);
        return PsoPointer.Decode(reader.ReadUInt32(), file.PointerLayout);
    }

    // 20-bit pointers keep only 12 offset bits. Within one traversal, an offset lower than the previous one into the same
    // block (longer than one page) moves to the next 4 KiB page. Blocks no longer than a page are never adjusted.
    private PsoPointer Unwrap(PsoPointer pointer, PageState? pages)
    {
        if (pages is null || !wide || pointer.IsNull || file.Block(pointer.BlockId).Length <= 0x1000)
            return pointer;
        var (page, previous) = pages.GetValueOrDefault(pointer.BlockId, (0, -1));
        if (previous >= 0 && pointer.Offset < previous)
            page += 0x1000;
        pages[pointer.BlockId] = (page, pointer.Offset);
        return new(pointer.BlockId, checked(page + pointer.Offset));
    }

    private PsoPointer Add(PsoPointer pointer, int offset)
    {
        long next = (long)pointer.Offset + offset;
        BinaryLayout.Require(next >= 0 && (wide ? next <= int.MaxValue : next <= 0xFFFFF), "PSO relative offset exceeds the pointer layout.");
        return new(pointer.BlockId, (int)next);
    }

    private PsoText Text(PsoPointer pointer, int? limit = null)
    {
        if (pointer.IsNull)
            return new([]);
        PsoBlock block = file.Block(pointer.BlockId);
        ReadOnlySpan<byte> bytes = file.Slice(pointer, limit ?? (block.Length - pointer.Offset));
        return Text(bytes);
    }
    // Pointer text runs to the end of its block when unterminated, and many pointers may share one block, so the budget
    // bounds the terminator search as well as the copy.
    private PsoText Text(ReadOnlySpan<byte> bytes)
    {
        int remaining = options.MaximumTextBytes - textBytes;
        int window = bytes.Length > remaining ? remaining + 1 : bytes.Length;
        int length = bytes[..window].IndexOf((byte)0);
        if (length < 0)
            length = window;
        if (length > remaining)
            throw new InvalidDataException("PSO text exceeds the configured limit.");
        textBytes += length;
        return new(bytes[..length]);
    }

    private PsoValue String(PsoEntry entry, ReadOnlySpan<byte> data, PageState? pages)
    {
        switch (entry.Subtype)
        {
            case 0:
                return Text(BinaryLayout.Slice(data, 0, (int)(entry.ReferenceKey >> 16)));
            case 1:
            case 2:
                return Text(Unwrap(ReadPointer(data), pages));
            case 3:
                PsoArrayHeader header = PsoArrayHeader.Read(data, file.PointerLayout);
                // The atString length excludes the NUL; lengths that include it are also accepted without dropping the
                // final character.
                return Text(Unwrap(header.Pointer, pages), header.Count);
            case 7:
            case 8:
                var reader = new BinarySpanReader(data, ByteOrder.Big);
                return new PsoHash(new Hash32(reader.ReadUInt32()));
            default:
                return Unsupported(entry, "Unknown string subtype.");
        }
    }

    private PsoValue Scalar(PsoEntry entry, ReadOnlySpan<byte> data)
    {
        var reader = new BinarySpanReader(data, ByteOrder.Big);
        return entry.DataType switch
        {
            PsoDataType.Bool => new PsoBoolean(reader.ReadByte() != 0),
            PsoDataType.SByte => new PsoInteger(reader.ReadSByte()),
            PsoDataType.Byte => new PsoInteger(reader.ReadByte()),
            PsoDataType.Int16 => new PsoInteger(reader.ReadInt16()),
            PsoDataType.UInt16 => new PsoInteger(reader.ReadUInt16()),
            PsoDataType.Int32 or PsoDataType.Enum or PsoDataType.Flags => new PsoInteger(reader.ReadInt32()),
            PsoDataType.UInt32 => new PsoInteger(reader.ReadUInt32()),
            PsoDataType.Int64 => new PsoInteger(reader.ReadInt64()),
            PsoDataType.Float => new PsoFloat(reader.ReadSingle()),
            PsoDataType.Half => new PsoHalf(reader.ReadUInt16()),
            PsoDataType.Float2 => new PsoVector2(reader.ReadVector2()),
            PsoDataType.Float3 or PsoDataType.Float3Aligned => new PsoVector3(reader.ReadVector3()),
            PsoDataType.Float4 or PsoDataType.Float4Aligned => new PsoVector4(reader.ReadVector4()),
            _ => Unsupported(entry, "Unsupported field type or structure subtype.")
        };
    }

    private PsoValue Array(PsoEntry entry, PsoEntry? info, PsoPointer owner, ReadOnlySpan<byte> structure, ReadOnlySpan<byte> field, int depth,
        PageState? ownerPages)
    {
        if (info is not PsoEntry element)
            return Unsupported(entry, "Array has no preceding ARRAYINFO descriptor.");
        if (entry.Subtype > 8)
            return Unsupported(entry, "Unknown array subtype.");
        bool inline = entry.IsInlineArray;
        int stride = file.Schema.ElementStride(element);
        if (inline && stride <= 0 && entry.ReferenceKey >> 16 != 0)
            return Unsupported(entry, "Array element layout is not supported.");
        BinaryLayout.Require(PsoLayout.ArrayStorageSize(entry, stride) <= field.Length, "PSO array storage exceeds its parent.");
        int count = file.Schema.ReadArrayCount(entry, element, structure);
        // Array headers are never page-adjusted; their elements continue the owner's traversal or start a new one.
        PsoPointer pointer = inline ? Add(owner, entry.DataOffset) : ReadPointer(field);
        if (!inline && pointer.IsNull)
        {
            BinaryLayout.Require(count == 0, "Nonempty PSO array has a null pointer.");
            return new PsoArray([]);
        }
        if (count > options.MaximumArrayElements - arrayElements)
            throw new InvalidDataException("PSO array elements exceed configured limit.");
        arrayElements += count;
        if (count == 0)
            return new PsoArray([]);
        if (stride <= 0)
            return Unsupported(entry, "Array element layout is not supported.");
        long length = (long)count * stride;
        BinaryLayout.Require(length <= int.MaxValue && (!inline || length <= field.Length), "PSO array exceeds its parent.");
        ReadOnlySpan<byte> data = file.Slice(pointer, (int)length);
        PageState? pages = ownerPages ?? (wide ? new() : null);
        PageState? structurePages = wide ? new() : pages;
        var values = new PsoValue[count];
        for (int i = 0; i < values.Length; i++)
        {
            ReadOnlySpan<byte> item = data.Slice(i * stride, stride);
            if (element.DataType == PsoDataType.Structure)
                values[i] = PsoLayout.IsPointerElement(element) ? PointerTarget(Unwrap(ReadPointer(item), pages), depth, pages)
                    : new PsoNodeValue(Structure(element.ReferenceKey, Add(pointer, i * stride), depth, !inline, structurePages));
            else
                values[i] = element.DataType == PsoDataType.String ? String(element, item, pages) : Scalar(element, item);
        }
        // Member/range arrays retain their entire fixed length; atFixedArray exposes its stored live count.
        return new PsoArray(values);
    }

    private PsoMap Map(PsoStruct owner, PsoEntry entry, ReadOnlySpan<byte> field, int depth)
    {
        BinaryLayout.Require(field.Length >= PsoLayout.MapStorageSize, "Truncated binary map.");
        var header = PsoArrayHeader.Read(field[8..], file.PointerLayout);
        BinaryLayout.Require(!header.Pointer.IsNull || header.Count == 0, "Nonempty map has a null pointer.");
        if (header.Count > options.MaximumArrayElements - arrayElements)
            throw new InvalidDataException("PSO map entries exceed configured limit.");
        arrayElements += header.Count;
        if (header.Pointer.IsNull)
        {
            PsoMapLayout.Descriptors(owner, entry);
            return new(0, [], field[0] != 0);
        }
        uint pairHash = file.Block(header.Pointer.BlockId).NameHash;
        var pair = PsoMapLayout.Pair(file.Schema, owner, entry, pairHash);
        file.Slice(header.Pointer, checked(header.Count * pair.Length));
        var entries = new PsoNode[header.Count];
        for (int i = 0; i < entries.Length; i++)
            entries[i] = Structure(pairHash, Add(header.Pointer, checked(i * pair.Length)), depth, true, null);
        return new(pairHash, entries, field[0] != 0);
    }

    private PsoUnsupported Unsupported(PsoEntry entry, string reason)
    {
        diagnostics.Add(new Diagnostic(PsoDiagnosticCodes.FieldUnsupported, reason, $"hash_{entry.NameHash:X8}"));
        return new(entry, reason);
    }

    // Per-block page and previous raw offset for one traversal of 20-bit pointers.
    private sealed class PageState : Dictionary<int, (int Page, int Previous)>;
}
