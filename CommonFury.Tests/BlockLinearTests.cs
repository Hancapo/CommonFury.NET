using CommonFury.Textures;
using Xunit;

namespace CommonFury.Tests;

public sealed class BlockLinearTests
{
    [Fact]
    public void GobOrdersPlaceKnownBytes()
    {
        // One GOB of single-byte elements: byte (x, y) lands at the listed swizzled offset.
        (int X, int Y, int TwoRows, int FourRows)[] cases =
        [
            (0, 0, 0, 0), (15, 0, 15, 15), (16, 0, 32, 64), (32, 0, 256, 256), (63, 7, 511, 511),
            (0, 1, 16, 16), (0, 2, 64, 32), (0, 3, 80, 48), (0, 4, 128, 128), (17, 5, 177, 209)
        ];
        foreach (var gob in (GobLayout[])[GobLayout.TwoRowQuarters, GobLayout.FourRowQuarters])
        {
            var surface = new BlockLinearSurface(64, 8, 1, 1, 0, gob);
            Assert.Equal(512, surface.Size);
            byte[] linear = new byte[512], swizzled = new byte[512];
            foreach (var (x, y, twoRows, fourRows) in cases)
            {
                Array.Clear(linear);
                linear[y * 64 + x] = 1;
                BlockLinear.Swizzle(linear, swizzled, surface);
                Assert.Equal(gob == GobLayout.TwoRowQuarters ? twoRows : fourRows, Array.IndexOf(swizzled, (byte)1));
            }
        }
    }

