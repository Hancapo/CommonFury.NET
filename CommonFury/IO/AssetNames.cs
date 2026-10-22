namespace CommonFury.IO;

/// <summary>Asset names derived from filenames, as animation references use them.</summary>
public static class AssetNames
{
    /// <summary>Lowercased last path component up to its first dot; accepts either separator.</summary>
    public static string ClipShortName(ReadOnlySpan<char> name)
    {
        int slash = name.LastIndexOfAny('/', '\\');
        name = name[(slash + 1)..];
        int dot = name.IndexOf('.');
        if (dot >= 0)
            name = name[..dot];
        return name.ToString().ToLowerInvariant();
    }
}
