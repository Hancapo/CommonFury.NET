using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using CommonFury.Compression;
using Xunit;

namespace CommonFury.Tests;

public sealed class XMemCompressTests
{
    // Fixtures are makecab LZX streams over synthetic content, framed as XMemCompress chunks; the manifest records each
    // content's SHA-256. Cabinet LZX enables E8 call translation, which calls-w15 exercises.
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "ReferenceData", "xmemcompress");
    private static readonly JsonElement Manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "xmemcompress.json"))).RootElement;

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
        var (compressed, length, sha256) = Load(name);
        Assert.Equal(sha256, Hash(XMemCompress.Decompress(compressed, length)));
        Assert.Equal(sha256, Hash(XMemCompress.Decompress(compressed, int.MaxValue)));
        Assert.Equal(sha256, Hash(XMemCompress.DecompressExact(compressed, length)));
        var destination = new byte[length];
        XMemCompress.DecompressExact(compressed, destination);
        Assert.Equal(sha256, Hash(destination));
    }

    [Fact]
    public void RejectsHeadersThatDisagreeWithTheData()
    {
        var (compressed, length, _) = Load("text-w15");
        Assert.Throws<InvalidDataException>(() => XMemCompress.Decompress(compressed, length - 1));
        Assert.Throws<InvalidDataException>(() => XMemCompress.DecompressExact(compressed, length + 1));
        Assert.Throws<InvalidDataException>(() => XMemCompress.DecompressExact(compressed[..47], length));
        foreach (var (offset, value) in new[] { (0, 0x0FF512EFu), (4, 0x01020000u), (12, 1u), (16, 0x4000u), (16, 0x30000u), (36, 1u) })
        {
            var copy = (byte[])compressed.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(offset), value);
            Assert.Throws<InvalidDataException>(() => XMemCompress.DecompressExact(copy, length));
        }
        var larger = (byte[])compressed.Clone();
        BinaryPrimitives.WriteInt64BigEndian(larger.AsSpan(24), length + 1);
        Assert.Throws<InvalidDataException>(() => XMemCompress.DecompressExact(larger, length + 1));
    }

    [Fact]
    public void RejectsTruncatedAndTrailingChunks()
    {
        var (compressed, length, _) = Load("records-w21-frames");
        for (int cut = 49; cut < compressed.Length; cut += 997)
            Assert.Throws<InvalidDataException>(() => XMemCompress.DecompressExact(compressed[..cut], length));
        // Bytes after a chunk's end marker must be zero, and a frame may not declare more output than the stream.
        var trailing = (byte[])compressed.Clone();
        trailing[^1] = 1;
        Assert.Throws<InvalidDataException>(() => XMemCompress.DecompressExact(trailing, length));
        var (two, twoLength, _) = Load("two-chunks-w15");
        Assert.Throws<InvalidDataException>(() => XMemCompress.DecompressExact(Redeclare(two, twoLength - 1), twoLength - 1));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SurvivesCorruptedBytes(string name)
    {
        var (compressed, length, _) = Load(name);
        var random = new Random(name.Length);
        var destination = new byte[length];
        for (int i = 0; i < 300; i++)
        {
            var copy = (byte[])compressed.Clone();
            int position = random.Next(48, copy.Length);
            copy[position] ^= (byte)random.Next(1, 256);
            try
            {
                XMemCompress.DecompressExact(copy, destination);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Fact]
    public void ValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => XMemCompress.Decompress([], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => XMemCompress.DecompressExact([], -1));
    }

    private static byte[] Redeclare(byte[] compressed, long length)
    {
        var copy = (byte[])compressed.Clone();
        BinaryPrimitives.WriteInt64BigEndian(copy.AsSpan(24), length);
        return copy;
    }

    private static (byte[] Compressed, int Length, string Sha256) Load(string name)
    {
        foreach (var item in Manifest.GetProperty("fixtures").EnumerateArray())
        {
            if (item.GetProperty("name").GetString() == name)
                return (File.ReadAllBytes(Path.Combine(Root, name + ".xmc")), item.GetProperty("length").GetInt32(), item.GetProperty("sha256").GetString()!);
        }
        throw new KeyNotFoundException(name);
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
