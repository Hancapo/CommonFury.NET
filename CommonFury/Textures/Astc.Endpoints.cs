using System.Numerics;

namespace CommonFury.Textures;

public static partial class Astc
{
    // Quantization levels 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 160, 192 and 256, as
    // (trits or quints, low bits). Weights use the first 12; endpoints need at least 6 levels.
    private const int MinimumColorLevel = 4;
    private static readonly byte[] LevelKind = [0, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0];
    private static readonly byte[] LevelBits = [1, 0, 2, 0, 1, 3, 1, 2, 4, 2, 3, 5, 3, 4, 6, 4, 5, 7, 5, 6, 8];
    private static readonly byte[] TritTable = BuildTrits(), QuintTable = BuildQuints();
    private static readonly byte[][] ColorTables = [.. Enumerable.Range(0, 21).Select(level => BuildUnquantize(level, color: true))];
    private static readonly byte[][] WeightTables = [.. Enumerable.Range(0, 12).Select(level => BuildUnquantize(level, color: false))];

    private static int IseBits(int count, int level) => LevelKind[level] switch
    {
        1 => (8 * count + 4) / 5 + count * LevelBits[level],
        2 => (7 * count + 2) / 3 + count * LevelBits[level],
        _ => count * LevelBits[level]
    };

    // The finest endpoint quantization whose sequence fits the available bits, or -1.
    private static int ColorLevel(int count, int bits)
    {
        for (int level = 20; level >= 0; level--)
            if (IseBits(count, level) <= bits)
                return level;
        return -1;
    }

    // Reads count quantized values from the sequence at start; bits past the sequence's end read as zero.
    private static void DecodeIse(UInt128 data, int start, int count, int level, Span<byte> values)
    {
        int kind = LevelKind[level], bits = LevelBits[level], end = start + IseBits(count, level), position = start;
        int Read(int width)
        {
            int available = Math.Clamp(end - position, 0, width);
            int value = available == 0 ? 0 : Bits(data, position, available);
            position += width;
            return value;
        }
        if (kind == 0)
        {
            for (int i = 0; i < count; i++)
                values[i] = (byte)Read(bits);
            return;
        }
        Span<int> low = stackalloc int[5];
        ReadOnlySpan<int> packedWidths = kind == 1 ? [2, 2, 1, 2, 1] : [3, 2, 2];
        int group = kind == 1 ? 5 : 3;
        for (int first = 0; first < count; first += group)
        {
            int packed = 0, shift = 0;
            for (int i = 0; i < group; i++)
            {
                low[i] = Read(bits);
                packed |= Read(packedWidths[i]) << shift;
                shift += packedWidths[i];
            }
            for (int i = 0; i < group && first + i < count; i++)
            {
                int digit = kind == 1 ? TritTable[packed] / Pow(3, i) % 3 : QuintTable[packed] / Pow(5, i) % 5;
                values[first + i] = (byte)(digit << bits | low[i]);
            }
        }
    }

    private static int Pow(int value, int exponent)
    {
        int result = 1;
        for (int i = 0; i < exponent; i++)
            result *= value;
        return result;
    }

    private static void UnquantizeColors(Span<byte> values, int level)
    {
        var table = ColorTables[level];
        for (int i = 0; i < values.Length; i++)
            values[i] = table[values[i]];
    }

    private static void UnquantizeWeights(Span<byte> values, int level)
    {
        var table = WeightTables[level];
        for (int i = 0; i < values.Length; i++)
            values[i] = table[values[i]];
    }

