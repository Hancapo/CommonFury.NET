using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Animations;

/// <summary>Identifies how an animation channel stores or reconstructs its samples. These are logical kinds, not stored
/// channel-group indices: formats number their groups differently, so readers and writers translate through an
/// <see cref="AnimationChannelGroupTable"/>.</summary>
public enum AnimationChannelType : byte
{
    /// <summary>Constant quaternion stored as XYZ; W is reconstructed as nonnegative.</summary>
    StaticQuaternion,
    StaticVector3,
    StaticFloat,
    /// <summary>Unquantized 32-bit float per frame.</summary>
    RawFloat,
    /// <summary>Fixed-width integer code per frame on a shared quantization grid.</summary>
    QuantizedFloat,
    /// <summary>Per-frame index into a shared table of quantized codes.</summary>
    IndirectQuantizedFloat,
    /// <summary>Segmented second-difference integer codes stored in the constant section.</summary>
    LinearFloat,
    /// <summary>Quaternion operation that reconstructs one omitted component after interpolation.</summary>
    ReconstructQuaternion,
    /// <summary>Quaternion operation that normalizes four interpolated components.</summary>
    NormalizeQuaternion,
}

/// <summary>Component layout of an animation track.</summary>
public enum AnimationTrackFormat : byte
{
    Vector3 = 0,
    /// <summary>XYZW order.</summary>
    Quaternion = 1,
    Float = 2
}

/// <summary>An owned stream channel. Quaternion components use XYZW ordering; no stored values are implicitly normalized.</summary>
public abstract class AnimationChannel
{
    public abstract AnimationChannelType Type { get; }
    /// <summary>First track component written by this channel (0..3); for quaternion reconstruction, the omitted component.</summary>
    public int ComponentIndex { get; set; }
    public virtual int ComponentCount => 1;
    /// <summary>Number of stored samples; one for constants, the frame count for per-frame channels.</summary>
    public virtual int SampleCount => 1;
    internal virtual int FrameBits => 0;
    internal int SourceWordOffset { get; set; }
    internal int SourceBitOffset { get; set; }
    private protected AnimationChannel(int componentIndex) { ComponentIndex = componentIndex; }
    /// <summary>Returns the components at an integer frame, starting at X; unused components are zero.
    /// Quaternion operations depend on the other channels of their track and must be sampled through it.</summary>
    public abstract Vector4 Sample(int frame);
    internal virtual Vector4 Interpolate(int first, int second, float amount) => Vector4.Lerp(Sample(first), Sample(second), amount);
    protected static void RequireFrame(int frame, int count)
    {
        if ((uint)frame >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(frame));
    }
    private protected static float Decode(float code, float quantum, float offset) => code * quantum + offset;
}

/// <summary>A scalar that is the same at every frame.</summary>
public sealed class StaticFloatChannel(float value = 0, int componentIndex = 0) : AnimationChannel(componentIndex)
{
    public override AnimationChannelType Type => AnimationChannelType.StaticFloat;
    public float Value { get; set; } = value;
    public override Vector4 Sample(int frame) { ArgumentOutOfRangeException.ThrowIfNegative(frame); return new(Value, 0, 0, 0); }
}

/// <summary>An XYZ vector that is the same at every frame.</summary>
public sealed class StaticVector3Channel(Vector3 value, int componentIndex = 0) : AnimationChannel(componentIndex)
{
    public override AnimationChannelType Type => AnimationChannelType.StaticVector3;
    public override int ComponentCount => 3;
    public Vector3 Value { get; set; } = value;
    public override Vector4 Sample(int frame) { ArgumentOutOfRangeException.ThrowIfNegative(frame); return new(Value, 0); }
}

/// <summary>Stores exactly three components. The nonnegative W component is reconstructed on sampling.</summary>
public sealed class StaticQuaternionChannel(Vector3 components) : AnimationChannel(0)
{
    public override AnimationChannelType Type => AnimationChannelType.StaticQuaternion;
    public override int ComponentCount => 4;
    public Vector3 Components { get; set; } = components;
    public override Vector4 Sample(int frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        Quaternion q = QuaternionMath.Reconstruct(Components);
        return new(q.X, q.Y, q.Z, q.W);
    }
}

