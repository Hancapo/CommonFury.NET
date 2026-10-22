using System.Globalization;

namespace CommonFury.Archives;

/// <summary>Bounded LRU cache of open archives for repeated reads of nested entries, owned by one thread.
/// An entry is a root file path plus indices: each index but the last selects a nested archive; the last is passed to the callback.</summary>
/// <remarks>Source files must stay unchanged while cached. Callbacks must not reenter or dispose the cache.</remarks>
public sealed class ArchiveHandleCache<TArchive> : IDisposable where TArchive : class, IDisposable
{
    private sealed record Cached(string Key, TArchive Archive, long Bytes);
    private readonly Dictionary<string, LinkedListNode<Cached>> cache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly LinkedList<Cached> recent = [];
    private readonly Func<string, TArchive> openRoot;
    private readonly Func<TArchive, int, TArchive> openNested;
    private readonly Func<TArchive, long> memoryBytesOf;
    private readonly int maximumArchives;
    private readonly long maximumMemoryBytes;
    private readonly int maximumDepth;
    private long memoryBytes;
    private bool disposed, reading;

    /// <summary>Includes oversized archives that were used once and not cached.</summary>
    public long OpenedArchives { get; private set; }
    public long CacheHits { get; private set; }
    public int CachedArchives => cache.Count;

    /// <param name="openRoot">Opens an archive from a full file path.</param>
    /// <param name="openNested">Opens the nested archive at an entry index. Must throw <see cref="InvalidDataException"/> for an index
    /// outside the archive; the result must stay usable after its parent is evicted.</param>
    /// <param name="memoryBytes">Managed storage an archive keeps alive. An archive larger than <paramref name="maximumMemoryBytes"/>
    /// is used once and not retained.</param>
    /// <param name="maximumArchives">Maximum archives held open.</param>
    /// <param name="maximumMemoryBytes">Maximum sum of <paramref name="memoryBytes"/> over cached archives.</param>
    /// <param name="maximumDepth">Maximum number of indices in an entry path.</param>
    public ArchiveHandleCache(Func<string, TArchive> openRoot, Func<TArchive, int, TArchive> openNested, Func<TArchive, long> memoryBytes,
        int maximumArchives = 32, long maximumMemoryBytes = 64L * 1024 * 1024, int maximumDepth = 33)
    {
        ArgumentNullException.ThrowIfNull(openRoot);
        ArgumentNullException.ThrowIfNull(openNested);
        ArgumentNullException.ThrowIfNull(memoryBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumArchives, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumMemoryBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDepth, 1);
        (this.openRoot, this.openNested, memoryBytesOf) = (openRoot, openNested, memoryBytes);
        (this.maximumArchives, this.maximumMemoryBytes, this.maximumDepth) = (maximumArchives, maximumMemoryBytes, maximumDepth);
    }

    /// <summary>Passes the innermost archive and the last index to <paramref name="read"/>, which borrows the archive only
    /// for the call and must return owned data.</summary>
    public T Read<T>(string rootPath, IReadOnlyList<int> indices, Func<TArchive, int, T> read)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (reading)
            throw new InvalidOperationException("Archive read callbacks cannot reenter their cache.");
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(read);
        if (indices.Count == 0 || indices.Count > maximumDepth)
            throw new ArgumentException($"An entry path must contain 1 to {maximumDepth} indices.", nameof(indices));
        foreach (int index in indices)
            ArgumentOutOfRangeException.ThrowIfNegative(index);
        string path = Path.GetFullPath(rootPath), key = path + '\0';
        var temporary = new List<TArchive>();
        reading = true;
        try
        {
            var archive = Get(key, () => openRoot(path), temporary);
            for (int depth = 0; depth < indices.Count - 1; depth++)
            {
                var parent = archive;
                int index = indices[depth];
                key += index.ToString(CultureInfo.InvariantCulture) + '/';
                archive = Get(key, () => openNested(parent, index), temporary);
            }
            return read(archive, indices[^1]);
        }
        finally
        {
            foreach (var archive in temporary)
                archive.Dispose();
            reading = false;
        }
    }

    private TArchive Get(string key, Func<TArchive> open, List<TArchive> temporary)
    {
        if (cache.TryGetValue(key, out var hit))
        {
            recent.Remove(hit);
            recent.AddLast(hit);
            CacheHits++;
            return hit.Value.Archive;
        }
        var archive = open();
        OpenedArchives++;
        long bytes;
        try { bytes = memoryBytesOf(archive); }
        catch
        {
            // Not yet cached or temporary: nothing else would close it.
            archive.Dispose();
            throw;
        }
        if (bytes > maximumMemoryBytes)
        {
            temporary.Add(archive);
            return archive;
        }
        while (cache.Count >= maximumArchives || memoryBytes + bytes > maximumMemoryBytes)
        {
            var oldest = recent.First!;
            recent.RemoveFirst();
            cache.Remove(oldest.Value.Key);
            memoryBytes -= oldest.Value.Bytes;
            oldest.Value.Archive.Dispose();
        }
        cache.Add(key, recent.AddLast(new Cached(key, archive, bytes)));
        memoryBytes += bytes;
        return archive;
    }

    /// <summary>No read callback may be in progress.</summary>
    public void Dispose()
    {
        if (reading)
            throw new InvalidOperationException("Cannot dispose an active archive read cache.");
        if (disposed)
            return;
        disposed = true;
        foreach (var item in recent)
            item.Archive.Dispose();
        cache.Clear();
        recent.Clear();
        memoryBytes = 0;
    }
}
