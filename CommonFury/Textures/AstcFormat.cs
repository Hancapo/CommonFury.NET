namespace CommonFury.Textures;

/// <summary>ASTC decode profile, which fixes how endpoints expand and which encodings are valid.</summary>
public enum AstcProfile
{
    /// <summary>Linear low dynamic range; HDR content decodes to the error colour.</summary>
    Ldr,
    /// <summary>Low dynamic range with sRGB-encoded colour channels; HDR content decodes to the error colour.</summary>
    LdrSrgb,
    /// <summary>High dynamic range; LDR content is also valid.</summary>
    Hdr
}

/// <summary>A 2D ASTC format: a block footprint and a profile. Every block is 16 bytes.</summary>
/// <remarks>ASTC has no DXGI identity, so it is described here rather than by <see cref="TextureFormat"/>.</remarks>
public readonly record struct AstcFormat
{
    public const int BlockBytes = 16;

    /// <summary>Accepts the 2D footprints 4×4, 5×4, 5×5, 6×5, 6×6, 8×5, 8×6, 8×8, 10×5, 10×6, 10×8, 10×10, 12×10 and 12×12.</summary>
    public AstcFormat(int blockWidth, int blockHeight, AstcProfile profile)
    {
        if (!IsFootprint(blockWidth, blockHeight))
            throw new ArgumentException($"{blockWidth}x{blockHeight} is not a 2D ASTC block footprint.", nameof(blockWidth));
        if (!Enum.IsDefined(profile))
            throw new ArgumentOutOfRangeException(nameof(profile));
        BlockWidth = blockWidth;
        BlockHeight = blockHeight;
        Profile = profile;
    }

    public int BlockWidth { get; }
    public int BlockHeight { get; }
    public AstcProfile Profile { get; }

    /// <summary>One block per element, for layout code such as <see cref="BlockLinear"/>.</summary>
    public TextureElement Element => new(BlockBytes, BlockWidth, BlockHeight);

    /// <summary>Bytes of a tightly packed image of the given pixel size, including partial edge blocks.</summary>
    public int SurfaceSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return checked(Element.CountX(width) * Element.CountY(height) * BlockBytes);
    }

    private static bool IsFootprint(int width, int height) => (width, height) is (4, 4) or (5, 4) or (5, 5) or (6, 5) or (6, 6)
        or (8, 5) or (8, 6) or (8, 8) or (10, 5) or (10, 6) or (10, 8) or (10, 10) or (12, 10) or (12, 12);
}
