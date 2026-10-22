using CommonFury.Binary;

namespace CommonFury.Vertices;

/// <summary>One used slot of a gen9 vertex declaration. <see cref="Format"/> is the raw DXGI identifier, so unknown encodings survive.</summary>
/// <param name="Semantic">Declaration slot, 0–51.</param>
/// <param name="Offset">Byte offset of the slot's first element: within the vertex for interleaved buffers, within the buffer for planar ones.</param>
/// <param name="Stride">Byte distance between consecutive elements of this slot: the vertex stride when interleaved, the element size when planar.</param>
/// <param name="Format">Nonzero DXGI format identifier; see <see cref="VertexElementFormats"/>.</param>
public readonly record struct Gen9VertexSlot(VertexSemantic Semantic, uint Offset, byte Stride, byte Format)
{
    /// <summary>Maps the DXGI identifier to an element format with an explicit 10/10/10/2 interpretation; false when unknown.</summary>
    public bool TryGetFormat(Rgb10A2Range rgb10A2Range, out VertexElementFormat format) =>
        VertexElementFormats.TryFromDxgi(Format, rgb10A2Range, out format);
}

/// <summary>The 0x140-byte gen9 vertex declaration shared by GTA V Enhanced (interleaved) and RDR2 (planar).
/// Layout: 52 UInt32 slot offsets at 0, 52 slot strides at 208, 52 DXGI formats at 260 and a packed UInt64 at 312
/// holding the vertex stride in bits 2–9 and the vertex count in bits 10–31. Other packed bits are preserved.</summary>
/// <remarks>A slot is used when its format byte is nonzero. Offsets and strides of unused slots are not modeled and are written as zero.
/// Instances are immutable; the slot list is an owned copy in slot order.</remarks>
public sealed class Gen9VertexDeclaration
{
    /// <summary>Encoded size in bytes.</summary>
    public const int Size = 0x140;
    public const int SlotCount = 52;
    /// <summary>Largest vertex count representable in the packed word.</summary>
    public const int MaximumVertexCount = 0x3FFFFF;
    private const int StrideOffset = 208, FormatOffset = 260, PackedOffset = 312;
    private const ulong CountMask = (ulong)MaximumVertexCount << 10, StrideMask = 255ul << 2;

    /// <summary>Used slots in ascending slot order.</summary>
    public IReadOnlyList<Gen9VertexSlot> Slots { get; }
    /// <summary>The packed word as stored, including bits not described by <see cref="Stride"/> and <see cref="VertexCount"/>.</summary>
    public ulong Packed { get; }
    /// <summary>Aggregate bytes per vertex (packed bits 2–9).</summary>
    public int Stride => (int)((Packed >> 2) & 255);
    /// <summary>Declared vertex count (packed bits 10–31), or zero when unspecified.</summary>
    public int VertexCount => (int)((Packed >> 10) & MaximumVertexCount);
    /// <summary>True when every used slot advances by the whole vertex stride, as in an interleaved buffer.</summary>
    public bool IsInterleaved => Slots.All(slot => slot.Stride == Stride);

