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
