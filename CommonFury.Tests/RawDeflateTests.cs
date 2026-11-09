using System.IO.Compression;
using CommonFury.Binary;
using CommonFury.Compression;
using Xunit;

namespace CommonFury.Tests;

[Collection(nameof(CompressionCacheTests))]
public sealed class RawDeflateTests
{
    // Node zlib.deflateRawSync(Buffer.alloc(32, 65)); independent of the library writer.
    private static readonly byte[] DeflateFixture = Convert.FromHexString("7374C40F00");
    private static readonly byte[] Decoded = Enumerable.Repeat((byte)'A', 32).ToArray();

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReadsExternalFixtureInBothModes(bool zeroHistory)
    {
        Assert.Equal(Decoded, RawDeflate.DecompressExact(DeflateFixture, 32, zeroHistory));
        Assert.Equal(Decoded, RawDeflate.Decompress(DeflateFixture, 32, zeroHistory));
        Assert.Equal(Decoded, RawDeflate.Decompress(DeflateFixture, 1000, zeroHistory));
    }

    [Theory]
    [InlineData(0)] [InlineData(32)] [InlineData(262160)]
    public void CompressRoundTripsAndEmitsAFinalBlockForEmptyInput(int length)
    {
        byte[] data = Enumerable.Repeat((byte)'B', length).ToArray();
        byte[] compressed = RawDeflate.Compress(data);
        if (length == 0)
            Assert.Equal(new byte[] { 3, 0 }, compressed);
        Assert.Equal(data, Inflate(compressed));
        Assert.Equal(data, RawDeflate.DecompressExact(compressed, length));
        Assert.Equal(data, RawDeflate.Decompress(compressed, length, zeroHistory: true));
        Assert.Equal(data, RawDeflate.Decompress(RawDeflate.Compress(data, CompressionLevel.Fastest), length));
    }

