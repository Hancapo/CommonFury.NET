using System.Buffers.Binary;

namespace CommonFury.Hashing;

/// <summary>Calculates checksums over binary payloads without interpreting their asset format.</summary>
public static class Checksums
{
    /// <summary>Fletcher-32 over little-endian 16-bit words; requires an even length. Zero is represented as all ones.</summary>
    public static uint Fletcher32(ReadOnlySpan<byte> data)
    {
        if (data.Length % 2 != 0)
            throw new ArgumentException("Fletcher32 requires complete UInt16 words.", nameof(data));
        uint first = 0xFFFF, second = 0xFFFF;
        while (!data.IsEmpty)
        {
            int count = Math.Min(data.Length, 720);
            for (int i = 0; i < count; i += 2)
            {
                first += BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
                second += first;
            }
            first = (first & 0xFFFF) + (first >> 16);
            second = (second & 0xFFFF) + (second >> 16);
            data = data[count..];
        }
        first = (first & 0xFFFF) + (first >> 16);
        second = (second & 0xFFFF) + (second >> 16);
        return (second << 16) | first;
    }

    /// <summary>CRC-32 with the reflected polynomial 0xEDB88320 and an initial and final XOR of all ones, as in zlib and PNG.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
            crc = Crc32Table[(byte)crc ^ value] ^ (crc >> 8);
        return ~crc;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    // RFC 1950 Adler-32. 5,552 bytes is the longest run whose sums cannot overflow before the modulo.
    internal static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Modulus = 65521;
        uint first = 1, second = 0;
        while (!data.IsEmpty)
        {
            int count = Math.Min(data.Length, 5552);
            foreach (byte value in data[..count])
            {
                first += value;
                second += first;
            }
            first %= Modulus;
            second %= Modulus;
            data = data[count..];
        }
        return (second << 16) | first;
    }
}
