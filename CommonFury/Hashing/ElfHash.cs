namespace CommonFury.Hashing;

/// <summary>Case-sensitive ELF hashes of ASCII identifiers.</summary>
public static class ElfHash
{
    /// <summary>A NUL ends the identifier; non-ASCII text is rejected.</summary>
    public static uint Hash(ReadOnlySpan<char> text)
    {
        uint hash = 0;
        foreach (char character in text)
        {
            if (character == '\0')
                break;
            if (character > 127)
                throw new ArgumentException("ELF identifier hashing requires ASCII text.", nameof(text));
            hash = Accumulate(hash, character);
        }
        return hash;
    }

    /// <summary>Appends one character code; the result is the hash of all codes so far.</summary>
    public static uint Accumulate(uint hash, uint code)
    {
        hash = unchecked((hash << 4) + code);
        uint high = hash & 0xF0000000;
        return (hash ^ (high >> 24)) & ~high;
    }
}
