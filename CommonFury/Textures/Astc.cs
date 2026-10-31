using System.Buffers.Binary;

namespace CommonFury.Textures;

/// <summary>Decodes 2D ASTC blocks (Khronos ASTC): void-extent blocks, one to four partitions, single and dual weight planes, every
/// color endpoint mode, bilinear weight infill and integer sequences of bits, trits and quints. Blocks are row-major, rows top to bottom.</summary>
/// <remarks>Output values match the ASTC reference decoder. RGBA8 output keeps the top 8 bits of each interpolated 16-bit value, as the
/// UNORM8 decode mode does; for <see cref="AstcProfile.LdrSrgb"/> the bytes stay sRGB-encoded. Blocks the format defines as errors
/// decode to the error color: opaque magenta in RGBA8, all-ones NaN in half floats. HDR endpoints in an LDR profile decode as
/// magenta.</remarks>
public static partial class Astc
{
    /// <summary>Decodes an LDR or sRGB texture into new tightly packed RGBA8 pixels.</summary>
    /// <exception cref="ArgumentException">The block length does not match the dimensions, or the profile is HDR.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> blocks, int width, int height, AstcFormat format)
    {
        var result = new byte[checked(width * height * 4)];
        Decode(blocks, width, height, format, result);
        return result;
    }

    /// <summary>Decodes into caller storage that holds exactly the RGBA8 image.</summary>
    /// <inheritdoc cref="Decode(ReadOnlySpan{byte}, int, int, AstcFormat)"/>
    public static void Decode(ReadOnlySpan<byte> blocks, int width, int height, AstcFormat format, Span<byte> destination)
    {
        if (format.Profile == AstcProfile.Hdr)
            throw new ArgumentException("RGBA8 output needs an LDR profile; decode HDR textures with DecodeHalf.", nameof(format));
        DecodeImage(blocks, width, height, format, destination, half: false);
    }

    /// <summary>Decodes an HDR texture into new pixels stored as <see cref="TextureFormat.R16G16B16A16Float"/>, little-endian.</summary>
    /// <exception cref="ArgumentException">The block length does not match the dimensions, or the profile is not HDR.</exception>
    public static byte[] DecodeHalf(ReadOnlySpan<byte> blocks, int width, int height, AstcFormat format)
    {
        var result = new byte[checked(width * height * 8)];
        DecodeHalf(blocks, width, height, format, result);
        return result;
    }

    /// <summary>Decodes into caller storage that holds exactly the half-float image.</summary>
    /// <inheritdoc cref="DecodeHalf(ReadOnlySpan{byte}, int, int, AstcFormat)"/>
    public static void DecodeHalf(ReadOnlySpan<byte> blocks, int width, int height, AstcFormat format, Span<byte> destination)
    {
        if (format.Profile != AstcProfile.Hdr)
            throw new ArgumentException("Half-float output needs the HDR profile; decode LDR textures with Decode.", nameof(format));
        DecodeImage(blocks, width, height, format, destination, half: true);
    }

    private static void DecodeImage(ReadOnlySpan<byte> blocks, int width, int height, AstcFormat format, Span<byte> destination, bool half)
    {
        if (blocks.Length != format.SurfaceSize(width, height))
            throw new ArgumentException("Block length does not match the dimensions.", nameof(blocks));
        int pixelBytes = half ? 8 : 4;
        if (destination.Length != checked(width * height * pixelBytes))
            throw new ArgumentException("Destination length does not match the image.", nameof(destination));
        var decoder = new BlockDecoder(format, half);
        Span<ushort> texels = stackalloc ushort[BlockDecoder.MaximumTexels * 4];
        int blockWidth = format.BlockWidth, blockHeight = format.BlockHeight;
        int blocksX = format.Element.CountX(width), blocksY = format.Element.CountY(height);
        for (int by = 0, offset = 0; by < blocksY; by++)
        {
            int rows = Math.Min(blockHeight, height - by * blockHeight);
            for (int bx = 0; bx < blocksX; bx++, offset += AstcFormat.BlockBytes)
            {
                decoder.DecodeBlock(blocks.Slice(offset, AstcFormat.BlockBytes), texels);
                int columns = Math.Min(blockWidth, width - bx * blockWidth);
                for (int y = 0; y < rows; y++)
                {
                    var line = destination.Slice(((by * blockHeight + y) * width + bx * blockWidth) * pixelBytes, columns * pixelBytes);
                    var source = texels.Slice(y * blockWidth * 4, columns * 4);
                    if (half)
                        for (int i = 0; i < source.Length; i++)
                            BinaryPrimitives.WriteUInt16LittleEndian(line[(i * 2)..], source[i]);
                    else
                        for (int i = 0; i < source.Length; i++)
                            line[i] = (byte)source[i];
                }
            }
        }
    }

