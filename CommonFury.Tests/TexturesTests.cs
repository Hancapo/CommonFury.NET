using System.Buffers.Binary;
using CommonFury.Textures;
using Xunit;

namespace CommonFury.Tests;

public sealed class TexturesTests
{
    [Fact]
    public void MipLayoutsIncludePartialBlocksAndShrinkVolumeDepth()
    {
        TextureMip[] mips = TextureLayout.CreateMips(7, 5, TextureFormat.BC1Unorm, 3);
        Assert.Equal(new[] { 32, 8, 8 }, mips.Select(mip => mip.Length));
        Assert.Equal(new[] { 0, 32, 40 }, mips.Select(mip => mip.Offset));
        Assert.Equal(new[] { 16, 8, 8 }, mips.Select(mip => mip.RowPitch));
        Assert.Equal(48, TextureLayout.DataSize(7, 5, TextureFormat.BC1Unorm, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureLayout.DataSize(1, 1, TextureFormat.BC1Unorm, 2));
        var volume = TextureLayout.CreateSubresources(2, 2, TextureFormat.R8Unorm, 3, new TextureShape(TextureDimension.Volume, 4));
        Assert.Equal(new[] { 16, 2, 1 }, volume.Select(part => part.Mip.Length));
        Assert.Equal(new[] { 0, 16, 18 }, volume.Select(part => part.Mip.Offset));
        Assert.Equal(3, TextureLayout.CalculateMipCount(2, 2, new TextureShape(TextureDimension.Volume, 4)));
        Assert.Equal(4, TextureLayout.CalculateMipCount(8, 3));
        Assert.Throws<ArgumentException>(() => TextureLayout.CreateSubresources(4, 2, TextureFormat.BC1Unorm, 1, new TextureShape(TextureDimension.Cube)));
        Assert.Throws<NotSupportedException>(() => TextureLayout.DataSize(4, 4, (TextureFormat)255, 1));
    }

    [Fact]
    public void SurfaceSizesMatchTightlyPackedRows()
    {
        Assert.Equal(60, TextureLayout.SurfaceSize(5, 3, TextureFormat.R11G11B10Float));
        Assert.Equal(16, TextureLayout.RowPitch(5, TextureFormat.BC1Unorm));
        Assert.Equal(32, TextureLayout.SurfaceSize(5, 5, TextureFormat.BC1Unorm));
        Assert.Equal(TextureLayout.DataSize(9, 7, TextureFormat.BC7Unorm, 1), TextureLayout.SurfaceSize(9, 7, TextureFormat.BC7Unorm));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextureLayout.RowPitch(0, TextureFormat.R8Unorm));
    }

    [Fact]
    public void FormatPropertiesAndD3DCodesRoundTrip()
    {
        foreach (TextureFormat format in Enum.GetValues<TextureFormat>())
        {
            Assert.False(TextureFormats.IsSrgb(TextureFormats.Linear(format)));
            if (TextureFormats.IsSrgb(format))
                Assert.Equal(format, TextureFormats.Srgb(TextureFormats.Linear(format)));
            uint code = TextureFormats.ToD3DFormat(format);
            if (code != 0)
                Assert.Equal(TextureFormats.Linear(format), TextureFormats.FromD3DFormat(code));
            Assert.Equal(format != TextureFormat.Unknown, TextureFormats.TryUnit(format, out _, out _));
        }
        Assert.Equal(TextureFormat.R8Unorm, TextureFormats.FromD3DFormat(0x20203852));
        Assert.Equal(TextureFormat.Unknown, TextureFormats.FromD3DFormat(12345));
        Assert.True(TextureFormats.IsBlockCompressed(TextureFormat.BC6HSf16) && !TextureFormats.IsBlockCompressed(TextureFormat.B5G6R5Unorm));
    }

