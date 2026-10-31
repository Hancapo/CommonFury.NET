using System.Buffers.Binary;
using CommonFury.Binary;
using CommonFury.Hashing;

namespace CommonFury.Compression;

/// <summary>A Zstandard frame header (RFC 8878).</summary>
/// <param name="ContentSize">Declared decompressed size, or null when the frame omits it.</param>
/// <param name="WindowSize">Bytes of history that offsets may reach, which bounds decoding memory for streams.</param>
/// <param name="DictionaryId">Dictionary the frame was compressed with, or zero when unspecified.</param>
/// <param name="HasChecksum">Whether the frame ends with the low 32 bits of the XXH64 of its content.</param>
public readonly record struct ZstandardFrameHeader(long? ContentSize, long WindowSize, uint DictionaryId, bool HasChecksum);

/// <summary>Zstandard decompression (RFC 8878): concatenated and skippable frames, every block and entropy mode, dictionaries and
/// content checksums. Inputs are not retained.</summary>
/// <remarks>Decoding rejects malformed or truncated input, trailing bytes that are not a frame, output beyond the caller's limit,
/// a declared content size that does not match, and checksum mismatches. Offsets must stay inside the frame window; dictionary
/// content is addressable while a frame's output is within its window. A frame that names a dictionary ID requires that dictionary;
/// a frame without an ID uses the supplied dictionary, if any.</remarks>
public static class Zstandard
{
    private const uint FrameMagic = 0xFD2FB528, SkippableMagic = 0x184D2A50, SkippableMask = 0xFFFFFFF0;
    [ThreadStatic] private static ZstandardDecoder? cachedDecoder;

