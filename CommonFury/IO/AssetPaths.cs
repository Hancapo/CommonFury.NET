namespace CommonFury.IO;

/// <summary>Relative logical asset paths, independent of the host filesystem.</summary>
public static class AssetPaths
{
    /// <summary>Lowercase forward-slash form; rejects absolute paths, devices and <c>..</c>.</summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string value = path.Trim().Replace('\\', '/');
        if (value.Length == 0 || value[0] == '/' || value[^1] == '/' || value.Contains(':') || value.Contains('\0'))
            throw new ArgumentException("Expected a nonempty relative logical asset path with a filename.", nameof(path));
        string[] parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Contains("..", StringComparer.Ordinal))
            throw new ArgumentException("Parent traversal is not a logical asset path.", nameof(path));
        string normalized = string.Join('/', parts.Where(part => part != ".")).ToLowerInvariant();
        if (normalized.Length == 0)
            throw new ArgumentException("Asset path has no filename.", nameof(path));
        return normalized;
    }
    /// <summary>The filename without its last extension; a leading dot belongs to the name.</summary>
    public static string Stem(string normalizedPath)
    {
        ReadOnlySpan<char> name = normalizedPath.AsSpan(normalizedPath.LastIndexOf('/') + 1);
        int dot = name.LastIndexOf('.');
        return (dot > 0 ? name[..dot] : name).ToString();
    }
}
