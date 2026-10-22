using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CommonFury.IO;

/// <summary>Writes files by staging beside the destination and renaming, so a failure leaves the previous file intact.</summary>
public static class AtomicFile
{
    /// <summary>Stages, flushes, then renames; creates missing parent directories. Directory durability is not guaranteed.
    /// Without <paramref name="overwrite"/>, an existing destination, even one created while staging, raises <see cref="IOException"/> and is left untouched.</summary>
    public static void Write(string destination, ReadOnlySpan<byte> data, bool overwrite = true) => WriteCore(destination, data, null, overwrite);

    /// <inheritdoc cref="Write(string, ReadOnlySpan{byte}, bool)"/>
    /// <remarks>The callback borrows the stream and must leave it open; if it throws, the destination is unchanged.</remarks>
    public static void Write(string destination, Action<Stream> writer, bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteCore(destination, default, writer, overwrite);
    }

    private static void WriteCore(string destination, ReadOnlySpan<byte> data, Action<Stream>? writer, bool overwrite)
    {
        string target = Path.GetFullPath(destination);
        // Fail fast without staging; the non-overwriting move below remains the authoritative check.
        if (!overwrite && Path.Exists(target))
            throw new IOException($"Destination already exists: {target}");
        string parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (writer is null)
                    stream.Write(data);
                else
                    writer(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite);
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>Serializes with source-generated metadata, then writes atomically.</summary>
    public static void WriteJson<T>(string destination, T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        Write(destination, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));
    }

    /// <summary>Stages every file, then replaces them all, rolling back on filesystem errors (not process termination).
    /// Cancellation is observed only before the first replacement.</summary>
    public static void WriteBatch(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var staged = new List<StagedFile>();
        var targets = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        bool preserveRecovery = false;
        try
        {
            foreach (var (path, data) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.GetFullPath(path);
                if (!targets.Add(target))
                    throw new ArgumentException("Duplicate normalized destination.", nameof(files));
                string parent = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(parent);
                string stem = Path.Combine(parent, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}");
                var item = new StagedFile(target, stem + ".new", stem + ".old");
                staged.Add(item);
                Write(item.NewPath, data.Span);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (StagedFile item in staged)
                {
                    item.HadOriginal = File.Exists(item.Target);
                    if (item.HadOriginal)
                        (ReplaceOverride ?? File.Replace)(item.NewPath, item.Target, item.BackupPath);
                    else
                        File.Move(item.NewPath, item.Target);
                    item.Committed = true;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                try
                {
                    for (int i = staged.Count - 1; i >= 0; i--)
                    {
                        StagedFile item = staged[i];
                        if (!item.Committed)
                        {
                            // File.Replace can fail after renaming the original to the backup but before moving the
                            // replacement in (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2): the backup is then the only copy.
                            if (item.HadOriginal && !File.Exists(item.Target) && File.Exists(item.BackupPath))
                                File.Move(item.BackupPath, item.Target);
                            continue;
                        }
                        if (item.HadOriginal)
                            File.Move(item.BackupPath, item.Target, overwrite: true);
                        else
                            File.Delete(item.Target);
                    }
                }
                catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException)
                {
                    preserveRecovery = true;
                    throw new AggregateException("Batch replacement and rollback failed; staging/backup files were retained beside their targets.", error, recoveryError);
                }
                throw;
            }
        }
        finally
        {
            if (!preserveRecovery)
                foreach (StagedFile item in staged)
                {
                    File.Delete(item.NewPath);
                    // Never delete a backup that is the only remaining copy of its original.
                    if (File.Exists(item.Target) || !File.Exists(item.BackupPath))
                        File.Delete(item.BackupPath);
                }
        }
    }

    // Test seam standing in for File.Replace on the calling thread, to reproduce partial OS failures.
    [ThreadStatic] internal static Action<string, string, string>? ReplaceOverride;

    private sealed class StagedFile(string target, string newPath, string backupPath)
    {
        public string Target { get; } = target;
        public string NewPath { get; } = newPath;
        public string BackupPath { get; } = backupPath;
        public bool HadOriginal { get; set; }
        public bool Committed { get; set; }
    }
}
