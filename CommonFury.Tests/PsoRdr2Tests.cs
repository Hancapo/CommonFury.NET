using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using CommonFury.Hashing;
using CommonFury.Pso;
using Xunit;

namespace CommonFury.Tests;

// Synthetic RDR2 PSO fixtures (20-bit pointers, 24-byte PMAP header, paged strings).
public sealed class PsoRdr2Tests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ReferenceData", "pso", name));
    private static PsoValue Field(PsoNode node, uint hash) => node.Fields.Single(f => f.Entry.NameHash == hash).Value;
    private static string Text(PsoValue value) => Encoding.UTF8.GetString(Assert.IsType<PsoText>(value).Bytes);

    [Fact]
    public void TypedGraphArraysAndHalfPreserveExactSource()
    {
        Assert.Equal(0x5A63E346u, Jenkins.PsoChecksum([1, 255, 2, 128]));
        byte[] source = Fixture("rdr2_sample.pso"), original = source.ToArray();
        var file = PsoFile.Read(source);
        source[0] = 0;
        Assert.Equal(PsoPointerLayout.BlockId20, file.PointerLayout);
        var document = file.ReadRoot();
        Assert.Equal(0x11111111u, document.Root.TypeHash);
        Assert.True(Assert.IsType<PsoBoolean>(Field(document.Root, 0x10)).Value);
        Assert.Equal(0x12345678, Assert.IsType<PsoInteger>(Field(document.Root, 0x11)).Value);
        Assert.Equal(new Vector3(1, 2, 3), Assert.IsType<PsoVector3>(Field(document.Root, 0x12)).Value);
        Assert.Equal(0xAABBCCDDu, Assert.IsType<PsoHash>(Field(document.Root, 0x13)).Value.Value);
        Assert.Equal(new long[] { 7, 8, 9 }, Assert.IsType<PsoArray>(Field(document.Root, 0x22222222)).Values.Select(v => Assert.IsType<PsoInteger>(v).Value));
        Assert.Equal((Half)1.5f, Assert.IsType<PsoHalf>(Field(document.Root, 0x14)).Value);
        Assert.True(document.Report.IsValid);
        Assert.Equal(original, file.Write());
    }

    [Fact]
    public void InvalidFieldIsIsolatedWithoutDiscardingOtherFieldsWhenRequested()
    {
        byte[] source = Fixture("rdr2_sample.pso");
        BinaryPrimitives.WriteUInt32BigEndian(source.AsSpan(0x28), 999);
        var file = PsoFile.Read(source);
        Assert.ThrowsAny<InvalidDataException>(() => file.ReadRoot());
        var document = file.ReadRoot(new(continueOnFieldErrors: true));
        Assert.False(document.Report.IsValid);
        Assert.Equal(PsoDiagnosticCodes.FieldDecode, Assert.Single(document.Report.Diagnostics).Code);
        Assert.IsType<PsoMalformed>(Field(document.Root, 0x22222222));
        Assert.Equal(0x12345678, Assert.IsType<PsoInteger>(Field(document.Root, 0x11)).Value);
        Assert.Equal(source, file.Write());
        Assert.Throws<NotSupportedException>(() => PsoFile.FromGraph(file.Schema, document.Root, PsoPointerLayout.BlockId20));
    }

    [Fact]
    public void SectionsLimitsAndLegacyPointerLayout()
    {
        byte[] data = Fixture("rdr2_sample.pso");
        int psin = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        Assert.Throws<InvalidDataException>(() => PsoFile.Read(data[..^1]));
        Assert.Throws<InvalidDataException>(() => PsoFile.Read([.. data, .. data[..psin]]));
        var bounded = PsoFile.Read(data).ReadRoot(new(maximumArrayElements: 2, continueOnFieldErrors: true));
        Assert.False(bounded.Report.IsValid);
        Assert.IsType<PsoMalformed>(Field(bounded.Root, 0x22222222));
        // Convert the exact PMAP extent to its GTA 16-byte header form.
        int pmapLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(psin + 4));
        byte[] legacy = [.. data[..(psin + 12)], .. data.AsSpan(psin + 16, 4), .. data[(psin + 24)..]];
        BinaryPrimitives.WriteUInt32BigEndian(legacy.AsSpan(psin + 4), (uint)(pmapLength - 8));
        var file = PsoFile.Read(legacy);
        Assert.Equal(PsoPointerLayout.BlockId12, file.PointerLayout);
        var document = file.ReadRoot();
        Assert.True(document.Report.IsValid);
        Assert.Equal(3, Assert.IsType<PsoArray>(Field(document.Root, 0x22222222)).Values.Count);
        // An explicit layout overrides detection.
        Assert.Throws<InvalidDataException>(() => PsoFile.Read(data, new() { PointerLayout = PsoPointerLayout.BlockId12 }).ReadRoot());
    }

    [Fact]
    public void PagedStringsFollowWrappedPointersAcrossFourKilobytePages()
    {
        byte[] raw = Fixture("rdr2_paged_txd.ymf");
        var file = PsoFile.Read(raw);
        Assert.True(file.Blocks.Any(block => block.NameHash == 1 && block.Length > 0x1000), "Fixture must cross a string page.");
        var document = file.ReadRoot();
        Assert.True(document.Report.IsValid);
        var relationships = Assert.IsType<PsoArray>(Assert.Single(document.Root.Fields).Value).Values;
        Assert.Equal(80, relationships.Count);
        for (int i = 0; i < relationships.Count; i++)
        {
            PsoNode node = Assert.IsType<PsoNodeValue>(relationships[i]).Node;
            Assert.Equal($"parent_{i:0000}_with_a_long_texture_name", Text(node.Fields[0].Value));
            Assert.Equal($"child_{i:0000}_with_a_long_texture_name", Text(node.Fields[1].Value));
        }
        Assert.Equal(raw, file.Write());
    }

    [Fact]
    public void DependencyManifestsDecodeHashedStrings()
    {
        var direct = PsoFile.Read(Fixture("rdr2_dependency.ymf")).ReadRoot().Root;
        PsoNode dependency = Assert.IsType<PsoNodeValue>(Assert.Single(Assert.IsType<PsoArray>(Assert.Single(direct.Fields).Value).Values)).Node;
        Assert.Equal(0x11111111u, Assert.IsType<PsoHash>(dependency.Fields[0].Value).Value.Value);
        var tables = PsoFile.Read(Fixture("rdr2_hash_dependency.ymf")).ReadRoot().Root;
        PsoNode table = Assert.IsType<PsoNodeValue>(Assert.Single(Assert.IsType<PsoArray>(Assert.Single(tables.Fields).Value).Values)).Node;
        Assert.Equal(0xC51A77D5u, table.TypeHash);
        Assert.Equal(new uint[] { 0x55555555, 0x66666666 },
            Assert.IsType<PsoArray>(table.Fields[1].Value).Values.Select(v => Assert.IsType<PsoHash>(v).Value.Value));
        Assert.Equal(7, Assert.IsType<PsoInteger>(Field(PsoFile.Read(Fixture("rdr2_pso.ymt")).ReadRoot().Root, 0x55667788)).Value);
    }

    [Fact]
    public void CyclesUnknownTypesAndRecordBoundsAreExplicit()
    {
        byte[] cycle = Fixture("rdr2_sample.pso");
        int schema = PsoTests.FindSection(cycle, PsoSectionId.Psch), first = schema + 32;
        cycle[first + 4] = (byte)PsoDataType.Structure;
        cycle[first + 5] = 3;
        BinaryPrimitives.WriteUInt32BigEndian(cycle.AsSpan(16), 1);
        var file = PsoFile.Read(cycle);
        var root = file.ReadRoot().Root;
        Assert.Same(root, Assert.IsType<PsoNodeValue>(Field(root, 0x10)).Node);
        Assert.Equal(cycle, file.Write());
        byte[] unknown = Fixture("rdr2_sample.pso");
        unknown[first + 4] = 0xFE;
        var opaque = PsoFile.Read(unknown).ReadRoot();
        Assert.IsType<PsoUnsupported>(Field(opaque.Root, 0x10));
        Assert.Equal(PsoDiagnosticCodes.FieldUnsupported, Assert.Single(opaque.Report.Diagnostics).Code);
        // A scalar may fit PSIN but must not escape the declared root instance.
        byte[] shortened = Fixture("rdr2_sample.pso");
        BinaryPrimitives.WriteInt32BigEndian(shortened.AsSpan(schema + 24), 1);
        var bounded = PsoFile.Read(shortened).ReadRoot(new(continueOnFieldErrors: true));
        Assert.True(Assert.IsType<PsoBoolean>(Field(bounded.Root, 0x10)).Value);
        Assert.IsType<PsoMalformed>(Field(bounded.Root, 0x11));
        Assert.False(bounded.Report.IsValid);
    }

    [Fact]
    public void EnumsAndUnknownSectionsRetainTheirData()
    {
        byte[] original = Fixture("rdr2_sample.pso");
        int schema = PsoTests.FindSection(original, PsoSectionId.Psch);
        byte[] old = original[schema..], updated = new byte[old.Length + 20];
        old.AsSpan(0, 20).CopyTo(updated);
        old.AsSpan(20).CopyTo(updated.AsSpan(28));
        BinaryPrimitives.WriteUInt32BigEndian(updated.AsSpan(4), (uint)updated.Length);
        BinaryPrimitives.WriteUInt32BigEndian(updated.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(updated.AsSpan(16), 28);
        BinaryPrimitives.WriteUInt32BigEndian(updated.AsSpan(20), 0xEEEEEEEE);
        BinaryPrimitives.WriteUInt32BigEndian(updated.AsSpan(24), (uint)(updated.Length - 12));
        updated[^12] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(updated.AsSpan(updated.Length - 10), 1);
        BinaryPrimitives.WriteUInt32BigEndian(updated.AsSpan(updated.Length - 8), 0x77777777);
        BinaryPrimitives.WriteInt32BigEndian(updated.AsSpan(updated.Length - 4), -7);
        byte[] all = [.. original[..schema], .. updated, .. "STDN"u8, 0, 0, 0, 11, 1, 2, 3];
        var file = PsoFile.Read(all);
        Assert.Equal(-7, Assert.IsType<PsoEnum>(file.Schema.ByHash[0xEEEEEEEE]).Entries[0].Value);
        Assert.Equal(all, file.Write());
        Assert.Equal(11, file.Sections.Single(s => s.Identifier == 0x5354444E).Data.Length);
    }

    [Fact]
    public void GraphWriterProducesTheRdr2LayoutAndRoundTrips()
    {
        var file = PsoFile.Read(Fixture("rdr2_paged_txd.ymf"));
        var document = file.ReadRoot();
        PsoFile copy = PsoFile.FromGraph(file.Schema, document.Root, PsoPointerLayout.BlockId20);
        byte[] bytes = copy.Write();
        Assert.Equal(PsoPointerLayout.BlockId20, PsoFile.Read(bytes).PointerLayout);
        int pmap = PsoTests.FindSection(bytes, PsoSectionId.Pmap);
        Assert.Equal((uint)(24 + copy.Blocks.Count * 16), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pmap + 4)));
        Assert.Equal(copy.Blocks.Count, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(pmap + 16)));
        Assert.True(copy.ValidateChecksum().IsValid);
        var relationships = Assert.IsType<PsoArray>(Assert.Single(copy.ReadRoot().Root.Fields).Value).Values;
        Assert.Equal(80, relationships.Count);
        Assert.Equal("child_0079_with_a_long_texture_name", Text(Assert.IsType<PsoNodeValue>(relationships[79]).Node.Fields[1].Value));
        // Rewriting the 12-bit layout from the same graph yields identical decoded values.
        var legacy = PsoFile.FromGraph(file.Schema, document.Root).ReadRoot().Root;
        Assert.Equal(Text(Assert.IsType<PsoNodeValue>(relationships[40]).Node.Fields[0].Value),
            Text(Assert.IsType<PsoNodeValue>(Assert.IsType<PsoArray>(legacy.Fields[0].Value).Values[40]).Node.Fields[0].Value));
    }

    [Fact]
    public void WrappedPointerArraysAdvanceOnePagePerDecreasingOffset()
    {
        // A 20-bit file whose pointer array targets nodes at 0x20, 0x1010 and 0x2000 of a 0x2010-byte block; raw offsets are 0x20, 0x10, 0.
        const uint RootHash = 0xA0000001, ItemHash = 0xA0000002, Value = 0xA0000003;
        var schema = new PsoSchema([
            new PsoStruct(RootHash, 16, [new(PsoEntry.ArrayInfoHash, PsoDataType.Structure, 3, 0, 0), new(0xA0000004, PsoDataType.Array, 0, 0, 0)]),
            new PsoStruct(ItemHash, 4, [new(Value, PsoDataType.UInt32, 0, 0, 0)])
        ]);
        var root = new PsoBlockBuilder(RootHash);
        root.Append(new byte[16]);
        var pointers = new PsoBlockBuilder(0xC);
        pointers.Append(new byte[24]);
        var items = new PsoBlockBuilder(ItemHash);
        byte[] storage = new byte[0x2010];
        int[] offsets = [0x20, 0x1010, 0x2000];
        for (int i = 0; i < offsets.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(storage.AsSpan(offsets[i]), (uint)(100 + i));
        items.Append(storage);
        var builder = new PsoBuilder(schema) { Root = root, PointerLayout = PsoPointerLayout.BlockId20 };
        builder.Blocks.AddRange([root, pointers, items]);
        builder.PointerPatches.Add(new(root, 0, pointers, 0));
        byte[] bytes = builder.Build().Write();
        int psin = 16;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(psin + 8), 3);
        for (int i = 0; i < offsets.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(psin + 16 + i * 8), (uint)((offsets[i] & 0xFFF) << 20 | 3));
        var array = Assert.IsType<PsoArray>(PsoFile.Read(bytes).ReadRoot().Root.Fields[0].Value);
        Assert.Equal(new long[] { 100, 101, 102 },
            array.Values.Select(v => Assert.IsType<PsoInteger>(Assert.IsType<PsoNodeValue>(v).Node.Fields[0].Value).Value));
        Assert.Equal(offsets, array.Values.Select(v => Assert.IsType<PsoNodeValue>(v).Node.Pointer.Offset));
        // 20-bit writing rejects offsets it cannot encode.
        builder.PointerPatches.Add(new(pointers, 0, items, 0x1010));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Build());
    }
}
