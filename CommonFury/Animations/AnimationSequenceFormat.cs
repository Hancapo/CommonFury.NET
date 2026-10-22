namespace CommonFury.Animations;

/// <summary>Bit-stream encoding used for <see cref="LinearFloatChannel"/> data.</summary>
public enum LinearChannelEncoding
{
    /// <summary>Segments of the sequence's power-of-two segment size; per-segment address and initial-code tables followed by
    /// Rice-coded sign-magnitude deltas with a per-channel divisor (GTA V).</summary>
    RiceCoded,
    /// <summary>Fixed 64-frame segments; groups of four zigzag deltas share a unary-coded width and are stored in four
    /// word-interleaved lanes (RDR2).</summary>
    LaneInterleaved
}

/// <summary>Selects uncompressed or compact storage for an animation sequence.</summary>
public enum AnimationSequenceStorage
{
    Uncompressed,
    /// <summary>The payload is compressed with the format's compact codec.</summary>
    Compact
}

/// <summary>How the 32-bit block hash in the sequence header is treated.</summary>
public enum AnimationSequenceHashPolicy
{
    /// <summary>The hash is an opaque value: read into <see cref="AnimationSequence.Hash"/> and written back unchanged.</summary>
    Preserve,
    /// <summary>The hash is the sign-extended one-at-a-time hash of the expanded payload, also for compact blocks: reading
    /// reports a mismatch as a rewrite warning and writing derives it.</summary>
    Derive
}

/// <summary>Maps stored channel-group indices to logical channel types. Groups appear in the block in index order;
/// unused indices must have zero channels.</summary>
public sealed class AnimationChannelGroupTable
{
    private readonly AnimationChannelType?[] types;
    private readonly int[] groups;

    /// <summary>Creates a table whose entry <c>i</c> is the type stored in group <c>i</c>, or null for an unused group.
    /// Each type may appear at most once.</summary>
    public AnimationChannelGroupTable(ReadOnlySpan<AnimationChannelType?> groupTypes)
    {
        if (groupTypes.IsEmpty || groupTypes.Length > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(groupTypes));
        types = groupTypes.ToArray();
        groups = new int[Enum.GetValues<AnimationChannelType>().Length];
        Array.Fill(groups, -1);
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i] is not { } type)
                continue;
            if (!Enum.IsDefined(type))
                throw new ArgumentOutOfRangeException(nameof(groupTypes));
            if (groups[(int)type] >= 0)
                throw new ArgumentException($"Channel type {type} appears in more than one group.", nameof(groupTypes));
            groups[(int)type] = i;
        }
    }

    /// <summary>Number of stored groups, including unused ones.</summary>
    public int GroupCount => types.Length;

    /// <summary>Returns the type stored in a group, or null for an unused or out-of-range group.</summary>
    public AnimationChannelType? TypeOf(int group) => (uint)group < (uint)types.Length ? types[group] : null;

    /// <summary>Returns the stored group of a type, or -1 when the format has no group for it.</summary>
    public int GroupOf(AnimationChannelType type) => (uint)type < (uint)groups.Length ? groups[(int)type] : -1;
}

/// <summary>Game-specific encoding choices for an animation sequence block. The block layout (32-byte header, constant
/// section, fixed-stride frames, per-group descriptor tables and mover references) is shared; these options are not.</summary>
public sealed record AnimationSequenceFormat
{
    /// <summary>Default limit on a sequence's payload and decoded samples (256 MiB including the header).</summary>
    public const int DefaultMaximumPayloadBytes = 256 * 1024 * 1024 - 0x20;

    public AnimationSequenceFormat(AnimationChannelGroupTable groups) => Groups = groups ?? throw new ArgumentNullException(nameof(groups));

    public AnimationChannelGroupTable Groups { get; init; }
    public LinearChannelEncoding LinearEncoding { get; init; }
    public AnimationSequenceHashPolicy HashPolicy { get; init; }
    /// <summary>Expands a compact payload to the given length; null rejects compact blocks with <see cref="NotSupportedException"/>.</summary>
    public Func<ReadOnlyMemory<byte>, int, byte[]>? DecompressCompact { get; init; }
    /// <summary>Compresses an uncompressed payload for compact storage; null rejects writing compact blocks.</summary>
    public Func<ReadOnlyMemory<byte>, byte[]>? CompressCompact { get; init; }
    /// <summary>Largest accepted payload size after the 32-byte header, compact or expanded.</summary>
    public int MaximumPayloadBytes { get; init; } = DefaultMaximumPayloadBytes;
    /// <summary>Whether reading binds mover references to channels of any track (first match in stored order) and rejects
    /// unresolved references. When false, references are retained until <see cref="AnimationSequence.BindRootReferences"/>.</summary>
    public bool BindRootReferencesOnRead { get; init; }
}
