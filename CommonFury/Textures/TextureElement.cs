namespace CommonFury.Textures;

/// <summary>The unit a texture format stores: bytes per element and the pixel footprint one element covers. Uncompressed formats
/// have 1×1 elements; block-compressed formats store one block per element.</summary>
/// <remarks>Layout code that does not interpret pixels, such as <see cref="BlockLinear"/>, works in elements, so it serves DXGI formats
/// and formats outside DXGI, such as ASTC, alike.</remarks>
public readonly record struct TextureElement
{
    /// <summary>Creates an element of positive size and footprint.</summary>
    public TextureElement(int bytes, int width = 1, int height = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Bytes = bytes;
        Width = width;
        Height = height;
    }

    /// <summary>Uses the encoding unit of a DXGI format.</summary>
    /// <exception cref="NotSupportedException">The format has no supported encoded layout.</exception>
    public TextureElement(TextureFormat format)
    {
        if (!TextureFormats.TryUnit(format, out int bytes, out int dimension))
            throw new NotSupportedException($"Texture format {(uint)format} has no supported encoded layout.");
        Bytes = bytes;
        Width = Height = dimension;
    }

    public int Bytes { get; }
    /// <summary>Footprint width in pixels.</summary>
    public int Width { get; }
    /// <summary>Footprint height in pixels.</summary>
    public int Height { get; }

    /// <summary>Elements per row of an image, counting a partial element at the edge.</summary>
    public int CountX(int pixelWidth) => checked((pixelWidth + Width - 1) / Width);

    /// <summary>Element rows of an image, counting a partial row at the edge.</summary>
    public int CountY(int pixelHeight) => checked((pixelHeight + Height - 1) / Height);
}
