namespace CommonFury.Vertices;

/// <summary>Storage encoding of one vertex attribute. Normalized encodings decode to their normalized numerical range.</summary>
public enum VertexElementFormat
{
    Float,
    Float2,
    Float3,
    Float4,
    Half,
    Half2,
    /// <summary>Three 16-bit floats; legacy declarations only, no DXGI encoding.</summary>
    Half3,
    Half4,
    /// <summary>Decoded as exact integers 0–255, not normalized (DXGI R8G8B8A8_UINT).</summary>
    Byte4,
    UNorm8x4,
    SNorm8x4,
    /// <summary>Unsigned 10/10/10/2 fields decoded to [0,1] (DXGI R10G10B10A2_UNORM).</summary>
    UNorm10x3A2,
    /// <summary>The same 10/10/10/2 bits remapped to [-1,1] as <c>value * 2 - 1</c>, not sign-extended
    /// (DXGI R10G10B10A2_UNORM storing signed normals).</summary>
    BiasedUNorm10x3A2,
    UNorm16,
    UNorm16x2,
    UNorm8x2,
    /// <summary>Decoded as exact integers, not normalized (DXGI R16G16_SINT).</summary>
    Int16x2,
    /// <summary>Decoded as exact integers, not normalized (DXGI R16G16B16A16_SINT).</summary>
    Int16x4,
    UNorm8,
    SNorm8,
    SNorm8x2,
    SNorm16x2,
    UNorm16x4,
    /// <summary>Signed 10/10/10/2 fields, sign-extended and decoded to [-1,1]: x, y and z as <c>value / 511</c>, w as the 2-bit value
    /// itself, with the most negative codes clamped to -1. No DXGI encoding.</summary>
    SNorm10x3A2,
    /// <summary>Decoded as an exact integer 0–255, not normalized (DXGI R8_UINT).</summary>
    UInt8,
    /// <summary>Decoded as exact integers 0–255, not normalized (DXGI R8G8_UINT).</summary>
    UInt8x2
}

/// <summary>Selects how DXGI R10G10B10A2_UNORM (format 24) vertex data is interpreted. Formats differ, so there is no default.</summary>
public enum Rgb10A2Range
{
    /// <summary>Decode to [0,1]: <see cref="VertexElementFormat.UNorm10x3A2"/>.</summary>
    ZeroToOne,
    /// <summary>Decode to [-1,1]: <see cref="VertexElementFormat.BiasedUNorm10x3A2"/>.</summary>
    MinusOneToOne
}

/// <summary>Sizes of vertex element formats and their DXGI format identifiers in gen9 declarations.</summary>
public static class VertexElementFormats
{
    /// <summary>DXGI format identifier of R10G10B10A2_UNORM, whose range interpretation varies by format.</summary>
    public const byte Rgb10A2Dxgi = 24;