    [Fact]
    public void SurfacesReferencePayloadsAndCheckTheirLength()
    {
        byte[] bytes = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();
        var surface = new TextureSurface(7, 5, TextureFormat.BC1Unorm, 3, bytes);
        bytes[47] = 99;
        Assert.Equal(99, surface.Data.Span[47]);
        Assert.Equal(8, surface.GetEncodedSlice(mipLevel: 2).Length);
        Assert.Throws<ArgumentException>(() => new TextureSurface(7, 5, TextureFormat.BC1Unorm, 3, new byte[47]));
        Assert.Throws<NotSupportedException>(() => new TextureSurface(4, 4, (TextureFormat)255, 1, new byte[3]));
        var volume = new TextureSurface(2, 2, TextureFormat.R8Unorm, 3, new byte[19], new TextureShape(TextureDimension.Volume, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => volume.GetEncodedSlice(mipLevel: 1, depthSlice: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => volume.GetSubresource(arrayIndex: 1));
    }

    [Theory]
    [InlineData(TextureFormat.BC1Unorm)]
    [InlineData(TextureFormat.BC3UnormSrgb)]
    [InlineData(TextureFormat.BC7Unorm)]
    [InlineData(TextureFormat.R16G16B16A16Float)]
    [InlineData(TextureFormat.B8G8R8A8Unorm)]
    [InlineData(TextureFormat.R11G11B10Float)]
    public void DdsKeepsExactEncodedPayloadAndColorSpace(TextureFormat format)
    {
        int size = TextureLayout.DataSize(8, 4, format, 3);
        byte[] payload = Enumerable.Range(0, size).Select(i => (byte)(i * 31)).ToArray();
        byte[] dds = DdsCodec.Write(new TextureSurface(8, 4, format, 3, payload));
        Assert.Equal(0x20534444U, BinaryPrimitives.ReadUInt32LittleEndian(dds));
        Assert.Equal(124U, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(4)));
        TextureSurface read = DdsCodec.Read(dds);
        Assert.Equal(format, read.Format);
        Assert.Equal(3, read.MipCount);
        Assert.Equal(payload, read.Data.ToArray());
        Assert.Throws<InvalidDataException>(() => DdsCodec.Read(dds.AsSpan(0, dds.Length - 1)));
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(112), 0x200);
        Assert.Throws<InvalidDataException>(() => DdsCodec.Read(dds));
    }

