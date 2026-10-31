using System.Buffers.Binary;
using System.Numerics;
using CommonFury.Binary;

namespace CommonFury.Compression;

// Reads a Zstandard backward bitstream: the last byte holds a marker bit above the first bits to read.
// Bits below the start of the stream read as zero; callers check IsComplete or IsOverflowed afterwards.
internal ref struct BackwardBitReader
{
    private readonly ReadOnlySpan<byte> data;
    private long position;

    public BackwardBitReader(ReadOnlySpan<byte> data)
    {
        BinaryLayout.Require(!data.IsEmpty && data[^1] != 0, "Zstandard bitstream lacks its end marker.");
        this.data = data;
        position = (long)(data.Length - 1) * 8 + BitOperations.Log2(data[^1]);
    }

    public readonly bool IsComplete => position == 0;
    public readonly bool IsOverflowed => position < 0;

    public uint Read(int count)
    {
        uint value = Peek(count);
        position -= count;
        return value;
    }

    public void Skip(int count) => position -= count;

    public readonly uint Peek(int count)
    {
        long low = position - count;
        uint mask = (uint)((1UL << count) - 1);
        if (low >= 0)
        {
            long index = low >> 3;
            if (index <= data.Length - 8)
                return (uint)(BinaryPrimitives.ReadUInt64LittleEndian(data[(int)index..]) >> (int)(low & 7)) & mask;
            if (data.Length >= 8)
                return (uint)(BinaryPrimitives.ReadUInt64LittleEndian(data[^8..]) >> (int)(low - (data.Length - 8) * 8L)) & mask;
        }
        ulong value = 0;
        for (long bit = position - 1; bit >= Math.Max(low, 0); bit--)
            value |= (ulong)(data[(int)(bit >> 3)] >> (int)(bit & 7) & 1) << (int)(bit - low);
        return (uint)value & mask;
    }
}

// One FSE decoding state. For sequence codes, Base and ExtraBits describe the value the symbol stands for;
// for other alphabets Base is the symbol itself.
internal struct FseEntry
{
    public uint Base;
    public ushort NextState;
    public byte StateBits;
    public byte ExtraBits;
}

internal sealed class FseTable
{
    public const int MaximumSymbols = 256;
    public readonly FseEntry[] Entries;
    public int AccuracyLog { get; private set; }

    public FseTable(int maximumAccuracyLog) => Entries = new FseEntry[1 << maximumAccuracyLog];

    public void CopyFrom(FseTable other)
    {
        AccuracyLog = other.AccuracyLog;
        other.Entries.AsSpan(0, 1 << other.AccuracyLog).CopyTo(Entries);
    }

    // Reads a normalized distribution from the forward bitstream at the start of data and builds the table.
    // Returns the bytes the description occupies.
    public int Read(ReadOnlySpan<byte> data, int maximumAccuracyLog, int maximumSymbol, ReadOnlySpan<uint> bases, ReadOnlySpan<byte> extraBits)
    {
        Span<short> counts = stackalloc short[MaximumSymbols];
        long bit = 0;
        int accuracyLog = 5 + (int)ReadForward(data, ref bit, 4);
        BinaryLayout.Require(accuracyLog <= maximumAccuracyLog, "Zstandard FSE accuracy exceeds its limit.");
        int remaining = 1 << accuracyLog, symbol = 0;
        while (remaining > 0 && symbol <= maximumSymbol)
        {
            int bits = BitOperations.Log2((uint)remaining + 1) + 1;
            uint value = ReadForward(data, ref bit, bits);
            uint lowerMask = (1u << (bits - 1)) - 1, threshold = (1u << bits) - 1 - ((uint)remaining + 1);
            if ((value & lowerMask) < threshold)
            {
                bit--;
                value &= lowerMask;
            }
            else if (value > lowerMask)
                value -= threshold;
            int probability = (int)value - 1;
            remaining -= Math.Abs(probability);
            counts[symbol++] = (short)probability;
            if (probability != 0)
                continue;
            while (true)
            {
                int repeat = (int)ReadForward(data, ref bit, 2);
                for (int i = 0; i < repeat; i++)
                {
                    BinaryLayout.Require(symbol <= maximumSymbol, "Zstandard FSE distribution exceeds its alphabet.");
                    counts[symbol++] = 0;
                }
                if (repeat != 3)
                    break;
            }
        }
        BinaryLayout.Require(remaining == 0, "Zstandard FSE distribution does not sum to its table size.");
        int length = (int)((bit + 7) >> 3);
        BinaryLayout.Require(length <= data.Length, "Truncated Zstandard FSE table description.");
        Build(counts[..symbol], accuracyLog, bases, extraBits);
        return length;
    }

