using System.Globalization;

namespace CommonFury.Hashing;

/// <summary>Hash literals as written in asset XML.</summary>
public static class HashText
{
    /// <summary>Parses a name, or one to eight hex digits prefixed with <c>0x</c> or <c>hash_</c>. Surrounding whitespace is ignored.</summary>
    public static Hash32 Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ReadOnlySpan<char> value = text.AsSpan().Trim();
        int prefix = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2
            : value.StartsWith("hash_", StringComparison.OrdinalIgnoreCase) ? 5 : 0;
        if (prefix == 0)
            return new(value.ToString());
        ReadOnlySpan<char> digits = value[prefix..];
        if (digits.Length is < 1 or > 8 || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint hash))
            throw new FormatException("Hash literals require one to eight hexadecimal digits.");
        return new(hash);
    }

    /// <summary>Formats the retained name when parsing it back gives the same hash, otherwise <c>0x</c> and eight hex digits.</summary>
    public static string Format(Hash32 value)
    {
        // A literal name resembling hash syntax must be emitted numerically to preserve its identity.
        string? name = value.Name;
        if (name is not null && name.AsSpan().Trim().Length == name.Length &&
            !name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("hash_", StringComparison.OrdinalIgnoreCase))
            return name;
        return "0x" + value.Value.ToString("X8", CultureInfo.InvariantCulture);
    }
}
