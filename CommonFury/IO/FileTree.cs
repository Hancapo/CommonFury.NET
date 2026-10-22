namespace CommonFury.IO;

/// <summary>Deterministic filesystem traversal that never follows reparse points.</summary>
public static class FileTree
{
    /// <summary>Entries sorted case-insensitively, excluding reparse points and, unless requested, dot-prefixed names.</summary>
    public static IEnumerable<FileSystemInfo> Children(string directory, bool includeHidden = false)
        => new DirectoryInfo(directory).EnumerateFileSystemInfos().Where(item =>
            (includeHidden || !item.Name.StartsWith('.')) && (item.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
    /// <summary>Full file paths in <see cref="Children"/> order; nesting is limited to 128 levels.</summary>
    public static IEnumerable<string> Files(string directory, bool recursive)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((Path.GetFullPath(directory), 0));
        while (pending.TryPop(out var folder))
        {
            if (folder.Depth > 128)
                throw new IOException("Asset folder nesting exceeds 128 levels.");
            foreach (var item in Children(folder.Path))
                if (item is DirectoryInfo sub)
                { if (recursive) pending.Push((sub.FullName, folder.Depth + 1)); }
                else
                    yield return item.FullName;
        }
    }

    /// <summary>Resolves a logical path under <paramref name="root"/>, rejecting escapes and paths through filesystem links below the root.</summary>
    public static string Destination(string root, string relative)
    {
        // GetDirectoryName never returns a trailing separator, so the walk below compares against a trimmed root.
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.GetFullPath(Path.Combine(fullRoot, AssetPaths.Normalize(relative)));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, comparison))
            throw new IOException("Asset destination escapes its output directory.");
        for (string? path = full; path is not null; path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Asset output cannot traverse a filesystem link: {path}");
            if (string.Equals(path, fullRoot, comparison))
                break;
        }
        return full;
    }
}
