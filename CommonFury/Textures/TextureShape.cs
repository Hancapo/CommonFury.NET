namespace CommonFury.Textures;

/// <summary>Encoded image organization: planar images, six-face cubes, or depth slices.</summary>
public enum TextureDimension
{
    TwoD = 0,
    /// <summary>Six square faces per array element, ordered +X, -X, +Y, -Y, +Z, -Z.</summary>
    Cube = 1,
    /// <summary>Depth slices whose count halves with each mip.</summary>
    Volume = 3
}

/// <summary>Texture topology. ArraySize counts images or complete cubes; volumes have one array element.</summary>
public readonly record struct TextureShape
{
    public TextureDimension Dimension { get; }
    /// <summary>Top-mip depth slice count; one unless the texture is a volume.</summary>
    public int Depth { get; }
    /// <summary>Number of images or complete cubes.</summary>
    public int ArraySize { get; }
    /// <summary>Six for cubes, otherwise one.</summary>
    public int Faces => Dimension == TextureDimension.Cube ? 6 : 1;
    /// <summary><see cref="ArraySize"/> × <see cref="Faces"/>.</summary>
    public int LayerCount => checked(ArraySize * Faces);
    public bool IsSingleImage => Dimension == TextureDimension.TwoD && ArraySize == 1;
    public static TextureShape TwoD => new(TextureDimension.TwoD);

    /// <summary>Creates a topology with positive depth and array size. Only volumes can have depth greater than one.</summary>
    public TextureShape(TextureDimension dimension, int depth = 1, int arraySize = 1)
    {
        if (!Enum.IsDefined(dimension))
            throw new ArgumentOutOfRangeException(nameof(dimension));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(arraySize);
        if (dimension == TextureDimension.Cube && arraySize > int.MaxValue / 6)
            throw new ArgumentOutOfRangeException(nameof(arraySize));
        if (dimension != TextureDimension.Volume && depth != 1 || dimension == TextureDimension.Volume && arraySize != 1)
            throw new ArgumentException("Only volumes have depth slices; volumes cannot be arrayed.");
        Dimension = dimension;
        Depth = depth;
        ArraySize = arraySize;
    }
}

/// <summary>One array-face mip, including all depth slices.</summary>
/// <param name="ArrayIndex">Array element.</param>
/// <param name="Face">Cube face, or zero.</param>
/// <param name="MipLevel">Mip level, zero being the largest.</param>
/// <param name="Depth">Depth slices stored for this mip.</param>
/// <param name="SlicePitch">Bytes per depth slice.</param>
/// <param name="Mip">Dimensions, pitch and byte range of this mip.</param>
public readonly record struct TextureSubresource(int ArrayIndex, int Face, int MipLevel, int Depth, int SlicePitch, TextureMip Mip);
