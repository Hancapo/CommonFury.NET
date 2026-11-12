using CommonFury.IO;

namespace CommonFury.Textures;

/// <summary>Owned contiguous 8-bit RGBA image.</summary>
/// <remarks>Constructors copy their input and operations never mutate it; a no-op resize may return the same image.
/// Channel values have no implied color profile.</remarks>
public sealed class RgbaImage
{
    /// <summary>Default limit, in bytes, for encoded input and decoded pixels (256 MiB).</summary>
    public const int DefaultMaximumBytes = 256 * 1024 * 1024;

    private readonly byte[] pixels;
    public int Width { get; }
    public int Height { get; }
    /// <summary>Row-major RGBA8, four bytes per pixel with no row padding.</summary>
    public ReadOnlySpan<byte> Pixels => pixels;
    public bool HasTransparency
    {
        get
        {
            for (int i = 3; i < pixels.Length; i += 4)
                if (pixels[i] != 255)
                    return true;
            return false;
        }
    }

    /// <summary>Copies <paramref name="pixels"/>, which must hold exactly width * height * 4 bytes.</summary>
    public RgbaImage(int width, int height, ReadOnlySpan<byte> pixels)
        : this(width, height, CopyPixels(width, height, pixels)) { }

    private RgbaImage(int width, int height, byte[] ownedPixels)
    {
        if (ownedPixels.Length != ByteCount(width, height))
            throw new ArgumentException("Pixels must contain exactly width * height * 4 RGBA bytes.", nameof(ownedPixels));
        (Width, Height, pixels) = (width, height, ownedPixels);
    }

    internal static RgbaImage FromOwned(int width, int height, byte[] pixels) => new(width, height, pixels);

    internal static int ByteCount(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return checked(width * height * 4);
    }

    private static byte[] CopyPixels(int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != ByteCount(width, height))
            throw new ArgumentException("Pixels must contain exactly width * height * 4 RGBA bytes.", nameof(pixels));
        return pixels.ToArray();
    }

    /// <summary>Decodes PNG, JPEG, BMP or TGA to RGBA8 without color-profile conversion. <paramref name="maximumBytes"/> limits
    /// both the input and the decoded pixels.</summary>
    /// <exception cref="InvalidDataException">The input exceeds the limit or is not a readable image.</exception>
    public static RgbaImage Read(ReadOnlySpan<byte> encoded, int maximumBytes = DefaultMaximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (encoded.Length > maximumBytes)
            throw new InvalidDataException("Encoded image exceeds the configured byte limit.");
        return TextureNative.ReadImage(encoded, maximumBytes);
    }

    /// <inheritdoc cref="Read(ReadOnlySpan{byte}, int)"/>
    public static RgbaImage Read(string path, int maximumBytes = DefaultMaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximumBytes)
            throw new InvalidDataException("Encoded image exceeds the configured byte limit.");
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        return Read(data, maximumBytes);
    }

    /// <summary>JPEG drops alpha; <paramref name="quality"/> (1 to 100) applies only to JPEG.</summary>
    public byte[] Write(ImageFileFormat format = ImageFileFormat.Png, int quality = 90)
    {
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format));
        if (quality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(quality));
        return TextureNative.WriteImage(this, format, quality);
    }

    /// <summary>Replaces the file atomically where supported.</summary>
    public void Save(string path, ImageFileFormat format = ImageFileFormat.Png, int quality = 90) =>
        AtomicFile.Write(path, Write(format, quality));

    /// <summary>Resamples to the given size; the result may not exceed <see cref="DefaultMaximumBytes"/>.</summary>
    public RgbaImage Resize(int width, int height, ImageResizeOptions? options = null)
    {
        options ??= new();
        options.ThrowIfInvalid();
        int length = ByteCount(width, height);
        if (length > DefaultMaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(width), "Resized image exceeds the supported allocation limit.");
        if (width == Width && height == Height)
            return this;
        return FromOwned(width, height, TextureNative.ResizeImage(this, width, height, options));
    }
}
