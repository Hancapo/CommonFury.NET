using System.Buffers.Binary;
using CommonFury.Numerics;

namespace CommonFury.Textures;

/// <summary>Single managed conversion boundary for supported uncompressed DXGI storage and tightly packed RGBA8 pixels.
/// Encoded storage is little-endian. Missing channels decode as zero, with alpha 255; single-channel formats other than
/// A8 replicate into red, green and blue. Floating-point channels are clamped to 0..1 when decoded.</summary>
public static class PixelCodec
{
    /// <summary>Whether the format is an uncompressed layout this codec converts.</summary>
    public static bool Supports(TextureFormat format) => TextureFormats.TryUnit(format, out _, out int dimension) && dimension == 1;

    /// <summary>Encodes RGBA8 pixels into caller storage that holds exactly the same pixel count in the selected format.</summary>
    public static void Encode(ReadOnlySpan<byte> rgba, TextureFormat format, Span<byte> destination)
    {
        int stride = Check(format, rgba.Length, destination.Length);
        for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
        {
            ReadOnlySpan<byte> color = rgba.Slice(pixel, 4);
            Span<byte> output = destination.Slice(offset, stride);
            byte r = color[0], g = color[1], b = color[2], a = color[3];
            switch (format)
            {
                case TextureFormat.R8G8B8A8Unorm:
                case TextureFormat.R8G8B8A8UnormSrgb:
                    color.CopyTo(output);
                    break;
                case TextureFormat.B8G8R8A8Unorm:
                case TextureFormat.B8G8R8A8UnormSrgb:
                case TextureFormat.B8G8R8X8Unorm:
                case TextureFormat.B8G8R8X8UnormSrgb:
                    output[0] = b;
                    output[1] = g;
                    output[2] = r;
                    output[3] = format is TextureFormat.B8G8R8X8Unorm or TextureFormat.B8G8R8X8UnormSrgb ? (byte)255 : a;
                    break;
                case TextureFormat.B5G6R5Unorm:
                    BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)(PixelMath.Quantize(r, 31) << 11 | PixelMath.Quantize(g, 63) << 5 | PixelMath.Quantize(b, 31)));
                    break;
                case TextureFormat.B5G5R5A1Unorm:
                    BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)(PixelMath.Quantize(a, 1) << 15 | PixelMath.Quantize(r, 31) << 10 | PixelMath.Quantize(g, 31) << 5 | PixelMath.Quantize(b, 31)));
                    break;
                case TextureFormat.R10G10B10A2Unorm:
                    BinaryPrimitives.WriteUInt32LittleEndian(output, PixelMath.Quantize(r, 1023) | PixelMath.Quantize(g, 1023) << 10 | PixelMath.Quantize(b, 1023) << 20 | PixelMath.Quantize(a, 3) << 30);
                    break;
                case TextureFormat.R11G11B10Float:
                    BinaryPrimitives.WriteUInt32LittleEndian(output, PackUnsignedFloat(r / 255f, 6) | PackUnsignedFloat(g / 255f, 6) << 11 | PackUnsignedFloat(b / 255f, 5) << 22);
                    break;
                case TextureFormat.R8Unorm:
                    output[0] = r;
                    break;
                case TextureFormat.A8Unorm:
                    output[0] = a;
                    break;
                case TextureFormat.R8G8Unorm:
                    output[0] = r;
                    output[1] = g;
                    break;
                case TextureFormat.R16Unorm:
                case TextureFormat.R16G16Unorm:
                case TextureFormat.R16G16B16A16Unorm:
                    for (int i = 0; i < stride / 2; i++)
                        BinaryPrimitives.WriteUInt16LittleEndian(output[(i * 2)..], (ushort)(color[i] * 257));
                    break;
                case TextureFormat.R16Float:
                case TextureFormat.R16G16Float:
                case TextureFormat.R16G16B16A16Float:
                    for (int i = 0; i < stride / 2; i++)
                        BinaryPrimitives.WriteUInt16LittleEndian(output[(i * 2)..], BitConverter.HalfToUInt16Bits((Half)(color[i] / 255f)));
                    break;
                case TextureFormat.R32Float:
                case TextureFormat.R32G32Float:
                case TextureFormat.R32G32B32A32Float:
                    for (int i = 0; i < stride / 4; i++)
                        BinaryPrimitives.WriteSingleLittleEndian(output[(i * 4)..], color[i] / 255f);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported uncompressed texture format {format}.");
            }
        }
    }

    /// <summary>Decodes encoded pixels into caller RGBA8 storage that holds exactly the same pixel count.</summary>
    public static void Decode(ReadOnlySpan<byte> source, TextureFormat format, Span<byte> rgba)
    {
        int stride = Check(format, rgba.Length, source.Length);
        for (int pixel = 0, offset = 0; pixel < rgba.Length; pixel += 4, offset += stride)
        {
            ReadOnlySpan<byte> input = source.Slice(offset, stride);
            Span<byte> color = rgba.Slice(pixel, 4);
            color.Clear();
            color[3] = 255;
            switch (format)
            {
                case TextureFormat.R8G8B8A8Unorm:
                case TextureFormat.R8G8B8A8UnormSrgb:
                    input.CopyTo(color);
                    break;
                case TextureFormat.B8G8R8A8Unorm:
                case TextureFormat.B8G8R8A8UnormSrgb:
                case TextureFormat.B8G8R8X8Unorm:
                case TextureFormat.B8G8R8X8UnormSrgb:
                    color[0] = input[2];
                    color[1] = input[1];
                    color[2] = input[0];
                    if (format is not (TextureFormat.B8G8R8X8Unorm or TextureFormat.B8G8R8X8UnormSrgb))
                        color[3] = input[3];
                    break;
                case TextureFormat.B5G6R5Unorm:
                    uint rgb = BinaryPrimitives.ReadUInt16LittleEndian(input);
                    color[0] = PixelMath.Expand(rgb >> 11, 31);
                    color[1] = PixelMath.Expand(rgb >> 5 & 63, 63);
                    color[2] = PixelMath.Expand(rgb & 31, 31);
                    break;
                case TextureFormat.B5G5R5A1Unorm:
                    uint rgbAlpha = BinaryPrimitives.ReadUInt16LittleEndian(input);
                    color[0] = PixelMath.Expand(rgbAlpha >> 10 & 31, 31);
                    color[1] = PixelMath.Expand(rgbAlpha >> 5 & 31, 31);
                    color[2] = PixelMath.Expand(rgbAlpha & 31, 31);
                    color[3] = (rgbAlpha & 0x8000) == 0 ? (byte)0 : (byte)255;
                    break;
                case TextureFormat.R10G10B10A2Unorm:
                    uint packed = BinaryPrimitives.ReadUInt32LittleEndian(input);
                    color[0] = PixelMath.Expand(packed & 1023, 1023);
                    color[1] = PixelMath.Expand(packed >> 10 & 1023, 1023);
                    color[2] = PixelMath.Expand(packed >> 20 & 1023, 1023);
                    color[3] = PixelMath.Expand(packed >> 30, 3);
                    break;
                case TextureFormat.R11G11B10Float:
                    uint small = BinaryPrimitives.ReadUInt32LittleEndian(input);
                    color[0] = PixelMath.ToByte(UnpackUnsignedFloat(small & 0x7FF, 6));
                    color[1] = PixelMath.ToByte(UnpackUnsignedFloat(small >> 11 & 0x7FF, 6));
                    color[2] = PixelMath.ToByte(UnpackUnsignedFloat(small >> 22, 5));
                    break;
                case TextureFormat.R8Unorm:
                    color[0] = color[1] = color[2] = input[0];
                    break;
                case TextureFormat.A8Unorm:
                    color[0] = color[1] = color[2] = 255;
                    color[3] = input[0];
                    break;
                case TextureFormat.R8G8Unorm:
                    color[0] = input[0];
                    color[1] = input[1];
                    break;
                case TextureFormat.R16Unorm:
                case TextureFormat.R16G16Unorm:
                case TextureFormat.R16G16B16A16Unorm:
                    for (int i = 0; i < stride / 2; i++)
                        color[i] = PixelMath.Expand(BinaryPrimitives.ReadUInt16LittleEndian(input[(i * 2)..]), 65535);
                    if (stride == 2)
                        color[1] = color[2] = color[0];
                    break;
                case TextureFormat.R16Float:
                case TextureFormat.R16G16Float:
                case TextureFormat.R16G16B16A16Float:
                    for (int i = 0; i < stride / 2; i++)
                        color[i] = PixelMath.ToByte((float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(input[(i * 2)..])));
                    if (stride == 2)
                        color[1] = color[2] = color[0];
                    break;
                case TextureFormat.R32Float:
                case TextureFormat.R32G32Float:
                case TextureFormat.R32G32B32A32Float:
                    for (int i = 0; i < stride / 4; i++)
                        color[i] = PixelMath.ToByte(BinaryPrimitives.ReadSingleLittleEndian(input[(i * 4)..]));
                    if (stride == 4)
                        color[1] = color[2] = color[0];
                    break;
                default:
                    throw new NotSupportedException($"Unsupported uncompressed texture format {format}.");
            }
        }
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
