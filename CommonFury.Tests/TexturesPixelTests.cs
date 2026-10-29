using System.Buffers.Binary;
using CommonFury.Numerics;
using CommonFury.Textures;
using Xunit;

namespace CommonFury.Tests;

public sealed class TexturesPixelTests
{
    private static readonly byte[] Image = [255, 0, 0, 255, 0, 255, 0, 0, 0, 0, 255, 128, 255, 255, 255, 255];

    [Theory]
    [InlineData(TextureFormat.R8G8B8A8Unorm)]
    [InlineData(TextureFormat.R8G8B8A8UnormSrgb)]
    [InlineData(TextureFormat.B8G8R8A8Unorm)]
    [InlineData(TextureFormat.R16G16B16A16Float)]
    [InlineData(TextureFormat.R16G16B16A16Unorm)]
    [InlineData(TextureFormat.R32G32B32A32Float)]
    public void FourChannelFormatsRoundTripRgba8(TextureFormat format)
    {
        TextureFormats.TryUnit(format, out int stride, out _);
        byte[] encoded = new byte[Image.Length / 4 * stride];
        PixelCodec.Encode(Image, format, encoded);
        byte[] decoded = new byte[Image.Length];
        PixelCodec.Decode(encoded, format, decoded);
        Assert.Equal(Image, decoded);
    }

    [Fact]
    public void PackedFormatsUseDocumentedBitLayouts()
    {
        byte[] encoded = new byte[2];
        PixelCodec.Encode([255, 0, 0, 255], TextureFormat.B5G6R5Unorm, encoded);
        Assert.Equal(0xF800, BinaryPrimitives.ReadUInt16LittleEndian(encoded));
        PixelCodec.Encode([0, 0, 255, 255], TextureFormat.B5G5R5A1Unorm, encoded);
        Assert.Equal(0x801F, BinaryPrimitives.ReadUInt16LittleEndian(encoded));
        byte[] rgba = new byte[4];
        PixelCodec.Decode([0x1F, 0x00], TextureFormat.B5G5R5A1Unorm, rgba);
        Assert.Equal(new byte[] { 0, 0, 255, 0 }, rgba);
        PixelCodec.Decode([7], TextureFormat.A8Unorm, rgba);
        Assert.Equal(new byte[] { 255, 255, 255, 7 }, rgba);
        PixelCodec.Decode([7], TextureFormat.R8Unorm, rgba);
        Assert.Equal(new byte[] { 7, 7, 7, 255 }, rgba);
        Assert.False(PixelCodec.Supports(TextureFormat.BC1Unorm));
        Assert.Throws<NotSupportedException>(() => PixelCodec.Decode(new byte[8], TextureFormat.BC1Unorm, new byte[64]));
        Assert.Throws<ArgumentException>(() => PixelCodec.Decode(new byte[3], TextureFormat.R8G8B8A8Unorm, new byte[4]));
    }