    // Decodes blocks of one footprint and profile, caching the partition and weight-infill tables blocks share.
    private sealed partial class BlockDecoder(AstcFormat format, bool half)
    {
        public const int MaximumTexels = 144;
        private readonly int blockWidth = format.BlockWidth, blockHeight = format.BlockHeight;
        private readonly int texelCount = format.BlockWidth * format.BlockHeight;
        private readonly AstcProfile profile = format.Profile;
        private readonly byte[]?[] partitionTables = new byte[3 * 1024][];
        private readonly Infill?[] infills = new Infill[13 * 13];

        // Writes four channels per texel, in final form: bytes for RGBA8, half-float bits otherwise.
        public void DecodeBlock(ReadOnlySpan<byte> data, Span<ushort> output)
        {
            var bits = new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(data[8..]), BinaryPrimitives.ReadUInt64LittleEndian(data));
            int mode = Bits(bits, 0, 11);
            if ((mode & 0x1FF) == 0x1FC)
            {
                VoidExtent(bits, mode, output);
                return;
            }
            if (!DecodeBlockMode(mode, out int gridWidth, out int gridHeight, out bool dualPlane, out int weightLevel)
                || gridWidth > blockWidth || gridHeight > blockHeight)
            {
                Error(output);
                return;
            }
            int planes = dualPlane ? 2 : 1, weightCount = gridWidth * gridHeight * planes, weightBits = IseBits(weightCount, weightLevel);
            int partitionCount = Bits(bits, 11, 2) + 1;
            if (weightCount > 64 || weightBits is < 24 or > 96 || dualPlane && partitionCount == 4)
            {
                Error(output);
                return;
            }

            Span<int> modes = stackalloc int[4];
            int belowWeights = 128 - weightBits, colorStart = 17, seed = 0;
            if (partitionCount == 1)
                modes[0] = Bits(bits, 13, 4);
            else
            {
                seed = Bits(bits, 13, 10);
                colorStart = 29;
                int encoded = Bits(bits, 23, 6);
                if ((encoded & 3) == 0)
                    modes[..partitionCount].Fill(encoded >> 2);
                else
                {
                    // Per-partition classes and modes continue below the weights.
                    int extra = 3 * partitionCount - 4;
                    belowWeights -= extra;
                    encoded |= Bits(bits, belowWeights, extra) << 6;
                    int baseClass = (encoded & 3) - 1;
                    for (int i = 0; i < partitionCount; i++)
                        modes[i] = (baseClass + (encoded >> (2 + i) & 1)) << 2 | encoded >> (2 + partitionCount + 2 * i) & 3;
                }
            }
            int planeTwoChannel = -1;
            if (dualPlane)
            {
                belowWeights -= 2;
                planeTwoChannel = Bits(bits, belowWeights, 2);
            }
            int colorCount = 0;
            for (int i = 0; i < partitionCount; i++)
                colorCount += ((modes[i] >> 2) + 1) * 2;
            int colorLevel = colorCount > 18 ? -1 : ColorLevel(colorCount, belowWeights - colorStart);
            if (colorLevel < MinimumColorLevel)
            {
                Error(output);
                return;
            }

            Span<byte> colors = stackalloc byte[18];
            DecodeIse(bits, colorStart, colorCount, colorLevel, colors);
            UnquantizeColors(colors[..colorCount], colorLevel);
            Span<byte> weights = stackalloc byte[64];
            DecodeIse(Reverse(bits), 0, weightCount, weightLevel, weights);
            UnquantizeWeights(weights[..weightCount], weightLevel);

            Span<byte> texelWeights = stackalloc byte[2 * MaximumTexels];
            InfillWeights(weights, gridWidth, gridHeight, planes, texelWeights);
            ReadOnlySpan<byte> partitionOf = partitionCount == 1 ? default : PartitionTable(partitionCount, seed);

            Span<int> endpoints = stackalloc int[4 * 8];
            Span<bool> logarithmic = stackalloc bool[4 * 2];
            for (int i = 0, offset = 0; i < partitionCount; i++)
            {
                int count = ((modes[i] >> 2) + 1) * 2;
                Endpoints(modes[i], colors.Slice(offset, count), endpoints.Slice(i * 8, 8), out logarithmic[i * 2], out logarithmic[i * 2 + 1]);
                offset += count;
            }
            for (int texel = 0; texel < texelCount; texel++)
            {
                int partition = partitionOf.IsEmpty ? 0 : partitionOf[texel];
                var pair = endpoints.Slice(partition * 8, 8);
                for (int channel = 0; channel < 4; channel++)
                {
                    int weight = texelWeights[(channel == planeTwoChannel ? MaximumTexels : 0) + texel];
                    int value = (pair[channel] * (64 - weight) + pair[4 + channel] * weight + 32) >> 6;
                    output[texel * 4 + channel] = Finish(value, logarithmic[partition * 2 + (channel == 3 ? 1 : 0)]);
                }
            }
        }