    // Five trits from 8 packed bits, stored as a base-3 number with the first trit least significant.
    private static byte[] BuildTrits()
    {
        var table = new byte[256];
        for (int t = 0; t < 256; t++)
        {
            int c, t4, t3, t2, t1, t0;
            if ((t >> 2 & 7) == 7)
            {
                c = (t >> 5 & 7) << 2 | t & 3;
                t4 = t3 = 2;
            }
            else
            {
                c = t & 0x1F;
                if ((t >> 5 & 3) == 3)
                    (t4, t3) = (2, t >> 7 & 1);
                else
                    (t4, t3) = (t >> 7 & 1, t >> 5 & 3);
            }
            if ((c & 3) == 3)
                (t2, t1, t0) = (2, c >> 4 & 1, (c >> 3 & 1) << 1 | (c >> 2 & 1 & ~(c >> 3) & 1));
            else if ((c >> 2 & 3) == 3)
                (t2, t1, t0) = (2, 2, c & 3);
            else
                (t2, t1, t0) = (c >> 4 & 1, c >> 2 & 3, (c >> 1 & 1) << 1 | (c & 1 & ~(c >> 1) & 1));
            table[t] = (byte)(t0 + 3 * (t1 + 3 * (t2 + 3 * (t3 + 3 * t4))));
        }
        return table;
    }

    // Three quints from 7 packed bits, stored as a base-5 number with the first quint least significant.
    private static byte[] BuildQuints()
    {
        var table = new byte[128];
        for (int q = 0; q < 128; q++)
        {
            int q2, q1, q0;
            if ((q >> 1 & 3) == 3 && (q >> 5 & 3) == 0)
            {
                q2 = (q & 1) << 2 | (q >> 4 & 1 & ~q & 1) << 1 | (q >> 3 & 1 & ~q & 1);
                q1 = q0 = 4;
            }
            else
            {
                int c;
                if ((q >> 1 & 3) == 3)
                    (q2, c) = (4, (q >> 3 & 3) << 3 | (~q >> 5 & 3) << 1 | q & 1);
                else
                    (q2, c) = (q >> 5 & 3, q & 0x1F);
                if ((c & 7) == 5)
                    (q1, q0) = (4, c >> 3 & 3);
                else
                    (q1, q0) = (c >> 3 & 3, c & 7);
            }
            table[q] = (byte)(q0 + 5 * (q1 + 5 * q2));
        }
        return table;
    }

    // Maps quantized values to 0–255 for endpoints or 0–64 for weights.
    private static byte[] BuildUnquantize(int level, bool color)
    {
        int kind = LevelKind[level], bits = LevelBits[level];
        var table = new byte[256];
        for (int raw = 0; raw < (kind == 0 ? 1 : kind == 1 ? 3 : 5) << bits; raw++)
        {
            int digit = raw >> bits, low = raw & ((1 << bits) - 1), value;
            if (kind == 0)
                value = Replicate(low, bits, color ? 8 : 6);
            else if (bits == 0)
                value = (kind == 1 ? [0, 32, 63] : new[] { 0, 16, 32, 47, 63 })[digit];
            else
            {
                int a = (low & 1) != 0 ? (color ? 0x1FF : 0x7F) : 0, b = low >> 1;
                (int scale, int spread) = color ? ColorTerms(kind, bits, b) : WeightTerms(kind, bits, b);
                int t = (digit * scale + spread) ^ a;
                value = (a & (color ? 0x80 : 0x20)) | t >> 2;
            }
            if (!color && value > 32)
                value++;
            table[raw] = (byte)value;
        }
        return table;
    }

    // Multiplier C and bit pattern B of the format's unquantization; b holds the value's bits above the lowest.
    private static (int, int) ColorTerms(int kind, int bits, int b) => (kind, bits) switch
    {
        (1, 1) => (204, 0),
        (1, 2) => (93, b << 8 | b << 4 | b << 2 | b << 1),
        (1, 3) => (44, b << 7 | b << 2 | b),
        (1, 4) => (22, b << 6 | b),
        (1, 5) => (11, b << 5 | b >> 2),
        (1, 6) => (5, b << 4 | b >> 4),
        (2, 1) => (113, 0),
        (2, 2) => (54, b << 8 | b << 3 | b << 2),
        (2, 3) => (26, b << 7 | b << 1 | b >> 1),
        (2, 4) => (13, b << 6 | b >> 1),
        _ => (6, b << 5 | b >> 3)
    };

