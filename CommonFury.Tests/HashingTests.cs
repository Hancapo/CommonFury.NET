using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CommonFury.Hashing;
using Xunit;

namespace CommonFury.Tests;

public sealed class HashingTests : IDisposable
{
    // Vectors produced independently of this library.
    private readonly JsonDocument reference = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ReferenceData", "hashing.json")));
    public void Dispose() => reference.Dispose();

    [Fact]
    public void JenkinsMatchesReferenceVectors()
    {
        Assert.True(Jenkins.Hash("a") == 3392050242 && Jenkins.Hash("ymap") == 3417157092, "Known independent Jenkins vectors.");
        Assert.Equal(0xC8E8C282u, Jenkins.Hash("DiffuseTex"));
        Assert.Equal(Jenkins.Hash("levels/example"), Jenkins.Hash("LEVELS\\EXAMPLE"));
        Assert.Equal(0u, Jenkins.Hash(""));
        foreach (JsonElement item in reference.RootElement.GetProperty("hashes").EnumerateArray())
        {
            string text = item.GetProperty("text").GetString()!;
            Assert.True(Jenkins.Hash(text) == item.GetProperty("hash").GetUInt32(), "Jenkins text mismatch.");
            Assert.True(Jenkins.HashUtf8(Encoding.UTF8.GetBytes(text)) == Jenkins.Hash(text), "UTF-8 hash mismatch.");
            Assert.True(Jenkins.Partial(text) == item.GetProperty("partial").GetUInt32(), "Partial Jenkins mismatch.");
            Assert.True(Jenkins.Continue(0x89ABCDEF, text) == item.GetProperty("continued").GetUInt32(), "Continued Jenkins mismatch.");
        }
        Assert.True(Jenkins.Finalize(Jenkins.Continue(Jenkins.Partial("prop_box.001"), "-3")) == Jenkins.Hash("prop_box.001-3"), "Incremental hash mismatch.");
        Assert.ThrowsAny<EncoderFallbackException>(() => Jenkins.Hash("\uD800"));
    }

    [Fact]
    public void ChecksumsMatchReferenceVectors()
    {
        foreach (JsonElement item in reference.RootElement.GetProperty("checksums").EnumerateArray())
        {
            byte[] bytes = item.GetProperty("data").GetBytesFromBase64();
            Assert.True(Jenkins.PsoChecksum(bytes) == item.GetProperty("pso").GetUInt32(), "Signed-byte PSO checksum mismatch.");
            Assert.True(Checksums.Fletcher32(bytes) == item.GetProperty("fletcher").GetUInt32(), "Fletcher32 mismatch.");
        }
        Assert.True(Checksums.Fletcher32([]) == uint.MaxValue, "Fletcher32 zero representation.");
        Assert.ThrowsAny<ArgumentException>(() => Checksums.Fletcher32([1]));
    }

    [Fact]
    public void Lookup3WordsMatchesReferenceVectors()
    {
        foreach (JsonElement item in reference.RootElement.GetProperty("words").EnumerateArray())
        {
            uint[] words = item.GetProperty("values").EnumerateArray().Select(x => x.GetUInt32()).ToArray();
            Assert.True(Jenkins.Lookup3Words(words, item.GetProperty("seed").GetUInt32()) == item.GetProperty("hash").GetUInt32(), "Lookup3 mismatch.");
        }
    }

    [Fact]
    public void OneAtATimeWordsMatchesTextbookDefinition()
    {
        var random = new Random(11);
        for (int count = 0; count < 9; count++)
        {
            byte[] data = new byte[count * 4];
            random.NextBytes(data);
            uint seed = (uint)random.Next();
            Assert.Equal(TextbookOneAtATime(data, seed), Jenkins.OneAtATimeWords(data, seed));
        }
        Assert.Throws<ArgumentException>(() => Jenkins.OneAtATimeWords(new byte[3], 0));
    }

    [Fact]
    public void Hash32IdentityIgnoresSpelling()
    {
        var named = new Hash32("CMapData");
        var hashed = new Hash32(named.Value);
        Assert.True(named == hashed && named.GetHashCode() == hashed.GetHashCode(), "Hash identity depends on spelling.");
        Assert.True(new Dictionary<Hash32, int> { [named] = 1 }.ContainsKey(hashed), "Hash dictionary equality.");
        Assert.Equal(new Hash32("DiffuseTex"), new Hash32(0xC8E8C282));
        Assert.Equal("0x0000002A", new Hash32(42).ToString());
    }

