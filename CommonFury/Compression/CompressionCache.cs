using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CommonFury.Compression;

/// <summary>Optional reuse of optimal-level <see cref="RawDeflate"/> output, keyed by content, so output bytes never change.</summary>
/// <remarks><see cref="Directory"/> persists results across processes by SHA-256 of the input. <see cref="RememberRecent"/> keeps recent
/// results in memory so reading back just-written data skips decompression; recalled bytes are compared in full first.</remarks>
public static class CompressionCache
{
    private const int RecentCapacity = 256;
    private static readonly ConcurrentDictionary<string, (byte[] Compressed, byte[] Data)> recent = new();
    private static readonly ConcurrentQueue<string> order = new();
    private static long hits, misses;

    private static ReadOnlySpan<byte> Version => "deflate-optimal-v1"u8;

    /// <summary>Null disables persistence.</summary>
    public static string? Directory { get; set; }

    /// <summary>Off by default.</summary>
    public static bool RememberRecent { get; set; }

    /// <summary>Persisted entries reused and computed by this process.</summary>
    public static (long Hits, long Misses) Statistics => (Interlocked.Read(ref hits), Interlocked.Read(ref misses));

    internal static byte[] Deflate(byte[] data, Func<byte[]> compress)
    {
        byte[] result;
        string? path = null;
        if (Directory is { } root)
        {
            string key = Key(Version, data);
            path = Path.Combine(root, key[..2], key + ".deflate");
            if (File.Exists(path))
            {
                try { result = File.ReadAllBytes(path); Interlocked.Increment(ref hits); Remember(result, data); return result; }
                // An unreadable entry (being replaced by another process, or denied to this user) falls back to compressing.
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        result = compress();
        Interlocked.Increment(ref misses);
        if (path is not null)
        {
            string temporary = path + "." + Environment.ProcessId + "." + Environment.CurrentManagedThreadId + ".tmp";
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(temporary, result);
                File.Move(temporary, path, overwrite: true);
            }
            // The cache is best effort: failing to persist an entry never fails the compression.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temporary); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        }
        Remember(result, data);
        return result;
    }

    internal static bool IsEnabled => Directory is not null || RememberRecent;

    internal static byte[]? Recall(ReadOnlySpan<byte> compressed)
    {
        if (!RememberRecent || recent.IsEmpty) return null;
        if (!recent.TryGetValue(Key(Version, compressed), out var entry)) return null;
        if (!entry.Compressed.AsSpan().SequenceEqual(compressed)) return null;
        return (byte[])entry.Data.Clone();
    }

    private static void Remember(byte[] compressed, byte[] data)
    {
        if (!RememberRecent) return;
        string key = Key(Version, compressed);
        if (recent.TryAdd(key, (compressed, data))) order.Enqueue(key);
        while (recent.Count > RecentCapacity && order.TryDequeue(out var old)) recent.TryRemove(old, out _);
    }

    private static string Key(ReadOnlySpan<byte> version, ReadOnlySpan<byte> data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(version);
        hash.AppendData(data);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        hash.GetHashAndReset(digest);
        return Convert.ToHexStringLower(digest);
    }
}
