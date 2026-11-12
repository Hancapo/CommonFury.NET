using System.Runtime.ExceptionServices;

namespace CommonFury.Textures;

/// <summary>Encodes RGBA8 images to texture formats with full mip chains, and decodes subresources back to RGBA8.</summary>
/// <remarks>Block-compressed formats use the bundled native codec (Windows x64); uncompressed formats use
/// <see cref="PixelCodec"/>. No container is read or written.</remarks>
public static class TextureCodec
{
    public static bool CanEncode(TextureFormat format) => PixelCodec.Supports(format) || format is
        TextureFormat.BC1Unorm or TextureFormat.BC1UnormSrgb or TextureFormat.BC2Unorm or TextureFormat.BC2UnormSrgb
        or TextureFormat.BC3Unorm or TextureFormat.BC3UnormSrgb or TextureFormat.BC4Unorm or TextureFormat.BC5Unorm
        or TextureFormat.BC7Unorm or TextureFormat.BC7UnormSrgb;

    /// <summary>Decoding also supports BC6H, which cannot be encoded.</summary>
    public static bool CanDecode(TextureFormat format) => CanEncode(format) || format is TextureFormat.BC6HUf16 or TextureFormat.BC6HSf16;

    /// <summary>Encodes RGBA8 values as given; the target format decides their color space, and each generated mip is filtered
    /// from <paramref name="image"/>.</summary>
    /// <returns>A 2D surface over a new array that the caller owns.</returns>
    public static TextureSurface Encode(RgbaImage image, TextureEncodingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        return EncodeMipChain(image, options ?? new(), default, keepFirstMip: false);
    }

