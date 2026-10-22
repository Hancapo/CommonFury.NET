using System.IO.Compression;
using CommonFury.Binary;

namespace CommonFury.Compression;

/// <summary>Strictly framed raw DEFLATE (RFC 1951, no zlib or gzip header). Decoding rejects truncation, bytes after the
/// final block and output beyond the stated size. Inputs are copied, not retained.</summary>
/// <remarks><c>zeroHistory</c> seeds a 32 KiB window of zeros, so back-references before the first byte decode as zeros instead
/// of failing. RSC7 resources need it; RSC8 resources do not. Streams valid without it decode identically.</remarks>
public static class RawDeflate
{
    private const int HistorySize = 32 * 1024;
    // A non-final stored block seeds the raw decoder's window without changing the resource bytes.
    private static readonly byte[] HistoryBlock = [0x00, 0x00, 0x80, 0xFF, 0x7F, .. new byte[HistorySize]];

    /// <summary>Decodes at most <paramref name="maximumLength"/> bytes.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> compressed, int maximumLength, bool zeroHistory = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        BinaryLayout.Require(!compressed.IsEmpty, "Missing DEFLATE stream.");
        if (CompressionCache.Recall(compressed) is { } remembered && remembered.Length <= maximumLength)
            return remembered;
        using var input = new StrictInput(compressed.ToArray(), zeroHistory);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        if (zeroHistory)
            SkipHistory(inflater);
        using var output = new MemoryStream(Math.Min(maximumLength, 64 * 1024));
        var buffer = new byte[(int)Math.Clamp((long)maximumLength + 1, 1, 64 * 1024)];
        while (true)
        {
            int count = inflater.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, maximumLength - output.Length + 1)));
            BinaryLayout.Require(count <= maximumLength - output.Length, "Decoded DEFLATE output exceeds its size limit.");
            if (count == 0)
                break;
            output.Write(buffer, 0, count);
        }
        BinaryLayout.Require(input.HasReadFinalByte, "Trailing data after the DEFLATE stream.");
        return output.ToArray();
    }

    /// <summary>Decodes exactly <paramref name="length"/> bytes.</summary>
    public static byte[] DecompressExact(ReadOnlySpan<byte> compressed, int length, bool zeroHistory = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (CompressionCache.Recall(compressed) is { } remembered && remembered.Length == length)
            return remembered;
        var output = new byte[length];
        DecompressExact(compressed, output, zeroHistory);
        return output;
    }

    /// <summary>Decodes exactly enough bytes to fill <paramref name="destination"/>.</summary>
    public static void DecompressExact(ReadOnlySpan<byte> compressed, Span<byte> destination, bool zeroHistory = false)
    {
        BinaryLayout.Require(!compressed.IsEmpty, "Missing DEFLATE stream.");
        using var input = new StrictInput(compressed.ToArray(), zeroHistory);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        if (zeroHistory)
            SkipHistory(inflater);
        int count = inflater.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false);
        BinaryLayout.Require(count == destination.Length, "DEFLATE output is shorter than its declared size.");
        Span<byte> probe = stackalloc byte[1];
        BinaryLayout.Require(inflater.Read(probe) == 0, "DEFLATE output exceeds its declared size.");
        BinaryLayout.Require(input.HasReadFinalByte, "Trailing data after the DEFLATE stream.");
    }

    /// <summary>Empty input produces the two-byte empty final block resource readers require.
    /// <see cref="CompressionLevel.Optimal"/> output may come from <see cref="CompressionCache"/>.</summary>
    public static void Compress(Stream destination, ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Optimal)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (data.IsEmpty)
        {
            // DeflateStream may emit no bytes for an empty input.
            destination.Write([0x03, 0x00]);
            return;
        }
        if (level == CompressionLevel.Optimal && CompressionCache.IsEnabled)
        {
            byte[] input = data.ToArray();
            destination.Write(CompressionCache.Deflate(input, () =>
            {
                using var buffer = new MemoryStream();
                Deflate(buffer, input, level);
                return buffer.ToArray();
            }));
            return;
        }
        Deflate(destination, data, level);
    }

    /// <inheritdoc cref="Compress(Stream, ReadOnlySpan{byte}, CompressionLevel)"/>
    public static byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream();
        Compress(output, data, level);
        return output.ToArray();
    }

    private static void Deflate(Stream destination, ReadOnlySpan<byte> data, CompressionLevel level)
    {
        using var deflater = new DeflateStream(destination, level, leaveOpen: true);
        deflater.Write(data);
    }

    private static void SkipHistory(DeflateStream inflater)
    {
        var history = new byte[HistorySize];
        inflater.ReadExactly(history);
    }

    // DeflateStream buffers input and exposes neither its terminal state nor unused bytes.
    // Withhold the last byte until a separate read: a decoder that finishes earlier has trailing data,
    // even if that data fits in its read-ahead buffer. A request beyond the last byte means truncation.
    // This keeps bulk reads and strict framing without reflection or process-wide AppContext switches.
    private sealed class StrictInput(byte[] compressed, bool zeroHistory) : Stream
    {
        private int historyPosition = zeroHistory ? 0 : HistoryBlock.Length;
        private int position;

        internal bool HasReadFinalByte => position == compressed.Length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
                return 0;
            if (historyPosition < HistoryBlock.Length)
            {
                int count = Math.Min(buffer.Length, HistoryBlock.Length - historyPosition);
                HistoryBlock.AsSpan(historyPosition, count).CopyTo(buffer);
                historyPosition += count;
                return count;
            }

            BinaryLayout.Require(position < compressed.Length, "Truncated DEFLATE stream.");
            int available = position < compressed.Length - 1 ? compressed.Length - 1 - position : 1;
            int copied = Math.Min(buffer.Length, available);
            compressed.AsSpan(position, copied).CopyTo(buffer);
            position += copied;
            return copied;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
