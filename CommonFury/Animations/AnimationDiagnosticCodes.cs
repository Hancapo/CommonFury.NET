namespace CommonFury.Animations;

/// <summary>Diagnostic codes reported by animation sequence reading and validation; values never change.</summary>
public static class AnimationDiagnosticCodes
{
    /// <summary>Nonzero bytes follow the decoded constant channels.</summary>
    public const string SequenceConstantPadding = "animation.sequence.constant_padding";
    /// <summary>Descriptor padding differs from the track-count sentinel.</summary>
    public const string SequenceDescriptorPadding = "animation.sequence.descriptor_padding";
    /// <summary>Unused frame bits are nonzero.</summary>
    public const string SequenceFramePadding = "animation.sequence.frame_padding";
    /// <summary>Nonzero bytes precede the mover references.</summary>
    public const string SequenceReferencePadding = "animation.sequence.reference_padding";
    /// <summary>A mover reference does not resolve to a channel of its declared tracks.</summary>
    public const string SequenceRootReference = "animation.sequence.root_reference";
    /// <summary>Nonzero bytes follow the mover references.</summary>
    public const string SequenceTrailingData = "animation.sequence.trailing_data";
    /// <summary>The stored block hash differs from the derived payload hash.</summary>
    public const string SequenceHash = "animation.sequence.hash";
    /// <summary>A sequence has no frames.</summary>
    public const string SequenceFrames = "animation.sequence.frames";
    /// <summary>The track count exceeds the packed descriptor limit.</summary>
    public const string SequenceTracks = "animation.sequence.tracks";
    /// <summary>The storage mode is not a defined value or has no codec in the format.</summary>
    public const string SequenceStorage = "animation.sequence.storage";
    public const string SequenceNullTrack = "animation.sequence.null_track";
    public const string SequenceEmptyTrack = "animation.sequence.empty_track";
    public const string SequenceNullChannel = "animation.sequence.null_channel";
    /// <summary>A channel type is undefined or has no stored group in the format.</summary>
    public const string SequenceChannelType = "animation.sequence.channel_type";
    /// <summary>One channel object occupies several channel slots.</summary>
    public const string SequenceChannelIdentity = "animation.sequence.channel_identity";
    /// <summary>Channel components exceed the track layout.</summary>
    public const string SequenceComponent = "animation.sequence.component";
    /// <summary>Channels overlap destination components.</summary>
    public const string SequenceComponentOverlap = "animation.sequence.component_overlap";
    /// <summary>A quaternion operation lacks its complete stored component layout.</summary>
    public const string SequenceQuaternionComponents = "animation.sequence.quaternion_components";
    /// <summary>A track has more than one quaternion operation.</summary>
    public const string SequenceQuaternionOperations = "animation.sequence.quaternion_operations";
    /// <summary>A varying channel's sample count differs from the frame count.</summary>
    public const string SequenceSampleCount = "animation.sequence.sample_count";
    public const string SequenceNonfinite = "animation.sequence.nonfinite";
    /// <summary>Quantization parameters are not finite.</summary>
    public const string SequenceQuantization = "animation.sequence.quantization";
    /// <summary>An encoded value exceeds its bit width.</summary>
    public const string SequenceValueWidth = "animation.sequence.value_width";
    /// <summary>Indirect frame indices address beyond the value table.</summary>
    public const string SequenceIndirectIndex = "animation.sequence.indirect_index";
    /// <summary>Linear channel settings do not fit the format's linear encoding.</summary>
    public const string SequenceLinearLayout = "animation.sequence.linear_layout";
    /// <summary>A channel group count exceeds UInt16 storage.</summary>
    public const string SequenceChannelCount = "animation.sequence.channel_count";
    /// <summary>Mover references exceed their four-bit count.</summary>
    public const string SequenceRootCount = "animation.sequence.root_count";
    /// <summary>Mover references must identify distinct channels of the sequence.</summary>
    public const string SequenceRootChannel = "animation.sequence.root_channel";
}
