using System.Buffers.Binary;
using CommonFury.Binary;

namespace CommonFury.Compression;

/// <summary>Decompression of XMemCompress streams: a 48-byte big-endian header (magic 0x0FF512EE) followed by chunks that are
/// each an independent LZX stream of frames. Inputs are not retained.</summary>
/// <remarks>Window sizes from 32 KB to 2 MB, verbatim, aligned and uncompressed blocks, and E8 call translation are supported.
/// Decoding rejects malformed or truncated input, sizes that disagree with the header, non-zero bytes after a chunk's last frame
/// and output beyond the caller's limit.</remarks>
public static class XMemCompress
{
    private const uint Magic = 0x0FF512EE, Version = 0x01030000;
    private const int HeaderLength = 48;
    [ThreadStatic] private static LzxDecoder? cachedDecoder;

    /// <summary>Decodes a stream whose declared size is at most <paramref name="maximumLength"/> bytes.</summary>
    /// <exception cref="InvalidDataException">The input is malformed or declares more than the limit.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> compressed, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        long length = ReadHeader(compressed, out _);
        BinaryLayout.Require(length <= maximumLength, "Decoded XMemCompress output exceeds its size limit.");
        return DecompressExact(compressed, (int)length);
    }

    /// <summary>Decodes a stream that must declare and produce exactly <paramref name="length"/> bytes.</summary>
    /// <inheritdoc cref="Decompress(ReadOnlySpan{byte}, int)"/>
    public static byte[] DecompressExact(ReadOnlySpan<byte> compressed, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var result = new byte[length];
        DecompressExact(compressed, result);
        return result;
    }

    /// <summary>Decodes a stream into <paramref name="destination"/>, which its declared size must fill exactly.</summary>
    /// <inheritdoc cref="Decompress(ReadOnlySpan{byte}, int)"/>
    public static void DecompressExact(ReadOnlySpan<byte> compressed, Span<byte> destination)
    {
        long length = ReadHeader(compressed, out int windowBits);
        BinaryLayout.Require(length == destination.Length, "XMemCompress declared size does not match the destination.");
        var decoder = cachedDecoder ?? new LzxDecoder();
        cachedDecoder = null;
        try
        {
            int offset = HeaderLength, produced = 0;
            long stored = 0;
            while (offset < compressed.Length)
            {
                BinaryLayout.Require(produced < destination.Length, "XMemCompress stream has chunks beyond its declared size.");
                var size = BinaryLayout.Slice(compressed, offset, 4);
                int chunkLength = BinaryPrimitives.ReadInt32BigEndian(size);
                var chunk = BinaryLayout.Slice(compressed, offset + 4, chunkLength);
                produced += decoder.DecodeChunk(chunk, destination[produced..], windowBits);
                offset += 4 + chunkLength;
                stored += chunkLength;
            }
            // The header counts chunk bytes without their size prefixes.
            BinaryLayout.Require(stored == BinaryPrimitives.ReadInt64BigEndian(compressed[32..]), "XMemCompress compressed size does not match its header.");
            BinaryLayout.Require(produced == destination.Length, "XMemCompress output is shorter than its declared size.");
        }
        finally
        {
            cachedDecoder = decoder;
        }
    }

    private static long ReadHeader(ReadOnlySpan<byte> data, out int windowBits)
    {
        BinaryLayout.Require(data.Length >= HeaderLength, "Truncated XMemCompress header.");
        BinaryLayout.Require(BinaryPrimitives.ReadUInt32BigEndian(data) == Magic, "Data is not an XMemCompress stream.");
        BinaryLayout.Require(BinaryPrimitives.ReadUInt32BigEndian(data[4..]) == Version, "Unsupported XMemCompress version.");
        BinaryLayout.Require(BinaryPrimitives.ReadUInt64BigEndian(data[8..]) == 0, "XMemCompress header sets reserved fields.");
        uint window = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        BinaryLayout.Require(window is >= 1 << 15 and <= 1 << 21 && uint.IsPow2(window), "Unsupported XMemCompress window size.");
        windowBits = int.Log2((int)window);
        long length = BinaryPrimitives.ReadInt64BigEndian(data[24..]);
        BinaryLayout.Require(length is >= 0 and <= int.MaxValue, "XMemCompress declared size is out of range.");
        return length;
    }
}
