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
