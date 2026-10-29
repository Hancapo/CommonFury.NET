using CommonFury.Binary;
using Microsoft.Win32.SafeHandles;

namespace CommonFury.Archives;

/// <summary>Random-access read-only bytes. Every read names its own offset, so streams never share a position.
/// Safe for concurrent reads unless a subclass says otherwise.</summary>
public abstract class ByteSource
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public abstract long Length { get; }
    /// <summary>Managed bytes kept alive, for cache accounting; zero for file-backed sources.</summary>
    public virtual long RetainedMemoryBytes => 0;
    /// <summary>The whole range must lie inside the source.</summary>
    public abstract void Read(long offset, Span<byte> destination);
    /// <summary>Throws when the source is disposed or its backing data has changed.</summary>
    public virtual void RequireOpen() { }
    /// <summary>Rejects an output path that would overwrite this source.</summary>
    public virtual void RequireDifferentDestination(string path) { }
    /// <summary>Rejects an output stream that would overwrite this source.</summary>
    public virtual void RequireDifferentDestination(Stream stream) { }
    /// <summary>A read-only seekable stream over a range; it borrows this source and does not dispose it.</summary>
    public virtual Stream Open(long offset, long length) => new ByteSliceStream(this, offset, length);

    public byte[] Read(long offset, int count)
    {
        var data = new byte[count];
        Read(offset, data);
        return data;
    }

    /// <summary>A view whose offsets start at zero; it borrows this source.</summary>
    public ByteSource Slice(long offset, long length)
    {
        RequireRange(offset, length);
        return new SliceByteSource(this, offset, length);
    }

    /// <summary>Throws <see cref="InvalidDataException"/> unless the range lies inside the source.</summary>
    public void RequireRange(long offset, long length) =>
        BinaryLayout.Require(offset >= 0 && length >= 0 && offset <= Length - length, "Byte range exceeds its source.");

    /// <summary>Borrows <paramref name="data"/> without copying; it must not change while in use.</summary>
    /// <param name="data">Backing bytes.</param>
    /// <param name="lifetime">Optional owner whose disposal also invalidates this source.</param>
    public static ByteSource FromMemory(byte[] data, ByteSource? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new MemoryByteSource(data, lifetime);
    }

    /// <summary>Serializes reads because they move the stream position. Disposing the result closes the stream unless <paramref name="leaveOpen"/>.</summary>
    public static StreamByteSource FromStream(Stream stream, bool leaveOpen = false) => new(stream, leaveOpen);

    /// <summary>Keeps one read-only handle for positional reads; dispose the result to close it.</summary>
    public static FileByteSource OpenFile(string path) => new(path);

    /// <summary>Keeps no handle: each read or stream opens the file and fails if its size or timestamp changed.</summary>
    public static ByteSource FromPath(string path) => new PathByteSource(path);

    private protected static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private sealed class MemoryByteSource(byte[] data, ByteSource? lifetime) : ByteSource
    {
        public override long Length => data.LongLength;
        public override long RetainedMemoryBytes => data.LongLength;
        public override void RequireOpen() => lifetime?.RequireOpen();
        public override void Read(long offset, Span<byte> destination)
        {
            RequireOpen();
            RequireRange(offset, destination.Length);
            data.AsSpan((int)offset, destination.Length).CopyTo(destination);
        }
    }

    private sealed class SliceByteSource(ByteSource parent, long start, long length) : ByteSource
    {
        public override long Length => length;
        public override void RequireOpen() => parent.RequireOpen();
        public override void Read(long offset, Span<byte> destination)
        {
            RequireRange(offset, destination.Length);
            parent.Read(start + offset, destination);
        }
        // Delegates so a parent with per-stream resources (a path source's file handle) opens them once per stream.
        public override Stream Open(long offset, long length)
        {
            RequireRange(offset, length);
            return parent.Open(start + offset, length);
        }
        public override void RequireDifferentDestination(string path) => parent.RequireDifferentDestination(path);
        public override void RequireDifferentDestination(Stream stream) => parent.RequireDifferentDestination(stream);
    }

    // Paths are resolved at creation. Each read opens its own handle; the source owns no persistent handle.
    // The file must remain unchanged until all consuming reads finish.
    private sealed class PathByteSource : ByteSource
    {
        private readonly string path;
        private readonly long length;
        private readonly DateTime modified;
        internal PathByteSource(string path)
        {
            this.path = Path.GetFullPath(path);
            var info = new FileInfo(this.path);
            if (!info.Exists)
                throw new FileNotFoundException("Byte source file is missing.", this.path);
            length = info.Length;
            modified = info.LastWriteTimeUtc;
        }
        public override long Length => length;
        public override void RequireOpen()
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != modified)
                throw new IOException($"Byte source file changed after it was added: {path}");
        }
        public override Stream Open(long offset, long count)
        {
            RequireOpen();
            RequireRange(offset, count);
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            try
            {
                RequireOpen();
                if (file.Length != length)
                    throw new IOException("Byte source file changed while opening it.");
                return new ByteSliceStream(new StreamByteSource(file, false), offset, count, ownsSource: true);
            }
            catch { file.Dispose(); throw; }
        }
        // One open and one positional read; no buffered stream for a single range.
        public override void Read(long offset, Span<byte> destination)
        {
            RequireOpen();
            RequireRange(offset, destination.Length);
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (RandomAccess.GetLength(handle) != length)
                throw new IOException("Byte source file changed while opening it.");
            while (!destination.IsEmpty)
            {
                int read = RandomAccess.Read(handle, destination, offset);
                if (read == 0)
                    throw new EndOfStreamException("Byte source file ended early.");
                destination = destination[read..];
                offset += read;
            }
        }
        public override void RequireDifferentDestination(string destination)
        {
            if (SamePath(path, destination))
                throw new ArgumentException("Output cannot replace a borrowed source file.", nameof(destination));
        }
        public override void RequireDifferentDestination(Stream stream)
        {
            if (stream is FileStream file)
                RequireDifferentDestination(file.Name);
        }
    }
}