    [Fact]
    public void SmallFloatsEncodeExactValuesAndRoundTripBytes()
    {
        byte[] encoded = new byte[4];
        PixelCodec.Encode([255, 255, 255, 255], TextureFormat.R11G11B10Float, encoded);
        // 1.0 is exponent 15 with a zero mantissa: 0x3C0 in each 11-bit field and 0x1E0 in the 10-bit field.
        Assert.Equal(0x3C0u | 0x3C0u << 11 | 0x1E0u << 22, BinaryPrimitives.ReadUInt32LittleEndian(encoded));
        PixelCodec.Encode([0, 0, 0, 0], TextureFormat.R11G11B10Float, encoded);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(encoded));
        byte[] rgba = new byte[4];
        for (int value = 0; value < 256; value++)
        {
            PixelCodec.Encode([(byte)value, (byte)value, (byte)value, 9], TextureFormat.R11G11B10Float, encoded);
            PixelCodec.Decode(encoded, TextureFormat.R11G11B10Float, rgba);
            Assert.InRange(rgba[0] - value, -1, 1);
            Assert.InRange(rgba[2] - value, -2, 2);
            Assert.Equal(255, rgba[3]);
        }
        // The largest finite value saturates; infinities and NaN decode as full and zero intensity.
        PixelCodec.Decode(BitConverter.GetBytes(0x7C0u | 0x7C1u << 11), TextureFormat.R11G11B10Float, rgba);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba);
    }

    [Fact]
    public void SmallFloatTablesMatchTheFormulaForEveryValue()
    {
        static byte Expected(int value, int mantissaBits)
        {
            int exponent = value >> mantissaBits, mantissa = value & ((1 << mantissaBits) - 1);
            float fraction = mantissa / (float)(1 << mantissaBits);
            float decoded = exponent == 0 ? MathF.ScaleB(fraction, -14)
                : exponent == 31 ? (mantissa == 0 ? float.PositiveInfinity : float.NaN)
                : MathF.ScaleB(1 + fraction, exponent - 15);
            return PixelMath.ToByte(decoded);
        }
        Assert.Equal(2048, PixelCodec.ElevenBitFloats.Length);
        Assert.Equal(1024, PixelCodec.TenBitFloats.Length);
        byte[] rgba = new byte[2048 * 4];
        byte[] encoded = new byte[2048 * 4];
        for (int value = 0; value < 2048; value++)
        {
            Assert.Equal(Expected(value, 6), PixelCodec.ElevenBitFloats[value]);
            if (value < 1024)
                Assert.Equal(Expected(value, 5), PixelCodec.TenBitFloats[value]);
            BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(value * 4), (uint)(value | (2047 - value) << 11 | (value & 1023) << 22));
        }
        PixelCodec.Decode(encoded, TextureFormat.R11G11B10Float, rgba);
        for (int value = 0; value < 2048; value++)
            Assert.Equal(new[] { Expected(value, 6), Expected(2047 - value, 6), Expected(value & 1023, 5), (byte)255 }, rgba[(value * 4)..(value * 4 + 4)]);
    }

    [Fact]
    public void ByteFormatsCopyOrSwapWholeImages()
    {
        byte[] image = Enumerable.Range(1, 4 * 5).Select(i => (byte)(i * 11)).ToArray();
        byte[] swapped = new byte[image.Length], opaque = new byte[image.Length];
        for (int i = 0; i < image.Length; i += 4)
        {
            swapped[i] = opaque[i] = image[i + 2];
            swapped[i + 1] = opaque[i + 1] = image[i + 1];
            swapped[i + 2] = opaque[i + 2] = image[i];
            swapped[i + 3] = image[i + 3];
            opaque[i + 3] = 255;
        }
        byte[] output = new byte[image.Length];
        PixelCodec.Encode(image, TextureFormat.R8G8B8A8UnormSrgb, output);
        Assert.Equal(image, output);
        PixelCodec.Decode(image, TextureFormat.R8G8B8A8Unorm, output);
        Assert.Equal(image, output);
        PixelCodec.Encode(image, TextureFormat.B8G8R8A8Unorm, output);
        Assert.Equal(swapped, output);
        PixelCodec.Decode(image, TextureFormat.B8G8R8A8UnormSrgb, output);
        Assert.Equal(swapped, output);
        PixelCodec.Encode(image, TextureFormat.B8G8R8X8UnormSrgb, output);
        Assert.Equal(opaque, output);
        PixelCodec.Decode(image, TextureFormat.B8G8R8X8Unorm, output);
        Assert.Equal(opaque, output);
    }

    // Every texel of the output is overwritten, including channels the format lacks.
    [Theory]
    [InlineData(TextureFormat.R8Unorm, new byte[] { 7, 9 }, new byte[] { 7, 7, 7, 255, 9, 9, 9, 255 })]
    [InlineData(TextureFormat.A8Unorm, new byte[] { 7, 9 }, new byte[] { 255, 255, 255, 7, 255, 255, 255, 9 })]
    [InlineData(TextureFormat.R8G8Unorm, new byte[] { 7, 9, 3, 4 }, new byte[] { 7, 9, 0, 255, 3, 4, 0, 255 })]
    [InlineData(TextureFormat.B5G6R5Unorm, new byte[] { 0x1F, 0xF8, 0xE0, 0x07 }, new byte[] { 255, 0, 255, 255, 0, 255, 0, 255 })]
    [InlineData(TextureFormat.B5G5R5A1Unorm, new byte[] { 0x00, 0xFC, 0xE0, 0x03 }, new byte[] { 255, 0, 0, 255, 0, 255, 0, 0 })]
    [InlineData(TextureFormat.R10G10B10A2Unorm, new byte[] { 0xFF, 0x03, 0x00, 0x40, 0x00, 0x00, 0xF0, 0xBF }, new byte[] { 255, 0, 0, 85, 0, 0, 255, 170 })]
    [InlineData(TextureFormat.R16Unorm, new byte[] { 0x00, 0x80, 0xFF, 0xFF }, new byte[] { 128, 128, 128, 255, 255, 255, 255, 255 })]
    [InlineData(TextureFormat.R16G16Unorm, new byte[] { 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00 }, new byte[] { 0, 255, 0, 255, 255, 0, 0, 255 })]
    [InlineData(TextureFormat.R16Float, new byte[] { 0x00, 0x38, 0x00, 0x3C }, new byte[] { 128, 128, 128, 255, 255, 255, 255, 255 })]
    [InlineData(TextureFormat.R16G16Float, new byte[] { 0x00, 0x3C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x38 }, new byte[] { 255, 0, 0, 255, 0, 128, 0, 255 })]
    [InlineData(TextureFormat.R32Float, new byte[] { 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x3F }, new byte[] { 255, 255, 255, 255, 128, 128, 128, 255 })]
    [InlineData(TextureFormat.R32G32Float, new byte[] { 0x00, 0x00, 0x80, 0x3F, 0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x00, 0x00, 0x3F }, new byte[] { 255, 0, 0, 255, 0, 128, 0, 255 })]
    public void DecodeWritesEveryChannelOfEveryTexel(TextureFormat format, byte[] encoded, byte[] expected)
    {
        byte[] rgba = Enumerable.Repeat((byte)0xAA, expected.Length).ToArray();
        PixelCodec.Decode(encoded, format, rgba);
        Assert.Equal(expected, rgba);
    }

    [Fact]
    public void ChannelMathRoundsAndClamps()
    {
        Assert.Equal(0, PixelMath.ToByte(float.NaN));
        Assert.Equal(255, PixelMath.ToByte(2));
        Assert.Equal(128, PixelMath.ToByte(0.5f));
        Assert.Equal(31u, PixelMath.Quantize(255, 31));
        Assert.Equal(255, PixelMath.Expand(31, 31));
        for (uint value = 0; value < 64; value++)
            Assert.Equal(value, PixelMath.Quantize(PixelMath.Expand(value, 63), 63));
    }

    [Fact]
    public void Bc1EncodesUniformBlocksExactlyAndDecodesKnownPalettes()
    {
        byte[] rgba = new byte[8 * 4 * 4];
        for (int i = 0; i < 32; i++) { rgba[i * 4] = 128; rgba[i * 4 + 1] = 128; rgba[i * 4 + 2] = 230; rgba[i * 4 + 3] = 255; }
        byte[] blocks = Bc1.Encode(rgba, 8, 4);
        // 5:6:5 (16, 32, 28) in both endpoints, all indices zero.
        Assert.Equal(new byte[] { 0x1C, 0x84, 0x1C, 0x84, 0, 0, 0, 0, 0x1C, 0x84, 0x1C, 0x84, 0, 0, 0, 0 }, blocks);

        // Red/blue four-colour block: indices 0,1,2,3 repeated per row.
        byte[] known = [0x00, 0xF8, 0x1F, 0x00, 0xE4, 0xE4, 0xE4, 0xE4];
        byte[] pixels = Bc1.Decode(known, 4, 4);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255, 170, 0, 85, 255, 85, 0, 170, 255 }, pixels[..16]);
        // c0 <= c1 selects three-colour mode with transparent index 3.
        byte[] transparent = Bc1.Decode([0x1F, 0x00, 0x00, 0xF8, 0xFF, 0xFF, 0xFF, 0xFF], 4, 4);
        Assert.All(transparent.Chunk(4), p => Assert.Equal(new byte[4], p));
    }

    [Fact]
    public void Bc1EdgeBlocksDecodeTheTopLeftOfTheirTexels()
    {
        byte[] blocks = Enumerable.Range(0, 32).Select(i => (byte)(i * 97 + 13)).ToArray();
        byte[] full = Bc1.Decode(blocks, 8, 8);
        for (int width = 5; width <= 8; width++)
            for (int height = 5; height <= 8; height++)
            {
                byte[] expected = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                    full.AsSpan(y * 32, width * 4).CopyTo(expected.AsSpan(y * width * 4));
                Assert.Equal(expected, Bc1.Decode(blocks, width, height));
            }
    }

    [Fact]
    public void Bc1EdgeBlocksFitOnlyTheirOwnTexels()
    {
        const int width = 5, height = 6;
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                bool edge = x == 4 || y >= 4;
                rgba[o] = edge ? (byte)128 : (byte)(x * 60);
                rgba[o + 1] = edge ? (byte)128 : (byte)(y * 70);
                rgba[o + 2] = edge ? (byte)230 : (byte)20;
                rgba[o + 3] = 255;
            }
        byte[] blocks = Bc1.Encode(rgba, width, height);
        // The gradient in the first block must not leak into the uniform edge blocks.
        byte[] uniform = [0x1C, 0x84, 0x1C, 0x84, 0, 0, 0, 0];
        Assert.NotEqual(uniform, blocks[..8]);
        Assert.Equal(uniform, blocks[8..16]);
        Assert.Equal(uniform, blocks[16..24]);
        Assert.Equal(uniform, blocks[24..32]);
    }

    [Fact]
    public void Bc1ApproximatesGradientsHandlesAlphaAndPartialBlocks()
    {
        const int width = 6, height = 5;
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                int r = x * 20 + y * 10; rgba[o] = (byte)r; rgba[o + 1] = (byte)(r / 2 + 40); rgba[o + 2] = 90; rgba[o + 3] = 255;
            }
        rgba[3] = 0;
        byte[] blocks = Bc1.Encode(rgba, width, height);
        Assert.Equal(TextureLayout.SurfaceSize(width, height, TextureFormat.BC1Unorm), blocks.Length);
        byte[] decoded = Bc1.Decode(blocks, width, height);
        Assert.Equal(new byte[4], decoded[..4]);
        Assert.True(BinaryPrimitives.ReadUInt16LittleEndian(blocks) <= BinaryPrimitives.ReadUInt16LittleEndian(blocks.AsSpan(2)), "Transparent texels require three-colour mode.");
        for (int i = 1; i < width * height; i++)
            for (int channel = 0; channel < 4; channel++)
                Assert.InRange(Math.Abs(decoded[i * 4 + channel] - rgba[i * 4 + channel]), 0, 40);
        Assert.Throws<ArgumentException>(() => Bc1.Encode(rgba.AsSpan(4), width, height));
    }
}