    /// <summary>Keeps <paramref name="firstMip"/>, already encoded in the target format, as mip 0 and generates the other
    /// mips from <paramref name="image"/>. <paramref name="firstMip"/> is copied and must have exactly the encoded length of
    /// mip 0.</summary>
    /// <returns>A 2D surface over a new array that the caller owns.</returns>
    public static TextureSurface Encode(RgbaImage image, TextureEncodingOptions options, ReadOnlySpan<byte> firstMip)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        return EncodeMipChain(image, options, firstMip, keepFirstMip: true);
    }

    private static TextureSurface EncodeMipChain(RgbaImage image, TextureEncodingOptions options, ReadOnlySpan<byte> firstMip, bool keepFirstMip)
    {
        options.ThrowIfInvalid();
        int count = options.GenerateMipmaps ? TextureLayout.CalculateMipCount(image.Width, image.Height, options.MinimumMipDimension) : 1;
        TextureMip[] layout = TextureLayout.CreateMips(image.Width, image.Height, options.Format, count);
        if (keepFirstMip && firstMip.Length != layout[0].Length)
            throw new ArgumentException("The first mip must match the requested encoded layout.", nameof(firstMip));
        int length = checked(layout[^1].Offset + layout[^1].Length);
        if (length > RgbaImage.DefaultMaximumBytes)
            throw new ArgumentException("Encoded mip chain exceeds the supported allocation limit.", nameof(image));
        var data = new byte[length];
        int first = 0;
        if (keepFirstMip)
        {
            firstMip.CopyTo(data);
            first = 1;
        }
        var resize = new ImageResizeOptions(options.MipFilter, TextureFormats.IsSrgb(options.Format) ? ImageColorSpace.Srgb : ImageColorSpace.Linear);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism };
        var images = new RgbaImage[count];
        For(first, count, parallel, level => images[level] = image.Resize(layout[level].Width, layout[level].Height, resize));
        List<EncodeStrip> strips = SplitMips(layout, first, options);
        For(0, strips.Count, parallel, index =>
        {
            (int level, int row, int rows) = strips[index];
            TextureMip mip = layout[level];
            ReadOnlySpan<byte> pixels = images[level].Pixels;
            if (PixelCodec.Supports(options.Format))
            {
                PixelCodec.Encode(pixels, options.Format, data.AsSpan(mip.Offset, mip.Length));
                return;
            }
            int y = row * 4, height = Math.Min(rows * 4, mip.Height - y), rowBytes = mip.Width * 4;
            TextureNative.EncodeMip(pixels.Slice(y * rowBytes, height * rowBytes), mip.Width, height, options,
                data.AsSpan(mip.Offset + row * mip.RowPitch, rows * mip.RowPitch));
        });
        return new(image.Width, image.Height, options.Format, count, data);
    }

    private readonly record struct EncodeStrip(int Level, int Row, int Rows);

    private const int StripBlocks = 1024;

    // Blocks encode independently and a strip of whole block rows clamps its partial edge exactly like the full mip, so
    // block-compressed mips split into strips of about StripBlocks blocks that encode in any order to the same bytes. Small
    // mips, uncompressed formats and serial encodes keep one strip per mip.
    private static List<EncodeStrip> SplitMips(TextureMip[] layout, int first, TextureEncodingOptions options)
    {
        bool split = options.MaxDegreeOfParallelism != 1 && !PixelCodec.Supports(options.Format);
        var strips = new List<EncodeStrip>();
        for (int level = first; level < layout.Length; level++)
        {
            int blockRows = (layout[level].Height + 3) / 4;
            int step = split ? Math.Max(1, StripBlocks / ((layout[level].Width + 3) / 4)) : blockRows;
            for (int row = 0; row < blockRows; row += step)
                strips.Add(new(level, row, Math.Min(step, blockRows - row)));
        }
        return strips;
    }

    // Throws the first failure itself rather than an AggregateException, as a serial loop would.
    private static void For(int from, int to, ParallelOptions parallel, Action<int> body)
    {
        try { Parallel.For(from, to, parallel, body); }
        catch (AggregateException error) { ExceptionDispatchInfo.Throw(error.InnerExceptions[0]); }
    }

    /// <summary>Decodes one subresource to RGBA8. HDR and floating-point values are clamped to [0,1] and quantized. BC1
    /// three-colour transparent texels decode to zero; BC4 decodes R as gray and BC5 as RG with B = 0 and A = 255.</summary>
    /// <exception cref="NotSupportedException"><see cref="CanDecode"/> is false for the surface format.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The coordinates are outside the surface topology.</exception>
    public static RgbaImage Decode(TextureSurface surface, int mipLevel = 0, int arrayIndex = 0, int face = 0, int depthSlice = 0)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!CanDecode(surface.Format))
            throw new NotSupportedException($"Texture format {surface.Format} has no supported RGBA8 decoder.");
        TextureMip mip = surface.GetSubresource(mipLevel, arrayIndex, face).Mip;
        int length = RgbaImage.ByteCount(mip.Width, mip.Height);
        if (length > RgbaImage.DefaultMaximumBytes)
            throw new InvalidDataException("Decoded image exceeds the supported allocation limit.");
        ReadOnlySpan<byte> encoded = surface.GetEncodedSlice(mipLevel, arrayIndex, face, depthSlice).Span;
        byte[] pixels;
        if (PixelCodec.Supports(surface.Format))
        {
            pixels = new byte[length];
            PixelCodec.Decode(encoded, surface.Format, pixels);
        }
        else if ((long)mip.Width * mip.Height >= ParallelDecodePixels && encoded.Length == (long)mip.RowPitch * ((mip.Height + 3) / 4))
            pixels = DecodeStrips(encoded, mip, surface.Format, length);
        else
            pixels = TextureNative.DecodeMip(encoded, mip.Width, mip.Height, surface.Format);
        return RgbaImage.FromOwned(mip.Width, mip.Height, pixels);
    }

    private const long ParallelDecodePixels = 512 * 512;
    private const int StripBlockRows = 32;

    // Block rows are contiguous and decode independently, so large block-compressed mips decode as strips of block
    // rows on the thread pool; each strip lands at its pixel rows, giving the same bytes as one serial decode.
    private static unsafe byte[] DecodeStrips(ReadOnlySpan<byte> encoded, TextureMip mip, TextureFormat format, int length)
    {
        var pixels = new byte[length];
        int blockRows = (mip.Height + 3) / 4, strips = (blockRows + StripBlockRows - 1) / StripBlockRows, rowBytes = mip.Width * 4;
        fixed (byte* source = encoded)
        {
            nint input = (nint)source;
            For(0, strips, new ParallelOptions(), strip =>
            {
                int firstRow = strip * StripBlockRows, rows = Math.Min(StripBlockRows, blockRows - firstRow);
                int y = firstRow * 4, height = Math.Min(rows * 4, mip.Height - y);
                var slice = new ReadOnlySpan<byte>((byte*)input + (long)firstRow * mip.RowPitch, rows * mip.RowPitch);
                TextureNative.DecodeMip(slice, mip.Width, height, format).CopyTo(pixels.AsSpan(y * rowBytes, height * rowBytes));
            });
        }
        return pixels;
    }
}
