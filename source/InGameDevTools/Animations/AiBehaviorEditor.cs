using InGameDevTools.Utils;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

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

    private void AiBehaviorEditorTab(float deltaSeconds, bool showDiagnostics)
    {
        _ = deltaSeconds;

        try
        {
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
        // Live AI tuning is intentionally not implemented in the source-editor first pass.
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
        ImGui.TextWrapped("Source edits change the entity type for future spawns after authored files are loaded. Live single-entity tuning is not part of this first pass.");
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

    private sealed record AiBehaviorVariantGroup(string Code, IReadOnlyList<string> States);
}
