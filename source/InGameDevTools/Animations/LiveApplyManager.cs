#if DEBUG
using ImGuiNET;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private readonly DevToolsLiveApplyManager _liveApplyManager = new();

    public sealed class DevToolsLiveApplyManager
    {
        private readonly Dictionary<string, LivePatchEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _backupSessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        public bool AutoApply;
        public bool WriteBackups;
        public bool RevertOnClose = true;
        public bool KeepLiveChangesOnClose;
        public string LastStatus = "";

        public string Apply(
            string key,
            string label,
            Func<LivePatchSnapshot> captureOriginal,
            Action applyCurrent,
            string? appliedStatus = null)
        {
            return Apply(key, label, captureOriginal, applyCurrent, () => appliedStatus ?? $"Live applied {label}.");
        }

        public string Apply(
            string key,
            string label,
            Func<LivePatchSnapshot> captureOriginal,
            Action applyCurrent,
            Func<string> appliedStatus)
        {
            try
            {
                LivePatchEntry entry = EnsureEntry(key, label, captureOriginal);
                if (WriteBackups)
                {
                    TryWriteBackup(entry);
                }

                applyCurrent();
                entry.Applied = true;
                entry.LastError = "";
                entry.LastAppliedAt = DateTime.Now;
                LastStatus = appliedStatus();
                return LastStatus;
            }
            catch (Exception exception)
            {
                LivePatchEntry entry = EnsureErrorEntry(key, label);
                entry.LastError = exception.Message;
                LastStatus = $"Live apply failed for {label}: {exception.Message}";
                return LastStatus;
            }
        }

        public void TrackOriginal(string key, string label, Func<LivePatchSnapshot> captureOriginal)
        {
            try
            {
                EnsureEntry(key, label, captureOriginal);
            }
            catch (Exception exception)
            {
                LivePatchEntry entry = EnsureErrorEntry(key, label);
                entry.LastError = exception.Message;
            }
        }

        public string Revert(string key)
        {
            if (!_entries.TryGetValue(key, out LivePatchEntry? entry))
            {
                LastStatus = "No live changes to revert.";
                return LastStatus;
            }

            try
            {
                entry.Snapshot.Revert();
                entry.Applied = false;
                entry.LastError = "";
                LastStatus = $"Reverted live changes for {entry.Label}.";
                return LastStatus;
            }
            catch (Exception exception)
            {
                entry.LastError = exception.Message;
                LastStatus = $"Live revert failed for {entry.Label}: {exception.Message}";
                return LastStatus;
            }
        }

        public string RevertAll()
        {
            int reverted = 0;
            List<string> failures = [];
            foreach (LivePatchEntry entry in _entries.Values.ToList())
            {
                if (!entry.Applied) continue;
                try
                {
                    entry.Snapshot.Revert();
                    entry.Applied = false;
                    entry.LastError = "";
                    reverted++;
                }
                catch (Exception exception)
                {
                    entry.LastError = exception.Message;
                    failures.Add($"{entry.Label}: {exception.Message}");
                }
            }

            LastStatus = failures.Count == 0
                ? $"Reverted {reverted} live target(s)."
                : $"Reverted {reverted} live target(s), {failures.Count} failed: {string.Join("; ", failures)}";
            return LastStatus;
        }

        public string GetStatus(string key, bool available)
        {
            if (!available) return "Live target unavailable";
            if (!_entries.TryGetValue(key, out LivePatchEntry? entry)) return "Not applied";
            if (!string.IsNullOrWhiteSpace(entry.LastError)) return $"Live apply failed: {entry.LastError}";
            return entry.Applied ? "Live applied" : "Not applied";
        }

        public bool IsApplied(string key) => _entries.TryGetValue(key, out LivePatchEntry? entry) && entry.Applied;

        public bool CanRevert(string key) => _entries.TryGetValue(key, out LivePatchEntry? entry) && entry.Applied;

        public void DrawGlobalControls(string id)
        {
            ImGui.Checkbox($"Live apply##{id}", ref AutoApply);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("When enabled, dirty editor changes are pushed into loaded runtime objects for this session.");
            }

            ImGui.SameLine();
            ImGui.Checkbox($"Write live backup copies##{id}", ref WriteBackups);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Writes diagnostic copies of the original runtime state before the first live patch.");
            }

            ImGui.Checkbox($"Revert on close##{id}", ref RevertOnClose);
            ImGui.SameLine();
            ImGui.Checkbox($"Keep live changes on close##{id}", ref KeepLiveChangesOnClose);

            if (ImGui.Button($"Revert all live changes##{id}"))
            {
                RevertAll();
            }

            if (!string.IsNullOrWhiteSpace(LastStatus))
            {
                ImGui.TextWrapped(LastStatus);
            }
        }

        public void DrawTargetControls(
            string id,
            string key,
            string label,
            bool available,
            Func<string> apply,
            Func<string> revert)
        {
            ImGui.SeparatorText("Live");
            ImGui.TextDisabled(GetStatus(key, available));

            if (!available) ImGui.BeginDisabled();
            if (ImGui.Button($"Apply selected##{id}"))
            {
                LastStatus = apply();
            }

            ImGui.SameLine();
            bool canRevert = available && _entries.TryGetValue(key, out LivePatchEntry? entry) && entry.Applied;
            if (!canRevert) ImGui.BeginDisabled();
            if (ImGui.Button($"Revert selected##{id}"))
            {
                LastStatus = revert();
            }
            if (!canRevert) ImGui.EndDisabled();
            if (!available) ImGui.EndDisabled();

            if (!available)
            {
                ImGui.TextWrapped($"{label}: no live runtime target.");
            }
        }

        public void DrawRuntimeStatus(
            string id,
            string key,
            string label,
            bool available,
            Func<string>? revert = null)
        {
            ImGui.SeparatorText("Runtime");
            ImGui.TextDisabled(GetStatus(key, available));

            bool canRevert = available && revert != null && CanRevert(key);
            if (!canRevert) ImGui.BeginDisabled();
            if (ImGui.Button($"Revert selected##{id}"))
            {
                LastStatus = revert == null ? "No live changes to revert." : revert();
            }
            if (!canRevert) ImGui.EndDisabled();

            if (!available)
            {
                ImGui.TextWrapped($"{label}: no live runtime target.");
            }
            else if (!string.IsNullOrWhiteSpace(LastStatus))
            {
                ImGui.TextWrapped(LastStatus);
            }
        }

        private LivePatchEntry EnsureEntry(string key, string label, Func<LivePatchSnapshot> captureOriginal)
        {
            if (_entries.TryGetValue(key, out LivePatchEntry? existing))
            {
                existing.Label = label;
                return existing;
            }

            LivePatchSnapshot snapshot = captureOriginal();
            LivePatchEntry entry = new(key, label, snapshot);
            _entries[key] = entry;
            if (WriteBackups)
            {
                TryWriteBackup(entry);
            }

            return entry;
        }

        private LivePatchEntry EnsureErrorEntry(string key, string label)
        {
            if (_entries.TryGetValue(key, out LivePatchEntry? existing))
            {
                existing.Label = label;
                return existing;
            }

            LivePatchEntry entry = new(key, label, LivePatchSnapshot.Empty);
            _entries[key] = entry;
            return entry;
        }

        private void TryWriteBackup(LivePatchEntry entry)
        {
            if (entry.BackupWritten || entry.Snapshot.BackupJson == null || string.IsNullOrWhiteSpace(entry.Snapshot.BackupRelativePath))
            {
                return;
            }

            try
            {
                string sourceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ModsNeedUpdate");
                string backupRoot = Path.Combine(sourceRoot, "_ingamedevtools_exports", "live-backups", _backupSessionId);
                string outputPath = Path.Combine(backupRoot, entry.Snapshot.BackupRelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, entry.Snapshot.BackupJson());
                entry.BackupWritten = true;
            }
            catch (Exception exception)
            {
                entry.LastError = $"backup failed: {exception.Message}";
            }
        }

        private sealed class LivePatchEntry(string key, string label, LivePatchSnapshot snapshot)
        {
            public string Key { get; } = key;
            public string Label { get; set; } = label;
            public LivePatchSnapshot Snapshot { get; } = snapshot;
            public bool Applied { get; set; }
            public bool BackupWritten { get; set; }
            public string LastError { get; set; } = "";
            public DateTime LastAppliedAt { get; set; }
        }
    }

    public sealed record LivePatchSnapshot(Action Revert, string? BackupRelativePath, Func<string>? BackupJson)
    {
        public static LivePatchSnapshot Empty { get; } = new(() => { }, null, null);
    }
}
#endif
