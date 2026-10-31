using System.Security.Cryptography;
using System.Text.Json;
using CommonFury.Compression;
using CommonFury.Hashing;
using Xunit;

namespace CommonFury.Tests;

public sealed class ZstandardTests
{
    // Fixtures come from libzstd 1.5.7 over synthetic content; the manifest records each content's SHA-256.
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "ReferenceData", "zstd");
    private static readonly JsonElement Manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "zstd.json"))).RootElement;

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var item in Manifest.GetProperty("fixtures").EnumerateArray())
            data.Add(item.GetProperty("name").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodesReferenceFixturesThroughEveryEntryPoint(string name)
    {
        var (compressed, length, sha256, dictionary) = Load(name);
        Assert.Equal(sha256, Hash(Zstandard.Decompress(compressed, length, dictionary)));
        Assert.Equal(sha256, Hash(Zstandard.Decompress(compressed, length + 1000, dictionary)));
        Assert.Equal(sha256, Hash(Zstandard.DecompressExact(compressed, length, dictionary)));
        using var stream = Zstandard.CreateDecompressionStream(new MemoryStream(compressed), length, dictionary);
        var streamed = new MemoryStream();
        var chunk = new byte[1000];
        for (int count; (count = stream.Read(chunk, 0, Random.Shared.Next(1, chunk.Length))) > 0;)
            streamed.Write(chunk, 0, count);
        Assert.Equal(sha256, Hash(streamed.ToArray()));
        if (length > 0)
            Assert.Throws<InvalidDataException>(() => Zstandard.Decompress(compressed, length - 1, dictionary));
    }

    [Fact]
    public void ReadsFrameHeaders()
    {
        var single = Zstandard.ReadFrameHeader(Load("text-checksum").Compressed);
        Assert.Equal(new ZstandardFrameHeader(20000, 20000, 0, true), single);
        var windowed = Zstandard.ReadFrameHeader(Load("window-1k").Compressed);
        Assert.Equal(new ZstandardFrameHeader(null, 1024, 0, true), windowed);
        var dictionary = Zstandard.ReadFrameHeader(Load("dictionary-frames").Compressed);
        Assert.Equal(Manifest.GetProperty("dictionaryId").GetUInt32(), dictionary.DictionaryId);
        Assert.Equal(0u, Zstandard.ReadFrameHeader(Load("dictionary-no-id").Compressed).DictionaryId);
        byte[] skippable = [0x50, 0x2A, 0x4D, 0x18, 2, 0, 0, 0, 9, 9, .. Load("hand-rle-modes").Compressed];
        Assert.Equal(new ZstandardFrameHeader(22, 22, 0, false), Zstandard.ReadFrameHeader(skippable));
        Assert.Throws<InvalidDataException>(() => Zstandard.ReadFrameHeader(skippable[..10]));
    }

    [Fact]
    public void ReadsDictionaries()
    {
        var trained = new ZstandardDictionary(File.ReadAllBytes(Path.Combine(Root, "trained.dict")));
        Assert.Equal(Manifest.GetProperty("dictionaryId").GetUInt32(), trained.Id);
        Assert.True(trained.HasEntropyTables);
        var raw = new ZstandardDictionary(File.ReadAllBytes(Path.Combine(Root, "raw-content.dict")));
        Assert.Equal(0u, raw.Id);
        Assert.False(raw.HasEntropyTables);
        Assert.Equal(3000, raw.ContentLength);
        var forcedRaw = new ZstandardDictionary(File.ReadAllBytes(Path.Combine(Root, "trained.dict")), rawContent: true);
        Assert.False(forcedRaw.HasEntropyTables);
        byte[] dictionary = File.ReadAllBytes(Path.Combine(Root, "trained.dict"));
        Assert.Throws<InvalidDataException>(() => new ZstandardDictionary(dictionary.AsSpan(0, 40)));
    }

    [Fact]
    public void RejectsMissingOrDifferentDictionaries()
    {
        var (compressed, length, _, dictionary) = Load("dictionary-frames");
        Assert.Throws<InvalidDataException>(() => Zstandard.Decompress(compressed, length));
        var other = new ZstandardDictionary(File.ReadAllBytes(Path.Combine(Root, "raw-content.dict")));
        Assert.Throws<InvalidDataException>(() => Zstandard.Decompress(compressed, length, other));
        // Without a dictionary, offsets into the raw content reach before the frame.
        var raw = Load("raw-dictionary");
        Assert.Throws<InvalidDataException>(() => Zstandard.Decompress(raw.Compressed, raw.Length));
        Assert.NotNull(dictionary);
    }

    [Fact]
    public void RejectsTruncatedAndTrailingInput()
    {
        foreach (string name in (string[])["hand-rle-modes", "tiny", "skewed-literals", "multi-frame", "dictionary-frames"])
        {
            var (compressed, length, _, dictionary) = Load(name);
            byte[] content = Zstandard.Decompress(compressed, length, dictionary);
            for (int cut = 0; cut < compressed.Length; cut++)
            {
                byte[] prefix = compressed[..cut];
                // A cut on a frame boundary leaves complete frames, which decode to a prefix of the content.
                byte[]? decoded = null;
                try { decoded = Zstandard.Decompress(prefix, length, dictionary); }
                catch (InvalidDataException) { }
                if (decoded is null)
                    Assert.Throws<InvalidDataException>(() => ReadAll(Zstandard.CreateDecompressionStream(new MemoryStream(prefix), length, dictionary)));
                else
                {
                    Assert.True(cut > 0 && decoded.Length < length && content.AsSpan().StartsWith(decoded), $"{name} cut at {cut}");
                    using var stream = Zstandard.CreateDecompressionStream(new MemoryStream(prefix), length, dictionary);
                    var streamed = new MemoryStream();
                    stream.CopyTo(streamed);
                    Assert.Equal(decoded, streamed.ToArray());
                }
            }
            byte[] trailing = [.. compressed, 0];
            Assert.Throws<InvalidDataException>(() => Zstandard.Decompress(trailing, length + 1, dictionary));
            Assert.Throws<InvalidDataException>(() => ReadAll(Zstandard.CreateDecompressionStream(new MemoryStream(trailing), length + 1, dictionary)));
        }
    }

    [Fact]
    public void RejectsInconsistentHeadersBlocksAndChecksums()
    {
        byte[] hand = Load("hand-rle-modes").Compressed;
        Assert.Throws<InvalidDataException>(() => Zstandard.Decompress(With(hand, 5, 21), 100));
        Assert.Throws<InvalidDataException>(() => Zstandard.Decompress(With(hand, 5, 23), 100));
        Assert.Throws<InvalidDataException>(() => Zstandard.Decompress(With(hand, 4, 0x28), 100));
        Assert.Throws<InvalidDataException>(() => Zstandard.Decompress(With(hand, 6, 0x46), 100));
        // A repeated sequence table needs an earlier table in the frame.
        Assert.Throws<InvalidDataException>(() => Zstandard.Decompress(With(hand, 23, 0xFC), 100));
        // An offset beyond the bytes produced so far.
        Assert.Throws<InvalidDataException>(() => Zstandard.Decompress(With(hand, 25, 6), 100));
        Assert.Throws<InvalidDataException>(() => Zstandard.DecompressExact(hand, new byte[21]));
        Assert.Throws<InvalidDataException>(() => Zstandard.DecompressExact(hand, new byte[23]));
        Assert.Throws<InvalidDataException>(() => Zstandard.Decompress([], 100));

        var (compressed, length, _, _) = Load("text-checksum");
        Assert.Throws<InvalidDataException>(() => Zstandard.Decompress(With(compressed, compressed.Length - 1, (byte)(compressed[^1] ^ 1)), length));
        Assert.Throws<InvalidDataException>(() => ReadAll(Zstandard.CreateDecompressionStream(
            new MemoryStream(With(compressed, compressed.Length - 1, (byte)(compressed[^1] ^ 1))), length)));
        Assert.Throws<InvalidDataException>(() => ReadAll(Zstandard.CreateDecompressionStream(new MemoryStream(compressed), length - 1)));
        Assert.Throws<InvalidDataException>(() => ReadAll(Zstandard.CreateDecompressionStream(new MemoryStream(), 10)));
    }

    // Hostile input ends in InvalidDataException or a successful decode, never another failure.
    [Theory]
    [InlineData("tiny")] [InlineData("hand-rle-modes")] [InlineData("skewed-literals")] [InlineData("dictionary-frames")]
    [InlineData("raw-dictionary-rle-literals")] [InlineData("text-checksum")]
    public void SurvivesCorruptedBytes(string name)
    {
        var (compressed, length, _, dictionary) = Load(name);
        var random = new Random(8878);
        for (int i = 0; i < 1500; i++)
        {
            byte[] corrupt = (byte[])compressed.Clone();
            int position = random.Next(corrupt.Length);
            corrupt[position] ^= (byte)(1 << random.Next(8));
            if (random.Next(4) == 0)
                corrupt[random.Next(corrupt.Length)] = (byte)random.Next(256);
            try { Zstandard.Decompress(corrupt, length * 2 + 64, dictionary); }
            catch (InvalidDataException) { }
        }
    }

    [Fact]
    public void ValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Zstandard.Decompress([1], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Zstandard.DecompressExact([1], -1));
        Assert.Throws<ArgumentNullException>(() => Zstandard.CreateDecompressionStream(null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Zstandard.CreateDecompressionStream(new MemoryStream(), -1));
    }

    [Fact]
    public void XxHash64MatchesReferenceVectors()
    {
        // Values from the reference xxHash implementation.
        byte[] bytes = [.. Enumerable.Range(0, 768).Select(i => (byte)i), .. "xyz"u8];
        (byte[] Data, ulong Unseeded, ulong Seeded)[] vectors =
        [
            ([], 0xEF46DB3751D8E999, 0xC4349FC93C010000),
            ("a"u8.ToArray(), 0xD24EC4F1A98C6E5B, 0x9A7C6D2EA45568C9),
            ("abc"u8.ToArray(), 0x44BC2CF5AD770999, 0x2ED0F59D6B43AC8B),
            ("Nobody inspects the spammish repetition"u8.ToArray(), 0xFBCEA83C8A378BF1, 0xEB8B157CA26CBF34),
            (bytes, 0xE921A1B45BD779F8, 0x179CA0201A65D20E),
        ];
        const ulong Seed = 0x9E3779B97F4A7C15;
        foreach (var (data, unseeded, seeded) in vectors)
        {
            Assert.Equal(unseeded, XxHash64.Hash(data));
            Assert.Equal(seeded, XxHash64.Hash(data, Seed));
            var incremental = new XxHash64(Seed);
            for (int offset = 0, step = 1; offset < data.Length; offset += step, step = step * 3 % 37 + 1)
                incremental.Append(data.AsSpan(offset, Math.Min(step, data.Length - offset)));
            Assert.Equal(seeded, incremental.GetCurrentHash());
            incremental.Reset();
            incremental.Append(data);
            Assert.Equal(seeded, incremental.GetCurrentHash());
        }
    }

    private static (byte[] Compressed, int Length, string Sha256, ZstandardDictionary? Dictionary) Load(string name)
    {
        var item = Manifest.GetProperty("fixtures").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
        ZstandardDictionary? dictionary = item.TryGetProperty("dictionary", out var file)
            ? new ZstandardDictionary(File.ReadAllBytes(Path.Combine(Root, file.GetString()!)), item.TryGetProperty("rawDictionary", out _))
            : null;
        return (File.ReadAllBytes(Path.Combine(Root, name + ".zst")), item.GetProperty("length").GetInt32(), item.GetProperty("sha256").GetString()!, dictionary);
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] With(byte[] data, int index, byte value)
    {
        byte[] copy = (byte[])data.Clone();
        copy[index] = value;
        return copy;
    }

    private static void ReadAll(Stream stream)
    {
        using (stream)
            stream.CopyTo(Stream.Null);
    }
}
