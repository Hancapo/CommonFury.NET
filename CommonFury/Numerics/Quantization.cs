using System.Buffers.Binary;
using System.Numerics;
using CommonFury.Binary;

namespace CommonFury.Numerics;

/// <summary>Scalar and vector quantization on explicit grids.</summary>
public static class Quantization
{
    /// <summary>Scales and rounds to nearest, ties to even; throws when the result does not fit Int16.</summary>
    public static short Signed16Rounded(float value, float scale)
    {
        double encoded = Math.Round((double)value * scale, MidpointRounding.ToEven);
        if (!float.IsFinite(scale) || !double.IsFinite(encoded) || encoded < short.MinValue || encoded > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        return (short)encoded;
    }

    /// <summary>Scales and truncates toward zero; throws when the result does not fit Int16.</summary>
    public static short Signed16(float value, float scale)
    {
        float encoded = MathF.Truncate(value * scale);
        if (!float.IsFinite(encoded) || encoded < short.MinValue || encoded > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Scaled value exceeds Int16 storage.");
        return (short)encoded;
    }

    /// <summary>Wraps the angle to one turn and encodes it in 255 steps, ties to even.</summary>
    public static byte EncodeAngle8(float radians)
    {
        if (!float.IsFinite(radians))
            throw new ArgumentOutOfRangeException(nameof(radians));
        double wrapped = radians % Math.Tau;
        if (wrapped < 0)
            wrapped += Math.Tau;
        return (byte)Math.Round(wrapped * 255 / Math.Tau, MidpointRounding.ToEven);
    }

    /// <summary>Decodes to radians; 255 is one full turn.</summary>
    public static float DecodeAngle8(byte angle) => (float)(angle * (Math.Tau / 255));

    /// <summary>Unsigned 0.16 fixed point: 65536 steps across the bounds.</summary>
    public static Vector3 DecodeFixedPosition16(ushort x, ushort y, ushort z, Aabb3 bounds) =>
        bounds.Minimum + new Vector3(x / 65536f, y / 65536f, z / 65536f) * bounds.Size;

    /// <summary>Truncates to unsigned 0.16 fixed point, saturating the upper endpoint at 65535; flat axes encode zero.
    /// Throws for positions outside the bounds.</summary>
    public static void EncodeFixedPosition16(Vector3 position, Aabb3 bounds, Span<ushort> output)
    {
        if (output.Length != 3)
            throw new ArgumentException("Position encoding requires three components.", nameof(output));
        if (!VectorMath.IsFinite(position))
            throw new ArgumentOutOfRangeException(nameof(position));
        output[0] = Encode(position.X, bounds.Minimum.X, bounds.Maximum.X);
        output[1] = Encode(position.Y, bounds.Minimum.Y, bounds.Maximum.Y);
        output[2] = Encode(position.Z, bounds.Minimum.Z, bounds.Maximum.Z);

        static ushort Encode(float value, float min, float max)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(nameof(position), "Position lies outside its quantization bounds.");
            return max == min ? (ushort)0 : (ushort)Math.Min(65535, ((double)value - min) / ((double)max - min) * 65536);
        }
    }

    /// <summary>Unsigned 16-bit normalized coordinates: 65535 steps across the bounds.</summary>
    public static Vector3 DecodePosition16(ushort x, ushort y, ushort z, Aabb3 bounds) =>
        bounds.Minimum + new Vector3(x / 65535f, y / 65535f, z / 65535f) * bounds.Size;

    /// <summary>Truncates to 65535 steps across the bounds. The box must be finite with positive size on every axis,
    /// and the position must lie inside it.</summary>
    public static void EncodePosition16(Vector3 position, Aabb3 bounds, Span<ushort> output)
    {
        if (output.Length != 3)
            throw new ArgumentException("Position encoding requires three components.", nameof(output));
        Vector3 size = bounds.Size;
        if (!VectorMath.IsFinite(position) || !VectorMath.IsFinite(size) || size.X <= 0 || size.Y <= 0 || size.Z <= 0 ||
            position.X < bounds.Minimum.X || position.Y < bounds.Minimum.Y || position.Z < bounds.Minimum.Z ||
            position.X > bounds.Maximum.X || position.Y > bounds.Maximum.Y || position.Z > bounds.Maximum.Z)
            throw new ArgumentOutOfRangeException(nameof(position), "Position must lie inside nondegenerate representable batch bounds.");
        Vector3 packed = (position - bounds.Minimum) / size * 65535;
        output[0] = (ushort)packed.X;
        output[1] = (ushort)packed.Y;
        output[2] = (ushort)packed.Z;
    }

    /// <summary>Double-precision form of <see cref="DecodePosition16(ushort, ushort, ushort, Aabb3)"/>:
    /// <c>origin + value * (extent / 65535)</c>, for coordinates that would round in single precision.</summary>
    public static Vector3D DecodePosition16(ushort x, ushort y, ushort z, Vector3 origin, Vector3D extent)
    {
        Vector3D scale = new(extent.X / 65535.0, extent.Y / 65535.0, extent.Z / 65535.0);
        return new(origin.X + x * scale.X, origin.Y + y * scale.Y, origin.Z + z * scale.Z);
    }

    /// <summary>Decodes little-endian UInt16 XYZ triples with the double-precision form; the result is owned.</summary>
    public static Vector3D[] DecodePositions16(ReadOnlySpan<byte> data, Vector3 origin, Vector3D extent)
    {
        if (data.Length % 6 != 0)
            throw new ArgumentException("Packed XYZ data must contain complete six-byte triples.", nameof(data));
        var result = new Vector3D[data.Length / 6];
        for (int i = 0; i < result.Length; i++)
        {
            ReadOnlySpan<byte> source = data[(i * 6)..];
            result[i] = DecodePosition16(BinaryPrimitives.ReadUInt16LittleEndian(source), BinaryPrimitives.ReadUInt16LittleEndian(source[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(source[4..]), origin, extent);
        }
        return result;
    }

    /// <summary>Decodes little-endian Int16 XYZ triples as <c>value * scale + offset</c>; the result is owned.</summary>
    public static Vector3[] DecodeSignedPositions16(ReadOnlySpan<byte> data, Vector3 scale, Vector3 offset)
    {
        if (data.Length % 6 != 0)
            throw new ArgumentException("Packed XYZ data must contain complete six-byte triples.", nameof(data));
        var result = new Vector3[data.Length / 6];
        for (int i = 0; i < result.Length; i++)
        {
            ReadOnlySpan<byte> source = data[(i * 6)..];
            result[i] = new Vector3(BinaryPrimitives.ReadInt16LittleEndian(source), BinaryPrimitives.ReadInt16LittleEndian(source[2..]),
                BinaryPrimitives.ReadInt16LittleEndian(source[4..])) * scale + offset;
        }
        return result;
    }

    public static float DecodeLinear(float encoded, float quantum, float offset) => encoded * quantum + offset;

    /// <summary>Nearest grid code, halves rounding up; throws when the code does not fit <paramref name="bitCount"/> unsigned bits.</summary>
    public static uint EncodeLinear(float value, float quantum, float offset, int bitCount)
    {
        if (bitCount is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        if (!float.IsFinite(quantum) || quantum <= 0 || !float.IsFinite(offset))
            throw new ArgumentOutOfRangeException(nameof(quantum));
        double code = Math.Floor(((double)value - offset) / quantum + 0.5);
        double maximum = bitCount == 32 ? uint.MaxValue : (1u << bitCount) - 1u;
        if (!double.IsFinite(code) || code < 0 || code > maximum)
            throw new ArgumentOutOfRangeException(nameof(value), "Sample exceeds the requested quantization grid.");
        return (uint)code;
    }

    /// <summary>Whether two packed normal components lie inside the unit disk, so Z can be reconstructed.</summary>
    public static bool IsPositiveZNormal(byte x, byte y)
    {
        int a = 2 * x - 255, b = 2 * y - 255;
        return a * a + b * b <= 255 * 255;
    }

    /// <summary>Maps bytes to [-1, 1] XY and reconstructs a nonnegative Z; throws when XY lies outside the unit disk.</summary>
    public static Vector3 DecodePositiveZNormal(byte x, byte y)
    {
        if (!IsPositiveZNormal(x, y))
            throw new ArgumentException("Packed normal components lie outside the representable disk.");
        Vector2 xy = NormalXY(x, y);
        return new(xy, MathF.Sqrt(1 - Vector2.Dot(xy, xy)));
    }

    /// <summary>Like <see cref="DecodePositiveZNormal(byte, byte)"/>, but clamps <c>Z²</c> to at least
    /// <paramref name="minimumZSquared"/> instead of rejecting XY outside the unit disk. The result is not renormalized.</summary>
    public static Vector3 DecodePositiveZNormal(byte x, byte y, float minimumZSquared)
    {
        if (!float.IsFinite(minimumZSquared) || minimumZSquared < 0 || minimumZSquared > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumZSquared));
        Vector2 xy = NormalXY(x, y);
        return new(xy, MathF.Sqrt(MathF.Max(minimumZSquared, 1 - Vector2.Dot(xy, xy))));
    }

    private static Vector2 NormalXY(byte x, byte y) => new Vector2(x / 255f, y / 255f) * 2 - Vector2.One;

    /// <summary>Truncates a value in [0, <paramref name="maximum"/>] to 255 steps.</summary>
    public static byte Unsigned8(float value, float maximum)
    {
        if (!float.IsFinite(maximum) || maximum <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        if (!float.IsFinite(value) || value < 0 || value > maximum)
            throw new ArgumentOutOfRangeException(nameof(value));
        return (byte)MathF.Min(255, value * (255 / maximum));
    }

    /// <summary>Maps a byte to [0, <paramref name="maximum"/>].</summary>
    public static float DecodeUnsigned8(byte value, float maximum)
    {
        if (!float.IsFinite(maximum) || maximum <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        return value * (maximum / 255);
    }

    /// <summary>Rounds each component to the nearest multiple of a positive step, ties to even.</summary>
    public static Vector3 Snap(Vector3 value, float step)
    {
        if (!VectorMath.IsFinite(value))
            throw new ArgumentException("Value must be finite.", nameof(value));
        if (!float.IsFinite(step) || step <= 0)
            throw new ArgumentOutOfRangeException(nameof(step));
        Vector3 scaled = value / step;
        var result = new Vector3(MathF.Round(scaled.X), MathF.Round(scaled.Y), MathF.Round(scaled.Z)) * step;
        if (!VectorMath.IsFinite(result))
            throw new ArgumentOutOfRangeException(nameof(step), "Snapped coordinates exceed finite storage.");
        return result;
    }

    /// <summary>Unsigned X in the high 11 bits, Y in the middle 11 and Z in the low 10.</summary>
    public static Vector3 Decode111110(uint packed) => new(
        BitField32.Extract(packed, 21, 11), BitField32.Extract(packed, 10, 11), BitField32.Extract(packed, 0, 10));

    /// <summary>Truncates nonnegative coordinates into the <see cref="Decode111110(uint)"/> layout; throws when out of range.</summary>
    public static uint Encode111110(Vector3 value)
    {
        if (!VectorMath.IsFinite(value) || value.X < 0 || value.Y < 0 || value.Z < 0 || value.X >= 2048 || value.Y >= 2048 || value.Z >= 1024)
            throw new ArgumentOutOfRangeException(nameof(value), "Coordinates exceed unsigned 11/11/10-bit storage.");
        return BitField32.Replace(BitField32.Replace((uint)value.Z, 10, 11, (uint)value.Y), 21, 11, (uint)value.X);
    }

    /// <summary>Decodes little-endian 11/11/10 words as <c>origin + value * step</c>; the result is owned.
    /// Throws <see cref="InvalidDataException"/> when a decoded coordinate is not finite.</summary>
    public static Vector3[] Decode111110Positions(ReadOnlySpan<byte> data, Vector3 origin, float step)
    {
        if (data.Length % 4 != 0)
            throw new ArgumentException("Packed vertices require complete four-byte words.", nameof(data));
        if (!float.IsFinite(step) || step <= 0)
            throw new ArgumentOutOfRangeException(nameof(step));
        var result = new Vector3[data.Length / 4];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = origin + Decode111110(BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..])) * step;
            if (!VectorMath.IsFinite(result[i]))
                throw new InvalidDataException("Decoded packed vertex exceeds finite coordinates.");
        }
        return result;
    }
}
