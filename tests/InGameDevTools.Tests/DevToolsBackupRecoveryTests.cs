using InGameDevTools.Utils;

namespace InGameDevTools.Tests;

public sealed class DevToolsBackupRecoveryTests
{
    [Fact]
    public void Backup_SkipsNewAndUnchangedFiles()
    {
        using TempDir temp = new();
        DevToolsFileBackupManager manager = new(temp.PathFor("backups"));
        string file = temp.PathFor("asset.json");

        Assert.False(manager.BackupBeforeOverwrite(file, "new"u8.ToArray(), 10).BackupCreated);

        File.WriteAllText(file, "same");
        Assert.False(manager.BackupBeforeOverwrite(file, "same"u8.ToArray(), 10).BackupCreated);
    }

    [Fact]
    public void Backup_CreatesOnlyOneBackupForSameOriginalContent()
    {
        using TempDir temp = new();
        DevToolsFileBackupManager manager = new(temp.PathFor("backups"));
        string file = temp.PathFor("asset.json");
        File.WriteAllText(file, "original");

        DevToolsFileBackupResult first = manager.BackupBeforeOverwrite(file, "changed"u8.ToArray(), 10);
        DevToolsFileBackupResult second = manager.BackupBeforeOverwrite(file, "changed-again"u8.ToArray(), 10);

        Assert.True(first.BackupCreated);
        Assert.False(second.BackupCreated);
        string folder = manager.BackupFolderForPath(file);
        Assert.Single(Directory.GetFiles(folder, "*.bak"));
        Assert.Equal("original", File.ReadAllText(Directory.GetFiles(folder, "*.bak")[0]));
    }

    [Fact]
    public void Backup_PrunesOldestBackupsPastRetention()
    {
        using TempDir temp = new();
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DevToolsFileBackupManager manager = new(temp.PathFor("backups"), () => now);
        string file = temp.PathFor("asset.json");

        for (int index = 0; index < 12; index++)
        {
            File.WriteAllText(file, $"original-{index}");
            now = now.AddSeconds(1);
            manager.BackupBeforeOverwrite(file, System.Text.Encoding.UTF8.GetBytes($"changed-{index}"), 10);
        }

        Assert.Equal(10, Directory.GetFiles(manager.BackupFolderForPath(file), "*.bak").Length);
    }

    [Fact]
    public void Backup_RejectsRelativeTraversalPaths()
    {
        using TempDir temp = new();
        DevToolsFileBackupManager manager = new(temp.PathFor("backups"));

        Assert.Throws<InvalidOperationException>(() => manager.BackupBeforeOverwrite("..\\asset.json", "x"u8.ToArray(), 10));
    }

    [Fact]
    public void Backup_RoundTripsBinaryPayload()
    {
        using TempDir temp = new();
        DevToolsFileBackupManager manager = new(temp.PathFor("backups"));
        string file = temp.PathFor("texture.png");
        byte[] original = [0, 1, 2, 3, 255];
        File.WriteAllBytes(file, original);

        DevToolsFileBackupResult result = manager.BackupBeforeOverwrite(file, [9, 8, 7], 10);

        Assert.True(result.BackupCreated);
        Assert.Equal(original, File.ReadAllBytes(result.BackupPath!));
    }

    [Fact]
    public void Recovery_WritesRollingLatestAndUpdatesIt()
    {
        using TempDir temp = new();
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DevToolsRecoveryManager manager = new(temp.PathFor("recovery"), () => now);

        manager.TrackText("Models", "game:foo", "foo", "target", "first", dirty: true, TimeSpan.Zero);
        manager.TrackText("Models", "game:foo", "foo", "target", "second", dirty: true, TimeSpan.Zero);

        DevToolsRecoverySnapshot snapshot = Assert.Single(manager.ListSnapshots());
        Assert.Equal("second", snapshot.Text);
    }

    [Fact]
    public void Recovery_ClearsCleanDocumentsAndSkipsCorruptFiles()
    {
        using TempDir temp = new();
        DevToolsRecoveryManager manager = new(temp.PathFor("recovery"));

        manager.TrackText("Models", "game:foo", "foo", "target", "dirty", dirty: true, TimeSpan.Zero);
        Assert.Single(manager.ListSnapshots());
        manager.TrackText("Models", "game:foo", "foo", "target", "dirty", dirty: false, TimeSpan.Zero);
        Assert.Empty(manager.ListSnapshots());

        string corruptFolder = Path.Combine(temp.PathFor("recovery"), "aa", "bad");
        Directory.CreateDirectory(corruptFolder);
        File.WriteAllText(Path.Combine(corruptFolder, "latest.json"), "{not json");
        Assert.Empty(manager.ListSnapshots());
    }

    [Fact]
    public void Recovery_DiscardWhereRemovesOnlyMatchingSnapshots()
    {
        using TempDir temp = new();
        DevToolsRecoveryManager manager = new(temp.PathFor("recovery"));

        manager.TrackText("Models", "game:shapes/weapons/moon-cres.json", "moon", "target", "dirty", dirty: true, TimeSpan.Zero);
        manager.TrackText("Worldgen", "game:worldgen/foo.json", "worldgen", "target", "dirty", dirty: true, TimeSpan.Zero);

        int removed = manager.DiscardWhere(snapshot => snapshot.Editor == "Models");

        Assert.Equal(1, removed);
        DevToolsRecoverySnapshot remaining = Assert.Single(manager.ListSnapshots());
        Assert.Equal("Worldgen", remaining.Editor);
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "ingamedevtools-tests", Guid.NewGuid().ToString("N"));

        public TempDir()
        {
            Directory.CreateDirectory(_path);
        }

        public string PathFor(string relativePath) => Path.Combine(_path, relativePath);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_path, recursive: true);
            }
            catch
            {
                // Test cleanup best effort.
            }
        }
    }
}
