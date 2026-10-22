using CommonFury.Archives;
using Xunit;

namespace CommonFury.Tests;

public sealed class ArchiveHandleCacheTests
{
    // Minimal nested archive over a byte source: a byte count followed by that many one-byte entries.
    // Entry values >= 0x80 denote a nested archive stored in a separate in-memory table.
    private sealed class FakeArchive(ByteSource source, IReadOnlyList<byte[]> nested, long memory) : IDisposable
    {
        public bool Disposed { get; private set; }
        public long Memory => memory;
        public int Count => source.Read(0, 1)[0];
        public byte Read(int index)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (index >= Count)
                throw new InvalidDataException("Entry index exceeds the selected archive table.");
            return source.Read(1 + index, 1)[0];
        }
        public FakeArchive OpenArchive(int index) => new(ByteSource.FromMemory(nested[Read(index) - 0x80]), nested, nested[Read(index) - 0x80].Length);
        public void Dispose()
        {
            Disposed = true;
            (source as IDisposable)?.Dispose();
        }
    }

    private static ArchiveHandleCache<FakeArchive> Cache(List<FakeArchive> opened, IReadOnlyList<byte[]> nested,
        int maximumArchives = 32, long maximumMemoryBytes = 64L * 1024 * 1024) => new(
        path => Track(opened, new FakeArchive(ByteSource.OpenFile(path), nested, 0)),
        (archive, index) => Track(opened, archive.OpenArchive(index)),
        archive => archive.Memory, maximumArchives, maximumMemoryBytes);

    private static FakeArchive Track(List<FakeArchive> opened, FakeArchive archive)
    {
        opened.Add(archive);
        return archive;
    }

    [Fact]
    public void ReusesArchivesAndKeepsOwnedResultsAfterEvictionAndDisposal()
    {
        string path = Path.Combine(Path.GetTempPath(), "commonfury-read-cache-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, [1, 0x80]);
            byte[][] nested = [[3, 4, 5, 6]];
            var opened = new List<FakeArchive>();
            byte result;
            using (var cache = Cache(opened, nested, maximumArchives: 2))
            {
                result = cache.Read(path, [0, 0], (a, i) => a.Read(i));
                Assert.Equal(4, result);
                Assert.Equal(2, cache.OpenedArchives);
                Assert.Equal(result, cache.Read(path, [0, 0], (a, i) => a.Read(i)));
                Assert.Equal(2, cache.OpenedArchives);
                Assert.Equal(2, cache.CacheHits);
                Assert.Equal(2, cache.CachedArchives);
                Assert.Throws<InvalidDataException>(() => cache.Read(path, [0, 8], (a, i) => a.Read(i)));
                Assert.Equal(result, cache.Read(path, [0, 0], (a, i) => a.Read(i)));
                Assert.Throws<InvalidOperationException>(() => cache.Read(path, [0, 0], (_, _) => cache.Read(path, [0, 0], (a, i) => a.Read(i))));
            }
            Assert.All(opened, archive => Assert.True(archive.Disposed));
            using (var small = Cache(opened, nested, maximumArchives: 1))
            {
                Assert.Equal(result, small.Read(path, [0, 0], (a, i) => a.Read(i)));
                Assert.Equal(1, small.CachedArchives);
                Assert.Equal(result, small.Read(path, [0, 0], (a, i) => a.Read(i)));
            }
            // No handles survive disposal.
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OversizedMemoryArchiveIsReadButNotRetained()
    {
        string path = Path.Combine(Path.GetTempPath(), "commonfury-read-memory-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, [1, 0x80]);
            var opened = new List<FakeArchive>();
            using (var cache = Cache(opened, [[3, 7, 8, 9]], maximumMemoryBytes: 0))
            {
                Assert.Equal(8, cache.Read(path, [0, 1], (a, i) => a.Read(i)));
                Assert.Equal(1, cache.CachedArchives);
                Assert.Equal(2, cache.OpenedArchives);
                Assert.True(opened[1].Disposed);
                Assert.Equal(8, cache.Read(path, [0, 1], (a, i) => a.Read(i)));
                Assert.Equal(1, cache.CachedArchives);
                Assert.Equal(3, cache.OpenedArchives);
            }
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MemoryCapEvictsLeastRecentlyUsedArchives()
    {
        string path = Path.Combine(Path.GetTempPath(), "commonfury-read-lru-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, [2, 0x80, 0x81]);
            var opened = new List<FakeArchive>();
            using var cache = Cache(opened, [[1, 10], [1, 20]], maximumMemoryBytes: 3);
            Assert.Equal(10, cache.Read(path, [0, 0], (a, i) => a.Read(i)));
            Assert.Equal(20, cache.Read(path, [1, 0], (a, i) => a.Read(i)));
            Assert.True(opened[1].Disposed);
            Assert.False(opened[2].Disposed);
            Assert.Equal(2, cache.CachedArchives);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectsInvalidPathsAndUseAfterDisposal()
    {
        var opened = new List<FakeArchive>();
        Assert.Throws<ArgumentOutOfRangeException>(() => Cache(opened, [], maximumArchives: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Cache(opened, [], maximumMemoryBytes: -1));
        var cache = Cache(opened, []);
        Assert.Throws<ArgumentException>(() => cache.Read("unused", [], (a, i) => a.Read(i)));
        Assert.Throws<ArgumentException>(() => cache.Read("unused", new int[34], (a, i) => a.Read(i)));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Read("unused", [-1], (a, i) => a.Read(i)));
        cache.Dispose();
        cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cache.Read("unused", [0], (a, i) => a.Read(i)));
    }

    [Fact]
    public void ArchiveIsClosedWhenMeasuringItsMemoryFails()
    {
        string path = Path.Combine(Path.GetTempPath(), "commonfury-read-cache-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(path, [1, 7]);
            var opened = new List<FakeArchive>();
            using var cache = new ArchiveHandleCache<FakeArchive>(
                file => Track(opened, new FakeArchive(ByteSource.OpenFile(file), [], 0)),
                (archive, index) => Track(opened, archive.OpenArchive(index)),
                _ => throw new InvalidDataException("Corrupt archive size."));
            Assert.Throws<InvalidDataException>(() => cache.Read(path, [0], (archive, index) => archive.Read(index)));
            Assert.True(Assert.Single(opened).Disposed);
            File.Delete(path); // the file handle was released
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
