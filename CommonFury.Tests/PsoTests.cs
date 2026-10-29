using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using CommonFury.Binary;
using CommonFury.Pso;
using Xunit;

namespace CommonFury.Tests;

// An independently built 12-bit PSO with every scalar type.
public sealed class PsoTests : IDisposable
{
    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "ReferenceData", "pso");
    private readonly JsonDocument referenceJson = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Directory, "reference.json")));
    private JsonElement reference => referenceJson.RootElement;
    public void Dispose() => referenceJson.Dispose();

    [Fact]
    public void ReadsRoundTripsAndRejectsMalformedContainers()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Directory, "all_types.pso"));
        PsoFile file = PsoFile.Read(bytes);
        Assert.Equal(PsoPointerLayout.BlockId12, file.PointerLayout);
        Assert.True(file.Write().AsSpan().SequenceEqual(bytes), "PSO raw roundtrip.");
        Assert.True(file.ValidateChecksum().IsValid, "Known checksum rejected.");
        byte[] schemaBytes = File.ReadAllBytes(Path.Combine(Directory, "schema.psch"));
        Assert.True(file.Schema.Write().AsSpan().SequenceEqual(schemaBytes), "PSCH struct/enum bytes differ from the reference.");
        PsoDocument document = file.ReadRoot();
        CompareNode(document.Root, reference.GetProperty("pso"));
        Assert.True(document.Report.Diagnostics.Count == 1 && document.Report.Diagnostics[0].Code == "pso.field.unsupported", "Unsupported map must be explicit.");
        Assert.True(file.Sections.Any(s => s.Identifier == 0x5A5A5A5A), "Unknown section lost.");
        PsoNode secondRead = file.ReadRoot().Root;
        Assert.True(document.Root.Fields[16].Value.Equals(secondRead.Fields[16].Value), "Text values must compare by content.");
        Assert.True(document.Root.Fields[21].Value.Equals(secondRead.Fields[21].Value), "Scalar arrays must compare by content.");
        bytes[0] = 0;
        Assert.True(file.Write()[0] == 'P', "Reader retained caller-owned mutable input.");
        bytes = file.Write();
        var builder = new PsoBuilder(file.Schema);
        foreach (JsonElement item in reference.GetProperty("builder").GetProperty("blocks").EnumerateArray())
        {
            var block = new PsoBlockBuilder(item.GetProperty("hash").GetUInt32());
            block.Append(item.GetProperty("data").GetBytesFromBase64());
            builder.Blocks.Add(block);
        }
        builder.Root = builder.Blocks[0];
        builder.AdditionalSections.Add(file.Sections.Single(s => s.Identifier == 0x5A5A5A5A));
        Assert.True(builder.Build().Write().AsSpan().SequenceEqual(bytes), "Builder differs from the independent reference bytes.");
        using var stream = new MemoryStream();
        file.Write(stream);
        Assert.True(stream.ToArray().AsSpan().SequenceEqual(bytes) && stream.CanWrite, "Stream output mismatch/ownership.");
        int pmap = FindSection(bytes, PsoSectionId.Pmap), psch = FindSection(bytes, PsoSectionId.Psch);
        foreach (int size in new[] { 0, 3, 7, 8, bytes.Length - 1 })
            Assert.ThrowsAny<InvalidDataException>(() => PsoFile.Read(bytes.AsSpan(0, size)));
        foreach (uint size in new uint[] { 0, 1, 7, uint.MaxValue })
            Assert.ThrowsAny<InvalidDataException>(() => PsoFile.Read(Patch(bytes, 4, size)));
        Assert.ThrowsAny<InvalidDataException>(() => PsoFile.Read([.. bytes, 0]));
        Assert.ThrowsAny<InvalidDataException>(() => PsoFile.Read(Patch(bytes, pmap + 8, 0)));
        Assert.ThrowsAny<InvalidDataException>(() => PsoFile.Read(Patch(bytes, pmap + 20, 0)));
        Assert.ThrowsAny<InvalidDataException>(() => PsoFile.Read(Patch(bytes, pmap + 28, uint.MaxValue)));
        Assert.ThrowsAny<InvalidDataException>(() => PsoFile.Read(Patch(bytes, psch + 8, uint.MaxValue)));
        Assert.ThrowsAny<InvalidDataException>(() => PsoFile.Read(Patch(bytes, psch + 16, 4)));
        Assert.ThrowsAny<InvalidDataException>(() => PsoFile.Read(Patch(bytes, 16 + 104, new PsoPointer(2, 1000).Encode())).ReadRoot());
        Assert.ThrowsAny<InvalidDataException>(() => PsoFile.Read(Patch(bytes, 16 + 144, 0)).ReadRoot());
        Assert.ThrowsAny<InvalidDataException>(() => file.ReadRoot(new(maximumNodes: 1)));
        Assert.ThrowsAny<InvalidDataException>(() => file.ReadRoot(new(maximumArrayElements: 1)));
        byte[] wrongChecksum = Patch(bytes, bytes.Length - 8, 0);
        Assert.True(!PsoFile.Read(wrongChecksum).ValidateChecksum().IsValid, "Bad checksum accepted.");
        Assert.True(PsoFile.Read(wrongChecksum).Write().AsSpan().SequenceEqual(wrongChecksum), "Raw write silently repaired checksum.");
    }

    [Fact]
    public void PointersEncodeWithinTheirLayout()
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new PsoPointer(4096, 0).Encode());
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new PsoPointer(1, 0x100000).Encode());
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new PsoPointer(-1, 0));
        Assert.True(PsoPointer.Decode(uint.MaxValue).Encode() == uint.MaxValue, "Pointer bit boundary.");
        Assert.Equal(uint.MaxValue, PsoPointer.Decode(uint.MaxValue, PsoPointerLayout.BlockId20).Encode(PsoPointerLayout.BlockId20));
        Assert.Equal(new PsoPointer(1, 8), PsoPointer.Decode(0x00800001, PsoPointerLayout.BlockId20));
        Assert.Equal(new PsoPointer(1, 8), PsoPointer.Decode(0x00008001));
        Assert.Equal(0x00800001u, new PsoPointer(1, 8).Encode(PsoPointerLayout.BlockId20));
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new PsoPointer(1, 0x1000).Encode(PsoPointerLayout.BlockId20));
        Assert.Equal(0xFFFFFu, (uint)new PsoPointer(0xFFFFF, 0).Encode(PsoPointerLayout.BlockId20));
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => PsoPointer.Decode(1, (PsoPointerLayout)16));
    }

    [Fact]
    public void PointerGraphs()
    {
        var schema = new PsoSchema([new PsoStruct(0x1234, 8, [new(0x99, PsoDataType.Structure, 3, 0, 0)])]);
        var root = new PsoBlockBuilder(0x1234);
        root.Append(new byte[8]);
        var builder = new PsoBuilder(schema) { Root = root };
        builder.Blocks.Add(root);
        builder.PointerPatches.Add(new(root, 0, root, 0));
        PsoFile file = builder.Build();
        Assert.True(root.Data.SequenceEqual(new byte[8]), "Build mutated caller block while patching pointers.");
        PsoNode node = file.ReadRoot().Root;
        Assert.True(node.Fields[0].Value is PsoNodeValue value && ReferenceEquals(value.Node, node), "Pointer cycle identity lost.");
        Assert.True(file.ValidateChecksum().IsValid, "Patched checksum mismatch.");
        builder.PointerPatches.Add(new(root, 1, root, 0));
        Assert.ThrowsAny<InvalidOperationException>(() => builder.Build());
        builder.PointerPatches.RemoveAt(1);
        builder.BlockAlignment = 3;
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => builder.Build());
        builder.BlockAlignment = 16;
        builder.Blocks.Add(root);
        Assert.ThrowsAny<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void PointerPatchErrorsAreReportedInPatchOrder()
    {
        var schema = new PsoSchema([new PsoStruct(0x1234, 8, [new(0x99, PsoDataType.Structure, 3, 0, 0)])]);
        var root = new PsoBlockBuilder(0x1234);
        root.Append(new byte[16]);
        var other = new PsoBlockBuilder(0x1234);
        other.Append(new byte[8]);
        var stray = new PsoBlockBuilder(0x1234);
        var builder = new PsoBuilder(schema) { Root = root };
        builder.Blocks.AddRange([root, other]);
        // Equal offsets in different blocks and starts four bytes apart do not overlap.
        builder.PointerPatches.AddRange([new(root, 4, other, 0), new(other, 4, root, 0), new(root, 0, root, 0), new(root, 12, root, 0)]);
        Assert.True(builder.Build().ValidateChecksum().IsValid);
        builder.PointerPatches.AddRange([new(root, 10, root, 0), new(stray, 0, root, 0)]);
        Assert.Equal("Overlapping pointer patches.", Assert.Throws<InvalidOperationException>(() => builder.Build()).Message);
        builder.PointerPatches.Insert(4, new(root, 13, root, 0));
        Assert.Equal("Pointer patch is outside its block.", Assert.Throws<InvalidOperationException>(() => builder.Build()).Message);

        var page = new PsoBlockBuilder(0x1234);
        page.Append(new byte[0x1010]);
        var wide = new PsoBuilder(schema) { Root = page, PointerLayout = PsoPointerLayout.BlockId20 };
        wide.Blocks.Add(page);
        wide.PointerPatches.AddRange([new(page, 0, page, 0), new(page, 2, page, 0), new(page, 8, page, 0x1000)]);
        Assert.Equal("Overlapping pointer patches.", Assert.Throws<InvalidOperationException>(() => wide.Build()).Message);
        wide.PointerPatches.RemoveAt(1);
        wide.PointerPatches.Add(new(page, 10, page, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => wide.Build());
    }

    [Fact]
    public void ChecksumValidationReportsSizeAndValueSeparately()
    {
        var schema = new PsoSchema([new PsoStruct(0x1234, 4, [new(0x99, PsoDataType.UInt32, 0, 0, 0)])]);
        var root = new PsoBlockBuilder(0x1234);
        root.Append([1, 2, 3, 4]);
        var builder = new PsoBuilder(schema) { Root = root };
        builder.Blocks.Add(root);
        builder.AdditionalSections.Add(new PsoSection([0x5A, 0x5A, 0x5A, 0x5A, 0, 0, 0, 9, 7]));
        byte[] bytes = builder.Build().Write();
        Assert.Equal([PsoDiagnosticCodes.ChecksumSize],
            PsoFile.Read(Patch(bytes, bytes.Length - 12, 1)).ValidateChecksum().Diagnostics.Select(d => d.Code));
        Assert.Equal([PsoDiagnosticCodes.ChecksumMismatch],
            PsoFile.Read(Patch(bytes, bytes.Length - 8, 1)).ValidateChecksum().Diagnostics.Select(d => d.Code));
        Assert.Equal([PsoDiagnosticCodes.ChecksumMismatch],
            PsoFile.Read(Patch(bytes, 16, 0x05060708)).ValidateChecksum().Diagnostics.Select(d => d.Code));
        // The payload byte of the section before CHKS.
        byte[] changed = (byte[])bytes.Clone();
        changed[^21] ^= 1;
        Assert.Equal([PsoDiagnosticCodes.ChecksumMismatch], PsoFile.Read(changed).ValidateChecksum().Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void BlockPaddingFillsAlignmentGaps()
    {
        var schema = new PsoSchema([new PsoStruct(0x1234, 4, [new(0x99, PsoDataType.UInt32, 0, 0, 0)])]);
        var root = new PsoBlockBuilder(0x1234);
        root.Append([1, 2, 3, 4]);
        var tail = new PsoBlockBuilder(0x1234);
        tail.Append([5, 6, 7, 8]);
        var builder = new PsoBuilder(schema) { Root = root, BlockPadding = 0x70 };
        builder.Blocks.AddRange([root, tail]);
        byte[] bytes = builder.Build().Write();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes[16..20]);
        Assert.All(bytes[20..32], value => Assert.Equal(0x70, value));
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, bytes[32..36]);
        PsoFile file = PsoFile.Read(bytes);
        Assert.True(file.ValidateChecksum().IsValid, "Padded checksum mismatch.");
        Assert.Equal(0x1234u, file.ReadRoot().Root.TypeHash);
    }

    [Fact]
    public void SliceBorrowsBlockRelativeBytesWithinTheBlock()
    {
        var schema = new PsoSchema([new PsoStruct(0x1234, 8, [new(0x99, PsoDataType.Int64, 0, 0, 0)])]);
        var root = new PsoBlockBuilder(0x1234);
        root.Append([1, 2, 3, 4, 5, 6, 7, 8]);
        var builder = new PsoBuilder(schema) { Root = root };
        builder.Blocks.Add(root);
        PsoFile file = builder.Build();

        PsoBlock block = file.Block(file.RootBlockId);
        Assert.Equal(PsoSectionId.Psin, file.Psin.Identifier);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, file.Psin.Data.Slice(block.Offset, block.Length).ToArray());
        Assert.Equal(new byte[] { 3, 4, 5 }, file.Slice(new(1, 2), 3).ToArray());
        Assert.True(file.Slice(new(1, 8), 0).IsEmpty);
        Assert.Throws<InvalidDataException>(() => file.Slice(new(1, 6), 3).Length);
        Assert.Throws<InvalidDataException>(() => file.Slice(new(1, 9), 0).Length);
        Assert.Throws<InvalidDataException>(() => file.Slice(new(1, 0), -1).Length);
        Assert.Throws<InvalidDataException>(() => file.Slice(default, 0).Length);
        Assert.Throws<InvalidDataException>(() => file.Block(2));
    }

    [Fact]
    public void UnknownSchemas()
    {
        byte[] raw = new byte[24];
        var writer = new BinarySpanWriter(raw, ByteOrder.Big);
        writer.WriteUInt32(PsoSectionId.Psch);
        writer.WriteUInt32(24);
        writer.WriteUInt32(1);
        writer.WriteUInt32(0x1234);
        writer.WriteUInt32(20);
        writer.WriteUInt32(0x99010203);
        PsoSchema schema = PsoSchema.Read(raw);
        Assert.True(schema.Types[0] is PsoOpaqueSchema opaque && opaque.Data.SequenceEqual(raw.AsSpan(20)), "Unknown schema discarded.");
        Assert.ThrowsAny<NotSupportedException>(() => schema.Write());
        var preserved = new PsoSchema([new PsoStruct(1, 0, [], reservedByte: 9, reservedWord: 0x99887766)]);
        PsoStruct parsed = (PsoStruct)PsoSchema.Read(preserved.Write()).Types[0];
        Assert.True(parsed.ReservedByte == 9 && parsed.ReservedWord == 0x99887766, "Schema reserved values lost.");
    }

    [Fact]
    public void HashStringTablesResolveNames()
    {
        var names = new[] { new CommonFury.Hashing.Hash32("CMapData"), new CommonFury.Hashing.Hash32("entities") };
        var schema = new PsoSchema([new PsoStruct(0x1234, 4, [new(0x99, PsoDataType.UInt32, 0, 0, 0)])]);
        var root = new PsoBlockBuilder(0x1234);
        root.Append(new byte[4]);
        var builder = new PsoBuilder(schema) { Root = root };
        builder.Blocks.Add(root);
        builder.AdditionalSections.Add(PsoHashStrings.Write(PsoSectionId.Strf, names[..1]));
        builder.AdditionalSections.Add(PsoHashStrings.Write(PsoSectionId.Strs, names));
        PsoFile file = builder.Build();
        IPsoNameResolver resolver = PsoNameTable.Read(file);
        Assert.Equal("CMapData", resolver.ResolveName(names[0].Value));
        Assert.Equal("entities", resolver.ResolveName(names[1].Value));
        Assert.Null(resolver.ResolveName(0x1234));
        Assert.Throws<InvalidOperationException>(() => PsoHashStrings.Write(PsoSectionId.Strf, [new(5u)]));
    }

    private static void CompareNode(PsoNode node, JsonElement expected)
    {
        Assert.True(node.TypeHash == expected.GetProperty("type").GetUInt32(), "PSO type hash mismatch.");
        Assert.True(node.Fields.Count == expected.GetProperty("fields").EnumerateObject().Count(), "PSO field count mismatch.");
        foreach (PsoField field in node.Fields)
            CompareValue(field.Value, expected.GetProperty("fields").GetProperty($"hash_{field.Entry.NameHash:X8}"));
    }

    private static void CompareValue(PsoValue value, JsonElement expected)
    {
        switch (value)
        {
            case PsoBoolean b:
                Assert.True(b.Value == expected.GetBoolean(), "PSO bool");
                break;
            case PsoInteger i:
                Assert.True(i.Value == expected.GetInt64(), "PSO integer");
                break;
            case PsoFloat f:
                NumericAssert.Near(f.Value, expected.GetDouble(), "PSO float");
                break;
            case PsoHalf h:
                Assert.True(h.Bits == expected.GetUInt16(), "PSO half bits");
                break;
            case PsoVector2 v:
                Near([v.Value.X, v.Value.Y], expected.GetProperty("vector"), "PSO vector2");
                break;
            case PsoVector3 v:
                Near([v.Value.X, v.Value.Y, v.Value.Z], expected.GetProperty("vector"), "PSO vector3");
                break;
            case PsoVector4 v:
                Near([v.Value.X, v.Value.Y, v.Value.Z, v.Value.W], expected.GetProperty("vector"), "PSO vector4");
                break;
            case PsoHash h:
                Assert.True(h.Value.Value == expected.GetProperty("hash").GetUInt32(), "PSO hash");
                break;
            case PsoText t:
                Assert.True(t.Decode(Encoding.UTF8) == expected.GetString(), "PSO text");
                break;
            case PsoNodeValue n:
                CompareNode(n.Node, expected);
                break;
            case PsoArray a:
                Assert.True(a.Values.Count == expected.GetArrayLength(), "PSO array count");
                for (int i = 0; i < a.Values.Count; i++)
                    CompareValue(a.Values[i], expected[i]);
                break;
            case PsoNull or PsoUnsupported:
                Assert.True(expected.ValueKind == JsonValueKind.Null, "PSO absent/unsupported value");
                break;
            default:
                throw new InvalidOperationException("Unexpected PSO value in reference corpus.");
        }
    }

    private static void Near(float[] values, JsonElement expected, string label)
    {
        for (int i = 0; i < values.Length; i++)
            NumericAssert.Near(values[i], expected[i].GetDouble(), label);
    }

    internal static int FindSection(byte[] bytes, uint identifier)
    {
        for (int offset = 0; offset < bytes.Length; offset += checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 4))))
            if (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)) == identifier)
                return offset;
        throw new InvalidOperationException("Section missing from fixture.");
    }

    internal static byte[] Patch(byte[] bytes, int offset, uint value)
    {
        byte[] copy = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(offset), value);
        return copy;
    }
}
