using System.Text.Json;
using System.Text.Json.Serialization;
using CommonFury.IO;
using Xunit;

namespace CommonFury.Tests;

public sealed class FileTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("commonfury-io-").FullName;
    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void AtomicWritesReplaceRollBackAndCleanUp()
    {
        string first = Path.Combine(folder, "first.bin"), second = Path.Combine(folder, "second.bin");
        AtomicFile.Write(first, "old"u8);
        AtomicFile.Write(first, "new"u8);
        Assert.True(File.ReadAllBytes(first).AsSpan().SequenceEqual("new"u8), "Atomic single-file replacement.");
        Assert.Throws<IOException>(() => AtomicFile.Write(first, stream => { stream.Write("partial"u8); throw new IOException("writer failed"); }));
        Assert.Equal("new"u8.ToArray(), File.ReadAllBytes(first));
        AtomicFile.Write(first, stream => stream.Write("streamed"u8));
        Assert.Equal("streamed"u8.ToArray(), File.ReadAllBytes(first));
        AtomicFile.WriteBatch(new Dictionary<string, ReadOnlyMemory<byte>> { [first] = "batch"u8.ToArray(), [second] = "second"u8.ToArray() }, TestContext.Current.CancellationToken);
        Assert.True(File.ReadAllText(first) == "batch" && File.ReadAllText(second) == "second", "Batch replacement.");
        string obstruction = Path.Combine(folder, "directory");
        Directory.CreateDirectory(obstruction);
        Assert.ThrowsAny<IOException>(() => AtomicFile.WriteBatch(new Dictionary<string, ReadOnlyMemory<byte>> { [first] = "bad"u8.ToArray(), [obstruction] = new byte[1] }, TestContext.Current.CancellationToken));
        Assert.True(File.ReadAllText(first) == "batch", "Batch rollback did not restore original.");
        Assert.ThrowsAny<OperationCanceledException>(() => AtomicFile.WriteBatch(new Dictionary<string, ReadOnlyMemory<byte>> { [first] = new byte[1] }, new CancellationToken(true)));
        Assert.True(File.ReadAllText(first) == "batch", "Cancellation changed destination.");
        Assert.True(Directory.GetFiles(folder).Length == 2, "Staging files leaked after success/rollback.");
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void BatchRestoresAnOriginalStrandedByAPartialReplace(int failingIndex)
    {
        string first = Path.Combine(folder, "first.bin"), second = Path.Combine(folder, "second.bin");
        File.WriteAllText(first, "first original");
        File.WriteAllText(second, "second original");
        string failing = failingIndex == 0 ? first : second;
        // Like ERROR_UNABLE_TO_MOVE_REPLACEMENT_2: the original is renamed to the backup, then moving the replacement fails.
        AtomicFile.ReplaceOverride = (replacement, target, backup) =>
        {
            if (target != failing)
            {
                File.Replace(replacement, target, backup);
                return;
            }
            File.Move(target, backup);
            throw new IOException("Unable to move the replacement file.");
        };
        try
        {
            Assert.Throws<IOException>(() => AtomicFile.WriteBatch(new Dictionary<string, ReadOnlyMemory<byte>>
                { [first] = "first new"u8.ToArray(), [second] = "second new"u8.ToArray() }, TestContext.Current.CancellationToken));
        }
        finally { AtomicFile.ReplaceOverride = null; }
        Assert.Equal("first original", File.ReadAllText(first));
        Assert.Equal("second original", File.ReadAllText(second));
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public void NoOverwriteRejectsExistingTargetsWithoutStagingOrChangingThem()
    {
        string path = Path.Combine(folder, "nested", "resource.bin");
        AtomicFile.Write(path, "first"u8, overwrite: false);
        Assert.Equal("first"u8.ToArray(), File.ReadAllBytes(path));
        bool ran = false;
        Assert.Throws<IOException>(() => AtomicFile.Write(path, _ => ran = true, overwrite: false));
        Assert.False(ran, "The writer ran although the destination already existed.");
        Assert.Throws<IOException>(() => AtomicFile.Write(path, "second"u8, overwrite: false));
        Assert.Equal("first"u8.ToArray(), File.ReadAllBytes(path));
        AtomicFile.Write(path, "third"u8);
        Assert.Equal("third"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void NoOverwriteDetectsTargetsCreatedWhileStaging()
    {
        string path = Path.Combine(folder, "race.bin");
        Assert.Throws<IOException>(() => AtomicFile.Write(path, stream =>
        {
            stream.Write("ours"u8);
            File.WriteAllText(path, "theirs");
        }, overwrite: false));
        Assert.Equal("theirs", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void JsonUsesSuppliedSourceGeneratedMetadata()
    {
        string reportPath = Path.Combine(folder, "report.json");
        AtomicFile.WriteJson(reportPath, new ReportFixture(42, "\u00E1rbol"), TestJson.Default.ReportFixture);
        using JsonDocument report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        Assert.True(report.RootElement.GetProperty("Count").GetInt32() == 42 && report.RootElement.GetProperty("Name").GetString() == "\u00E1rbol", "AOT-safe report serialization.");
    }

    [Fact]
    public void LogicalPathsNormalizeAndRejectHostPaths()
    {
        Assert.Equal("stream/a/shared.ytyp", AssetPaths.Normalize("STREAM\\A\\Shared.YTYP"));
        foreach (string path in new[] { "", ".", "../types.ytyp", "dir/../../types.ytyp", "/types.ytyp", "C:\\types.ytyp", "dir/", "bad\0name.ytyp" })
            Assert.Throws<ArgumentException>(() => AssetPaths.Normalize(path));
        Assert.Equal("shared", AssetPaths.Stem("stream/a/shared.ytyp"));
        Assert.Equal(".hidden", AssetPaths.Stem("a/.hidden"));
        Assert.Equal("platform:/models/prop.ydr", VirtualPaths.Normalize("PLATFORM:\\models\\.\\PROP.ydr"));
        Assert.Equal("dlc_test:/", VirtualPaths.NormalizeRoot("DLC_TEST:/"));
        Assert.Equal("stream/prop.ydr", VirtualPaths.Normalize("stream/PROP.ydr"));
        Assert.Equal("platform:/models/prop.ydr", VirtualPaths.Combine("platform:/models", "PROP.ydr"));
        Assert.Throws<ArgumentException>(() => VirtualPaths.Normalize("C:/game/prop.ydr"));
        Assert.Throws<ArgumentException>(() => VirtualPaths.Normalize("platform:/../prop.ydr"));
        Assert.Throws<ArgumentException>(() => VirtualPaths.Normalize("platform:/"));
        Assert.True(AssetNames.ClipShortName("C:\\Clips\\INTRO.test.ycd") == "intro", "Clip short name rules.");
    }

    [Fact]
    public void FileTreeIsSortedSkipsHiddenAndConfinesDestinations()
    {
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "b.txt"), "");
        File.WriteAllText(Path.Combine(folder, "A.txt"), "");
        File.WriteAllText(Path.Combine(folder, ".hidden"), "");
        File.WriteAllText(Path.Combine(folder, "sub", "c.txt"), "");
        Assert.Equal(["A.txt", "b.txt", "sub"], FileTree.Children(folder).Select(item => item.Name));
        Assert.Equal(["A.txt", "b.txt"], FileTree.Files(folder, recursive: false).Select(Path.GetFileName));
        Assert.Equal(3, FileTree.Files(folder, recursive: true).Count());
        Assert.Equal(Path.Combine(folder, "sub", "c.txt"), FileTree.Destination(folder, "SUB/C.TXT"));
        Assert.Throws<ArgumentException>(() => FileTree.Destination(folder, "../escape.txt"));
    }

    [Fact]
    public void DestinationChecksOnlyBelowTheRootEvenWithATrailingSeparator()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Creates a directory junction with mklink.");
        string target = Directory.CreateDirectory(Path.Combine(folder, "real", "out")).Parent!.FullName;
        string link = Path.Combine(folder, "link");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
            { RedirectStandardOutput = true, UseShellExecute = false })!)
        {
            mklink.StandardOutput.ReadToEnd();
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        try
        {
            // The junction is an ancestor of the root, which the caller chose; only paths below the root are checked.
            string root = Path.Combine(link, "out");
            string expected = Path.Combine(root, "a.dds");
            Assert.Equal(expected, FileTree.Destination(root, "a.dds"));
            Assert.Equal(expected, FileTree.Destination(root + Path.DirectorySeparatorChar, "a.dds"));
            // A root that is itself a link is still rejected, with or without the separator.
            Assert.Throws<IOException>(() => FileTree.Destination(link + Path.DirectorySeparatorChar, "out/a.dds"));
        }
        finally { Directory.Delete(link); }
    }
}

internal sealed record ReportFixture(int Count, string Name);
[JsonSerializable(typeof(ReportFixture))]
internal partial class TestJson : JsonSerializerContext;
