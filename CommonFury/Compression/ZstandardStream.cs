using System.Buffers.Binary;
using CommonFury.Binary;
using CommonFury.Hashing;

namespace CommonFury.Compression;

// Decodes block by block into a sliding window that keeps the history later blocks may reference.
internal sealed class ZstandardStream(Stream source, long maximumLength, ZstandardDictionary? dictionary, bool leaveOpen) : Stream
{
    private readonly XxHash64 hash = new();
    private readonly byte[] input = new byte[3 + ZstandardDecoder.MaximumBlockSize];
    private ZstandardDecoder? decoder;
    private ZstandardFrameHeader header;
    private byte[] window = [];
    private int windowLength, pendingStart;
    private long produced, frameProduced;
    private bool inFrame, finished, disposed;

    public override bool CanRead => !disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty)
            return 0;
        while (pendingStart == windowLength)
            if (finished || !DecodeNext())
                return 0;
        int count = Math.Min(buffer.Length, windowLength - pendingStart);
        window.AsSpan(pendingStart, count).CopyTo(buffer);
        pendingStart += count;
        return count;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            if (decoder is not null)
                Zstandard.ReturnDecoder(decoder);
            decoder = null;
            if (!leaveOpen)
                source.Dispose();
        }
        base.Dispose(disposing);
    }

    // Decodes the next block, starting a frame first when needed. Returns false at the end of the source.
    private bool DecodeNext()
    {
        if (!inFrame && !BeginFrame())
        {
            finished = true;
            return false;
        }
        var block = Zstandard.ReadBlockHeader(Fill(0, 3), decoder!.BlockMaximum);
        var stored = Fill(3, block.Stored);
        long frameRoom = header.ContentSize is { } size ? size - frameProduced : long.MaxValue;
        int room = (int)Math.Min(decoder.BlockMaximum, Math.Min(frameRoom, maximumLength - produced));
        int start = PrepareWindow(room), written;
        if (block.Type == 2)
            written = decoder.DecodeBlock(stored, window.AsSpan(0, start + room), start, frameProduced);
        else
        {
            BinaryLayout.Require(block.Size <= room, "Decoded Zstandard output exceeds its size limit.");
            var target = window.AsSpan(start, block.Size);
            if (block.Type == 0)
                stored.CopyTo(target);
            else
                target.Fill(stored[0]);
            written = block.Size;
        }
        pendingStart = start;
        windowLength = start + written;
        frameProduced += written;
        produced += written;
        if (header.HasChecksum)
            hash.Append(window.AsSpan(start, written));
        if (block.Last)
        {
            Zstandard.CheckFrameEnd(header, frameProduced, header.HasChecksum ? hash : null, Fill(0, header.HasChecksum ? 4 : 0));
            inFrame = false;
        }
        return true;
    }

    private bool BeginFrame()
    {
        while (true)
        {
            int count = source.ReadAtLeast(input.AsSpan(0, 4), 4, throwOnEndOfStream: false);
            if (count == 0)
            {
                BinaryLayout.Require(produced > 0 || decoder is not null, "Missing Zstandard frame.");
                return false;
            }
            BinaryLayout.Require(count == 4, "Truncated Zstandard frame header.");
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(input);
            decoder ??= Zstandard.RentDecoder();
            if (!Zstandard.IsSkippable(magic))
                break;
            Skip(BinaryPrimitives.ReadUInt32LittleEndian(Fill(0, 4)));
        }
        Fill(4, 1);
        int length = Zstandard.HeaderLength(input[4]);
        Fill(5, length - 5);
        header = Zstandard.ReadHeader(input.AsSpan(0, length), out _);
        decoder.BeginFrame(header, dictionary);
        hash.Reset();
        frameProduced = 0;
        windowLength = pendingStart = 0;
        inFrame = true;
        return true;
    }

    // Moves the history the next block may reference to the front of the window and makes room after it.
    // Twice the history plus a block of room keeps the cost of moving history below one byte per decoded byte.
    private int PrepareWindow(int room)
    {
        if (windowLength + room <= window.Length)
            return windowLength;
        int keep = (int)Math.Min(windowLength, Math.Min(header.WindowSize, frameProduced));
        var history = window.AsSpan(windowLength - keep, keep);
        if (window.Length < 2L * keep + room)
        {
            long size = Math.Min(Array.MaxLength, 2L * keep + room);
            BinaryLayout.Require(size >= keep + room, "Zstandard frame window exceeds the supported size.");
            var larger = new byte[size];
            history.CopyTo(larger);
            window = larger;
        }
        else
            history.CopyTo(window);
        windowLength = keep;
        return keep;
    }

    private Span<byte> Fill(int offset, int count)
    {
        var target = input.AsSpan(offset, count);
        BinaryLayout.Require(source.ReadAtLeast(target, count, throwOnEndOfStream: false) == count, "Truncated Zstandard stream.");
        return target;
    }

    private void Skip(uint count)
    {
        while (count > 0)
        {
            int chunk = (int)Math.Min(count, (uint)input.Length);
            Fill(0, chunk);
            count -= (uint)chunk;
        }
    }
}
