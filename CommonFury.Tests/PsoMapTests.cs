using System.Buffers.Binary;
using CommonFury.Pso;
using Xunit;

namespace CommonFury.Tests;

public sealed class PsoMapTests
{
    private const uint Root = 0xABC01001, Pair = 0xABC01002, MapField = 0xABC01003, Key = 0xABC01004, Item = 0xABC01005;
    private static readonly PsoEntry MapEntry = new(MapField, PsoDataType.Map, 1, 0, 1);
    private static readonly PsoEntry KeyEntry = new(Key, PsoDataType.UInt32, 0, 0, 0);
    private static readonly PsoEntry ItemEntry = new(Item, PsoDataType.Structure, 3, 8, 0);
    private static PsoSchema Schema() => new([
        new PsoStruct(Root, 32, [KeyEntry with { NameHash = 0x100 }, ItemEntry with { NameHash = 0x101 }, MapEntry]),
        new PsoStruct(Pair, 16, [KeyEntry, ItemEntry], reservedByte: 2)
    ]);

    private static PsoFile Fixture()
    {
        byte[] header = new byte[32];
        header[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), new PsoPointer(2, 0).Encode());
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(18), 1);
        byte[] pair = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(pair, 42);
        BinaryPrimitives.WriteUInt32BigEndian(pair.AsSpan(8), new PsoPointer(1, 0).Encode());
        var root = new PsoBlockBuilder(Root);
        root.Append(header);
        var entries = new PsoBlockBuilder(Pair);
        entries.Append(pair);
        var builder = new PsoBuilder(Schema()) { Root = root };
        builder.Blocks.AddRange([root, entries]);
        return builder.Build();
    }

    // Independent 20-bit fixture: 24-byte PMAP header, 24-byte map storage, two pairs of eight bytes.
    // Field hashes moved out of the anonymous-descriptor range 0x100-0xCFF (originally 0x123, 0x111 and 0x222).
    internal static byte[] BlockId20Fixture()
    {
        byte[] b = new byte[224];
        void W(int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v);
        void H(int at, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), v);
        "PSIN"u8.CopyTo(b); W(4, 56); b[16] = 1; W(24, 2); H(32, 2); H(34, 2);
        W(40, 7); W(44, 11); W(48, 20); W(52, unchecked((uint)-4));
        "PMAP"u8.CopyTo(b.AsSpan(56)); W(60, 56); W(64, 1); H(72, 2);
        W(80, 0xAAAA); W(84, 16); W(92, 24); W(96, 0xBBBB); W(100, 40); W(108, 16);
        "PSCH"u8.CopyTo(b.AsSpan(112)); W(116, 112); W(120, 2); W(124, 0xAAAA); W(128, 28); W(132, 0xBBBB); W(136, 76);
        H(142, 3); W(144, 24);
        W(152, 256); b[156] = 6; W(164, 256); b[168] = 5;
        W(176, 0xA123); b[180] = 16; b[181] = 1; W(184, 1);
        H(190, 2); W(192, 8);
        W(200, 0xA111); b[204] = 6; W(212, 0xA222); b[216] = 5; H(218, 4);
        return b;
    }

    [Fact]
    public void ReadsIndependentMapLayoutAndRewritesCycles()
    {
        var file = Fixture();
        var graph = file.ReadRoot().Root;
        var map = Assert.IsType<PsoMap>(Assert.Single(graph.Fields).Value);
        Assert.True(map.Sorted);
        Assert.Equal(42, Assert.IsType<PsoInteger>(map.Entries[0].Fields[0].Value).Value);
        Assert.Same(graph, Assert.IsType<PsoNodeValue>(map.Entries[0].Fields[1].Value).Node);
        var copy = PsoFile.FromGraph(file.Schema, graph).ReadRoot().Root;
        var copiedMap = Assert.IsType<PsoMap>(copy.Fields[0].Value);
        Assert.Same(copy, Assert.IsType<PsoNodeValue>(copiedMap.Entries[0].Fields[1].Value).Node);
    }

    [Fact]
    public void EmptyMapCanBeAuthoredWithoutAllocatingPairs()
    {
        var node = PsoNode.Create(Root, [new(MapEntry, new PsoMap(0, []))]);
        var result = PsoFile.FromGraph(Schema(), node).ReadRoot();
        Assert.Empty(Assert.IsType<PsoMap>(result.Root.Fields[0].Value).Entries);
    }

    [Fact]
    public void PairSchemaMustAgreeWithMapDescriptors()
    {
        var wrong = new PsoSchema([
            new PsoStruct(Root, 32, [KeyEntry with { NameHash = 0x100 }, ItemEntry with { NameHash = 0x101 }, MapEntry]),
            new PsoStruct(Pair, 16, [KeyEntry with { DataType = PsoDataType.Float }, ItemEntry])
        ]);
        var pair = PsoNode.Create(Pair, [new(KeyEntry with { DataType = PsoDataType.Float }, new PsoFloat(42)), new(ItemEntry, PsoNull.Instance)]);
        var root = PsoNode.Create(Root, [new(MapEntry, new PsoMap(Pair, [pair]))]);
        Assert.Throws<InvalidDataException>(() => PsoFile.FromGraph(wrong, root));
    }

    [Fact]
    public void BlockId20BinaryMapIsDecodedInsteadOfBeingMistakenForAPointer()
    {
        var file = PsoFile.Read(BlockId20Fixture());
        Assert.Equal(PsoPointerLayout.BlockId20, file.PointerLayout);
        var document = file.ReadRoot();
        Assert.True(document.Report.IsValid);
        var map = Assert.IsType<PsoMap>(Assert.Single(document.Root.Fields).Value);
        Assert.True(map.Sorted);
        Assert.Equal(2, map.Entries.Count);
        Assert.Equal(7, Assert.IsType<PsoInteger>(map.Entries[0].Fields[0].Value).Value);
        Assert.Equal(-4, Assert.IsType<PsoInteger>(map.Entries[1].Fields[1].Value).Value);
        // The 24-byte map storage also round-trips through the graph writer in the 20-bit layout.
        var copy = PsoFile.FromGraph(file.Schema, document.Root, PsoPointerLayout.BlockId20);
        Assert.Equal(PsoPointerLayout.BlockId20, PsoFile.Read(copy.Write()).PointerLayout);
        var copied = Assert.IsType<PsoMap>(copy.ReadRoot().Root.Fields[0].Value);
        Assert.Equal(map.Entries.Select(e => e.Fields), copied.Entries.Select(e => e.Fields), new FieldListComparer());
    }

    [Theory]
    [InlineData(24, 0u)]
    [InlineData(32, 0x00030003u)]
    [InlineData(184, 0xFFFF0001u)]
    public void MalformedBlockId20MapIsReportedOrRejected(int at, uint value)
    {
        byte[] bytes = BlockId20Fixture();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), value);
        var file = PsoFile.Read(bytes);
        Assert.ThrowsAny<InvalidDataException>(() => file.ReadRoot());
        var document = file.ReadRoot(new(continueOnFieldErrors: true));
        Assert.False(document.Report.IsValid);
        Assert.IsType<PsoMalformed>(Assert.Single(document.Root.Fields).Value);
    }

    private sealed class FieldListComparer : IEqualityComparer<IReadOnlyList<PsoField>>
    {
        public bool Equals(IReadOnlyList<PsoField>? x, IReadOnlyList<PsoField>? y) => x!.Select(f => f.Value).SequenceEqual(y!.Select(f => f.Value));
        public int GetHashCode(IReadOnlyList<PsoField> obj) => obj.Count;
    }
}