    [Theory]
    [InlineData(1, 1, 1, 1, 0, 0)] [InlineData(7, 9, 1, 4, 0, 0)] [InlineData(37, 70, 1, 16, 2, 0)] [InlineData(19, 300, 1, 8, 5, 0)]
    [InlineData(11, 21, 1, 12, 1, 0)] [InlineData(130, 17, 1, 2, 3, 0)] [InlineData(9, 13, 5, 4, 1, 1)] [InlineData(5, 3, 9, 16, 0, 2)]
    public void MatchesThePerByteAddressFormula(int width, int height, int depth, int bytes, int heightLog2, int depthLog2)
    {
        foreach (var gob in (GobLayout[])[GobLayout.TwoRowQuarters, GobLayout.FourRowQuarters])
        {
            var surface = new BlockLinearSurface(width, height, depth, bytes, heightLog2, gob, depthLog2);
            var linear = new byte[surface.LinearSize];
            new Random(width * 31 + height).NextBytes(linear);
            var swizzled = new byte[surface.Size];
            BlockLinear.Swizzle(linear, swizzled, surface);
            int rowBytes = width * bytes, written = 0;
            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < rowBytes; x++, written++)
                        Assert.Equal(linear[(z * height + y) * rowBytes + x], swizzled[Address(surface, x, y, z)]);
            // Padding is zero and nothing else is written.
            Assert.Equal(surface.Size - written, swizzled.Count(value => value == 0) - linear.Count(value => value == 0));
            var back = new byte[surface.LinearSize];
            BlockLinear.Deswizzle(swizzled, back, surface);
            Assert.Equal(linear, back);
        }
    }

    [Fact]
    public void LaysOutMipChainsWithShrinkingBlocks()
    {
        // 300x1021 with 8x8 16-byte blocks: 640-byte GOB rows at the first mip, block height 16 GOBs halving with the mips.
        var layout = new BlockLinearLayout(300, 1021, 1, new TextureElement(16, 8, 8), 10, 1, 4, GobLayout.FourRowQuarters);
        Assert.Equal([0, 81920, 102400, 108544, 110592, 111104, 111616, 112128, 112640, 113152], layout.Levels.Select(level => level.Offset));
        Assert.Equal([4, 3, 2, 1, 0, 0, 0, 0, 0, 0], layout.Levels.Select(level => level.Surface.BlockHeightLog2));
        Assert.Equal((75, 255, 10, 32), (layout.Levels[2].Width, layout.Levels[2].Height, layout.Levels[2].Surface.Width, layout.Levels[2].Surface.Height));
        Assert.Equal(113664, layout.LayerSize);
        Assert.Equal(layout.LayerSize, layout.LayerStride);
        Assert.Equal(layout.LayerSize, layout.Size);

        // Several layers pad each layer to a whole block of the first mip.
        var array = new BlockLinearLayout(300, 1021, 1, new TextureElement(16, 8, 8), 10, 3, 4, GobLayout.FourRowQuarters);
        Assert.Equal(16 * 512 * 14, array.LayerStride);
        Assert.Equal(3 * array.LayerStride, array.Size);
        Assert.Equal(3 * array.LinearLayerSize, array.LinearSize);

        // Volume depth shrinks its block depth the same way.
        var volume = new BlockLinearLayout(16, 16, 4, new TextureElement(4), 3, 1, 1, GobLayout.TwoRowQuarters, 2);
        Assert.Equal([2, 1, 0], volume.Levels.Select(level => level.Surface.BlockDepthLog2));
        Assert.Equal([1, 0, 0], volume.Levels.Select(level => level.Surface.BlockHeightLog2));
    }

    [Fact]
    public void LayoutRoundTripsEveryLayerAndMip()
    {
        var layout = new BlockLinearLayout(70, 45, 1, new TextureElement(TextureFormat.BC1Unorm), 4, 6, 3, GobLayout.FourRowQuarters);
        var linear = new byte[layout.LinearSize];
        new Random(5).NextBytes(linear);
        var swizzled = layout.Swizzle(linear);
        Assert.Equal(layout.Size, swizzled.Length);
        Assert.Equal(linear, layout.Deswizzle(swizzled));
        var level = layout.Levels[1];
        var single = new byte[level.Surface.LinearSize];
        BlockLinear.Deswizzle(swizzled.AsSpan(2 * layout.LayerStride + level.Offset, level.Surface.Size), single, level.Surface);
        Assert.Equal(linear.AsSpan(2 * layout.LinearLayerSize + level.LinearOffset, single.Length).ToArray(), single);
    }

    [Fact]
    public void ValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockLinearSurface(0, 1, 1, 1, 0, GobLayout.TwoRowQuarters));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockLinearSurface(1, 1, 1, 1, 6, GobLayout.TwoRowQuarters));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockLinearSurface(1, 1, 1, 1, 0, (GobLayout)7));
        var surface = new BlockLinearSurface(4, 4, 1, 4, 0, GobLayout.TwoRowQuarters);
        Assert.Throws<ArgumentException>(() => BlockLinear.Deswizzle(new byte[surface.Size - 1], new byte[surface.LinearSize], surface));
        Assert.Throws<ArgumentException>(() => BlockLinear.Deswizzle(new byte[surface.Size], new byte[surface.LinearSize + 1], surface));
        Assert.Throws<ArgumentException>(() => BlockLinear.Swizzle(new byte[1], new byte[1], default));
        var layout = new BlockLinearLayout(8, 8, 1, new TextureElement(4), 1, 2, 0, GobLayout.TwoRowQuarters);
        Assert.Throws<ArgumentException>(() => layout.Deswizzle(new byte[layout.Size + 1]));
        Assert.Throws<ArgumentException>(() => layout.Swizzle(new byte[layout.LinearSize - 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockLinearLayout(8, 8, 1, new TextureElement(4), 5, 1, 0, GobLayout.TwoRowQuarters));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockLinearLayout(8, 8, 1, new TextureElement(4), 1, 0, 0, GobLayout.TwoRowQuarters));
        Assert.Throws<NotSupportedException>(() => new TextureElement(TextureFormat.Unknown));
        Assert.Equal((3, 2), (new TextureElement(16, 5, 4).CountX(11), new TextureElement(16, 5, 4).CountY(5)));
    }

    // Byte (x, y, z) of a linear surface, derived bit by bit from the GOB and block arrangement.
    private static int Address(BlockLinearSurface s, int x, int y, int z)
    {
        int gobsX = (s.Width * s.BytesPerElement + 63) / 64, blockHeight = 1 << s.BlockHeightLog2, blockDepth = 1 << s.BlockDepthLog2;
        int blocksY = (s.Height + 8 * blockHeight - 1) / (8 * blockHeight);
        int block = z / blockDepth * blocksY * gobsX + y / (8 * blockHeight) * gobsX + x / 64;
        int gob = z % blockDepth * blockHeight + y / 8 % blockHeight;
        int inner = s.Gob == GobLayout.TwoRowQuarters
            ? x % 64 / 32 * 256 + y % 8 / 2 * 64 + x % 32 / 16 * 32 + y % 2 * 16 + x % 16
            : x % 64 / 32 * 256 + y % 8 / 4 * 128 + x % 32 / 16 * 64 + y % 4 * 16 + x % 16;
        return block * 512 * blockHeight * blockDepth + gob * 512 + inner;
    }
}
