using System.Numerics;

namespace CommonFury.Textures;

/// <summary>One mip's dimensions, row pitch and byte range in the encoded payload.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="RowPitch">Bytes per row of pixels, or per row of 4×4 blocks for block-compressed formats.</param>
/// <param name="Offset">Byte offset in the encoded payload.</param>
/// <param name="Length">Byte length, including every depth slice.</param>
public readonly record struct TextureMip(int Width, int Height, int RowPitch, int Offset, int Length);

/// <summary>Tightly packed encoded layouts ordered by array element, cube face and largest-to-smallest mip; no pixel conversion is performed.</summary>
public static class TextureLayout
{
    /// <summary>Counts levels down to the first level whose largest dimension is at most minimumDimension.</summary>
    public static int CalculateMipCount(int width, int height, int minimumDimension = 1)
    {
        CheckDimensions(width, height, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumDimension);
        int count = 1;
        while (Math.Max(width, height) > minimumDimension)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            count++;
        }
        return count;
    }

    /// <summary>Counts a full chain using width, height and volume depth. Array elements and cube faces do not shrink in count.</summary>
    public static int CalculateMipCount(int width, int height, TextureShape shape, int minimumDimension = 1)
    {
        _ = new TextureShape(shape.Dimension, shape.Depth, shape.ArraySize);
        CheckDimensions(width, height, 1);
        return CalculateMipCount(Math.Max(width, shape.Depth), height, minimumDimension);
    }

    /// <summary>Bytes per tightly packed row of pixels, or of 4×4 blocks for block-compressed formats.</summary>
    public static int RowPitch(int width, TextureFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        Unit(format, out int unitSize, out int unitWidth);
        return checked(((width - 1) / unitWidth + 1) * unitSize);
    }

    /// <summary>Bytes for one tightly packed 2D surface, including partial edge blocks.</summary>
    public static int SurfaceSize(int width, int height, TextureFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Unit(format, out _, out int unitWidth);
        return checked(RowPitch(width, format) * ((height - 1) / unitWidth + 1));
    }

    public static int DataSize(int width, int height, TextureFormat format, int mipCount)
    {
        TextureMip[] mips = CreateMips(width, height, format, mipCount);
        return checked(mips[^1].Offset + mips[^1].Length);
    }

    /// <summary>Encoded size across every array element, cube face, depth slice and mip.</summary>
    public static int DataSize(int width, int height, TextureFormat format, int mipCount, TextureShape shape)
    {
        var parts = CreateSubresources(width, height, format, mipCount, shape);
        return checked(parts[^1].Mip.Offset + parts[^1].Mip.Length);
    }

    /// <summary>Lays out every array element, cube face and mip contiguously. Volume depth halves with each mip.</summary>
    public static TextureSubresource[] CreateSubresources(int width, int height, TextureFormat format, int mipCount, TextureShape shape)
    {
        _ = new TextureShape(shape.Dimension, shape.Depth, shape.ArraySize);
        CheckDimensions(width, height, mipCount, shape.Depth);
        if (shape.Dimension == TextureDimension.Cube && width != height)
            throw new ArgumentException("Cube faces must be square.", nameof(width));
        Unit(format, out int unitSize, out int unitWidth);
        long count = (long)shape.ArraySize * shape.Faces * mipCount;
        if (count > 65536)
            throw new ArgumentOutOfRangeException(nameof(shape), "Texture exceeds the subresource limit.");
        var result = new TextureSubresource[(int)count];
        int index = 0, offset = 0;
        for (int array = 0; array < shape.ArraySize; array++)
            for (int face = 0; face < shape.Faces; face++)
                for (int mip = 0; mip < mipCount; mip++)
                {
                    int w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip), d = Math.Max(1, shape.Depth >> mip);
                    int rowPitch = checked(((w - 1) / unitWidth + 1) * unitSize);
                    int slicePitch = checked(rowPitch * ((h - 1) / unitWidth + 1));
                    int length = checked(slicePitch * d);
                    result[index++] = new(array, face, mip, d, slicePitch, new(w, h, rowPitch, offset, length));
                    offset = checked(offset + length);
                }
        return result;
    }

    /// <summary>Lays out one planar mip chain contiguously, largest mip first.</summary>
    public static TextureMip[] CreateMips(int width, int height, TextureFormat format, int mipCount)
    {
        CheckDimensions(width, height, mipCount);
        Unit(format, out int unitSize, out int unitWidth);
        var mips = new TextureMip[mipCount];
        int offset = 0;
        for (int i = 0; i < mipCount; i++)
        {
            int pitch = checked(((width - 1) / unitWidth + 1) * unitSize);
            int length = checked(pitch * ((height - 1) / unitWidth + 1));
            mips[i] = new(width, height, pitch, offset, length);
            offset = checked(offset + length);
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        return mips;
    }

    /// <summary>Rejects nonpositive dimensions and a mip count beyond the complete chain of the largest dimension.</summary>
    public static void CheckDimensions(int width, int height, int mipCount, int depth = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mipCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        if (mipCount > BitOperations.Log2((uint)Math.Max(Math.Max(width, height), depth)) + 1)
            throw new ArgumentOutOfRangeException(nameof(mipCount), "Mip count exceeds the dimensions' complete chain.");
    }

    private static void Unit(TextureFormat format, out int unitSize, out int unitWidth)
    {
        if (!TextureFormats.TryUnit(format, out unitSize, out unitWidth))
            throw new NotSupportedException($"Texture format {(uint)format} has no supported encoded layout.");
    }
}
