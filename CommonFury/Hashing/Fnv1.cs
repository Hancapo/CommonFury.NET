namespace CommonFury.Hashing;

/// <summary>FNV-1 hashing, which multiplies by the prime before mixing in each byte (FNV-1a mixes first).</summary>
public static class Fnv1
{
    public static ulong Hash64(ReadOnlySpan<byte> data)
    {
        ulong hash = 0xCBF29CE484222325;
        foreach (byte value in data)
            hash = unchecked(hash * 0x100000001B3) ^ value;
        return hash;
    }
}
