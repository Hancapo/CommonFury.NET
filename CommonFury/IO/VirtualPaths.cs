namespace CommonFury.IO;

/// <summary>Logical paths: relative asset paths or <c>device:/relative</c>, never host drive paths.</summary>
public static class VirtualPaths
{
    /// <summary>Normalizes a relative path, keeping a normalized device prefix.</summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string value = path.Trim().Replace('\\', '/');
        int colon = value.IndexOf(':');
        if (colon < 0)
            return AssetPaths.Normalize(value);
        string device = Device(value, colon);
        return device + ":/" + AssetPaths.Normalize(value[(colon + 2)..]);
    }

    /// <summary>Normalizes a mount prefix; an empty root and a bare <c>device:/</c> are allowed.</summary>
    public static string NormalizeRoot(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string value = path.Trim().Replace('\\', '/');
        if (value.Length == 0)
            return "";
        int colon = value.IndexOf(':');
        if (colon >= 0 && value.Length == colon + 2)
            return Device(value, colon) + ":/";
        return Normalize(value.TrimEnd('/'));
    }

    public static string Combine(string root, string relativePath)
    {
        string prefix = NormalizeRoot(root);
        string relative = AssetPaths.Normalize(relativePath);
        return prefix.Length == 0 ? relative : prefix.TrimEnd('/') + "/" + relative;
    }

    private static string Device(string value, int colon)
    {
        if (colon < 2 || colon + 1 >= value.Length || value[colon + 1] != '/' ||
            value.AsSpan(0, colon).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_"))
            throw new ArgumentException("Expected a logical device:/ path; host drive paths are not supported.", nameof(value));
        return value[..colon].ToLowerInvariant();
    }
}
