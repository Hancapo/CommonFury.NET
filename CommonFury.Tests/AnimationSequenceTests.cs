using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using CommonFury.Animations;
using CommonFury.Compression;
using Xunit;

namespace CommonFury.Tests;

public sealed class AnimationSequenceTests
{
    // Test stand-ins for the game libraries' formats. The real compact codec is game-specific; raw DEFLATE exercises
    // the injected-codec plumbing.
    private static readonly AnimationSequenceFormat Gta = new(new([
        AnimationChannelType.StaticQuaternion, AnimationChannelType.StaticVector3, AnimationChannelType.StaticFloat,
        AnimationChannelType.RawFloat, AnimationChannelType.QuantizedFloat, AnimationChannelType.IndirectQuantizedFloat,
        AnimationChannelType.LinearFloat, AnimationChannelType.ReconstructQuaternion, AnimationChannelType.NormalizeQuaternion]))
    {
        LinearEncoding = LinearChannelEncoding.RiceCoded,
        DecompressCompact = (data, length) => RawDeflate.DecompressExact(data.Span, length),
        CompressCompact = data => RawDeflate.Compress(data.Span, CompressionLevel.Optimal),
    };

    private static readonly AnimationSequenceFormat Rdr2 = new(new([
        AnimationChannelType.StaticQuaternion, AnimationChannelType.StaticVector3, AnimationChannelType.StaticFloat,
        AnimationChannelType.RawFloat, AnimationChannelType.QuantizedFloat, AnimationChannelType.IndirectQuantizedFloat,
        null, AnimationChannelType.ReconstructQuaternion, AnimationChannelType.NormalizeQuaternion, AnimationChannelType.LinearFloat]))
    {
        LinearEncoding = LinearChannelEncoding.LaneInterleaved,
        HashPolicy = AnimationSequenceHashPolicy.Derive,
        MaximumPayloadBytes = 0x0FFFFFF0,
        BindRootReferencesOnRead = true,
    };

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "ReferenceData", "animations", "all_channels.seq");

    [Fact]
    public void ReferenceChannelsPreserveEncodingsAndEvaluateFractionalFrames()
    {
        byte[] bytes = File.ReadAllBytes(Fixture);
        AnimationSequence sequence = AnimationSequence.Read(bytes, 9, Gta);
        Array.Clear(bytes);
        Assert.True(sequence.Validate(Gta).IsValid, string.Join("; ", sequence.Validate(Gta).Diagnostics));
        Assert.Equal(65, sequence.FrameCount);
        NumericAssert.Near(sequence.Sample(3, 0.5f, AnimationTrackFormat.Float).X, 0.5, "raw subframe");
        NumericAssert.Near(sequence.Sample(4, 2.5f, AnimationTrackFormat.Float).X, 2.5, "quantized subframe");
        NumericAssert.Near(sequence.Sample(5, 1.5f, AnimationTrackFormat.Float).X, 1.5, "indirect subframe");
        NumericAssert.Near(sequence.Sample(6, 63.5f, AnimationTrackFormat.Float).X, 4032.5, "linear segment boundary");
        Vector4 reconstructed = sequence.Sample(7, 0.5f, AnimationTrackFormat.Quaternion);
        NumericAssert.Near(reconstructed.X, 0.05, "stored component interpolation");
        NumericAssert.Near(reconstructed.W, Math.Sqrt(1 - 0.05 * 0.05), "reconstruction after interpolation");
        Vector4 normalized = sequence.Sample(8, 0.5f, AnimationTrackFormat.Quaternion);
        NumericAssert.Near(normalized.Length(), 1, "normalization opcode");
        byte[] rewritten = sequence.Write(Gta);
        AnimationSequence reread = AnimationSequence.Read(rewritten, 9, Gta);
        Assert.Equal(sequence.Tracks.SelectMany(t => t.Channels).Select(c => c.Type), reread.Tracks.SelectMany(t => t.Channels).Select(c => c.Type));
        Assert.Equal(File.ReadAllBytes(Fixture), rewritten);
    }

    [Fact]
    public void EditedGtaSequenceReencodesEveryChannelKindIdentically()
    {
        AnimationSequence sequence = AnimationSequence.Read(File.ReadAllBytes(Fixture), 9, Gta);
        // Changing the opaque hash forces the canonical encoder instead of the retained source bytes.
        sequence.Hash ^= 1;
        byte[] rewritten = sequence.Write(Gta);
        byte[] expected = File.ReadAllBytes(Fixture);
        BinaryPrimitives.WriteUInt32LittleEndian(expected, sequence.Hash);
        Assert.Equal(expected, rewritten);
    }

    [Fact]
    public void MalformedFramesAndRiceStreamsFailWithinTheirOwnAllocations()
    {
        byte[] source = File.ReadAllBytes(Fixture);
        byte[] truncatedFrames = source.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(truncatedFrames.AsSpan(0x18), 0);
        Assert.Throws<InvalidDataException>(() => AnimationSequence.Read(truncatedFrames, 9, Gta));
        Assert.Throws<InvalidDataException>(() => AnimationSequence.Read(source.AsSpan(0, source.Length - 1), 9, Gta));
        byte[] badIndex = source.ToArray();
        int constantBytes = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(0x0C)));
        int frameBytes = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(0x18));
        int counts = 0x20 + constantBytes + frameBytes * 65;
        int firstDescriptor = counts + 18;
        BinaryPrimitives.WriteUInt16LittleEndian(badIndex.AsSpan(firstDescriptor), 9 << 2);
        Assert.Throws<InvalidDataException>(() => AnimationSequence.Read(badIndex, 9, Gta));

        var linearSequence = new AnimationSequence(2) { SegmentSize = 2 };
        var linearTrack = new SequenceTrack();
        linearTrack.Channels.Add(new LinearFloatChannel([0, 0], 1, segmentSize: 2));
        linearSequence.Tracks.Add(linearTrack);
        byte[] missingRiceTerminator = linearSequence.Write(Gta);
        // One zero-width initial value and one Rice zero delta: clearing its terminal bit leaves no bounded unary terminator.
        missingRiceTerminator.AsSpan(0x20 + 16, 4).Clear();
        Assert.Throws<InvalidDataException>(() => AnimationSequence.Read(missingRiceTerminator, 1, Gta));
    }

    [Fact]
    public void AuthoredChannelsOwnSamplesAndRequireCompleteComponentLayouts()
    {
        float[] input = [0, 4];
        var channel = new RawFloatChannel(input);
        input[1] = 99;
        var sequence = new AnimationSequence(2);
        var track = new SequenceTrack();
        track.Channels.Add(channel);
        sequence.Tracks.Add(track);
        NumericAssert.Near(sequence.Sample(0, 0.5f, AnimationTrackFormat.Float).X, 2, "owned channel input");
        Assert.Throws<InvalidOperationException>(() => sequence.Sample(0, 0.5f, AnimationTrackFormat.Vector3));
        AnimationSequence decoded = AnimationSequence.Read(sequence.Write(Gta), 1, Gta);
        NumericAssert.Near(decoded.Sample(0, 1, AnimationTrackFormat.Float).X, 4, "authored endpoint");
        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.Sample(0, 2, AnimationTrackFormat.Float));
        sequence.Storage = AnimationSequenceStorage.Compact;
        byte[] compact = sequence.Write(Gta);
        AnimationSequence expanded = AnimationSequence.Read(compact, 1, Gta);
        Assert.Equal(AnimationSequenceStorage.Compact, expanded.Storage);
        Assert.Equal(compact, expanded.Write(Gta));
        Assert.Throws<NotSupportedException>(() => AnimationSequence.Read(compact, 1, Rdr2));
        Assert.Throws<NotSupportedException>(() => expanded.Write(Rdr2));
        Assert.IsType<RawFloatChannel>(expanded.Tracks[0].Channels[0]).Values[1] = 8;
        AnimationSequence edited = AnimationSequence.Read(expanded.Write(Gta), 1, Gta);
        NumericAssert.Near(edited.Sample(0, 0.5f, AnimationTrackFormat.Float).X, 4, "edited compact payload");
        var zeroWidthTable = new AnimationSequence(2);
        var constantTrack = new SequenceTrack();
        constantTrack.Channels.Add(new IndirectQuantizedFloatChannel(3, 0, 1, 7, [], [3, 7]));
        zeroWidthTable.Tracks.Add(constantTrack);
        NumericAssert.Near(AnimationSequence.Read(zeroWidthTable.Write(Gta), 1, Gta).Sample(0, 0.5f, AnimationTrackFormat.Float).X, 7,
            "declared zero-width indirect codes");
    }

    private static int[] Codes(int frames) =>
        Enumerable.Range(0, frames).Select(i => (int)(5000 + 3000 * MathF.Sin(i * .21f) + (i % 7 == 0 ? 40000 : 0) + i * i)).ToArray();

    [Fact]
    public void LaneLinearChannelsRoundTripAcrossSegmentsAndReencodeEdits()
    {
        int[] codes = Codes(150);
        var sequence = new AnimationSequence(codes.Length) { SegmentSize = 64 };
        sequence.Tracks.Add(new SequenceTrack { Channels = { new LinearFloatChannel(codes, .001f, -2) } });
        byte[] written = sequence.Write(Rdr2);
        var read = AnimationSequence.Read(written, 1, Rdr2);
        var channel = Assert.IsType<LinearFloatChannel>(read.Tracks[0].Channels[0]);
        Assert.Equal(codes, channel.EncodedValues);
        Assert.Equal(.001f, channel.Quantum);
        Assert.Equal(-2f, channel.Offset);
        Assert.Empty(read.SourceDiagnostics);
        Assert.Equal(written, read.Write(Rdr2));
        channel.EncodedValues[100] = 7;
        channel.EncodedValues[64] = 123456;
        var edited = AnimationSequence.Read(read.Write(Rdr2), 1, Rdr2);
        Assert.Equal(channel.EncodedValues, Assert.IsType<LinearFloatChannel>(edited.Tracks[0].Channels[0]).EncodedValues);
        Assert.Equal(-2 + 7 * .001f, edited.Sample(0, 100, AnimationTrackFormat.Float).X, 4);
    }

    [Fact]
    public void NegativeLaneSegmentInitialCodesAreReportedNotWritten()
    {
        int[] codes = Codes(70);
        codes[64] = -1;
        var sequence = new AnimationSequence(codes.Length);
        sequence.Tracks.Add(new SequenceTrack { Channels = { new LinearFloatChannel(codes, 1) } });
        Assert.Contains(sequence.Validate(Rdr2).Diagnostics, d => d.Code == AnimationDiagnosticCodes.SequenceLinearLayout);
        codes[64] = 5;
        codes[3] = -9;
        var valid = new AnimationSequence(codes.Length);
        valid.Tracks.Add(new SequenceTrack { Channels = { new LinearFloatChannel(codes, 1) } });
        Assert.Empty(valid.Validate(Rdr2).Diagnostics);
        Assert.Equal(codes, ((LinearFloatChannel)AnimationSequence.Read(valid.Write(Rdr2), 1, Rdr2).Tracks[0].Channels[0]).EncodedValues);
    }

    [Fact]
    public void DerivedHashesCoverTheExpandedPayloadOfCompactBlocks()
    {
        // Neither game combines derived hashes with compact storage; the shared definition hashes the expanded payload.
        AnimationSequenceFormat format = Rdr2 with
        {
            DecompressCompact = (data, length) => RawDeflate.DecompressExact(data.Span, length),
            CompressCompact = data => RawDeflate.Compress(data.Span, CompressionLevel.Optimal),
        };
        var sequence = new AnimationSequence(4) { SegmentSize = 64, Storage = AnimationSequenceStorage.Compact };
        sequence.Tracks.Add(new SequenceTrack { Channels = { new StaticFloatChannel(1, 0), new StaticFloatChannel(2, 1), new StaticFloatChannel(3, 2) } });
        byte[] written = sequence.Write(format);
        byte[] expanded = RawDeflate.DecompressExact(written.AsSpan(0x20), BinaryPrimitives.ReadInt32LittleEndian(written.AsSpan(4)));
        Assert.Equal(AnimationSequenceCodec.DataHash(expanded), BinaryPrimitives.ReadUInt32LittleEndian(written));
        // An unedited compact block whose hash matches is reproduced byte for byte.
        Assert.Equal(written, AnimationSequence.Read(written, 1, format).Write(format));
        // A mismatching stored hash is reported on read, as for uncompressed blocks.
        byte[] stale = (byte[])written.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(stale, 0x12345678);
        Assert.Contains(AnimationSequence.Read(stale, 1, format).Validate(format).Diagnostics, d => d.Code == AnimationDiagnosticCodes.SequenceHash);
    }

    [Fact]
    public void GroupTablesMapStoredGroupsAndDerivedHashesAreChecked()
    {
        int frames = 31;
        uint[] z = Enumerable.Range(0, frames).Select(i => (uint)(i * (65535 / (frames - 1)))).ToArray();
        var sequence = new AnimationSequence(frames) { SegmentSize = 64 };
        var rotation = new SequenceTrack
        {
            Channels = { new StaticFloatChannel(0, 0), new StaticFloatChannel(0, 1), new QuantizedFloatChannel(16, 2f / 65535, -1, z, 2),
                new QuaternionOperationChannel(AnimationChannelType.ReconstructQuaternion, 3) }
        };
        var linear = new LinearFloatChannel(Codes(frames), .0001f);
        sequence.Tracks.Add(rotation);
        sequence.Tracks.Add(new SequenceTrack { Channels = { linear, new StaticFloatChannel(0, 1), new StaticFloatChannel(.25f, 2) } });
        sequence.RootTranslationChannels.Add(linear);
        byte[] bytes = sequence.Write(Rdr2);
        // Ten groups: the linear count lives in group 9 and the mover reference names stored group 9.
        int constants = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        int frameBytes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(24));
        int countTable = 0x20 + constants + frameBytes * frames;
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(countTable + 9 * 2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(countTable + 6 * 2)));
        Assert.Equal(9, bytes[^6]);
        Assert.Equal(AnimationSequenceCodec.DataHash(bytes.AsSpan(0x20)), BinaryPrimitives.ReadUInt32LittleEndian(bytes));

        var read = AnimationSequence.Read(bytes, 2, Rdr2);
        Assert.Same(read.Tracks[1].Channels.OfType<LinearFloatChannel>().Single(), Assert.Single(read.RootTranslationChannels));
        for (int frame = 0; frame < frames; frame += 5)
        {
            Assert.Equal(sequence.Sample(0, frame, AnimationTrackFormat.Quaternion), read.Sample(0, frame, AnimationTrackFormat.Quaternion));
            Assert.Equal(sequence.Sample(1, frame, AnimationTrackFormat.Vector3), read.Sample(1, frame, AnimationTrackFormat.Vector3));
        }
        Assert.Equal(bytes, read.Write(Rdr2));

        byte[] staleHash = bytes.ToArray();
        staleHash[0] ^= 1;
        var stale = AnimationSequence.Read(staleHash, 2, Rdr2);
        var notice = Assert.Single(stale.SourceDiagnostics);
        Assert.Equal((AnimationDiagnosticCodes.SequenceHash, DiagnosticScope.Rewrite), (notice.Code, notice.Scope));
        Assert.True(stale.Validate(Rdr2).IsValid);
        Assert.Equal(bytes, stale.Write(Rdr2));

        // A stored channel in a group the format leaves unused is not guessed at.
        byte[] unused = bytes.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(unused.AsSpan(countTable + 6 * 2), 1);
        Assert.Throws<NotSupportedException>(() => AnimationSequence.Read(unused, 2, Rdr2));
        // GTA V's nine-group table has no linear group 9 or lane encoding; the RDR2 block is not readable as GTA V.
        Assert.ThrowsAny<Exception>(() => AnimationSequence.Read(bytes, 2, Gta));
    }

    [Fact]
    public void RootReferencesBindToDeclaredTracksOnRequest()
    {
        var sequence = new AnimationSequence(2);
        var mover = new RawFloatChannel([1, 2]);
        sequence.Tracks.Add(new SequenceTrack { Channels = { new StaticFloatChannel(3) } });
        sequence.Tracks.Add(new SequenceTrack { Channels = { mover } });
        sequence.RecalculateRootMotionChannels([1], []);
        Assert.Same(mover, Assert.Single(sequence.RootTranslationChannels));
        var read = AnimationSequence.Read(sequence.Write(Gta), 2, Gta);
        Assert.Empty(read.RootTranslationChannels);
        Assert.True(read.BindRootReferences([1], []));
        Assert.IsType<RawFloatChannel>(Assert.Single(read.RootTranslationChannels));
        Assert.False(read.BindRootReferences([0], []));
        Assert.Contains(read.SourceDiagnostics, d => d.Code == AnimationDiagnosticCodes.SequenceRootReference);
    }

    [Fact]
    public void GroupTablesRejectDuplicateTypes()
    {
        Assert.Throws<ArgumentException>(() => new AnimationChannelGroupTable([AnimationChannelType.RawFloat, AnimationChannelType.RawFloat]));
        var table = new AnimationChannelGroupTable([null, AnimationChannelType.LinearFloat]);
        Assert.Equal((-1, 1, (AnimationChannelType?)null), (table.GroupOf(AnimationChannelType.RawFloat), table.GroupOf(AnimationChannelType.LinearFloat), table.TypeOf(5)));
        var sequence = new AnimationSequence(1);
        sequence.Tracks.Add(new SequenceTrack { Channels = { new StaticFloatChannel(1) } });
        Assert.Contains(sequence.Validate(new AnimationSequenceFormat(table)).Diagnostics, d => d.Code == AnimationDiagnosticCodes.SequenceChannelType);
    }
}
