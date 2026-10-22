using System.Globalization;

namespace CommonFury.Hashing;

/// <summary>Jenkins name hash. Equality uses only the numeric value; a retained spelling is for display.</summary>
public readonly struct Hash32 : IEquatable<Hash32>
{
    public uint Value { get; }
    public string? Name { get; }
    public Hash32(uint value) { Value = value; Name = null; }
    /// <summary>Parses an explicit <c>0xXXXXXXXX</c> filename stem, otherwise hashes the stem as a name.</summary>
    public static Hash32 FromFileStem(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        if (stem.Length == 10 && stem.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(stem.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
            return new(value);
        return new(stem);
    }
    /// <summary>Hashes <paramref name="name"/> and retains its spelling; an empty name retains none.</summary>
    public Hash32(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Value = Jenkins.Hash(name);
        Name = name.Length == 0 ? null : name;
    }
    public bool Equals(Hash32 other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is Hash32 other && Equals(other);
    public override int GetHashCode() => unchecked((int)Value);
    public static bool operator ==(Hash32 left, Hash32 right) => left.Equals(right);
    public static bool operator !=(Hash32 left, Hash32 right) => !left.Equals(right);
    /// <summary>The retained spelling, or the value as <c>0x</c> and eight hex digits.</summary>
    public override string ToString() => Name ?? $"0x{Value.ToString("X8", CultureInfo.InvariantCulture)}";
}
