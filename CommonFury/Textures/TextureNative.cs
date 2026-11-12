using System.Runtime.InteropServices;

namespace CommonFury.Textures;

// Inputs are pinned only per call; native outputs are copied to managed storage and freed with the
// allocator's own export, including on failure.
internal static unsafe partial class TextureNative
{
    private const string Library = "commonfury_texture";
    private static volatile bool versionChecked;

    // Checked on first use rather than in a type initializer, so a missing library reports its own load error.
    private static void RequireVersion()
    {
        if (versionChecked)
            return;
        uint version = CodecVersion();
        if (version != 2)
            throw new NotSupportedException($"Unsupported native texture codec ABI {version}; expected 2.");
        versionChecked = true;
    }

    internal static RgbaImage ReadImage(ReadOnlySpan<byte> encoded, int maximumBytes)
    {
        RequireVersion();
        fixed (byte* input = encoded)
        {
            int status = ImageDecode(input, (nuint)encoded.Length, (nuint)maximumBytes, out NativeBuffer output, out int width, out int height);
            return RgbaImage.FromOwned(width, height, CopyAndRelease(output, status, invalidInput: true));
        }
    }

    internal static byte[] WriteImage(RgbaImage image, ImageFileFormat format, int quality)
    {
        RequireVersion();
        fixed (byte* input = image.Pixels)
        {
            int status = ImageEncode(input, (nuint)image.Pixels.Length, image.Width, image.Height, (int)format, quality, out NativeBuffer output);
            return CopyAndRelease(output, status);
        }
    }

    internal static byte[] ResizeImage(RgbaImage image, int width, int height, ImageResizeOptions options)
    {
        RequireVersion();
        fixed (byte* input = image.Pixels)
        {
            int status = ImageResize(input, (nuint)image.Pixels.Length, image.Width, image.Height, width, height,
                (int)options.Filter, options.ColorSpace == ImageColorSpace.Srgb ? 1 : 0, out NativeBuffer output);
            return CopyAndRelease(output, status);
        }
    }

    internal static void EncodeMip(ReadOnlySpan<byte> rgba, int width, int height, TextureEncodingOptions options, Span<byte> destination)
    {
        RequireVersion();
        fixed (byte* input = rgba)
        {
            int status = EncodeNativeMip(input, (nuint)rgba.Length, width, height,
                (uint)options.Format, (int)options.Bc1Alpha, options.Quality, out NativeBuffer output);
            try
            {
                Check(status);
                if (output.Size != (nuint)destination.Length || output.Data == 0)
                    throw new InvalidDataException("Native encoder returned an inconsistent mip layout.");
                new ReadOnlySpan<byte>((void*)output.Data, destination.Length).CopyTo(destination);
            }
            finally { ReleaseBuffer(output.Data); }
        }
    }

    internal static byte[] DecodeMip(ReadOnlySpan<byte> encoded, int width, int height, TextureFormat format)
    {
        RequireVersion();
        fixed (byte* input = encoded)
        {
            int status = DecodeNativeMip(input, (nuint)encoded.Length, width, height, (uint)format, out NativeBuffer output);
            return CopyAndRelease(output, status, invalidInput: true);
        }
    }

    private static byte[] CopyAndRelease(NativeBuffer output, int status, bool invalidInput = false)
    {
        try
        {
            Check(status, invalidInput);
            if (output.Data == 0 || output.Size > int.MaxValue)
                throw new InvalidDataException("Native codec returned an invalid buffer.");
            return new ReadOnlySpan<byte>((void*)output.Data, (int)output.Size).ToArray();
        }
        finally { ReleaseBuffer(output.Data); }
    }

    private static void Check(int status, bool invalidInput = false)
    {
        if (status == 1)
            return;
        string message = Marshal.PtrToStringUTF8(LastError()) ?? "Native texture operation failed.";
        if (invalidInput)
            throw new InvalidDataException(message);
        throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBuffer { public nint Data; public nuint Size; }

    [LibraryImport(Library, EntryPoint = "cftex_version")]
    private static partial uint CodecVersion();
    [LibraryImport(Library, EntryPoint = "cftex_image_decode")]
    private static partial int ImageDecode(byte* data, nuint length, nuint maximumBytes, out NativeBuffer output, out int width, out int height);
    [LibraryImport(Library, EntryPoint = "cftex_image_encode")]
    private static partial int ImageEncode(byte* data, nuint length, int width, int height, int format, int quality, out NativeBuffer output);
    [LibraryImport(Library, EntryPoint = "cftex_image_resize")]
    private static partial int ImageResize(byte* data, nuint length, int width, int height, int newWidth, int newHeight, int filter, int srgb, out NativeBuffer output);
    [LibraryImport(Library, EntryPoint = "cftex_encode_mip")]
    private static partial int EncodeNativeMip(byte* data, nuint length, int width, int height, uint format, int bc1Alpha, float quality, out NativeBuffer output);
    [LibraryImport(Library, EntryPoint = "cftex_decode_mip")]
    private static partial int DecodeNativeMip(byte* data, nuint length, int width, int height, uint format, out NativeBuffer output);
    [LibraryImport(Library, EntryPoint = "cftex_release_buffer")]
    private static partial void ReleaseBuffer(nint data);
    [LibraryImport(Library, EntryPoint = "cftex_last_error")]
    private static partial nint LastError();
}