/// <summary>Stores editable, unquantized scalar samples in an owned array.</summary>
public sealed class RawFloatChannel : AnimationChannel
{
    public override AnimationChannelType Type => AnimationChannelType.RawFloat;
    /// <summary>One sample per frame; editable in place.</summary>
    public float[] Values { get; }
    public override int SampleCount => Values.Length;
    internal override int FrameBits => 32;
    /// <summary>Copies <paramref name="values"/>.</summary>
    public RawFloatChannel(ReadOnlySpan<float> values, int componentIndex = 0) : base(componentIndex) { Values = values.ToArray(); }
    public override Vector4 Sample(int frame) { RequireFrame(frame, Values.Length); return new(Values[frame], 0, 0, 0); }
}

/// <summary>Stores scalar samples as integer codes interpreted as code × Quantum + Offset.</summary>
public sealed class QuantizedFloatChannel : AnimationChannel
{
    public override AnimationChannelType Type => AnimationChannelType.QuantizedFloat;
    /// <summary>Bits per stored code, 0 to 32.</summary>
    public int ValueBits { get; set; }
    public float Quantum { get; set; }
    public float Offset { get; set; }
    /// <summary>One code per frame. Writing stores these codes exactly; it never re-quantizes.</summary>
    public uint[] EncodedValues { get; }
    public override int SampleCount => EncodedValues.Length;
    internal override int FrameBits => ValueBits;
    /// <summary>Copies <paramref name="encodedValues"/>; the caller chooses the grid.</summary>
    public QuantizedFloatChannel(int valueBits, float quantum, float offset, ReadOnlySpan<uint> encodedValues, int componentIndex = 0) : base(componentIndex)
    { ValueBits = valueBits; Quantum = quantum; Offset = offset; EncodedValues = encodedValues.ToArray(); }
    public override Vector4 Sample(int frame) { RequireFrame(frame, SampleCount); return new(Decode(EncodedValues[frame], Quantum, Offset), 0, 0, 0); }
    internal override Vector4 Interpolate(int first, int second, float amount) =>
        new(Decode(float.Lerp(EncodedValues[first], EncodedValues[second], amount), Quantum, Offset), 0, 0, 0);
}

/// <summary>Stores scalar samples as per-frame indices into a table of codes, each interpreted as code × Quantum + Offset.</summary>
public sealed class IndirectQuantizedFloatChannel : AnimationChannel
{
    public override AnimationChannelType Type => AnimationChannelType.IndirectQuantizedFloat;
    public int FrameIndexBits { get; set; }
    /// <summary>Stored bits per table code; zero means every frame decodes code zero.</summary>
    public int ValueBits { get; set; }
    public float Quantum { get; set; }
    public float Offset { get; set; }
    public uint[] EncodedValues { get; }
    public uint[] FrameIndices { get; }
    public override int SampleCount => FrameIndices.Length;
    internal override int FrameBits => FrameIndexBits;
    internal byte[]? SourcePackedValues { get; set; }
    internal uint[]? SourceValues { get; set; }
    internal int SourceValueBits { get; set; }
    /// <summary>Copies <paramref name="encodedValues"/> and <paramref name="frameIndices"/>.</summary>
    public IndirectQuantizedFloatChannel(int frameIndexBits, int valueBits, float quantum, float offset,
        ReadOnlySpan<uint> encodedValues, ReadOnlySpan<uint> frameIndices, int componentIndex = 0) : base(componentIndex)
    { FrameIndexBits = frameIndexBits; ValueBits = valueBits; Quantum = quantum; Offset = offset; EncodedValues = encodedValues.ToArray(); FrameIndices = frameIndices.ToArray(); }
    private uint Code(int frame)
    {
        RequireFrame(frame, SampleCount);
        // A zero-width code field decodes as code zero for every frame.
        if (ValueBits == 0)
            return 0;
        uint index = FrameIndices[frame];
        if (index >= EncodedValues.Length)
            throw new InvalidOperationException("Indirect frame index exceeds the value table.");
        return EncodedValues[index];
    }
    public override Vector4 Sample(int frame) => new(Decode(Code(frame), Quantum, Offset), 0, 0, 0);
    internal override Vector4 Interpolate(int first, int second, float amount) =>
        new(Decode(float.Lerp(Code(first), Code(second), amount), Quantum, Offset), 0, 0, 0);
}

