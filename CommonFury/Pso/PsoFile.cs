using CommonFury.Binary;
using CommonFury.Hashing;
using CommonFury.IO;

namespace CommonFury.Pso;

/// <summary>Container reading options. Defaults detect the pointer layout and apply the strict schema rules.</summary>
public sealed record PsoFileOptions
{
    /// <summary>Forces a pointer layout; null detects it from the exact PMAP extent and falls back to <see cref="PsoPointerLayout.BlockId12"/>.</summary>
    public PsoPointerLayout? PointerLayout { get; init; }
    /// <summary>Accepts distinct PSCH type hashes that share one definition offset (RDR2).</summary>
    public bool AllowAliasedSchemaDefinitions { get; init; }
}

/// <summary>Owned immutable PSO container. Write reproduces all sections byte-for-byte; decoded nodes are an inspection view.</summary>
public sealed class PsoFile
{
    /// <summary>Serializes a complete value graph with canonical padding; shared nodes are written once.</summary>
    public static PsoFile FromGraph(PsoSchema schema, PsoNode root, PsoPointerLayout layout = PsoPointerLayout.BlockId12) =>
        new PsoGraphWriter(schema, layout).Write(root);
    /// <summary>All sections in file order, including unknown ones.</summary>
    public IReadOnlyList<PsoSection> Sections { get; }
    /// <summary>One-based PMAP blocks in table order.</summary>
    public IReadOnlyList<PsoBlock> Blocks { get; }
    public int RootBlockId { get; }
    /// <summary>PMAP header word after the block count (offset 14 for 12-bit, 18 for 20-bit layouts).</summary>
    public ushort PmapReserved { get; }
    public PsoPointerLayout PointerLayout { get; }
    public PsoSchema Schema { get; }
    internal PsoSection Psin { get; }

    private PsoFile(List<PsoSection> sections, PsoFileOptions options)
    {
        Sections = sections.AsReadOnly();
        PsoSection Require(uint id)
        {
            PsoSection[] matches = sections.Where(s => s.Identifier == id).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException($"Expected exactly one PSO section 0x{id:X8}.");
            return matches[0];
        }
        Psin = Require(PsoSectionId.Psin);
        BinaryLayout.Require(Psin.Data.Length >= 16, "Truncated PSIN prefix.");
        Schema = PsoSchema.Read(Require(PsoSectionId.Psch).Data, options.AllowAliasedSchemaDefinitions);
        ReadOnlySpan<byte> pmap = Require(PsoSectionId.Pmap).Data;
        BinaryLayout.Require(pmap.Length >= 16, "Truncated PMAP header.");
        PointerLayout = options.PointerLayout ?? DetectLayout(pmap);
        bool wide = PointerLayout == PsoPointerLayout.BlockId20;
        var reader = new BinarySpanReader(pmap, ByteOrder.Big);
        reader.Seek(8);
        RootBlockId = reader.ReadInt32();
        if (wide)
        {
            BinaryLayout.Require(pmap.Length >= 24, "Truncated PMAP header.");
            reader.Seek(16);
        }
        ushort count = reader.ReadUInt16();
        PmapReserved = reader.ReadUInt16();
        if (wide)
            reader.Seek(24);
        BinaryLayout.Require(count <= (wide ? ushort.MaxValue : 0xFFF) && count <= reader.Remaining / 16, "Truncated PMAP or too many addressable blocks.");
        BinaryLayout.Require(RootBlockId >= 1 && RootBlockId <= count, "Root block does not exist.");
        var blocks = new PsoBlock[count];
        for (int i = 0; i < count; i++)
        {
            uint hash = reader.ReadUInt32();
            int offset = reader.ReadInt32(), reserved = reader.ReadInt32(), length = reader.ReadInt32();
            BinaryLayout.Require(offset >= 16, "PMAP block points inside PSIN header.");
            BinaryLayout.Slice(Psin.Data, offset, length);
            blocks[i] = new(hash, offset, length, reserved);
        }
        Blocks = Array.AsReadOnly(blocks);
    }

