using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Animations;

/// <summary>One degree of freedom, with explicit destination component indices on its channels.</summary>
public sealed class SequenceTrack
{
    /// <summary>Owned channel list; each channel writes its own component range, plus at most one quaternion operation.</summary>
    public List<AnimationChannel> Channels { get; } = [];

    internal Vector4 Sample(int first, int second, float amount, AnimationTrackFormat format)
    {
        Span<float> components = stackalloc float[4];
        components.Clear();
        int present = 0;
        QuaternionOperationChannel? operation = null;
        foreach (AnimationChannel channel in Channels)
        {
            if (channel is QuaternionOperationChannel op)
            {
                if (operation is not null)
                    throw new InvalidOperationException("A track contains multiple quaternion operations.");
                operation = op;
                continue;
            }
            int index = channel.ComponentIndex;
            int width = channel.ComponentCount;
            if (index < 0 || index + width > 4)
                throw new InvalidOperationException("Channel components exceed their track.");
            int mask = ((1 << width) - 1) << index;
            if ((present & mask) != 0)
                throw new InvalidOperationException("Track channels overlap destination components.");
            Vector4 value = channel.Interpolate(first, second, amount);
            for (int i = 0; i < width; i++)
                components[index + i] = value[i];
            present |= mask;
        }
        int required = format switch
        {
            AnimationTrackFormat.Float => 1,
            AnimationTrackFormat.Vector3 => 7,
            // A reconstruction stores its three present components packed in slots 0..2.
            AnimationTrackFormat.Quaternion => operation?.Type == AnimationChannelType.ReconstructQuaternion ? 7 : 15,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        if (present != required || operation is not null && format != AnimationTrackFormat.Quaternion)
            throw new InvalidOperationException("Track components do not match the requested DOF format.");
        Vector4 result = new(components[0], components[1], components[2], components[3]);
        if (operation is null)
            return result;
        Quaternion q = operation.Type == AnimationChannelType.ReconstructQuaternion
            ? QuaternionMath.Reconstruct(new(result.X, result.Y, result.Z), operation.ComponentIndex)
            : QuaternionMath.NormalizeChecked(new(result.X, result.Y, result.Z, result.W));
        return new(q.X, q.Y, q.Z, q.W);
    }
}

/// <summary>Owns the channels for a block of animation frames. Writing preserves integer encodings without choosing a lossy
/// channel policy; an unedited block read from a file is rewritten byte for byte.</summary>
public sealed class AnimationSequence
{
    /// <summary>Opaque header hash; derived on write instead when the format uses <see cref="AnimationSequenceHashPolicy.Derive"/>.</summary>
    public uint Hash { get; set; }
    /// <summary>Frames stored in this block, including its terminating overlap sample.</summary>
    public int FrameCount { get; }
    public AnimationSequenceStorage Storage { get; set; }
    /// <summary>Additional bytes required by the original in-place decompressor; retained for unchanged compact blocks.</summary>
    public ushort SlopSize { get; set; }
    /// <summary>Header byte at offset 30. <see cref="LinearChannelEncoding.RiceCoded"/> linear channels use it as their
    /// power-of-two segment size; <see cref="LinearChannelEncoding.LaneInterleaved"/> retains it without interpreting it.</summary>
    public byte SegmentSize { get; set; } = byte.MaxValue;
    /// <summary>One entry per animation track, in the owning animation's track order.</summary>
    public List<SequenceTrack> Tracks { get; } = [];
    /// <summary>Channels referenced by the block's mover-translation references, in stored order.</summary>
    public List<AnimationChannel> RootTranslationChannels { get; } = [];
    /// <summary>Channels referenced by the block's mover-rotation references, in stored order.</summary>
    public List<AnimationChannel> RootRotationChannels { get; } = [];
    private readonly List<Diagnostic> sourceIssues = [];
    /// <summary>Warnings about source data that a rewrite cannot reproduce or would normalize.</summary>
    public IReadOnlyList<Diagnostic> SourceDiagnostics => sourceIssues.AsReadOnly();
    internal List<RootChannelReference> SourceTranslationReferences { get; } = [];
    internal List<RootChannelReference> SourceRotationReferences { get; } = [];
    internal List<AnimationChannel> SourceChannelOrder { get; } = [];
    internal bool RootReferencesBound { get; set; }
    internal bool HasUnmodeledData { get; set; }
    internal byte[]? OriginalBytes { get; set; }
    internal byte[]? OriginalCanonicalBytes { get; set; }
    internal AnimationSequenceStorage OriginalStorage { get; set; }
    internal bool OriginalHashDerived { get; set; }
    internal uint? OriginalPayloadHash { get; set; }
    /// <summary>An owned read-only view of the original sequence block; it remains valid for this object's lifetime.</summary>
    public ReadOnlyMemory<byte> SourceEncoding => OriginalBytes ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>Creates an animation sequence with a frame count from 0 to 65535; export requires at least one frame.</summary>
    public AnimationSequence(int frameCount)
    {
        if (frameCount is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        FrameCount = frameCount;
    }

    /// <summary>Decodes one stored block for an animation with <paramref name="trackCount"/> tracks. The result owns its data.</summary>
    public static AnimationSequence Read(ReadOnlySpan<byte> data, int trackCount, AnimationSequenceFormat format) =>
        AnimationSequenceCodec.Read(data, trackCount, format);

    /// <summary>Throws <see cref="ValidationException"/> when the sequence is invalid for <paramref name="format"/>.</summary>
    public byte[] Write(AnimationSequenceFormat format) => AnimationSequenceCodec.Write(this, format);

    /// <summary>Samples a local frame in [0, FrameCount-1]. Scalar channels interpolate before quaternion stream operations.</summary>
    public Vector4 Sample(int track, float frame, AnimationTrackFormat format)
    {
        if ((uint)track >= (uint)Tracks.Count)
            throw new ArgumentOutOfRangeException(nameof(track));
        if (!float.IsFinite(frame) || frame < 0 || frame > FrameCount - 1)
            throw new ArgumentOutOfRangeException(nameof(frame));
        int first = (int)MathF.Floor(frame);
        return Tracks[track].Sample(first, Math.Min(first + 1, FrameCount - 1), frame - first, format);
    }

    /// <summary>Explicitly replaces mover references with every channel of the given track indices, without changing channel encodings.</summary>
    public void RecalculateRootMotionChannels(IReadOnlyCollection<int> translationTracks, IReadOnlyCollection<int> rotationTracks)
    {
        ArgumentNullException.ThrowIfNull(translationTracks);
        ArgumentNullException.ThrowIfNull(rotationTracks);
        var translation = new List<AnimationChannel>();
        var rotation = new List<AnimationChannel>();
        for (int i = 0; i < Tracks.Count; i++)
        {
            if (translationTracks.Contains(i))
                translation.AddRange(Tracks[i].Channels);
            if (rotationTracks.Contains(i))
                rotation.AddRange(Tracks[i].Channels);
        }
        if (translation.Count > 15 || rotation.Count > 15)
            throw new InvalidOperationException("Mover channels exceed packed reference counts.");
        RootTranslationChannels.Clear();
        RootTranslationChannels.AddRange(translation);
        RootRotationChannels.Clear();
        RootRotationChannels.AddRange(rotation);
        RootReferencesBound = true;
    }

    /// <summary>Binds the stored mover references to channels of the given translation and rotation track indices. A reference
    /// matching several channels is rejected; an unresolved reference is reported in <see cref="SourceDiagnostics"/>.
    /// Returns whether every reference resolved.</summary>
    public bool BindRootReferences(IReadOnlyCollection<int> translationTracks, IReadOnlyCollection<int> rotationTracks)
    {
        ArgumentNullException.ThrowIfNull(translationTracks);
        ArgumentNullException.ThrowIfNull(rotationTracks);
        Bind(SourceTranslationReferences, translationTracks, RootTranslationChannels);
        Bind(SourceRotationReferences, rotationTracks, RootRotationChannels);
        RootReferencesBound = RootTranslationChannels.Count == SourceTranslationReferences.Count && RootRotationChannels.Count == SourceRotationReferences.Count;
        return RootReferencesBound;

        void Bind(List<RootChannelReference> references, IReadOnlyCollection<int> tracks, List<AnimationChannel> destination)
        {
            destination.Clear();
            foreach (RootChannelReference reference in references)
            {
                AnimationChannel? match = null;
                for (int i = 0; i < Tracks.Count; i++)
                    if (tracks.Contains(i))
                        foreach (AnimationChannel channel in Tracks[i].Channels)
                            if (reference.Matches(channel))
                            {
                                if (match is not null)
                                    throw new InvalidDataException("Mover channel reference is ambiguous within its track descriptors.");
                                match = channel;
                            }
                if (match is null)
                    AddDiagnostic(AnimationDiagnosticCodes.SequenceRootReference, "Mover reference does not resolve to its declared mover track.", "RootMotionChannels");
                else
                    destination.Add(match);
            }
        }
    }

    // Unrestricted binding: the first channel in stored order that matches each reference.
    internal void BindRootReferencesToAnyTrack()
    {
        Bind(SourceTranslationReferences, RootTranslationChannels);
        Bind(SourceRotationReferences, RootRotationChannels);
        RootReferencesBound = true;

        void Bind(List<RootChannelReference> references, List<AnimationChannel> destination)
        {
            destination.Clear();
            foreach (RootChannelReference reference in references)
                destination.Add(SourceChannelOrder.FirstOrDefault(reference.Matches)
                    ?? throw new InvalidDataException("Mover reference does not resolve to a stored channel."));
        }
    }

    internal void AddDiagnostic(string code, string message, string path)
    {
        HasUnmodeledData = true;
        sourceIssues.Add(new(code, message, path, DiagnosticSeverity.Warning));
    }

    internal void AddRewriteNotice(string code, string message, string path) =>
        sourceIssues.Add(new(code, message, path, DiagnosticSeverity.Warning) { Scope = DiagnosticScope.Rewrite });

    /// <summary>Checks sequence frame counts, channel storage and packed limits without changing samples or quantization.
    /// With a format, also checks that every channel type has a stored group and that linear channels fit its encoding.</summary>
    public ValidationReport Validate(AnimationSequenceFormat? format = null)
    {
        var issues = new List<Diagnostic>(sourceIssues);
        void Error(string code, string message, string path) => issues.Add(new(code, message, path));
        if (FrameCount == 0)
            Error(AnimationDiagnosticCodes.SequenceFrames, "A sequence must contain at least one frame for export.", "FrameCount");
        if (Tracks.Count > 16383)
            Error(AnimationDiagnosticCodes.SequenceTracks, "Track count exceeds the packed descriptor limit including its padding sentinel.", "Tracks");
        if (!Enum.IsDefined(Storage))
            Error(AnimationDiagnosticCodes.SequenceStorage, "Sequence storage mode is unsupported.", "Storage");
        var seen = new HashSet<AnimationChannel>(ReferenceEqualityComparer.Instance);
        int[] counts = new int[Enum.GetValues<AnimationChannelType>().Length];
        for (int i = 0; i < Tracks.Count; i++)
        {
            SequenceTrack? track = Tracks[i];
            string path = $"Tracks[{i}]";
            if (track is null)
            { Error(AnimationDiagnosticCodes.SequenceNullTrack, "Track is null.", path); continue; }
            if (track.Channels.Count == 0)
                Error(AnimationDiagnosticCodes.SequenceEmptyTrack, "Track has no stored components.", path);
            int occupied = 0, operations = 0;
            foreach (AnimationChannel? channel in track.Channels)
            {
                if (channel is null)
                { Error(AnimationDiagnosticCodes.SequenceNullChannel, "Channel is null.", path); continue; }
                if (!Enum.IsDefined(channel.Type) || format is not null && format.Groups.GroupOf(channel.Type) < 0)
                { Error(AnimationDiagnosticCodes.SequenceChannelType, "Channel type has no supported stream encoding.", path); continue; }
                if (!seen.Add(channel))
                    Error(AnimationDiagnosticCodes.SequenceChannelIdentity, "The same mutable channel belongs to multiple channel slots.", path);
                counts[(int)channel.Type]++;
                int width = channel.ComponentCount, component = channel.ComponentIndex;
                if (component is < 0 or > 3 || component + width > 4 || width > 1 && component != 0)
                    Error(AnimationDiagnosticCodes.SequenceComponent, "Channel destination components exceed the supported track layout.", path);
                else if (width != 0)
                {
                    int mask = ((1 << width) - 1) << component;
                    if ((mask & occupied) != 0)
                        Error(AnimationDiagnosticCodes.SequenceComponentOverlap, "Channels overlap destination components.", path);
                    occupied |= mask;
                }
                if (channel is QuaternionOperationChannel operation)
                {
                    operations++;
                    int required = operation.Type == AnimationChannelType.ReconstructQuaternion ? 7 : 15;
                    int actual = track.Channels.Where(c => c is not null && c.ComponentCount > 0 && c.ComponentIndex >= 0 && c.ComponentIndex + c.ComponentCount <= 4)
                        .Aggregate(0, (mask, c) => mask | (((1 << c.ComponentCount) - 1) << c.ComponentIndex));
                    if (actual != required)
                        Error(AnimationDiagnosticCodes.SequenceQuaternionComponents, "Quaternion operation lacks its complete stored component layout.", path);
                    continue;
                }
                bool dynamic = channel is RawFloatChannel or QuantizedFloatChannel or IndirectQuantizedFloatChannel or LinearFloatChannel;
                if (dynamic && channel.SampleCount != FrameCount)
                    Error(AnimationDiagnosticCodes.SequenceSampleCount, "Varying channel sample count differs from the sequence frame count.", path);
                switch (channel)
                {
                    case RawFloatChannel raw when !AllFinite(raw.Values):
                        Error(AnimationDiagnosticCodes.SequenceNonfinite, "Raw samples must be finite for evaluation.", path);
                        break;
                    case StaticFloatChannel scalar when !float.IsFinite(scalar.Value):
                        Error(AnimationDiagnosticCodes.SequenceNonfinite, "Static sample is not finite.", path);
                        break;
                    case StaticVector3Channel vector when !VectorMath.IsFinite(vector.Value):
                        Error(AnimationDiagnosticCodes.SequenceNonfinite, "Static vector is not finite.", path);
                        break;
                    case StaticQuaternionChannel quaternion when !VectorMath.IsFinite(quaternion.Components):
                        Error(AnimationDiagnosticCodes.SequenceNonfinite, "Static quaternion components are not finite.", path);
                        break;
                    case QuantizedFloatChannel quantized:
                        CheckGrid(quantized.Quantum, quantized.Offset, path);
                        // Players read at most 31 bits of a quantized code; a 32-bit code reads as zero and shifts every later channel.
                        if (quantized.ValueBits > 31)
                            Error(AnimationDiagnosticCodes.SequenceValueWidth, "Quantized channels store at most 31 bits per code.", path);
                        else
                            CheckCodes(quantized.ValueBits, quantized.EncodedValues, path);
                        break;
                    case IndirectQuantizedFloatChannel indirect:
                        CheckGrid(indirect.Quantum, indirect.Offset, path);
                        CheckCodes(indirect.ValueBits, indirect.EncodedValues, path);
                        CheckCodes(indirect.FrameIndexBits, indirect.FrameIndices, path);
                        if (indirect.ValueBits > 0 && indirect.FrameIndices.AsSpan().ContainsAnyInRange((uint)indirect.EncodedValues.Length, uint.MaxValue))
                            Error(AnimationDiagnosticCodes.SequenceIndirectIndex, "Indirect frame indices must address the explicitly stored value table.", path);
                        break;
                    case LinearFloatChannel linear:
                        CheckGrid(linear.Quantum, linear.Offset, path);
                        if (format?.LinearEncoding == LinearChannelEncoding.RiceCoded
                            && (!BitOperations.IsPow2(linear.SegmentSize) || linear.SegmentSize != SegmentSize || linear.RiceDivisor > 31))
                            Error(AnimationDiagnosticCodes.SequenceLinearLayout, "Linear segment size must be a power of two shared with the sequence; Rice divisor must be at most 31.", path);
                        if (format?.LinearEncoding == LinearChannelEncoding.LaneInterleaved)
                            for (int f = 0; f < linear.EncodedValues.Length; f += 64)
                                if (linear.EncodedValues[f] < 0)
                                {
                                    Error(AnimationDiagnosticCodes.SequenceLinearLayout, "Linear segment initial codes (every 64th frame) must be nonnegative.", path);
                                    break;
                                }
                        break;
                }
            }
            if (operations > 1)
                Error(AnimationDiagnosticCodes.SequenceQuaternionOperations, "A track has more than one quaternion operation.", path);
        }
        if (counts.Any(c => c > ushort.MaxValue))
            Error(AnimationDiagnosticCodes.SequenceChannelCount, "Channel group count exceeds UInt16 storage.", "Tracks");
        foreach (var entry in new[] { (RootTranslationChannels, "RootTranslationChannels"), (RootRotationChannels, "RootRotationChannels") })
        {
            if (entry.Item1.Count > 15)
                Error(AnimationDiagnosticCodes.SequenceRootCount, "Mover reference count exceeds four bits.", entry.Item2);
            if (entry.Item1.Any(c => c is null || !seen.Contains(c)) || entry.Item1.Distinct(ReferenceEqualityComparer.Instance).Count() != entry.Item1.Count)
                Error(AnimationDiagnosticCodes.SequenceRootChannel, "Mover references must identify distinct channels in this sequence.", entry.Item2);
        }
        return new(issues);
        void CheckGrid(float quantum, float offset, string path)
        { if (!float.IsFinite(quantum) || !float.IsFinite(offset)) Error(AnimationDiagnosticCodes.SequenceQuantization, "Quantization parameters must be finite.", path); }
        void CheckCodes(int bits, uint[] values, string path)
        { if (bits is < 0 or > 32 || bits < 32 && values.AsSpan().ContainsAnyInRange(1u << bits, uint.MaxValue)) Error(AnimationDiagnosticCodes.SequenceValueWidth, "Encoded value exceeds its requested bit width.", path); }
        static bool AllFinite(ReadOnlySpan<float> values)
        {
            foreach (float value in values)
                if (!float.IsFinite(value))
                    return false;
            return true;
        }
    }
}

internal readonly record struct RootChannelReference(AnimationChannelType Type, byte Component, ushort WordOffset, ushort BitOffset)
{
    public bool Matches(AnimationChannel channel) => channel.Type == Type && channel.ComponentIndex == Component
        && channel.SourceWordOffset == WordOffset && channel.SourceBitOffset == BitOffset;
}