    public void Build(ReadOnlySpan<short> counts, int accuracyLog, ReadOnlySpan<uint> bases, ReadOnlySpan<byte> extraBits)
    {
        int size = 1 << accuracyLog, highThreshold = size - 1;
        Span<byte> symbols = stackalloc byte[size];
        Span<ushort> next = stackalloc ushort[MaximumSymbols];
        for (int symbol = 0; symbol < counts.Length; symbol++)
        {
            if (counts[symbol] == -1)
            {
                symbols[highThreshold--] = (byte)symbol;
                next[symbol] = 1;
            }
            else
                next[symbol] = (ushort)counts[symbol];
        }
        int position = 0, step = (size >> 1) + (size >> 3) + 3, mask = size - 1;
        for (int symbol = 0; symbol < counts.Length; symbol++)
            for (int i = 0; i < counts[symbol]; i++)
            {
                symbols[position] = (byte)symbol;
                do position = (position + step) & mask;
                while (position > highThreshold);
            }
        BinaryLayout.Require(position == 0, "Zstandard FSE distribution does not fill its table.");
        for (int state = 0; state < size; state++)
        {
            int symbol = symbols[state];
            int nextState = next[symbol]++;
            int bits = accuracyLog - BitOperations.Log2((uint)nextState);
            Entries[state] = new FseEntry
            {
                Base = bases.IsEmpty ? (uint)symbol : bases[symbol],
                ExtraBits = extraBits.IsEmpty ? (byte)0 : extraBits[symbol],
                StateBits = (byte)bits,
                NextState = (ushort)((nextState << bits) - size)
            };
        }
        AccuracyLog = accuracyLog;
    }

    public void BuildRle(byte symbol, int maximumSymbol, ReadOnlySpan<uint> bases, ReadOnlySpan<byte> extraBits)
    {
        BinaryLayout.Require(symbol <= maximumSymbol, "Zstandard RLE code exceeds its alphabet.");
        Entries[0] = new FseEntry { Base = bases[symbol], ExtraBits = extraBits[symbol] };
        AccuracyLog = 0;
    }

    // Reads bits least significant first; bits past the end read as zero and the caller checks the length used.
    private static uint ReadForward(ReadOnlySpan<byte> data, ref long bit, int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++, bit++)
        {
            long index = bit >> 3;
            if (index < data.Length)
                value |= (uint)(data[(int)index] >> (int)(bit & 7) & 1) << i;
        }
        return value;
    }
}

// Literal prefix codes: a lookup table indexed by the next MaximumBits bits of a backward stream.
internal sealed class HuffmanTable
{
    private const int MaximumCodeBits = 11;
    private readonly ushort[] entries = new ushort[1 << MaximumCodeBits];
    private readonly FseTable weightTable = new(6);
    public int MaximumBits { get; private set; }

    public void CopyFrom(HuffmanTable other)
    {
        MaximumBits = other.MaximumBits;
        other.entries.AsSpan(0, 1 << other.MaximumBits).CopyTo(entries);
    }

