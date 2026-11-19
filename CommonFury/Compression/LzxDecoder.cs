using System.Buffers.Binary;
using CommonFury.Binary;

namespace CommonFury.Compression;

// Decodes one LZX stream (a run of frames, each with its own compressed size) into linear output. State starts fresh for every
// stream, so match offsets never reach before the stream's first byte.
internal sealed class LzxDecoder
{
    private const int MaxSlots = 50, MainSymbols = 256 + MaxSlots * 8, LengthSymbols = 249, PretreeSymbols = 20, AlignedSymbols = 8;
    private const int MaxFrameSize = 32768, VerbatimBlock = 1, AlignedBlock = 2, UncompressedBlock = 3;
    private static readonly int[] ExtraBits = new int[MaxSlots + 1], PositionBase = new int[MaxSlots + 1];

    private readonly byte[] mainLengths = new byte[MainSymbols], lengthLengths = new byte[LengthSymbols];
    private readonly byte[] pretreeLengths = new byte[PretreeSymbols], alignedLengths = new byte[AlignedSymbols];
    private readonly HuffmanTable main = new(MainSymbols, 12), length = new(LengthSymbols, 11);
    private readonly HuffmanTable pretree = new(PretreeSymbols, 6), aligned = new(AlignedSymbols, 7);
    private readonly List<int> frameSizes = [];

    static LzxDecoder()
    {
        for (int slot = 0, position = 0; slot <= MaxSlots; slot++)
        {
            ExtraBits[slot] = slot < 4 ? 0 : Math.Min((slot - 2) >> 1, 17);
            PositionBase[slot] = position;
            position += 1 << ExtraBits[slot];
        }
    }

