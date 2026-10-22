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
