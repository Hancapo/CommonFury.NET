using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using CommonFury.Hashing;

namespace CommonFury.Pso;

/// <summary>Resolves PSO identifiers (type, field, enum member and hashed-string hashes) to display spellings.
/// Implement it over a file's STRF/STRS tables (<see cref="PsoNameTable"/>) or an external catalog.
/// Spellings are labels only: PSO identifiers are not necessarily hashes of their spelling.</summary>
public interface IPsoNameResolver
{
    string? ResolveName(uint hash);
}

/// <summary>Immutable hash-to-name lookup over Jenkins-hashed names, such as a file's STRF/STRS tables.</summary>
public sealed class PsoNameTable : IPsoNameResolver
{
    private readonly Dictionary<uint, Hash32> names;
    public IReadOnlyDictionary<uint, Hash32> Names => this.names;
    /// <summary>Copies named hashes; the first spelling for a numeric hash wins. Unnamed hashes are ignored.</summary>
    public PsoNameTable(IEnumerable<Hash32> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        this.names = [];
        foreach (Hash32 name in names)
            if (name.Name is not null)
                this.names.TryAdd(name.Value, name);
    }
    /// <summary>Reads the final (STRF) table, then fills gaps from the debug (STRS) table. Encrypted STRE strings are ignored.</summary>
    public static PsoNameTable Read(PsoFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new(PsoHashStrings.Read(file, PsoSectionId.Strf).Values.Concat(PsoHashStrings.Read(file, PsoSectionId.Strs).Values));
    }
    public string? ResolveName(uint hash) => this.names.TryGetValue(hash, out Hash32 name) ? name.Name : null;
}

/// <summary>NUL-separated plaintext hash-name tables (STRF and STRS). Encrypted STRE strings are not interpreted.</summary>
public static class PsoHashStrings
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Reads every section with <paramref name="identifier"/>, rejecting unterminated names, ambiguous spellings and nonzero padding.</summary>
    public static Dictionary<uint, Hash32> Read(PsoFile file, uint identifier)
    {
        ArgumentNullException.ThrowIfNull(file);
        var names = new Dictionary<uint, Hash32>();
        foreach (PsoSection section in file.Sections.Where(section => section.Identifier == identifier))
        {
            ReadOnlySpan<byte> data = section.Data[8..];
            while (!data.IsEmpty && data[0] != 0)
            {
                int end = data.IndexOf((byte)0);
                if (end < 0)
                    throw new InvalidDataException("Unterminated PSO hash string.");
                var hash = new Hash32(Utf8.GetString(data[..end]));
                if (names.TryGetValue(hash.Value, out Hash32 previous) && previous.Name != hash.Name)
                    throw new InvalidDataException("Ambiguous PSO hash string collision.");
                names[hash.Value] = hash;
                data = data[(end + 1)..];
            }
            if (data.ContainsAnyExcept((byte)0))
                throw new InvalidDataException("Nonzero data after PSO hash string table padding.");
        }
        return names;
    }

    /// <summary>Encodes a table sorted by hash value.</summary>
    /// <exception cref="InvalidOperationException">A hash has no spelling, or a spelling contains NUL.</exception>
    public static PsoSection Write(uint identifier, IEnumerable<Hash32> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var bytes = new ArrayBufferWriter<byte>();
        bytes.Write(new byte[8]);
        foreach (Hash32 hash in names.OrderBy(hash => hash.Value))
        {
            string name = hash.Name ?? throw new InvalidOperationException("A PSO hash-name table requires source spelling.");
            if (name.Contains('\0'))
                throw new InvalidOperationException("PSO hash names cannot contain NUL.");
            bytes.Write(Utf8.GetBytes(name));
            bytes.Write<byte>([0]);
        }
        byte[] output = bytes.WrittenSpan.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(output, identifier);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), (uint)output.Length);
        return new(output);
    }
}
