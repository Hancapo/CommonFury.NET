using System.Buffers.Binary;
using System.Numerics;
using CommonFury.Vertices;
using Xunit;

namespace CommonFury.Tests;

public sealed class VertexTests
{
    private static readonly VertexElementFormat[] DxgiFormats = Enum.GetValues<VertexElementFormat>().Where(f => f != VertexElementFormat.Half3).ToArray();

    [Fact]
    public void SemanticsUseTheSharedDeclarationSlots()
    {
        // Declaration slot numbers.
        Assert.Equal(new byte[] { 0, 4, 8, 9, 12, 16, 17, 20, 21, 24, 25, 26, 28, 29, 30, 31, 32, 33, 34, 35 },
            Enum.GetValues<VertexSemantic>().Select(s => (byte)s));
    }

    [Fact]
    public void DxgiIdentifiersRoundTripAndRgb10A2RangeIsExplicit()
    {
        foreach (VertexElementFormat format in DxgiFormats)
        {
            Rgb10A2Range range = format == VertexElementFormat.BiasedUNorm10x3A2 ? Rgb10A2Range.MinusOneToOne : Rgb10A2Range.ZeroToOne;
            Assert.Equal(format, VertexElementFormats.FromDxgi(VertexElementFormats.ToDxgi(format), range));
        }
        Assert.Equal(VertexElementFormat.UNorm10x3A2, VertexElementFormats.FromDxgi(24, Rgb10A2Range.ZeroToOne));
        Assert.Equal(VertexElementFormat.BiasedUNorm10x3A2, VertexElementFormats.FromDxgi(24, Rgb10A2Range.MinusOneToOne));
        Assert.Throws<ArgumentOutOfRangeException>(() => VertexElementFormats.FromDxgi(24, (Rgb10A2Range)7));
        Assert.False(VertexElementFormats.TryFromDxgi(253, Rgb10A2Range.ZeroToOne, out _));
        Assert.Throws<InvalidDataException>(() => VertexElementFormats.FromDxgi(253, Rgb10A2Range.ZeroToOne));
        Assert.Throws<InvalidOperationException>(() => VertexElementFormats.ToDxgi(VertexElementFormat.Half3));
        // DXGI identifiers and element sizes.
        foreach (var (dxgi, size) in new (byte, int)[] { (2, 16), (6, 12), (10, 8), (16, 8), (24, 4), (28, 4), (30, 4), (34, 4) })
            Assert.Equal(size, VertexElementFormats.Size(VertexElementFormats.FromDxgi(dxgi, Rgb10A2Range.MinusOneToOne)));
    }

