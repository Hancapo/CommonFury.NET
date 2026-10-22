using System.Security.AccessControl;
using System.Security.Principal;
using CommonFury.Compression;
using Xunit;

namespace CommonFury.Tests;

// Global cache settings: these tests must not run alongside other DEFLATE users that rely on the defaults.
[CollectionDefinition(nameof(CompressionCacheTests), DisableParallelization = true)]
public sealed class CompressionCacheCollection;

[Collection(nameof(CompressionCacheTests))]
public sealed class CompressionCacheTests
{
    [Fact]
    public void CachedCompressionIsByteIdenticalColdAndWarmAndReadBackMatches()
    {
        byte[] data = new byte[200_000];
        new Random(7).NextBytes(data.AsSpan(0, 50_000));
        byte[] reference = RawDeflate.Compress(data);
        string root = Path.Combine(Path.GetTempPath(), "commonfury-deflate-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            CompressionCache.Directory = root;
            CompressionCache.RememberRecent = true;
            var before = CompressionCache.Statistics;
            byte[] cold = RawDeflate.Compress(data);
            byte[] warm = RawDeflate.Compress(data);
            var after = CompressionCache.Statistics;
            Assert.Equal(reference, cold);
            Assert.Equal(reference, warm);
            Assert.Equal(1, after.Misses - before.Misses);
            Assert.Equal(1, after.Hits - before.Hits);
            Assert.Single(Directory.EnumerateFiles(root, "*.deflate", SearchOption.AllDirectories));
            Assert.Equal(data, RawDeflate.DecompressExact(warm, data.Length));
            Assert.Equal(data, RawDeflate.Decompress(warm, data.Length));
            // Recalled data never overrides the size contract.
            Assert.Throws<InvalidDataException>(() => RawDeflate.DecompressExact(warm, data.Length - 1));
            // A corrupted payload is never trusted for read-back: only exact compressed bytes are recalled.
            byte[] tampered = (byte[])warm.Clone();
            tampered[^1] ^= 0xFF;
            byte[]? decoded = null;
            try { decoded = RawDeflate.DecompressExact(tampered, data.Length); } catch (InvalidDataException) { }
            Assert.False(decoded is not null && decoded.AsSpan().SequenceEqual(data));
        }
        finally
        {
            CompressionCache.Directory = null;
            CompressionCache.RememberRecent = false;
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")] // skipped elsewhere by SkipUnless below
    public void UnreadableCacheEntryFallsBackToCompressing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses a Windows ACL to make the cache entry unreadable.");
        byte[] data = new byte[100_000];
        new Random(3).NextBytes(data.AsSpan(0, 1_000));
        byte[] reference = RawDeflate.Compress(data);
        string root = Path.Combine(Path.GetTempPath(), "commonfury-deflate-cache-" + Guid.NewGuid().ToString("N"));
        FileSecurity? security = null;
        FileInfo? entry = null;
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        try
        {
            CompressionCache.Directory = root;
            RawDeflate.Compress(data);
            entry = new FileInfo(Directory.EnumerateFiles(root, "*.deflate", SearchOption.AllDirectories).Single());
            security = entry.GetAccessControl();
            security.AddAccessRule(deny);
            entry.SetAccessControl(security);
            Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllBytes(entry.FullName));
            var before = CompressionCache.Statistics;
            Assert.Equal(reference, RawDeflate.Compress(data));
            Assert.Equal(1, CompressionCache.Statistics.Misses - before.Misses);
        }
        finally
        {
            CompressionCache.Directory = null;
            if (security is not null && entry is not null)
            {
                security.RemoveAccessRule(deny);
                entry.SetAccessControl(security);
            }
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