/// <summary>Byte source over a readable, seekable stream; reads are serialized.</summary>
public sealed class StreamByteSource : ByteSource, IDisposable
{
    private readonly Stream stream;
    private readonly bool leaveOpen;
    private readonly object gate = new();
    private readonly long length;
    private bool disposed;

    /// <summary>The stream length is captured now and must not change.</summary>
    public StreamByteSource(Stream stream, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("Byte sources require a readable, seekable stream.", nameof(stream));
        this.stream = stream;
        this.leaveOpen = leaveOpen;
        length = stream.Length;
    }

    /// <inheritdoc/>
    public override long Length => length;
    /// <inheritdoc/>
    public override void RequireOpen() => ObjectDisposedException.ThrowIf(disposed, this);

    /// <inheritdoc/>
    public override void Read(long offset, Span<byte> destination)
    {
        lock (gate)
        {
            RequireOpen();
            RequireRange(offset, destination.Length);
            stream.Position = offset;
            stream.ReadExactly(destination);
        }
    }

    /// <inheritdoc/>
    public override void RequireDifferentDestination(string path)
    {
        if (stream is FileStream file && SamePath(path, file.Name))
            throw new ArgumentException("Save to a separate destination; the source is read-only.", nameof(path));
    }

    /// <inheritdoc/>
    public override void RequireDifferentDestination(Stream destination)
    {
        if (ReferenceEquals(destination, stream))
            throw new ArgumentException("A source stream cannot also be its output stream.", nameof(destination));
        if (destination is FileStream file)
            RequireDifferentDestination(file.Name);
    }

    /// <summary>Closes the stream unless created with leaveOpen; later reads throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            if (!leaveOpen)
                stream.Dispose();
        }
    }
}

/// <summary>Byte source over a read-only file handle; positional reads need no lock.</summary>
public sealed class FileByteSource : ByteSource, IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly long length;
    private bool disposed;

    /// <summary>Opens the file for shared reading and captures its length.</summary>
    public FileByteSource(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try { length = RandomAccess.GetLength(handle); }
        catch { handle.Dispose(); throw; }
    }

    public string Path { get; }
    /// <inheritdoc/>
    public override long Length => length;
    /// <inheritdoc/>
    public override void RequireOpen() => ObjectDisposedException.ThrowIf(disposed, this);

    /// <summary>Throws <see cref="EndOfStreamException"/> if the file was truncated after opening.</summary>
    public override void Read(long offset, Span<byte> destination)
    {
        RequireOpen();
        RequireRange(offset, destination.Length);
        int cursor = 0;
        while (cursor < destination.Length)
        {
            int count = RandomAccess.Read(handle, destination[cursor..], offset + cursor);
            if (count == 0)
                throw new EndOfStreamException("Byte source file was truncated.");
            cursor += count;
        }
    }

    /// <inheritdoc/>
    public override void RequireDifferentDestination(string path)
    {
        if (SamePath(Path, path))
            throw new ArgumentException("Save to a separate destination; the source is read-only.", nameof(path));
    }

    /// <inheritdoc/>
    public override void RequireDifferentDestination(Stream stream)
    {
        if (stream is FileStream file)
            RequireDifferentDestination(file.Name);
    }

    /// <summary>Later reads throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        disposed = true;
        handle.Dispose();
    }
}

/// <summary>Read-only seekable stream over a range of a <see cref="ByteSource"/>, with its own position.</summary>
public sealed class ByteSliceStream : Stream
{
    private readonly ByteSource source;
    private readonly long offset;
    private readonly long length;
    private readonly bool ownsSource;
    private long position;
    private bool disposed;

    /// <summary>With <paramref name="ownsSource"/>, disposing the stream disposes a disposable source.</summary>
    public ByteSliceStream(ByteSource source, long offset, long length, bool ownsSource = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.RequireOpen();
        source.RequireRange(offset, length);
        (this.source, this.offset, this.length, this.ownsSource) = (source, offset, length, ownsSource);
    }

    /// <inheritdoc/>
    public override bool CanRead => !disposed;
    /// <inheritdoc/>
    public override bool CanSeek => !disposed;
    /// <inheritdoc/>
    public override bool CanWrite => false;
    /// <inheritdoc/>
    public override long Length { get { Check(); return length; } }
    /// <inheritdoc/>
    public override long Position { get { Check(); return position; } set => Seek(value, SeekOrigin.Begin); }
    private void Check() { ObjectDisposedException.ThrowIf(disposed, this); source.RequireOpen(); }
    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        Check();
        int count = (int)Math.Min(buffer.Length, length - position);
        source.Read(offset + position, buffer[..count]);
        position += count;
        return count;
    }
    /// <inheritdoc/>
    public override int ReadByte()
    {
        Span<byte> value = stackalloc byte[1];
        return Read(value) == 0 ? -1 : value[0];
    }
    /// <summary>Positions outside the range throw <see cref="IOException"/>.</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        Check();
        long next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (next < 0 || next > length)
            throw new IOException("Seek exceeds the byte range.");
        return position = next;
    }
    /// <inheritdoc/>
    public override void Flush() => Check();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed && ownsSource && source is IDisposable owned)
            owned.Dispose();
        disposed = true;
        base.Dispose(disposing);
    }
}
