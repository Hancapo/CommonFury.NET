using System.Buffers.Binary;
using CommonFury.Binary;
using CommonFury.IO;

namespace CommonFury.Textures;

/// <summary>DDS import/export for contiguous 2D images, arrays, cubes and volumes.
/// Preserves dimensions, DXGI color space and encoded bytes; file headers are regenerated.
/// Padded rows and nonzero DX10 alpha-mode metadata are unsupported.</summary>
public static class DdsCodec
{
    private const uint Magic = 0x20534444, Dx10 = 0x30315844;

    /// <summary>Decodes a DDS image and mip chain, validating its header and payload layout. The payload is copied into an owned array.</summary>
    public static TextureSurface Read(ReadOnlySpan<byte> data)
    {
        BinaryLayout.Require(data.Length >= 128, "DDS header is truncated.");
        var header = new BinarySpanReader(data);
        BinaryLayout.Require(header.ReadUInt32() == Magic && header.ReadUInt32() == 124, "DDS magic or header size is invalid.");
        uint flags = header.ReadUInt32();
        uint height = header.ReadUInt32(), width = header.ReadUInt32(), pitch = header.ReadUInt32(), depth = header.ReadUInt32();
        uint mipCount = header.ReadUInt32();
        BinaryLayout.Require(width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue, "DDS dimensions are invalid.");
        BinaryLayout.Require(depth <= int.MaxValue, "DDS depth is invalid.");
        if (mipCount == 0)
            mipCount = 1;
        BinaryLayout.Require(mipCount <= int.MaxValue, "DDS mip count is invalid.");
        header.Seek(76);
        BinaryLayout.Require(header.ReadUInt32() == 32, "DDS pixel format size is invalid.");
        uint pixelFlags = header.ReadUInt32(), fourCc = header.ReadUInt32(), bits = header.ReadUInt32();
        uint red = header.ReadUInt32(), green = header.ReadUInt32(), blue = header.ReadUInt32(), alpha = header.ReadUInt32();
        header.Seek(112);
        uint caps2 = header.ReadUInt32();
        BinaryLayout.Require((caps2 & ~0x20FE00U) == 0, "DDS capability bits are unsupported.");
        bool cube = (caps2 & 0x200) != 0, volume = (caps2 & 0x200000) != 0;
        BinaryLayout.Require(!cube || !volume && (caps2 & 0xFC00) == 0xFC00, "DDS cube must contain all six faces and cannot be a volume.");
        BinaryLayout.Require(cube || (caps2 & 0xFC00) == 0, "DDS face flags require a cube.");
        int arrays = 1;
        int payloadOffset = 128;
        TextureFormat format;
        if ((pixelFlags & 4) != 0 && fourCc == Dx10)
        {
            BinaryLayout.Require(data.Length >= 148, "DDS DX10 header is truncated.");
            header.Seek(128);
            format = (TextureFormat)header.ReadUInt32();
            uint dimension = header.ReadUInt32(), misc = header.ReadUInt32(), arraySize = header.ReadUInt32();
            BinaryLayout.Require(dimension is 3 or 4 && (misc & ~4U) == 0 && arraySize is > 0 and <= int.MaxValue,
                "DDS DX10 topology is unsupported or invalid.");
            bool dxCube = (misc & 4) != 0;
            BinaryLayout.Require(!dxCube || dimension == 3, "A DDS cube must be two-dimensional.");
            BinaryLayout.Require(!cube || dxCube, "DDS cube headers disagree.");
            BinaryLayout.Require(!volume || dimension == 4, "DDS volume headers disagree.");
            cube = dxCube;
            volume = dimension == 4;
            arrays = (int)arraySize;
            BinaryLayout.Require(header.ReadUInt32() == 0, "DDS alpha-mode metadata has no supported resource counterpart.");
            payloadOffset = 148;
        }
        else if ((pixelFlags & 4) != 0)
        {
            format = TextureFormats.FromD3DFormat(fourCc);
            if (fourCc is 0x55344342 or 0x53344342)
                format = fourCc == 0x55344342 ? TextureFormat.BC4Unorm : TextureFormat.BC4Snorm;
            if (fourCc is 0x55354342 or 0x53354342)
                format = fourCc == 0x55354342 ? TextureFormat.BC5Unorm : TextureFormat.BC5Snorm;
        }
        else
        {
            format = (bits, red, green, blue, alpha) switch
            {
                (32, 0xFF0000, 0xFF00, 0xFF, 0xFF000000) when (pixelFlags & 0x40) != 0 => TextureFormat.B8G8R8A8Unorm,
                (32, 0xFF, 0xFF00, 0xFF0000, 0xFF000000) when (pixelFlags & 0x40) != 0 => TextureFormat.R8G8B8A8Unorm,
                (32, 0x3FF, 0xFFC00, 0x3FF00000, 0xC0000000) when (pixelFlags & 0x40) != 0 => TextureFormat.R10G10B10A2Unorm,
                (16, 0xF800, 0x7E0, 0x1F, 0) when (pixelFlags & 0x40) != 0 => TextureFormat.B5G6R5Unorm,
                (16, 0x7C00, 0x3E0, 0x1F, 0x8000) when (pixelFlags & 0x40) != 0 => TextureFormat.B5G5R5A1Unorm,
                (8, 0xFF, 0, 0, 0) when (pixelFlags & 0x20000) != 0 => TextureFormat.R8Unorm,
                (8, 0, 0, 0, 0xFF) when (pixelFlags & 2) != 0 => TextureFormat.A8Unorm,
                _ => TextureFormat.Unknown
            };
        }
        BinaryLayout.Require(TextureFormats.TryUnit(format, out _, out int unitWidth), "DDS encoded pixel format is unsupported.");
        BinaryLayout.Require(volume ? depth > 0 && arrays == 1 && (flags & 0x800000) != 0 : depth <= 1 && (flags & 0x800000) == 0,
            "DDS depth flags disagree with its topology.");
        TextureSurface result;
        try
        {
            result = new((int)width, (int)height, format, (int)mipCount, data[payloadOffset..].ToArray(),
                new TextureShape(volume ? TextureDimension.Volume : cube ? TextureDimension.Cube : TextureDimension.TwoD, volume ? (int)depth : 1, arrays));
        }
        catch (ArgumentException exception) { throw new InvalidDataException("DDS dimensions, mip chain or encoded payload length is invalid.", exception); }
        catch (OverflowException exception) { throw new InvalidDataException("DDS dimensions exceed the supported payload size.", exception); }
        // Block-compressed volumes normally store one top-level slice; a whole-volume size is also accepted.
        int expectedPitch = unitWidth == 4 ? result.Subresources[0].SlicePitch : result.Mips[0].RowPitch;
        if ((flags & (unitWidth == 4 ? 0x80000U : 8U)) != 0)
            BinaryLayout.Require(pitch == expectedPitch || unitWidth == 4 && pitch == result.Mips[0].Length,
                "DDS pitch does not match its contiguous encoded mip layout.");
        return result;
    }

