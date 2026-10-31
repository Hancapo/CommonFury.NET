using System.Buffers.Binary;
using System.Numerics;

namespace CommonFury.Hashing;

/// <summary>64-bit xxHash (XXH64) of byte data, computed at once or incrementally.</summary>
/// <remarks>Incremental hashing over any split of the input gives the same result as <see cref="Hash"/>. Instances are not thread-safe.</remarks>
public sealed class XxHash64
{
    private const ulong Prime1 = 11400714785074694791, Prime2 = 14029467366897019727, Prime3 = 1609587929392839161;
    private const ulong Prime4 = 9650029242287828579, Prime5 = 2870177450012600261;
    private readonly ulong seed;
    private readonly byte[] pending = new byte[32];
    private ulong lane1, lane2, lane3, lane4, length;
    private int pendingLength;

    public XxHash64(ulong seed = 0)
    {
        this.seed = seed;
        Reset();
    }

    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        ulong v1 = seed + Prime1 + Prime2, v2 = seed + Prime2, v3 = seed, v4 = seed - Prime1;
        int consumed = Stripes(data, ref v1, ref v2, ref v3, ref v4);
        return Finish(data[consumed..], (ulong)data.Length, data.Length >= 32, v1, v2, v3, v4, seed);
    }

    /// <summary>Hashes more input; the data is not retained.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        length += (ulong)data.Length;
        if (pendingLength > 0)
        {
            int count = Math.Min(32 - pendingLength, data.Length);
            data[..count].CopyTo(pending.AsSpan(pendingLength));
            pendingLength += count;
            data = data[count..];
            if (pendingLength < 32)
                return;
            Stripes(pending, ref lane1, ref lane2, ref lane3, ref lane4);
            pendingLength = 0;
        }
        int consumed = Stripes(data, ref lane1, ref lane2, ref lane3, ref lane4);
        data[consumed..].CopyTo(pending);
        pendingLength = data.Length - consumed;
    }

    /// <summary>Hash of everything appended since construction or the last <see cref="Reset"/>; hashing can continue.</summary>
    public ulong GetCurrentHash() => Finish(pending.AsSpan(0, pendingLength), length, length >= 32, lane1, lane2, lane3, lane4, seed);

    public void Reset()
    {
        lane1 = seed + Prime1 + Prime2;
        lane2 = seed + Prime2;
        lane3 = seed;
        lane4 = seed - Prime1;
        length = 0;
        pendingLength = 0;
    }

    private static int Stripes(ReadOnlySpan<byte> data, ref ulong v1, ref ulong v2, ref ulong v3, ref ulong v4)
    {
        int offset = 0;
        for (; offset <= data.Length - 32; offset += 32)
        {
            var stripe = data.Slice(offset, 32);
            v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(stripe));
            v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(stripe[8..]));
            v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(stripe[16..]));
            v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(stripe[24..]));
        }
        return offset;
    }

    private static ulong Finish(ReadOnlySpan<byte> tail, ulong length, bool longInput, ulong v1, ulong v2, ulong v3, ulong v4, ulong seed)
    {
        ulong hash;
        if (longInput)
        {
            hash = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            hash = Merge(hash, v1);
            hash = Merge(hash, v2);
            hash = Merge(hash, v3);
            hash = Merge(hash, v4);
        }
        else
            hash = seed + Prime5;
        hash += length;
        int offset = 0;
        for (; offset <= tail.Length - 8; offset += 8)
            hash = BitOperations.RotateLeft(hash ^ Round(0, BinaryPrimitives.ReadUInt64LittleEndian(tail[offset..])), 27) * Prime1 + Prime4;
        if (offset <= tail.Length - 4)
        {
            hash = BitOperations.RotateLeft(hash ^ BinaryPrimitives.ReadUInt32LittleEndian(tail[offset..]) * Prime1, 23) * Prime2 + Prime3;
            offset += 4;
        }
        for (; offset < tail.Length; offset++)
            hash = BitOperations.RotateLeft(hash ^ tail[offset] * Prime5, 11) * Prime1;
        hash ^= hash >> 33;
        hash *= Prime2;
        hash ^= hash >> 29;
        hash *= Prime3;
        return hash ^ hash >> 32;
    }

    private static ulong Round(ulong accumulator, ulong lane) => BitOperations.RotateLeft(accumulator + lane * Prime2, 31) * Prime1;

    private static ulong Merge(ulong hash, ulong lane) => (hash ^ Round(0, lane)) * Prime1 + Prime4;
}