        // Converts an interpolated 16-bit value to the output form.
        private ushort Finish(int value, bool logarithmic)
        {
            if (!half)
                return (ushort)(value >> 8);
            return logarithmic ? LogarithmicToHalf(value) : UnormToHalf(value);
        }

        private void VoidExtent(UInt128 bits, int mode, Span<ushort> output)
        {
            bool hdr = (mode & 0x200) != 0;
            int lowS = Bits(bits, 12, 13), highS = Bits(bits, 25, 13), lowT = Bits(bits, 38, 13), highT = Bits(bits, 51, 13);
            bool allOnes = (lowS & highS & lowT & highT) == 0x1FFF;
            if (Bits(bits, 10, 2) != 3 || (lowS >= highS || lowT >= highT) && !allOnes || hdr && profile != AstcProfile.Hdr)
            {
                Error(output);
                return;
            }
            Span<ushort> color = stackalloc ushort[4];
            for (int channel = 0; channel < 4; channel++)
            {
                int value = Bits(bits, 64 + 16 * channel, 16);
                // Stored half floats pass through, except that signalling NaNs come out quiet as in the reference decoder.
                if (hdr && (value & 0x7C00) == 0x7C00 && (value & 0x3FF) != 0)
                    value |= 0x200;
                color[channel] = hdr ? (ushort)value : Finish(value, false);
            }
            for (int texel = 0; texel < texelCount; texel++)
                color.CopyTo(output.Slice(texel * 4, 4));
        }

        private void Error(Span<ushort> output)
        {
            ReadOnlySpan<ushort> color = half ? [0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF] : [0xFF, 0, 0xFF, 0xFF];
            for (int texel = 0; texel < texelCount; texel++)
                color.CopyTo(output.Slice(texel * 4, 4));
        }

        // Spreads the weight grid over the block's texels with the format's fixed-point bilinear filter.
        private void InfillWeights(ReadOnlySpan<byte> grid, int gridWidth, int gridHeight, int planes, Span<byte> texelWeights)
        {
            if (gridWidth == blockWidth && gridHeight == blockHeight)
            {
                for (int texel = 0; texel < texelCount; texel++)
                    for (int plane = 0; plane < planes; plane++)
                        texelWeights[plane * MaximumTexels + texel] = grid[texel * planes + plane];
                return;
            }
            var infill = infills[gridWidth * 13 + gridHeight] ??= new Infill(blockWidth, blockHeight, gridWidth, gridHeight);
            for (int texel = 0; texel < texelCount; texel++)
            {
                var indices = infill.Indices.AsSpan(texel * 4, 4);
                var factors = infill.Factors.AsSpan(texel * 4, 4);
                for (int plane = 0; plane < planes; plane++)
                {
                    int sum = 8;
                    for (int i = 0; i < 4; i++)
                        sum += grid[indices[i] * planes + plane] * factors[i];
                    texelWeights[plane * MaximumTexels + texel] = (byte)(sum >> 4);
                }
            }
        }

