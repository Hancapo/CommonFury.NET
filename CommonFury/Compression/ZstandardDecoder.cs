using System.Buffers.Binary;
using CommonFury.Binary;

namespace CommonFury.Compression;

// Entropy and repeat-offset state of one frame, plus compressed-block decoding. Raw and RLE blocks need none of it.
internal sealed class ZstandardDecoder
{
    public const int MaximumBlockSize = 128 * 1024;
    private const int MaximumLiteralLengthCode = 35, MaximumMatchLengthCode = 52, MaximumOffsetCode = 31;

    private static readonly uint[] LiteralLengthBases =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024,
        2048, 4096, 8192, 16384, 32768, 65536
    ];
    private static readonly byte[] LiteralLengthBits =
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private static readonly uint[] MatchLengthBases =
    [
        3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
        35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539
    ];
    private static readonly byte[] MatchLengthBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16
    ];
    private static readonly uint[] OffsetBases = [.. Enumerable.Range(0, MaximumOffsetCode + 1).Select(code => 1u << code)];
    private static readonly byte[] OffsetBits = [.. Enumerable.Range(0, MaximumOffsetCode + 1).Select(code => (byte)code)];

    private static readonly FseTable PredefinedLiteralLengths = Predefined(6, LiteralLengthBases, LiteralLengthBits,
        [4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1]);
    private static readonly FseTable PredefinedMatchLengths = Predefined(6, MatchLengthBases, MatchLengthBits,
    [
        1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1
    ]);
    private static readonly FseTable PredefinedOffsets = Predefined(5, OffsetBases, OffsetBits,
        [1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1]);

    private readonly HuffmanTable huffman = new();
    private readonly FseTable literalLengths = new(9), offsets = new(8), matchLengths = new(9);
    private readonly byte[] literalBuffer = new byte[MaximumBlockSize];
    private HuffmanTable? activeHuffman;
    private FseTable? activeLiteralLengths, activeOffsets, activeMatchLengths;
    private byte[] dictionaryContent = [];
    private long windowSize;
    private uint repeat1, repeat2, repeat3;

    public int BlockMaximum { get; private set; }

    public void BeginFrame(ZstandardFrameHeader header, ZstandardDictionary? dictionary)
    {
        if (header.DictionaryId != 0)
        {
            BinaryLayout.Require(dictionary is not null, $"Zstandard frame requires dictionary {header.DictionaryId}.");
            BinaryLayout.Require(dictionary!.Id == header.DictionaryId,
                $"Zstandard frame requires dictionary {header.DictionaryId}, not {dictionary.Id}.");
        }
        windowSize = header.WindowSize;
        BlockMaximum = (int)Math.Min(MaximumBlockSize, header.WindowSize);
        dictionaryContent = dictionary?.Content ?? [];
        activeHuffman = dictionary?.Huffman;
        activeLiteralLengths = dictionary?.LiteralLengths;
        activeOffsets = dictionary?.Offsets;
        activeMatchLengths = dictionary?.MatchLengths;
        (repeat1, repeat2, repeat3) = dictionary?.RepeatOffsets ?? (1, 4, 8);
    }

    // Drops references to the last dictionary so a cached decoder does not keep it alive.
    public void Release()
    {
        dictionaryContent = [];
        activeHuffman = null;
        activeLiteralLengths = activeOffsets = activeMatchLengths = null;
    }

    /// Decodes one compressed block into output at position. output[..position] holds the latest frame bytes and
    /// frameProduced counts every frame byte before this block. Returns the bytes written.
    public int DecodeBlock(ReadOnlySpan<byte> block, Span<byte> output, int position, long frameProduced)
    {
        ReadOnlySpan<byte> literals = ReadLiterals(block, out int used);
        return ExecuteSequences(block[used..], literals, output, position, frameProduced);
    }

    public static void ReadDictionaryTables(ReadOnlySpan<byte> data, out HuffmanTable huffman, out FseTable offsets,
        out FseTable matchLengths, out FseTable literalLengths, out int length)
    {
        huffman = new HuffmanTable();
        offsets = new FseTable(8);
        matchLengths = new FseTable(9);
        literalLengths = new FseTable(9);
        length = huffman.Read(data);
        length += offsets.Read(data[length..], 8, MaximumOffsetCode, OffsetBases, OffsetBits);
        length += matchLengths.Read(data[length..], 9, MaximumMatchLengthCode, MatchLengthBases, MatchLengthBits);
        length += literalLengths.Read(data[length..], 9, MaximumLiteralLengthCode, LiteralLengthBases, LiteralLengthBits);
    }

    private ReadOnlySpan<byte> ReadLiterals(ReadOnlySpan<byte> block, out int used)
    {
        BinaryLayout.Require(!block.IsEmpty, "Missing Zstandard literals section.");
        int type = block[0] & 3, sizeFormat = block[0] >> 2 & 3;
        if (type < 2)
        {
            int headerLength = sizeFormat switch { 1 => 2, 3 => 3, _ => 1 };
            BinaryLayout.Require(block.Length >= headerLength, "Truncated Zstandard literals header.");
            int size = headerLength switch
            {
                1 => block[0] >> 3,
                2 => block[0] >> 4 | block[1] << 4,
                _ => block[0] >> 4 | block[1] << 4 | block[2] << 12
            };
            BinaryLayout.Require(size <= BlockMaximum, "Zstandard literals exceed the block size limit.");
            if (type == 0)
            {
                used = headerLength + size;
                return BinaryLayout.Slice(block, headerLength, size);
            }
            BinaryLayout.Require(block.Length > headerLength, "Truncated Zstandard RLE literals.");
            used = headerLength + 1;
            literalBuffer.AsSpan(0, size).Fill(block[headerLength]);
            return literalBuffer.AsSpan(0, size);
        }

        int length = sizeFormat < 2 ? 3 : sizeFormat + 2, sizeBits = sizeFormat < 2 ? 10 : sizeFormat * 4 + 6;
        BinaryLayout.Require(block.Length >= length, "Truncated Zstandard literals header.");
        ulong header = 0;
        for (int i = 0; i < length; i++)
            header |= (ulong)block[i] << (8 * i);
        int regenerated = (int)(header >> 4) & ((1 << sizeBits) - 1), compressedSize = (int)(header >> (4 + sizeBits)) & ((1 << sizeBits) - 1);
        BinaryLayout.Require(regenerated <= BlockMaximum, "Zstandard literals exceed the block size limit.");
        var data = BinaryLayout.Slice(block, length, compressedSize);
        used = length + compressedSize;
        if (type == 2)
        {
            data = data[huffman.Read(data)..];
            activeHuffman = huffman;
        }
        BinaryLayout.Require(activeHuffman is not null, "Zstandard treeless literals have no previous Huffman table.");
        var destination = literalBuffer.AsSpan(0, regenerated);
        if (sizeFormat == 0)
        {
            activeHuffman!.Decode(data, destination);
            return destination;
        }
        BinaryLayout.Require(data.Length >= 6, "Truncated Zstandard literal jump table.");
        int first = BinaryPrimitives.ReadUInt16LittleEndian(data), second = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        int third = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]), segment = (regenerated + 3) / 4;
        BinaryLayout.Require(first + second + third <= data.Length - 6, "Zstandard literal streams exceed their section.");
        BinaryLayout.Require(3 * segment <= regenerated, "Zstandard literals are too short for four streams.");
        var streams = data[6..];
        activeHuffman!.Decode(streams[..first], destination[..segment]);
        activeHuffman.Decode(streams.Slice(first, second), destination.Slice(segment, segment));
        activeHuffman.Decode(streams.Slice(first + second, third), destination.Slice(2 * segment, segment));
        activeHuffman.Decode(streams[(first + second + third)..], destination[(3 * segment)..]);
        return destination;
    }

    private int ExecuteSequences(ReadOnlySpan<byte> section, ReadOnlySpan<byte> literals, Span<byte> output, int position, long frameProduced)
    {
        BinaryLayout.Require(!section.IsEmpty, "Missing Zstandard sequences section.");
        int count = section[0], headerLength = 1, end = Math.Min(output.Length, checked(position + BlockMaximum));
        if (count == 0)
        {
            BinaryLayout.Require(section.Length == 1, "Trailing data after an empty Zstandard sequences section.");
            CopyLiterals(literals, output, position, end);
            return literals.Length;
        }
        if (count >= 128)
        {
            headerLength = count == 255 ? 3 : 2;
            BinaryLayout.Require(section.Length > headerLength, "Truncated Zstandard sequences header.");
            count = count == 255 ? section[1] + (section[2] << 8) + 0x7F00 : (count - 128 << 8) + section[1];
        }
        BinaryLayout.Require(section.Length > headerLength, "Truncated Zstandard sequences header.");
        int modes = section[headerLength];
        BinaryLayout.Require((modes & 3) == 0, "Zstandard sequence compression modes use reserved bits.");
        int offset = headerLength + 1;
        var literalLengthTable = SelectTable(modes >> 6, section, ref offset, literalLengths, PredefinedLiteralLengths,
            ref activeLiteralLengths, 9, MaximumLiteralLengthCode, LiteralLengthBases, LiteralLengthBits);
        var offsetTable = SelectTable(modes >> 4 & 3, section, ref offset, offsets, PredefinedOffsets,
            ref activeOffsets, 8, MaximumOffsetCode, OffsetBases, OffsetBits);
        var matchLengthTable = SelectTable(modes >> 2 & 3, section, ref offset, matchLengths, PredefinedMatchLengths,
            ref activeMatchLengths, 9, MaximumMatchLengthCode, MatchLengthBases, MatchLengthBits);

        var reader = new BackwardBitReader(section[offset..]);
        FseEntry[] literalLengthEntries = literalLengthTable.Entries, offsetEntries = offsetTable.Entries, matchLengthEntries = matchLengthTable.Entries;
        uint literalLengthState = reader.Read(literalLengthTable.AccuracyLog);
        uint offsetState = reader.Read(offsetTable.AccuracyLog);
        uint matchLengthState = reader.Read(matchLengthTable.AccuracyLog);
        int written = position, literalPosition = 0;
        for (int sequence = 0; sequence < count; sequence++)
        {
            FseEntry literalLengthEntry = literalLengthEntries[literalLengthState], offsetEntry = offsetEntries[offsetState];
            FseEntry matchLengthEntry = matchLengthEntries[matchLengthState];
            long offsetValue = offsetEntry.Base + reader.Read(offsetEntry.ExtraBits);
            int matchLength = (int)(matchLengthEntry.Base + reader.Read(matchLengthEntry.ExtraBits));
            int literalLength = (int)(literalLengthEntry.Base + reader.Read(literalLengthEntry.ExtraBits));
            long distance = ResolveOffset(offsetValue, literalLength);
            if (sequence + 1 < count)
            {
                literalLengthState = literalLengthEntry.NextState + reader.Read(literalLengthEntry.StateBits);
                matchLengthState = matchLengthEntry.NextState + reader.Read(matchLengthEntry.StateBits);
                offsetState = offsetEntry.NextState + reader.Read(offsetEntry.StateBits);
            }

            BinaryLayout.Require(literalLength <= literals.Length - literalPosition, "Zstandard sequences use more literals than the block holds.");
            BinaryLayout.Require(literalLength + matchLength <= end - written, "Decoded Zstandard output exceeds its size limit.");
            literals.Slice(literalPosition, literalLength).CopyTo(output[written..]);
            literalPosition += literalLength;
            written += literalLength;
            CopyMatch(output, written, distance, matchLength, frameProduced + (written - position));
            written += matchLength;
        }
        BinaryLayout.Require(reader.IsComplete, "Zstandard sequence bitstream length does not match its sequences.");
        CopyLiterals(literals[literalPosition..], output, written, end);
        return written + literals.Length - literalPosition - position;
    }

    private static void CopyLiterals(ReadOnlySpan<byte> literals, Span<byte> output, int written, int end)
    {
        BinaryLayout.Require(literals.Length <= end - written, "Decoded Zstandard output exceeds its size limit.");
        literals.CopyTo(output[written..]);
    }

    private FseTable SelectTable(int mode, ReadOnlySpan<byte> section, ref int offset, FseTable owned, FseTable predefined,
        ref FseTable? active, int maximumAccuracyLog, int maximumSymbol, uint[] bases, byte[] extraBits)
    {
        switch (mode)
        {
            case 0:
                active = predefined;
                break;
            case 1:
                BinaryLayout.Require(offset < section.Length, "Truncated Zstandard RLE sequence code.");
                owned.BuildRle(section[offset++], maximumSymbol, bases, extraBits);
                active = owned;
                break;
            case 2:
                offset += owned.Read(section[offset..], maximumAccuracyLog, maximumSymbol, bases, extraBits);
                active = owned;
                break;
            default:
                BinaryLayout.Require(active is not null, "Zstandard repeated sequence table has no previous table.");
                break;
        }
        return active!;
    }

    private long ResolveOffset(long value, int literalLength)
    {
        if (value > 3)
        {
            repeat3 = repeat2;
            repeat2 = repeat1;
            repeat1 = (uint)(value - 3);
            return repeat1;
        }
        int index = (int)value + (literalLength == 0 ? 1 : 0);
        uint distance = index switch { 1 => repeat1, 2 => repeat2, 3 => repeat3, _ => repeat1 - 1 };
        BinaryLayout.Require(distance != 0, "Zstandard repeat offset is zero.");
        if (index == 1)
            return distance;
        if (index != 2)
            repeat3 = repeat2;
        repeat2 = repeat1;
        repeat1 = distance;
        return distance;
    }

    // produced counts frame bytes before the match. Offsets beyond them reach into the dictionary, which stays
    // addressable only within the first window of the frame; other offsets must lie within the window.
    private void CopyMatch(Span<byte> output, int written, long distance, int length, long produced)
    {
        if (distance > produced)
        {
            long intoDictionary = distance - produced;
            BinaryLayout.Require(intoDictionary <= dictionaryContent.Length && produced <= windowSize,
                "Zstandard offset reaches before the start of its frame.");
            int fromDictionary = (int)Math.Min(intoDictionary, length);
            dictionaryContent.AsSpan(dictionaryContent.Length - (int)intoDictionary, fromDictionary).CopyTo(output[written..]);
            written += fromDictionary;
            length -= fromDictionary;
            if (length == 0)
                return;
            // The rest of the match continues from the first frame byte.
            int frameStart = checked(written - fromDictionary - (int)produced);
            BinaryLayout.Require(frameStart >= 0, "Zstandard offset reaches outside the retained window.");
            CopyOverlapping(output, frameStart, written, length);
            return;
        }
        BinaryLayout.Require(distance <= windowSize, "Zstandard offset exceeds the frame window.");
        BinaryLayout.Require(distance <= written, "Zstandard offset reaches outside the retained window.");
        CopyOverlapping(output, written - (int)distance, written, length);
    }

    // Copies forward so a source overlapping its destination repeats, as LZ77 matches require.
    private static void CopyOverlapping(Span<byte> output, int source, int destination, int length)
    {
        int distance = destination - source;
        if (distance >= length)
        {
            output.Slice(source, length).CopyTo(output.Slice(destination, length));
            return;
        }
        if (distance == 1)
        {
            output.Slice(destination, length).Fill(output[source]);
            return;
        }
        // Each pass doubles the repeated run that already precedes the destination.
        while (length > 0)
        {
            int count = Math.Min(destination - source, length);
            output.Slice(source, count).CopyTo(output.Slice(destination, count));
            destination += count;
            length -= count;
        }
    }

    private static FseTable Predefined(int accuracyLog, uint[] bases, byte[] extraBits, short[] counts)
    {
        var table = new FseTable(accuracyLog);
        table.Build(counts, accuracyLog, bases, extraBits);
        return table;
    }
}
