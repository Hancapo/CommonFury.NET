using CommonFury.Binary;

namespace CommonFury.Textures;

/// <summary>Order of the 16-byte row segments inside a 512-byte GOB (64 bytes by 8 rows). Bytes 0–15 of a row segment stay
/// contiguous; GPUs differ in how address bits 4–8 take the row and the segment's position. Sizes and offsets do not depend on it.</summary>
public enum GobLayout
{
    /// <summary>Address bits 4–8 take row bit 0, byte-column bit 4, row bits 1–2 and byte-column bit 5: each 64-byte quarter holds
    /// 32 bytes of 2 rows. This is the Maxwell-generation (Tegra X1) arrangement.</summary>
    TwoRowQuarters,
    /// <summary>Address bits 4–8 take row bits 0–1, byte-column bit 4, row bit 2 and byte-column bit 5: each 64-byte quarter holds
    /// 16 bytes of 4 rows.</summary>
    FourRowQuarters
}

/// <summary>One surface in block-linear layout, measured in elements: pixels for uncompressed formats, blocks for compressed ones.</summary>
/// <remarks>Rows of elements are grouped into GOBs of 64 bytes by 8 rows, stored as 512 contiguous bytes in the order
/// <see cref="Gob"/> selects. GOBs stack <c>1 &lt;&lt; BlockHeightLog2</c> high and <c>1 &lt;&lt; BlockDepthLog2</c> deep into blocks,
/// which run left to right, then top to bottom, then front to back.</remarks>
public readonly record struct BlockLinearSurface
{
    /// <summary>Creates a surface of positive size, with block height and depth of at most 32 GOBs (log2 0–5).</summary>
    public BlockLinearSurface(int width, int height, int depth, int bytesPerElement, int blockHeightLog2, GobLayout gob, int blockDepthLog2 = 0)
    {
        if (!Enum.IsDefined(gob))
            throw new ArgumentOutOfRangeException(nameof(gob));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerElement);
        ArgumentOutOfRangeException.ThrowIfNegative(blockHeightLog2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockHeightLog2, 5);
        ArgumentOutOfRangeException.ThrowIfNegative(blockDepthLog2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockDepthLog2, 5);
        Width = width;
        Height = height;
        Depth = depth;
        BytesPerElement = bytesPerElement;
        BlockHeightLog2 = blockHeightLog2;
        BlockDepthLog2 = blockDepthLog2;
        Gob = gob;
        LinearRowBytes = checked(width * bytesPerElement);
        LinearSize = checked(LinearRowBytes * height * depth);
        GobsX = (LinearRowBytes + BlockLinear.GobWidth - 1) / BlockLinear.GobWidth;
        long blockBytes = (long)BlockLinear.GobSize << (blockHeightLog2 + blockDepthLog2);
        long blocksY = (height + (BlockLinear.GobHeight << blockHeightLog2) - 1) / (BlockLinear.GobHeight << blockHeightLog2);
        long blocksZ = (depth + (1 << blockDepthLog2) - 1) >> blockDepthLog2;
        Size = checked((int)(GobsX * blockBytes * blocksY * blocksZ));
    }

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public int BytesPerElement { get; }
    /// <summary>Block height in GOBs, as a power of two.</summary>
    public int BlockHeightLog2 { get; }
    /// <summary>Block depth in GOBs, as a power of two.</summary>
    public int BlockDepthLog2 { get; }
    public GobLayout Gob { get; }
    /// <summary>Bytes of the swizzled surface, including padding to whole GOBs and blocks.</summary>
    public int Size { get; }
    /// <summary>Bytes of the tightly packed linear surface: rows, then depth slices.</summary>
    public int LinearSize { get; }
    /// <summary>Bytes per tightly packed linear row.</summary>
    public int LinearRowBytes { get; }
    internal int GobsX { get; }
}

/// <summary>One mip of a <see cref="BlockLinearLayout"/>.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Depth">Depth slices.</param>
/// <param name="Surface">Element geometry, with the block height and depth this mip uses.</param>
/// <param name="Offset">Byte offset of the swizzled mip within a layer.</param>
/// <param name="LinearOffset">Byte offset of the tightly packed mip within a linear layer.</param>
public readonly record struct BlockLinearLevel(int Width, int Height, int Depth, BlockLinearSurface Surface, int Offset, int LinearOffset);

