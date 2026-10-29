using CommonFury.Archives;
using Xunit;

namespace CommonFury.Tests;

public sealed class ArchiveByteSourceTests
{
    private static readonly byte[] Data = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

    [Fact]
    public void AllSourcesReadTheSameRangesAndRejectOutOfRange()
    {
        string path = Path.Combine(Path.GetTempPath(), "commonfury-source-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, Data);
        try
        {
            using var stream = new MemoryStream(Data);
            using var streamSource = ByteSource.FromStream(stream, leaveOpen: true);
            using var file = ByteSource.OpenFile(path);
            foreach (var source in new[] { ByteSource.FromMemory(Data), streamSource, file, ByteSource.FromPath(path) })
            {
                Assert.Equal(64, source.Length);
                Assert.Equal(Data[10..20], source.Read(10, 10));
                Assert.Empty(source.Read(64, 0));
                Assert.Throws<InvalidDataException>(() => source.Read(60, 5));
                Assert.Throws<InvalidDataException>(() => source.Read(-1, 1));
                var slice = source.Slice(16, 16);
                Assert.Equal(Data[20..24], slice.Read(4, 4));
                Assert.Throws<InvalidDataException>(() => slice.Read(14, 4));
                Assert.Throws<InvalidDataException>(() => source.Slice(60, 5));
                using var range = source.Open(8, 8);
                Assert.Equal(8, range.Length);
                range.Position = 2;
                byte[] buffer = new byte[16];
                Assert.Equal(6, range.Read(buffer));
                Assert.Equal(Data[10..16], buffer[..6]);
                Assert.Throws<IOException>(() => range.Seek(1, SeekOrigin.End));
            }
            Assert.Equal(64, ByteSource.FromMemory(Data).RetainedMemoryBytes);
            Assert.Equal(0, file.RetainedMemoryBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void IndependentStreamsKeepTheirOwnPositions()
    {
        var source = ByteSource.FromMemory(Data);
        using var first = source.Open(0, 32);
        using var second = source.Open(32, 32);
        first.Position = 5;
        Assert.Equal(32, second.ReadByte());
        Assert.Equal(5, first.ReadByte());
        Assert.Throws<NotSupportedException>(() => first.WriteByte(1));
    }

    [Fact]
    public void SliceStreamReadByteStopsAtTheRangeEndAndRequiresAnOpenSource()
    {
        var streamSource = ByteSource.FromStream(new MemoryStream(Data));
        Stream range = streamSource.Open(60, 3);
        Assert.Equal([60, 61, 62], new[] { range.ReadByte(), range.ReadByte(), range.ReadByte() });
        Assert.Equal(-1, range.ReadByte());
        Assert.Equal(-1, range.ReadByte());
        Assert.Equal(3, range.Position);
        range.Position = 1;
        Assert.Equal(61, range.ReadByte());
        Assert.Equal(-1, ByteSource.FromMemory(Data).Open(64, 0).ReadByte());
        streamSource.Dispose();
        Assert.Throws<ObjectDisposedException>(() => range.ReadByte());
        Stream disposed = ByteSource.FromMemory(Data).Open(0, 4);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => disposed.ReadByte());
    }

    [Fact]
    public void DisposalAndChangedFilesInvalidateReads()
    {
        string path = Path.Combine(Path.GetTempPath(), "commonfury-source-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, Data);
        try
        {
            var stream = new MemoryStream(Data);
            var streamSource = ByteSource.FromStream(stream);
            var borrowed = ByteSource.FromMemory(Data, streamSource);
            streamSource.Dispose();
            Assert.Throws<ObjectDisposedException>(() => streamSource.Read(0, 1));
            Assert.Throws<ObjectDisposedException>(() => borrowed.Read(0, 1));
            Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());

            var file = ByteSource.OpenFile(path);
            Assert.Throws<ArgumentException>(() => file.RequireDifferentDestination(path));
            file.Dispose();
            Assert.Throws<ObjectDisposedException>(() => file.Read(0, 1));

            var byPath = ByteSource.FromPath(path);
            Assert.Throws<ArgumentException>(() => byPath.RequireDifferentDestination(path));
            File.WriteAllBytes(path, Data[..32]);
            Assert.Throws<IOException>(() => byPath.Read(0, 1));
            Assert.Throws<FileNotFoundException>(() => ByteSource.FromPath(path + ".missing"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StreamSourcesRejectTheirOwnStreamAsOutputAndUnseekableInput()
    {
        using var stream = new MemoryStream(Data);
        using var source = ByteSource.FromStream(stream, leaveOpen: true);
        Assert.Throws<ArgumentException>(() => source.RequireDifferentDestination(stream));
        source.RequireDifferentDestination(new MemoryStream());
        Assert.Throws<ArgumentException>(() => ByteSource.FromStream(new UnseekableStream()));
    }

    private sealed class UnseekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public void StreamsOverPathSlicesKeepOneHandleInsteadOfReopeningPerRead()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses Windows share modes to observe the open handle.");
        string path = Path.Combine(Path.GetTempPath(), "commonfury-path-source-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        try
        {
            ByteSource slice = ByteSource.FromPath(path).Slice(16, 64);
            using (Stream stream = slice.Open(8, 32))
            {
                Span<byte> first = stackalloc byte[4];
                stream.ReadExactly(first);
                Assert.Equal(new byte[] { 24, 25, 26, 27 }, first.ToArray());
                // The stream holds its own read handle, so the file cannot be deleted underneath it.
                Assert.ThrowsAny<IOException>(() => File.Delete(path));
                stream.ReadExactly(first);
                Assert.Equal(new byte[] { 28, 29, 30, 31 }, first.ToArray());
            }
            Assert.Equal(new byte[] { 40, 41 }, slice.Read(24, 2));
            File.Delete(path); // no handle outlives the stream or a direct read
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
