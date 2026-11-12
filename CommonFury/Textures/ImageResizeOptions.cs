namespace CommonFury.Textures;

/// <summary>Image file format for output.</summary>
public enum ImageFileFormat { Png, Jpeg, Bmp, Tga }
/// <summary>Resampling filter used when resizing.</summary>
public enum ImageFilter { Box, Triangle, CubicBSpline, CatmullRom, Mitchell, Point }
/// <summary>Color space used to interpret channel values while processing images.</summary>
public enum ImageColorSpace { Linear, Srgb }

/// <summary>Resize filter and the color space used to filter RGB; alpha is always linear.</summary>
public sealed record ImageResizeOptions(ImageFilter Filter = ImageFilter.Mitchell, ImageColorSpace ColorSpace = ImageColorSpace.Srgb)
{
    /// <exception cref="ArgumentOutOfRangeException">The filter or color space is not a defined value.</exception>
    public void ThrowIfInvalid()
    {
        if (!Enum.IsDefined(Filter))
            throw new ArgumentOutOfRangeException(nameof(Filter));
        if (!Enum.IsDefined(ColorSpace))
            throw new ArgumentOutOfRangeException(nameof(ColorSpace));
    }
}
