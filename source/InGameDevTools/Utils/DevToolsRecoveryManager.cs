using Newtonsoft.Json;

namespace InGameDevTools.Utils;

internal sealed class DevToolsRecoveryManager
{
    private const string LatestFileName = "latest.json";
    private readonly string _root;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, PendingRecoveryCapture> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DevToolsRecoverySnapshot> _snapshots = new(StringComparer.Ordinal);
    private IReadOnlyList<DevToolsRecoverySnapshot> _snapshotList = Array.Empty<DevToolsRecoverySnapshot>();
    private bool _snapshotCacheLoaded;
    private bool _snapshotListDirty = true;

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
        TrackText(editor, documentKey, documentLabel, targetPath, () => text, dirty, delay);
    }

    public void TrackText(
        string editor,
        string documentKey,
        string documentLabel,
        string targetPath,
        Func<string> captureText,
        bool dirty,
        TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(captureText);
        TrackCapture(
            editor,
            documentKey,
            documentLabel,
            targetPath,
            dirty,
            delay,
            () =>
            {
                string text = captureText();
                return new CapturedRecoveryPayload("text", text, null, System.Text.Encoding.UTF8.GetBytes(text));
            });
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
        TrackBinary(editor, documentKey, documentLabel, targetPath, () => bytes, dirty, delay);
    }

    public void TrackBinary(
        string editor,
        string documentKey,
        string documentLabel,
        string targetPath,
        Func<byte[]> captureBytes,
        bool dirty,
        TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(captureBytes);
        TrackCapture(
            editor,
            documentKey,
            documentLabel,
            targetPath,
            dirty,
            delay,
            () =>
            {
                byte[] bytes = captureBytes();
                return new CapturedRecoveryPayload("binary-base64", null, null, bytes);
            });
    }

    public void FlushPending()
    {
        DateTimeOffset now = _now();
        foreach (PendingRecoveryCapture pending in _pending.Values.ToArray())
        {
            TryCaptureAndWrite(pending, now);
        }

        _pending.Clear();
    }

    public IReadOnlyList<DevToolsRecoverySnapshot> ListSnapshots()
    {
        EnsureSnapshotCacheLoaded();
        if (_snapshotListDirty)
        {
            _snapshotList = _snapshots.Values
                .OrderByDescending(snapshot => snapshot.UpdatedUtc)
                .ToArray();
            _snapshotListDirty = false;
        }

        return _snapshotList;
    }

    public bool TryLoadSnapshot(string recoveryKey, out DevToolsRecoverySnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(recoveryKey)) return false;

        string path = Path.Combine(FolderForRecoveryKey(recoveryKey), LatestFileName);
        try
        {
            if (!File.Exists(path)) return false;
            snapshot = JsonConvert.DeserializeObject<DevToolsRecoverySnapshot>(File.ReadAllText(path));
            return snapshot != null &&
                string.Equals(snapshot.RecoveryKey, recoveryKey, StringComparison.Ordinal);
        }
        catch
        {
            snapshot = null;
            return false;
        }
    }

    public bool Discard(string recoveryKey)
    {
        if (string.IsNullOrWhiteSpace(recoveryKey)) return false;
        _pending.Remove(recoveryKey);
        EnsureSnapshotCacheLoaded();

        string folder = FolderForRecoveryKey(recoveryKey);
        bool folderExisted = Directory.Exists(folder);
        try
        {
            if (folderExisted)
            {
                Directory.Delete(folder, recursive: true);
            }

            bool removed = _snapshots.Remove(recoveryKey);
            if (removed) _snapshotListDirty = true;
            return folderExisted || removed;
        }
        catch
        {
            return false;
        }
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

    private void TrackCapture(
        string editor,
        string documentKey,
        string documentLabel,
        string targetPath,
        bool dirty,
        TimeSpan delay,
        Func<CapturedRecoveryPayload> capture)
    {
        string recoveryKey = BuildRecoveryKey(editor, documentKey);
        if (!dirty)
        {
            bool wasPending = _pending.Remove(recoveryKey);
            EnsureSnapshotCacheLoaded();
            if (wasPending || _snapshots.ContainsKey(recoveryKey))
            {
                Discard(recoveryKey);
            }
            return;
        }

        DateTimeOffset now = _now();
        TimeSpan safeDelay = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        if (!_pending.TryGetValue(recoveryKey, out PendingRecoveryCapture? pending))
        {
            pending = new PendingRecoveryCapture(
                recoveryKey,
                editor,
                documentLabel,
                targetPath,
                capture,
                now + safeDelay);
            _pending[recoveryKey] = pending;
        }
        else
        {
            pending.Editor = editor;
            pending.DocumentLabel = documentLabel;
            pending.TargetPath = targetPath;
            pending.Capture = capture;
        }

        if (now < pending.WriteAfterUtc) return;

        TryCaptureAndWrite(pending, now);
        pending.WriteAfterUtc = now + safeDelay;
    }

    private bool TryCaptureAndWrite(PendingRecoveryCapture pending, DateTimeOffset now)
    {
        try
        {
            CapturedRecoveryPayload payload = pending.Capture();
            string contentHash = DevToolsFileBackupManager.Sha256Hex(payload.Bytes);
            EnsureSnapshotCacheLoaded();
            if (_snapshots.TryGetValue(pending.RecoveryKey, out DevToolsRecoverySnapshot? persisted) &&
                string.Equals(persisted.ContentSha256, contentHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? binaryBase64 = payload.PayloadKind == "binary-base64"
                ? Convert.ToBase64String(payload.Bytes)
                : payload.BinaryBase64;
            DevToolsRecoverySnapshot snapshot = CreateSnapshot(
                pending.RecoveryKey,
                pending.Editor,
                pending.DocumentLabel,
                pending.TargetPath,
                payload.PayloadKind,
                payload.Text,
                binaryBase64,
                contentHash,
                now);
            WriteSnapshot(snapshot);
            return true;
        }
        catch
        {
            // Recovery must never throw out of an editor draw or shutdown path.
            return false;
        }
    }

    private static DevToolsRecoverySnapshot CreateSnapshot(
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

        EnsureSnapshotCacheLoaded();
        _snapshots[snapshot.RecoveryKey] = WithoutPayload(snapshot);
        _snapshotListDirty = true;
    }

    private void EnsureSnapshotCacheLoaded()
    {
        if (_snapshotCacheLoaded) return;
        _snapshotCacheLoaded = true;
        if (!Directory.Exists(_root)) return;

        foreach (string path in Directory.EnumerateFiles(_root, LatestFileName, SearchOption.AllDirectories))
        {
            try
            {
                DevToolsRecoverySnapshot? snapshot = JsonConvert.DeserializeObject<DevToolsRecoverySnapshot>(File.ReadAllText(path));
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.RecoveryKey)) continue;

                if (!_snapshots.TryGetValue(snapshot.RecoveryKey, out DevToolsRecoverySnapshot? existing) ||
                    snapshot.UpdatedUtc >= existing.UpdatedUtc)
                {
                    _snapshots[snapshot.RecoveryKey] = WithoutPayload(snapshot);
                }
            }
            catch
            {
                // Corrupt recovery files should not break the editor.
            }
        }

        _snapshotListDirty = true;
    }

    private static DevToolsRecoverySnapshot WithoutPayload(DevToolsRecoverySnapshot snapshot)
    {
        return new DevToolsRecoverySnapshot
        {
            RecoveryKey = snapshot.RecoveryKey,
            Editor = snapshot.Editor,
            DocumentLabel = snapshot.DocumentLabel,
            TargetPath = snapshot.TargetPath,
            PayloadKind = snapshot.PayloadKind,
            ContentSha256 = snapshot.ContentSha256,
            UpdatedUtc = snapshot.UpdatedUtc
        };
    }

    private string FolderForRecoveryKey(string recoveryKey)
    {
        string hash = DevToolsFileBackupManager.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(recoveryKey.ToLowerInvariant()));
        return Path.Combine(_root, hash[..2], hash);
    }

    private sealed class PendingRecoveryCapture(
        string recoveryKey,
        string editor,
        string documentLabel,
        string targetPath,
        Func<CapturedRecoveryPayload> capture,
        DateTimeOffset writeAfterUtc)
    {
        public string RecoveryKey { get; } = recoveryKey;
        public string Editor { get; set; } = editor;
        public string DocumentLabel { get; set; } = documentLabel;
        public string TargetPath { get; set; } = targetPath;
        public Func<CapturedRecoveryPayload> Capture { get; set; } = capture;
        public DateTimeOffset WriteAfterUtc { get; set; } = writeAfterUtc;
    }

    private sealed record CapturedRecoveryPayload(
        string PayloadKind,
        string? Text,
        string? BinaryBase64,
        byte[] Bytes);
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
