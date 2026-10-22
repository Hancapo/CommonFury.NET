using System.Numerics;
using CommonFury.Binary;
using CommonFury.Numerics;

namespace CommonFury.Collisions;

/// <summary>Three signed 16-bit quantized coordinates.</summary>
public readonly record struct Short3(short X, short Y, short Z);

/// <summary>The 16-byte stored BVH node: quantized minimum and maximum (six Int16), UInt16 item id, item count byte and padding byte.
/// Branch: <see cref="ItemId"/> is the forward escape distance and <see cref="ItemCount"/> is zero.</summary>
public readonly record struct QuantizedBvhNode(Short3 Minimum, Short3 Maximum, ushort ItemId, byte ItemCount, byte Padding = 0)
{
    /// <summary>Encoded size in bytes.</summary>
    public const int Size = 16;

    public bool IsLeaf => ItemCount != 0;

    /// <summary>Reads a little-endian node from the first 16 bytes of the input.</summary>
    public static QuantizedBvhNode Read(ReadOnlySpan<byte> data)
    {
        var reader = new BinarySpanReader(BinaryLayout.Slice(data, 0, Size));
        Short3 minimum = new(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());
        Short3 maximum = new(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());
        return new(minimum, maximum, reader.ReadUInt16(), reader.ReadByte(), reader.ReadByte());
    }

    /// <summary>Writes the node as 16 little-endian bytes.</summary>
    public void Write(Span<byte> data)
    {
        var writer = new BinarySpanWriter(data);
        writer.WriteInt16(Minimum.X);
        writer.WriteInt16(Minimum.Y);
        writer.WriteInt16(Minimum.Z);
        writer.WriteInt16(Maximum.X);
        writer.WriteInt16(Maximum.Y);
        writer.WriteInt16(Maximum.Z);
        writer.WriteUInt16(ItemId);
        writer.WriteByte(ItemCount);
        writer.WriteByte(Padding);
    }
}

/// <summary>Selects how box limits are converted to quantized coordinates. Formats differ, so there is no default.</summary>
public enum BvhQuantizationRounding
{
    /// <summary>Multiply the offset from the center by <see cref="BvhQuantization.QuantumInverse"/>, clamp to the Int16 range,
    /// then floor minima and ceil maxima.</summary>
    InverseClamped,
    /// <summary>Divide the offset from the center by <see cref="BvhQuantization.Quantum"/>, floor minima and ceil maxima,
    /// and reject coordinates outside the Int16 range.</summary>
    DivideChecked
}

/// <summary>Maps world coordinates to signed 16-bit BVH coordinates: <c>world = Center + quantized * Quantum</c>.</summary>
/// <param name="Center">World position of quantized zero.</param>
/// <param name="Quantum">Positive per-axis size of one quantized step.</param>
/// <param name="QuantumInverse">Stored reciprocal of <paramref name="Quantum"/>.</param>
public readonly record struct BvhQuantization(Vector3 Center, Vector3 Quantum, Vector3 QuantumInverse)
{
    /// <summary>Spreads the box size over 65534 steps per axis, using 1/32767 for flat axes, centered on the box.</summary>
    public static BvhQuantization FromSize(Aabb3 bounds)
    {
        Vector3 quantum = BoundsQuantization.Step(bounds);
        return new(bounds.Center, quantum, Vector3.One / quantum);
    }

    /// <summary>Spreads the larger half-extent over 32766 steps per axis, with a 1e-6 minimum step, centered on the box.</summary>
    public static BvhQuantization FromExtent(Aabb3 bounds)
    {
        Vector3 center = bounds.Center;
        Vector3 extent = Vector3.Max(Vector3.Abs(bounds.Minimum - center), Vector3.Abs(bounds.Maximum - center));
        Vector3 quantum = Vector3.Max(extent / 32766, new Vector3(1e-6f));
        if (!VectorMath.IsFinite(quantum) || !VectorMath.IsFinite(Vector3.One / quantum))
            throw new ArgumentException("BVH extent exceeds finite range.", nameof(bounds));
        return new(center, quantum, Vector3.One / quantum);
    }

    /// <summary>Returns the world-space limits of quantized coordinates without validating their order.</summary>
    public (Vector3 Minimum, Vector3 Maximum) Dequantize(Short3 minimum, Short3 maximum) =>
        (Center + new Vector3(minimum.X, minimum.Y, minimum.Z) * Quantum, Center + new Vector3(maximum.X, maximum.Y, maximum.Z) * Quantum);

    public (Vector3 Minimum, Vector3 Maximum) Dequantize(QuantizedBvhNode node) => Dequantize(node.Minimum, node.Maximum);

    /// <summary>Converts box limits to conservative quantized coordinates: minima round down and maxima round up.</summary>
    public (Short3 Minimum, Short3 Maximum) Quantize(Aabb3 bounds, BvhQuantizationRounding rounding)
    {
        Vector3 minimum, maximum;
        switch (rounding)
        {
            case BvhQuantizationRounding.InverseClamped:
                minimum = (bounds.Minimum - Center) * QuantumInverse;
                maximum = (bounds.Maximum - Center) * QuantumInverse;
                return (new(BoundsQuantization.BoundCoordinate(minimum.X, false), BoundsQuantization.BoundCoordinate(minimum.Y, false),
                        BoundsQuantization.BoundCoordinate(minimum.Z, false)),
                    new(BoundsQuantization.BoundCoordinate(maximum.X, true), BoundsQuantization.BoundCoordinate(maximum.Y, true),
                        BoundsQuantization.BoundCoordinate(maximum.Z, true)));
            case BvhQuantizationRounding.DivideChecked:
                minimum = (bounds.Minimum - Center) / Quantum;
                maximum = (bounds.Maximum - Center) / Quantum;
                return (new(Checked(minimum.X, false), Checked(minimum.Y, false), Checked(minimum.Z, false)),
                    new(Checked(maximum.X, true), Checked(maximum.Y, true), Checked(maximum.Z, true)));
            default:
                throw new ArgumentOutOfRangeException(nameof(rounding));
        }
    }

    /// <summary>Quantizes an unquantized node into its stored form, keeping its id and count.</summary>
    public QuantizedBvhNode Quantize(BvhNode node, BvhQuantizationRounding rounding)
    {
        var (minimum, maximum) = Quantize(node.Bounds, rounding);
        return new(minimum, maximum, node.ItemId, node.ItemCount);
    }

    private static short Checked(float value, bool maximum)
    {
        float rounded = maximum ? MathF.Ceiling(value) : MathF.Floor(value);
        if (!float.IsFinite(rounded) || rounded < short.MinValue || rounded > short.MaxValue)
            throw new ArgumentException("BVH coordinate exceeds quantization range.");
        return (short)rounded;
    }
}
