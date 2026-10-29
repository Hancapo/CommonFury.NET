using System.Buffers;
using CommonFury.Binary;
using CommonFury.Hashing;

namespace CommonFury.Pso;

/// <summary>Mutable binary block. Append copies its input; Data is borrowed and must be reacquired after mutation.</summary>
public sealed class PsoBlockBuilder(uint nameHash)
{
    private readonly ArrayBufferWriter<byte> buffer = new();
    public uint NameHash { get; } = nameHash;
    public ReadOnlySpan<byte> Data => buffer.WrittenSpan;
    /// <summary>Copies bytes to the end of the block and returns their offset.</summary>
    public int Append(ReadOnlySpan<byte> data)
    {
        int offset = buffer.WrittenCount;
        buffer.Write(data);
        return offset;
    }
    // Makes room for at least length more bytes, so appending them does not regrow the buffer.
    internal void Reserve(int length) => buffer.GetSpan(length);
}

/// <summary>Pointer fixup from a field in one block to an offset in another.</summary>
public sealed record PsoPointerPatch(PsoBlockBuilder Source, int Offset, PsoBlockBuilder Target, int TargetOffset);

/// <summary>Low-level PSIN/PMAP/PSCH construction. Pointer patches affect output copies, never the caller's blocks.</summary>
public sealed class PsoBuilder(PsoSchema schema)
{
    public PsoSchema Schema { get; } = schema ?? throw new ArgumentNullException(nameof(schema));
    /// <summary>Blocks in PMAP order; IDs are one-based positions.</summary>
    public List<PsoBlockBuilder> Blocks { get; } = [];
    public List<PsoPointerPatch> PointerPatches { get; } = [];
    /// <summary>Extra sections written after PSCH, such as STRF or PSIG.</summary>
    public List<PsoSection> AdditionalSections { get; } = [];
    public PsoBlockBuilder? Root { get; set; }
    /// <summary>Power-of-two alignment of block starts within PSIN.</summary>
    public int BlockAlignment { get; set; } = 16;
    /// <summary>Eight bytes written after the PSIN header.</summary>
    public ulong PsinPrefix { get; set; } = 0x7070707070707070;
    /// <summary>Header word after the PMAP block count; the 24-byte header repeats it to fill its padding.</summary>
    public ushort PmapReserved { get; set; } = 0x7070;
    /// <summary>Byte that fills the gap before each aligned block within PSIN (default 0).</summary>
    public byte BlockPadding { get; set; }
    /// <summary>Last word of the CHKS section.</summary>
    public uint ChecksumTail { get; set; } = 0x79707070;
    /// <summary>Whether to write the terminal CHKS section (default true).</summary>
    public bool IncludeChecksum { get; set; } = true;
    /// <summary>Pointer encoding and PMAP header size. 20-bit pointers hold 12-bit offsets, so patches beyond 4 KiB are rejected.</summary>
    public PsoPointerLayout PointerLayout { get; set; } = PsoPointerLayout.BlockId12;

