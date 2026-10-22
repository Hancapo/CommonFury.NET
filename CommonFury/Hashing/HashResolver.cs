using System.Collections.ObjectModel;

namespace CommonFury.Hashing;

/// <summary>Mutable name registry. The first spelling registered for a hash wins. Not thread safe.</summary>
public sealed class HashResolver
{
    private readonly Dictionary<Hash32, string> byHash = [];
    private readonly Dictionary<string, Hash32> byName = new(StringComparer.Ordinal);
    /// <summary>Every registered spelling, including ones that collide.</summary>
    public IReadOnlyDictionary<string, Hash32> Names { get; }

    public HashResolver() => Names = new ReadOnlyDictionary<string, Hash32>(byName);

    /// <summary>Trims and registers a name; returns null for blank input.</summary>
    public Hash32? RegisterName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string normalized = name.Trim();
        if (normalized.Length == 0)
            return null;
        var hash = new Hash32(normalized);
        byName[normalized] = hash;
        byHash.TryAdd(hash, normalized);
        return hash;
    }

    /// <summary>Registers nonblank names and returns their hashes in order.</summary>
    public IReadOnlyList<Hash32> RegisterNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var result = new List<Hash32>();
        foreach (string name in names)
            if (RegisterName(name) is { } hash)
                result.Add(hash);
        return result;
    }

    /// <summary>Reads one name per line, skipping blank lines and <c>#</c>, <c>;</c> or <c>//</c> comments. Does not close the reader.</summary>
    public IReadOnlyList<Hash32> RegisterNames(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        IEnumerable<string> ReadNames()
        {
            while (reader.ReadLine() is { } line)
            {
                string name = line.Trim();
                if (name.Length > 0 && !name.StartsWith('#') && !name.StartsWith(';') && !name.StartsWith("//", StringComparison.Ordinal))
                    yield return name;
            }
        }
        return RegisterNames(ReadNames());
    }

    /// <inheritdoc cref="RegisterNames(TextReader)"/>
    public IReadOnlyList<Hash32> RegisterNamesFile(string path)
    {
        using var reader = File.OpenText(path);
        return RegisterNames(reader);
    }

    /// <summary>Registers filename stems (the name without its last extension), accepting either slash style.</summary>
    public IReadOnlyList<Hash32> RegisterPaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return RegisterNames(paths.Select(path => Path.GetFileNameWithoutExtension(path.Replace('\\', '/'))));
    }

    /// <summary>Registers the filename stems of a file or of the files in a directory.</summary>
    public IReadOnlyList<Hash32> RegisterDirectory(string path, bool recursive = true) => File.Exists(path)
        ? RegisterPaths([path])
        : RegisterPaths(Directory.EnumerateFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly));

    /// <summary>Returns the hash's retained spelling, else its registered name, else null.</summary>
    public string? ResolveName(Hash32 value) => value.Name ?? byHash.GetValueOrDefault(value);
    public void Clear() { byHash.Clear(); byName.Clear(); }
}
