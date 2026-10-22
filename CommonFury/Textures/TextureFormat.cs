namespace CommonFury.Textures;

/// <summary>DXGI encoded-pixel identities. Unknown numeric values are retained; support is checked where a layout is needed.</summary>
public enum TextureFormat : uint
{
    Unknown = 0,
    R32G32B32A32Float = 2,
    R16G16B16A16Float = 10,
    R16G16B16A16Unorm = 11,
    R32G32Float = 16,
    R10G10B10A2Unorm = 24,
    R11G11B10Float = 26,
    R8G8B8A8Unorm = 28,
    R8G8B8A8UnormSrgb = 29,
    R16G16Float = 34,
    R16G16Unorm = 35,
    R32Float = 41,
    R8G8Unorm = 49,
    R16Float = 54,
    R16Unorm = 56,
    R8Unorm = 61,
    A8Unorm = 65,
    /// <summary>BC1 (DXT1) blocks.</summary>
    BC1Unorm = 71,
    BC1UnormSrgb = 72,
    /// <summary>BC2 (DXT3) blocks.</summary>
    BC2Unorm = 74,
    BC2UnormSrgb = 75,
    /// <summary>BC3 (DXT5) blocks.</summary>
    BC3Unorm = 77,
    BC3UnormSrgb = 78,
    BC4Unorm = 80,
    BC4Snorm = 81,
    BC5Unorm = 83,
    BC5Snorm = 84,
    B5G6R5Unorm = 85,
    B5G5R5A1Unorm = 86,
    B8G8R8A8Unorm = 87,
    B8G8R8X8Unorm = 88,
    B8G8R8A8UnormSrgb = 91,
    B8G8R8X8UnormSrgb = 93,
    BC6HUf16 = 95,
    BC6HSf16 = 96,
    BC7Unorm = 98,
    BC7UnormSrgb = 99
}

/// <summary>Encoding properties of <see cref="TextureFormat"/> values and the Direct3D 9 format codes used by older files.</summary>
public static class TextureFormats
{
    /// <summary>Returns the bytes per encoding unit and the unit's edge in pixels: 4 for block-compressed formats, 1 otherwise.
    /// Returns false, with zero bytes, for formats without a supported layout.</summary>
    public static bool TryUnit(TextureFormat format, out int bytes, out int dimension)
    {
        dimension = IsBlockCompressed(format) ? 4 : 1;
        bytes = format switch
        {
            TextureFormat.BC1Unorm or TextureFormat.BC1UnormSrgb or TextureFormat.BC4Unorm or TextureFormat.BC4Snorm => 8,
            TextureFormat.BC2Unorm or TextureFormat.BC2UnormSrgb or TextureFormat.BC3Unorm or TextureFormat.BC3UnormSrgb
                or TextureFormat.BC5Unorm or TextureFormat.BC5Snorm or TextureFormat.BC6HUf16 or TextureFormat.BC6HSf16
                or TextureFormat.BC7Unorm or TextureFormat.BC7UnormSrgb => 16,
            TextureFormat.R8Unorm or TextureFormat.A8Unorm => 1,
            TextureFormat.B5G6R5Unorm or TextureFormat.B5G5R5A1Unorm or TextureFormat.R8G8Unorm or TextureFormat.R16Float or TextureFormat.R16Unorm => 2,
            TextureFormat.B8G8R8A8Unorm or TextureFormat.B8G8R8A8UnormSrgb or TextureFormat.R8G8B8A8Unorm
                or TextureFormat.B8G8R8X8Unorm or TextureFormat.B8G8R8X8UnormSrgb or TextureFormat.R8G8B8A8UnormSrgb
                or TextureFormat.R10G10B10A2Unorm or TextureFormat.R11G11B10Float or TextureFormat.R16G16Float
                or TextureFormat.R16G16Unorm or TextureFormat.R32Float => 4,
            TextureFormat.R16G16B16A16Float or TextureFormat.R16G16B16A16Unorm or TextureFormat.R32G32Float => 8,
            TextureFormat.R32G32B32A32Float => 16,
            _ => 0
        };
        return bytes != 0;
    }

    /// <summary>BC1–BC7: 4×4 pixel blocks.</summary>
    public static bool IsBlockCompressed(TextureFormat format) => format is >= TextureFormat.BC1Unorm and <= TextureFormat.BC5Snorm
        or >= TextureFormat.BC6HUf16 and <= TextureFormat.BC7UnormSrgb;

    /// <summary>Whether colour channels use the sRGB transfer function; alpha is always linear.</summary>
    public static bool IsSrgb(TextureFormat format) => format is TextureFormat.BC1UnormSrgb or TextureFormat.BC2UnormSrgb
        or TextureFormat.BC3UnormSrgb or TextureFormat.BC7UnormSrgb or TextureFormat.R8G8B8A8UnormSrgb
        or TextureFormat.B8G8R8A8UnormSrgb or TextureFormat.B8G8R8X8UnormSrgb;

