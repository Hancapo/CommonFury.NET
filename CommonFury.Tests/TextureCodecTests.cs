using System.Buffers.Binary;
using CommonFury.Textures;
using Xunit;

namespace CommonFury.Tests;

public sealed class TextureCodecTests
{
    public static TheoryData<TextureFormat, Bc1AlphaMode, float> ParallelCases()
    {
        var cases = new TheoryData<TextureFormat, Bc1AlphaMode, float>();
        TextureFormat[] formats =
        [
            TextureFormat.BC1Unorm, TextureFormat.BC1UnormSrgb, TextureFormat.BC2Unorm, TextureFormat.BC3Unorm,
            TextureFormat.BC3UnormSrgb, TextureFormat.BC4Unorm, TextureFormat.BC5Unorm, TextureFormat.BC7Unorm, TextureFormat.BC7UnormSrgb,
        ];
        foreach (float quality in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            foreach (TextureFormat format in formats)
                cases.Add(format, Bc1AlphaMode.Opaque, quality);
            cases.Add(TextureFormat.BC1Unorm, Bc1AlphaMode.PunchThrough, quality);
        }
        return cases;
    }

    [Theory]
    [MemberData(nameof(ParallelCases))]
    public void ParallelEncodeMatchesSerialEncodeByteForByte(TextureFormat format, Bc1AlphaMode alpha, float quality)
    {
        // 66 x 1030: the first two mips split into several strips of block rows, with partial edge blocks in both directions.
        const int width = 66, height = 1030;
        var rgba = new byte[width * height * 4];
        new Random(11).NextBytes(rgba);
        var image = new RgbaImage(width, height, rgba);
        var options = new TextureEncodingOptions { Format = format, Bc1Alpha = alpha, Quality = quality, MinimumMipDimension = 4 };

        TextureSurface serial = TextureCodec.Encode(image, options with { MaxDegreeOfParallelism = 1 });
        TextureSurface parallel = TextureCodec.Encode(image, options);
        TextureSurface capped = TextureCodec.Encode(image, options with { MaxDegreeOfParallelism = 3 });

        Assert.Equal(serial.MipCount, parallel.MipCount);
        Assert.True(serial.Data.Span.SequenceEqual(parallel.Data.Span));
        Assert.True(serial.Data.Span.SequenceEqual(capped.Data.Span));
    }

    [Theory]
    [InlineData(TextureFormat.BC1Unorm, 8)]
    [InlineData(TextureFormat.BC3Unorm, 16)]
    [InlineData(TextureFormat.BC5Unorm, 16)]
    [InlineData(TextureFormat.BC7Unorm, 16)]
    public void LargeBlockCompressedMipsDecodeExactlyLikeTheirBlockRowsAlone(TextureFormat format, int blockBytes)
    {
        // 600 x 1030: strip-decoded (>= 512 x 512), with a partial last block row.
        const int width = 600, height = 1030;
        var rgba = new byte[width * height * 4];
        new Random(7).NextBytes(rgba);
        TextureSurface surface = TextureCodec.Encode(new RgbaImage(width, height, rgba), new() { Format = format, GenerateMipmaps = false, Quality = 0 });
        byte[] whole = TextureCodec.Decode(surface).Pixels.ToArray();

        // Each run of 16 block rows, decoded as its own small surface (serial path), must match the same rows.
        ReadOnlyMemory<byte> encoded = surface.GetEncodedSlice();
        int pitch = (width + 3) / 4 * blockBytes, blockRows = (height + 3) / 4;
        Assert.Equal(pitch * blockRows, encoded.Length);
        for (int first = 0; first < blockRows; first += 16)
        {
            int rows = Math.Min(16, blockRows - first), y = first * 4, h = Math.Min(rows * 4, height - y);
            var part = new TextureSurface(width, h, format, 1, encoded.Slice(first * pitch, rows * pitch));
            Assert.True(TextureCodec.Decode(part).Pixels.SequenceEqual(whole.AsSpan(y * width * 4, h * width * 4)), $"rows {y}..{y + h}");
        }
    }

