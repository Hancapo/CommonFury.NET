using System.Globalization;

namespace CommonFury.Hashing;

/// <summary>Unfinalized Jenkins state, as used by cinematic streaming names. Equality uses only the numeric state.</summary>
public readonly struct PartialHash32 : IEquatable<PartialHash32>
{
    public uint Value { get; }
    public string? Name { get; }
    public PartialHash32(uint value) { Value = value; Name = null; }
    /// <summary>Computes the partial hash of <paramref name="name"/> and retains its spelling.</summary>
    public PartialHash32(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Value = Jenkins.Partial(name);
        Name = name.Length == 0 ? null : name;
    }
    public bool Equals(PartialHash32 other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is PartialHash32 other && Equals(other);
    public override int GetHashCode() => unchecked((int)Value);
    public static bool operator ==(PartialHash32 left, PartialHash32 right) => left.Equals(right);
    public static bool operator !=(PartialHash32 left, PartialHash32 right) => !left.Equals(right);
    /// <summary>The retained spelling, or the value as <c>0x</c> and eight hex digits.</summary>
    public override string ToString() => Name ?? $"0x{Value.ToString("X8", CultureInfo.InvariantCulture)}";
}
