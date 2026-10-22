using System.Numerics;
using CommonFury.Binary;

namespace CommonFury.Animations;

// Rice-coded linear channels (GTA V): [0] word count, [1] address bits | initial bits << 8 | Rice divisor << 16, [2] quantum,
// [3] offset; then per-segment delta addresses, per-segment initial codes and Rice-coded sign-magnitude second differences.
internal static partial class AnimationSequenceCodec
{
    private static LinearFloatChannel ReadRiceLinear(ref BinarySpanReader constants, int frames, byte segmentSize, int component)
    {
        int words = constants.ReadInt32();
        uint widths = constants.ReadUInt32();
        float quantum = constants.ReadSingle(), offset = constants.ReadSingle();
        BinaryLayout.Require(words >= 4 && words - 4 <= constants.Remaining / 4 && BitOperations.IsPow2(segmentSize),
            "Linear animation allocation or segment size is invalid.");
        int addressBits = (int)BitField32.Extract(widths, 0, 8);
        int initialBits = (int)BitField32.Extract(widths, 8, 8);
        int divisor = (int)BitField32.Extract(widths, 16, 8);
        BinaryLayout.Require(addressBits <= 32 && initialBits <= 32 && divisor <= 31,
            "Linear animation bit widths exceed their signed runtime representation.");
        byte[] packed = constants.ReadBytes((words - 4) * 4).ToArray();
        var reader = new BinaryBitReader(packed);
        int segments = (frames + segmentSize - 1) / segmentSize;
        uint[] addresses = new uint[segments];
        int[] initials = new int[segments];
        for (int i = 0; i < segments; i++)
            addresses[i] = reader.ReadUInt32(addressBits);
        for (int i = 0; i < segments; i++)
            initials[i] = unchecked((int)reader.ReadUInt32(initialBits));
        long deltaStart = reader.Position;
        int[] values = new int[frames];
        for (int segment = 0; segment < segments; segment++)
        {
            long start = deltaStart + addresses[segment];
            long limit = segment + 1 < segments ? deltaStart + addresses[segment + 1] : reader.Length;
            BinaryLayout.Require(start >= deltaStart && start <= limit && limit <= reader.Length,
                "Linear animation segment offsets exceed their own channel allocation.");
            reader.Seek(start);
            int first = segment * segmentSize, count = Math.Min(segmentSize, frames - first);
            int value = initials[segment], velocity = 0;
            if (count > 0)
                values[first] = value;
            for (int i = 1; i < count; i++)
            {
                int delta = ReadRice(ref reader, divisor, limit);
                long nextVelocity = (long)velocity + delta, nextValue = value + nextVelocity;
                BinaryLayout.Require(nextVelocity is >= int.MinValue and <= int.MaxValue && nextValue is >= int.MinValue and <= int.MaxValue,
                    "Linear animation prediction overflows signed sample storage.");
                velocity = (int)nextVelocity;
                value = (int)nextValue;
                values[first + i] = value;
            }
        }
        byte[] source = new byte[words * 4];
        var writer = new BinarySpanWriter(source);
        writer.WriteInt32(words);
        writer.WriteUInt32(widths);
        writer.WriteSingle(quantum);
        writer.WriteSingle(offset);
        writer.WriteBytes(packed);
        return new(values, quantum, offset, component, segmentSize, (byte)divisor)
        {
            SourcePackedValues = source, SourceValues = (int[])values.Clone(), SourceSegmentSize = segmentSize,
            SourceRiceDivisor = (byte)divisor, SourceEncoding = LinearChannelEncoding.RiceCoded
        };
    }

    private static int ReadRice(ref BinaryBitReader reader, int divisor, long limit)
    {
        RequireBits(reader, divisor, limit);
        uint remainder = reader.ReadUInt32(divisor);
        uint quotient = 0;
        while (true)
        {
            RequireBits(reader, 1, limit);
            if (reader.ReadUInt32(1) != 0)
                break;
            quotient++;
            BinaryLayout.Require(quotient < 32, "Linear Rice quotient exceeds the runtime's bounded 32-bit unary code.");
        }
        long magnitude = ((long)quotient << divisor) | remainder;
        BinaryLayout.Require(magnitude <= int.MaxValue, "Linear Rice magnitude exceeds signed delta storage.");
        if (magnitude == 0)
            return 0;
        RequireBits(reader, 1, limit);
        return reader.ReadUInt32(1) == 0 ? (int)magnitude : -(int)magnitude;
    }

    private static void RequireBits(BinaryBitReader reader, int count, long limit) =>
        BinaryLayout.Require(reader.Position <= limit - count, "Linear Rice code exceeds its own segment allocation.");

