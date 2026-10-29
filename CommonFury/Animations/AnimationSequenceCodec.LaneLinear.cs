using System.Buffers.Binary;
using System.Numerics;
using CommonFury.Binary;

namespace CommonFury.Animations;

// Lane-interleaved linear channels: [0] word count, [1] address bits | initial bits << 8 | data rows << 16, [2] quantum,
// [3] offset; then a bit stream (from bit 128) of per-segment data-stream addresses, per-segment initial codes and one unary
// width code per group of four deltas (w zero bits then a one bit; 32 zeros mean width 32). The last 4 × rows words hold four
// lane streams interleaved by word; each group stores w bits per lane, starting at the segment's address plus earlier widths.
// Segments span 64 frames; deltas are zigzag second differences, and each segment stores one bridging delta to the next.
internal static partial class AnimationSequenceCodec
{
    private const int LaneSegmentFrames = 64;

    private static LinearFloatChannel ReadLaneLinear(ref BinarySpanReader constants, int frames, int component)
    {
        uint words = constants.ReadUInt32(), widths = constants.ReadUInt32();
        float quantum = constants.ReadSingle(), offset = constants.ReadSingle();
        if (words < 4 || words - 4 > constants.Remaining / 4)
            throw new InvalidDataException("Linear channel allocation is invalid.");
        var stream = new uint[words];
        stream[0] = words;
        stream[1] = widths;
        stream[2] = BitConverter.SingleToUInt32Bits(quantum);
        stream[3] = BitConverter.SingleToUInt32Bits(offset);
        for (int i = 4; i < words; i++)
            stream[i] = constants.ReadUInt32();
        int addressBits = (int)(widths & 0xFF), initialBits = (int)(widths >> 8 & 0xFF), rows = (int)(widths >> 16);
        if (addressBits > 32 || initialBits > 32 || (long)rows * 4 > words - 4)
            throw new InvalidDataException("Linear channel widths exceed their allocation.");
        int dataBase = (int)words - rows * 4, segments = (frames + LaneSegmentFrames - 1) / LaneSegmentFrames;
        int[] values = new int[frames];
        for (int segment = 0; segment < segments; segment++)
        {
            long address = Bits(stream, 128 + (long)segment * addressBits, addressBits, dataBase);
            int initial = unchecked((int)Bits(stream, 128 + (long)segments * addressBits + (long)segment * initialBits, initialBits, dataBase));
            long unary = 128 + (long)segments * (addressBits + initialBits) + (long)segment * 16 + address;
            int first = segment * LaneSegmentFrames, count = Math.Min(LaneSegmentFrames, frames - first);
            int groups = (count - 1 + (first + LaneSegmentFrames < frames ? 1 : 0) + 3) / 4;
            int value = initial, velocity = 0;
            values[first] = value;
            for (int group = 0, t = 0; group < groups; group++)
            {
                // The unary width code is decoded from one window of up to 32 bits; a terminator past the stream is out of range.
                int window = (int)Math.Clamp((long)dataBase * 32 - unary, 0, 32);
                int width = BitOperations.TrailingZeroCount(Bits(stream, unary, window, dataBase));
                if (width > window)
                    throw new InvalidDataException("Linear channel bit field exceeds its stream.");
                unary += width + 1;
                for (int lane = 0; lane < 4; lane++, t++)
                {
                    uint zigzag = Lane(stream, dataBase, rows, lane, address, width);
                    int delta = (int)(zigzag >> 1) ^ -(int)(zigzag & 1);
                    if (t + 1 >= count)
                        continue;
                    long nextVelocity = (long)velocity + delta, nextValue = value + nextVelocity;
                    if (nextVelocity is < int.MinValue or > int.MaxValue || nextValue is < int.MinValue or > int.MaxValue)
                        throw new InvalidDataException("Linear prediction overflows signed sample storage.");
                    velocity = (int)nextVelocity;
                    value = (int)nextValue;
                    values[first + t + 1] = value;
                }
                address += width;
            }
        }
        return new LinearFloatChannel(values, quantum, offset, component, LaneSegmentFrames)
        {
            SourcePackedValues = WordsToBytes(stream), SourceValues = values,
            SourceSegmentSize = LaneSegmentFrames, SourceEncoding = LinearChannelEncoding.LaneInterleaved
        };
    }

    private static uint Bits(uint[] stream, long position, int count, int limit)
    {
        if (count == 0)
            return 0;
        if (position < 0 || position + count > (long)limit * 32)
            throw new InvalidDataException("Linear channel bit field exceeds its stream.");
        int word = (int)(position >> 5), bit = (int)(position & 31);
        ulong pair = stream[word] | (word + 1 < limit ? (ulong)stream[word + 1] << 32 : 0);
        return (uint)(pair >> bit) & (count == 32 ? uint.MaxValue : (1u << count) - 1);
    }

    private static uint Lane(uint[] stream, int dataBase, int rows, int lane, long position, int count)
    {
        if (count == 0)
            return 0;
        if (position + count > (long)rows * 32)
            throw new InvalidDataException("Linear delta exceeds its lane stream.");
        int row = (int)(position >> 5), bit = (int)(position & 31);
        ulong pair = stream[dataBase + row * 4 + lane] | (row + 1 < rows ? (ulong)stream[dataBase + (row + 1) * 4 + lane] << 32 : 0);
        return (uint)(pair >> bit) & (count == 32 ? uint.MaxValue : (1u << count) - 1);
    }