        private byte[] PartitionTable(int partitionCount, int seed)
        {
            ref var table = ref partitionTables[(partitionCount - 2) * 1024 + seed];
            if (table is not null)
                return table;
            table = new byte[texelCount];
            for (int y = 0, texel = 0; y < blockHeight; y++)
                for (int x = 0; x < blockWidth; x++)
                    table[texel++] = SelectPartition(seed, x, y, partitionCount, texelCount < 31);
            return table;
        }
    }

    private sealed class Infill
    {
        public readonly byte[] Indices, Factors;

        public Infill(int blockWidth, int blockHeight, int gridWidth, int gridHeight)
        {
            int count = blockWidth * blockHeight, last = gridWidth * gridHeight - 1;
            Indices = new byte[count * 4];
            Factors = new byte[count * 4];
            int scaleS = (1024 + blockWidth / 2) / (blockWidth - 1), scaleT = (1024 + blockHeight / 2) / (blockHeight - 1);
            for (int t = 0, texel = 0; t < blockHeight; t++)
                for (int s = 0; s < blockWidth; s++, texel++)
                {
                    int gs = (scaleS * s * (gridWidth - 1) + 32) >> 6, gt = (scaleT * t * (gridHeight - 1) + 32) >> 6;
                    int fs = gs & 15, ft = gt & 15, origin = (gs >> 4) + (gt >> 4) * gridWidth;
                    int w11 = (fs * ft + 8) >> 4;
                    ReadOnlySpan<int> factors = [16 - fs - ft + w11, fs - w11, ft - w11, w11];
                    ReadOnlySpan<int> indices = [origin, origin + 1, origin + gridWidth, origin + gridWidth + 1];
                    for (int i = 0; i < 4; i++)
                    {
                        // Neighbours past the grid edge always have a zero factor.
                        Indices[texel * 4 + i] = (byte)Math.Min(indices[i], last);
                        Factors[texel * 4 + i] = (byte)factors[i];
                    }
                }
        }
    }

    private static bool DecodeBlockMode(int mode, out int width, out int height, out bool dualPlane, out int weightLevel)
    {
        int range = mode >> 4 & 1, a = mode >> 5 & 3;
        bool precision = (mode >> 9 & 1) != 0;
        dualPlane = (mode >> 10 & 1) != 0;
        width = height = weightLevel = 0;
        if ((mode & 3) != 0)
        {
            range |= (mode & 3) << 1;
            int b = mode >> 7 & 3;
            switch (mode >> 2 & 3)
            {
                case 0: (width, height) = (b + 4, a + 2); break;
                case 1: (width, height) = (b + 8, a + 2); break;
                case 2: (width, height) = (a + 2, b + 8); break;
                default:
                    if ((mode & 0x100) != 0)
                        (width, height) = ((b & 1) + 2, a + 2);
                    else
                        (width, height) = (a + 2, (b & 1) + 6);
                    break;
            }
        }
        else
        {
            range |= (mode >> 2 & 3) << 1;
            if ((mode >> 2 & 3) == 0)
                return false;
            int b = mode >> 9 & 3;
            switch (mode >> 7 & 3)
            {
                case 0: (width, height) = (12, a + 2); break;
                case 1: (width, height) = (a + 2, 12); break;
                case 2:
                    (width, height) = (a + 6, b + 6);
                    dualPlane = precision = false;
                    break;
                default:
                    if ((mode >> 5 & 3) > 1)
                        return false;
                    (width, height) = (mode >> 5 & 1) == 0 ? (6, 10) : (10, 6);
                    break;
            }
        }
        weightLevel = range - 2 + (precision ? 6 : 0);
        return true;
    }

    private static byte SelectPartition(int seed, int x, int y, int partitionCount, bool smallBlock)
    {
        if (smallBlock)
        {
            x <<= 1;
            y <<= 1;
        }
        seed += (partitionCount - 1) * 1024;
        uint random = Hash52((uint)seed);
        Span<int> seeds = stackalloc int[8];
        for (int i = 0; i < 8; i++)
            seeds[i] = (int)(random >> (4 * i) & 15);
        int shift1, shift2;
        if ((seed & 1) != 0)
        {
            shift1 = (seed & 2) != 0 ? 4 : 5;
            shift2 = partitionCount == 3 ? 6 : 5;
        }
        else
        {
            shift1 = partitionCount == 3 ? 6 : 5;
            shift2 = (seed & 2) != 0 ? 4 : 5;
        }
        for (int i = 0; i < 8; i++)
            seeds[i] = seeds[i] * seeds[i] >> (i % 2 == 0 ? shift1 : shift2);
        int a = (int)((seeds[0] * x + seeds[1] * y + (random >> 14)) & 0x3F);
        int b = (int)((seeds[2] * x + seeds[3] * y + (random >> 10)) & 0x3F);
        int c = partitionCount < 3 ? 0 : (int)((seeds[4] * x + seeds[5] * y + (random >> 6)) & 0x3F);
        int d = partitionCount < 4 ? 0 : (int)((seeds[6] * x + seeds[7] * y + (random >> 2)) & 0x3F);
        if (a >= b && a >= c && a >= d)
            return 0;
        if (b >= c && b >= d)
            return 1;
        return c >= d ? (byte)2 : (byte)3;
    }

    private static uint Hash52(uint value)
    {
        value ^= value >> 15;
        value *= 0xEEDE0891;
        value ^= value >> 5;
        value += value << 16;
        value ^= value >> 7;
        value ^= value >> 3;
        value ^= value << 6;
        value ^= value >> 17;
        return value;
    }

    private static int Bits(UInt128 bits, int start, int count) => (int)(uint)(bits >> start) & (int)((1u << count) - 1);

    private static UInt128 Reverse(UInt128 bits)
    {
        ulong low = (ulong)bits, high = (ulong)(bits >> 64);
        return new UInt128(ReverseBits(low), ReverseBits(high));
    }

    private static ulong ReverseBits(ulong value)
    {
        value = (value >> 1 & 0x5555555555555555) | (value & 0x5555555555555555) << 1;
        value = (value >> 2 & 0x3333333333333333) | (value & 0x3333333333333333) << 2;
        value = (value >> 4 & 0x0F0F0F0F0F0F0F0F) | (value & 0x0F0F0F0F0F0F0F0F) << 4;
        return BinaryPrimitives.ReverseEndianness(value);
    }
}