    private static byte[] EncodeRiceLinear(LinearFloatChannel channel, int maximumPayloadBytes)
    {
        bool riceSource = channel.SourcePackedValues is not null && channel.SourceEncoding == LinearChannelEncoding.RiceCoded;
        if (riceSource && channel.SourceSegmentSize == channel.SegmentSize && channel.SourceRiceDivisor == channel.RiceDivisor
            && channel.EncodedValues.AsSpan().SequenceEqual(channel.SourceValues))
        {
            byte[] retained = channel.SourcePackedValues!.ToArray();
            var update = new BinarySpanWriter(retained);
            update.Seek(8);
            update.WriteSingle(channel.Quantum);
            update.WriteSingle(channel.Offset);
            return retained;
        }
        if (!BitOperations.IsPow2(channel.SegmentSize) || channel.RiceDivisor > 31)
            throw new InvalidOperationException("Linear animation requires a power-of-two segment size and a Rice divisor of 0 through 31.");
        int count = channel.EncodedValues.Length, size = channel.SegmentSize, divisor = channel.RiceDivisor;
        int segments = (count + size - 1) / size;
        int[] initial = new int[segments];
        uint[] addresses = new uint[segments];
        int[] deltas = new int[segments * (size - 1)];
        long deltaBits = 0;
        uint maximumInitial = 0;
        for (int segment = 0; segment < segments; segment++)
        {
            if (deltaBits > uint.MaxValue)
                throw new InvalidOperationException("Linear animation delta offsets exceed UInt32 storage.");
            addresses[segment] = (uint)deltaBits;
            int first = segment * size;
            initial[segment] = channel.EncodedValues[first];
            maximumInitial = Math.Max(maximumInitial, unchecked((uint)initial[segment]));
            long previous = initial[segment], velocity = 0;
            for (int local = 1; local < size; local++)
            {
                long delta = 0;
                if (first + local < count)
                {
                    long value = channel.EncodedValues[first + local];
                    long nextVelocity = value - previous;
                    delta = nextVelocity - velocity;
                    if (nextVelocity is < int.MinValue or > int.MaxValue)
                        throw new InvalidOperationException("Linear animation velocity exceeds signed storage.");
                    previous = value;
                    velocity = nextVelocity;
                }
                long magnitude = Math.Abs(delta);
                if (magnitude > int.MaxValue || (magnitude >> divisor) >= 32)
                    throw new InvalidOperationException("Linear sample delta exceeds the selected Rice divisor's bounded code. Choose a larger divisor explicitly.");
                deltas[segment * (size - 1) + local - 1] = (int)delta;
                deltaBits += divisor + (magnitude >> divisor) + 1 + (magnitude == 0 ? 0 : 1);
            }
        }
        int addressBits = segments == 0 ? 0 : 32 - BitOperations.LeadingZeroCount(addresses[^1]);
        int initialBits = 32 - BitOperations.LeadingZeroCount(maximumInitial);
        long payloadBits = (long)segments * (addressBits + initialBits) + deltaBits;
        long payloadBytes = (payloadBits + 31) / 32 * 4;
        if (payloadBytes > maximumPayloadBytes - 16)
            throw new InvalidOperationException("Linear animation exceeds its bounded storage limit.");
        byte[] result = new byte[16 + (int)payloadBytes];
        var header = new BinarySpanWriter(result);
        header.WriteInt32(result.Length / 4);
        uint widths = BitField32.Replace(BitField32.Replace((uint)addressBits, 8, 8, (uint)initialBits), 16, 8, (uint)divisor);
        if (riceSource)
            widths |= (uint)channel.SourcePackedValues![7] << 24;
        header.WriteUInt32(widths);
        header.WriteSingle(channel.Quantum);
        header.WriteSingle(channel.Offset);
        var writer = new BinaryBitWriter(result.AsSpan(16));
        foreach (uint address in addresses)
            writer.WriteUInt32(address, addressBits);
        foreach (int value in initial)
            writer.WriteUInt32(unchecked((uint)value), initialBits);
        uint mask = divisor == 0 ? 0 : uint.MaxValue >> (32 - divisor);
        foreach (int delta in deltas)
        {
            uint magnitude = (uint)Math.Abs(delta), quotient = magnitude >> divisor;
            writer.WriteUInt32(magnitude & mask, divisor);
            for (int i = 0; i < quotient; i++)
                writer.WriteUInt32(0, 1);
            writer.WriteUInt32(1, 1);
            if (magnitude != 0)
                writer.WriteUInt32(delta < 0 ? 1u : 0u, 1);
        }
        return result;
    }
}
