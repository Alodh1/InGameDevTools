using InGameDevTools.Utils;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private const int AiBehaviorIndexBatchSize = 90;

    private readonly List<AiBehaviorEntry> _aiBehaviorEntries = [];
    private readonly List<AiBehaviorEntry> _visibleAiBehaviorEntries = [];
    private readonly List<IAsset> _aiBehaviorIndexAssets = [];
    private readonly Dictionary<string, AiBehaviorDraftState> _aiBehaviorDraftStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImGuiThreePanelLayoutState _aiBehaviorLayout = new(0.26f, 0.34f);
    private readonly DevToolsEditorDiagnostics _aiBehaviorDiagnostics = new("Entity AI");

    private AiBehaviorIndexState _aiBehaviorIndexState;
    private int _aiBehaviorIndexAssetIndex;
    private int _aiBehaviorEntryIndex;
    private int _aiBehaviorTaskIndex;
    private string _aiBehaviorFilter = "";
    private string _aiBehaviorDomainFilter = "";
    private bool _aiBehaviorDirtyOnly;
    private string _aiBehaviorLoadedKey = "";
    private string _aiBehaviorOriginalText = "";
    private string _aiBehaviorCurrentText = "";
    private bool _aiBehaviorTextValid;
    private string _aiBehaviorValidationStatus = "No entity AI asset loaded.";
    private string _aiBehaviorStatus = "Entity AI editor ready.";
    private string _aiBehaviorNewTaskCode = "wander";
    private int _aiBehaviorKnownTaskCodeIndex;
    private string[]? _aiBehaviorKnownTaskCodes;
    private readonly List<AiBehaviorLiveTaskInfo> _aiBehaviorLiveTasks = [];
    private readonly List<AiBehaviorLiveTransition> _aiBehaviorLiveTransitions = [];
    private readonly Dictionary<string, string> _aiBehaviorLiveActiveBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AiBehaviorLiveFieldSnapshot> _aiBehaviorLiveFieldSnapshots = [];
    private long _aiBehaviorLiveEntityId;
    private string _aiBehaviorLiveEntityCode = "";
    private string _aiBehaviorLiveStatus = "No live entity target selected.";
    private string _aiBehaviorLiveServerStatus = "Singleplayer server API has not been checked.";
    private bool _aiBehaviorLiveAutoRefresh = true;
    private float _aiBehaviorLiveRefreshAccumulator;
    private int _aiBehaviorLiveTaskEditIndex;

    private const float AiBehaviorLiveRefreshIntervalSeconds = 0.75f;

    private void AiBehaviorEditorTab(float deltaSeconds, bool showDiagnostics)
    {
        try
        {
            UpdateAiBehaviorLiveAutoRefresh(deltaSeconds);
            EnsureAiBehaviorEntriesIndexed();

            NVector2 available = ImGui.GetContentRegionAvail();
            float scale = Math.Max(0.75f, _devToolsUiScale);
            float splitterThickness = Math.Max(5f, 6f * scale);
            ImGuiLayoutHelper.CalculateThreePanelWidths(
                available.X,
                splitterThickness,
                _aiBehaviorLayout,
                260f * scale,
                520f * scale,
                500f * scale,
                340f * scale,
                760f * scale,
                out float panelAvailableWidth,
                out float leftWidth,
                out float centerWidth,
                out float rightWidth);

            DrawAiBehaviorBrowser(new NVector2(leftWidth, available.Y));
            ImGui.SameLine(0, 0);
            ImGuiLayoutHelper.DrawVerticalSplitter("##ai-behavior-left-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _aiBehaviorLayout.LeftFraction, 260f * scale, Math.Max(260f * scale, panelAvailableWidth - rightWidth - 500f * scale));
            ImGui.SameLine(0, 0);
            DrawAiBehaviorEditorPanel(new NVector2(centerWidth, available.Y));
            ImGui.SameLine(0, 0);
            ImGuiLayoutHelper.DrawVerticalSplitter("##ai-behavior-right-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _aiBehaviorLayout.RightFraction, 340f * scale, Math.Max(340f * scale, panelAvailableWidth - leftWidth - 500f * scale), invertDrag: true);
            ImGui.SameLine(0, 0);
            DrawAiBehaviorInspector(new NVector2(rightWidth, available.Y), showDiagnostics);
        }
        catch (Exception exception)
        {
            _aiBehaviorIndexState = AiBehaviorIndexState.Failed;
            _aiBehaviorStatus = $"Entity AI editor error: {exception.Message}";
            _aiBehaviorDiagnostics.Exception("Entity AI editor failed", exception);
            ImGui.TextColored(new NVector4(1f, 0.36f, 0.28f, 1f), _aiBehaviorStatus);
            _aiBehaviorDiagnostics.Draw("entity-ai-editor-error", showDiagnostics);
        }
    }

    private void ResetAiBehaviorLayout()
    {
        _aiBehaviorLayout.Reset();
    }

    private void ClearAiBehaviorLiveApplyState()
    {
        RestoreAiBehaviorLiveFieldSnapshots(updateStatus: false);
        _aiBehaviorLiveTasks.Clear();
        _aiBehaviorLiveTransitions.Clear();
        _aiBehaviorLiveActiveBySlot.Clear();
        _aiBehaviorLiveFieldSnapshots.Clear();
        _aiBehaviorLiveEntityId = 0;
        _aiBehaviorLiveEntityCode = "";
        _aiBehaviorLiveStatus = "No live entity target selected.";
        _aiBehaviorLiveServerStatus = "Singleplayer server API has not been checked.";
        _aiBehaviorLiveRefreshAccumulator = 0f;
        _aiBehaviorLiveTaskEditIndex = 0;
    }

    private void UpdateAiBehaviorLiveAutoRefresh(float deltaSeconds)
    {
        if (!_aiBehaviorLiveAutoRefresh || _aiBehaviorLiveEntityId == 0)
        {
            _aiBehaviorLiveRefreshAccumulator = 0f;
            return;
        }

        _aiBehaviorLiveRefreshAccumulator += Math.Clamp(deltaSeconds, 0f, 0.25f);
        if (_aiBehaviorLiveRefreshAccumulator < AiBehaviorLiveRefreshIntervalSeconds) return;

        _aiBehaviorLiveRefreshAccumulator = 0f;
        RefreshAiBehaviorLiveSnapshot(recordTransitions: true);
    }

    private void EnsureAiBehaviorEntriesIndexed()
    {
        if (_aiBehaviorIndexState == AiBehaviorIndexState.Ready || _aiBehaviorIndexState == AiBehaviorIndexState.Failed) return;
        if (_aiBehaviorIndexState == AiBehaviorIndexState.Idle)
        {
            StartAiBehaviorIndexing(clearLoaded: false);
        }

        ProcessAiBehaviorIndexBatch();
    }

    private void StartAiBehaviorIndexing(bool clearLoaded)
    {
        _aiBehaviorIndexState = AiBehaviorIndexState.Indexing;
        _aiBehaviorIndexAssetIndex = 0;
        _aiBehaviorEntries.Clear();
        _visibleAiBehaviorEntries.Clear();
        _aiBehaviorIndexAssets.Clear();
        _aiBehaviorDiagnostics.Clear();
        _aiBehaviorKnownTaskCodes = null;

        if (clearLoaded)
        {
            _aiBehaviorDraftStates.Clear();
            _aiBehaviorLoadedKey = "";
            _aiBehaviorOriginalText = "";
            _aiBehaviorCurrentText = "";
            _aiBehaviorTaskIndex = 0;
        }

        foreach (IAsset asset in _api.Assets.AllAssets.Values)
        {
            if (IsAiBehaviorEntityAsset(asset))
            {
                _aiBehaviorIndexAssets.Add(asset);
            }
        }

        _aiBehaviorStatus = BuildAiBehaviorIndexProgressText();
    }

    private void ProcessAiBehaviorIndexBatch()
    {
        if (_aiBehaviorIndexState != AiBehaviorIndexState.Indexing) return;

        try
        {
            int processed = 0;
            while (processed < AiBehaviorIndexBatchSize && _aiBehaviorIndexAssetIndex < _aiBehaviorIndexAssets.Count)
            {
                IndexAiBehaviorAsset(_aiBehaviorIndexAssets[_aiBehaviorIndexAssetIndex++]);
                processed++;
            }

            if (_aiBehaviorIndexAssetIndex >= _aiBehaviorIndexAssets.Count)
            {
                CompleteAiBehaviorIndexing();
            }
            else
            {
                _aiBehaviorStatus = BuildAiBehaviorIndexProgressText();
                RebuildVisibleAiBehaviorEntries();
            }
        }
        catch (Exception exception)
        {
            _aiBehaviorIndexState = AiBehaviorIndexState.Failed;
            _aiBehaviorStatus = $"Entity AI indexing failed: {exception.Message}";
            _aiBehaviorDiagnostics.Exception("Entity AI indexing failed", exception);
        }
    }

    private void CompleteAiBehaviorIndexing()
    {
        _aiBehaviorEntries.Sort((left, right) => string.Compare(left.SortKey, right.SortKey, StringComparison.OrdinalIgnoreCase));
        RebuildVisibleAiBehaviorEntries();
        _aiBehaviorIndexState = AiBehaviorIndexState.Ready;
        _aiBehaviorStatus = $"Indexed {_aiBehaviorEntries.Count} entity AI source asset(s).";
        if (_visibleAiBehaviorEntries.Count > 0 && string.IsNullOrWhiteSpace(_aiBehaviorLoadedKey))
        {
            LoadAiBehaviorEntry(_visibleAiBehaviorEntries[Math.Clamp(_aiBehaviorEntryIndex, 0, _visibleAiBehaviorEntries.Count - 1)], keepDirty: true);
        }
    }

    private string BuildAiBehaviorIndexProgressText()
    {
        return $"Indexing entity AI sources {_aiBehaviorIndexAssetIndex}/{_aiBehaviorIndexAssets.Count}.";
    }

    private void IndexAiBehaviorAsset(IAsset asset)
    {
        string sourceText = ReadAssetText(asset);
        if (!TryParseJsonObjectDetailed(sourceText, out JObject? json, out string error) || json == null)
        {
            if (!string.IsNullOrWhiteSpace(sourceText))
            {
                _aiBehaviorDiagnostics.Warning($"Skipped malformed entity source {asset.Location}: {error}", sourceText);
            }
            return;
        }

        if (!TryFindAiTaskBehavior(json, out JObject? behavior, out JArray? tasks, out JObject? tasksByType, out _) || behavior == null)
        {
            return;
        }

        string? sourceCode = json["code"]?.ToString();
        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            sourceCode = Path.GetFileNameWithoutExtension(asset.Location.Path);
        }

        HashSet<string> runtimeCodes = ExpandAiBehaviorEntityCodes(asset.Location.Domain ?? "game", json, sourceCode)
            .Select(code => NormalizeAiBehaviorEntityCode(asset.Location.Domain ?? "game", code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        runtimeCodes.Add(NormalizeAiBehaviorEntityCode(asset.Location.Domain ?? "game", sourceCode));

        _aiBehaviorEntries.Add(new AiBehaviorEntry(
            asset,
            sourceText,
            json,
            StripAiBehaviorCodeDomain(sourceCode),
            runtimeCodes,
            tasks?.Count ?? 0,
            CountAiBehaviorTasksByType(tasksByType),
            behavior["aitasks"] is JArray,
            behavior["aitasksByType"] is JObject));
    }

    private static bool IsAiBehaviorEntityAsset(IAsset? asset)
    {
        if (asset?.Location == null) return false;
        string path = asset.Location.Path.Replace('\\', '/');
        return path.StartsWith("entities/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildVisibleAiBehaviorEntries()
    {
        AiBehaviorEntry? selected = SelectedAiBehaviorEntry;
        string filter = _aiBehaviorFilter.Trim();
        string loadedKey = _aiBehaviorLoadedKey;
        bool loadedDirty = IsAiBehaviorTextDirty(_aiBehaviorCurrentText, _aiBehaviorOriginalText);

        _visibleAiBehaviorEntries.Clear();
        foreach (AiBehaviorEntry entry in _aiBehaviorEntries)
        {
            if (!string.IsNullOrWhiteSpace(filter) && !entry.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ImGuiLayoutHelper.MatchesDomain(_aiBehaviorDomainFilter, entry.Domain)) continue;
            if (_aiBehaviorDirtyOnly && !IsAiBehaviorEntryDirty(entry, loadedKey, loadedDirty)) continue;
            _visibleAiBehaviorEntries.Add(entry);
        }

        if (selected != null)
        {
            int index = _visibleAiBehaviorEntries.FindIndex(entry => entry.Key.Equals(selected.Key, StringComparison.OrdinalIgnoreCase));
            _aiBehaviorEntryIndex = Math.Max(0, index);
        }
        else
        {
            _aiBehaviorEntryIndex = Math.Clamp(_aiBehaviorEntryIndex, 0, Math.Max(0, _visibleAiBehaviorEntries.Count - 1));
        }
    }

    private AiBehaviorEntry? SelectedAiBehaviorEntry =>
        _visibleAiBehaviorEntries.Count == 0
            ? null
            : _visibleAiBehaviorEntries[Math.Clamp(_aiBehaviorEntryIndex, 0, _visibleAiBehaviorEntries.Count - 1)];

    private bool IsAiBehaviorEntryDirty(AiBehaviorEntry entry, string loadedKey, bool loadedDirty)
    {
        if (entry.Key.Equals(loadedKey, StringComparison.OrdinalIgnoreCase)) return loadedDirty;
        return _aiBehaviorDraftStates.TryGetValue(entry.Key, out AiBehaviorDraftState? draft) && draft.IsDirty;
    }

    private void DrawAiBehaviorBrowser(NVector2 size)
    {
        ImGui.BeginChild("##entity-ai-browser", size, true);
        ImGui.SeparatorText("Entity AI sources");

        bool filterChanged = false;
        filterChanged |= ImGui.InputText("Filter##entity-ai-filter", ref _aiBehaviorFilter, 256);
        filterChanged |= ImGuiLayoutHelper.DrawDomainCombo("Domain##entity-ai-domain", ref _aiBehaviorDomainFilter, _aiBehaviorEntries.Select(entry => entry.Domain));
        filterChanged |= ImGui.Checkbox("Dirty only##entity-ai-dirty-only", ref _aiBehaviorDirtyOnly);
        if (filterChanged)
        {
            RebuildVisibleAiBehaviorEntries();
        }

        if (ImGui.Button("Use looked-at entity##entity-ai-looked-at", new NVector2(-1, 0)))
        {
            SelectLookedAtAiBehaviorEntity();
        }

        if (ImGui.Button("Reload index##entity-ai-reload"))
        {
            StartAiBehaviorIndexing(clearLoaded: true);
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{_visibleAiBehaviorEntries.Count} / {_aiBehaviorEntries.Count}");

        if (_aiBehaviorIndexState == AiBehaviorIndexState.Indexing)
        {
            ImGui.TextWrapped(_aiBehaviorStatus);
        }

        if (_visibleAiBehaviorEntries.Count == 0)
        {
            ImGui.TextWrapped(_aiBehaviorIndexState == AiBehaviorIndexState.Ready ? "No entity AI sources match the current filters." : _aiBehaviorStatus);
            ImGui.EndChild();
            return;
        }

        _aiBehaviorEntryIndex = Math.Clamp(_aiBehaviorEntryIndex, 0, _visibleAiBehaviorEntries.Count - 1);
        if (ImGui.BeginChild("##entity-ai-source-list", new NVector2(-float.Epsilon, -float.Epsilon), true))
        {
            for (int index = 0; index < _visibleAiBehaviorEntries.Count; index++)
            {
                AiBehaviorEntry entry = _visibleAiBehaviorEntries[index];
                bool dirty = IsAiBehaviorEntryDirty(entry, _aiBehaviorLoadedKey, IsAiBehaviorTextDirty(_aiBehaviorCurrentText, _aiBehaviorOriginalText));
                string marker = dirty ? "*" : "";
                string label = $"{entry.DisplayCode}{marker}##entity-ai-entry-{index}";
                if (ImGui.Selectable(label, index == _aiBehaviorEntryIndex))
                {
                    _aiBehaviorEntryIndex = index;
                    LoadAiBehaviorEntry(entry, keepDirty: true);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{entry.Domain}:{entry.AssetPath}\n{entry.TaskCount} base task(s), {entry.TasksByTypeCount} typed task row(s)");
                }
            }
        }
        ImGui.EndChild();
        ImGui.EndChild();
    }

    private void DrawAiBehaviorEditorPanel(NVector2 size)
    {
        ImGui.BeginChild("##entity-ai-editor", size, true);

        if (_aiBehaviorIndexState == AiBehaviorIndexState.Indexing)
        {
            ImGui.TextWrapped(_aiBehaviorStatus);
            ImGui.EndChild();
            return;
        }

        if (_aiBehaviorIndexState == AiBehaviorIndexState.Failed)
        {
            ImGui.TextColored(new NVector4(1f, 0.38f, 0.32f, 1f), "Entity AI indexing failed.");
            ImGui.TextWrapped(_aiBehaviorStatus);
            ImGui.EndChild();
            return;
        }

        AiBehaviorEntry? entry = SelectedAiBehaviorEntry;
        if (entry == null)
        {
            ImGui.TextWrapped("No entity AI source selected.");
            ImGui.EndChild();
            return;
        }

        EnsureAiBehaviorEntryLoaded(entry);
        ImGui.TextUnformatted($"Entity AI: {entry.DisplayCode}");
        ImGui.SameLine();
        if (IsAiBehaviorTextDirty(_aiBehaviorCurrentText, _aiBehaviorOriginalText))
        {
            ImGui.TextColored(new NVector4(1f, 0.72f, 0.30f, 1f), "modified");
        }
        ImGui.Separator();

        if (!TryParseJsonObjectDetailed(_aiBehaviorCurrentText, out JObject? root, out string parseError) || root == null)
        {
            ImGui.TextColored(new NVector4(1f, 0.35f, 0.25f, 1f), $"Invalid JSON: {parseError}");
            DrawAiBehaviorRawJsonEditor();
            ImGui.EndChild();
            return;
        }

        if (!TryFindAiTaskBehavior(root, out JObject? behavior, out JArray? tasks, out JObject? tasksByType, out string behaviorPath) || behavior == null)
        {
            ImGui.TextWrapped("No taskai behavior found in this draft. Use the full JSON editor to add one.");
            DrawAiBehaviorRawJsonEditor();
            ImGui.EndChild();
            return;
        }

        DrawAiBehaviorTaskArrayEditor(root, behavior, tasks, tasksByType, behaviorPath);
        ImGui.EndChild();
    }

    private void DrawAiBehaviorTaskArrayEditor(JObject root, JObject behavior, JArray? tasks, JObject? tasksByType, string behaviorPath)
    {
        if (tasks == null)
        {
            ImGui.TextWrapped("This taskai behavior does not have a base aitasks array.");
            if (tasksByType != null)
            {
                ImGui.TextWrapped($"It has {CountAiBehaviorTasksByType(tasksByType)} typed task row(s) in aitasksByType. Typed rows are preserved; V1 edits the base aitasks array.");
            }

            if (ImGui.Button("Create base aitasks array##entity-ai-create-aitasks", new NVector2(-1, 0)))
            {
                behavior["aitasks"] = new JArray();
                SetAiBehaviorCurrentRoot(root);
            }

            DrawAiBehaviorRawJsonEditor();
            return;
        }

        ImGui.TextDisabled($"{behaviorPath}.aitasks: {tasks.Count} task(s)");

        DrawAiBehaviorTaskToolbar(root, tasks);

        float listHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y * 0.28f, 140f, 320f);
        if (ImGui.BeginChild("##entity-ai-task-list", new NVector2(-float.Epsilon, listHeight), true))
        {
            _aiBehaviorTaskIndex = Math.Clamp(_aiBehaviorTaskIndex, 0, Math.Max(0, tasks.Count - 1));
            for (int index = 0; index < tasks.Count; index++)
            {
                string label = GetAiBehaviorTaskLabel(tasks[index], index);
                if (ImGui.Selectable($"{label}##entity-ai-task-{index}", index == _aiBehaviorTaskIndex))
                {
                    _aiBehaviorTaskIndex = index;
                    RememberAiBehaviorDraft();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(tasks[index].ToString(Formatting.Indented));
                }
            }
        }
        ImGui.EndChild();

        if (tasks.Count == 0)
        {
            ImGui.TextWrapped("No AI tasks configured.");
            DrawAiBehaviorRawJsonEditor();
            return;
        }

        _aiBehaviorTaskIndex = Math.Clamp(_aiBehaviorTaskIndex, 0, tasks.Count - 1);
        if (tasks[_aiBehaviorTaskIndex] is not JObject task)
        {
            ImGui.TextWrapped("Selected task is not an object. Use raw JSON to repair it.");
            DrawAiBehaviorRawJsonEditor();
            return;
        }

        ImGui.Separator();
        bool changed = DrawAiBehaviorTaskEditor(task);
        if (changed)
        {
            SetAiBehaviorCurrentRoot(root);
        }

        if (ImGui.CollapsingHeader("Selected task JSON##entity-ai-task-json"))
        {
            string taskText = task.ToString(Formatting.Indented);
            if (ImGui.InputTextMultiline("##entity-ai-selected-task-json", ref taskText, 256 * 1024, new NVector2(-float.Epsilon, 220f), ImGuiInputTextFlags.AllowTabInput))
            {
                try
                {
                    JToken replacement = JToken.Parse(taskText);
                    tasks[_aiBehaviorTaskIndex] = replacement;
                    SetAiBehaviorCurrentRoot(root);
                }
                catch (Exception exception)
                {
                    _aiBehaviorTextValid = false;
                    _aiBehaviorValidationStatus = $"Selected task JSON parse error: {exception.Message}";
                }
            }
        }

        if (ImGui.CollapsingHeader("Full entity JSON##entity-ai-full-json"))
        {
            DrawAiBehaviorRawJsonEditor();
        }
    }

    private void DrawAiBehaviorTaskToolbar(JObject root, JArray tasks)
    {
        IReadOnlyList<string> knownCodes = GetKnownAiTaskCodes();
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X * 0.36f));
        ImGui.InputText("New task code##entity-ai-new-task-code", ref _aiBehaviorNewTaskCode, 128);
        if (knownCodes.Count > 0)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X * 0.36f));
            _aiBehaviorKnownTaskCodeIndex = Math.Clamp(_aiBehaviorKnownTaskCodeIndex, 0, knownCodes.Count - 1);
            if (ImGui.Combo("Known##entity-ai-known-task-code", ref _aiBehaviorKnownTaskCodeIndex, knownCodes.ToArray(), knownCodes.Count))
            {
                _aiBehaviorNewTaskCode = knownCodes[_aiBehaviorKnownTaskCodeIndex];
            }
        }

        if (ImGui.Button("Add task##entity-ai-add-task"))
        {
            string code = string.IsNullOrWhiteSpace(_aiBehaviorNewTaskCode) ? "wander" : _aiBehaviorNewTaskCode.Trim();
            tasks.Add(new JObject { ["code"] = code });
            _aiBehaviorTaskIndex = tasks.Count - 1;
            SetAiBehaviorCurrentRoot(root);
        }
        ImGui.SameLine();

        bool hasTask = tasks.Count > 0 && _aiBehaviorTaskIndex >= 0 && _aiBehaviorTaskIndex < tasks.Count;
        if (!hasTask) ImGui.BeginDisabled();
        if (ImGui.Button("Remove selected##entity-ai-remove-task"))
        {
            tasks.RemoveAt(_aiBehaviorTaskIndex);
            _aiBehaviorTaskIndex = Math.Clamp(_aiBehaviorTaskIndex, 0, Math.Max(0, tasks.Count - 1));
            SetAiBehaviorCurrentRoot(root);
        }
        ImGui.SameLine();
        if (ImGui.Button("Duplicate##entity-ai-duplicate-task"))
        {
            tasks.Insert(_aiBehaviorTaskIndex + 1, tasks[_aiBehaviorTaskIndex].DeepClone());
            _aiBehaviorTaskIndex++;
            SetAiBehaviorCurrentRoot(root);
        }
        ImGui.SameLine();
        if (_aiBehaviorTaskIndex <= 0) ImGui.BeginDisabled();
        if (ImGui.Button("Up##entity-ai-task-up"))
        {
            JToken task = tasks[_aiBehaviorTaskIndex];
            tasks.RemoveAt(_aiBehaviorTaskIndex);
            _aiBehaviorTaskIndex--;
            tasks.Insert(_aiBehaviorTaskIndex, task);
            SetAiBehaviorCurrentRoot(root);
        }
        if (_aiBehaviorTaskIndex <= 0) ImGui.EndDisabled();
        ImGui.SameLine();
        if (_aiBehaviorTaskIndex >= tasks.Count - 1) ImGui.BeginDisabled();
        if (ImGui.Button("Down##entity-ai-task-down"))
        {
            JToken task = tasks[_aiBehaviorTaskIndex];
            tasks.RemoveAt(_aiBehaviorTaskIndex);
            _aiBehaviorTaskIndex++;
            tasks.Insert(_aiBehaviorTaskIndex, task);
            SetAiBehaviorCurrentRoot(root);
        }
        if (_aiBehaviorTaskIndex >= tasks.Count - 1) ImGui.EndDisabled();
        if (!hasTask) ImGui.EndDisabled();
    }

    private bool DrawAiBehaviorTaskEditor(JObject task)
    {
        bool changed = false;
        ImGui.SeparatorText(GetAiBehaviorTaskCode(task) ?? "AI task");
        changed |= DrawAiBehaviorStringProperty(task, "code", "Code", required: true);
        changed |= DrawAiBehaviorStringProperty(task, "id", "Id", required: false);
        changed |= DrawAiBehaviorIntProperty(task, "priority", "Priority", 0, 10000);
        changed |= DrawAiBehaviorIntProperty(task, "slot", "Slot", 0, 64);
        changed |= DrawAiBehaviorFloatProperty(task, "executionChance", "Execution chance", 0f, 1f, "%.3f");
        changed |= DrawAiBehaviorIntProperty(task, "mincooldown", "Min cooldown ms", 0, 3_600_000);
        changed |= DrawAiBehaviorIntProperty(task, "maxcooldown", "Max cooldown ms", 0, 3_600_000);
        changed |= DrawAiBehaviorFloatProperty(task, "moveSpeed", "Move speed", 0f, 10f, "%.3f");
        changed |= DrawAiBehaviorFloatProperty(task, "seekingRange", "Seeking range", 0f, 256f, "%.2f");
        changed |= DrawAiBehaviorFloatProperty(task, "attackRange", "Attack range", 0f, 64f, "%.2f");
        changed |= DrawAiBehaviorFloatProperty(task, "damage", "Damage", 0f, 500f, "%.2f");

        if (ImGui.CollapsingHeader("Other parameters##entity-ai-other-params"))
        {
            foreach (JProperty property in task.Properties().ToList())
            {
                if (AiBehaviorFirstClassProperties.Contains(property.Name)) continue;
                ImGui.BulletText($"{property.Name}: {SummarizeAiBehaviorToken(property.Value, 110)}");
            }
        }

        return changed;
    }

    private bool DrawAiBehaviorStringProperty(JObject task, string propertyName, string label, bool required)
    {
        bool exists = task[propertyName] != null;
        string value = task[propertyName]?.ToString() ?? "";
        if (!exists && !required)
        {
            if (ImGui.Button($"Add {label}##entity-ai-add-{propertyName}"))
            {
                task[propertyName] = "";
                return true;
            }
            return false;
        }

        if (ImGui.InputText($"{label}##entity-ai-{propertyName}", ref value, 256))
        {
            if (required || !string.IsNullOrWhiteSpace(value))
            {
                task[propertyName] = value;
            }
            else
            {
                task.Remove(propertyName);
            }
            return true;
        }

        if (!required)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Remove##entity-ai-remove-{propertyName}"))
            {
                task.Remove(propertyName);
                return true;
            }
        }

        return false;
    }

    private bool DrawAiBehaviorIntProperty(JObject task, string propertyName, string label, int min, int max)
    {
        bool exists = task[propertyName] != null;
        if (!exists)
        {
            if (ImGui.Button($"Add {label}##entity-ai-add-{propertyName}"))
            {
                task[propertyName] = min;
                return true;
            }
            return false;
        }

        int value = TryReadJsonDouble(task[propertyName], out double parsed) ? (int)Math.Round(parsed) : min;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 90f));
        if (ImGui.DragInt($"{label}##entity-ai-{propertyName}", ref value, 1f, min, max))
        {
            task[propertyName] = Math.Clamp(value, min, max);
            return true;
        }

        ImGui.SameLine();
        if (ImGui.Button($"Remove##entity-ai-remove-{propertyName}"))
        {
            task.Remove(propertyName);
            return true;
        }

        return false;
    }

    private bool DrawAiBehaviorFloatProperty(JObject task, string propertyName, string label, float min, float max, string format)
    {
        bool exists = task[propertyName] != null;
        if (!exists)
        {
            if (ImGui.Button($"Add {label}##entity-ai-add-{propertyName}"))
            {
                task[propertyName] = min;
                return true;
            }
            return false;
        }

        float value = TryReadJsonFloat(task[propertyName], out float parsed) ? parsed : min;
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 90f));
        if (ImGui.DragFloat($"{label}##entity-ai-{propertyName}", ref value, 0.01f, min, max, format))
        {
            task[propertyName] = Math.Clamp(value, min, max);
            return true;
        }

        ImGui.SameLine();
        if (ImGui.Button($"Remove##entity-ai-remove-{propertyName}"))
        {
            task.Remove(propertyName);
            return true;
        }

        return false;
    }

    private void DrawAiBehaviorInspector(NVector2 size, bool showDiagnostics)
    {
        ImGui.BeginChild("##entity-ai-inspector", size, true);
        AiBehaviorEntry? entry = SelectedAiBehaviorEntry;
        if (entry == null)
        {
            ImGui.TextWrapped(_aiBehaviorStatus);
            _aiBehaviorDiagnostics.Draw("entity-ai-inspector-diagnostics", showDiagnostics);
            ImGui.EndChild();
            return;
        }

        EnsureAiBehaviorEntryLoaded(entry);
        bool dirty = IsAiBehaviorTextDirty(_aiBehaviorCurrentText, _aiBehaviorOriginalText);
        ImGui.TextWrapped($"Source: {entry.Domain}:{entry.AssetPath}");
        ImGui.TextWrapped($"Entity code: {entry.DisplayCode}");
        ImGui.TextWrapped($"Runtime variants indexed: {entry.RuntimeCodes.Count}");
        ImGui.TextWrapped($"Base tasks: {entry.TaskCount}; typed rows: {entry.TasksByTypeCount}");
        ImGui.TextWrapped(dirty ? "Draft: modified" : "Draft: clean");
        ImGui.TextWrapped(_aiBehaviorValidationStatus);

        ImGui.Separator();
        ImGui.TextWrapped("Source edits change the entity type for future spawns after authored files are loaded.");
        ImGui.Separator();
        DrawAiBehaviorLivePanel();
        ImGui.Separator();

        bool canSave = dirty && _aiBehaviorTextValid;
        if (!canSave) ImGui.BeginDisabled();
        if (ImGui.Button("Save authored file##entity-ai-save", new NVector2(-1, 0)))
        {
            QueueSourceSave(TrySaveAiBehaviorToSource(entry), status => _aiBehaviorStatus = status);
        }
        if (!canSave) ImGui.EndDisabled();

        if (ImGui.Button("Revert draft##entity-ai-revert", new NVector2(-1, 0)))
        {
            _aiBehaviorCurrentText = _aiBehaviorOriginalText;
            _aiBehaviorDraftStates.Remove(entry.Key);
            _aiBehaviorTaskIndex = 0;
            ValidateAiBehaviorCurrentText();
            _aiBehaviorStatus = $"Reverted draft for {entry.DisplayCode}.";
        }

        if (ImGui.Button("Copy JSON##entity-ai-copy-json", new NVector2(-1, 0)))
        {
            ImGui.SetClipboardText(_aiBehaviorCurrentText);
            _aiBehaviorStatus = "Copied entity AI JSON to clipboard.";
        }

        ImGui.Separator();
        _aiBehaviorDiagnostics.Draw("entity-ai-inspector-diagnostics", showDiagnostics);
        ImGui.EndChild();
    }

    private void DrawAiBehaviorLivePanel()
    {
        ImGui.SeparatorText("Live single entity");
        ImGui.TextWrapped("Read-only SP view. It shows the running task manager for one looked-at entity; source edits still need authored files.");

        ImGui.Checkbox("Auto refresh##entity-ai-live-auto-refresh", ref _aiBehaviorLiveAutoRefresh);

        if (ImGui.Button("Use looked-at live target##entity-ai-live-looked-at", new NVector2(-1, 0)))
        {
            Entity? entity = TryGetLookedAtEntityForAiBehavior();
            if (entity == null)
            {
                _aiBehaviorLiveStatus = "No looked-at entity target found.";
                _aiBehaviorLiveTasks.Clear();
                _aiBehaviorLiveActiveBySlot.Clear();
                _aiBehaviorLiveTransitions.Clear();
            }
            else
            {
                SetAiBehaviorLiveTarget(entity, refresh: true);
            }
        }

        bool hasLiveTarget = _aiBehaviorLiveEntityId != 0;
        if (!hasLiveTarget) ImGui.BeginDisabled();
        if (ImGui.Button("Refresh live tasks##entity-ai-live-refresh", new NVector2(-1, 0)))
        {
            RefreshAiBehaviorLiveSnapshot(recordTransitions: true);
        }
        if (!hasLiveTarget) ImGui.EndDisabled();

        if (_aiBehaviorLiveTransitions.Count > 0)
        {
            if (ImGui.Button("Clear activity log##entity-ai-live-clear-log", new NVector2(-1, 0)))
            {
                _aiBehaviorLiveTransitions.Clear();
            }
        }

        bool hasLiveEdits = _aiBehaviorLiveFieldSnapshots.Any(snapshot => snapshot.EntityId == _aiBehaviorLiveEntityId);
        if (!hasLiveEdits) ImGui.BeginDisabled();
        if (ImGui.Button("Revert live AI edits##entity-ai-live-revert-fields", new NVector2(-1, 0)))
        {
            RestoreAiBehaviorLiveFieldSnapshots(updateStatus: true);
            RefreshAiBehaviorLiveSnapshot(recordTransitions: false);
        }
        if (!hasLiveEdits) ImGui.EndDisabled();

        if (hasLiveTarget)
        {
            ImGui.TextWrapped($"Live target: {_aiBehaviorLiveEntityCode} #{_aiBehaviorLiveEntityId}");
        }
        ImGui.TextWrapped(_aiBehaviorLiveServerStatus);
        ImGui.TextWrapped(_aiBehaviorLiveStatus);

        if (_aiBehaviorLiveActiveBySlot.Count > 0)
        {
            ImGui.SeparatorText("Active slots");
            foreach (KeyValuePair<string, string> active in _aiBehaviorLiveActiveBySlot.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                ImGui.TextWrapped($"{active.Key}: {active.Value}");
            }
        }

        if (_aiBehaviorLiveTransitions.Count > 0)
        {
            ImGui.SeparatorText("Activity log");
            if (ImGui.BeginChild("##entity-ai-live-transition-list", new NVector2(-float.Epsilon, 116f), true))
            {
                for (int index = _aiBehaviorLiveTransitions.Count - 1; index >= 0; index--)
                {
                    AiBehaviorLiveTransition transition = _aiBehaviorLiveTransitions[index];
                    ImGui.TextWrapped($"{transition.Time} {transition.Text}");
                }
            }
            ImGui.EndChild();
        }

        if (_aiBehaviorLiveTasks.Count == 0) return;

        ImGui.SeparatorText("Tasks");
        if (ImGui.BeginChild("##entity-ai-live-task-list", new NVector2(-float.Epsilon, Math.Clamp(_aiBehaviorLiveTasks.Count * 24f + 18f, 96f, 260f)), true))
        {
            _aiBehaviorLiveTaskEditIndex = Math.Clamp(_aiBehaviorLiveTaskEditIndex, 0, Math.Max(0, _aiBehaviorLiveTasks.Count - 1));
            for (int index = 0; index < _aiBehaviorLiveTasks.Count; index++)
            {
                AiBehaviorLiveTaskInfo task = _aiBehaviorLiveTasks[index];
                NVector4 color = task.IsActive
                    ? new NVector4(0.55f, 1f, 0.50f, 1f)
                    : new NVector4(0.72f, 0.72f, 0.72f, 1f);
                string state = task.IsActive ? "active" : "idle";
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                if (ImGui.Selectable($"{index}: {task.Code} [{state}]##entity-ai-live-task-{index}", _aiBehaviorLiveTaskEditIndex == index))
                {
                    _aiBehaviorLiveTaskEditIndex = index;
                }
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{task.TypeName}\npriority: {task.Priority}\nslot: {task.Slot}\ncooldown: {task.Cooldown}\ngates: {task.GateSummary}");
                }
            }
        }
        ImGui.EndChild();

        ImGui.SeparatorText("Why not / gates");
        if (ImGui.BeginChild("##entity-ai-live-gate-list", new NVector2(-float.Epsilon, Math.Clamp(_aiBehaviorLiveTasks.Count * 30f + 20f, 120f, 260f)), true))
        {
            for (int index = 0; index < _aiBehaviorLiveTasks.Count; index++)
            {
                AiBehaviorLiveTaskInfo task = _aiBehaviorLiveTasks[index];
                string slotBlock = BuildAiBehaviorSlotBlockText(task);
                string summary = string.IsNullOrWhiteSpace(slotBlock)
                    ? task.GateSummary
                    : $"{slotBlock}; {task.GateSummary}";
                NVector4 color = task.IsActive
                    ? new NVector4(0.55f, 1f, 0.50f, 1f)
                    : new NVector4(0.88f, 0.74f, 0.52f, 1f);
                ImGui.TextColored(color, $"{task.Code}: {summary}");
                if (ImGui.IsItemHovered())
                {
                    string details = string.IsNullOrWhiteSpace(task.GateDetails)
                        ? "No readable gate fields were found on this task."
                        : task.GateDetails;
                    if (!string.IsNullOrWhiteSpace(slotBlock))
                    {
                        details = $"{slotBlock}\n{details}";
                    }
                    ImGui.SetTooltip(details);
                }
            }
        }
        ImGui.EndChild();

        DrawAiBehaviorLiveEditPanel();
    }

    private void DrawAiBehaviorLiveEditPanel()
    {
        if (_aiBehaviorLiveTasks.Count == 0) return;

        _aiBehaviorLiveTaskEditIndex = Math.Clamp(_aiBehaviorLiveTaskEditIndex, 0, _aiBehaviorLiveTasks.Count - 1);
        AiBehaviorLiveTaskInfo task = _aiBehaviorLiveTasks[_aiBehaviorLiveTaskEditIndex];
        ImGui.SeparatorText("Live edit selected task");
        ImGui.TextWrapped($"{task.Code} on {_aiBehaviorLiveEntityCode} #{_aiBehaviorLiveEntityId}");
        ImGui.TextWrapped("Live edits affect this one running entity instance. Use authored source save for future spawns.");

        bool changed = false;
        int drawn = 0;

        ImGui.SeparatorText("Common");
        drawn += DrawAiBehaviorLiveIntField(task.TaskObject, "Priority", 0, 10000, "Priority", "priority") ? 1 : 0;
        drawn += DrawAiBehaviorLiveIntField(task.TaskObject, "Slot", 0, 64, "Slot", "slot") ? 1 : 0;
        drawn += DrawAiBehaviorLiveFloatField(task.TaskObject, "Execution chance", 0f, 1f, "%.3f", "ExecutionChance", "executionChance", "chance") ? 1 : 0;
        drawn += DrawAiBehaviorLiveIntField(task.TaskObject, "Min cooldown ms", 0, 3_600_000, "MinCooldownMs", "mincooldown") ? 1 : 0;
        drawn += DrawAiBehaviorLiveIntField(task.TaskObject, "Max cooldown ms", 0, 3_600_000, "MaxCooldownMs", "maxcooldown") ? 1 : 0;
        drawn += DrawAiBehaviorLiveBoolField(task.TaskObject, "When swimming", "WhenSwimming", "whenSwimming") ? 1 : 0;
        drawn += DrawAiBehaviorLiveRangeField(task.TaskObject, "Day time frames", 0f, 1f, "%.3f", "duringDayTimeFrames", "DuringDayTimeFrames", "DayTimeFrames", "dayTimeFrames") ? 1 : 0;
        drawn += DrawAiBehaviorLiveRangeField(task.TaskObject, "Light levels", 0f, 32f, "%.1f", "EntityLightLevels", "entityLightLevels", "LightLevelRange", "lightLevelRange") ? 1 : 0;
        drawn += DrawAiBehaviorLiveRangeField(task.TaskObject, "Temperature range", -50f, 100f, "%.1f", "TemperatureRange", "temperatureRange") ? 1 : 0;

        ImGui.SeparatorText("Per-task numeric fields");
        int taskSpecificDrawn = 0;
        foreach (AiBehaviorLiveNumericSpec spec in AiBehaviorLiveNumericSpecs)
        {
            if (DrawAiBehaviorLiveFloatField(task.TaskObject, spec.Label, spec.Min, spec.Max, spec.Format, spec.MemberNames))
            {
                taskSpecificDrawn++;
            }
        }

        changed = _aiBehaviorLiveFieldSnapshots.Any(snapshot => snapshot.EntityId == _aiBehaviorLiveEntityId);
        if (drawn == 0 && taskSpecificDrawn == 0)
        {
            ImGui.TextWrapped("No writable common live fields were found on this task.");
        }

        if (changed)
        {
            ImGui.TextWrapped($"{_aiBehaviorLiveFieldSnapshots.Count(snapshot => snapshot.EntityId == _aiBehaviorLiveEntityId)} live field edit snapshot(s) are available for revert.");
        }
    }

    private bool DrawAiBehaviorLiveIntField(object task, string label, int min, int max, params string[] memberNames)
    {
        if (!TryFindAiBehaviorLiveMember(task, memberNames, out AiBehaviorLiveMember member) || !member.CanWrite) return false;
        if (!TryConvertAiBehaviorNumber(member.Value, out double number)) return false;

        int value = Math.Clamp((int)Math.Round(number), min, max);
        int edited = value;
        if (ImGui.InputInt($"{label}##entity-ai-live-{member.Name}", ref edited))
        {
            edited = Math.Clamp(edited, min, max);
            TrySetAiBehaviorLiveMember(task, member, edited);
            RefreshAiBehaviorLiveSnapshot(recordTransitions: false);
        }

        return true;
    }

    private bool DrawAiBehaviorLiveFloatField(object task, string label, float min, float max, string format, params string[] memberNames)
    {
        if (!TryFindAiBehaviorLiveMember(task, memberNames, out AiBehaviorLiveMember member) || !member.CanWrite) return false;
        if (!TryConvertAiBehaviorNumber(member.Value, out double number)) return false;

        float value = Math.Clamp((float)number, min, max);
        if (ImGui.SliderFloat($"{label}##entity-ai-live-{member.Name}", ref value, min, max, format))
        {
            TrySetAiBehaviorLiveMember(task, member, value);
            RefreshAiBehaviorLiveSnapshot(recordTransitions: false);
        }

        return true;
    }

    private bool DrawAiBehaviorLiveBoolField(object task, string label, params string[] memberNames)
    {
        if (!TryFindAiBehaviorLiveMember(task, memberNames, out AiBehaviorLiveMember member) || !member.CanWrite) return false;
        if (member.Value is not bool value) return false;

        if (ImGui.Checkbox($"{label}##entity-ai-live-{member.Name}", ref value))
        {
            TrySetAiBehaviorLiveMember(task, member, value);
            RefreshAiBehaviorLiveSnapshot(recordTransitions: false);
        }

        return true;
    }

    private bool DrawAiBehaviorLiveRangeField(object task, string label, float min, float max, string format, params string[] memberNames)
    {
        if (!TryFindAiBehaviorLiveMember(task, memberNames, out AiBehaviorLiveMember member) || !member.CanWrite) return false;
        if (!TryExtractAiBehaviorRange(member.Value, out NVector2 range)) return false;

        range.X = Math.Clamp(range.X, min, max);
        range.Y = Math.Clamp(range.Y, min, max);
        if (ImGui.SliderFloat2($"{label}##entity-ai-live-{member.Name}", ref range, min, max, format))
        {
            if (TryCreateAiBehaviorRangeValue(member.ValueType, range, out object? newValue))
            {
                TrySetAiBehaviorLiveMember(task, member, newValue);
                RefreshAiBehaviorLiveSnapshot(recordTransitions: false);
            }
        }

        return true;
    }

    private void DrawAiBehaviorRawJsonEditor()
    {
        int textCapacity = Math.Max(_aiBehaviorCurrentText.Length + 8192, 2 * 1024 * 1024);
        if (ImGui.InputTextMultiline("##entity-ai-json-text", ref _aiBehaviorCurrentText, (uint)textCapacity, new NVector2(-float.Epsilon, Math.Max(180f, ImGui.GetContentRegionAvail().Y - 24f)), ImGuiInputTextFlags.AllowTabInput))
        {
            ValidateAiBehaviorCurrentText();
            RememberAiBehaviorDraft();
        }
    }

    private void LoadAiBehaviorEntry(AiBehaviorEntry entry, bool keepDirty)
    {
        RememberAiBehaviorDraft();
        _aiBehaviorLoadedKey = entry.Key;
        _aiBehaviorOriginalText = entry.SourceJson.ToString(Formatting.Indented);

        if (keepDirty && _aiBehaviorDraftStates.TryGetValue(entry.Key, out AiBehaviorDraftState? draft))
        {
            _aiBehaviorCurrentText = draft.Text;
            _aiBehaviorTaskIndex = draft.TaskIndex;
        }
        else
        {
            _aiBehaviorCurrentText = _aiBehaviorOriginalText;
            _aiBehaviorTaskIndex = 0;
        }

        ValidateAiBehaviorCurrentText();
    }

    private void EnsureAiBehaviorEntryLoaded(AiBehaviorEntry entry)
    {
        if (!_aiBehaviorLoadedKey.Equals(entry.Key, StringComparison.OrdinalIgnoreCase))
        {
            LoadAiBehaviorEntry(entry, keepDirty: true);
        }
    }

    private void RememberAiBehaviorDraft()
    {
        if (string.IsNullOrWhiteSpace(_aiBehaviorLoadedKey)) return;

        bool dirty = IsAiBehaviorTextDirty(_aiBehaviorCurrentText, _aiBehaviorOriginalText);
        if (dirty)
        {
            _aiBehaviorDraftStates[_aiBehaviorLoadedKey] = new AiBehaviorDraftState(
                _aiBehaviorCurrentText,
                _aiBehaviorTaskIndex,
                _aiBehaviorTextValid,
                _aiBehaviorValidationStatus,
                _aiBehaviorOriginalText);
        }
        else
        {
            _aiBehaviorDraftStates.Remove(_aiBehaviorLoadedKey);
        }
    }

    private void SetAiBehaviorCurrentRoot(JToken root)
    {
        _aiBehaviorCurrentText = root.ToString(Formatting.Indented);
        ValidateAiBehaviorCurrentText();
        RememberAiBehaviorDraft();
        RebuildVisibleAiBehaviorEntries();
    }

    private void ValidateAiBehaviorCurrentText()
    {
        if (!TryParseJsonObjectDetailed(_aiBehaviorCurrentText, out JObject? root, out string error) || root == null)
        {
            _aiBehaviorTextValid = false;
            _aiBehaviorValidationStatus = $"Invalid JSON: {error}";
            return;
        }

        if (!TryFindAiTaskBehavior(root, out _, out JArray? tasks, out JObject? tasksByType, out _))
        {
            _aiBehaviorTextValid = false;
            _aiBehaviorValidationStatus = "No taskai behavior found.";
            return;
        }

        List<string> warnings = [];
        if (tasks == null)
        {
            warnings.Add("base aitasks array missing");
        }
        else
        {
            IReadOnlyList<string> knownCodes = GetKnownAiTaskCodes();
            HashSet<string> known = knownCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < tasks.Count; index++)
            {
                if (tasks[index] is not JObject task)
                {
                    warnings.Add($"task {index} is not an object");
                    continue;
                }

                string? code = GetAiBehaviorTaskCode(task);
                if (string.IsNullOrWhiteSpace(code))
                {
                    warnings.Add($"task {index} has no code");
                }
                else if (known.Count > 0 && !known.Contains(code))
                {
                    warnings.Add($"task {index} code '{code}' is not registered in AiTaskRegistry");
                }
            }
        }

        if (tasksByType != null)
        {
            warnings.Add("aitasksByType is preserved but not first-class editable yet");
        }

        _aiBehaviorTextValid = warnings.All(warning => !warning.Contains("not an object", StringComparison.OrdinalIgnoreCase) && !warning.Contains("has no code", StringComparison.OrdinalIgnoreCase));
        _aiBehaviorValidationStatus = warnings.Count == 0
            ? "Valid entity AI JSON."
            : $"{warnings.Count} warning(s): {string.Join("; ", warnings.Take(5))}{(warnings.Count > 5 ? $"; ...and {warnings.Count - 5} more" : "")}";
    }

    private bool IsAiBehaviorTextDirty(string currentText, string originalText)
    {
        if (TryParseJsonObjectDetailed(currentText, out JObject? current, out _) &&
            TryParseJsonObjectDetailed(originalText, out JObject? original, out _) &&
            current != null &&
            original != null)
        {
            return !JToken.DeepEquals(current, original);
        }

        return !string.Equals(currentText, originalText, StringComparison.Ordinal);
    }

    private SourceSaveResult TrySaveAiBehaviorToSource(AiBehaviorEntry entry)
    {
        try
        {
            if (!TryParseJsonObjectDetailed(_aiBehaviorCurrentText, out JObject? root, out string error) || root == null)
            {
                return SourceSaveResult.Fail($"Entity AI save failed: invalid JSON: {error}");
            }

            if (!TryFindAiTaskBehavior(root, out _, out _, out _, out _))
            {
                return SourceSaveResult.Fail("Entity AI save failed: no taskai behavior found.");
            }

            string relativePath = Path.Combine("assets", entry.Domain, entry.AssetPath.Replace('/', Path.DirectorySeparatorChar));
            string outputPath = GetToolAuthoredAssetPath("entity-ai", relativePath);
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : entry.SourceText;
            string newText = root.ToString(Formatting.Indented);
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved authored entity AI JSON to {outputPath}.",
                () =>
                {
                    string result = WriteAuthoredFile(outputPath, newText);
                    _aiBehaviorOriginalText = newText;
                    RememberAiBehaviorDraft();
                    RebuildVisibleAiBehaviorEntries();
                    return result;
                });
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            _aiBehaviorDiagnostics.Exception($"Entity AI save failed for {entry.DisplayCode}", exception);
            return SourceSaveResult.Fail($"Entity AI save failed for {entry.DisplayCode}: {exception.Message}");
        }
    }

    private void SelectLookedAtAiBehaviorEntity()
    {
        Entity? entity = TryGetLookedAtEntityForAiBehavior();
        if (entity != null)
        {
            SetAiBehaviorLiveTarget(entity, refresh: true);
        }

        AssetLocation? code = entity?.Properties?.Code;
        if (code == null)
        {
            _aiBehaviorStatus = "No looked-at entity target found.";
            return;
        }

        string key = NormalizeAiBehaviorEntityCode(code.Domain, code.Path);
        int index = _visibleAiBehaviorEntries.FindIndex(entry => entry.RuntimeCodes.Contains(key));
        if (index < 0)
        {
            index = _aiBehaviorEntries.FindIndex(entry => entry.RuntimeCodes.Contains(key));
            if (index >= 0)
            {
                _aiBehaviorFilter = "";
                _aiBehaviorDomainFilter = "";
                _aiBehaviorDirtyOnly = false;
                RebuildVisibleAiBehaviorEntries();
                index = _visibleAiBehaviorEntries.FindIndex(entry => entry.RuntimeCodes.Contains(key));
            }
        }

        if (index < 0)
        {
            _aiBehaviorStatus = $"No source taskai entry found for looked-at entity {key}.";
            return;
        }

        _aiBehaviorEntryIndex = index;
        LoadAiBehaviorEntry(_visibleAiBehaviorEntries[index], keepDirty: true);
        _aiBehaviorStatus = $"Selected looked-at entity {key}.";
    }

    private void SetAiBehaviorLiveTarget(Entity entity, bool refresh)
    {
        bool changedTarget = _aiBehaviorLiveEntityId != entity.EntityId;
        if (changedTarget)
        {
            RestoreAiBehaviorLiveFieldSnapshots(updateStatus: false);
        }

        _aiBehaviorLiveEntityId = entity.EntityId;
        AssetLocation? code = entity.Properties?.Code;
        _aiBehaviorLiveEntityCode = code == null ? "<unknown entity>" : $"{code.Domain}:{code.Path}";
        _aiBehaviorLiveStatus = refresh ? "Refreshing live AI task list." : "Live entity target selected.";
        _aiBehaviorLiveRefreshAccumulator = 0f;
        if (changedTarget)
        {
            _aiBehaviorLiveTransitions.Clear();
            _aiBehaviorLiveActiveBySlot.Clear();
        }
        if (refresh)
        {
            RefreshAiBehaviorLiveSnapshot(recordTransitions: false);
        }
    }

    private void RefreshAiBehaviorLiveSnapshot(bool recordTransitions = true)
    {
        _aiBehaviorLiveTasks.Clear();

        ICoreServerAPI? serverApi = InGameDevToolsModSystem.ActiveServerApi;
        if (serverApi == null)
        {
            _aiBehaviorLiveServerStatus = "Singleplayer server API: unavailable; live AI read requires singleplayer.";
            _aiBehaviorLiveStatus = "Source editing still works; no live task manager is available.";
            _aiBehaviorLiveActiveBySlot.Clear();
            return;
        }

        _aiBehaviorLiveServerStatus = "Singleplayer server API: available.";
        if (_aiBehaviorLiveEntityId == 0)
        {
            _aiBehaviorLiveStatus = "No live entity target selected.";
            _aiBehaviorLiveActiveBySlot.Clear();
            return;
        }

        try
        {
            Entity? serverEntity = serverApi.World.GetEntityById(_aiBehaviorLiveEntityId);
            if (serverEntity == null)
            {
                _aiBehaviorLiveStatus = $"Server entity #{_aiBehaviorLiveEntityId} was not found. Look at the entity again and refresh.";
                _aiBehaviorLiveActiveBySlot.Clear();
                return;
            }

            if (!TryGetAiTaskManager(serverEntity, out object? taskManager, out string managerSource) || taskManager == null)
            {
                _aiBehaviorLiveStatus = $"No live task manager found on {_aiBehaviorLiveEntityCode} #{_aiBehaviorLiveEntityId}.";
                _aiBehaviorLiveActiveBySlot.Clear();
                return;
            }

            IReadOnlyList<object> tasks = GetAiBehaviorLiveTasks(taskManager);
            HashSet<object> activeTasks = GetAiBehaviorActiveTaskSet(taskManager);
            foreach (object task in tasks)
            {
                _aiBehaviorLiveTasks.Add(BuildAiBehaviorLiveTaskInfo(task, activeTasks.Contains(task)));
            }

            Dictionary<string, string> activeBySlot = BuildAiBehaviorActiveSlotMap(_aiBehaviorLiveTasks);
            UpdateAiBehaviorLiveTransitions(activeBySlot, recordTransitions);
            _aiBehaviorLiveStatus = $"Live read: {_aiBehaviorLiveTasks.Count} task(s), {activeBySlot.Count} active slot(s) from {managerSource}.";
        }
        catch (Exception exception)
        {
            _aiBehaviorLiveStatus = $"Live AI read failed: {exception.Message}";
            _aiBehaviorDiagnostics.Exception("Entity AI live read failed", exception);
        }
    }

    private static Dictionary<string, string> BuildAiBehaviorActiveSlotMap(IEnumerable<AiBehaviorLiveTaskInfo> tasks)
    {
        Dictionary<string, string> activeBySlot = new(StringComparer.OrdinalIgnoreCase);
        int unnamedIndex = 0;

        foreach (AiBehaviorLiveTaskInfo task in tasks)
        {
            if (!task.IsActive) continue;

            string slot = string.IsNullOrWhiteSpace(task.Slot) || task.Slot == "?"
                ? $"task {++unnamedIndex}"
                : BuildAiBehaviorSlotKey(task.Slot);
            activeBySlot[slot] = task.Code;
        }

        return activeBySlot;
    }

    private string BuildAiBehaviorSlotBlockText(AiBehaviorLiveTaskInfo task)
    {
        if (task.IsActive) return "";
        string slotKey = BuildAiBehaviorSlotKey(task.Slot);
        if (string.IsNullOrWhiteSpace(slotKey)) return "";

        if (_aiBehaviorLiveActiveBySlot.TryGetValue(slotKey, out string? activeCode) &&
            !string.Equals(activeCode, task.Code, StringComparison.OrdinalIgnoreCase))
        {
            return $"{slotKey} is currently occupied by {activeCode}";
        }

        return "";
    }

    private static string BuildAiBehaviorSlotKey(string slot)
    {
        return string.IsNullOrWhiteSpace(slot) || slot == "?"
            ? ""
            : $"slot {slot}";
    }

    private void UpdateAiBehaviorLiveTransitions(IReadOnlyDictionary<string, string> activeBySlot, bool recordTransitions)
    {
        if (recordTransitions)
        {
            foreach (KeyValuePair<string, string> previous in _aiBehaviorLiveActiveBySlot)
            {
                if (!activeBySlot.ContainsKey(previous.Key))
                {
                    AddAiBehaviorLiveTransition($"{previous.Key}: stopped {previous.Value}");
                }
            }

            foreach (KeyValuePair<string, string> current in activeBySlot)
            {
                if (!_aiBehaviorLiveActiveBySlot.TryGetValue(current.Key, out string? previousCode))
                {
                    AddAiBehaviorLiveTransition($"{current.Key}: started {current.Value}");
                }
                else if (!string.Equals(previousCode, current.Value, StringComparison.OrdinalIgnoreCase))
                {
                    AddAiBehaviorLiveTransition($"{current.Key}: {previousCode} -> {current.Value}");
                }
            }
        }

        _aiBehaviorLiveActiveBySlot.Clear();
        foreach (KeyValuePair<string, string> active in activeBySlot)
        {
            _aiBehaviorLiveActiveBySlot[active.Key] = active.Value;
        }
    }

    private void AddAiBehaviorLiveTransition(string text)
    {
        _aiBehaviorLiveTransitions.Add(new AiBehaviorLiveTransition(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), text));
        const int maxTransitions = 80;
        if (_aiBehaviorLiveTransitions.Count > maxTransitions)
        {
            _aiBehaviorLiveTransitions.RemoveRange(0, _aiBehaviorLiveTransitions.Count - maxTransitions);
        }
    }

    private bool TryGetAiTaskManager(Entity serverEntity, out object? taskManager, out string source)
    {
        foreach (object? behavior in EnumerateAiBehaviorCandidates(serverEntity))
        {
            taskManager = TryGetMemberValue(behavior, "TaskManager");
            if (taskManager != null)
            {
                source = $"{behavior.GetType().Name}.TaskManager";
                return true;
            }
        }

        if (TryFindAiTaskManagerInObjectGraph(serverEntity, out taskManager, out source))
        {
            return true;
        }

        taskManager = null;
        source = "";
        return false;
    }

    private IEnumerable<object> EnumerateAiBehaviorCandidates(Entity serverEntity)
    {
        Type entityType = serverEntity.GetType();
        Type? taskAiBehaviorType = FindAiBehaviorType("EntityBehaviorTaskAI");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        if (taskAiBehaviorType != null)
        {
            foreach (MethodInfo method in entityType.GetMethods(flags))
            {
                if (!string.Equals(method.Name, "GetBehavior", StringComparison.Ordinal) ||
                    !method.IsGenericMethodDefinition ||
                    method.GetGenericArguments().Length != 1 ||
                    method.GetParameters().Length != 0)
                {
                    continue;
                }

                object? behavior;
                try
                {
                    behavior = method.MakeGenericMethod(taskAiBehaviorType).Invoke(serverEntity, null);
                }
                catch
                {
                    continue;
                }

                if (behavior != null) yield return behavior;
            }
        }

        foreach (MethodInfo method in entityType.GetMethods(flags))
        {
            if (!string.Equals(method.Name, "GetBehavior", StringComparison.Ordinal)) continue;

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string)) continue;

            foreach (string behaviorCode in new[] { "taskai", "TaskAI", "ai", "entityai" })
            {
                object? behavior;
                try
                {
                    behavior = method.Invoke(serverEntity, [behaviorCode]);
                }
                catch
                {
                    continue;
                }

                if (behavior != null) yield return behavior;
            }
        }
    }

    private static Type? FindAiBehaviorType(string typeNameSuffix)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? direct = assembly.GetType($"Vintagestory.GameContent.{typeNameSuffix}", throwOnError: false);
            if (direct != null) return direct;

            foreach (Type type in GetAiBehaviorLoadableTypes(assembly))
            {
                if (type.Name.Equals(typeNameSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }
            }
        }

        return null;
    }

    private static IEnumerable<Type> GetAiBehaviorLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null)!;
        }
        catch
        {
            return [];
        }
    }

    private static bool TryFindAiTaskManagerInObjectGraph(object root, out object? taskManager, out string source)
    {
        Queue<(object Value, string Source, int Depth)> queue = new();
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        queue.Enqueue((root, root.GetType().Name, 0));

        while (queue.Count > 0 && visited.Count < 350)
        {
            (object value, string currentSource, int depth) = queue.Dequeue();
            if (!visited.Add(value)) continue;

            object? direct = TryGetMemberValue(value, "TaskManager");
            if (direct != null)
            {
                taskManager = direct;
                source = $"{currentSource}.TaskManager";
                return true;
            }

            if (depth >= 3) continue;

            foreach ((string memberName, object? child) in EnumerateAiBehaviorObjectChildren(value))
            {
                if (child == null || child is string || child.GetType().IsValueType) continue;

                if (child is IEnumerable enumerable && child is not JObject && child is not JToken)
                {
                    int itemIndex = 0;
                    foreach (object? item in enumerable)
                    {
                        if (item == null || item is string || item.GetType().IsValueType) continue;
                        if (itemIndex++ > 64) break;
                        queue.Enqueue((item, $"{currentSource}.{memberName}[{itemIndex - 1}]", depth + 1));
                    }
                    continue;
                }

                queue.Enqueue((child, $"{currentSource}.{memberName}", depth + 1));
            }
        }

        taskManager = null;
        source = "";
        return false;
    }

    private static IEnumerable<(string Name, object? Value)> EnumerateAiBehaviorObjectChildren(object value)
    {
        Type type = value.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (FieldInfo field in type.GetFields(flags))
        {
            if (!ShouldProbeAiBehaviorMember(field.FieldType, field.Name)) continue;
            object? child;
            try
            {
                child = field.GetValue(value);
            }
            catch
            {
                continue;
            }
            yield return (field.Name, child);
        }

        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            if (property.GetIndexParameters().Length != 0 || !ShouldProbeAiBehaviorMember(property.PropertyType, property.Name)) continue;
            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch
            {
                continue;
            }
            yield return (property.Name, child);
        }
    }

    private static bool ShouldProbeAiBehaviorMember(Type memberType, string memberName)
    {
        if (memberType.IsPrimitive || memberType.IsEnum || memberType == typeof(string)) return false;
        string name = memberName.ToLowerInvariant();
        string typeName = memberType.FullName?.ToLowerInvariant() ?? "";
        return name.Contains("behavior", StringComparison.Ordinal) ||
            name.Contains("task", StringComparison.Ordinal) ||
            name.Contains("ai", StringComparison.Ordinal) ||
            typeName.Contains("behavior", StringComparison.Ordinal) ||
            typeName.Contains("task", StringComparison.Ordinal) ||
            typeName.Contains("ai", StringComparison.Ordinal);
    }

    private static IReadOnlyList<object> GetAiBehaviorLiveTasks(object taskManager)
    {
        object? allTasks = TryGetMemberValue(taskManager, "AllTasks");
        if (allTasks is not IEnumerable enumerable) return [];

        List<object> tasks = [];
        foreach (object? task in enumerable)
        {
            if (task != null) tasks.Add(task);
        }
        return tasks;
    }

    private static HashSet<object> GetAiBehaviorActiveTaskSet(object taskManager)
    {
        HashSet<object> activeTasks = new(ReferenceEqualityComparer.Instance);
        object? active = TryGetMemberValue(taskManager, "ActiveTasksBySlot");
        if (active == null) return activeTasks;

        if (active is IDictionary dictionary)
        {
            foreach (object? value in dictionary.Values)
            {
                AddAiBehaviorActiveTaskValue(activeTasks, value);
            }
            return activeTasks;
        }

        AddAiBehaviorActiveTaskValue(activeTasks, active);
        return activeTasks;
    }

    private static void AddAiBehaviorActiveTaskValue(HashSet<object> activeTasks, object? value)
    {
        if (value == null || value is string) return;
        if (value is IEnumerable enumerable)
        {
            foreach (object? item in enumerable)
            {
                if (item != null) activeTasks.Add(item);
            }
            return;
        }

        activeTasks.Add(value);
    }

    private AiBehaviorLiveTaskInfo BuildAiBehaviorLiveTaskInfo(object task, bool isActive)
    {
        string typeName = task.GetType().FullName ?? task.GetType().Name;
        string code = GetAiBehaviorTaskRegistryCode(task.GetType())
            ?? ReadAiBehaviorMemberString(task, "Code")
            ?? ReadAiBehaviorMemberString(task, "code")
            ?? task.GetType().Name;
        string priority = ReadAiBehaviorMemberString(task, "Priority")
            ?? ReadAiBehaviorMemberString(task, "priority")
            ?? "?";
        string slot = ReadAiBehaviorMemberString(task, "Slot")
            ?? ReadAiBehaviorMemberString(task, "slot")
            ?? "?";
        string cooldown = BuildAiBehaviorCooldownText(task);
        AiBehaviorLiveGateInfo gateInfo = BuildAiBehaviorLiveGateInfo(task, isActive);
        return new AiBehaviorLiveTaskInfo(task, code, typeName, priority, slot, cooldown, isActive, gateInfo.Summary, gateInfo.Details);
    }

    private static AiBehaviorLiveGateInfo BuildAiBehaviorLiveGateInfo(object task, bool isActive)
    {
        List<string> summary = [];
        List<string> details = [];

        if (isActive)
        {
            summary.Add("running now");
        }

        if (TryReadAiBehaviorNumber(task, out double executionChance, "ExecutionChance", "executionChance", "chance"))
        {
            details.Add($"Execution chance: {executionChance.ToString("0.###", CultureInfo.InvariantCulture)}");
            if (executionChance >= 0 && executionChance < 0.999)
            {
                summary.Add($"chance {executionChance:P0}");
            }
        }

        AddAiBehaviorGateDetail(task, details, summary, "Cooldown until", "cooldown pending",
            "CooldownUntilMs", "cooldownUntilMs", "CooldownUntilTotalMs", "cooldownUntilTotalMs",
            "CooldownUntilTotalHours", "cooldownUntilTotalHours", "cooldownUntilWorldTime", "nextExecuteTotalHours");
        AddAiBehaviorGateDetail(task, details, summary, "Emotion required", "emotion gate",
            "WhenInEmotionStates", "whenInEmotionStates");
        AddAiBehaviorGateDetail(task, details, summary, "Emotion blocked", "emotion gate",
            "WhenNotInEmotionStates", "whenNotInEmotionStates");
        AddAiBehaviorGateDetail(task, details, summary, "Swimming", "swim gate",
            "WhenSwimming", "whenSwimming");
        AddAiBehaviorGateDetail(task, details, summary, "Day time frames", "time gate",
            "duringDayTimeFrames", "DuringDayTimeFrames", "DayTimeFrames", "dayTimeFrames");
        AddAiBehaviorGateDetail(task, details, summary, "Light levels", "light gate",
            "EntityLightLevels", "entityLightLevels", "LightLevelRange", "lightLevelRange");
        AddAiBehaviorGateDetail(task, details, summary, "Temperature range", "temperature gate",
            "TemperatureRange", "temperatureRange");

        if (details.Count == 0)
        {
            details.Add("No common gate fields were readable on this task. Exact ShouldExecute probing is not run automatically because task implementations can have side effects.");
        }

        if (summary.Count == 0)
        {
            summary.Add("inactive; no common blockers readable");
        }

        return new AiBehaviorLiveGateInfo(string.Join("; ", summary.Distinct(StringComparer.OrdinalIgnoreCase)), string.Join("\n", details));
    }

    private static void AddAiBehaviorGateDetail(object task, List<string> details, List<string> summary, string label, string summaryLabel, params string[] memberNames)
    {
        if (!TryReadAiBehaviorMember(task, out string memberName, out string text, memberNames)) return;
        details.Add($"{label} ({memberName}): {text}");
        if (!string.IsNullOrWhiteSpace(summaryLabel))
        {
            summary.Add(summaryLabel);
        }
    }

    private string? GetAiBehaviorTaskRegistryCode(Type taskType)
    {
        try
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? registry = assembly.GetType("Vintagestory.GameContent.AiTaskRegistry", throwOnError: false);
                object? taskCodes = registry?.GetField("TaskCodes", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (taskCodes is not IDictionary dictionary) continue;

                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is Type registeredType && registeredType.IsAssignableFrom(taskType))
                    {
                        string? code = entry.Value?.ToString();
                        if (!string.IsNullOrWhiteSpace(code)) return code;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            _aiBehaviorDiagnostics.Warning($"Could not read AiTaskRegistry task labels: {exception.Message}");
        }

        return null;
    }

    private static string BuildAiBehaviorCooldownText(object task)
    {
        string? min = ReadAiBehaviorMemberString(task, "MinCooldownMs") ?? ReadAiBehaviorMemberString(task, "mincooldown");
        string? max = ReadAiBehaviorMemberString(task, "MaxCooldownMs") ?? ReadAiBehaviorMemberString(task, "maxcooldown");
        if (!string.IsNullOrWhiteSpace(min) || !string.IsNullOrWhiteSpace(max))
        {
            return $"{min ?? "?"}-{max ?? "?"} ms";
        }

        return ReadAiBehaviorMemberString(task, "CooldownUntilMs") ??
            ReadAiBehaviorMemberString(task, "cooldownUntilMs") ??
            "?";
    }

    private static string? ReadAiBehaviorMemberString(object value, string memberName)
    {
        object? memberValue = TryGetMemberValue(value, memberName);
        return memberValue switch
        {
            null => null,
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            decimal d => d.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => memberValue.ToString()
        };
    }

    private static bool TryReadAiBehaviorNumber(object value, out double number, params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            object? memberValue = TryGetMemberValue(value, memberName);
            if (memberValue == null) continue;

            try
            {
                number = Convert.ToDouble(memberValue, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                if (double.TryParse(memberValue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    return true;
                }
            }
        }

        number = 0;
        return false;
    }

    private static bool TryReadAiBehaviorMember(object value, out string memberName, out string text, params string[] memberNames)
    {
        foreach (string candidate in memberNames)
        {
            object? memberValue = TryGetMemberValue(value, candidate);
            if (!IsMeaningfulAiBehaviorGateValue(memberValue)) continue;

            memberName = candidate;
            text = FormatAiBehaviorMemberValue(memberValue!, 180);
            return true;
        }

        memberName = "";
        text = "";
        return false;
    }

    private static bool IsMeaningfulAiBehaviorGateValue(object? value)
    {
        if (value == null) return false;
        if (value is string text) return !string.IsNullOrWhiteSpace(text);
        if (value is bool) return true;
        if (value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            try
            {
                return Math.Abs(Convert.ToDouble(value, CultureInfo.InvariantCulture)) > double.Epsilon;
            }
            catch
            {
                return true;
            }
        }

        if (value is IEnumerable enumerable)
        {
            foreach (object? item in enumerable)
            {
                if (item != null) return true;
            }

            return false;
        }

        return true;
    }

    private static string FormatAiBehaviorMemberValue(object value, int maxLength)
    {
        string text;
        if (value is string stringValue)
        {
            text = stringValue;
        }
        else if (value is IEnumerable enumerable)
        {
            List<string> items = [];
            int count = 0;
            foreach (object? item in enumerable)
            {
                if (count++ >= 8)
                {
                    items.Add("...");
                    break;
                }

                items.Add(item == null ? "null" : Convert.ToString(item, CultureInfo.InvariantCulture) ?? item.ToString() ?? "?");
            }

            text = items.Count == 0 ? "[]" : $"[{string.Join(", ", items)}]";
        }
        else if (value is IFormattable formattable)
        {
            text = formattable.ToString(null, CultureInfo.InvariantCulture);
        }
        else
        {
            text = value.ToString() ?? "?";
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= maxLength ? text : $"{text[..Math.Max(0, maxLength - 3)]}...";
    }

    private bool TrySetAiBehaviorLiveMember(object task, AiBehaviorLiveMember member, object? value)
    {
        if (!member.CanWrite)
        {
            _aiBehaviorLiveStatus = $"{member.Name} is not writable on {task.GetType().Name}.";
            return false;
        }

        CaptureAiBehaviorLiveFieldSnapshot(task, member);
        if (!TrySetAiBehaviorMemberValue(task, member.Name, value, out string error))
        {
            _aiBehaviorLiveStatus = $"Live AI edit failed for {member.Name}: {error}";
            return false;
        }

        _aiBehaviorLiveStatus = $"Live edited {member.Name} on {task.GetType().Name}; use Revert live AI edits to restore.";
        return true;
    }

    private void CaptureAiBehaviorLiveFieldSnapshot(object task, AiBehaviorLiveMember member)
    {
        int taskRuntimeId = RuntimeHelpers.GetHashCode(task);
        bool exists = _aiBehaviorLiveFieldSnapshots.Any(snapshot =>
            snapshot.EntityId == _aiBehaviorLiveEntityId &&
            snapshot.TaskRuntimeId == taskRuntimeId &&
            string.Equals(snapshot.MemberName, member.Name, StringComparison.OrdinalIgnoreCase));
        if (exists) return;

        _aiBehaviorLiveFieldSnapshots.Add(new AiBehaviorLiveFieldSnapshot(
            _aiBehaviorLiveEntityId,
            taskRuntimeId,
            new WeakReference<object>(task),
            member.Name,
            CloneAiBehaviorLiveValue(member.Value)));
    }

    private void RestoreAiBehaviorLiveFieldSnapshots(bool updateStatus)
    {
        long targetEntityId = _aiBehaviorLiveEntityId;
        bool restoreAll = targetEntityId == 0;
        int restored = 0;
        int skipped = 0;

        for (int index = _aiBehaviorLiveFieldSnapshots.Count - 1; index >= 0; index--)
        {
            AiBehaviorLiveFieldSnapshot snapshot = _aiBehaviorLiveFieldSnapshots[index];
            if (!restoreAll && snapshot.EntityId != targetEntityId) continue;

            if (snapshot.Target.TryGetTarget(out object? task) &&
                TrySetAiBehaviorMemberValue(task, snapshot.MemberName, CloneAiBehaviorLiveValue(snapshot.OriginalValue), out _))
            {
                restored++;
            }
            else
            {
                skipped++;
            }

            _aiBehaviorLiveFieldSnapshots.RemoveAt(index);
        }

        if (updateStatus)
        {
            _aiBehaviorLiveStatus = skipped == 0
                ? $"Reverted {restored} live AI field edit(s)."
                : $"Reverted {restored} live AI field edit(s); skipped {skipped} stale task reference(s).";
        }
    }

    private static bool TryFindAiBehaviorLiveMember(object value, IEnumerable<string> memberNames, out AiBehaviorLiveMember member)
    {
        foreach (string memberName in memberNames)
        {
            if (TryFindAiBehaviorProperty(value.GetType(), memberName, out PropertyInfo? property) && property != null)
            {
                object? memberValue;
                try
                {
                    memberValue = property.GetValue(value);
                }
                catch
                {
                    continue;
                }

                member = new AiBehaviorLiveMember(
                    property.Name,
                    property.PropertyType,
                    memberValue,
                    property.GetSetMethod(nonPublic: true) != null);
                return true;
            }

            if (TryFindAiBehaviorField(value.GetType(), memberName, out FieldInfo? field) && field != null)
            {
                object? memberValue;
                try
                {
                    memberValue = field.GetValue(value);
                }
                catch
                {
                    continue;
                }

                member = new AiBehaviorLiveMember(
                    field.Name,
                    field.FieldType,
                    memberValue,
                    !field.IsInitOnly && !field.IsLiteral);
                return true;
            }
        }

        member = default;
        return false;
    }

    private static bool TrySetAiBehaviorMemberValue(object value, string memberName, object? newValue, out string error)
    {
        if (TryFindAiBehaviorProperty(value.GetType(), memberName, out PropertyInfo? property) && property != null)
        {
            MethodInfo? setter = property.GetSetMethod(nonPublic: true);
            if (setter == null)
            {
                error = "property has no setter";
                return false;
            }

            if (!TryConvertAiBehaviorLiveValue(newValue, property.PropertyType, out object? converted))
            {
                error = $"cannot convert value to {property.PropertyType.Name}";
                return false;
            }

            try
            {
                property.SetValue(value, converted);
                error = "";
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        if (TryFindAiBehaviorField(value.GetType(), memberName, out FieldInfo? field) && field != null)
        {
            if (field.IsInitOnly || field.IsLiteral)
            {
                error = "field is read-only";
                return false;
            }

            if (!TryConvertAiBehaviorLiveValue(newValue, field.FieldType, out object? converted))
            {
                error = $"cannot convert value to {field.FieldType.Name}";
                return false;
            }

            try
            {
                field.SetValue(value, converted);
                error = "";
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        error = "member was not found";
        return false;
    }

    private static bool TryFindAiBehaviorProperty(Type type, string memberName, out PropertyInfo? property)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (Type? current = type; current != null; current = current.BaseType)
        {
            property = current.GetProperties(flags)
                .FirstOrDefault(candidate => candidate.GetIndexParameters().Length == 0 && string.Equals(candidate.Name, memberName, StringComparison.OrdinalIgnoreCase));
            if (property != null) return true;
        }

        property = null;
        return false;
    }

    private static bool TryFindAiBehaviorField(Type type, string memberName, out FieldInfo? field)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (Type? current = type; current != null; current = current.BaseType)
        {
            field = current.GetFields(flags)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, memberName, StringComparison.OrdinalIgnoreCase));
            if (field != null) return true;
        }

        field = null;
        return false;
    }

    private static bool TryConvertAiBehaviorNumber(object? value, out double number)
    {
        if (value == null)
        {
            number = 0;
            return false;
        }

        try
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }
    }

    private static bool TryConvertAiBehaviorLiveValue(object? value, Type targetType, out object? converted)
    {
        Type nullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value == null)
        {
            converted = nullableType.IsValueType ? Activator.CreateInstance(nullableType) : null;
            return true;
        }

        if (targetType.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }

        if (nullableType.IsEnum)
        {
            try
            {
                converted = value is string text
                    ? Enum.Parse(nullableType, text, ignoreCase: true)
                    : Enum.ToObject(nullableType, value);
                return true;
            }
            catch
            {
                converted = null;
                return false;
            }
        }

        if (nullableType.IsArray && value is IEnumerable enumerableValue)
        {
            Type elementType = nullableType.GetElementType() ?? typeof(object);
            List<object?> convertedItems = [];
            foreach (object? item in enumerableValue)
            {
                if (!TryConvertAiBehaviorLiveValue(item, elementType, out object? convertedItem))
                {
                    converted = null;
                    return false;
                }
                convertedItems.Add(convertedItem);
            }

            Array array = Array.CreateInstance(elementType, convertedItems.Count);
            for (int index = 0; index < convertedItems.Count; index++)
            {
                array.SetValue(convertedItems[index], index);
            }

            converted = array;
            return true;
        }

        try
        {
            converted = Convert.ChangeType(value, nullableType, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            converted = null;
            return false;
        }
    }

    private static bool TryExtractAiBehaviorRange(object? value, out NVector2 range)
    {
        List<float> numbers = [];
        if (value is IEnumerable enumerable && value is not string)
        {
            foreach (object? item in enumerable)
            {
                if (TryConvertAiBehaviorNumber(item, out double number))
                {
                    numbers.Add((float)number);
                }

                if (numbers.Count >= 2) break;
            }
        }

        if (numbers.Count >= 2)
        {
            range = new NVector2(numbers[0], numbers[1]);
            return true;
        }

        range = default;
        return false;
    }

    private static bool TryCreateAiBehaviorRangeValue(Type targetType, NVector2 range, out object? value)
    {
        Type nullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (!nullableType.IsArray)
        {
            value = null;
            return false;
        }

        Type elementType = nullableType.GetElementType() ?? typeof(float);
        Array array = Array.CreateInstance(elementType, 2);
        if (!TryConvertAiBehaviorLiveValue(range.X, elementType, out object? first) ||
            !TryConvertAiBehaviorLiveValue(range.Y, elementType, out object? second))
        {
            value = null;
            return false;
        }

        array.SetValue(first, 0);
        array.SetValue(second, 1);
        value = array;
        return true;
    }

    private static object? CloneAiBehaviorLiveValue(object? value)
    {
        if (value is Array array)
        {
            return array.Clone();
        }

        return value;
    }

    private static object? TryGetMemberValue(object? value, string memberName)
    {
        if (value == null) return null;

        PropertyInfo? property = TryFindAiBehaviorProperty(value.GetType(), memberName, out PropertyInfo? foundProperty)
            ? foundProperty
            : null;
        if (property != null && property.GetIndexParameters().Length == 0)
        {
            try
            {
                return property.GetValue(value);
            }
            catch
            {
                // Try a field below.
            }
        }

        FieldInfo? field = TryFindAiBehaviorField(value.GetType(), memberName, out FieldInfo? foundField)
            ? foundField
            : null;
        if (field != null)
        {
            try
            {
                return field.GetValue(value);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private Entity? TryGetLookedAtEntityForAiBehavior()
    {
        try
        {
            object? player = _api.World?.Player;
            object? selection = player?.GetType().GetProperty("CurrentEntitySelection")?.GetValue(player);
            return selection?.GetType().GetProperty("Entity")?.GetValue(selection) as Entity;
        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyList<string> GetKnownAiTaskCodes()
    {
        if (_aiBehaviorKnownTaskCodes != null) return _aiBehaviorKnownTaskCodes;

        SortedSet<string> codes = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? registry = assembly.GetType("Vintagestory.GameContent.AiTaskRegistry", throwOnError: false);
                object? taskTypes = registry?.GetField("TaskTypes", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                if (taskTypes is System.Collections.IDictionary dictionary)
                {
                    foreach (object? key in dictionary.Keys)
                    {
                        string? code = key?.ToString();
                        if (!string.IsNullOrWhiteSpace(code)) codes.Add(code);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            _aiBehaviorDiagnostics.Warning($"Could not read AiTaskRegistry task codes: {exception.Message}");
        }

        if (codes.Count == 0)
        {
            foreach (string fallback in new[] { "wander", "idle", "seekentity", "meleeattack", "fleeentity", "lookaround", "stayclose", "gotoentity", "eat", "die" })
            {
                codes.Add(fallback);
            }
        }

        _aiBehaviorKnownTaskCodes = codes.ToArray();
        return _aiBehaviorKnownTaskCodes;
    }

    private static bool TryFindAiTaskBehavior(JObject root, out JObject? behavior, out JArray? tasks, out JObject? tasksByType, out string behaviorPath)
    {
        behavior = null;
        tasks = null;
        tasksByType = null;
        behaviorPath = "";

        if (root["server"] is not JObject server || server["behaviors"] is not JArray behaviors)
        {
            return false;
        }

        for (int index = 0; index < behaviors.Count; index++)
        {
            if (behaviors[index] is not JObject candidate) continue;
            string? code = candidate["code"]?.ToString() ?? candidate["name"]?.ToString();
            bool isTaskAi = string.Equals(code, "taskai", StringComparison.OrdinalIgnoreCase) ||
                candidate["aitasks"] is JArray ||
                candidate["aitasksByType"] is JObject;
            if (!isTaskAi) continue;

            behavior = candidate;
            tasks = candidate["aitasks"] as JArray;
            tasksByType = candidate["aitasksByType"] as JObject;
            behaviorPath = $"server.behaviors[{index}]";
            return true;
        }

        return false;
    }

    private static int CountAiBehaviorTasksByType(JObject? tasksByType)
    {
        if (tasksByType == null) return 0;
        int count = 0;
        foreach (JToken token in tasksByType.DescendantsAndSelf())
        {
            if (token is JArray array)
            {
                count += array.Count;
            }
        }
        return count;
    }

    private IEnumerable<string> ExpandAiBehaviorEntityCodes(string domain, JObject sourceJson, string sourceCode)
    {
        if (sourceJson["variantgroups"] is not JArray groups || groups.Count == 0)
        {
            yield return sourceCode;
            yield break;
        }

        List<AiBehaviorVariantGroup> variantGroups = [];
        foreach (JObject group in groups.OfType<JObject>())
        {
            string? groupCode = group["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(groupCode)) continue;
            List<string> states = ResolveAiBehaviorVariantStates(domain, group).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (states.Count == 0) yield break;
            variantGroups.Add(new(groupCode, states));
        }

        if (variantGroups.Count == 0)
        {
            yield return sourceCode;
            yield break;
        }

        foreach (Dictionary<string, string> combination in BuildAiBehaviorVariantCombinations(variantGroups))
        {
            yield return BuildAiBehaviorVariantCode(sourceCode, variantGroups, combination);
        }
    }

    private IEnumerable<string> ResolveAiBehaviorVariantStates(string domain, JObject group)
    {
        if (group["states"] is JArray states)
        {
            foreach (JToken state in states)
            {
                string? value = state.ToString();
                if (!string.IsNullOrWhiteSpace(value)) yield return value;
            }
        }

        string? loadFromProperties = group["loadFromProperties"]?.ToString();
        if (!string.IsNullOrWhiteSpace(loadFromProperties))
        {
            foreach (string value in LoadAiBehaviorWorldPropertyStates(domain, loadFromProperties))
            {
                yield return value;
            }
        }
    }

    private IEnumerable<string> LoadAiBehaviorWorldPropertyStates(string domain, string loadFromProperties)
    {
        string path = EnsureJsonFilePath($"worldproperties/{loadFromProperties.Trim().TrimStart('/')}");
        foreach (string candidateDomain in new[] { domain, "game" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IAsset? asset = _api.Assets.TryGet(new AssetLocation(candidateDomain, path), true);
            JObject? json = TryParseJsonObject(ReadAssetText(asset));
            if (json?["variants"] is not JArray variants) continue;

            foreach (JToken variant in variants)
            {
                string? code = variant.Type == JTokenType.String
                    ? variant.ToString()
                    : variant["Code"]?.ToString() ?? variant["code"]?.ToString();
                if (!string.IsNullOrWhiteSpace(code)) yield return code;
            }

            yield break;
        }
    }

    private static IEnumerable<Dictionary<string, string>> BuildAiBehaviorVariantCombinations(IReadOnlyList<AiBehaviorVariantGroup> groups)
    {
        List<Dictionary<string, string>> combinations = [new(StringComparer.OrdinalIgnoreCase)];
        foreach (AiBehaviorVariantGroup group in groups)
        {
            List<Dictionary<string, string>> next = [];
            foreach (Dictionary<string, string> combination in combinations)
            {
                foreach (string state in group.States)
                {
                    Dictionary<string, string> copy = new(combination, StringComparer.OrdinalIgnoreCase)
                    {
                        [group.Code] = state
                    };
                    next.Add(copy);
                }
            }

            combinations = next;
        }

        return combinations;
    }

    private static string BuildAiBehaviorVariantCode(string sourceCode, IReadOnlyList<AiBehaviorVariantGroup> groups, IReadOnlyDictionary<string, string> states)
    {
        string code = sourceCode;
        List<string> suffixes = [];
        foreach (AiBehaviorVariantGroup group in groups)
        {
            if (!states.TryGetValue(group.Code, out string? state)) continue;
            string placeholder = "{" + group.Code + "}";
            if (code.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                code = ReplaceAiBehaviorInvariant(code, placeholder, state);
            }
            else
            {
                suffixes.Add(state);
            }
        }

        return suffixes.Count == 0 ? code : $"{code}-{string.Join('-', suffixes)}";
    }

    private static string ReplaceAiBehaviorInvariant(string value, string oldValue, string newValue)
    {
        int index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            value = value[..index] + newValue + value[(index + oldValue.Length)..];
            index = value.IndexOf(oldValue, index + newValue.Length, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private static string NormalizeAiBehaviorEntityCode(string defaultDomain, string code)
    {
        string trimmed = StripAiBehaviorCodeDomain(code);
        string domain = code.Contains(':', StringComparison.Ordinal) ? code[..code.IndexOf(':')] : defaultDomain;
        return $"{domain}:{trimmed}";
    }

    private static string StripAiBehaviorCodeDomain(string code)
    {
        int separator = code.IndexOf(':');
        return separator >= 0 ? code[(separator + 1)..] : code;
    }

    private static string? GetAiBehaviorTaskCode(JObject task)
    {
        return task["code"]?.ToString();
    }

    private static string GetAiBehaviorTaskLabel(JToken token, int index)
    {
        if (token is not JObject task) return $"{index}: <invalid task>";

        string code = GetAiBehaviorTaskCode(task) ?? "<missing code>";
        List<string> details = [];
        if (task["id"] != null) details.Add($"id {task["id"]}");
        if (task["priority"] != null) details.Add($"p {task["priority"]}");
        if (task["slot"] != null) details.Add($"slot {task["slot"]}");
        return details.Count == 0
            ? $"{index}: {code}"
            : $"{index}: {code} ({string.Join(", ", details)})";
    }

    private static string SummarizeAiBehaviorToken(JToken token, int maxLength)
    {
        string text = token.Type is JTokenType.Object or JTokenType.Array
            ? token.ToString(Formatting.None)
            : token.ToString();
        return text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static readonly HashSet<string> AiBehaviorFirstClassProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "code",
        "id",
        "priority",
        "slot",
        "executionChance",
        "mincooldown",
        "maxcooldown",
        "moveSpeed",
        "seekingRange",
        "attackRange",
        "damage"
    };

    private static readonly AiBehaviorLiveNumericSpec[] AiBehaviorLiveNumericSpecs =
    [
        new("Move speed", 0f, 10f, "%.3f", ["moveSpeed", "movespeed", "MoveSpeed"]),
        new("Seeking range", 0f, 256f, "%.2f", ["seekingRange", "SeekingRange", "seekRange", "range"]),
        new("Attack range", 0f, 64f, "%.2f", ["attackRange", "AttackRange"]),
        new("Damage", 0f, 500f, "%.2f", ["damage", "Damage"]),
        new("Knockback strength", 0f, 100f, "%.2f", ["knockbackStrength", "KnockbackStrength"]),
        new("Attack angle deg", 0f, 360f, "%.1f", ["attackAngleRangeDeg", "AttackAngleRangeDeg"]),
        new("Max follow time", 0f, 3600f, "%.1f", ["maxFollowTime", "MaxFollowTime"]),
        new("Leap chance", 0f, 1f, "%.3f", ["leapChance", "LeapChance"]),
        new("Flee distance", 0f, 256f, "%.2f", ["fleeDistance", "FleeDistance"]),
        new("Retaliate range", 0f, 256f, "%.2f", ["retaliateRange", "RetaliateRange"])
    ];

    private enum AiBehaviorIndexState
    {
        Idle,
        Indexing,
        Ready,
        Failed
    }

    private sealed record AiBehaviorEntry(
        IAsset Asset,
        string SourceText,
        JObject SourceJson,
        string SourceCode,
        IReadOnlySet<string> RuntimeCodes,
        int TaskCount,
        int TasksByTypeCount,
        bool HasBaseTasks,
        bool HasTasksByType)
    {
        public string Domain => Asset.Location.Domain ?? "game";
        public string AssetPath => Asset.Location.Path.Replace('\\', '/');
        public string Key => Asset.Location.ToString();
        public string DisplayCode => $"{Domain}:{SourceCode}";
        public string SortKey => $"{DisplayCode}:{AssetPath}";
        public string SearchText => $"{DisplayCode} {AssetPath} {SourceText}";
    }

    private sealed record AiBehaviorDraftState(
        string Text,
        int TaskIndex,
        bool IsValid,
        string ValidationStatus,
        string OriginalText)
    {
        public bool IsDirty => !string.Equals(Text, OriginalText, StringComparison.Ordinal);
    }

    private sealed record AiBehaviorLiveTaskInfo(
        object TaskObject,
        string Code,
        string TypeName,
        string Priority,
        string Slot,
        string Cooldown,
        bool IsActive,
        string GateSummary,
        string GateDetails);

    private sealed record AiBehaviorLiveTransition(string Time, string Text);

    private sealed record AiBehaviorLiveGateInfo(string Summary, string Details);

    private readonly record struct AiBehaviorLiveMember(string Name, Type ValueType, object? Value, bool CanWrite);

    private sealed record AiBehaviorLiveFieldSnapshot(
        long EntityId,
        int TaskRuntimeId,
        WeakReference<object> Target,
        string MemberName,
        object? OriginalValue);

    private sealed record AiBehaviorLiveNumericSpec(
        string Label,
        float Min,
        float Max,
        string Format,
        string[] MemberNames);

    private sealed record AiBehaviorVariantGroup(string Code, IReadOnlyList<string> States);
}