/// <summary>Converts between block-linear and tightly packed linear layouts, as used by NVIDIA Tegra-class GPUs.</summary>
public static class BlockLinear
{
    /// <summary>Bytes per GOB row.</summary>
    public const int GobWidth = 64;
    /// <summary>Rows per GOB.</summary>
    public const int GobHeight = 8;
    public const int GobSize = GobWidth * GobHeight;

    /// <summary>Reads a swizzled surface of exactly <see cref="BlockLinearSurface.Size"/> bytes into a linear surface of exactly
    /// <see cref="BlockLinearSurface.LinearSize"/> bytes. Padding bytes are ignored.</summary>
    public static void Deswizzle(ReadOnlySpan<byte> swizzled, Span<byte> linear, BlockLinearSurface surface)
    {
        CheckLengths(swizzled.Length, linear.Length, surface);
        Copy(surface, swizzled, linear, toLinear: true);
    }

    /// <summary>Writes a linear surface into a swizzled one; padding bytes become zero.</summary>
    /// <inheritdoc cref="Deswizzle"/>
    public static void Swizzle(ReadOnlySpan<byte> linear, Span<byte> swizzled, BlockLinearSurface surface)
    {
        CheckLengths(swizzled.Length, linear.Length, surface);
        swizzled.Clear();
        Copy(surface, linear, swizzled, toLinear: false);
    }

    private static void CheckLengths(int swizzled, int linear, BlockLinearSurface surface)
    {
        if (surface.Size == 0)
            throw new ArgumentException("The surface is uninitialized.", nameof(surface));
        if (swizzled != surface.Size)
            throw new ArgumentException("Swizzled length does not match the surface.", nameof(swizzled));
        if (linear != surface.LinearSize)
            throw new ArgumentException("Linear length does not match the surface.", nameof(linear));
    }

    private static void Copy(BlockLinearSurface surface, ReadOnlySpan<byte> source, Span<byte> destination, bool toLinear)
    {
        int heightLog2 = surface.BlockHeightLog2, depthLog2 = surface.BlockDepthLog2, rowBytes = surface.LinearRowBytes;
        int blockBytes = GobSize << (heightLog2 + depthLog2), blockRowBytes = surface.GobsX * blockBytes;
        int blocksY = (surface.Height + (GobHeight << heightLog2) - 1) / (GobHeight << heightLog2);
        int sliceBytes = blockRowBytes * blocksY, heightMask = (1 << heightLog2) - 1, depthMask = (1 << depthLog2) - 1;
        bool twoRows = surface.Gob == GobLayout.TwoRowQuarters;
        int columnBit4 = twoRows ? 32 : 64;
        for (int z = 0, linearOffset = 0; z < surface.Depth; z++)
        {
            int zOffset = (z >> depthLog2) * sliceBytes + ((z & depthMask) << heightLog2) * GobSize;
            for (int y = 0; y < surface.Height; y++, linearOffset += rowBytes)
            {
                int yOffset = zOffset + (y >> (3 + heightLog2)) * blockRowBytes + (y >> 3 & heightMask) * GobSize
                    + (twoRows ? (y >> 1 & 3) * 64 + (y & 1) * 16 : (y >> 2 & 1) * 128 + (y & 3) * 16);
                // Bytes within a 16-byte sector stay contiguous, so rows move a sector at a time.
                for (int x = 0; x < rowBytes;)
                {
                    int run = Math.Min(16 - (x & 15), rowBytes - x);
                    int swizzledOffset = yOffset + (x >> 6) * blockBytes + (x >> 5 & 1) * 256 + (x >> 4 & 1) * columnBit4 + (x & 15);
                    if (toLinear)
                        source.Slice(swizzledOffset, run).CopyTo(destination.Slice(linearOffset + x, run));
                    else
                        source.Slice(linearOffset + x, run).CopyTo(destination.Slice(swizzledOffset, run));
                    x += run;
                }
            }
        }
    }
}