    /// <inheritdoc cref="Read(ReadOnlySpan{byte})"/>
    public static TextureSurface Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Read(File.ReadAllBytes(path));
    }

    /// <summary>Allocates a canonical DX10 DDS header and copies the encoded payload without transcoding.</summary>
    public static byte[] Write(TextureSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var data = new byte[checked(148 + surface.Data.Length)];
        var header = new BinarySpanWriter(data);
        TextureFormats.TryUnit(surface.Format, out _, out int unitWidth);
        uint flags = 0x1007U | (unitWidth == 4 ? 0x80000U : 8U) | (surface.MipCount > 1 ? 0x20000U : 0);
        bool volume = surface.Shape.Dimension == TextureDimension.Volume, cube = surface.Shape.Dimension == TextureDimension.Cube;
        if (volume)
            flags |= 0x800000;
        header.WriteUInt32(Magic);
        header.WriteUInt32(124);
        header.WriteUInt32(flags);
        header.WriteUInt32((uint)surface.Height);
        header.WriteUInt32((uint)surface.Width);
        header.WriteUInt32((uint)(unitWidth == 4 ? surface.Subresources[0].SlicePitch : surface.Mips[0].RowPitch));
        header.WriteUInt32(volume ? (uint)surface.Shape.Depth : 0);
        header.WriteUInt32((uint)surface.MipCount);
        header = new(data.AsSpan(76));
        header.WriteUInt32(32);
        header.WriteUInt32(4);
        header.WriteUInt32(Dx10);
        header = new(data.AsSpan(108));
        header.WriteUInt32(0x1000U | (surface.MipCount > 1 ? 0x400008U : 0));
        header.WriteUInt32(cube ? 0xFE00U : volume ? 0x200000U : 0);
        if (!surface.Shape.IsSingleImage)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(108), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(108)) | 8U);
        header = new(data.AsSpan(128));
        header.WriteUInt32((uint)surface.Format);
        header.WriteUInt32(volume ? 4U : 3U);
        header.WriteUInt32(cube ? 4U : 0U);
        header.WriteUInt32((uint)surface.Shape.ArraySize);
        header.WriteUInt32(0);
        surface.Data.Span.CopyTo(data.AsSpan(148));
        return data;
    }

    /// <summary>Encodes a surface as DDS and replaces the destination file atomically where supported.
    /// With <paramref name="overwrite"/> false an existing destination is left unchanged and <see cref="IOException"/> is thrown.</summary>
    public static void Save(string path, TextureSurface surface, bool overwrite = true) => AtomicFile.Write(path, Write(surface), overwrite);
}
