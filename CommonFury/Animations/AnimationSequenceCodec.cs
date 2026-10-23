using System.Buffers.Binary;
using CommonFury.Binary;
using CommonFury.Hashing;

namespace CommonFury.Animations;

// Block layout: 32-byte header, constant section, FrameCount fixed-stride frame records, one UInt16 count per stored channel
// group, per-group UInt16 descriptors (track << 2 | component) padded to multiples of four with track-count << 2, then
// 6-byte mover references (stored group, component, constant word offset, frame bit offset).
internal static partial class AnimationSequenceCodec
{
    private const int HeaderSize = 0x20;

    internal static AnimationSequence Read(ReadOnlySpan<byte> data, int trackCount, AnimationSequenceFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (trackCount is < 0 or > 16383)
            throw new ArgumentOutOfRangeException(nameof(trackCount));
        AnimationChannelGroupTable table = format.Groups;
        int countBytes = table.GroupCount * 2;
        var header = new BinarySpanReader(BinaryLayout.Slice(data, 0, HeaderSize));
        uint hash = header.ReadUInt32(), length = header.ReadUInt32(), compactLength = header.ReadUInt32();
        uint constantSize = header.ReadUInt32(), rootOffset = header.ReadUInt32();
        ushort slop = header.ReadUInt16(), frames = header.ReadUInt16(), frameSize = header.ReadUInt16();
        ushort indirectWords = header.ReadUInt16(), quantizeBits = header.ReadUInt16();
        byte segmentSize = header.ReadByte(), rootCounts = header.ReadByte();
        BinaryLayout.Require(length <= format.MaximumPayloadBytes && compactLength <= format.MaximumPayloadBytes,
            "Animation sequence exceeds the supported storage limit.");
        int storedSize = checked((int)(compactLength == 0 ? length : compactLength));
        BinaryLayout.Require(data.Length == HeaderSize + storedSize, "Animation sequence allocation does not match its declared payload size.");
        byte[] payload;
        if (compactLength == 0)
            payload = data[HeaderSize..].ToArray();
        else
        {
            if (format.DecompressCompact is not { } decompress)
                throw new NotSupportedException("Compact (compressed) animation blocks are not supported by this format.");
            payload = decompress(data[HeaderSize..].ToArray(), (int)length);
            BinaryLayout.Require(payload.Length == length, "Compact animation payload does not expand to its declared size.");
        }
        long indexOffset = constantSize + (long)frames * frameSize;
        BinaryLayout.Require(constantSize % 4 == 0 && frameSize % 4 == 0 && indexOffset <= payload.Length - countBytes,
            "Animation constant or frame sections exceed their sequence allocation.");
        var result = new AnimationSequence(frames)
        {
            Hash = hash,
            SlopSize = slop,
            SegmentSize = segmentSize,
            Storage = compactLength == 0 ? AnimationSequenceStorage.Uncompressed : AnimationSequenceStorage.Compact,
        };
        // A derived hash covers the expanded payload, whether the block is stored compact or not.
        if (format.HashPolicy == AnimationSequenceHashPolicy.Derive && hash != DataHash(payload))
            result.AddRewriteNotice(AnimationDiagnosticCodes.SequenceHash, "Stored block hash differs from the payload hash; writing derives it.", "Hash");
        for (int i = 0; i < trackCount; i++)
            result.Tracks.Add(new());
        var constants = new BinarySpanReader(payload.AsSpan(0, (int)constantSize));
        var counts = new BinarySpanReader(payload.AsSpan((int)indexOffset, countBytes));
        var descriptors = new BinarySpanReader(payload.AsSpan((int)indexOffset + countBytes));
        int frameBitOffset = 0, actualIndirectWords = 0, actualQuantizeBits = 0;
        long decodedBytes = 0;
        for (int group = 0; group < table.GroupCount; group++)
        {
            int count = counts.ReadUInt16();
            AnimationChannelType? groupType = table.TypeOf(group);
            if (groupType is null && count != 0)
                throw new NotSupportedException($"Channel group {group} has no identified encoding.");
            for (int i = 0; i < count; i++)
            {
                AnimationChannelType type = groupType!.Value;
                ushort descriptor = descriptors.ReadUInt16();
                int track = descriptor >> 2, component = descriptor & 3;
                BinaryLayout.Require(track < trackCount, "Animation channel descriptor addresses an absent track.");
                if (type is AnimationChannelType.RawFloat or AnimationChannelType.QuantizedFloat
                    or AnimationChannelType.IndirectQuantizedFloat or AnimationChannelType.LinearFloat)
                {
                    decodedBytes += (long)frames * 4;
                    BinaryLayout.Require(decodedBytes <= format.MaximumPayloadBytes, "Decoded animation samples exceed the owned storage limit.");
                }
                int wordOffset = constants.Position / 4;
                AnimationChannel channel = ReadChannel(type, component, frames, segmentSize, format, ref constants);
                if (channel is IndirectQuantizedFloatChannel indirectTable)
                {
                    decodedBytes += (long)indirectTable.EncodedValues.Length * 4;
                    BinaryLayout.Require(decodedBytes <= format.MaximumPayloadBytes, "Decoded animation value tables exceed the owned storage limit.");
                }
                channel.SourceWordOffset = wordOffset;
                channel.SourceBitOffset = frameBitOffset;
                BinaryLayout.Require(channel.FrameBits is >= 0 and <= 32 && frameBitOffset <= frameSize * 8 - channel.FrameBits,
                    "Animation frame channel exceeds its declared frame stride.");
                frameBitOffset += channel.FrameBits;
                if (channel is QuantizedFloatChannel q)
                    actualQuantizeBits += q.ValueBits;
                if (channel is IndirectQuantizedFloatChannel)
                    actualIndirectWords += constants.Position / 4 - wordOffset;
                result.SourceChannelOrder.Add(channel);
                result.Tracks[track].Channels.Add(channel);
            }
            int aligned = BinaryLayout.Align(count, 4);
            for (int i = count; i < aligned; i++)
                if (descriptors.ReadUInt16() != trackCount << 2)
                    result.AddDiagnostic(AnimationDiagnosticCodes.SequenceDescriptorPadding, "Channel descriptor padding differs from the track-count sentinel and is retained in the source encoding.", "SourceEncoding");
        }
        BinaryLayout.Require(actualIndirectWords == indirectWords && actualQuantizeBits == quantizeBits,
            "Animation channel group sizes disagree with the sequence header.");
        if (constants.ReadBytes(constants.Remaining).ContainsAnyExcept((byte)0))
            result.AddDiagnostic(AnimationDiagnosticCodes.SequenceConstantPadding, "Nonzero bytes follow the decoded constant channels; rewriting an edited sequence would discard them.", "SourceEncoding");
        for (int frame = 0; frame < frames; frame++)
        {
            var reader = new BinaryBitReader(payload.AsSpan((int)constantSize + frame * frameSize, frameSize));
            foreach (AnimationChannel channel in result.SourceChannelOrder)
            {
                switch (channel)
                {
                    case RawFloatChannel raw:
                        raw.Values[frame] = BitConverter.UInt32BitsToSingle(reader.ReadUInt32(32));
                        break;
                    case QuantizedFloatChannel quantized:
                        quantized.EncodedValues[frame] = reader.ReadUInt32(quantized.ValueBits);
                        break;
                    case IndirectQuantizedFloatChannel indirect:
                        uint index = reader.ReadUInt32(indirect.FrameIndexBits);
                        BinaryLayout.Require(indirect.ValueBits == 0 || index < indirect.EncodedValues.Length, "Indirect animation frame index exceeds its bounded value table.");
                        indirect.FrameIndices[frame] = index;
                        break;
                }
            }
            while (reader.Remaining > 0)
                if (reader.ReadUInt32((int)Math.Min(32, reader.Remaining)) != 0 && !result.HasUnmodeledData)
                    result.AddDiagnostic(AnimationDiagnosticCodes.SequenceFramePadding, "Nonzero unused frame bits are retained in the source encoding.", "SourceEncoding");
        }
        foreach (SequenceTrack track in result.Tracks)
            track.Channels.Sort((a, b) => SortComponent(a).CompareTo(SortComponent(b)));
        int referencesStart = checked((int)indexOffset + countBytes + descriptors.Position);
        int translationCount = rootCounts >> 4, rotationCount = rootCounts & 15;
        BinaryLayout.Require(rootOffset == 0 && rootCounts == 0 || rootOffset >= HeaderSize && rootOffset <= HeaderSize + payload.Length,
            "Mover reference offset exceeds its sequence allocation.");
        int actualRootOffset = rootOffset == 0 && rootCounts == 0 ? referencesStart : (int)rootOffset - HeaderSize;
        BinaryLayout.Require(actualRootOffset >= referencesStart && actualRootOffset <= payload.Length - (translationCount + rotationCount) * 6,
            "Mover references exceed their sequence allocation or overlap channel descriptors.");
        if (payload.AsSpan(referencesStart, actualRootOffset - referencesStart).ContainsAnyExcept((byte)0))
            result.AddDiagnostic(AnimationDiagnosticCodes.SequenceReferencePadding, "Nonzero bytes before mover references are retained in the source encoding.", "SourceEncoding");
        var references = new BinarySpanReader(payload.AsSpan(actualRootOffset));
        ReadReferences(result.SourceTranslationReferences, translationCount, table, ref references);
        ReadReferences(result.SourceRotationReferences, rotationCount, table, ref references);
        if (references.ReadBytes(references.Remaining).ContainsAnyExcept((byte)0))
            result.AddDiagnostic(AnimationDiagnosticCodes.SequenceTrailingData, "Nonzero bytes after mover references are retained in the source encoding.", "SourceEncoding");
        result.RootReferencesBound = rootCounts == 0;
        if (format.BindRootReferencesOnRead)
            result.BindRootReferencesToAnyTrack();
        result.OriginalBytes = data.ToArray();
        result.OriginalStorage = result.Storage;
        result.OriginalCanonicalBytes = EncodeUncompressed(result, format);
        return result;
    }