    /// <summary>Encoded bytes per element.</summary>
    public static int Size(VertexElementFormat format) => format switch
    {
        VertexElementFormat.UNorm8 or VertexElementFormat.SNorm8 or VertexElementFormat.UInt8 => 1,
        VertexElementFormat.Half or VertexElementFormat.UNorm16 or VertexElementFormat.UNorm8x2 or VertexElementFormat.SNorm8x2
            or VertexElementFormat.UInt8x2 => 2,
        VertexElementFormat.Float or VertexElementFormat.Half2 or VertexElementFormat.Byte4 or VertexElementFormat.UNorm8x4 or VertexElementFormat.SNorm8x4
            or VertexElementFormat.UNorm10x3A2 or VertexElementFormat.BiasedUNorm10x3A2 or VertexElementFormat.UNorm16x2 or VertexElementFormat.Int16x2
            or VertexElementFormat.SNorm16x2 or VertexElementFormat.SNorm10x3A2 => 4,
        VertexElementFormat.Half3 => 6,
        VertexElementFormat.Float2 or VertexElementFormat.Half4 or VertexElementFormat.Int16x4 or VertexElementFormat.UNorm16x4 => 8,
        VertexElementFormat.Float3 => 12,
        VertexElementFormat.Float4 => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>Returns the DXGI format identifier stored in a gen9 declaration; both 10/10/10/2 interpretations map to 24.</summary>
    public static byte ToDxgi(VertexElementFormat format) => format switch
    {
        VertexElementFormat.Float4 => 2,
        VertexElementFormat.Float3 => 6,
        VertexElementFormat.Half4 => 10,
        VertexElementFormat.UNorm16x4 => 11,
        VertexElementFormat.Int16x4 => 14,
        VertexElementFormat.Float2 => 16,
        VertexElementFormat.UNorm10x3A2 or VertexElementFormat.BiasedUNorm10x3A2 => Rgb10A2Dxgi,
        VertexElementFormat.UNorm8x4 => 28,
        VertexElementFormat.Byte4 => 30,
        VertexElementFormat.SNorm8x4 => 31,
        VertexElementFormat.Half2 => 34,
        VertexElementFormat.UNorm16x2 => 35,
        VertexElementFormat.SNorm16x2 => 37,
        VertexElementFormat.Int16x2 => 38,
        VertexElementFormat.Float => 41,
        VertexElementFormat.UNorm8x2 => 49,
        VertexElementFormat.UInt8x2 => 50,
        VertexElementFormat.SNorm8x2 => 51,
        VertexElementFormat.Half => 54,
        VertexElementFormat.UNorm16 => 56,
        VertexElementFormat.UNorm8 => 61,
        VertexElementFormat.UInt8 => 62,
        VertexElementFormat.SNorm8 => 63,
        _ => throw new InvalidOperationException($"Vertex format {format} has no supported gen9 encoding.")
    };

    /// <summary>Maps a DXGI vertex format identifier to an element format, choosing the 10/10/10/2 range explicitly.
    /// Returns false for identifiers whose encoding is not known; they are never guessed.</summary>
    public static bool TryFromDxgi(byte dxgi, Rgb10A2Range rgb10A2Range, out VertexElementFormat format)
    {
        if (dxgi == Rgb10A2Dxgi)
        {
            format = rgb10A2Range switch
            {
                Rgb10A2Range.ZeroToOne => VertexElementFormat.UNorm10x3A2,
                Rgb10A2Range.MinusOneToOne => VertexElementFormat.BiasedUNorm10x3A2,
                _ => throw new ArgumentOutOfRangeException(nameof(rgb10A2Range))
            };
            return true;
        }
        VertexElementFormat? known = dxgi switch
        {
            2 => VertexElementFormat.Float4,
            6 => VertexElementFormat.Float3,
            10 => VertexElementFormat.Half4,
            11 => VertexElementFormat.UNorm16x4,
            14 => VertexElementFormat.Int16x4,
            16 => VertexElementFormat.Float2,
            28 => VertexElementFormat.UNorm8x4,
            30 => VertexElementFormat.Byte4,
            31 => VertexElementFormat.SNorm8x4,
            34 => VertexElementFormat.Half2,
            35 => VertexElementFormat.UNorm16x2,
            37 => VertexElementFormat.SNorm16x2,
            38 => VertexElementFormat.Int16x2,
            41 => VertexElementFormat.Float,
            49 => VertexElementFormat.UNorm8x2,
            50 => VertexElementFormat.UInt8x2,
            51 => VertexElementFormat.SNorm8x2,
            54 => VertexElementFormat.Half,
            56 => VertexElementFormat.UNorm16,
            61 => VertexElementFormat.UNorm8,
            62 => VertexElementFormat.UInt8,
            63 => VertexElementFormat.SNorm8,
            _ => null
        };
        format = known.GetValueOrDefault();
        return known.HasValue;
    }

    /// <summary>Maps a DXGI vertex format identifier to an element format, rejecting unknown identifiers as invalid data.</summary>
    public static VertexElementFormat FromDxgi(byte dxgi, Rgb10A2Range rgb10A2Range) =>
        TryFromDxgi(dxgi, rgb10A2Range, out VertexElementFormat format) ? format
            : throw new InvalidDataException($"Gen9 vertex format {dxgi} is unsupported; its encoding will not be guessed.");
}