    [Fact]
    public void ElementsRoundTrip()
    {
        Vector4 normalized = new(0.25f, 0.5f, 0.75f, 1);
        Vector4 signed = new(-1, -0.5f, 0.5f, 1);
        foreach (VertexElementFormat format in Enum.GetValues<VertexElementFormat>())
        {
            Vector4 value = format switch
            {
                VertexElementFormat.Byte4 => new(0, 7, 128, 255),
                VertexElementFormat.Int16x2 or VertexElementFormat.Int16x4 => new(-32768, 5, -7, 32767),
                VertexElementFormat.SNorm8x4 or VertexElementFormat.BiasedUNorm10x3A2 => signed,
                _ => normalized
            };
            var bytes = new byte[VertexElementFormats.Size(format)];
            VertexElementCodec.Write(bytes, format, value);
            Vector4 decoded = VertexElementCodec.Read(bytes, format);
            int lanes = format switch
            {
                VertexElementFormat.Float or VertexElementFormat.Half or VertexElementFormat.UNorm16 => 1,
                VertexElementFormat.Float2 or VertexElementFormat.Half2 or VertexElementFormat.UNorm16x2 or VertexElementFormat.UNorm8x2 or VertexElementFormat.Int16x2 => 2,
                VertexElementFormat.Float3 or VertexElementFormat.Half3 => 3,
                _ => 4
            };
            for (int lane = 0; lane < 4; lane++)
                Assert.Equal(lane < lanes ? value[lane] : 0, decoded[lane], 5e-3f);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => VertexElementCodec.Write(new byte[4], VertexElementFormat.UNorm8x4, new(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => VertexElementCodec.Write(new byte[4], VertexElementFormat.BiasedUNorm10x3A2, new(float.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(() => VertexElementCodec.Write(new byte[4], VertexElementFormat.Byte4, new(0.5f)));
    }

    [Fact]
    public void Rgb10A2RangesDecodeTheSameBitsDifferently()
    {
        byte[] zero = new byte[4], ones = [0xFF, 0xFF, 0xFF, 0xFF];
        Assert.Equal(Vector4.Zero, VertexElementCodec.Read(zero, VertexElementFormat.UNorm10x3A2));
        Assert.Equal(new Vector4(-1), VertexElementCodec.Read(zero, VertexElementFormat.BiasedUNorm10x3A2));
        Assert.Equal(Vector4.One, VertexElementCodec.Read(ones, VertexElementFormat.UNorm10x3A2));
        Assert.Equal(Vector4.One, VertexElementCodec.Read(ones, VertexElementFormat.BiasedUNorm10x3A2));
    }

    [Fact]
    public void BiasedAndUNorm8MatchReferenceCodec()
    {
        var random = new Random(3);
        var bytes = new byte[4];
        for (int i = 0; i < 2000; i++)
        {
            uint bits = (uint)random.NextInt64(0, 1L << 32);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, bits);
            Assert.Equal(ReferencePackedNormal(bits), VertexElementCodec.Read(bytes, VertexElementFormat.BiasedUNorm10x3A2));
            Assert.Equal(new Vector4(bytes[0], bytes[1], bytes[2], bytes[3]) / 255f, VertexElementCodec.Read(bytes, VertexElementFormat.UNorm8x4));
            Vector4 value = new(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1);
            VertexElementCodec.Write(bytes, VertexElementFormat.BiasedUNorm10x3A2, value);
            Assert.Equal(ReferenceEncodePackedNormal(value), BinaryPrimitives.ReadUInt32LittleEndian(bytes));
            Vector4 unit = (value + Vector4.One) / 2;
            VertexElementCodec.Write(bytes, VertexElementFormat.UNorm8x4, unit);
            for (int lane = 0; lane < 4; lane++)
                Assert.Equal((byte)MathF.Round((unit[lane] - 0) / (1 - 0) * 255, MidpointRounding.ToEven), bytes[lane]);
        }
    }

    [Fact]
    public void InterleavedDeclarationMatchesReferenceLayout()
    {
        (VertexSemantic, VertexElementFormat, int)[] elements =
            [(VertexSemantic.Position, VertexElementFormat.Float3, 0), (VertexSemantic.Normal, VertexElementFormat.UNorm10x3A2, 12),
             (VertexSemantic.Color0, VertexElementFormat.UNorm8x4, 16), (VertexSemantic.TexCoord0, VertexElementFormat.Half2, 20)];
        var declaration = Gen9VertexDeclaration.Interleaved(24, elements, vertexCount: 1000);
        byte[] bytes = declaration.ToArray();
        Assert.Equal(InterleavedReference(24, elements, 1000), bytes);
        Assert.True(declaration.IsInterleaved);
        var read = Gen9VertexDeclaration.Read(bytes);
        Assert.Equal((24, 1000), (read.Stride, read.VertexCount));
        Assert.Equal(declaration.Slots, read.Slots);
        Assert.True(read.TryFind(VertexSemantic.Normal, out Gen9VertexSlot normal));
        Assert.True(normal.TryGetFormat(Rgb10A2Range.ZeroToOne, out VertexElementFormat format) && format == VertexElementFormat.UNorm10x3A2);
        Assert.Throws<ArgumentException>(() => Gen9VertexDeclaration.Interleaved(16, elements));
    }

    [Fact]
    public void PlanarDeclarationMatchesReferenceWriter()
    {
        (VertexSemantic, VertexElementFormat)[] elements =
            [(VertexSemantic.Position, VertexElementFormat.Float3), (VertexSemantic.Normal, VertexElementFormat.BiasedUNorm10x3A2), (VertexSemantic.TexCoord3, VertexElementFormat.Float2)];
        var declaration = Gen9VertexDeclaration.Planar(3, elements);
        Assert.Equal(24, declaration.Stride);
        Assert.False(declaration.IsInterleaved);
        Assert.Equal(new uint[] { 0, 36, 48 }, declaration.Slots.Select(s => s.Offset));
        Assert.Equal(PlanarReference(3, [(0, 12, 6), (4, 4, 24), (31, 8, 16)], 24ul << 2), declaration.ToArray());
        // A preserved packed word keeps its stride and unknown bits; only the count changes.
        ulong metadata = (32ul << 2) | 3 | (0xABul << 40);
        var preserved = Gen9VertexDeclaration.Planar(7, elements, metadata);
        Assert.Equal(PlanarReference(7, [(0, 12, 6), (4, 4, 24), (31, 8, 16)], metadata), preserved.ToArray());
        Assert.Throws<ArgumentException>(() => Gen9VertexDeclaration.Planar(7, elements, 8ul << 2));
    }

    [Fact]
    public void ReadPreservesPackedBitsAndRejectsInvalidSlots()
    {
        byte[] bytes = new byte[Gen9VertexDeclaration.Size];
        bytes[260 + 51] = 253;
        bytes[208 + 51] = 9;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(312), 0xFFFF_FFFF_0000_0003ul | (9ul << 2) | (5ul << 10));
        var read = Gen9VertexDeclaration.Read(bytes);
        Gen9VertexSlot slot = Assert.Single(read.Slots);
        Assert.Equal(((VertexSemantic)51, (byte)253), (slot.Semantic, slot.Format));
        Assert.False(slot.TryGetFormat(Rgb10A2Range.ZeroToOne, out _));
        Assert.Equal(bytes, read.ToArray());
        Assert.Equal(6, read.WithVertexCount(6).VertexCount);
        Assert.Equal(read.Packed & ~(0x3FFFFFul << 10), read.WithVertexCount(6).Packed & ~(0x3FFFFFul << 10));
        bytes[100] = 0xEE; // offset byte of an unused slot: kept by the in-place count update
        Gen9VertexDeclaration.ReplaceVertexCount(bytes, 0x3FFFFF);
        Assert.Equal(0xEE, bytes[100]);
        Assert.Equal(0x3FFFFF, Gen9VertexDeclaration.Read(bytes).VertexCount);
        Assert.Throws<InvalidDataException>(() => Gen9VertexDeclaration.Read(new byte[0x13F]));
        Assert.Throws<ArgumentException>(() => new Gen9VertexDeclaration([new((VertexSemantic)52, 0, 4, 2)], 0));
        Assert.Throws<ArgumentException>(() => new Gen9VertexDeclaration([new(VertexSemantic.Position, 0, 4, 2), new(VertexSemantic.Position, 0, 4, 2)], 0));
        Assert.Throws<ArgumentException>(() => new Gen9VertexDeclaration([new(VertexSemantic.Position, 0, 4, 0)], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gen9VertexDeclaration.Pack(8, 0x400000));
    }

    // Independent decoder of biased 10/10/10/2 normals, used as the oracle.
    private static Vector4 ReferencePackedNormal(uint value) => new(
        (value & 1023) * (2f / 1023) - 1, (value >> 10 & 1023) * (2f / 1023) - 1,
        (value >> 20 & 1023) * (2f / 1023) - 1, (value >> 30) * (2f / 3) - 1);

    // Independent encoder of biased 10/10/10/2 normals, used as the oracle.
    private static uint ReferenceEncodePackedNormal(Vector4 value) =>
        Quantize(value.X, -1, 1, 1023) | Quantize(value.Y, -1, 1, 1023) << 10 | Quantize(value.Z, -1, 1, 1023) << 20 | Quantize(value.W, -1, 1, 3) << 30;

    private static uint Quantize(float value, float minimum, float maximum, int steps) =>
        (uint)MathF.Round((value - minimum) / (maximum - minimum) * steps, MidpointRounding.ToEven);

    // Independent interleaved declaration writer used as the oracle.
    private static byte[] InterleavedReference(int stride, (VertexSemantic Semantic, VertexElementFormat Format, int Offset)[] elements, int vertexCount)
    {
        byte[] data = new byte[0x140];
        foreach (var element in elements)
        {
            int slot = (byte)element.Semantic;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(slot * 4), (uint)element.Offset);
            data[208 + slot] = (byte)stride;
            data[260 + slot] = VertexElementFormats.ToDxgi(element.Format);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(312), ((ulong)stride << 2) | ((ulong)vertexCount << 10));
        return data;
    }

    // Independent planar declaration writer (zeroed storage) used as the oracle.
    private static byte[] PlanarReference(int vertexCount, (int Slot, byte Size, byte Format)[] elements, ulong metadata)
    {
        byte[] data = new byte[0x140];
        int offset = 0;
        foreach (var element in elements)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(element.Slot * 4), (uint)offset);
            data[208 + element.Slot] = element.Size;
            data[260 + element.Slot] = element.Format;
            offset = checked(offset + vertexCount * element.Size);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(312), (metadata & ~(0x3FFFFFul << 10)) | ((ulong)vertexCount << 10));
        return data;
    }
}