    private static int SortComponent(AnimationChannel channel) => channel is QuaternionOperationChannel ? 4 : channel.ComponentIndex;

    private static AnimationChannel ReadChannel(AnimationChannelType type, int component, int frames, byte segmentSize,
        AnimationSequenceFormat format, ref BinarySpanReader reader)
    {
        switch (type)
        {
            case AnimationChannelType.StaticQuaternion:
                return new StaticQuaternionChannel(reader.ReadVector3()) { ComponentIndex = component };
            case AnimationChannelType.StaticVector3:
                return new StaticVector3Channel(reader.ReadVector3(), component);
            case AnimationChannelType.StaticFloat:
                return new StaticFloatChannel(reader.ReadSingle(), component);
            case AnimationChannelType.RawFloat:
                return new RawFloatChannel(new float[frames], component);
            case AnimationChannelType.QuantizedFloat:
                int bits = reader.ReadInt32();
                float quantum = reader.ReadSingle(), offset = reader.ReadSingle();
                BinaryLayout.Require(bits is >= 0 and <= 32, "Quantized animation bit width exceeds UInt32 storage.");
                return new QuantizedFloatChannel(bits, quantum, offset, new uint[frames], component);
            case AnimationChannelType.IndirectQuantizedFloat:
                int frameBits = reader.ReadInt32(), valueBits = reader.ReadInt32(), words = reader.ReadInt32();
                float indirectQuantum = reader.ReadSingle(), indirectOffset = reader.ReadSingle();
                BinaryLayout.Require(frameBits is >= 0 and <= 32 && valueBits is >= 0 and <= 32 && words >= 0 && words <= reader.Remaining / 4,
                    "Indirect animation channel dimensions exceed their bounded constant section.");
                byte[] packed = reader.ReadBytes(words * 4).ToArray();
                long capacity = valueBits == 0 ? 0 : Math.Min((long)words * 32 / valueBits, 1L << frameBits);
                BinaryLayout.Require(capacity <= format.MaximumPayloadBytes / 4, "Indirect animation value table exceeds the storage limit.");
                uint[] codes = new uint[(int)capacity];
                var valueReader = new BinaryBitReader(packed);
                for (int i = 0; i < codes.Length; i++)
                    codes[i] = valueReader.ReadUInt32(valueBits);
                return new IndirectQuantizedFloatChannel(frameBits, valueBits, indirectQuantum, indirectOffset, codes, new uint[frames], component)
                { SourcePackedValues = packed, SourceValues = (uint[])codes.Clone(), SourceValueBits = valueBits };
            case AnimationChannelType.LinearFloat:
                return format.LinearEncoding switch
                {
                    LinearChannelEncoding.RiceCoded => ReadRiceLinear(ref reader, frames, segmentSize, component),
                    LinearChannelEncoding.LaneInterleaved => ReadLaneLinear(ref reader, frames, component),
                    _ => throw new NotSupportedException("Unknown linear channel encoding."),
                };
            case AnimationChannelType.ReconstructQuaternion:
            case AnimationChannelType.NormalizeQuaternion:
                return new QuaternionOperationChannel(type, component);
            default:
                throw new InvalidDataException("Unsupported animation stream opcode.");
        }
    }