    /// <summary>Copies the slots, rejecting duplicate or out-of-range semantics and zero formats.</summary>
    public Gen9VertexDeclaration(IEnumerable<Gen9VertexSlot> slots, ulong packed)
    {
        ArgumentNullException.ThrowIfNull(slots);
        Gen9VertexSlot[] ordered = slots.OrderBy(slot => slot.Semantic).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            if ((byte)ordered[i].Semantic >= SlotCount || (i > 0 && ordered[i].Semantic == ordered[i - 1].Semantic))
                throw new ArgumentException("Vertex semantics must be unique and within the 52 declaration slots.", nameof(slots));
            if (ordered[i].Format == 0)
                throw new ArgumentException("A used declaration slot requires a nonzero format.", nameof(slots));
        }
        Slots = Array.AsReadOnly(ordered);
        Packed = packed;
    }

    /// <summary>Returns the packed word for a stride and vertex count, keeping the other bits of <paramref name="preserved"/>.</summary>
    public static ulong Pack(int stride, int vertexCount, ulong preserved = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stride);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stride, 255);
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(vertexCount, MaximumVertexCount);
        return (preserved & ~(StrideMask | CountMask)) | ((ulong)stride << 2) | ((ulong)vertexCount << 10);
    }

    /// <summary>Builds an interleaved declaration: every slot advances by <paramref name="stride"/> and offsets are within the vertex.</summary>
    public static Gen9VertexDeclaration Interleaved(int stride, ReadOnlySpan<(VertexSemantic Semantic, VertexElementFormat Format, int Offset)> elements, int vertexCount = 0)
    {
        if (stride is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(stride), "Gen9 declarations store the stride in eight bits.");
        var slots = new Gen9VertexSlot[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            var (semantic, format, offset) = elements[i];
            if (offset < 0 || offset > stride - VertexElementFormats.Size(format))
                throw new ArgumentException("A vertex element exceeds the stride.", nameof(elements));
            slots[i] = new(semantic, (uint)offset, (byte)stride, VertexElementFormats.ToDxgi(format));
        }
        return new(slots, Pack(stride, vertexCount));
    }

    /// <summary>Builds a planar declaration: channels are stored one after another in the given order, each advancing by its element size.</summary>
    /// <param name="vertexCount">Vertices in the buffer; each channel occupies <c>vertexCount * size</c> bytes.</param>
    /// <param name="elements">Channels in storage order.</param>
    /// <param name="packedMetadata">An existing packed word whose stride and other bits are kept (its stride must cover the channels);
    /// null derives the stride from the channel sizes.</param>
    public static Gen9VertexDeclaration Planar(int vertexCount, ReadOnlySpan<(VertexSemantic Semantic, VertexElementFormat Format)> elements, ulong? packedMetadata = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(vertexCount, MaximumVertexCount);
        var slots = new Gen9VertexSlot[elements.Length];
        int offset = 0, size = 0;
        for (int i = 0; i < elements.Length; i++)
        {
            int elementSize = VertexElementFormats.Size(elements[i].Format);
            slots[i] = new(elements[i].Semantic, (uint)offset, (byte)elementSize, VertexElementFormats.ToDxgi(elements[i].Format));
            offset = checked(offset + vertexCount * elementSize);
            size += elementSize;
        }
        int stride = packedMetadata is { } packed ? (int)((packed >> 2) & 255) : size;
        if (size > stride || stride > 255)
            throw new ArgumentException("Vertex channels exceed the eight-bit declaration stride.", nameof(elements));
        return new(slots, Pack(stride, vertexCount, packedMetadata ?? 0));
    }

    /// <summary>Replaces the packed vertex count of an encoded declaration in place, leaving every other byte unchanged.</summary>
    public static void ReplaceVertexCount(Span<byte> declaration, int vertexCount)
    {
        if (declaration.Length < Size)
            throw new ArgumentException("Vertex declaration is truncated.", nameof(declaration));
        ulong packed = Endian.U64(declaration[PackedOffset..], ByteOrder.Little);
        Endian.Put64(declaration[PackedOffset..], Pack((int)((packed >> 2) & 255), vertexCount, packed), ByteOrder.Little);
    }

    /// <summary>Reads a declaration from the first 0x140 bytes of the input.</summary>
    public static Gen9VertexDeclaration Read(ReadOnlySpan<byte> data)
    {
        BinaryLayout.Require(data.Length >= Size, "Vertex declaration is truncated.");
        var slots = new List<Gen9VertexSlot>();
        for (int i = 0; i < SlotCount; i++)
        {
            byte format = data[FormatOffset + i];
            if (format != 0)
                slots.Add(new((VertexSemantic)i, Endian.U32(data[(i * 4)..], ByteOrder.Little), data[StrideOffset + i], format));
        }
        return new(slots, Endian.U64(data[PackedOffset..], ByteOrder.Little));
    }

    /// <summary>Writes the declaration into the first 0x140 bytes of the destination, clearing unused slots.</summary>
    public void Write(Span<byte> data)
    {
        if (data.Length < Size)
            throw new ArgumentException("Declaration destination is too small.", nameof(data));
        data[..Size].Clear();
        foreach (Gen9VertexSlot slot in Slots)
        {
            int index = (byte)slot.Semantic;
            Endian.Put32(data[(index * 4)..], slot.Offset, ByteOrder.Little);
            data[StrideOffset + index] = slot.Stride;
            data[FormatOffset + index] = slot.Format;
        }
        Endian.Put64(data[PackedOffset..], Packed, ByteOrder.Little);
    }

    public byte[] ToArray()
    {
        var data = new byte[Size];
        Write(data);
        return data;
    }

    /// <summary>Returns a copy whose packed vertex count is replaced; slot offsets are not recalculated.</summary>
    public Gen9VertexDeclaration WithVertexCount(int vertexCount) => new(Slots, Pack(Stride, vertexCount, Packed));

    public bool TryFind(VertexSemantic semantic, out Gen9VertexSlot slot)
    {
        foreach (Gen9VertexSlot candidate in Slots)
            if (candidate.Semantic == semantic)
            { slot = candidate; return true; }
        slot = default;
        return false;
    }
}