    private static (int, int) WeightTerms(int kind, int bits, int b) => (kind, bits) switch
    {
        (1, 1) => (50, 0),
        (1, 2) => (23, b << 6 | b << 2 | b),
        (1, 3) => (11, b << 5 | b),
        (2, 1) => (28, 0),
        _ => (13, b << 6 | b << 1)
    };

    private static int Replicate(int value, int bits, int width)
    {
        int result = 0;
        for (int shift = width - bits; shift > -bits; shift -= bits)
            result |= shift >= 0 ? value << shift : value >> -shift;
        return result;
    }

    // Truncating UNORM16 to FP16 conversion, treating the value as a fraction of 65536 except for 0xFFFF.
    private static ushort UnormToHalf(int value)
    {
        if (value == 0xFFFF)
            return 0x3C00;
        if (value < 4)
            return (ushort)(value << 8);
        int leading = BitOperations.LeadingZeroCount((uint)value) - 16;
        return (ushort)(((value << (leading + 1)) & 0xFFFF) >> 6 | (14 - leading) << 10);
    }

    // Converts the 16-bit logarithmic HDR value to FP16, clamped below infinity.
    private static ushort LogarithmicToHalf(int value)
    {
        int mantissa = value & 0x7FF, exponent = value >> 11;
        int curved = mantissa < 512 ? mantissa * 3 : mantissa < 1536 ? mantissa * 4 - 512 : mantissa * 5 - 2048;
        return (ushort)Math.Min(exponent << 10 | curved >> 3, 0x7BFF);
    }

    private sealed partial class BlockDecoder
    {
        // Writes endpoint 0 then endpoint 1 as 16-bit RGBA and reports which lanes hold logarithmic HDR values.
        private void Endpoints(int mode, ReadOnlySpan<byte> colors, Span<int> endpoints, out bool hdrColor, out bool hdrAlpha)
        {
            Span<int> v = stackalloc int[8];
            for (int i = 0; i < colors.Length; i++)
                v[i] = colors[i];
            Span<int> e0 = endpoints[..4], e1 = endpoints[4..];
            hdrColor = hdrAlpha = false;
            bool defaultAlpha = false;
            switch (mode)
            {
                case 0:
                    Set(e0, v[0], v[0], v[0], 255);
                    Set(e1, v[1], v[1], v[1], 255);
                    break;
                case 1:
                {
                    int l0 = v[0] >> 2 | v[1] & 0xC0, l1 = Math.Min(l0 + (v[1] & 0x3F), 255);
                    Set(e0, l0, l0, l0, 255);
                    Set(e1, l1, l1, l1, 255);
                    break;
                }
                case 2:
                case 3:
                    LuminanceHdr(mode, v[0], v[1], e0, e1);
                    hdrColor = defaultAlpha = true;
                    break;
                case 4:
                    Set(e0, v[0], v[0], v[0], v[2]);
                    Set(e1, v[1], v[1], v[1], v[3]);
                    break;
                case 5:
                    TransferBits(ref v[1], ref v[0]);
                    TransferBits(ref v[3], ref v[2]);
                    Set(e0, v[0], v[0], v[0], v[2]);
                    int l = Clamp(v[0] + v[1]);
                    Set(e1, l, l, l, Clamp(v[2] + v[3]));
                    break;
                case 6:
                    Set(e0, v[0] * v[3] >> 8, v[1] * v[3] >> 8, v[2] * v[3] >> 8, 255);
                    Set(e1, v[0], v[1], v[2], 255);
                    break;
                case 7:
                    ScaledHdr(v, e0, e1);
                    hdrColor = defaultAlpha = true;
                    break;
                case 8:
                case 12:
                    Direct(v, mode == 12, e0, e1);
                    break;
                case 9:
                case 13:
                    BaseOffset(v, mode == 13, e0, e1);
                    break;
                case 10:
                    Set(e0, v[0] * v[3] >> 8, v[1] * v[3] >> 8, v[2] * v[3] >> 8, v[4]);
                    Set(e1, v[0], v[1], v[2], v[5]);
                    break;
                default:
                    DirectHdr(v, e0, e1);
                    hdrColor = true;
                    if (mode == 11)
                        defaultAlpha = true;
                    else if (mode == 14)
                        (e0[3], e1[3]) = (v[6], v[7]);
                    else
                    {
                        AlphaHdr(v[6], v[7], out e0[3], out e1[3]);
                        hdrAlpha = true;
                    }
                    break;
            }
            if (defaultAlpha)
            {
                hdrAlpha = profile == AstcProfile.Hdr;
                e0[3] = e1[3] = hdrAlpha ? 0x7800 : 0xFF;
            }
            if (profile == AstcProfile.Hdr)
            {
                for (int channel = 0; channel < 4; channel++)
                    if (!(channel == 3 ? hdrAlpha : hdrColor))
                    {
                        e0[channel] *= 257;
                        e1[channel] *= 257;
                    }
                return;
            }
            if (hdrColor || hdrAlpha)
            {
                Set(e0, 0xFF, 0, 0xFF, 0xFF);
                Set(e1, 0xFF, 0, 0xFF, 0xFF);
                hdrColor = hdrAlpha = false;
            }
            for (int i = 0; i < 8; i++)
                endpoints[i] = profile == AstcProfile.LdrSrgb ? endpoints[i] << 8 | 0x80 : endpoints[i] * 257;
        }

