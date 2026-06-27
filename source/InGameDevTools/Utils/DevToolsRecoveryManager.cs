using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InGameDevTools.Utils;

internal sealed class DevToolsRecoveryManager
{
    private const string LatestFileName = "latest.json";
    private readonly string _root;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, PendingRecoverySnapshot> _pending = new(StringComparer.Ordinal);

    public DevToolsRecoveryManager(string root, Func<DateTimeOffset>? now = null)
    {
        _root = Path.GetFullPath(root);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public void TrackText(
        string editor,
        string documentKey,
        string documentLabel,
        string targetPath,
        string text,
        bool dirty,
        TimeSpan delay)
    {
        string recoveryKey = BuildRecoveryKey(editor, documentKey);
        if (!dirty)
        {
            _pending.Remove(recoveryKey);
            Discard(recoveryKey);
            return;
        }

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        TrackPayload(editor, documentKey, documentLabel, targetPath, "text", text, null, bytes, delay);
    }

    public void TrackBinary(
        string editor,
        string documentKey,
        string documentLabel,
        string targetPath,
        byte[] bytes,
        bool dirty,
        TimeSpan delay)
    {
        string recoveryKey = BuildRecoveryKey(editor, documentKey);
        if (!dirty)
        {
            _pending.Remove(recoveryKey);
            Discard(recoveryKey);
            return;
        }

        TrackPayload(editor, documentKey, documentLabel, targetPath, "binary-base64", null, Convert.ToBase64String(bytes), bytes, delay);
    }

    public void FlushPending()
    {
        foreach (PendingRecoverySnapshot pending in _pending.Values.ToList())
        {
            WriteSnapshot(pending.Snapshot);
            _pending.Remove(pending.Snapshot.RecoveryKey);
        }
    }

    public IReadOnlyList<DevToolsRecoverySnapshot> ListSnapshots()
    {
        if (!Directory.Exists(_root)) return [];

        List<DevToolsRecoverySnapshot> snapshots = [];
        foreach (string path in Directory.EnumerateFiles(_root, LatestFileName, SearchOption.AllDirectories))
        {
            try
            {
                DevToolsRecoverySnapshot? snapshot = JsonConvert.DeserializeObject<DevToolsRecoverySnapshot>(File.ReadAllText(path));
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.RecoveryKey)) continue;
                snapshots.Add(snapshot);
            }
            catch
            {
                // Corrupt recovery files should not break the editor.
            }
        }

        return snapshots
            .OrderByDescending(snapshot => snapshot.UpdatedUtc)
            .ToArray();
    }

    public bool Discard(string recoveryKey)
    {
        if (string.IsNullOrWhiteSpace(recoveryKey)) return false;
        _pending.Remove(recoveryKey);
        string folder = FolderForRecoveryKey(recoveryKey);
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public int DiscardWhere(Func<DevToolsRecoverySnapshot, bool> predicate)
    {
        int removed = 0;
        foreach (DevToolsRecoverySnapshot snapshot in ListSnapshots())
        {
            bool matches;
            try
            {
                matches = predicate(snapshot);
            }
            catch
            {
                matches = false;
            }

            if (!matches) continue;
            if (Discard(snapshot.RecoveryKey)) removed++;
        }

        return removed;
    }

    public static string BuildRecoveryKey(string editor, string documentKey)
    {
        return $"{editor.Trim()}::{documentKey.Trim()}";
    }

    private void TrackPayload(
        string editor,
        string documentKey,
        string documentLabel,
        string targetPath,
        string payloadKind,
        string? text,
        string? binaryBase64,
        byte[] payloadBytes,
        TimeSpan delay)
    {
        string recoveryKey = BuildRecoveryKey(editor, documentKey);
        string contentHash = DevToolsFileBackupManager.Sha256Hex(payloadBytes);
        DateTimeOffset now = _now();

        if (_pending.TryGetValue(recoveryKey, out PendingRecoverySnapshot? existing))
        {
            if (existing.Snapshot.ContentSha256 == contentHash && now < existing.WriteAfterUtc) return;

            existing.Snapshot = CreateSnapshot(recoveryKey, editor, documentLabel, targetPath, payloadKind, text, binaryBase64, contentHash, now);
            if (now >= existing.WriteAfterUtc)
            {
                WriteSnapshot(existing.Snapshot);
                existing.WriteAfterUtc = now + delay;
            }
            return;
        }

        DevToolsRecoverySnapshot current = CreateSnapshot(recoveryKey, editor, documentLabel, targetPath, payloadKind, text, binaryBase64, contentHash, now);
        PendingRecoverySnapshot pending = new(current, now + delay);
        _pending[recoveryKey] = pending;
        if (delay <= TimeSpan.Zero)
        {
            WriteSnapshot(current);
            _pending.Remove(recoveryKey);
        }
    }

    private DevToolsRecoverySnapshot CreateSnapshot(
        string recoveryKey,
        string editor,
        string documentLabel,
        string targetPath,
        string payloadKind,
        string? text,
        string? binaryBase64,
        string contentHash,
        DateTimeOffset now)
    {
        return new DevToolsRecoverySnapshot
        {
            RecoveryKey = recoveryKey,
            Editor = editor,
            DocumentLabel = documentLabel,
            TargetPath = targetPath,
            PayloadKind = payloadKind,
            Text = text,
            BinaryBase64 = binaryBase64,
            ContentSha256 = contentHash,
            UpdatedUtc = now.UtcDateTime
        };
    }

    private void WriteSnapshot(DevToolsRecoverySnapshot snapshot)
    {
        string folder = FolderForRecoveryKey(snapshot.RecoveryKey);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, LatestFileName);
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
        File.Move(tempPath, path, overwrite: true);
    }

    private string FolderForRecoveryKey(string recoveryKey)
    {
        string hash = DevToolsFileBackupManager.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(recoveryKey.ToLowerInvariant()));
        return Path.Combine(_root, hash[..2], hash);
    }

    private sealed class PendingRecoverySnapshot(DevToolsRecoverySnapshot snapshot, DateTimeOffset writeAfterUtc)
    {
        public DevToolsRecoverySnapshot Snapshot { get; set; } = snapshot;
        public DateTimeOffset WriteAfterUtc { get; set; } = writeAfterUtc;
    }
}

internal sealed class DevToolsRecoverySnapshot
{
    public string RecoveryKey { get; set; } = "";
    public string Editor { get; set; } = "";
    public string DocumentLabel { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string PayloadKind { get; set; } = "text";
    public string? Text { get; set; }
    public string? BinaryBase64 { get; set; }
    public string ContentSha256 { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}
