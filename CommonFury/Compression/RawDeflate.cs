using System.Buffers;
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
        using var input = new StrictInput(compressed, zeroHistory);
        using var inflater = Open(input, zeroHistory);
        var output = new byte[Math.Min(maximumLength, 64 * 1024)];
        int length = 0;
        Span<byte> probe = stackalloc byte[1];
        while (true)
        {
            if (length == output.Length)
            {
                // A full buffer grows only once another byte exists, so output that exactly fills it needs no trimming copy.
                if (Inflate(inflater, input, probe) == 0)
                    break;
                BinaryLayout.Require(length < maximumLength, "Decoded DEFLATE output exceeds its size limit.");
                Array.Resize(ref output, (int)Math.Min(maximumLength, 2L * length));
                output[length++] = probe[0];
                // The probe byte can fill the grown buffer; probe again rather than read into an empty span.
                continue;
            }
            int count = Inflate(inflater, input, output.AsSpan(length));
            if (count == 0)
                break;
            length += count;
        }
        BinaryLayout.Require(input.HasReadFinalByte, "Trailing data after the DEFLATE stream.");
        return length == output.Length ? output : output[..length];
    }

    /// <summary>Decodes the first <paramref name="length"/> bytes, or fewer when the stream ends first. The stream after
    /// them is neither decoded nor validated, so trailing data, truncation or corruption past that point is not reported.</summary>
    public static byte[] DecompressPrefix(ReadOnlySpan<byte> compressed, int length, bool zeroHistory = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        BinaryLayout.Require(!compressed.IsEmpty, "Missing DEFLATE stream.");
        using var input = new StrictInput(compressed, zeroHistory);
        using var inflater = Open(input, zeroHistory);
        // Grows like Decompress, so a declared length far beyond the real data does not allocate it up front.
        var output = new byte[Math.Min(length, 64 * 1024)];
        int count = 0;
        while (count < length)
        {
            if (count == output.Length)
                Array.Resize(ref output, (int)Math.Min(length, 2L * count));
            int read = Inflate(inflater, input, output.AsSpan(count));
            if (read == 0)
                break;
            count += read;
        }
        return count == output.Length ? output : output[..count];
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
        using var input = new StrictInput(compressed, zeroHistory);
        using var inflater = Open(input, zeroHistory);
        int count = 0;
        while (count < destination.Length && Inflate(inflater, input, destination[count..]) is var read and > 0)
            count += read;
        BinaryLayout.Require(count == destination.Length, "DEFLATE output is shorter than its declared size.");
        Span<byte> probe = stackalloc byte[1];
        BinaryLayout.Require(Inflate(inflater, input, probe) == 0, "DEFLATE output exceeds its declared size.");
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

    private static DeflateStream Open(StrictInput input, bool zeroHistory)
    {
        var inflater = new DeflateStream(input, CompressionMode.Decompress);
        if (zeroHistory)
            SkipHistory(inflater, input);
        return inflater;
    }

    private static void SkipHistory(DeflateStream inflater, StrictInput input)
    {
        byte[] history = ArrayPool<byte>.Shared.Rent(HistorySize);
        for (int count = 0; count < HistorySize;)
        {
            int read = Inflate(inflater, input, history.AsSpan(count, HistorySize - count));
            BinaryLayout.Require(read > 0, "Truncated DEFLATE stream.");
            count += read;
        }
        ArrayPool<byte>.Shared.Return(history);
    }

    // The inflater reports every malformed stream as an unsupported compression method; truncation keeps its own message.
    private static int Inflate(DeflateStream inflater, StrictInput input, Span<byte> buffer)
    {
        try { return inflater.Read(buffer); }
        catch (InvalidDataException exception) when (!input.Truncated)
        { throw new InvalidDataException($"Corrupt DEFLATE stream: decoding fails after {input.ValidOutput()} output bytes.", exception); }
    }

    // DeflateStream buffers input and exposes neither its terminal state nor unused bytes.
    // Withhold the last byte until a separate read: a decoder that finishes earlier has trailing data,
    // even if that data fits in its read-ahead buffer. A request beyond the last byte means truncation.
    // This keeps bulk reads and strict framing without reflection or process-wide AppContext switches.
    // The input copy is rented and returned on disposal, so large resources do not allocate a fresh array per decode.
    private sealed class StrictInput : Stream
    {
        private readonly byte[] compressed;
        private readonly int length;
        private readonly bool zeroHistory;
        private int historyPosition;
        private int position;

        internal StrictInput(ReadOnlySpan<byte> data, bool zeroHistory)
        {
            compressed = ArrayPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(compressed);
            (length, this.zeroHistory) = (data.Length, zeroHistory);
            historyPosition = zeroHistory ? 0 : HistoryBlock.Length;
        }

        // Decodes again one byte at a time, which only a failed stream pays for, to locate the failure exactly.
        internal long ValidOutput()
        {
            using var input = new StrictInput(compressed.AsSpan(0, length), zeroHistory);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            Span<byte> one = stackalloc byte[1];
            long count = zeroHistory ? -HistorySize : 0;
            try
            {
                while (inflater.Read(one) == 1)
                    count++;
            }
            catch (InvalidDataException) { }
            return Math.Max(count, 0);
        }

        internal bool HasReadFinalByte => position == length;
        internal bool Truncated { get; private set; }
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

            Truncated = position >= length;
            BinaryLayout.Require(!Truncated, "Truncated DEFLATE stream.");
            int available = position < length - 1 ? length - 1 - position : 1;
            int copied = Math.Min(buffer.Length, available);
            compressed.AsSpan(position, copied).CopyTo(buffer);
            position += copied;
            return copied;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && position >= 0)
            {
                position = -1;
                ArrayPool<byte>.Shared.Return(compressed);
            }
            base.Dispose(disposing);
        }
    }
}