    private static byte[] EncodeLaneLinear(LinearFloatChannel channel)
    {
        if (channel.SourcePackedValues is not null && channel.SourceEncoding == LinearChannelEncoding.LaneInterleaved
            && channel.EncodedValues.AsSpan().SequenceEqual(channel.SourceValues))
        {
            byte[] retained = channel.SourcePackedValues.ToArray();
            BinaryPrimitives.WriteSingleLittleEndian(retained.AsSpan(8), channel.Quantum);
            BinaryPrimitives.WriteSingleLittleEndian(retained.AsSpan(12), channel.Offset);
            return retained;
        }
        return WordsToBytes(EncodeLaneWords(channel.EncodedValues, channel.Quantum, channel.Offset));
    }

    // Minimal widths throughout; each segment stores its 63 in-segment deltas plus one bridging delta to the next
    // segment's initial code, padded with zero deltas to whole groups of four.
    private static uint[] EncodeLaneWords(int[] codes, float quantum, float offset)
    {
        int frames = codes.Length, segments = (frames + LaneSegmentFrames - 1) / LaneSegmentFrames;
        if (frames == 0)
            throw new InvalidOperationException("A linear channel needs at least one frame.");
        var addresses = new uint[segments];
        var initials = new uint[segments];
        var widths = new List<int>();
        var lanes = new List<uint>[] { [], [], [], [] };
        long laneBits = 0;
        // A segment has at most 63 in-segment deltas and one bridging delta.
        Span<int> deltas = stackalloc int[LaneSegmentFrames];
        for (int segment = 0; segment < segments; segment++)
        {
            if (laneBits > uint.MaxValue)
                throw new InvalidOperationException("Linear lane stream exceeds UInt32 addressing.");
            addresses[segment] = (uint)laneBits;
            int first = segment * LaneSegmentFrames, count = Math.Min(LaneSegmentFrames, frames - first);
            if (codes[first] < 0)
                throw new InvalidOperationException("Linear segment initial codes must be nonnegative.");
            initials[segment] = (uint)codes[first];
            int targets = count - 1 + (first + LaneSegmentFrames < frames ? 1 : 0);
            deltas.Clear();
            long previous = codes[first], velocity = 0;
            for (int t = 1; t <= targets; t++)
            {
                long next = codes[first + t] - previous, delta = next - velocity;
                if (next is < int.MinValue or > int.MaxValue || delta is < int.MinValue or > int.MaxValue)
                    throw new InvalidOperationException("Linear second difference exceeds signed storage.");
                deltas[t - 1] = (int)delta;
                velocity = next;
                previous = codes[first + t];
            }
            for (int group = 0; group < (targets + 3) / 4; group++)
            {
                uint maximum = 0;
                for (int lane = 0; lane < 4; lane++)
                {
                    int d = deltas[group * 4 + lane];
                    uint zigzag = (uint)(d << 1) ^ (uint)(d >> 31);
                    lanes[lane].Add(zigzag);
                    maximum = Math.Max(maximum, zigzag);
                }
                int width = 32 - BitOperations.LeadingZeroCount(maximum);
                widths.Add(width);
                laneBits += width;
            }
        }
        int addressBits = 32 - BitOperations.LeadingZeroCount(addresses.Max());
        int initialBits = 32 - BitOperations.LeadingZeroCount(initials.Max());
        long mainBits = 128 + (long)segments * (addressBits + initialBits) + widths.Sum(w => (long)w + 1);
        int mainWords = checked((int)((mainBits + 31) / 32)), rows = checked((int)((laneBits + 31) / 32));
        if (rows > ushort.MaxValue)
            throw new InvalidOperationException("Linear lane stream exceeds its row count field.");
        uint[] words = new uint[checked(mainWords + rows * 4)];
        byte[] main = new byte[mainWords * 4];
        var writer = new BinaryBitWriter(main);
        for (int i = 0; i < 4; i++)
            writer.WriteUInt32(0, 32);
        foreach (uint a in addresses)
            writer.WriteUInt32(a, addressBits);
        foreach (uint i in initials)
            writer.WriteUInt32(i, initialBits);
        foreach (int w in widths)
        {
            for (int z = w; z > 0; z -= Math.Min(z, 32))
                writer.WriteUInt32(0, Math.Min(z, 32));
            writer.WriteUInt32(1, 1);
        }
        for (int i = 4; i < mainWords; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(main.AsSpan(i * 4));
        words[0] = (uint)words.Length;
        words[1] = (uint)addressBits | (uint)initialBits << 8 | (uint)rows << 16;
        words[2] = BitConverter.SingleToUInt32Bits(quantum);
        words[3] = BitConverter.SingleToUInt32Bits(offset);
        for (int lane = 0; lane < 4; lane++)
        {
            byte[] data = new byte[rows * 4];
            var laneWriter = new BinaryBitWriter(data);
            for (int g = 0; g < widths.Count; g++)
                laneWriter.WriteUInt32(lanes[lane][g], widths[g]);
            for (int row = 0; row < rows; row++)
                words[mainWords + row * 4 + lane] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(row * 4));
        }
        return words;
    }

    private static byte[] WordsToBytes(uint[] words)
    {
        byte[] result = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), words[i]);
        return result;
    }
}
