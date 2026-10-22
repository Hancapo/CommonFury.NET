using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace CommonFury.Hashing;

/// <summary>Jenkins one-at-a-time hashes for names and data, plus lookup3 for word arrays.</summary>
public static class Jenkins
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Hashes a name as UTF-8, lowercasing ASCII and mapping <c>\</c> to <c>/</c>.
    /// A leading quote limits hashing to the quoted name; a NUL ends the input.</summary>
    public static uint Hash(ReadOnlySpan<char> text) => Finalize(Partial(text));
    /// <inheritdoc cref="Hash"/>
    public static uint HashUtf8(ReadOnlySpan<byte> text) => Finalize(PartialUtf8(text));
    /// <summary>Hashes text without case or slash normalization, as parser type and member names are hashed.</summary>
    public static uint HashCaseSensitive(ReadOnlySpan<char> text)
    {
        int length = Utf8.GetByteCount(text);
        Span<byte> bytes = length <= 512 ? stackalloc byte[length] : new byte[length];
        Utf8.GetBytes(text, bytes);
        uint state = 0;
        foreach (byte value in bytes)
        { if (value == 0) break; state = MixByte(state, value); }
        return Finalize(state);
    }
    /// <summary>Like <see cref="Hash"/> without the final mix; pass the state to <see cref="Continue"/> or <see cref="Finalize"/>.</summary>
    public static uint Partial(ReadOnlySpan<char> text) => AccumulateText(0, text, true);
    /// <summary>Appends normalized text to a partial state. Quotes are ordinary characters; a NUL ends the input.</summary>
    public static uint Continue(uint state, ReadOnlySpan<char> text) => AccumulateText(state, text, false);

    private static uint AccumulateText(uint state, ReadOnlySpan<char> text, bool quoted)
    {
        int count = Utf8.GetByteCount(text);
        Span<byte> bytes = count <= 512 ? stackalloc byte[count] : new byte[count];
        Utf8.GetBytes(text, bytes);
        return quoted ? PartialUtf8(bytes) : ContinueUtf8(state, bytes);
    }

    /// <inheritdoc cref="Partial"/>
    public static uint PartialUtf8(ReadOnlySpan<byte> text)
    {
        bool quoted = !text.IsEmpty && text[0] == '"';
        if (quoted)
            text = text[1..];
        return Accumulate(0, text, quoted);
    }

    /// <inheritdoc cref="Continue"/>
    public static uint ContinueUtf8(uint state, ReadOnlySpan<byte> text) => Accumulate(state, text, false);

    private static uint Accumulate(uint state, ReadOnlySpan<byte> text, bool quoted)
    {
        foreach (byte raw in text)
        {
            if (raw == 0 || (quoted && raw == '"'))
                break;
            byte value = raw is >= (byte)'A' and <= (byte)'Z' ? (byte)(raw + 32)
                : raw == '\\' ? (byte)'/' : raw;
            state = MixByte(state, value);
        }
        return state;
    }

    internal static uint MixByte(uint state, uint value)
    {
        uint sum = unchecked((state + value) * 1025u);
        return sum ^ (sum >> 6);
    }

    public static uint Finalize(uint state)
    {
        uint tail = unchecked(state * 9);
        return unchecked((tail ^ (tail >> 11)) * 32769);
    }

    /// <summary>PSO CHKS checksum: signed bytes, seed 0x3FAC7125, no normalization.</summary>
    public static uint PsoChecksum(ReadOnlySpan<byte> data)
    {
        uint state = 0x3FAC7125;
        foreach (byte value in data)
            state = MixByte(state, unchecked((uint)(sbyte)value));
        return Finalize(state);
    }

    /// <summary>Bob Jenkins' lookup3 <c>hashword</c>. Unrelated to <see cref="OneAtATimeWords"/>.</summary>
    public static uint Lookup3Words(ReadOnlySpan<uint> words, uint initialValue = 0)
    {
        unchecked
        {
            uint a = 0xDEADBEEF + (uint)words.Length * 4 + initialValue, b = a, c = a;
            while (words.Length > 3)
            {
                a += words[0];
                b += words[1];
                c += words[2];
                a = (a - c) ^ BitOperations.RotateLeft(c, 4);
                c += b;
                b = (b - a) ^ BitOperations.RotateLeft(a, 6);
                a += c;
                c = (c - b) ^ BitOperations.RotateLeft(b, 8);
                b += a;
                a = (a - c) ^ BitOperations.RotateLeft(c, 16);
                c += b;
                b = (b - a) ^ BitOperations.RotateLeft(a, 19);
                a += c;
                c = (c - b) ^ BitOperations.RotateLeft(b, 4);
                b += a;
                words = words[3..];
            }
            if (words.Length == 3)
                c += words[2];
            if (words.Length >= 2)
                b += words[1];
            if (words.Length >= 1)
            {
                a += words[0];
                c = (c ^ b) - BitOperations.RotateLeft(b, 14);
                a = (a ^ c) - BitOperations.RotateLeft(c, 11);
                b = (b ^ a) - BitOperations.RotateLeft(a, 25);
                c = (c ^ b) - BitOperations.RotateLeft(b, 16);
                a = (a ^ c) - BitOperations.RotateLeft(c, 4);
                b = (b ^ a) - BitOperations.RotateLeft(a, 14);
                c = (c ^ b) - BitOperations.RotateLeft(b, 24);
            }
            return c;
        }
    }

    /// <summary>One-at-a-time hash mixing each little-endian 32-bit word as one step, finalized once. Unrelated to <see cref="Lookup3Words"/>.</summary>
    public static uint OneAtATimeWords(ReadOnlySpan<byte> data, uint seed)
    {
        if (data.Length % 4 != 0)
            throw new ArgumentException("Word hashing requires complete 32-bit values.", nameof(data));
        uint state = seed;
        for (int i = 0; i < data.Length; i += 4)
            state = MixByte(state, BinaryPrimitives.ReadUInt32LittleEndian(data[i..]));
        return Finalize(state);
    }
}