    /// <summary>Decodes every frame, producing at most <paramref name="maximumLength"/> bytes.</summary>
    /// <exception cref="InvalidDataException">The input is malformed, needs a different dictionary, or decodes to more than the limit.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> compressed, int maximumLength, ZstandardDictionary? dictionary = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        var output = new Output(maximumLength, default, growable: true);
        Decode(compressed, ref output, dictionary);
        return output.ToArray();
    }

    /// <summary>Decodes every frame, which must produce exactly <paramref name="length"/> bytes.</summary>
    /// <inheritdoc cref="Decompress(ReadOnlySpan{byte}, int, ZstandardDictionary?)"/>
    public static byte[] DecompressExact(ReadOnlySpan<byte> compressed, int length, ZstandardDictionary? dictionary = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var result = new byte[length];
        DecompressExact(compressed, result, dictionary);
        return result;
    }

    /// <summary>Decodes every frame into <paramref name="destination"/>, which the output must fill exactly.</summary>
    /// <inheritdoc cref="Decompress(ReadOnlySpan{byte}, int, ZstandardDictionary?)"/>
    public static void DecompressExact(ReadOnlySpan<byte> compressed, Span<byte> destination, ZstandardDictionary? dictionary = null)
    {
        var output = new Output(destination.Length, destination, growable: false);
        Decode(compressed, ref output, dictionary);
        BinaryLayout.Require(output.Length == destination.Length, "Zstandard output is shorter than its declared size.");
    }

    /// <summary>Returns a read-only stream that decodes every frame from <paramref name="source"/>, producing at most
    /// <paramref name="maximumLength"/> bytes. Memory grows with the frame window, never beyond the output produced.</summary>
    /// <remarks>Reads throw <see cref="InvalidDataException"/> for the same conditions as <see cref="Decompress"/>. Bytes returned
    /// before a frame's checksum is checked are unverified; the check runs before the frame's last bytes are returned.</remarks>
    public static Stream CreateDecompressionStream(Stream source, long maximumLength, ZstandardDictionary? dictionary = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        if (!source.CanRead)
            throw new ArgumentException("The source stream is not readable.", nameof(source));
        return new ZstandardStream(source, maximumLength, dictionary, leaveOpen);
    }

    /// <summary>Reads the header of the first frame, after any skippable frames.</summary>
    /// <exception cref="InvalidDataException">No complete frame header is present.</exception>
    public static ZstandardFrameHeader ReadFrameHeader(ReadOnlySpan<byte> data)
    {
        while (TrySkippable(data, out int skipped))
            data = data[skipped..];
        return ReadHeader(data, out _);
    }

    internal static bool TrySkippable(ReadOnlySpan<byte> data, out int length)
    {
        length = 0;
        if (data.Length < 4 || (BinaryPrimitives.ReadUInt32LittleEndian(data) & SkippableMask) != SkippableMagic)
            return false;
        BinaryLayout.Require(data.Length >= 8, "Truncated Zstandard skippable frame.");
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        BinaryLayout.Require(size <= data.Length - 8, "Truncated Zstandard skippable frame.");
        length = 8 + (int)size;
        return true;
    }

    internal static bool IsSkippable(uint magic) => (magic & SkippableMask) == SkippableMagic;

    // Bytes in a frame header given its descriptor byte, magic included.
    internal static int HeaderLength(byte descriptor)
    {
        int contentSizeFlag = descriptor >> 6;
        bool singleSegment = (descriptor & 0x20) != 0;
        int contentSizeBytes = contentSizeFlag == 0 ? (singleSegment ? 1 : 0) : 1 << contentSizeFlag;
        return 5 + (singleSegment ? 0 : 1) + (descriptor & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 } + contentSizeBytes;
    }

    internal static ZstandardFrameHeader ReadHeader(ReadOnlySpan<byte> data, out int length)
    {
        BinaryLayout.Require(data.Length >= 5, "Truncated Zstandard frame header.");
        BinaryLayout.Require(BinaryPrimitives.ReadUInt32LittleEndian(data) == FrameMagic, "Data is not a Zstandard frame.");
        byte descriptor = data[4];
        BinaryLayout.Require((descriptor & 0x08) == 0, "Zstandard frame header sets a reserved bit.");
        length = HeaderLength(descriptor);
        BinaryLayout.Require(data.Length >= length, "Truncated Zstandard frame header.");
        bool singleSegment = (descriptor & 0x20) != 0;
        int offset = 5;
        long window = 0;
        if (!singleSegment)
        {
            int exponent = data[offset] >> 3, mantissa = data[offset++] & 7;
            long windowBase = 1L << (10 + exponent);
            window = windowBase + windowBase / 8 * mantissa;
        }
        int dictionaryBytes = (descriptor & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
        uint dictionaryId = 0;
        for (int i = 0; i < dictionaryBytes; i++)
            dictionaryId |= (uint)data[offset++] << (8 * i);
        long? contentSize = null;
        switch (length - offset)
        {
            case 1: contentSize = data[offset]; break;
            case 2: contentSize = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) + 256; break;
            case 4: contentSize = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]); break;
            case 8:
                ulong size = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
                BinaryLayout.Require(size <= long.MaxValue, "Zstandard content size exceeds the supported range.");
                contentSize = (long)size;
                break;
        }
        return new(contentSize, singleSegment ? contentSize!.Value : window, dictionaryId, (descriptor & 0x04) != 0);
    }

    // Returns the last-block flag, the block type, the decoded size for raw and RLE blocks, and the stored byte count.
    internal static (bool Last, int Type, int Size, int Stored) ReadBlockHeader(ReadOnlySpan<byte> data, int blockMaximum)
    {
        BinaryLayout.Require(data.Length >= 3, "Truncated Zstandard block header.");
        int header = data[0] | data[1] << 8 | data[2] << 16, type = header >> 1 & 3, size = header >> 3;
        BinaryLayout.Require(type != 3, "Zstandard block uses the reserved block type.");
        BinaryLayout.Require(size <= blockMaximum, "Zstandard block exceeds its maximum size.");
        return ((header & 1) != 0, type, size, type == 1 ? 1 : size);
    }

    internal static void CheckFrameEnd(ZstandardFrameHeader header, long produced, XxHash64? hash, ReadOnlySpan<byte> checksum)
    {
        BinaryLayout.Require(header.ContentSize is not { } size || size == produced, "Zstandard frame content size does not match its header.");
        if (hash is not null)
            BinaryLayout.Require(BinaryPrimitives.ReadUInt32LittleEndian(checksum) == (uint)hash.GetCurrentHash(), "Zstandard content checksum mismatch.");
    }

    internal static ZstandardDecoder RentDecoder()
    {
        var decoder = cachedDecoder ?? new ZstandardDecoder();
        cachedDecoder = null;
        return decoder;
    }

    internal static void ReturnDecoder(ZstandardDecoder decoder)
    {
        decoder.Release();
        cachedDecoder = decoder;
    }

    private static void Decode(ReadOnlySpan<byte> input, ref Output output, ZstandardDictionary? dictionary)
    {
        BinaryLayout.Require(!input.IsEmpty, "Missing Zstandard frame.");
        var decoder = RentDecoder();
        XxHash64? reusableHash = null;
        try
        {
            while (!input.IsEmpty)
            {
                if (TrySkippable(input, out int skipped))
                {
                    input = input[skipped..];
                    continue;
                }
                var header = ReadHeader(input, out int offset);
                decoder.BeginFrame(header, dictionary);
                XxHash64? hash = header.HasChecksum ? reusableHash ??= new XxHash64() : null;
                hash?.Reset();
                int frameStart = output.Length;
                // A declared size bounds the frame's output, so a frame that would exceed it fails while decoding.
                int frameLimit = output.Limit;
                if (header.ContentSize is { } declared)
                {
                    BinaryLayout.Require(declared <= output.Limit - frameStart, "Decoded Zstandard output exceeds its size limit.");
                    frameLimit = frameStart + (int)declared;
                    output.Reserve(frameLimit);
                }
                bool last;
                do
                {
                    var block = ReadBlockHeader(input[offset..], decoder.BlockMaximum);
                    var stored = BinaryLayout.Slice(input, offset + 3, block.Stored);
                    offset += 3 + block.Stored;
                    int start = output.Length, written;
                    if (block.Type == 2)
                    {
                        output.Reserve((int)Math.Min(frameLimit, (long)start + decoder.BlockMaximum));
                        var frame = output.Span[frameStart..Math.Min(output.Span.Length, frameLimit)];
                        written = decoder.DecodeBlock(stored, frame, start - frameStart, start - frameStart);
                    }
                    else
                    {
                        BinaryLayout.Require(block.Size <= frameLimit - start, "Decoded Zstandard output exceeds its size limit.");
                        output.Reserve(start + block.Size);
                        var target = output.Span.Slice(start, block.Size);
                        if (block.Type == 0)
                            stored.CopyTo(target);
                        else
                            target.Fill(stored[0]);
                        written = block.Size;
                    }
                    output.Length += written;
                    hash?.Append(output.Span.Slice(start, written));
                    last = block.Last;
                }
                while (!last);
                int checksumLength = header.HasChecksum ? 4 : 0;
                CheckFrameEnd(header, output.Length - frameStart, hash, BinaryLayout.Slice(input, offset, checksumLength));
                input = input[(offset + checksumLength)..];
            }
        }
        finally
        {
            ReturnDecoder(decoder);
        }
    }

    // Output of the span decoder: caller storage, or an array that grows up to the limit.
    private ref struct Output(int limit, Span<byte> storage, bool growable)
    {
        private byte[] array = [];
        public Span<byte> Span = storage;
        public int Length;
        public readonly int Limit => limit;

        // Ensures the span holds at least `end` bytes; callers never pass more than the limit.
        public void Reserve(int end)
        {
            if (end <= Span.Length || !growable)
                return;
            Array.Resize(ref array, (int)Math.Min(limit, Math.Max(end, 2L * array.Length)));
            Span = array;
        }

        public readonly byte[] ToArray() => Length == array.Length ? array : array.AsSpan(0, Length).ToArray();
    }
}