    private static void ReadReferences(List<RootChannelReference> references, int count, AnimationChannelGroupTable table, ref BinarySpanReader reader)
    {
        for (int i = 0; i < count; i++)
        {
            byte group = reader.ReadByte(), component = reader.ReadByte();
            ushort word = reader.ReadUInt16(), bit = reader.ReadUInt16();
            BinaryLayout.Require(table.TypeOf(group) is not null && component < 4, "Mover reference contains an invalid channel group or component.");
            references.Add(new(table.TypeOf(group)!.Value, component, word, bit));
        }
    }

    internal static byte[] Write(AnimationSequence sequence, AnimationSequenceFormat format)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(format);
        sequence.Validate(format).ThrowIfInvalid();
        if (sequence.Storage == AnimationSequenceStorage.Compact && format.CompressCompact is null)
            throw new NotSupportedException("Compact animation storage is not supported by this format.");
        byte[] uncompressed = EncodeUncompressed(sequence, format);
        bool unchanged = sequence.OriginalCanonicalBytes is not null && uncompressed.AsSpan().SequenceEqual(sequence.OriginalCanonicalBytes);
        if (unchanged && sequence.Storage == sequence.OriginalStorage)
        {
            byte[] original = sequence.OriginalBytes!.ToArray();
            if (format.HashPolicy != AnimationSequenceHashPolicy.Derive)
                return original;
            return Finish(original, format, sequence.OriginalStorage == AnimationSequenceStorage.Compact
                ? format.DecompressCompact!(original.AsMemory(HeaderSize), checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(4))))
                : original.AsSpan(HeaderSize));
        }
        if (!unchanged && (sequence.HasUnmodeledData || !sequence.RootReferencesBound && (sequence.SourceTranslationReferences.Count + sequence.SourceRotationReferences.Count > 0)))
            throw new InvalidOperationException("Edited sequence cannot preserve unmodeled source data or unresolved mover references. Resolve the source diagnostics before rewriting.");
        if (unchanged && sequence.OriginalBytes is not null)
        {
            ReadOnlySpan<byte> source = sequence.OriginalBytes;
            int originalSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source[4..]));
            byte[] body = sequence.OriginalStorage == AnimationSequenceStorage.Compact
                ? format.DecompressCompact!(source[HeaderSize..].ToArray(), originalSize)
                : source[HeaderSize..].ToArray();
            uncompressed = new byte[HeaderSize + body.Length];
            source[..HeaderSize].CopyTo(uncompressed);
            body.CopyTo(uncompressed, HeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(uncompressed.AsSpan(8), 0);
        }
        if (sequence.Storage == AnimationSequenceStorage.Uncompressed)
            return Finish(uncompressed, format, uncompressed.AsSpan(HeaderSize));
        byte[] compact = format.CompressCompact!(uncompressed.AsMemory(HeaderSize));
        int slop = BinaryLayout.Align(compact.Length, 16);
        if (slop > ushort.MaxValue)
            throw new InvalidOperationException("Compact sequence requires more in-place slop than its UInt16 header supports.");
        byte[] result = new byte[HeaderSize + compact.Length];
        uncompressed.AsSpan(0, HeaderSize).CopyTo(result);
        compact.CopyTo(result, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)compact.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x14), (ushort)slop);
        return Finish(result, format, uncompressed.AsSpan(HeaderSize));
    }

    // Derived hashes cover the expanded payload, so compact and uncompressed storage of the same data share a hash.
    private static byte[] Finish(byte[] block, AnimationSequenceFormat format, ReadOnlySpan<byte> expandedPayload)
    {
        if (format.HashPolicy == AnimationSequenceHashPolicy.Derive)
            BinaryPrimitives.WriteUInt32LittleEndian(block, DataHash(expandedPayload));
        return block;
    }

    // Integrity hash of the payload after the header: one-at-a-time over sign-extended bytes, then finalized.
    internal static uint DataHash(ReadOnlySpan<byte> data)
    {
        uint state = 0;
        foreach (byte value in data)
            state = Jenkins.MixByte(state, unchecked((uint)(sbyte)value));
        return Jenkins.Finalize(state);
    }

    private sealed record ChannelLocation(AnimationChannel Channel, ushort Descriptor, int WordOffset, int BitOffset, byte[] ConstantData);

    private static byte[] EncodeUncompressed(AnimationSequence sequence, AnimationSequenceFormat format)
    {
        AnimationChannelGroupTable table = format.Groups;
        var groups = new List<ChannelLocation>[table.GroupCount];
        for (int i = 0; i < groups.Length; i++)
            groups[i] = [];
        for (int track = 0; track < sequence.Tracks.Count; track++)
            foreach (AnimationChannel channel in sequence.Tracks[track].Channels)
            {
                int group = table.GroupOf(channel.Type);
                if (group < 0)
                    throw new InvalidOperationException($"Channel type {channel.Type} has no stored group in this format.");
                groups[group].Add(new(channel, checked((ushort)((track << 2) | channel.ComponentIndex)), 0, 0, []));
            }
        int constantBytes = 0, frameBits = 0, indirectWords = 0, quantizeBits = 0, descriptorBytes = table.GroupCount * 2;
        var locations = new Dictionary<AnimationChannel, ChannelLocation>(ReferenceEqualityComparer.Instance);
        foreach (List<ChannelLocation> group in groups)
        {
            descriptorBytes = checked(descriptorBytes + BinaryLayout.Align(group.Count, 4) * 2);
            for (int i = 0; i < group.Count; i++)
            {
                AnimationChannel channel = group[i].Channel;
                byte[] constants = EncodeChannel(channel, format);
                var location = group[i] with { ConstantData = constants, WordOffset = constantBytes / 4, BitOffset = frameBits };
                group[i] = location;
                locations.Add(channel, location);
                constantBytes = checked(constantBytes + constants.Length);
                frameBits = checked(frameBits + channel.FrameBits);
                if (channel is IndirectQuantizedFloatChannel)
                    indirectWords = checked(indirectWords + constants.Length / 4);
                if (channel is QuantizedFloatChannel q)
                    quantizeBits = checked(quantizeBits + q.ValueBits);
            }
        }
        int frameBytes = BinaryLayout.Align(checked((frameBits + 7) / 8), 4);
        if (frameBytes > ushort.MaxValue || indirectWords > ushort.MaxValue || quantizeBits > ushort.MaxValue)
            throw new InvalidOperationException("Animation sequence group sizes exceed UInt16 header storage.");
        List<RootChannelReference> translation = References(sequence.RootTranslationChannels, sequence.SourceTranslationReferences);
        List<RootChannelReference> rotation = References(sequence.RootRotationChannels, sequence.SourceRotationReferences);
        long total = HeaderSize + (long)constantBytes + (long)frameBytes * sequence.FrameCount + descriptorBytes + (translation.Count + rotation.Count) * 6;
        if (total - HeaderSize > format.MaximumPayloadBytes)
            throw new InvalidOperationException("Animation sequence exceeds the resource storage limit.");
        byte[] result = new byte[(int)total];
        var writer = new BinarySpanWriter(result);
        writer.WriteUInt32(sequence.Hash);
        writer.WriteUInt32((uint)(total - HeaderSize));
        writer.WriteUInt32(0);
        writer.WriteUInt32((uint)constantBytes);
        writer.WriteUInt32((uint)(HeaderSize + constantBytes + frameBytes * sequence.FrameCount + descriptorBytes));
        writer.WriteUInt16(sequence.SlopSize);
        writer.WriteUInt16((ushort)sequence.FrameCount);
        writer.WriteUInt16((ushort)frameBytes);
        writer.WriteUInt16((ushort)indirectWords);
        writer.WriteUInt16((ushort)quantizeBits);
        writer.WriteByte(sequence.SegmentSize);
        writer.WriteByte(checked((byte)((translation.Count << 4) | rotation.Count)));
        foreach (List<ChannelLocation> group in groups)
            foreach (ChannelLocation location in group)
                writer.WriteBytes(location.ConstantData);
        for (int frame = 0; frame < sequence.FrameCount; frame++)
        {
            var bits = new BinaryBitWriter(result.AsSpan(HeaderSize + constantBytes + frameBytes * frame, frameBytes));
            foreach (List<ChannelLocation> group in groups)
                foreach (ChannelLocation location in group)
                    switch (location.Channel)
                    {
                        case RawFloatChannel raw:
                            bits.WriteUInt32(BitConverter.SingleToUInt32Bits(raw.Values[frame]), 32);
                            break;
                        case QuantizedFloatChannel q:
                            bits.WriteUInt32(q.EncodedValues[frame], q.ValueBits);
                            break;
                        case IndirectQuantizedFloatChannel indirect:
                            bits.WriteUInt32(indirect.FrameIndices[frame], indirect.FrameIndexBits);
                            break;
                    }
        }
        writer.Seek(HeaderSize + constantBytes + frameBytes * sequence.FrameCount);
        foreach (List<ChannelLocation> group in groups)
            writer.WriteUInt16(checked((ushort)group.Count));
        foreach (List<ChannelLocation> group in groups)
        {
            foreach (ChannelLocation location in group)
                writer.WriteUInt16(location.Descriptor);
            for (int i = group.Count; i < BinaryLayout.Align(group.Count, 4); i++)
                writer.WriteUInt16(checked((ushort)(sequence.Tracks.Count << 2)));
        }
        foreach (RootChannelReference reference in translation.Concat(rotation))
        {
            writer.WriteByte(checked((byte)table.GroupOf(reference.Type)));
            writer.WriteByte(reference.Component);
            writer.WriteUInt16(reference.WordOffset);
            writer.WriteUInt16(reference.BitOffset);
        }
        if (format.HashPolicy == AnimationSequenceHashPolicy.Derive)
            BinaryPrimitives.WriteUInt32LittleEndian(result, DataHash(result.AsSpan(HeaderSize)));
        return result;

        List<RootChannelReference> References(List<AnimationChannel> channels, List<RootChannelReference> source)
        {
            if (!sequence.RootReferencesBound && channels.Count == 0)
                return source.ToList();
            var references = new List<RootChannelReference>();
            foreach (AnimationChannel channel in channels)
            {
                if (!locations.TryGetValue(channel, out ChannelLocation? location))
                    throw new InvalidOperationException("Mover channel does not belong to its sequence.");
                if (location.WordOffset > ushort.MaxValue || location.BitOffset > ushort.MaxValue)
                    throw new InvalidOperationException("Mover channel offset exceeds its packed reference.");
                references.Add(new(channel.Type, checked((byte)channel.ComponentIndex), (ushort)location.WordOffset, (ushort)location.BitOffset));
            }
            return references.OrderBy(r => table.GroupOf(r.Type)).ThenBy(r => r.Component).ToList();
        }
    }

    private static byte[] EncodeChannel(AnimationChannel channel, AnimationSequenceFormat format)
    {
        byte[] data;
        switch (channel)
        {
            case StaticFloatChannel scalar:
                data = new byte[4];
                var scalarWriter = new BinarySpanWriter(data);
                scalarWriter.WriteSingle(scalar.Value);
                return data;
            case StaticVector3Channel vector:
                data = new byte[12];
                var vectorWriter = new BinarySpanWriter(data);
                vectorWriter.WriteVector3(vector.Value);
                return data;
            case StaticQuaternionChannel quaternion:
                data = new byte[12];
                var quaternionWriter = new BinarySpanWriter(data);
                quaternionWriter.WriteVector3(quaternion.Components);
                return data;
            case QuantizedFloatChannel quantized:
                data = new byte[12];
                var quantizedWriter = new BinarySpanWriter(data);
                quantizedWriter.WriteInt32(quantized.ValueBits);
                quantizedWriter.WriteSingle(quantized.Quantum);
                quantizedWriter.WriteSingle(quantized.Offset);
                return data;
            case IndirectQuantizedFloatChannel indirect:
                byte[] packed;
                if (indirect.SourcePackedValues is not null && indirect.ValueBits == indirect.SourceValueBits && indirect.EncodedValues.AsSpan().SequenceEqual(indirect.SourceValues))
                    packed = indirect.SourcePackedValues;
                else
                {
                    long length = ((long)indirect.EncodedValues.Length * indirect.ValueBits + 31) / 32 * 4;
                    if (length > format.MaximumPayloadBytes - 20)
                        throw new InvalidOperationException("Indirect animation table exceeds its storage limit.");
                    packed = new byte[(int)length];
                    var bitWriter = new BinaryBitWriter(packed);
                    foreach (uint value in indirect.EncodedValues)
                        bitWriter.WriteUInt32(value, indirect.ValueBits);
                }
                data = new byte[20 + packed.Length];
                var indirectWriter = new BinarySpanWriter(data);
                indirectWriter.WriteInt32(indirect.FrameIndexBits);
                indirectWriter.WriteInt32(indirect.ValueBits);
                indirectWriter.WriteInt32(packed.Length / 4);
                indirectWriter.WriteSingle(indirect.Quantum);
                indirectWriter.WriteSingle(indirect.Offset);
                indirectWriter.WriteBytes(packed);
                return data;
            case LinearFloatChannel linear:
                return format.LinearEncoding switch
                {
                    LinearChannelEncoding.RiceCoded => EncodeRiceLinear(linear, format.MaximumPayloadBytes),
                    LinearChannelEncoding.LaneInterleaved => EncodeLaneLinear(linear),
                    _ => throw new NotSupportedException("Unknown linear channel encoding."),
                };
            case RawFloatChannel or QuaternionOperationChannel:
                return [];
            default:
                throw new InvalidOperationException("Unsupported animation channel model.");
        }
    }
}