    [Theory]
    [InlineData(TextureFormat.BC1Unorm)]
    [InlineData(TextureFormat.BC2Unorm)]
    [InlineData(TextureFormat.BC3Unorm)]
    [InlineData(TextureFormat.BC4Unorm)]
    [InlineData(TextureFormat.BC5Unorm)]
    [InlineData(TextureFormat.BC7UnormSrgb)]
    public void EncodedImagesRoundtripThroughDds(TextureFormat format)
    {
        byte[] pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 192; pixels[i + 1] = 64; pixels[i + 2] = 32; pixels[i + 3] = 255; }
        var image = new RgbaImage(8, 8, pixels);
        Array.Clear(pixels);
        TextureSurface surface = TextureCodec.Encode(image, new() { Format = format });
        Assert.Equal(4, surface.MipCount);
        Assert.Equal(format, surface.Format);
        TextureSurface reread = DdsCodec.Read(DdsCodec.Write(surface));
        Assert.Equal(surface.Data.ToArray(), reread.Data.ToArray());
        RgbaImage decoded = TextureCodec.Decode(reread);
        Assert.Equal(8, decoded.Width);
        Assert.Equal(8, decoded.Height);
        Assert.InRange(decoded.Pixels[0], (byte)180, (byte)204);
        Assert.Equal(1, TextureCodec.Decode(reread, 3).Width);
        Assert.False(decoded.HasTransparency);
    }

    [Fact]
    public void FirstMipIsKeptAndTheRestAreGenerated()
    {
        var rgba = new byte[16 * 16 * 4];
        new Random(3).NextBytes(rgba);
        var image = new RgbaImage(16, 16, rgba);
        var options = new TextureEncodingOptions { Format = TextureFormat.BC3Unorm, Quality = 0.5f };
        TextureSurface full = TextureCodec.Encode(image, options);
        byte[] firstMip = Enumerable.Range(0, full.Mips[0].Length).Select(i => (byte)i).ToArray();

        TextureSurface kept = TextureCodec.Encode(image, options, firstMip);

        Assert.Equal(full.MipCount, kept.MipCount);
        Assert.Equal(firstMip, kept.Data.Span[..full.Mips[0].Length].ToArray());
        Assert.Equal(full.Data.Span[full.Mips[0].Length..].ToArray(), kept.Data.Span[full.Mips[0].Length..].ToArray());
        Assert.Throws<ArgumentException>(() => TextureCodec.Encode(image, options, firstMip.AsSpan(1)));
        Assert.Throws<ArgumentException>(() => TextureCodec.Encode(image, options, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Bc1DecodesKnownPalettesAndTransparentTexelsToZero()
    {
        // Red/blue four-colour block: indices 0,1,2,3 repeated per row.
        var opaque = new TextureSurface(4, 4, TextureFormat.BC1Unorm, 1, new byte[] { 0x00, 0xF8, 0x1F, 0x00, 0xE4, 0xE4, 0xE4, 0xE4 });
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255, 170, 0, 85, 255, 85, 0, 170, 255 }, TextureCodec.Decode(opaque).Pixels[..16].ToArray());
        // c0 <= c1 selects three-colour mode with transparent index 3.
        var transparent = new TextureSurface(4, 4, TextureFormat.BC1Unorm, 1, new byte[] { 0x1F, 0x00, 0x00, 0xF8, 0xFF, 0xFF, 0xFF, 0xFF });
        Assert.All(TextureCodec.Decode(transparent).Pixels.ToArray().Chunk(4), pixel => Assert.Equal(new byte[4], pixel));
    }

    [Fact]
    public void PunchThroughKeepsOpaqueBlocksFourColourAndClearsTransparentTexels()
    {
        // Left block opaque gradient, right block with one transparent texel.
        const int width = 8, height = 4;
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        { rgba[i * 4] = (byte)(i * 8); rgba[i * 4 + 1] = (byte)(255 - i * 8); rgba[i * 4 + 2] = 90; rgba[i * 4 + 3] = 255; }
        rgba[(0 * width + 4) * 4 + 3] = 0;
        var options = new TextureEncodingOptions { Format = TextureFormat.BC1Unorm, GenerateMipmaps = false, Bc1Alpha = Bc1AlphaMode.PunchThrough };
        TextureSurface surface = TextureCodec.Encode(new RgbaImage(width, height, rgba), options);
        ReadOnlySpan<byte> blocks = surface.Data.Span;
        Assert.True(BinaryPrimitives.ReadUInt16LittleEndian(blocks) > BinaryPrimitives.ReadUInt16LittleEndian(blocks[2..]), "Opaque blocks use four-colour mode.");
        Assert.True(BinaryPrimitives.ReadUInt16LittleEndian(blocks[8..]) <= BinaryPrimitives.ReadUInt16LittleEndian(blocks[10..]), "Transparent texels require three-colour mode.");
        RgbaImage decoded = TextureCodec.Decode(surface);
        Assert.Equal(new byte[4], decoded.Pixels.Slice(4 * 4, 4).ToArray());
        Assert.Equal(255, decoded.Pixels[3]);
    }

    [Fact]
    public void ImageIoAndUncompressedConversionsHaveOwnedStorageAndCheckedLimits()
    {
        var image = new RgbaImage(2, 2, [255, 0, 0, 255, 0, 255, 0, 0, 0, 0, 255, 128, 255, 255, 255, 255]);
        byte[] png = image.Write(ImageFileFormat.Png);
        RgbaImage decoded = RgbaImage.Read(png);
        Assert.Equal(image.Pixels.ToArray(), decoded.Pixels.ToArray());
        Assert.True(decoded.HasTransparency);
        foreach (TextureFormat format in new[] { TextureFormat.R8G8B8A8Unorm, TextureFormat.B8G8R8A8Unorm, TextureFormat.R16G16B16A16Float, TextureFormat.R32G32B32A32Float })
        {
            TextureSurface surface = TextureCodec.Encode(image, new() { Format = format, GenerateMipmaps = false });
            Assert.Equal(image.Pixels.ToArray(), TextureCodec.Decode(surface).Pixels.ToArray());
        }
        Assert.Equal(1, image.Resize(1, 1).Width);
        Assert.Same(image, image.Resize(2, 2));
        Assert.Throws<ArgumentException>(() => new RgbaImage(2, 2, [1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => RgbaImage.Read(png, maximumBytes: 4));
        Assert.Throws<InvalidDataException>(() => RgbaImage.Read("invalid image"u8));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Write(quality: 0));
    }

    [Fact]
    public void SaveWritesAReadableFile()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var image = new RgbaImage(3, 2, Enumerable.Range(0, 24).Select(i => (byte)(i * 10)).ToArray());
            string path = Path.Combine(directory, "image.png");
            image.Save(path);
            Assert.Equal(image.Pixels.ToArray(), RgbaImage.Read(path).Pixels.ToArray());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void OptionsAreValidatedBeforeEncoding()
    {
        var image = new RgbaImage(4, 4, new byte[64]);
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureCodec.Encode(image, new() { Quality = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureCodec.Encode(image, new() { Quality = 1.5f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureCodec.Encode(image, new() { MinimumMipDimension = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureCodec.Encode(image, new() { MipFilter = (ImageFilter)99 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureCodec.Encode(image, new() { Bc1Alpha = (Bc1AlphaMode)7 }));
        Assert.Throws<ArgumentException>(() => TextureCodec.Encode(image, new() { Format = TextureFormat.BC3Unorm, Bc1Alpha = Bc1AlphaMode.PunchThrough }));
        Assert.Throws<NotSupportedException>(() => TextureCodec.Encode(image, new() { Format = TextureFormat.BC6HUf16 }));
        Assert.False(TextureCodec.CanEncode(TextureFormat.BC6HUf16));
        Assert.True(TextureCodec.CanDecode(TextureFormat.BC6HUf16));
        Assert.False(TextureCodec.CanDecode(TextureFormat.BC4Snorm));

        new TextureEncodingOptions { Format = TextureFormat.BC6HUf16 }.ThrowIfInvalid(requireEncoder: false);
        Assert.Throws<NotSupportedException>(() => new TextureEncodingOptions { Format = TextureFormat.BC6HUf16 }.ThrowIfInvalid());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageResizeOptions((ImageFilter)99).ThrowIfInvalid());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageResizeOptions(ColorSpace: (ImageColorSpace)2).ThrowIfInvalid());
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Resize(2, 2, new ImageResizeOptions((ImageFilter)99)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void MaxDegreeOfParallelismRejectsZeroAndValuesBelowMinusOne(int value)
    {
        var image = new RgbaImage(4, 4, new byte[64]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TextureCodec.Encode(image, new() { Format = TextureFormat.BC1Unorm, MaxDegreeOfParallelism = value }));
    }
}
