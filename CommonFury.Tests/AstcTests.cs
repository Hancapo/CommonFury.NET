using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using CommonFury.Textures;
using Xunit;

namespace CommonFury.Tests;

public sealed class AstcTests
{
    // Expected hashes are of decodes by ARM astcenc 5.7.0, the reference decoder: random 128-bit blocks (including error and
    // void-extent encodings) at every 2D footprint and profile, and astcenc's own encodings of synthetic images.
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "ReferenceData", "astc");
    private static readonly JsonElement Manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "astc.json"))).RootElement;

    public static TheoryData<string, string> RandomCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var item in Manifest.GetProperty("random").EnumerateArray())
            data.Add(item.GetProperty("footprint").GetString()!, item.GetProperty("profile").GetString()!);
        return data;
    }

    public static TheoryData<string> EncodedCases()
    {
        var data = new TheoryData<string>();
        foreach (var item in Manifest.GetProperty("encoded").EnumerateArray())
            data.Add(item.GetProperty("file").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(RandomCases))]
    public void RandomBlocksMatchTheReferenceDecoder(string footprint, string profile)
    {
        var item = Manifest.GetProperty("random").EnumerateArray()
            .Single(entry => entry.GetProperty("footprint").GetString() == footprint && entry.GetProperty("profile").GetString() == profile);
        int[] size = [.. footprint.Split('x').Select(int.Parse)];
        var format = new AstcFormat(size[0], size[1], Enum.Parse<AstcProfile>(profile));
        byte[] blocks = File.ReadAllBytes(Path.Combine(Root, "random-blocks.bin"));
        Assert.Equal(item.GetProperty("sha256").GetString(), Hash(DecodeFor(format, blocks, item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32())));
    }

    [Theory]
    [MemberData(nameof(EncodedCases))]
    public void EncodedImagesMatchTheReferenceDecoder(string file)
    {
        var item = Manifest.GetProperty("encoded").EnumerateArray().Single(entry => entry.GetProperty("file").GetString() == file);
        byte[] data = File.ReadAllBytes(Path.Combine(Root, file));
        Assert.Equal(0x5CA1AB13u, BinaryPrimitives.ReadUInt32LittleEndian(data));
        int width = data[7] | data[8] << 8 | data[9] << 16, height = data[10] | data[11] << 8 | data[12] << 16;
        Assert.Equal((item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32()), (width, height));
        var format = new AstcFormat(data[4], data[5], Enum.Parse<AstcProfile>(item.GetProperty("profile").GetString()!));
        Assert.Equal(item.GetProperty("sha256").GetString(), Hash(DecodeFor(format, data[16..], width, height)));
    }

    [Fact]
    public void DecodesVoidExtentBlocksByHand()
    {
        // LDR void extent with all-ones coordinates and RGBA 0x1234, 0xFF80, 0x00FF, 0x8000.
        byte[] block = VoidExtent(hdr: false, 0x1234, 0xFF80, 0x00FF, 0x8000);
        byte[] pixels = Astc.Decode(block, 4, 4, new AstcFormat(4, 4, AstcProfile.Ldr));
        for (int i = 0; i < 16; i++)
            Assert.Equal(new byte[] { 0x12, 0xFF, 0x00, 0x80 }, pixels.AsSpan(i * 4, 4).ToArray());
        // A partial edge block writes only the texels inside the image.
        Assert.Equal(new byte[] { 0x12, 0xFF, 0x00, 0x80 }, Astc.Decode(block, 1, 1, new AstcFormat(12, 12, AstcProfile.LdrSrgb)));

        // HDR void extent stores half floats directly; LDR profiles reject it with the error colour.
        byte[] hdr = VoidExtent(hdr: true, 0x3C00, 0x4000, 0x0000, 0x3800);
        byte[] half = Astc.DecodeHalf(hdr, 4, 4, new AstcFormat(4, 4, AstcProfile.Hdr));
        Assert.Equal(new ushort[] { 0x3C00, 0x4000, 0x0000, 0x3800 }, Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(half.AsSpan(i * 2))));
        Assert.Equal(new byte[] { 0xFF, 0x00, 0xFF, 0xFF }, Astc.Decode(hdr, 4, 4, new AstcFormat(4, 4, AstcProfile.Ldr))[..4]);

        // Reserved bits 10 and 11 must both be set.
        block[1] &= 0xF3;
        Assert.Equal(new byte[] { 0xFF, 0x00, 0xFF, 0xFF }, Astc.Decode(block, 4, 4, new AstcFormat(4, 4, AstcProfile.Ldr))[..4]);
    }

    [Fact]
    public void ValidatesFormatsAndLengths()
    {
        Assert.Throws<ArgumentException>(() => new AstcFormat(7, 7, AstcProfile.Ldr));
        Assert.Throws<ArgumentException>(() => new AstcFormat(4, 5, AstcProfile.Ldr));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AstcFormat(4, 4, (AstcProfile)9));
        var format = new AstcFormat(8, 8, AstcProfile.LdrSrgb);
        Assert.Equal(new TextureElement(16, 8, 8), format.Element);
        Assert.Equal(2 * 2 * 16, format.SurfaceSize(9, 16));
        Assert.Throws<ArgumentException>(() => Astc.Decode(new byte[48], 9, 16, format));
        Assert.Throws<ArgumentException>(() => Astc.Decode(new byte[64], 9, 16, format, new byte[9 * 16 * 4 - 1]));
        Assert.Throws<ArgumentException>(() => Astc.Decode(new byte[16], 4, 4, new AstcFormat(4, 4, AstcProfile.Hdr)));
        Assert.Throws<ArgumentException>(() => Astc.DecodeHalf(new byte[16], 4, 4, new AstcFormat(4, 4, AstcProfile.Ldr)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Astc.Decode(new byte[16], 0, 4, format));
    }

    private static byte[] DecodeFor(AstcFormat format, byte[] blocks, int width, int height) =>
        format.Profile == AstcProfile.Hdr ? Astc.DecodeHalf(blocks, width, height, format) : Astc.Decode(blocks, width, height, format);

    private static byte[] VoidExtent(bool hdr, ushort r, ushort g, ushort b, ushort a)
    {
        var block = new byte[16];
        ulong header = 0x1FC | (hdr ? 0x200UL : 0) | 3UL << 10 | ((1UL << 52) - 1) << 12;
        BinaryPrimitives.WriteUInt64LittleEndian(block, header);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), r);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(10), g);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(12), b);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(14), a);
        return block;
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
