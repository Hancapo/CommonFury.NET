namespace CommonFury.Textures;

/// <summary>BC1 alpha: opaque, or one-bit punch-through where blocks with alpha below 128 use three-colour mode.</summary>
public enum Bc1AlphaMode { Opaque, PunchThrough }

/// <summary>RGBA8 encoding policy for <see cref="TextureCodec.Encode(RgbaImage, TextureEncodingOptions?)"/>.</summary>
/// <remarks>Channel values are kept as supplied; <c>Format</c> decides their linear/sRGB interpretation when generating mips.
/// <c>Quality</c> is encoder effort, not an error bound. The chain stops once the largest dimension reaches <c>MinimumMipDimension</c>.</remarks>
public sealed record TextureEncodingOptions
{
    public TextureFormat Format { get; init; } = TextureFormat.BC7UnormSrgb;
    /// <summary>Encoder effort from 0 (fastest) to 1 (best).</summary>
    public float Quality { get; init; } = 1;
    public bool GenerateMipmaps { get; init; } = true;
    public int MinimumMipDimension { get; init; } = 1;
    public ImageFilter MipFilter { get; init; } = ImageFilter.Mitchell;
    public Bc1AlphaMode Bc1Alpha { get; init; } = Bc1AlphaMode.Opaque;
    /// <summary>Most threads one encode may use: -1 lets the shared thread pool decide, 1 encodes on the calling thread. The
    /// encoded bytes are the same for every value.</summary>
    public int MaxDegreeOfParallelism { get; init; } = -1;

    /// <summary>Checks every option; <paramref name="requireEncoder"/> also requires <see cref="TextureCodec.CanEncode"/> for
    /// <see cref="Format"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its range or not a defined enum value.</exception>
    /// <exception cref="ArgumentException">Punch-through alpha is requested for a format other than BC1.</exception>
    /// <exception cref="NotSupportedException">The format has no encoder and <paramref name="requireEncoder"/> is set.</exception>
    public void ThrowIfInvalid(bool requireEncoder = true)
    {
        if (MaxDegreeOfParallelism is 0 or < -1)
            throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism));
        if (!float.IsFinite(Quality) || Quality is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(Quality));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinimumMipDimension);
        if (!Enum.IsDefined(MipFilter))
            throw new ArgumentOutOfRangeException(nameof(MipFilter));
        if (!Enum.IsDefined(Bc1Alpha))
            throw new ArgumentOutOfRangeException(nameof(Bc1Alpha));
        if (requireEncoder && !TextureCodec.CanEncode(Format))
            throw new NotSupportedException($"Texture format {Format} has no supported pixel encoder.");
        if (Bc1Alpha != Bc1AlphaMode.Opaque && Format is not (TextureFormat.BC1Unorm or TextureFormat.BC1UnormSrgb))
            throw new ArgumentException("Punch-through mode applies only to BC1.", nameof(Bc1Alpha));
    }
}