    /// <summary>Returns the linear-colour counterpart of an sRGB format, or the format itself.</summary>
    public static TextureFormat Linear(TextureFormat format) => format switch
    {
        TextureFormat.BC1UnormSrgb => TextureFormat.BC1Unorm,
        TextureFormat.BC2UnormSrgb => TextureFormat.BC2Unorm,
        TextureFormat.BC3UnormSrgb => TextureFormat.BC3Unorm,
        TextureFormat.BC7UnormSrgb => TextureFormat.BC7Unorm,
        TextureFormat.R8G8B8A8UnormSrgb => TextureFormat.R8G8B8A8Unorm,
        TextureFormat.B8G8R8A8UnormSrgb => TextureFormat.B8G8R8A8Unorm,
        TextureFormat.B8G8R8X8UnormSrgb => TextureFormat.B8G8R8X8Unorm,
        _ => format
    };

    /// <summary>Returns the sRGB counterpart of a linear format, or the format itself when none exists.</summary>
    public static TextureFormat Srgb(TextureFormat format) => format switch
    {
        TextureFormat.BC1Unorm => TextureFormat.BC1UnormSrgb,
        TextureFormat.BC2Unorm => TextureFormat.BC2UnormSrgb,
        TextureFormat.BC3Unorm => TextureFormat.BC3UnormSrgb,
        TextureFormat.BC7Unorm => TextureFormat.BC7UnormSrgb,
        TextureFormat.R8G8B8A8Unorm => TextureFormat.R8G8B8A8UnormSrgb,
        TextureFormat.B8G8R8A8Unorm => TextureFormat.B8G8R8A8UnormSrgb,
        TextureFormat.B8G8R8X8Unorm => TextureFormat.B8G8R8X8UnormSrgb,
        _ => format
    };

    /// <summary>Returns the Direct3D 9 format code (a D3DFMT number or FourCC) for the format's linear counterpart, or 0 when none exists.
    /// Besides the standard codes this includes RAGE FourCCs: <c>BC6 </c>, <c>BC7 </c>, <c>G8R8</c> and <c>R16 </c>.</summary>
    public static uint ToD3DFormat(TextureFormat format) => Linear(format) switch
    {
        TextureFormat.BC1Unorm => 0x31545844,
        TextureFormat.BC2Unorm => 0x33545844,
        TextureFormat.BC3Unorm => 0x35545844,
        TextureFormat.BC4Unorm => 0x31495441,
        TextureFormat.BC5Unorm => 0x32495441,
        TextureFormat.BC7Unorm => 0x20374342,
        TextureFormat.B8G8R8A8Unorm => 21,
        TextureFormat.B5G6R5Unorm => 23,
        TextureFormat.B5G5R5A1Unorm => 25,
        TextureFormat.A8Unorm => 28,
        TextureFormat.R8Unorm => 50,
        TextureFormat.R10G10B10A2Unorm => 31,
        TextureFormat.R8G8B8A8Unorm => 32,
        TextureFormat.R16G16Unorm => 34,
        TextureFormat.R16G16B16A16Unorm => 36,
        TextureFormat.B8G8R8X8Unorm => 22,
        TextureFormat.R8G8Unorm => 0x38523847,
        TextureFormat.R16Unorm => 0x20363152,
        TextureFormat.BC6HUf16 => 0x20364342,
        TextureFormat.R16Float => 111,
        TextureFormat.R16G16Float => 112,
        TextureFormat.R16G16B16A16Float => 113,
        TextureFormat.R32Float => 114,
        TextureFormat.R32G32Float => 115,
        TextureFormat.R32G32B32A32Float => 116,
        _ => 0
    };

    /// <summary>Maps a Direct3D 9 format code (a D3DFMT number or FourCC, including the RAGE FourCCs accepted by
    /// <see cref="ToD3DFormat"/> and <c>R8  </c>) to a linear format, or <see cref="TextureFormat.Unknown"/>.</summary>
    public static TextureFormat FromD3DFormat(uint value) => value switch
    {
        0x31545844 => TextureFormat.BC1Unorm,
        0x33545844 => TextureFormat.BC2Unorm,
        0x35545844 => TextureFormat.BC3Unorm,
        0x31495441 => TextureFormat.BC4Unorm,
        0x32495441 => TextureFormat.BC5Unorm,
        0x20374342 => TextureFormat.BC7Unorm,
        21 => TextureFormat.B8G8R8A8Unorm,
        23 => TextureFormat.B5G6R5Unorm,
        25 => TextureFormat.B5G5R5A1Unorm,
        28 => TextureFormat.A8Unorm,
        50 => TextureFormat.R8Unorm,
        31 => TextureFormat.R10G10B10A2Unorm,
        32 => TextureFormat.R8G8B8A8Unorm,
        34 => TextureFormat.R16G16Unorm,
        36 => TextureFormat.R16G16B16A16Unorm,
        22 => TextureFormat.B8G8R8X8Unorm,
        0x38523847 => TextureFormat.R8G8Unorm,
        0x20363152 => TextureFormat.R16Unorm,
        0x20203852 => TextureFormat.R8Unorm,
        0x20364342 => TextureFormat.BC6HUf16,
        111 => TextureFormat.R16Float,
        112 => TextureFormat.R16G16Float,
        113 => TextureFormat.R16G16B16A16Float,
        114 => TextureFormat.R32Float,
        115 => TextureFormat.R32G32Float,
        116 => TextureFormat.R32G32B32A32Float,
        _ => TextureFormat.Unknown
    };
}