    // Returns the bytes produced. Frame headers are 0xFF, a big-endian u16 frame size and a u16 compressed size, or just the
    // compressed size for a 32 KB frame; a zero size ends the stream and only zero bytes may follow it.
    public int DecodeChunk(ReadOnlySpan<byte> chunk, Span<byte> destination, int windowBits)
    {
        Array.Clear(mainLengths);
        Array.Clear(lengthLengths);
        frameSizes.Clear();
        int windowSize = 1 << windowBits, mainCount = 256 + 8 * (windowBits switch { 20 => 42, 21 => 50, _ => 2 * windowBits });
        uint r0 = 1, r1 = 1, r2 = 1;
        int blockType = 0, blockRemaining = 0, e8FileSize = 0;
        int offset = 0, position = 0, frameStart = 0;
        while (offset < chunk.Length)
        {
            int headerStart = offset, frameSize = MaxFrameSize, dataLength;
            if (chunk[offset] == 0xFF)
            {
                var header = BinaryLayout.Slice(chunk, offset, 5);
                frameSize = BinaryPrimitives.ReadUInt16BigEndian(header[1..]);
                dataLength = BinaryPrimitives.ReadUInt16BigEndian(header[3..]);
                offset += 5;
            }
            else
            {
                dataLength = BinaryPrimitives.ReadUInt16BigEndian(BinaryLayout.Slice(chunk, offset, 2));
                offset += 2;
            }
            if (frameSize == 0 || dataLength == 0)
            {
                BinaryLayout.Require(!chunk[headerStart..].ContainsAnyExcept((byte)0), "LZX stream has data after its end marker.");
                break;
            }
            BinaryLayout.Require(frameSize <= MaxFrameSize, "LZX frame exceeds 32 KB.");
            BinaryLayout.Require(frameSize <= destination.Length - frameStart, "LZX stream exceeds its declared size.");
            var reader = new BitReader(BinaryLayout.Slice(chunk, offset, dataLength));
            offset += dataLength;
            if (frameSizes.Count == 0 && reader.Read(1) != 0)
                e8FileSize = reader.Read(16) << 16 | reader.Read(16);
            int frameEnd = frameStart + frameSize;
            while (position < frameEnd)
            {
                if (blockRemaining == 0)
                {
                    // Unlike cabinet LZX, an odd-length uncompressed block is not followed by a padding byte.
                    blockType = reader.Read(3);
                    blockRemaining = reader.Read(24);
                    switch (blockType)
                    {
                        case AlignedBlock:
                            for (int i = 0; i < AlignedSymbols; i++)
                                alignedLengths[i] = (byte)reader.Read(3);
                            aligned.Build(alignedLengths);
                            goto case VerbatimBlock;
                        case VerbatimBlock:
                            ReadLengths(ref reader, mainLengths, 0, 256);
                            ReadLengths(ref reader, mainLengths, 256, mainCount);
                            main.Build(mainLengths.AsSpan(0, mainCount));
                            ReadLengths(ref reader, lengthLengths, 0, LengthSymbols);
                            length.Build(lengthLengths);
                            break;
                        case UncompressedBlock:
                            reader.AlignToBytes();
                            r0 = reader.ReadRawUInt32();
                            r1 = reader.ReadRawUInt32();
                            r2 = reader.ReadRawUInt32();
                            break;
                        default:
                            throw new InvalidDataException("LZX block has an invalid type.");
                    }
                    continue;
                }
                int blockEnd = position + blockRemaining, runEnd = Math.Min(blockEnd, frameEnd);
                if (blockType == UncompressedBlock)
                {
                    reader.ReadRaw(destination[position..runEnd]);
                    position = runEnd;
                }
                else
                {
                    int limit = Math.Min(blockEnd, destination.Length);
                    while (position < runEnd)
                    {
                        int symbol = main.Decode(ref reader);
                        if (symbol < 256)
                        {
                            destination[position++] = (byte)symbol;
                            continue;
                        }
                        symbol -= 256;
                        int matchLength = symbol & 7, slot = symbol >> 3;
                        if (matchLength == 7)
                            matchLength += length.Decode(ref reader);
                        matchLength += 2;
                        uint matchOffset;
                        switch (slot)
                        {
                            case 0:
                                matchOffset = r0;
                                break;
                            case 1:
                                matchOffset = r1;
                                r1 = r0;
                                r0 = matchOffset;
                                break;
                            case 2:
                                matchOffset = r2;
                                r2 = r0;
                                r0 = matchOffset;
                                break;
                            default:
                                int extra = ExtraBits[slot];
                                matchOffset = (uint)(PositionBase[slot] - 2);
                                if (blockType == AlignedBlock && extra >= 3)
                                    matchOffset += (uint)(reader.Read(extra - 3) << 3) + (uint)aligned.Decode(ref reader);
                                else
                                    matchOffset += (uint)reader.Read(extra);
                                r2 = r1;
                                r1 = r0;
                                r0 = matchOffset;
                                break;
                        }
                        BinaryLayout.Require(matchOffset <= (uint)Math.Min(position, windowSize), "LZX match reaches before the window.");
                        BinaryLayout.Require(matchLength <= limit - position, "LZX match runs past its block.");
                        CopyMatch(destination, position, (int)matchOffset, matchLength);
                        position += matchLength;
                    }
                }
                blockRemaining = blockEnd - position;
            }
            BinaryLayout.Require(!reader.Overrun, "Truncated LZX frame.");
            frameSizes.Add(frameSize);
            frameStart = frameEnd;
        }
        BinaryLayout.Require(position == frameStart, "LZX stream ends inside a match.");
        if (e8FileSize != 0)
            TranslateCalls(destination[..position], e8FileSize);
        return position;
    }

    // Path lengths are deltas from the previous block's (mod 17); 17 and 18 are runs of zeros and 19 a run of one value.
    private void ReadLengths(ref BitReader reader, byte[] lengths, int first, int last)
    {
        for (int i = 0; i < PretreeSymbols; i++)
            pretreeLengths[i] = (byte)reader.Read(4);
        pretree.Build(pretreeLengths);
        for (int x = first; x < last;)
        {
            int code = pretree.Decode(ref reader);
            if (code <= 16)
            {
                lengths[x] = (byte)((lengths[x] - code + 17) % 17);
                x++;
                continue;
            }
            int run = code switch { 17 => reader.Read(4) + 4, 18 => reader.Read(5) + 20, _ => reader.Read(1) + 4 };
            BinaryLayout.Require(run <= last - x, "LZX path lengths overrun their tree.");
            byte value = 0;
            if (code == 19)
            {
                code = pretree.Decode(ref reader);
                BinaryLayout.Require(code <= 16, "LZX path length run has an invalid value.");
                value = (byte)((lengths[x] - code + 17) % 17);
            }
            lengths.AsSpan(x, run).Fill(value);
            x += run;
        }
    }

    private static void CopyMatch(Span<byte> output, int position, int offset, int length)
    {
        int source = position - offset;
        if (offset >= length)
        {
            output.Slice(source, length).CopyTo(output[position..]);
            return;
        }
        for (int i = 0; i < length; i++)
            output[position + i] = output[source + i];
    }

