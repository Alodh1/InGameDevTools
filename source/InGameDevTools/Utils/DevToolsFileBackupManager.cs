using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace InGameDevTools.Utils;

internal sealed class DevToolsFileBackupManager
{
    private const string IndexFileName = "index.json";
    private static DevToolsFileBackupManager? Shared;
    private static bool SharedEnabled = true;
    private static int SharedRetentionPerFile = 10;

    private readonly string _root;
    private readonly Func<DateTimeOffset> _now;

    public DevToolsFileBackupManager(string root, Func<DateTimeOffset>? now = null)
    {
        _root = Path.GetFullPath(root);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public static void ConfigureShared(string root, bool enabled, int retentionPerFile)
    {
        Shared = new DevToolsFileBackupManager(root);
        SharedEnabled = enabled;
        SharedRetentionPerFile = Math.Clamp(retentionPerFile, 1, 200);
    }

    public static void BackupSharedBeforeOverwrite(string originalPath, byte[] replacementBytes)
    {
        if (!SharedEnabled || Shared == null) return;

        try
        {
            Shared.BackupBeforeOverwrite(originalPath, replacementBytes, SharedRetentionPerFile);
        }
        catch
        {
            // Shared backup writes must not prevent the user's explicit save.
        }
    }

    public DevToolsFileBackupResult BackupBeforeOverwrite(string originalPath, byte[] replacementBytes, int retentionPerFile)
    {
        if (!Path.IsPathRooted(originalPath))
        {
            throw new InvalidOperationException($"Backup path must be absolute: {originalPath}");
        }

        string fullPath = Path.GetFullPath(originalPath);
        if (IsInternalDevToolsBackupPath(fullPath))
        {
            return DevToolsFileBackupResult.Skipped("internal backup/recovery path");
        }

        if (fullPath.EndsWith(".ingamedevtools-manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return DevToolsFileBackupResult.Skipped("manifest file");
        }

        if (!File.Exists(fullPath))
        {
            return DevToolsFileBackupResult.Skipped("new file");
        }

        byte[] originalBytes = File.ReadAllBytes(fullPath);
        if (originalBytes.AsSpan().SequenceEqual(replacementBytes))
        {
            return DevToolsFileBackupResult.Skipped("unchanged");
        }

        string contentHash = Sha256Hex(originalBytes);
        string folder = BackupFolderForPath(fullPath);
        Directory.CreateDirectory(folder);

        if (Directory.EnumerateFiles(folder, "*.bak", SearchOption.TopDirectoryOnly)
            .Any(path => Path.GetFileName(path).Contains(contentHash[..16], StringComparison.OrdinalIgnoreCase)))
        {
            Prune(folder, retentionPerFile);
            return DevToolsFileBackupResult.Skipped("duplicate content");
        }

        string timestamp = _now().UtcDateTime.ToString("yyyyMMdd-HHmmssfff");
        string fileName = $"{SanitizeFileName(Path.GetFileName(fullPath))}.{timestamp}.{contentHash[..16]}.bak";
        string backupPath = Path.Combine(folder, fileName);
        File.WriteAllBytes(backupPath, originalBytes);
        WriteIndex(folder, fullPath);
        Prune(folder, retentionPerFile);
        return DevToolsFileBackupResult.Created(backupPath);
    }

    internal string BackupFolderForPath(string originalPath)
    {
        string fullPath = Path.GetFullPath(originalPath);
        string pathHash = Sha256Hex(System.Text.Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant()));
        return Path.Combine(_root, pathHash[..2], pathHash);
    }

    private void WriteIndex(string folder, string originalPath)
    {
        JObject index = new()
        {
            ["originalPath"] = originalPath,
            ["updatedUtc"] = _now().UtcDateTime.ToString("O")
        };
        File.WriteAllText(Path.Combine(folder, IndexFileName), index.ToString());
    }

    private static void Prune(string folder, int retentionPerFile)
    {
        if (retentionPerFile <= 0) return;

        FileInfo[] backups = new DirectoryInfo(folder)
            .EnumerateFiles("*.bak", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();

        foreach (FileInfo backup in backups.Skip(retentionPerFile))
        {
            try
            {
                backup.Delete();
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private bool IsInternalDevToolsBackupPath(string fullPath)
    {
        string normalized = Path.GetFullPath(fullPath);
        string root = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        return normalized.Split(Path.DirectorySeparatorChar)
            .Any(part => part.Equals("live-backups", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("recovery", StringComparison.OrdinalIgnoreCase));
    }

    internal static string Sha256Hex(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "file";
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "file" : sanitized;
    }
}

internal sealed record DevToolsFileBackupResult(bool BackupCreated, string? BackupPath, string Reason)
{
    public static DevToolsFileBackupResult Created(string path) => new(true, path, "created");
    public static DevToolsFileBackupResult Skipped(string reason) => new(false, null, reason);
}