        private static void Set(Span<int> endpoint, int r, int g, int b, int a)
        {
            endpoint[0] = r;
            endpoint[1] = g;
            endpoint[2] = b;
            endpoint[3] = a;
        }

        private static int Clamp(int value) => Math.Clamp(value, 0, 255);

        // Moves the top bit of the offset into the base and leaves the offset as a signed 6-bit value.
        private static void TransferBits(ref int offset, ref int baseValue)
        {
            baseValue = baseValue >> 1 | offset & 0x80;
            offset = offset >> 1 & 0x3F;
            if ((offset & 0x20) != 0)
                offset -= 0x40;
        }

        private static void BlueContract(Span<int> endpoint)
        {
            endpoint[0] = endpoint[0] + endpoint[2] >> 1;
            endpoint[1] = endpoint[1] + endpoint[2] >> 1;
        }

        private static void Direct(ReadOnlySpan<int> v, bool alpha, Span<int> e0, Span<int> e1)
        {
            int a0 = alpha ? v[6] : 255, a1 = alpha ? v[7] : 255;
            if (v[1] + v[3] + v[5] >= v[0] + v[2] + v[4])
            {
                Set(e0, v[0], v[2], v[4], a0);
                Set(e1, v[1], v[3], v[5], a1);
                return;
            }
            Set(e0, v[1], v[3], v[5], a1);
            Set(e1, v[0], v[2], v[4], a0);
            BlueContract(e0);
            BlueContract(e1);
        }

        private static void BaseOffset(Span<int> v, bool alpha, Span<int> e0, Span<int> e1)
        {
            int channels = alpha ? 4 : 3;
            for (int i = 0; i < channels; i++)
                TransferBits(ref v[2 * i + 1], ref v[2 * i]);
            Set(e0, v[0], v[2], v[4], alpha ? v[6] : 255);
            Set(e1, v[0] + v[1], v[2] + v[3], v[4] + v[5], alpha ? v[6] + v[7] : 255);
            if (v[1] + v[3] + v[5] < 0)
            {
                Span<int> swap = stackalloc int[4];
                e0.CopyTo(swap);
                e1.CopyTo(e0);
                swap.CopyTo(e1);
                BlueContract(e0);
                BlueContract(e1);
            }
            for (int i = 0; i < 4; i++)
            {
                e0[i] = Clamp(e0[i]);
                e1[i] = Clamp(e1[i]);
            }
        }

