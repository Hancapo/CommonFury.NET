using System.Buffers.Binary;
using System.Runtime.InteropServices;
using CommonFury.Numerics;

namespace CommonFury.Textures;

/// <summary>Single managed conversion boundary for supported uncompressed DXGI storage and tightly packed RGBA8 pixels.
/// Encoded storage is little-endian. Missing channels decode as zero, with alpha 255; single-channel formats other than
/// A8 replicate into red, green and blue. Floating-point channels are clamped to 0..1 when decoded.</summary>
public static class PixelCodec
{
    private static byte[]? elevenBitFloats, tenBitFloats;

    /// <summary>Whether the format is an uncompressed layout this codec converts.</summary>
    public static bool Supports(TextureFormat format) => TextureFormats.TryUnit(format, out _, out int dimension) && dimension == 1;

    /// <summary>Encodes RGBA8 pixels into caller storage that holds exactly the same pixel count in the selected format.</summary>
    public static void Encode(ReadOnlySpan<byte> rgba, TextureFormat format, Span<byte> destination)
    {
        int stride = Check(format, rgba.Length, destination.Length);
        switch (format)
        {
            case TextureFormat.R8G8B8A8Unorm:
            case TextureFormat.R8G8B8A8UnormSrgb:
                rgba.CopyTo(destination);
                break;
            case TextureFormat.B8G8R8A8Unorm:
            case TextureFormat.B8G8R8A8UnormSrgb:
            case TextureFormat.B8G8R8X8Unorm:
            case TextureFormat.B8G8R8X8UnormSrgb:
                SwapRedBlue(rgba, destination, format is TextureFormat.B8G8R8X8Unorm or TextureFormat.B8G8R8X8UnormSrgb);
                break;
            case TextureFormat.B5G6R5Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                {
                    ReadOnlySpan<byte> color = rgba.Slice(pixel, 4);
                    BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)(PixelMath.Quantize(color[0], 31) << 11 | PixelMath.Quantize(color[1], 63) << 5 | PixelMath.Quantize(color[2], 31)));
                }
                break;
            case TextureFormat.B5G5R5A1Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                {
                    ReadOnlySpan<byte> color = rgba.Slice(pixel, 4);
                    BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)(PixelMath.Quantize(color[3], 1) << 15 | PixelMath.Quantize(color[0], 31) << 10 | PixelMath.Quantize(color[1], 31) << 5 | PixelMath.Quantize(color[2], 31)));
                }
                break;
            case TextureFormat.R10G10B10A2Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                {
                    ReadOnlySpan<byte> color = rgba.Slice(pixel, 4);
                    BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], PixelMath.Quantize(color[0], 1023) | PixelMath.Quantize(color[1], 1023) << 10 | PixelMath.Quantize(color[2], 1023) << 20 | PixelMath.Quantize(color[3], 3) << 30);
                }
                break;
            case TextureFormat.R11G11B10Float:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                {
                    ReadOnlySpan<byte> color = rgba.Slice(pixel, 4);
                    BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], PackUnsignedFloat(color[0] / 255f, 6) | PackUnsignedFloat(color[1] / 255f, 6) << 11 | PackUnsignedFloat(color[2] / 255f, 5) << 22);
                }
                break;
            case TextureFormat.R8Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset++)
                    destination[offset] = rgba[pixel];
                break;
            case TextureFormat.A8Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset++)
                    destination[offset] = rgba[pixel + 3];
                break;
            case TextureFormat.R8G8Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += 2)
                {
                    destination[offset] = rgba[pixel];
                    destination[offset + 1] = rgba[pixel + 1];
                }
                break;
            case TextureFormat.R16Unorm:
            case TextureFormat.R16G16Unorm:
            case TextureFormat.R16G16B16A16Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                    for (int i = 0; i < stride / 2; i++)
                        BinaryPrimitives.WriteUInt16LittleEndian(destination[(offset + i * 2)..], (ushort)(rgba[pixel + i] * 257));
                break;
            case TextureFormat.R16Float:
            case TextureFormat.R16G16Float:
            case TextureFormat.R16G16B16A16Float:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                    for (int i = 0; i < stride / 2; i++)
                        BinaryPrimitives.WriteUInt16LittleEndian(destination[(offset + i * 2)..], BitConverter.HalfToUInt16Bits((Half)(rgba[pixel + i] / 255f)));
                break;
            case TextureFormat.R32Float:
            case TextureFormat.R32G32Float:
            case TextureFormat.R32G32B32A32Float:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                    for (int i = 0; i < stride / 4; i++)
                        BinaryPrimitives.WriteSingleLittleEndian(destination[(offset + i * 4)..], rgba[pixel + i] / 255f);
                break;
            default:
                throw new NotSupportedException($"Unsupported uncompressed texture format {format}.");
        }
    }

    /// <summary>Decodes encoded pixels into caller RGBA8 storage that holds exactly the same pixel count.</summary>
    public static void Decode(ReadOnlySpan<byte> source, TextureFormat format, Span<byte> rgba)
    {
        int stride = Check(format, rgba.Length, source.Length);
        switch (format)
        {
            case TextureFormat.R8G8B8A8Unorm:
            case TextureFormat.R8G8B8A8UnormSrgb:
                source.CopyTo(rgba);
                break;
            case TextureFormat.B8G8R8A8Unorm:
            case TextureFormat.B8G8R8A8UnormSrgb:
            case TextureFormat.B8G8R8X8Unorm:
            case TextureFormat.B8G8R8X8UnormSrgb:
                SwapRedBlue(source, rgba, format is TextureFormat.B8G8R8X8Unorm or TextureFormat.B8G8R8X8UnormSrgb);
                break;
            case TextureFormat.B5G6R5Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                {
                    uint rgb = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
                    Store(rgba, pixel, PixelMath.Expand(rgb >> 11, 31), PixelMath.Expand(rgb >> 5 & 63, 63), PixelMath.Expand(rgb & 31, 31), 255);
                }
                break;
            case TextureFormat.B5G5R5A1Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                {
                    uint rgbAlpha = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
                    Store(rgba, pixel, PixelMath.Expand(rgbAlpha >> 10 & 31, 31), PixelMath.Expand(rgbAlpha >> 5 & 31, 31), PixelMath.Expand(rgbAlpha & 31, 31),
                        (rgbAlpha & 0x8000) == 0 ? (byte)0 : (byte)255);
                }
                break;
            case TextureFormat.R10G10B10A2Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                {
                    uint packed = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
                    Store(rgba, pixel, PixelMath.Expand(packed & 1023, 1023), PixelMath.Expand(packed >> 10 & 1023, 1023), PixelMath.Expand(packed >> 20 & 1023, 1023),
                        PixelMath.Expand(packed >> 30, 3));
                }
                break;
            case TextureFormat.R11G11B10Float:
                ReadOnlySpan<byte> eleven = ElevenBitFloats, ten = TenBitFloats;
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                {
                    uint small = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
                    Store(rgba, pixel, eleven[(int)(small & 0x7FF)], eleven[(int)(small >> 11 & 0x7FF)], ten[(int)(small >> 22)], 255);
                }
                break;
            case TextureFormat.R8Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset++)
                    Store(rgba, pixel, source[offset], source[offset], source[offset], 255);
                break;
            case TextureFormat.A8Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset++)
                    Store(rgba, pixel, 255, 255, 255, source[offset]);
                break;
            case TextureFormat.R8G8Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += 2)
                    Store(rgba, pixel, source[offset], source[offset + 1], 0, 255);
                break;
            case TextureFormat.R16Unorm:
            case TextureFormat.R16G16Unorm:
            case TextureFormat.R16G16B16A16Unorm:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                {
                    Span<byte> color = rgba.Slice(pixel, 4);
                    for (int i = 0; i < stride / 2; i++)
                        color[i] = PixelMath.Expand(BinaryPrimitives.ReadUInt16LittleEndian(source[(offset + i * 2)..]), 65535);
                    FillMissing(color, stride / 2);
                }
                break;
            case TextureFormat.R16Float:
            case TextureFormat.R16G16Float:
            case TextureFormat.R16G16B16A16Float:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                {
                    Span<byte> color = rgba.Slice(pixel, 4);
                    for (int i = 0; i < stride / 2; i++)
                        color[i] = PixelMath.ToByte((float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(source[(offset + i * 2)..])));
                    FillMissing(color, stride / 2);
                }
                break;
            case TextureFormat.R32Float:
            case TextureFormat.R32G32Float:
            case TextureFormat.R32G32B32A32Float:
                for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
                {
                    Span<byte> color = rgba.Slice(pixel, 4);
                    for (int i = 0; i < stride / 4; i++)
                        color[i] = PixelMath.ToByte(BinaryPrimitives.ReadSingleLittleEndian(source[(offset + i * 4)..]));
                    FillMissing(color, stride / 4);
                }
                break;
            default:
                throw new NotSupportedException($"Unsupported uncompressed texture format {format}.");
        }
    }

    // Decoded bytes of every unsigned small float, built on first use from UnpackUnsignedFloat so lookups match it exactly.
    internal static ReadOnlySpan<byte> ElevenBitFloats => elevenBitFloats ??= SmallFloatBytes(6);
    internal static ReadOnlySpan<byte> TenBitFloats => tenBitFloats ??= SmallFloatBytes(5);

    private static byte[] SmallFloatBytes(int mantissaBits)
    {
        var table = new byte[32 << mantissaBits];
        for (int value = 0; value < table.Length; value++)
            table[value] = PixelMath.ToByte(UnpackUnsignedFloat((uint)value, mantissaBits));
        return table;
    }

    // Encoding and decoding both swap bytes 0 and 2 of each texel; opaque variants force alpha to 255.
    private static void SwapRedBlue(ReadOnlySpan<byte> source, Span<byte> destination, bool opaque)
    {
        ReadOnlySpan<uint> input = MemoryMarshal.Cast<byte, uint>(source);
        Span<uint> output = MemoryMarshal.Cast<byte, uint>(destination);
        uint alpha = opaque ? 0xFF000000 : 0;
        for (int i = 0; i < input.Length; i++)
        {
            uint texel = BitConverter.IsLittleEndian ? input[i] : BinaryPrimitives.ReverseEndianness(input[i]);
            texel = texel & 0xFF00FF00 | texel >> 16 & 0xFF | (texel & 0xFF) << 16 | alpha;
            output[i] = BitConverter.IsLittleEndian ? texel : BinaryPrimitives.ReverseEndianness(texel);
        }
    }

    private static void Store(Span<byte> rgba, int pixel, byte red, byte green, byte blue, byte alpha)
        => BinaryPrimitives.WriteUInt32LittleEndian(rgba[pixel..], (uint)(red | green << 8 | blue << 16 | alpha << 24));

    // Missing channels decode as zero with alpha 255; a single channel replicates into red, green and blue.
    private static void FillMissing(Span<byte> color, int channels)
    {
        if (channels == 1)
            color[1] = color[2] = color[0];
        else if (channels == 2)
            color[2] = 0;
        if (channels < 4)
            color[3] = 255;
    }

    // Unsigned 5-bit-exponent floats (bias 15) as used by R11G11B10Float; rounds to nearest even and saturates
    // at the largest finite value. NaN and nonpositive inputs encode as zero.
    private static uint PackUnsignedFloat(float value, int mantissaBits)
    {
        uint largest = 30u << mantissaBits | ((1u << mantissaBits) - 1);
        if (!(value > 0))
            return 0;
        uint bits = BitConverter.SingleToUInt32Bits(value);
        int exponent = (int)(bits >> 23) - 127 + 15;
        if (exponent <= 0)
            return Math.Min(largest, (uint)MathF.Round(value * MathF.Pow(2, 14 + mantissaBits), MidpointRounding.ToEven));
        int shift = 23 - mantissaBits;
        ulong combined = (ulong)exponent << 23 | (bits & 0x7FFFFF);
        ulong rounded = (combined + (1UL << (shift - 1)) - 1 + (combined >> shift & 1)) >> shift;
        return (uint)Math.Min(largest, rounded);
    }

    private static float UnpackUnsignedFloat(uint value, int mantissaBits)
    {
        uint exponent = value >> mantissaBits, mantissa = value & ((1u << mantissaBits) - 1);
        float scale = 1 << mantissaBits;
        return exponent switch
        {
            0 => mantissa / scale * MathF.Pow(2, -14),
            31 => mantissa == 0 ? float.PositiveInfinity : float.NaN,
            _ => (1 + mantissa / scale) * MathF.Pow(2, (int)exponent - 15)
        };
    }

    private static int Check(TextureFormat format, int rgbaLength, int encodedLength)
    {
        if (!Supports(format))
            throw new NotSupportedException($"Unsupported uncompressed texture format {format}.");
        TextureFormats.TryUnit(format, out int stride, out _);
        if ((rgbaLength & 3) != 0 || checked(rgbaLength / 4 * stride) != encodedLength)
            throw new ArgumentException("Pixel buffers do not match the selected encoded layout.");
        return stride;
    }
}