/// <summary>Mip chains and layers of a block-linear texture: per-mip block size reduction, offsets and layer alignment.</summary>
/// <remarks>Each mip halves width, height and depth (minimum one) and is measured in whole elements. A mip's block height (and depth)
/// halves while the mip's element rows (slices) fit in half the block, down to one GOB. Mips follow each other within a layer. With more
/// than one layer, each layer is padded to a whole block of the first mip; one layer is not padded.</remarks>
public sealed class BlockLinearLayout
{
    /// <summary>Lays out a texture whose first mip uses the given block height and depth.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="depth">Depth slices of a volume; one otherwise.</param>
    /// <param name="element">Bytes and pixel footprint of one element.</param>
    /// <param name="mipCount">Mips per layer.</param>
    /// <param name="layerCount">Array elements times cube faces.</param>
    /// <param name="blockHeightLog2">Block height in GOBs of the first mip, as a power of two (0–5).</param>
    /// <param name="gob">Order of row segments inside each GOB.</param>
    /// <param name="blockDepthLog2">Block depth in GOBs of the first mip, as a power of two (0–5).</param>
    public BlockLinearLayout(int width, int height, int depth, TextureElement element, int mipCount, int layerCount, int blockHeightLog2,
        GobLayout gob, int blockDepthLog2 = 0)
    {
        TextureLayout.CheckDimensions(width, height, mipCount, depth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layerCount);
        if (element.Bytes == 0)
            throw new ArgumentException("The element is uninitialized.", nameof(element));
        var levels = new BlockLinearLevel[mipCount];
        int offset = 0, linearOffset = 0;
        for (int level = 0; level < mipCount; level++)
        {
            int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level), d = Math.Max(1, depth >> level);
            int rows = element.CountY(h), heightLog2 = blockHeightLog2, depthLog2 = blockDepthLog2;
            while (heightLog2 > 0 && rows <= BlockLinear.GobHeight << (heightLog2 - 1))
                heightLog2--;
            while (depthLog2 > 0 && d <= 1 << (depthLog2 - 1))
                depthLog2--;
            var surface = new BlockLinearSurface(element.CountX(w), rows, d, element.Bytes, heightLog2, gob, depthLog2);
            levels[level] = new(w, h, d, surface, offset, linearOffset);
            offset = checked(offset + surface.Size);
            linearOffset = checked(linearOffset + surface.LinearSize);
        }
        var first = levels[0].Surface;
        LayerSize = offset;
        LayerStride = layerCount == 1 ? offset : BinaryLayout.Align(offset, BlockLinear.GobSize << (first.BlockHeightLog2 + first.BlockDepthLog2));
        LinearLayerSize = linearOffset;
        LayerCount = layerCount;
        Size = checked(LayerStride * layerCount);
        LinearSize = checked(linearOffset * layerCount);
        Levels = Array.AsReadOnly(levels);
    }

    public IReadOnlyList<BlockLinearLevel> Levels { get; }
    public int LayerCount { get; }
    /// <summary>Swizzled bytes of one layer's mips, without padding.</summary>
    public int LayerSize { get; }
    /// <summary>Bytes from one swizzled layer to the next.</summary>
    public int LayerStride { get; }
    /// <summary>Swizzled bytes of every layer.</summary>
    public int Size { get; }
    /// <summary>Tightly packed bytes of one layer's mips.</summary>
    public int LinearLayerSize { get; }
    /// <summary>Tightly packed bytes of every layer, ordered by layer, then mip, as <see cref="TextureLayout"/> orders subresources.</summary>
    public int LinearSize { get; }

    /// <summary>Deswizzles every layer and mip of exactly <see cref="Size"/> bytes into a new array of <see cref="LinearSize"/> bytes.</summary>
    /// <exception cref="ArgumentException">The input length does not match the layout.</exception>
    public byte[] Deswizzle(ReadOnlySpan<byte> swizzled)
    {
        if (swizzled.Length != Size)
            throw new ArgumentException("Swizzled length does not match the layout.", nameof(swizzled));
        var linear = new byte[LinearSize];
        for (int layer = 0; layer < LayerCount; layer++)
            foreach (var level in Levels)
                BlockLinear.Deswizzle(swizzled.Slice(layer * LayerStride + level.Offset, level.Surface.Size),
                    linear.AsSpan(layer * LinearLayerSize + level.LinearOffset, level.Surface.LinearSize), level.Surface);
        return linear;
    }

    /// <summary>Swizzles every layer and mip of exactly <see cref="LinearSize"/> bytes into a new array of <see cref="Size"/> bytes;
    /// padding is zero.</summary>
    /// <exception cref="ArgumentException">The input length does not match the layout.</exception>
    public byte[] Swizzle(ReadOnlySpan<byte> linear)
    {
        if (linear.Length != LinearSize)
            throw new ArgumentException("Linear length does not match the layout.", nameof(linear));
        var swizzled = new byte[Size];
        for (int layer = 0; layer < LayerCount; layer++)
            foreach (var level in Levels)
                BlockLinear.Swizzle(linear.Slice(layer * LinearLayerSize + level.LinearOffset, level.Surface.LinearSize),
                    swizzled.AsSpan(layer * LayerStride + level.Offset, level.Surface.Size), level.Surface);
        return swizzled;
    }
}