    [Fact]
    public void RejectsMalformedTruncatedTrailingAndWrongLength()
    {
        for (int length = 0; length < DeflateFixture.Length; length++)
        {
            Assert.Throws<InvalidDataException>(() => RawDeflate.DecompressExact(DeflateFixture[..length], 32));
            Assert.Throws<InvalidDataException>(() => RawDeflate.Decompress(DeflateFixture[..length], 32));
        }
        Assert.Throws<InvalidDataException>(() => RawDeflate.DecompressExact([7], 32));
        Assert.Throws<InvalidDataException>(() => RawDeflate.DecompressExact(DeflateFixture, 16));
        Assert.Throws<InvalidDataException>(() => RawDeflate.DecompressExact(DeflateFixture, 48));
        Assert.Throws<InvalidDataException>(() => RawDeflate.DecompressExact(DeflateFixture, 0));
        Assert.Throws<InvalidDataException>(() => RawDeflate.Decompress(DeflateFixture, 31));
        Assert.Throws<InvalidDataException>(() => RawDeflate.DecompressExact([.. DeflateFixture, 0], 32));
        Assert.Throws<InvalidDataException>(() => RawDeflate.Decompress([.. DeflateFixture, 0], 32));
        // A complete non-final stored block emits all declared bytes but still needs a final block.
        byte[] nonFinal = [0, 32, 0, 223, 255, .. Decoded];
        Assert.Throws<InvalidDataException>(() => RawDeflate.DecompressExact(nonFinal, 32));
        Assert.Equal(Decoded, RawDeflate.DecompressExact([.. nonFinal, 3, 0], 32));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DecompressEnforcesTheLimitAtEverySize(bool zeroHistory)
    {
        byte[] data = new byte[200_001];
        new Random(3).NextBytes(data.AsSpan(0, 100_000));
        byte[] compressed = RawDeflate.Compress(data, CompressionLevel.Fastest);
        foreach (int limit in new[] { data.Length, data.Length + 1, 1 << 20, int.MaxValue })
            Assert.Equal(data, RawDeflate.Decompress(compressed, limit, zeroHistory));
        foreach (int limit in new[] { 0, 1, 64 * 1024, 64 * 1024 + 1, 131_072, data.Length - 1 })
            Assert.Throws<InvalidDataException>(() => RawDeflate.Decompress(compressed, limit, zeroHistory));
        Assert.Empty(RawDeflate.Decompress([3, 0], 0, zeroHistory));
        Assert.Throws<InvalidDataException>(() => RawDeflate.Decompress(compressed[..^1], data.Length, zeroHistory));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RejectsOutputOneByteBeyondAGrownBuffer(bool zeroHistory)
    {
        // 65,535 + 3 stored bytes: the buffer grows from 64 KiB to the 65,537-byte limit, and the probe byte fills it.
        byte[] data = [.. Enumerable.Range(0, 65538).Select(i => (byte)i)];
        byte[] stream = [0, 0xFF, 0xFF, 0, 0, .. data[..65535], 1, 3, 0, 0xFC, 0xFF, .. data[65535..]];
        Assert.Equal(data, RawDeflate.Decompress(stream, 65538, zeroHistory));
        Assert.Equal("Decoded DEFLATE output exceeds its size limit.", Assert.Throws<InvalidDataException>(() => RawDeflate.Decompress(stream, 65537, zeroHistory)).Message);
    }

    [Fact]
    public void ReportsCorruptStreamsWithTheDecodedOffset()
    {
        // A stored block of 32 bytes, then a final block with the reserved type 3.
        byte[] corrupt = [0, 32, 0, 223, 255, .. Decoded, 7];
        foreach (bool zeroHistory in new[] { false, true })
        {
            var exception = Assert.Throws<InvalidDataException>(() => RawDeflate.Decompress(corrupt, 64, zeroHistory));
            Assert.Equal("Corrupt DEFLATE stream: decoding fails after 32 output bytes.", exception.Message);
            Assert.Equal("Corrupt DEFLATE stream: decoding fails after 32 output bytes.", Assert.Throws<InvalidDataException>(() => RawDeflate.DecompressExact(corrupt, 40, zeroHistory)).Message);
        }
        Assert.Equal("Truncated DEFLATE stream.", Assert.Throws<InvalidDataException>(() => RawDeflate.Decompress(DeflateFixture[..2], 32)).Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DecompressPrefixStopsAfterTheRequestedBytes(bool zeroHistory)
    {
        byte[] data = new byte[200_001];
        new Random(5).NextBytes(data);
        byte[] compressed = RawDeflate.Compress(data, CompressionLevel.Fastest);
        foreach (int length in new[] { 0, 1, 64 * 1024, 64 * 1024 + 1, 131_072, data.Length - 1, data.Length })
            Assert.Equal(data[..length], RawDeflate.DecompressPrefix(compressed, length, zeroHistory));
        // A stream shorter than the request returns all of it.
        Assert.Equal(data, RawDeflate.DecompressPrefix(compressed, int.MaxValue, zeroHistory));
        Assert.Equal(Decoded, RawDeflate.DecompressPrefix(DeflateFixture, 1000, zeroHistory));
    }

    [Fact]
    public void DecompressPrefixIgnoresDataAfterThePrefixButNotDamageInsideIt()
    {
        // A stored block of 32 bytes, then a final block with the reserved type 3.
        byte[] corrupt = [0, 32, 0, 223, 255, .. Decoded, 7];
        Assert.Equal(Decoded[..16], RawDeflate.DecompressPrefix(corrupt, 16));
        Assert.Equal(Decoded[..16], RawDeflate.DecompressPrefix([.. DeflateFixture, 0], 16));
        Assert.Equal("Corrupt DEFLATE stream: decoding fails after 32 output bytes.",
            Assert.Throws<InvalidDataException>(() => RawDeflate.DecompressPrefix(corrupt, 40)).Message);
        Assert.Throws<InvalidDataException>(() => RawDeflate.DecompressPrefix(DeflateFixture[..2], 32));
        Assert.Throws<InvalidDataException>(() => RawDeflate.DecompressPrefix([], 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => RawDeflate.DecompressPrefix(DeflateFixture, -1));
    }

    [Fact]
    public void ZeroHistoryResolvesReferencesBeforeTheFirstByte()
    {
        // Fixed-Huffman block: one length-3 match at distance 1, then end of block. Nothing precedes it.
        var bytes = new byte[3];
        var writer = new BinaryBitWriter(bytes);
        writer.WriteUInt32(1, 1); // BFINAL
        writer.WriteUInt32(1, 2); // BTYPE = fixed Huffman
        writer.WriteUInt32(BitField32.ReverseLowBits(1, 7), 7); // symbol 257: length 3, Huffman codes are MSB-first
        writer.WriteUInt32(0, 5); // distance code 0: distance 1
        writer.WriteUInt32(0, 7); // symbol 256: end of block
        Assert.Equal(new byte[3], RawDeflate.DecompressExact(bytes, 3, zeroHistory: true));
        Assert.Equal(new byte[3], RawDeflate.Decompress(bytes, 3, zeroHistory: true));
        Assert.ThrowsAny<InvalidDataException>(() => RawDeflate.DecompressExact(bytes, 3));
    }

    private static byte[] Inflate(byte[] payload)
    {
        using var source = new MemoryStream(payload);
        using var inflater = new DeflateStream(source, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        inflater.CopyTo(decoded);
        return decoded.ToArray();
    }
}