    // Reads a Huffman tree description and returns the bytes it occupies.
    public int Read(ReadOnlySpan<byte> data)
    {
        BinaryLayout.Require(!data.IsEmpty, "Truncated Zstandard Huffman tree description.");
        Span<byte> weights = stackalloc byte[256];
        int header = data[0], count, length;
        if (header >= 128)
        {
            count = header - 127;
            length = 1 + (count + 1) / 2;
            BinaryLayout.Require(length <= data.Length, "Truncated Zstandard Huffman weights.");
            for (int i = 0; i < count; i++)
                weights[i] = (byte)(i % 2 == 0 ? data[1 + i / 2] >> 4 : data[1 + i / 2] & 15);
        }
        else
        {
            length = 1 + header;
            BinaryLayout.Require(header > 0 && length <= data.Length, "Truncated Zstandard Huffman weights.");
            var compressed = data[1..length];
            int used = weightTable.Read(compressed, 6, 15, default, default);
            count = DecodeWeights(compressed[used..], weights);
        }
        Build(weights[..count]);
        return length;
    }

    public void Decode(ReadOnlySpan<byte> stream, Span<byte> destination)
    {
        var reader = new BackwardBitReader(stream);
        int bits = MaximumBits;
        for (int i = 0; i < destination.Length; i++)
        {
            ushort entry = entries[reader.Peek(bits)];
            destination[i] = (byte)entry;
            reader.Skip(entry >> 8);
        }
        BinaryLayout.Require(reader.IsComplete, "Zstandard Huffman stream length does not match its literals.");
    }

    private int DecodeWeights(ReadOnlySpan<byte> stream, Span<byte> weights)
    {
        var reader = new BackwardBitReader(stream);
        var table = weightTable.Entries;
        int log = weightTable.AccuracyLog;
        uint first = reader.Read(log), second = reader.Read(log);
        int count = 0;
        // Two interleaved states share the stream; once it is exhausted, the other state's symbol is the last.
        while (true)
        {
            Append(weights, ref count, table[first].Base);
            first = table[first].NextState + reader.Read(table[first].StateBits);
            if (reader.IsOverflowed)
            {
                Append(weights, ref count, table[second].Base);
                break;
            }
            Append(weights, ref count, table[second].Base);
            second = table[second].NextState + reader.Read(table[second].StateBits);
            if (reader.IsOverflowed)
            {
                Append(weights, ref count, table[first].Base);
                break;
            }
        }
        return count;
    }

    // At most 255 weights are stored; the last symbol's weight is implied.
    private static void Append(Span<byte> weights, ref int count, uint weight)
    {
        BinaryLayout.Require(count < 255, "Zstandard Huffman weights exceed the literal alphabet.");
        weights[count++] = (byte)weight;
    }

    // The last symbol's weight is implied: it completes the code space to the next power of two.
    private void Build(ReadOnlySpan<byte> weights)
    {
        uint total = 0;
        foreach (byte weight in weights)
        {
            BinaryLayout.Require(weight <= MaximumCodeBits, "Zstandard Huffman weight exceeds the code length limit.");
            if (weight > 0)
                total += 1u << (weight - 1);
        }
        BinaryLayout.Require(total > 0, "Zstandard Huffman weights are all zero.");
        int maximumBits = BitOperations.Log2(total) + 1;
        uint rest = (1u << maximumBits) - total;
        BinaryLayout.Require(maximumBits <= MaximumCodeBits && BitOperations.IsPow2(rest), "Zstandard Huffman weights do not form a complete code.");
        int lastWeight = BitOperations.Log2(rest) + 1;
        Span<int> starts = stackalloc int[MaximumCodeBits + 2];
        for (int i = 0; i <= weights.Length; i++)
        {
            int weight = i < weights.Length ? weights[i] : lastWeight;
            if (weight > 0)
                starts[weight + 1] += 1 << (weight - 1);
        }
        for (int weight = 1; weight <= MaximumCodeBits + 1; weight++)
            starts[weight] += starts[weight - 1];
        for (int symbol = 0; symbol <= weights.Length; symbol++)
        {
            int weight = symbol < weights.Length ? weights[symbol] : lastWeight;
            if (weight == 0)
                continue;
            int span = 1 << (weight - 1);
            ushort entry = (ushort)(symbol | (maximumBits + 1 - weight) << 8);
            entries.AsSpan(starts[weight], span).Fill(entry);
            starts[weight] += span;
        }
        MaximumBits = maximumBits;
    }
}