    [Theory]
    [InlineData(TextureDimension.TwoD, 1, 3)]
    [InlineData(TextureDimension.Cube, 1, 1)]
    [InlineData(TextureDimension.Cube, 1, 2)]
    [InlineData(TextureDimension.Volume, 4, 1)]
    public void DdsPreservesAllSubresources(TextureDimension dimension, int depth, int arrays)
    {
        var shape = new TextureShape(dimension, depth, arrays);
        byte[] data = new byte[TextureLayout.DataSize(4, 4, TextureFormat.R8G8B8A8Unorm, 3, shape)];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 251);
        var dds = DdsCodec.Read(DdsCodec.Write(new TextureSurface(4, 4, TextureFormat.R8G8B8A8Unorm, 3, data, shape)));
        Assert.Equal(shape, dds.Shape);
        Assert.True(data.AsSpan().SequenceEqual(dds.Data.Span));
    }

    [Fact]
    public void BlockCompressedVolumesUseTheStandardPerSliceLinearSize()
    {
        var surface = new TextureSurface(64, 64, TextureFormat.BC1Unorm, 1, new byte[4 * 2048], new TextureShape(TextureDimension.Volume, 4));
        byte[] file = DdsCodec.Write(surface);
        // One 64x64 BC1 slice: 16 x 16 blocks of 8 bytes.
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20)));
        Assert.Equal(4, DdsCodec.Read(file).Shape.Depth);
        // A whole top-level volume as the linear size is also accepted.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), 4 * 2048);
        Assert.Equal(4, DdsCodec.Read(file).Shape.Depth);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), 1234);
        Assert.Throws<InvalidDataException>(() => DdsCodec.Read(file));
    }

    [Fact]
    public void LegacyCubeHeadersRequireEveryFace()
    {
        var cube = new TextureSurface(4, 4, TextureFormat.BC1Unorm, 1, new byte[48], new TextureShape(TextureDimension.Cube));
        byte[] dx10 = DdsCodec.Write(cube);
        byte[] legacy = new byte[128 + cube.Data.Length];
        dx10.AsSpan(0, 128).CopyTo(legacy);
        cube.Data.Span.CopyTo(legacy.AsSpan(128));
        BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(84), 0x31545844);
        Assert.Equal(cube.Shape, DdsCodec.Read(legacy).Shape);
        BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(112), 0x200);
        Assert.Throws<InvalidDataException>(() => DdsCodec.Read(legacy));
    }

    // Independently written DDS files with legacy (non-DX10) headers.
    [Theory]
    [InlineData("dds-Mixed_Diffuse.dds", TextureFormat.BC1Unorm, 32)]
    [InlineData("dds-normal.dds", TextureFormat.BC3Unorm, 80)]
    [InlineData("dds-Color.dds", TextureFormat.B8G8R8A8Unorm, 84)]
    public void ReadsPinnedLegacyDdsHeaders(string name, TextureFormat format, int size)
    {
        TextureSurface read = DdsCodec.Read(Path.Combine(AppContext.BaseDirectory, "ReferenceData", name));
        Assert.Equal(format, read.Format);
        Assert.Equal(Enumerable.Range(0, size).Select(i => (byte)i), read.Data.ToArray());
    }

    [Theory]
    [InlineData(TextureFormat.BC1Unorm, 8, 8, 1)]
    [InlineData(TextureFormat.BC7UnormSrgb, 16, 4, 3)]
    [InlineData(TextureFormat.B8G8R8A8Unorm, 6, 3, 2)]
    [InlineData(TextureFormat.R11G11B10Float, 4, 4, 3)]
    public void WriterReproducesReferenceSingleSurfaceDds(TextureFormat format, int width, int height, int mipCount)
    {
        byte[] payload = Enumerable.Range(0, TextureLayout.DataSize(width, height, format, mipCount)).Select(i => (byte)(i * 7)).ToArray();
        var surface = new TextureSurface(width, height, format, mipCount, payload);
        Assert.Equal(ReferenceDdsBytes(surface), DdsCodec.Write(surface));
    }

    [Fact]
    public void SaveHonorsOverwrite()
    {
        string directory = Directory.CreateTempSubdirectory("commonfury-dds-").FullName;
        try
        {
            string path = Path.Combine(directory, "a.dds");
            var surface = new TextureSurface(4, 4, TextureFormat.BC1Unorm, 1, new byte[8]);
            DdsCodec.Save(path, surface);
            Assert.Throws<IOException>(() => DdsCodec.Save(path, surface, overwrite: false));
            Assert.Equal(DdsCodec.Write(surface), File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    // Independent single-surface DX10 header writer used as the oracle.
    private static byte[] ReferenceDdsBytes(TextureSurface surface)
    {
        byte[] result = new byte[checked(148 + surface.Data.Length)];
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), value);
        bool compressed = TextureFormats.IsBlockCompressed(surface.Format), chain = surface.MipCount > 1;
        U32(0, 0x20534444); U32(4, 124);
        U32(8, 0x1007u | (compressed ? 0x80000u : 8u) | (chain ? 0x20000u : 0u));
        U32(12, (uint)surface.Height); U32(16, (uint)surface.Width);
        U32(20, (uint)(compressed ? surface.Mips[0].Length : TextureLayout.RowPitch(surface.Width, surface.Format)));
        U32(28, (uint)surface.MipCount); U32(76, 32); U32(80, 4); U32(84, 0x30315844);
        U32(108, 0x1000u | (chain ? 0x400008u : 0u));
        U32(128, (byte)surface.Format); U32(132, 3); U32(140, 1);
        surface.Data.Span.CopyTo(result.AsSpan(148));
        return result;
    }
}