    /// <summary>Lays out the registered blocks at aligned positions and applies the patches to copies.</summary>
    public PsoFile Build()
    {
        if (BlockAlignment <= 0 || !System.Numerics.BitOperations.IsPow2((uint)BlockAlignment))
            throw new ArgumentOutOfRangeException(nameof(BlockAlignment));
        bool wide = PointerLayout switch
        {
            PsoPointerLayout.BlockId12 => false,
            PsoPointerLayout.BlockId20 => true,
            _ => throw new ArgumentOutOfRangeException(nameof(PointerLayout))
        };
        if (Blocks.Count < 1 || Blocks.Count > (wide ? ushort.MaxValue : 0xFFF))
            throw new InvalidOperationException(wide ? "PSO requires 1 to 65535 blocks." : "PSO requires 1 to 4095 blocks.");
        var ids = new Dictionary<PsoBlockBuilder, int>(ReferenceEqualityComparer.Instance);
        var offsets = new int[Blocks.Count];
        int psinSize = 16;
        for (int i = 0; i < Blocks.Count; i++)
        {
            PsoBlockBuilder block = Blocks[i] ?? throw new InvalidOperationException("Null PSO block.");
            if (!ids.TryAdd(block, i + 1))
                throw new InvalidOperationException("The same block cannot appear twice.");
            offsets[i] = BinaryLayout.Align(psinSize, BlockAlignment);
            psinSize = checked(offsets[i] + block.Data.Length);
        }
        if (Root is null || !ids.TryGetValue(Root, out int rootId))
            throw new InvalidOperationException("Root must be a registered block.");
        byte[] schema = Schema.Write();
        int pmapHeader = wide ? 24 : 16;
        int pmapSize = checked(pmapHeader + Blocks.Count * 16);
        int size = checked(psinSize + pmapSize + schema.Length + (IncludeChecksum ? 20 : 0));
        foreach (PsoSection section in AdditionalSections)
        {
            if (section is null || section.Identifier is PsoSectionId.Psin or PsoSectionId.Pmap or PsoSectionId.Psch or PsoSectionId.Chks)
                throw new InvalidOperationException("Additional sections cannot replace structural or checksum sections.");
            size = checked(size + section.Data.Length);
        }
        byte[] bytes = new byte[size];
        var writer = new BinarySpanWriter(bytes, ByteOrder.Big);
        writer.WriteUInt32(PsoSectionId.Psin);
        writer.WriteUInt32((uint)psinSize);
        writer.WriteUInt64(PsinPrefix);
        for (int i = 0; i < Blocks.Count; i++)
        {
            while (writer.Position < offsets[i])
                writer.WriteByte(BlockPadding);
            writer.WriteBytes(Blocks[i].Data);
        }
        // Overlaps are found by sorting the patch starts. A patch overlapping an earlier one is reported before any later
        // patch's error, so each error path first checks the patches before it.
        var starts = new long[PointerPatches.Count];
        for (int p = 0; p < starts.Length; p++)
        {
            PsoPointerPatch patch = PointerPatches[p];
            if (patch is null || !ids.TryGetValue(patch.Source, out int sourceId) || !ids.TryGetValue(patch.Target, out int targetId))
            {
                RejectOverlaps(starts.AsSpan(0, p));
                throw new InvalidOperationException("Pointer patches must reference registered blocks.");
            }
            if (patch.Offset < 0 || patch.Offset > patch.Source.Data.Length - 4 || patch.TargetOffset < 0 || patch.TargetOffset > patch.Target.Data.Length)
            {
                RejectOverlaps(starts.AsSpan(0, p));
                throw new InvalidOperationException("Pointer patch is outside its block.");
            }
            starts[p] = (long)sourceId << 32 | (uint)patch.Offset;
            uint word;
            try { word = new PsoPointer(targetId, patch.TargetOffset).Encode(PointerLayout); }
            catch (ArgumentOutOfRangeException)
            {
                RejectOverlaps(starts.AsSpan(0, p + 1));
                throw;
            }
            Endian.Put32(bytes.AsSpan(offsets[sourceId - 1] + patch.Offset), word, ByteOrder.Big);
        }
        RejectOverlaps(starts);
        writer.WriteUInt32(PsoSectionId.Pmap);
        writer.WriteUInt32((uint)pmapSize);
        writer.WriteInt32(rootId);
        if (wide)
            writer.WriteUInt32(0);
        writer.WriteUInt16((ushort)Blocks.Count);
        writer.WriteUInt16(PmapReserved);
        if (wide)
        {
            writer.WriteUInt16(PmapReserved);
            writer.WriteUInt16(PmapReserved);
        }
        for (int i = 0; i < Blocks.Count; i++)
        {
            writer.WriteUInt32(Blocks[i].NameHash);
            writer.WriteInt32(offsets[i]);
            writer.WriteInt32(0);
            writer.WriteInt32(Blocks[i].Data.Length);
        }
        writer.WriteBytes(schema);
        foreach (PsoSection section in AdditionalSections)
            writer.WriteBytes(section.Data);
        if (IncludeChecksum)
        {
            writer.WriteUInt32(PsoSectionId.Chks);
            writer.WriteUInt32(20);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(ChecksumTail);
            uint checksum = Jenkins.PsoChecksum(bytes);
            Endian.Put32(bytes.AsSpan(size - 12), (uint)size, ByteOrder.Big);
            Endian.Put32(bytes.AsSpan(size - 8), checksum, ByteOrder.Big);
        }
        return PsoFile.ReadOwned(bytes, new() { PointerLayout = PointerLayout });
    }

    // Each patch covers four bytes. A start is the block ID in the high word over an offset below 2^31, so starts in
    // different blocks are never closer than four.
    private static void RejectOverlaps(Span<long> starts)
    {
        starts.Sort();
        for (int i = 1; i < starts.Length; i++)
            if (starts[i] - starts[i - 1] < 4)
                throw new InvalidOperationException("Overlapping pointer patches.");
    }
}