    // The two header sizes give block tables of different residues modulo 16, so an exact extent is unambiguous.
    private static PsoPointerLayout DetectLayout(ReadOnlySpan<byte> pmap)
    {
        var reader = new BinarySpanReader(pmap, ByteOrder.Big);
        reader.Seek(12);
        if (16 + reader.ReadUInt16() * 16 == pmap.Length)
            return PsoPointerLayout.BlockId12;
        if (pmap.Length >= 24)
        {
            reader.Seek(16);
            if (24 + reader.ReadUInt16() * 16 == pmap.Length)
                return PsoPointerLayout.BlockId20;
        }
        return PsoPointerLayout.BlockId12;
    }

    /// <summary>Checks only the PSIN signature.</summary>
    public static bool IsPso(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("PSIN"u8);

    /// <summary>Reads the section sequence and the PMAP and PSCH sections; copies the input.</summary>
    public static PsoFile Read(ReadOnlySpan<byte> data, PsoFileOptions? options = null)
    {
        BinaryLayout.Require(IsPso(data), "Expected PSIN at the beginning of a PSO file.");
        var sections = new List<PsoSection>();
        while (!data.IsEmpty)
        {
            var header = new BinarySpanReader(data, ByteOrder.Big);
            header.ReadUInt32();
            uint size = header.ReadUInt32();
            BinaryLayout.Require(size >= 8 && size <= data.Length, "Invalid PSO section size or trailing bytes.");
            sections.Add(new(data[..(int)size]));
            data = data[(int)size..];
        }
        return new(sections, options ?? new());
    }

    /// <summary>Reproduces the sections byte for byte.</summary>
    public byte[] Write()
    {
        int size = 0;
        foreach (PsoSection section in Sections)
            size = checked(size + section.Data.Length);
        var result = new byte[size];
        var writer = new BinarySpanWriter(result, ByteOrder.Big);
        foreach (PsoSection section in Sections)
            writer.WriteBytes(section.Data);
        return result;
    }

    /// <summary>Writes to a borrowed stream without closing it; an I/O failure may leave partial output.</summary>
    public void Write(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        foreach (PsoSection section in Sections)
            destination.Write(section.Data);
    }
    /// <summary>Replaces the file atomically where supported.</summary>
    public void Save(string path) => AtomicFile.Write(path, Write());
    /// <summary>Decodes the graph reachable from the root block.</summary>
    public PsoDocument ReadRoot(PsoReadOptions? options = null) => new PsoReader(this, options ?? new()).Read();

    /// <summary>Checks the presence, layout and value of the CHKS section.</summary>
    public ValidationReport ValidateChecksum()
    {
        var diagnostics = new List<Diagnostic>();
        PsoSection[] checks = Sections.Where(s => s.Identifier == PsoSectionId.Chks).ToArray();
        if (checks.Length == 0)
        { diagnostics.Add(new Diagnostic(PsoDiagnosticCodes.ChecksumMissing, "CHKS section is absent.")); return new ValidationReport(diagnostics); }
        if (checks.Length != 1 || checks[0].Data.Length != 20 || Sections[^1] != checks[0])
        {
            diagnostics.Add(new Diagnostic(PsoDiagnosticCodes.ChecksumLayout, "Checksum validation requires one terminal 20-byte CHKS section."));
            return new ValidationReport(diagnostics);
        }
        byte[] bytes = Write();
        var reader = new BinarySpanReader(bytes.AsSpan(bytes.Length - 12), ByteOrder.Big);
        uint length = reader.ReadUInt32(), expected = reader.ReadUInt32();
        bytes.AsSpan(bytes.Length - 12, 8).Clear();
        if (length != bytes.Length)
            diagnostics.Add(new Diagnostic(PsoDiagnosticCodes.ChecksumSize, "Recorded file size does not match."));
        if (expected != Jenkins.PsoChecksum(bytes))
            diagnostics.Add(new Diagnostic(PsoDiagnosticCodes.ChecksumMismatch, "PSO checksum does not match."));
        return new ValidationReport(diagnostics);
    }

    internal PsoBlock Block(int id)
    {
        if (id < 1 || id > Blocks.Count)
            throw new InvalidDataException($"Unknown PSO block {id}.");
        return Blocks[id - 1];
    }

    internal ReadOnlySpan<byte> Slice(PsoPointer pointer, int length)
    {
        PsoBlock block = Block(pointer.BlockId);
        BinaryLayout.Require(pointer.Offset <= block.Length - length && length >= 0, "PSO pointer exceeds its declared block.");
        return BinaryLayout.Slice(Psin.Data, checked(block.Offset + pointer.Offset), length);
    }
}