        private static void LuminanceHdr(int mode, int v0, int v1, Span<int> e0, Span<int> e1)
        {
            int y0, y1;
            if (mode == 2)
                (y0, y1) = v1 >= v0 ? (v0 << 4, v1 << 4) : ((v1 << 4) + 8, (v0 << 4) - 8);
            else
            {
                if ((v0 & 0x80) != 0)
                    (y0, y1) = ((v1 & 0xE0) << 4 | (v0 & 0x7F) << 2, (v1 & 0x1F) << 2);
                else
                    (y0, y1) = ((v1 & 0xF0) << 4 | (v0 & 0x7F) << 1, (v1 & 0xF) << 1);
                y1 = Math.Min(y1 + y0, 0xFFF);
            }
            Set(e0, y0 << 4, y0 << 4, y0 << 4, 0);
            Set(e1, y1 << 4, y1 << 4, y1 << 4, 0);
        }

        // HDR RGB with a shared scale: a major component, a mode and variable-placement high bits.
        private static void ScaledHdr(ReadOnlySpan<int> v, Span<int> e0, Span<int> e1)
        {
            int modeValue = (v[0] & 0xC0) >> 6 | (v[1] & 0x80) >> 5 | (v[2] & 0x80) >> 4;
            int major, submode;
            if ((modeValue & 0xC) != 0xC)
                (major, submode) = (modeValue >> 2, modeValue & 3);
            else if (modeValue != 0xF)
                (major, submode) = (modeValue & 3, 4);
            else
                (major, submode) = (0, 5);
            int red = v[0] & 0x3F, green = v[1] & 0x1F, blue = v[2] & 0x1F, scale = v[3] & 0x1F;
            int bit0 = v[1] >> 6 & 1, bit1 = v[1] >> 5 & 1, bit2 = v[2] >> 6 & 1, bit3 = v[2] >> 5 & 1;
            int bit4 = v[3] >> 7 & 1, bit5 = v[3] >> 6 & 1, bit6 = v[3] >> 5 & 1;
            int oneHot = 1 << submode;
            if ((oneHot & 0x30) != 0) { green |= bit0 << 6; blue |= bit2 << 6; }
            if ((oneHot & 0x3A) != 0) { green |= bit1 << 5; blue |= bit3 << 5; }
            if ((oneHot & 0x3D) != 0) scale |= bit6 << 5;
            if ((oneHot & 0x2D) != 0) scale |= bit5 << 6;
            if ((oneHot & 0x04) != 0) { scale |= bit4 << 7; red |= bit3 << 6; }
            if ((oneHot & 0x3B) != 0) red |= bit4 << 6;
            if ((oneHot & 0x10) != 0) red |= bit5 << 7;
            if ((oneHot & 0x0F) != 0) red |= bit2 << 7;
            if ((oneHot & 0x05) != 0) red |= bit1 << 8 | bit0 << 9;
            if ((oneHot & 0x0A) != 0) red |= bit0 << 8;
            if ((oneHot & 0x02) != 0) red |= bit6 << 9 | bit5 << 10;
            if ((oneHot & 0x01) != 0) red |= bit3 << 10;
            int shift = ((ReadOnlySpan<int>)[1, 1, 2, 3, 4, 5])[submode];
            red <<= shift;
            green <<= shift;
            blue <<= shift;
            scale <<= shift;
            if (submode != 5)
            {
                green = red - green;
                blue = red - blue;
            }
            if (major == 1)
                (red, green) = (green, red);
            else if (major == 2)
                (red, blue) = (blue, red);
            Set(e0, Math.Max(red - scale, 0) << 4, Math.Max(green - scale, 0) << 4, Math.Max(blue - scale, 0) << 4, 0);
            Set(e1, Math.Max(red, 0) << 4, Math.Max(green, 0) << 4, Math.Max(blue, 0) << 4, 0);
        }

