using System.Buffers.Binary;
using CommonFury.Pso;
using Xunit;

namespace CommonFury.Tests;

public sealed class PsoGraphWriterTests
{
    private const uint RootHash = 0xA1234567;
    private const uint ArrayHash = 0xA1234568;

    [Fact]
    public void FixedArrayUsesTheAlignedStoredLiveCount()
    {
        // Three byte slots, one alignment byte, then a big-endian 32-bit count.
        PsoFile file = File([
            new(PsoEntry.ArrayInfoHash, PsoDataType.Byte, 0, 0, 0),
            new(ArrayHash, PsoDataType.Array, 1, 0, 3u << 16)
        ], [10, 20, 255, 0, 0, 0, 0, 2]);

        PsoArray values = Assert.IsType<PsoArray>(file.ReadRoot().Root.Fields[0].Value);
        Assert.Equal(new long[] { 10, 20 }, values.Values.Select(value => Assert.IsType<PsoInteger>(value).Value));
    }

    [Fact]
    public void FixedArrayRejectsInvalidAndTruncatedStoredCounts()
    {
        PsoEntry[] entries = [
            new(PsoEntry.ArrayInfoHash, PsoDataType.Byte, 0, 0, 0),
            new(ArrayHash, PsoDataType.Array, 1, 0, 3u << 16)
        ];
        Assert.Throws<InvalidDataException>(() => File(entries, [0, 0, 0, 0, 0, 0, 0, 4]).ReadRoot());
        Assert.Throws<InvalidDataException>(() => File(entries, [0, 0, 0, 0, 255, 255, 255, 255]).ReadRoot());
        Assert.Throws<InvalidDataException>(() => File(entries, new byte[7]).ReadRoot());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void RangeAndMemberArraysRetainTheirEntireFixedLength(byte subtype)
    {
        PsoFile file = File([
            new(PsoEntry.ArrayInfoHash, PsoDataType.UInt32, 0, 0, 0),
            new(ArrayHash, PsoDataType.Array, subtype, 0, 2u << 16)
        ], [0, 0, 0, 7, 0, 0, 0, 0]);

        PsoArray values = Assert.IsType<PsoArray>(file.ReadRoot().Root.Fields[0].Value);
        Assert.Equal(new long[] { 7, 0 }, values.Values.Select(value => Assert.IsType<PsoInteger>(value).Value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ExternalArraysReadCountsFromTheirDeclaredStorage(byte subtype)
    {
        byte[] root = new byte[subtype is 6 or 7 or 8 ? 20 : subtype == 3 ? 8 : 16];
        BinaryPrimitives.WriteUInt32BigEndian(root, new PsoPointer(2, 0).Encode());
        uint reference = subtype == 3 ? 2u << 16 : subtype is 6 or 7 or 8 ? 16u << 16 : 0;
        switch (subtype)
        {
            case 0:
                BinaryPrimitives.WriteUInt16BigEndian(root.AsSpan(8), 2);
                break;
            case 5:
                BinaryPrimitives.WriteUInt32BigEndian(root.AsSpan(8), 2);
                break;
            case 6:
                BinaryPrimitives.WriteUInt32BigEndian(root.AsSpan(16), 2);
                break;
            case 7:
                root[16] = 2;
                break;
            case 8:
                BinaryPrimitives.WriteUInt16BigEndian(root.AsSpan(16), 2);
                break;
        }
        PsoFile file = File([
            new(PsoEntry.ArrayInfoHash, PsoDataType.UInt32, 0, 0, 0),
            new(ArrayHash, PsoDataType.Array, subtype, 0, reference)
        ], root, ((uint)PsoDataType.UInt32, [0, 0, 0, 11, 0, 0, 0, 22]));

        PsoArray values = Assert.IsType<PsoArray>(file.ReadRoot().Root.Fields[0].Value);
        Assert.Equal(new long[] { 11, 22 }, values.Values.Select(value => Assert.IsType<PsoInteger>(value).Value));
    }

    [Fact]
    public void ArrayElementsCanBeArraysThatFindTheirDescriptorByContentsIndex()
    {
        // A member array of two atArrays; its ARRAYINFO (index 1) is an atArray whose contents index names entry 0.
        PsoEntry[] entries = [
            new(PsoEntry.ArrayInfoHash, PsoDataType.UInt32, 0, 0, 0),
            new(PsoEntry.ArrayInfoHash, PsoDataType.Array, 0, 0, 0),
            new(ArrayHash, PsoDataType.Array, 4, 0, 2u << 16 | 1)
        ];
        byte[] root = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(root, new PsoPointer(2, 0).Encode());
        BinaryPrimitives.WriteUInt16BigEndian(root.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16BigEndian(root.AsSpan(10), 2);
        PsoFile file = File(entries, root, ((uint)PsoDataType.UInt32, [0, 0, 0, 11, 0, 0, 0, 22]));
        Assert.Equal(16, file.Schema.ElementStride(entries[1]));

        PsoDocument document = file.ReadRoot();
        Assert.Empty(document.Report.Diagnostics);
        PsoArray groups = Assert.IsType<PsoArray>(document.Root.Fields[0].Value);
        long[][] Values(PsoArray array) => array.Values.Select(group => Assert.IsType<PsoArray>(group).Values.Select(value => Assert.IsType<PsoInteger>(value).Value).ToArray()).ToArray();
        Assert.Equal([[11, 22], []], Values(groups));

        byte[] written = PsoFile.FromGraph(file.Schema, document.Root).Write();
        Assert.Equal(file.Write(), written);
        Assert.Equal([[11, 22], []], Values(Assert.IsType<PsoArray>(PsoFile.Read(written).ReadRoot().Root.Fields[0].Value)));

        // A descriptor index that names a field, or the array itself, has no element layout.
        PsoEntry[] fieldIndex = [entries[0], entries[1] with { ReferenceKey = 2 }, entries[2]];
        PsoDocument unsupported = File(fieldIndex, root, ((uint)PsoDataType.UInt32, new byte[8])).ReadRoot();
        Assert.Equal(["Array has no ARRAYINFO descriptor.", "Array has no ARRAYINFO descriptor."],
            Assert.IsType<PsoArray>(unsupported.Root.Fields[0].Value).Values.Select(value => Assert.IsType<PsoUnsupported>(value).Reason));
        var schema = new PsoSchema([new PsoStruct(RootHash, 32, fieldIndex)]);
        Assert.Equal("A nested PSO array has no ARRAYINFO descriptor.", Assert.Throws<InvalidDataException>(() => PsoFile.FromGraph(schema,
            PsoNode.Create(RootHash, [new(fieldIndex[2], PsoArray.Create([PsoArray.Create([]), PsoArray.Create([])]))]))).Message);
        // A descriptor that describes itself nests until the depth limit.
        byte[] self = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(self, new PsoPointer(1, 0).Encode());
        BinaryPrimitives.WriteUInt16BigEndian(self.AsSpan(8), 2);
        Assert.Throws<InvalidDataException>(() => File([entries[0], entries[1] with { ReferenceKey = 1 }, entries[2]], self).ReadRoot());
    }

    [Fact]
    public void TypedPointerArrayRetainsSharedNodeIdentity()
    {
        byte[] root = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(root, new PsoPointer(2, 0).Encode());
        BinaryPrimitives.WriteUInt16BigEndian(root.AsSpan(8), 2);
        byte[] pointers = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(pointers, new PsoPointer(1, 0).Encode());
        BinaryPrimitives.WriteUInt32BigEndian(pointers.AsSpan(8), new PsoPointer(1, 0).Encode());
        PsoFile file = File([
            new(PsoEntry.ArrayInfoHash, PsoDataType.Structure, 3, 0, RootHash),
            new(ArrayHash, PsoDataType.Array, 0, 0, 0)
        ], root, ((uint)PsoDataType.Structure, pointers));

        PsoNode node = file.ReadRoot().Root;
        PsoArray values = Assert.IsType<PsoArray>(node.Fields[0].Value);
        Assert.Equal(2, values.Values.Count);
        Assert.All(values.Values, value => Assert.Same(node, Assert.IsType<PsoNodeValue>(value).Node));
    }

    [Fact]
    public void ExternalTypedStructureArrayCanReferenceItsContainingNode()
    {
        byte[] root = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(root, new PsoPointer(1, 0).Encode());
        BinaryPrimitives.WriteUInt16BigEndian(root.AsSpan(8), 1);
        PsoFile file = File([
            new(PsoEntry.ArrayInfoHash, PsoDataType.Structure, 0, 0, RootHash),
            new(ArrayHash, PsoDataType.Array, 0, 0, 0)
        ], root);

        PsoNode node = file.ReadRoot().Root;
        PsoArray values = Assert.IsType<PsoArray>(node.Fields[0].Value);
        Assert.Same(node, Assert.IsType<PsoNodeValue>(Assert.Single(values.Values)).Node);
    }

    [Fact]
    public void AtStringNativeLengthRetainsTheLastCharacterAndAcceptsOlderTerminatorCounts()
    {
        // Native AtString: pointer, padding, Length=15, Allocated=16, padding.
        // The second header uses a length that includes the NUL (Length=16).
        byte[] root = [
            0, 0, 0, 2, 0, 0, 0, 0, 0, 15, 0, 16, 0, 0, 0, 0,
            0, 0, 0, 2, 0, 0, 0, 0, 0, 16, 0, 16, 0, 0, 0, 0
        ];
        PsoFile file = File([
            new(0xA1234569, PsoDataType.String, 3, 0, 0),
            new(0xA1234570, PsoDataType.String, 3, 16, 0)
        ], root, (1, "BSS_1_MCS_2.WAV\0"u8.ToArray()));

        Assert.All(file.ReadRoot().Root.Fields, field =>
            Assert.True(Assert.IsType<PsoText>(field.Value).Bytes.SequenceEqual("BSS_1_MCS_2.WAV"u8)));
    }

    [Fact]
    public void IncompleteNodeCanBeReferencedBeforeItsSingleCompletion()
    {
        PsoEntry self = new(0xA1234569, PsoDataType.Structure, 3, 0, RootHash);
        PsoEntry values = new(ArrayHash, PsoDataType.Array, 0, 8, 0);
        PsoEntry name = new(0xA1234570, PsoDataType.String, 1, 24, 0);
        var schema = new PsoSchema([new PsoStruct(RootHash, 32, [self, new(PsoEntry.ArrayInfoHash, PsoDataType.UInt32, 0, 0, 0), values, name])]);

        PsoNode root = PsoNode.CreateIncomplete(RootHash);
        Assert.False(root.IsComplete);
        Assert.Throws<InvalidDataException>(() => PsoFile.FromGraph(schema, root));
        root.Complete([
            new(self, new PsoNodeValue(root)),
            new(values, PsoArray.Create([new PsoInteger(7), new PsoInteger(8)])),
            new(name, new PsoText("root"u8))
        ]);
        Assert.True(root.IsComplete);
        Assert.Throws<InvalidOperationException>(() => root.Complete([]));

        PsoNode copy = PsoFile.FromGraph(schema, root).ReadRoot().Root;
        Assert.Same(copy, Assert.IsType<PsoNodeValue>(copy.Fields[0].Value).Node);
        Assert.Equal(root.Fields.Skip(1), copy.Fields.Skip(1));
        Assert.Throws<InvalidOperationException>(() => copy.Complete([]));
        Assert.Throws<ArgumentException>(() => new PsoText("a\0b"u8));
        Assert.Throws<ArgumentException>(() => PsoArray.Create([null!]));
    }

    [Fact]
    public void TextBudgetBoundsPointersToUnterminatedText()
    {
        // Ten string pointers share one 1000-byte block without a terminator, so each decodes to the whole block.
        byte[] root = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(root, new PsoPointer(2, 0).Encode());
        BinaryPrimitives.WriteUInt16BigEndian(root.AsSpan(8), 10);
        byte[] pointers = new byte[80];
        for (int i = 0; i < 10; i++)
            BinaryPrimitives.WriteUInt32BigEndian(pointers.AsSpan(i * 8), new PsoPointer(3, 0).Encode());
        PsoFile file = File([
            new(PsoEntry.ArrayInfoHash, PsoDataType.String, 1, 0, 0),
            new(ArrayHash, PsoDataType.Array, 0, 0, 0)
        ], root, ((uint)PsoDataType.String, pointers), (1, Enumerable.Repeat((byte)'A', 1000).ToArray()));

        PsoArray values = Assert.IsType<PsoArray>(file.ReadRoot(new(maximumTextBytes: 10_000)).Root.Fields[0].Value);
        Assert.All(values.Values, value => Assert.Equal(1000, Assert.IsType<PsoText>(value).Bytes.Length));
        Assert.Throws<InvalidDataException>(() => file.ReadRoot(new(maximumTextBytes: 9_999)));
        PsoDocument isolated = file.ReadRoot(new(continueOnFieldErrors: true, maximumTextBytes: 5_000));
        Assert.IsType<PsoMalformed>(isolated.Root.Fields[0].Value);
        Assert.Contains(isolated.Report.Diagnostics, diagnostic => diagnostic.Code == PsoDiagnosticCodes.FieldDecode);
    }

    [Fact]
    public void EnumSubtypesStoreSignedThirtyTwoSixteenAndEightBitValues()
    {
        // Two 8-bit enums, a 16-bit enum and a 32-bit enum, packed as their subtypes size them.
        PsoEntry[] entries = [
            new(0xA1234571, PsoDataType.Enum, 2, 0, 0), new(0xA1234572, PsoDataType.Enum, 2, 1, 0),
            new(0xA1234573, PsoDataType.Enum, 1, 2, 0), new(0xA1234574, PsoDataType.Enum, 0, 4, 0)
        ];
        byte[] data = [7, 0xFF, 0xFF, 0xFE, 0, 0, 0x01, 0x02];
        PsoFile file = File(entries, data);
        PsoNode root = file.ReadRoot().Root;
        Assert.Equal(new long[] { 7, -1, -2, 0x102 }, root.Fields.Select(field => Assert.IsType<PsoInteger>(field.Value).Value));
        Assert.Equal(new[] { 1, 1, 2, 4 }, entries.Select(file.Schema.ElementStride));

        PsoFile written = PsoFile.FromGraph(file.Schema, root);
        PsoBlock block = written.Blocks[written.RootBlockId - 1];
        Assert.Equal(data, written.Psin.Data.Slice(block.Offset, block.Length).ToArray());

        PsoDocument unknown = File([new(0xA1234571, PsoDataType.Enum, 3, 0, 0)], new byte[4]).ReadRoot();
        Assert.IsType<PsoUnsupported>(unknown.Root.Fields[0].Value);
        Assert.Contains(unknown.Report.Diagnostics, diagnostic => diagnostic.Code == PsoDiagnosticCodes.FieldUnsupported);
    }

    [Fact]
    public void FieldsMatchTheirSchemaEntriesInAnyOrder()
    {
        PsoEntry first = new(0xA1234571, PsoDataType.UInt32, 0, 0, 0), second = new(0xA1234572, PsoDataType.UInt32, 0, 4, 0);
        var schema = new PsoSchema([new PsoStruct(RootHash, 8, [first, second])]);
        PsoField a = new(first, new PsoInteger(1)), b = new(second, new PsoInteger(2));
        byte[] ordered = PsoFile.FromGraph(schema, PsoNode.Create(RootHash, [a, b])).Write();
        Assert.Equal(ordered, PsoFile.FromGraph(schema, PsoNode.Create(RootHash, [b, a])).Write());

        Assert.Equal("PSO fields cannot be null.", Error(schema, [a, null!]));
        Assert.Equal("Duplicate PSO field.", Error(schema, [a, a]));
        Assert.Equal("PSO field 0xA1234572 is missing or has mismatched metadata.", Error(schema, [a]));
        Assert.Equal("PSO node contains fields absent from its schema.", Error(schema, [a, b, new(first with { DataOffset = 2 }, new PsoInteger(3))]));
        // Fields in schema order still collide when the schema repeats an entry.
        Assert.Equal("Duplicate PSO field.", Error(new PsoSchema([new PsoStruct(RootHash, 8, [first, first])]), [a, a]));

        static string Error(PsoSchema schema, PsoField[] fields) =>
            Assert.Throws<InvalidDataException>(() => PsoFile.FromGraph(schema, PsoNode.Create(RootHash, fields))).Message;
    }

    private static PsoFile File(PsoEntry[] entries, byte[] data, params (uint Type, byte[] Data)[] blocks)
    {
        var schema = new PsoSchema([new PsoStruct(RootHash, data.Length, entries)]);
        var root = new PsoBlockBuilder(RootHash);
        root.Append(data);
        var builder = new PsoBuilder(schema) { Root = root };
        builder.Blocks.Add(root);
        foreach (var (type, bytes) in blocks)
        {
            var block = new PsoBlockBuilder(type);
            block.Append(bytes);
            builder.Blocks.Add(block);
        }
        return builder.Build();
    }
}