    // Undoes the encoder's E8 call translation frame by frame: absolute targets inside the declared file size become relative.
    private void TranslateCalls(Span<byte> output, int fileSize)
    {
        int frameStart = 0;
        for (int frame = 0; frame < frameSizes.Count && frame < 32768; frame++)
        {
            int frameSize = frameSizes[frame];
            var data = output.Slice(frameStart, Math.Min(frameSize, output.Length - frameStart));
            for (int i = 0; i < data.Length - 10;)
            {
                if (data[i] != 0xE8)
                {
                    i++;
                    continue;
                }
                int current = frameStart + i, target = BinaryPrimitives.ReadInt32LittleEndian(data[(i + 1)..]);
                if (target >= -current && target < fileSize)
                    BinaryPrimitives.WriteInt32LittleEndian(data[(i + 1)..], target >= 0 ? target - current : target + fileSize);
                i += 5;
            }
            frameStart += frameSize;
        }
    }

    // 16-bit little-endian words read most significant bit first. Bits past the end read as zero; Overrun reports them.
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int position;
        private ulong buffer;
        private int count;

        public readonly bool Overrun => (long)position * 8 - count > (long)data.Length * 8;

        public uint Peek16()
        {
            if (count < 16)
                Fill();
            return (uint)(buffer >> 48);
        }

        public void Skip(int bits)
        {
            buffer <<= bits;
            count -= bits;
        }

        public int Read(int bits)
        {
            if (bits == 0)
                return 0;
            if (count < bits)
                Fill();
            int value = (int)(buffer >> (64 - bits));
            Skip(bits);
            return value;
        }

        // Uncompressed blocks start at the next word boundary, a whole word later when the bits are already aligned.
        public void AlignToBytes()
        {
            long consumed = (long)position * 8 - count;
            position = checked((int)((consumed / 16 + 1) * 2));
            buffer = 0;
            count = 0;
        }

        public void ReadRaw(Span<byte> target)
        {
            BinaryLayout.Slice(data, position, target.Length).CopyTo(target);
            position += target.Length;
        }

        public uint ReadRawUInt32()
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(BinaryLayout.Slice(data, position, 4));
            position += 4;
            return value;
        }

        private void Fill()
        {
            while (count <= 48)
            {
                uint word = position + 1 < data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data[position..])
                    : position < data.Length ? data[position] : 0u;
                position += 2;
                buffer |= (ulong)word << (48 - count);
                count += 16;
            }
        }
    }

    // Canonical Huffman codes of up to 16 bits: a direct table for short codes, then a search by length.
    private sealed class HuffmanTable(int maxSymbols, int tableBits)
    {
        private readonly int[] table = new int[1 << tableBits];
        private readonly ushort[] symbols = new ushort[maxSymbols];
        private readonly int[] counts = new int[17], firstCode = new int[17], firstIndex = new int[17];

        public void Build(ReadOnlySpan<byte> lengths)
        {
            Array.Clear(counts);
            foreach (byte length in lengths)
                counts[length]++;
            counts[0] = 0;
            for (int length = 1, code = 0, index = 0; length <= 16; length++)
            {
                code = (code + counts[length - 1]) << 1;
                BinaryLayout.Require(code + counts[length] <= 1 << length, "LZX Huffman tree is oversubscribed.");
                firstCode[length] = code;
                firstIndex[length] = index;
                index += counts[length];
            }
            Span<int> next = stackalloc int[17];
            firstIndex.CopyTo(next);
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                if (lengths[symbol] != 0)
                    symbols[next[lengths[symbol]]++] = (ushort)symbol;
            }
            Array.Clear(table);
            for (int length = 1; length <= tableBits; length++)
            {
                for (int i = 0; i < counts[length]; i++)
                {
                    int shift = tableBits - length, start = (firstCode[length] + i) << shift;
                    table.AsSpan(start, 1 << shift).Fill(symbols[firstIndex[length] + i] << 5 | length);
                }
            }
        }

        public int Decode(ref BitReader reader)
        {
            uint bits = reader.Peek16();
            int entry = table[bits >> (16 - tableBits)];
            if (entry != 0)
            {
                reader.Skip(entry & 31);
                return entry >> 5;
            }
            for (int length = tableBits + 1; length <= 16; length++)
            {
                int index = (int)(bits >> (16 - length)) - firstCode[length];
                if ((uint)index < (uint)counts[length])
                {
                    reader.Skip(length);
                    return symbols[firstIndex[length] + index];
                }
            }
            throw new InvalidDataException("LZX data holds an invalid Huffman code.");
        }
    }
}
