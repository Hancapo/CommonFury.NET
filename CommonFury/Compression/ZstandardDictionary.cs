using System.Buffers.Binary;
using CommonFury.Binary;

namespace CommonFury.Compression;

/// <summary>A Zstandard dictionary: either the standard format (magic, ID, entropy tables, repeat offsets and content) or raw content.</summary>
/// <remarks>The constructor copies the data, so the instance is immutable and may be shared between threads and calls.</remarks>
public sealed class ZstandardDictionary
{
    private const uint Magic = 0xEC30A437;

    /// <summary>Reads a dictionary. Data starting with the dictionary magic is parsed as the standard format unless
    /// <paramref name="rawContent"/> is set; other data is raw content with ID 0.</summary>
    /// <exception cref="InvalidDataException">A standard-format dictionary is malformed.</exception>
    public ZstandardDictionary(ReadOnlySpan<byte> data, bool rawContent = false)
    {
        if (rawContent || data.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            Content = data.ToArray();
            return;
        }
        Id = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        ZstandardDecoder.ReadDictionaryTables(data[8..], out var huffman, out var offsets, out var matchLengths, out var literalLengths, out int length);
        var repeats = BinaryLayout.Slice(data, 8 + length, 12);
        Content = data[(20 + length)..].ToArray();
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(repeats), second = BinaryPrimitives.ReadUInt32LittleEndian(repeats[4..]);
        uint third = BinaryPrimitives.ReadUInt32LittleEndian(repeats[8..]);
        foreach (uint offset in (ReadOnlySpan<uint>)[first, second, third])
            BinaryLayout.Require(offset != 0 && offset <= Content.Length, "Zstandard dictionary repeat offset lies outside its content.");
        Huffman = huffman;
        Offsets = offsets;
        MatchLengths = matchLengths;
        LiteralLengths = literalLengths;
        RepeatOffsets = (first, second, third);
    }

    /// <summary>Dictionary ID that frames reference; zero for raw content.</summary>
    public uint Id { get; }

    /// <summary>Bytes that frames may reference as history before their first byte.</summary>
    public int ContentLength => Content.Length;

    /// <summary>Whether the dictionary carries entropy tables and repeat offsets.</summary>
    public bool HasEntropyTables => Huffman is not null;

    internal byte[] Content { get; }
    internal HuffmanTable? Huffman { get; }
    internal FseTable? Offsets { get; }
    internal FseTable? MatchLengths { get; }
    internal FseTable? LiteralLengths { get; }
    internal (uint, uint, uint)? RepeatOffsets { get; }
}
