using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

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
        for (int by = 0, offset = 0; by < blocksY; by++)
        {
            int rows = Math.Min(4, height - by * 4);
            for (int bx = 0; bx < blocksX; bx++, offset += 8)
            {
                int columns = Math.Min(4, width - bx * 4);
                // The encoder reads nothing else of a texel outside the image, so only its presence is reset.
                present.Clear();
                for (int y = 0; y < rows; y++)
                {
                    var line = rgba.Slice(((by * 4 + y) * width + bx * 4) * 4, columns * 4);
                    for (int x = 0; x < columns; x++)
                    {
                        int i = y * 4 + x;
                        present[i] = true;
                        colours[i] = new(line[x * 4], line[x * 4 + 1], line[x * 4 + 2]);
                        transparent[i] = line[x * 4 + 3] < 128;
                    }
                }
                EncodeBlock(colours, present, transparent, destination.Slice(offset, 8));
            }
        }
    }

    /// <summary>Decodes BC1 blocks into new tightly packed RGBA8 pixels. Three-colour transparent texels decode to zero.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> blocks, int width, int height)
    {
        if (blocks.Length != TextureLayout.SurfaceSize(width, height, TextureFormat.BC1Unorm))
            throw new ArgumentException("Block length does not match the dimensions.", nameof(blocks));
        byte[] result = new byte[checked(width * height * 4)];
        // Texels move as whole native-order words, which keeps their byte order on any platform.
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(result.AsSpan());
        Span<byte> palette = stackalloc byte[16];
        ReadOnlySpan<uint> entries = MemoryMarshal.Cast<byte, uint>(palette);
        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
        for (int by = 0, offset = 0; by < blocksY; by++)
        {
            int rows = Math.Min(4, height - by * 4);
            for (int bx = 0; bx < blocksX; bx++, offset += 8)
            {
                var block = blocks.Slice(offset, 8);
                Palette(BinaryPrimitives.ReadUInt16LittleEndian(block), BinaryPrimitives.ReadUInt16LittleEndian(block[2..]), palette);
                uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
                for (int y = 0; y < rows; y++, indices >>= 8)
                {
                    var line = pixels.Slice((by * 4 + y) * width + bx * 4, Math.Min(4, width - bx * 4));
                    for (int x = 0; x < line.Length; x++)
                        line[x] = entries[(int)(indices >> (x * 2)) & 3];
                }
            }
        }
        return result;
    }

    // Palette entries follow the common integer interpolation of expanded 8-bit endpoints.
    private static void Palette(ushort c0, ushort c1, Span<byte> palette)
    {
        Expand(c0, palette);
        Expand(c1, palette[4..]);
        for (int channel = 0; channel < 3; channel++)
        {
            int a = palette[channel], b = palette[4 + channel];
            palette[8 + channel] = (byte)(c0 > c1 ? (2 * a + b) / 3 : (a + b) / 2);
            palette[12 + channel] = (byte)(c0 > c1 ? (a + 2 * b) / 3 : 0);
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
        Span<Vector3> entries = stackalloc Vector3[4];
        for (int candidate = 0; candidate < 4; candidate++)
            entries[candidate] = new(palette[candidate * 4], palette[candidate * 4 + 1], palette[candidate * 4 + 2]);
        int candidates = c0 > c1 ? 4 : 3;
        uint indices = 0;
        for (int i = 0; i < 16; i++)
        {
            int index = 0;
            if (present[i] && transparent[i]) index = 3;
            else if (present[i] && c0 != c1)
            {
                float best = float.MaxValue;
                for (int candidate = 0; candidate < candidates; candidate++)
                {
                    float distance = Vector3.DistanceSquared(entries[candidate], colours[i]);
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

    private static void Expand(ushort colour, Span<byte> entry)
    {
        int r = colour >> 11 & 31, g = colour >> 5 & 63, b = colour & 31;
        entry[0] = (byte)(r << 3 | r >> 2);
        entry[1] = (byte)(g << 2 | g >> 4);
        entry[2] = (byte)(b << 3 | b >> 2);
    }
}
