using System.Buffers.Binary;
using System.Numerics;

namespace CommonFury.Textures;

/// <summary>BC1 block compression for tightly packed RGBA8 images, rows top to bottom.
/// Colour bytes are encoded as given: the caller supplies sRGB-encoded bytes for <see cref="TextureFormat.BC1UnormSrgb"/>.</summary>
/// <remarks>Opaque blocks use four-colour mode. Blocks containing a pixel with alpha below 128 use three-colour mode,
/// where those pixels become transparent black. Uniform blocks store the nearest 5:6:5 colour in both endpoints with
/// all indices zero. Partial edge blocks are fitted to their existing pixels only.</remarks>
public static class Bc1
{
    /// <summary>Encodes RGBA8 pixels into a new block array of <see cref="TextureLayout.SurfaceSize"/> bytes.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        byte[] result = new byte[TextureLayout.SurfaceSize(width, height, TextureFormat.BC1Unorm)];
        Encode(rgba, width, height, result);
        return result;
    }

    /// <summary>Encodes RGBA8 pixels into caller storage, which must hold exactly one BC1 surface. Input is not retained.</summary>
    public static void Encode(ReadOnlySpan<byte> rgba, int width, int height, Span<byte> destination)
    {
        int size = TextureLayout.SurfaceSize(width, height, TextureFormat.BC1Unorm);
        if (rgba.Length != checked(width * height * 4)) throw new ArgumentException("RGBA8 length does not match the dimensions.", nameof(rgba));
        if (destination.Length != size) throw new ArgumentException("Destination length does not match one BC1 surface.", nameof(destination));
        Span<Vector3> colours = stackalloc Vector3[16];
        Span<bool> present = stackalloc bool[16], transparent = stackalloc bool[16];
        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
        for (int by = 0; by < blocksY; by++)
            for (int bx = 0; bx < blocksX; bx++)
            {
                for (int i = 0; i < 16; i++)
                {
                    int x = bx * 4 + i % 4, y = by * 4 + i / 4;
                    present[i] = x < width && y < height;
                    if (!present[i]) { colours[i] = default; transparent[i] = false; continue; }
                    var pixel = rgba.Slice((y * width + x) * 4, 4);
                    colours[i] = new(pixel[0], pixel[1], pixel[2]);
                    transparent[i] = pixel[3] < 128;
                }
                EncodeBlock(colours, present, transparent, destination.Slice((by * blocksX + bx) * 8, 8));
            }
    }

    /// <summary>Decodes BC1 blocks into new tightly packed RGBA8 pixels. Three-colour transparent texels decode to zero.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> blocks, int width, int height)
    {
        if (blocks.Length != TextureLayout.SurfaceSize(width, height, TextureFormat.BC1Unorm))
            throw new ArgumentException("Block length does not match the dimensions.", nameof(blocks));
        byte[] result = new byte[checked(width * height * 4)];
        Span<byte> palette = stackalloc byte[16];
        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
        for (int by = 0; by < blocksY; by++)
            for (int bx = 0; bx < blocksX; bx++)
            {
                var block = blocks.Slice((by * blocksX + bx) * 8, 8);
                Palette(BinaryPrimitives.ReadUInt16LittleEndian(block), BinaryPrimitives.ReadUInt16LittleEndian(block[2..]), palette);
                uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
                for (int i = 0; i < 16; i++)
                {
                    int x = bx * 4 + i % 4, y = by * 4 + i / 4;
                    if (x >= width || y >= height) continue;
                    int index = (int)(indices >> (i * 2)) & 3;
                    palette.Slice(index * 4, 4).CopyTo(result.AsSpan((y * width + x) * 4, 4));
                }
            }
        return result;
    }

    // Palette entries follow the common integer interpolation of expanded 8-bit endpoints.
    private static void Palette(ushort c0, ushort c1, Span<byte> palette)
    {
        var a = Expand(c0); var b = Expand(c1);
        for (int channel = 0; channel < 3; channel++)
        {
            palette[channel] = (byte)a[channel]; palette[4 + channel] = (byte)b[channel];
            palette[8 + channel] = (byte)(c0 > c1 ? (2 * a[channel] + b[channel]) / 3 : (a[channel] + b[channel]) / 2);
            palette[12 + channel] = (byte)(c0 > c1 ? (a[channel] + 2 * b[channel]) / 3 : 0);
        }
        palette[3] = palette[7] = palette[11] = 255;
        palette[15] = c0 > c1 ? (byte)255 : (byte)0;
    }

    private static void EncodeBlock(ReadOnlySpan<Vector3> colours, ReadOnlySpan<bool> present, ReadOnlySpan<bool> transparent, Span<byte> block)
    {
        bool threeColour = false, any = false, uniform = true;
        Vector3 first = default;
        for (int i = 0; i < 16; i++)
        {
            if (!present[i]) continue;
            if (transparent[i]) { threeColour = true; continue; }
            if (!any) { first = colours[i]; any = true; }
            else if (colours[i] != first) uniform = false;
        }
        if (!any)
        {
            // Every existing pixel is transparent: black endpoints, transparent index 3.
            BinaryPrimitives.WriteUInt32LittleEndian(block, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(block[4..], uint.MaxValue);
            return;
        }
        if (uniform)
        {
            ushort solid = Quantize(first);
            WriteBlock(block, solid, solid, colours, present, transparent);
            return;
        }
        var (low, high) = FitEndpoints(colours, present, transparent);
        ushort c0 = Quantize(high), c1 = Quantize(low);
        // Four-colour mode requires c0 > c1; three-colour mode requires c0 <= c1.
        if (threeColour ? c0 > c1 : c0 < c1) (c0, c1) = (c1, c0);
        WriteBlock(block, c0, c1, colours, present, transparent);
    }

    private static void WriteBlock(Span<byte> block, ushort c0, ushort c1, ReadOnlySpan<Vector3> colours, ReadOnlySpan<bool> present, ReadOnlySpan<bool> transparent)
    {
        Span<byte> palette = stackalloc byte[16];
        Palette(c0, c1, palette);
        bool fourColour = c0 > c1;
        uint indices = 0;
        for (int i = 0; i < 16; i++)
        {
            int index = 0;
            if (present[i] && transparent[i]) index = 3;
            else if (present[i] && c0 != c1)
            {
                float best = float.MaxValue;
                for (int candidate = 0; candidate < (fourColour ? 4 : 3); candidate++)
                {
                    var entry = new Vector3(palette[candidate * 4], palette[candidate * 4 + 1], palette[candidate * 4 + 2]);
                    float distance = Vector3.DistanceSquared(entry, colours[i]);
                    if (distance < best) { best = distance; index = candidate; }
                }
            }
            indices |= (uint)index << (i * 2);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(block, c0);
        BinaryPrimitives.WriteUInt16LittleEndian(block[2..], c1);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], indices);
    }

    // Endpoints span the opaque pixels along their principal colour axis.
    private static (Vector3 Low, Vector3 High) FitEndpoints(ReadOnlySpan<Vector3> colours, ReadOnlySpan<bool> present, ReadOnlySpan<bool> transparent)
    {
        Vector3 mean = Vector3.Zero; int count = 0;
        for (int i = 0; i < 16; i++) if (present[i] && !transparent[i]) { mean += colours[i]; count++; }
        mean /= count;
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        for (int i = 0; i < 16; i++)
        {
            if (!present[i] || transparent[i]) continue;
            var d = colours[i] - mean;
            xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
        }
        Vector3 axis = new(1, 1, 1);
        for (int iteration = 0; iteration < 8; iteration++)
        {
            axis = new(xx * axis.X + xy * axis.Y + xz * axis.Z, xy * axis.X + yy * axis.Y + yz * axis.Z, xz * axis.X + yz * axis.Y + zz * axis.Z);
            float length = axis.Length();
            if (length < 1e-6f) { axis = Vector3.Normalize(new(1, 1, 1)); break; }
            axis /= length;
        }
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            if (!present[i] || transparent[i]) continue;
            float t = Vector3.Dot(colours[i] - mean, axis);
            min = Math.Min(min, t); max = Math.Max(max, t);
        }
        var clamp = new Vector3(255);
        return (Vector3.Clamp(mean + axis * min, Vector3.Zero, clamp), Vector3.Clamp(mean + axis * max, Vector3.Zero, clamp));
    }

    private static ushort Quantize(Vector3 colour)
    {
        int r = ((int)MathF.Round(colour.X) * 31 + 127) / 255, g = ((int)MathF.Round(colour.Y) * 63 + 127) / 255, b = ((int)MathF.Round(colour.Z) * 31 + 127) / 255;
        return (ushort)(r << 11 | g << 5 | b);
    }

    private static int[] Expand(ushort colour)
    {
        int r = colour >> 11 & 31, g = colour >> 5 & 63, b = colour & 31;
        return [r << 3 | r >> 2, g << 2 | g >> 4, b << 3 | b >> 2];
    }
}