        private static void DirectHdr(ReadOnlySpan<int> v, Span<int> e0, Span<int> e1)
        {
            int modeValue = (v[1] & 0x80) >> 7 | (v[2] & 0x80) >> 6 | (v[3] & 0x80) >> 5;
            int major = (v[4] & 0x80) >> 7 | (v[5] & 0x80) >> 6;
            if (major == 3)
            {
                Set(e0, v[0] << 8, v[2] << 8, (v[4] & 0x7F) << 9, 0);
                Set(e1, v[1] << 8, v[3] << 8, (v[5] & 0x7F) << 9, 0);
                return;
            }
            int a = v[0] | (v[1] & 0x40) << 2, b0 = v[2] & 0x3F, b1 = v[3] & 0x3F, c = v[1] & 0x3F, d0 = v[4] & 0x7F, d1 = v[5] & 0x7F;
            int dBits = ((ReadOnlySpan<int>)[7, 6, 7, 6, 5, 6, 5, 6])[modeValue];
            int bit0 = v[2] >> 6 & 1, bit1 = v[3] >> 6 & 1, bit2 = v[4] >> 6 & 1, bit3 = v[5] >> 6 & 1, bit4 = v[4] >> 5 & 1, bit5 = v[5] >> 5 & 1;
            int oneHot = 1 << modeValue;
            if ((oneHot & 0xA4) != 0) a |= bit0 << 9;
            if ((oneHot & 0x08) != 0) a |= bit2 << 9;
            if ((oneHot & 0x50) != 0) a |= bit4 << 9 | bit5 << 10;
            if ((oneHot & 0xA0) != 0) a |= bit1 << 10;
            if ((oneHot & 0xC0) != 0) a |= bit2 << 11;
            if ((oneHot & 0x04) != 0) c |= bit1 << 6;
            if ((oneHot & 0xE8) != 0) c |= bit3 << 6;
            if ((oneHot & 0x20) != 0) c |= bit2 << 7;
            if ((oneHot & 0x5B) != 0) { b0 |= bit0 << 6; b1 |= bit1 << 6; }
            if ((oneHot & 0x12) != 0) { b0 |= bit2 << 7; b1 |= bit3 << 7; }
            if ((oneHot & 0xAF) != 0) { d0 |= bit4 << 5; d1 |= bit5 << 5; }
            if ((oneHot & 0x05) != 0) { d0 |= bit2 << 6; d1 |= bit3 << 6; }
            d0 = d0 << (32 - dBits) >> (32 - dBits);
            d1 = d1 << (32 - dBits) >> (32 - dBits);
            int shift = (modeValue >> 1) ^ 3;
            a <<= shift;
            b0 <<= shift;
            b1 <<= shift;
            c <<= shift;
            d0 <<= shift;
            d1 <<= shift;
            Span<int> first = [a - c, a - b0 - c - d0, a - b1 - c - d1], second = [a, a - b0, a - b1];
            for (int i = 0; i < 3; i++)
            {
                first[i] = Math.Clamp(first[i], 0, 4095);
                second[i] = Math.Clamp(second[i], 0, 4095);
            }
            if (major != 0)
            {
                (first[0], first[major]) = (first[major], first[0]);
                (second[0], second[major]) = (second[major], second[0]);
            }
            Set(e0, first[0] << 4, first[1] << 4, first[2] << 4, 0);
            Set(e1, second[0] << 4, second[1] << 4, second[2] << 4, 0);
        }

        private static void AlphaHdr(int v6, int v7, out int alpha0, out int alpha1)
        {
            int modeValue = (v6 >> 7 & 1) | (v7 >> 6 & 2);
            v6 &= 0x7F;
            v7 &= 0x7F;
            if (modeValue == 3)
            {
                alpha0 = v6 << 9;
                alpha1 = v7 << 9;
                return;
            }
            v6 |= (v7 << (modeValue + 1)) & 0x780;
            v7 &= 0x3F >> modeValue;
            v7 ^= 32 >> modeValue;
            v7 -= 32 >> modeValue;
            v6 <<= 4 - modeValue;
            v7 = Math.Clamp(v6 + (v7 << (4 - modeValue)), 0, 0xFFF);
            alpha0 = v6 << 4;
            alpha1 = v7 << 4;
        }
    }
}