    [Fact]
    public void FileStemsAcceptExplicitHexOrNames()
    {
        Assert.Equal(0xC8E8C282u, Hash32.FromFileStem("0xC8E8C282").Value);
        Assert.Null(Hash32.FromFileStem("0xC8E8C282").Name);
        Assert.Equal(new Hash32("DiffuseTex"), Hash32.FromFileStem("DiffuseTex"));
        Assert.Equal(new Hash32("0x123"), Hash32.FromFileStem("0x123"));
    }

    [Fact]
    public void HashTextRoundTripsNamesAndNumericForms()
    {
        Assert.Equal("prop", HashText.Parse(" prop ").Name);
        Assert.Equal(0xABCu, HashText.Parse("0xabc").Value);
        Assert.Equal(0xABCu, HashText.Parse("hash_ABC").Value);
        Assert.Throws<FormatException>(() => HashText.Parse("0x123456789"));
        Assert.Equal("prop", HashText.Format(new Hash32("prop")));
        Assert.Equal("0x00000ABC", HashText.Format(new Hash32(0xABC)));
        var lookalike = new Hash32("0xabc");
        Assert.Equal(lookalike, HashText.Parse(HashText.Format(lookalike)));
    }

    [Fact]
    public void PartialHashesSkipFinalMixing()
    {
        var partial = new PartialHash32("CMapData");
        Assert.Equal(Jenkins.Partial("CMapData"), partial.Value);
        Assert.Equal(Jenkins.Hash("CMapData"), Jenkins.Finalize(partial.Value));
        Assert.Equal(partial, new PartialHash32(partial.Value));
    }

    [Fact]
    public void ElfHashAccumulatesAsciiAndStopsAtNul()
    {
        Assert.Equal(0u, ElfHash.Hash(""));
        Assert.Equal(TextbookElf("Root_Bone"), ElfHash.Hash("Root_Bone"));
        Assert.Equal(ElfHash.Hash("abc"), ElfHash.Hash("abc\0def"));
        Assert.Equal(ElfHash.Hash("ab"), ElfHash.Accumulate(ElfHash.Hash("a"), 'b'));
        Assert.Throws<ArgumentException>(() => ElfHash.Hash("é"));
    }

    [Fact]
    public void ResolverRetainsFirstSpellingSupportsBulkSourcesAndHasExplicitLifetime()
    {
        var resolver = new HashResolver();
        Assert.Null(resolver.RegisterName("  "));
        Assert.Equal(new Hash32("Prop"), resolver.RegisterName(" Prop "));
        resolver.RegisterName("PROP");
        Assert.Equal("Prop", resolver.ResolveName(new Hash32(new Hash32("prop").Value)));
        Assert.Equal("literal", resolver.ResolveName(new Hash32("literal")));
        Assert.Null(resolver.ResolveName(new Hash32(0xDEADBEEF)));
        Assert.Equal(2, resolver.RegisterNames(new StringReader("# comment\n; comment\n// comment\ncar\nboat\n")).Count);
        Assert.Single(resolver.RegisterPaths([@"folder\asset.ydr"]));
        Assert.Equal("asset", resolver.ResolveName(new Hash32(new Hash32("asset").Value)));
        Assert.Equal(5, resolver.Names.Count);
        resolver.Clear();
        Assert.Empty(resolver.Names);
        Assert.Null(resolver.ResolveName(new Hash32(new Hash32("prop").Value)));
    }

    private static uint TextbookOneAtATime(ReadOnlySpan<byte> data, uint hash)
    {
        unchecked
        {
            for (int i = 0; i < data.Length; i += 4)
            {
                hash += BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
                hash += hash << 10;
                hash ^= hash >> 6;
            }
            hash += hash << 3;
            hash ^= hash >> 11;
            hash += hash << 15;
            return hash;
        }
    }

    private static uint TextbookElf(string text)
    {
        uint hash = 0;
        foreach (char c in text)
        {
            hash = (hash << 4) + c;
            uint high = hash & 0xF0000000;
            if (high != 0)
                hash ^= high >> 24;
            hash &= ~high;
        }
        return hash;
    }
}