/// <summary>Signed codes, each interpreted as code × Quantum + Offset, stored as segmented second differences:
/// value[t + 1] = value[t] + velocity[t], velocity[t] = velocity[t - 1] + delta[t]. Sample edits are re-encoded exactly with the
/// sequence format's <see cref="LinearChannelEncoding"/>; unedited channels keep their stored packing.</summary>
public sealed class LinearFloatChannel : AnimationChannel
{
    public override AnimationChannelType Type => AnimationChannelType.LinearFloat;
    public int[] EncodedValues { get; }
    public override int SampleCount => EncodedValues.Length;
    public float Quantum { get; set; }
    public float Offset { get; set; }
    /// <summary>Frames per segment for <see cref="LinearChannelEncoding.RiceCoded"/>; must be a power of two equal to the
    /// sequence's segment size. <see cref="LinearChannelEncoding.LaneInterleaved"/> always uses 64-frame segments.</summary>
    public byte SegmentSize { get; set; }
    /// <summary>Rice divisor (0..31) for <see cref="LinearChannelEncoding.RiceCoded"/>; unused by other encodings.</summary>
    public byte RiceDivisor { get; set; }
    internal byte[]? SourcePackedValues { get; set; }
    internal int[]? SourceValues { get; set; }
    internal byte SourceSegmentSize { get; set; }
    internal byte SourceRiceDivisor { get; set; }
    internal LinearChannelEncoding SourceEncoding { get; set; }
    /// <summary>Copies <paramref name="encodedValues"/>.</summary>
    public LinearFloatChannel(ReadOnlySpan<int> encodedValues, float quantum, float offset = 0,
        int componentIndex = 0, byte segmentSize = 64, byte riceDivisor = 0) : base(componentIndex)
    { EncodedValues = encodedValues.ToArray(); Quantum = quantum; Offset = offset; SegmentSize = segmentSize; RiceDivisor = riceDivisor; }
    public override Vector4 Sample(int frame) { RequireFrame(frame, SampleCount); return new(Decode(EncodedValues[frame], Quantum, Offset), 0, 0, 0); }
    internal override Vector4 Interpolate(int first, int second, float amount) =>
        new(Decode(float.Lerp(EncodedValues[first], EncodedValues[second], amount), Quantum, Offset), 0, 0, 0);
}

/// <summary>Quaternion operation applied after scalar interpolation: reconstruct one omitted component, or normalize all four.</summary>
public sealed class QuaternionOperationChannel : AnimationChannel
{
    public override AnimationChannelType Type { get; }
    public override int ComponentCount => 0;
    /// <summary>For reconstruction, <paramref name="componentIndex"/> is the omitted component.</summary>
    public QuaternionOperationChannel(AnimationChannelType type = AnimationChannelType.ReconstructQuaternion, int componentIndex = 3) : base(componentIndex)
    {
        if (type is not AnimationChannelType.ReconstructQuaternion and not AnimationChannelType.NormalizeQuaternion)
            throw new ArgumentOutOfRangeException(nameof(type));
        Type = type;
    }
    /// <summary>Always throws <see cref="InvalidOperationException"/>: sample the owning track instead.</summary>
    public override Vector4 Sample(int frame) => throw new InvalidOperationException("Quaternion operations require their sequence track.");
}
