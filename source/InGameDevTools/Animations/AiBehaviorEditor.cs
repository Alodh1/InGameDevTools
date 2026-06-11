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
    private readonly List<AiBehaviorEntry> _aiBehaviorEntries = [];
    private readonly List<AiBehaviorEntry> _visibleAiBehaviorEntries = [];
    private readonly Dictionary<string, AiBehaviorDraftState> _aiBehaviorDraftStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImGuiThreePanelLayoutState _aiBehaviorLayout = new(0.26f, 0.34f);
    private readonly DevToolsEditorDiagnostics _aiBehaviorDiagnostics = new("Entity AI");
    private readonly DevToolsAssetIndexer _aiBehaviorIndexer = new(batchSize: 90);
    private readonly DevToolsTextHistory _aiBehaviorTextHistory = new();
    private bool _aiBehaviorShowTextDiff;

    private bool _aiBehaviorIndexIncludedServerAssets;
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
    private int _aiBehaviorTypedTaskTypeIndex;
    private int _aiBehaviorTypedTaskIndex;
    private string _aiBehaviorNewTaskTypeKey = "";
    private string _aiBehaviorNewOtherParameterName = "";
    private string _aiBehaviorNewOtherParameterJson = "\"\"";
    private string[]? _aiBehaviorKnownTaskCodes;
    private readonly List<AiBehaviorLiveTaskInfo> _aiBehaviorLiveTasks = [];
    private readonly List<AiBehaviorLiveTransition> _aiBehaviorLiveTransitions = [];
    private readonly Dictionary<string, string> _aiBehaviorLiveActiveBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AiBehaviorLiveFieldSnapshot> _aiBehaviorLiveFieldSnapshots = [];
    private readonly List<AiBehaviorLiveRemovedTaskSnapshot> _aiBehaviorLiveRemovedTaskSnapshots = [];
    private readonly List<AiBehaviorLiveAddedTaskSnapshot> _aiBehaviorLiveAddedTaskSnapshots = [];
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
            _aiBehaviorIndexer.Fail();
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
        RestoreAiBehaviorLiveRemovedTaskSnapshots(updateStatus: false);
        RestoreAiBehaviorLiveAddedTaskSnapshots(updateStatus: false);
        _aiBehaviorLiveTasks.Clear();
        _aiBehaviorLiveTransitions.Clear();
        _aiBehaviorLiveActiveBySlot.Clear();
        _aiBehaviorLiveFieldSnapshots.Clear();
        _aiBehaviorLiveRemovedTaskSnapshots.Clear();
        _aiBehaviorLiveAddedTaskSnapshots.Clear();
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
        if (_aiBehaviorIndexer.IsReady &&
            _aiBehaviorEntries.Count == 0 &&
            !_aiBehaviorIndexIncludedServerAssets &&
            InGameDevToolsModSystem.ActiveServerApi != null)
        {
            StartAiBehaviorIndexing(clearLoaded: false);
        }

        _aiBehaviorIndexer.EnsureIndexed(() => StartAiBehaviorIndexing(clearLoaded: false), ProcessAiBehaviorIndexBatch);
    }

    private void StartAiBehaviorIndexing(bool clearLoaded)
    {
        _aiBehaviorIndexer.Begin();
        _aiBehaviorIndexIncludedServerAssets = false;
        _aiBehaviorEntries.Clear();
        _visibleAiBehaviorEntries.Clear();
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

        // Authored files first so the user's saved copies win the duplicate check.
        _aiBehaviorIndexer.AddSource("authored entity AI files", () => CollectToolAuthoredAssets("entity-ai"), IsAiBehaviorEntityAsset, _aiBehaviorDiagnostics);
        _aiBehaviorIndexer.AddSource("client entity category", () => _api.Assets.GetManyInCategory("entities", ""), IsAiBehaviorEntityAsset, _aiBehaviorDiagnostics);
        _aiBehaviorIndexer.AddSource("client loaded assets", () => _api.Assets.AllAssets.Values, IsAiBehaviorEntityAsset, _aiBehaviorDiagnostics);

        ICoreServerAPI? serverApi = InGameDevToolsModSystem.ActiveServerApi;
        if (serverApi != null)
        {
            _aiBehaviorIndexIncludedServerAssets = true;
            _aiBehaviorIndexer.AddSource("server entity category", () => serverApi.Assets.GetManyInCategory("entities", ""), IsAiBehaviorEntityAsset, _aiBehaviorDiagnostics);
            _aiBehaviorIndexer.AddSource("server loaded assets", () => serverApi.Assets.AllAssets.Values, IsAiBehaviorEntityAsset, _aiBehaviorDiagnostics);
        }

        _aiBehaviorIndexer.SortPendingByLocation();
        _aiBehaviorStatus = BuildAiBehaviorIndexProgressText();
    }

    private void ProcessAiBehaviorIndexBatch()
    {
        if (!_aiBehaviorIndexer.TryProcessBatch(
                IndexAiBehaviorAsset,
                CompleteAiBehaviorIndexing,
                () =>
                {
                    _aiBehaviorStatus = BuildAiBehaviorIndexProgressText();
                    RebuildVisibleAiBehaviorEntries();
                },
                out Exception? error))
        {
            _aiBehaviorStatus = $"Entity AI indexing failed: {error?.Message}";
            _aiBehaviorDiagnostics.Exception("Entity AI indexing failed", error!);
        }
    }

    private void CompleteAiBehaviorIndexing()
    {
        _aiBehaviorEntries.Sort((left, right) => string.Compare(left.SortKey, right.SortKey, StringComparison.OrdinalIgnoreCase));
        RebuildVisibleAiBehaviorEntries();
        _aiBehaviorStatus = $"Indexed {_aiBehaviorEntries.Count} entity AI source asset(s).";
        if (_visibleAiBehaviorEntries.Count > 0 && string.IsNullOrWhiteSpace(_aiBehaviorLoadedKey))
        {
            LoadAiBehaviorEntry(_visibleAiBehaviorEntries[Math.Clamp(_aiBehaviorEntryIndex, 0, _visibleAiBehaviorEntries.Count - 1)], keepDirty: true);
        }
    }

    private string BuildAiBehaviorIndexProgressText()
    {
        string serverSuffix = _aiBehaviorIndexIncludedServerAssets ? " including server assets" : " client assets only";
        return $"Indexing entity AI sources {_aiBehaviorIndexer.Position}/{_aiBehaviorIndexer.PendingAssets.Count}{serverSuffix}.";
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

        if (_aiBehaviorIndexer.IsIndexing)
        {
            ImGui.TextWrapped(_aiBehaviorStatus);
        }

        if (_visibleAiBehaviorEntries.Count == 0)
        {
            ImGui.TextWrapped(_aiBehaviorIndexer.IsReady ? "No entity AI sources match the current filters." : _aiBehaviorStatus);
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

        if (_aiBehaviorIndexer.IsIndexing)
        {
            ImGui.TextWrapped(_aiBehaviorStatus);
            ImGui.EndChild();
            return;
        }

        if (_aiBehaviorIndexer.IsFailed)
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
            if (tasksByType != null) ImGui.TextDisabled($"{behaviorPath}.aitasksByType: {CountAiBehaviorTasksByType(tasksByType)} typed task row(s)");

            if (ImGui.Button("Create base aitasks array##entity-ai-create-aitasks", new NVector2(-1, 0)))
            {
                behavior["aitasks"] = new JArray();
                SetAiBehaviorCurrentRoot(root);
            }

            if (tasksByType != null)
            {
                ImGui.Separator();
                DrawAiBehaviorTasksByTypeEditor(root, behavior, tasksByType);
            }

            DrawAiBehaviorRawJsonEditor();
            return;
        }

        ImGui.TextDisabled($"{behaviorPath}.aitasks: {tasks.Count} task(s)");

        DrawAiBehaviorTaskToolbar(root, tasks, ref _aiBehaviorTaskIndex, "base");
        DrawAiBehaviorTaskListEditor(root, tasks, ref _aiBehaviorTaskIndex, "base", "No base AI tasks configured.");

        ImGui.Separator();
        if (tasksByType != null)
        {
            DrawAiBehaviorTasksByTypeEditor(root, behavior, tasksByType);
        }
        else if (ImGui.Button("Create aitasksByType map##entity-ai-create-typed-tasks", new NVector2(-1, 0)))
        {
            behavior["aitasksByType"] = new JObject();
            SetAiBehaviorCurrentRoot(root);
        }

        if (ImGui.CollapsingHeader("Full entity JSON##entity-ai-full-json"))
        {
            DrawAiBehaviorRawJsonEditor();
        }
    }

    private void DrawAiBehaviorTaskListEditor(JObject root, JArray tasks, ref int taskIndex, string idSuffix, string emptyText)
    {
        ImGui.PushID($"entity-ai-task-list-editor-{idSuffix}");
        try
        {
            float listHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y * 0.24f, 120f, 280f);
            if (ImGui.BeginChild("##task-list", new NVector2(-float.Epsilon, listHeight), true))
            {
                taskIndex = Math.Clamp(taskIndex, 0, Math.Max(0, tasks.Count - 1));
                for (int index = 0; index < tasks.Count; index++)
                {
                    string label = GetAiBehaviorTaskLabel(tasks[index], index);
                    if (ImGui.Selectable($"{label}##task-{index}", index == taskIndex))
                    {
                        taskIndex = index;
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
                ImGui.TextWrapped(emptyText);
                return;
            }

            taskIndex = Math.Clamp(taskIndex, 0, tasks.Count - 1);
            if (tasks[taskIndex] is not JObject task)
            {
                ImGui.TextWrapped("Selected task is not an object. Use raw JSON to repair it.");
                return;
            }

            ImGui.Separator();
            ImGui.PushID($"selected-task-{idSuffix}-{taskIndex}");
            bool changed = DrawAiBehaviorTaskEditor(task);
            ImGui.PopID();
            if (changed)
            {
                SetAiBehaviorCurrentRoot(root);
            }

            if (ImGui.CollapsingHeader("Selected task JSON##task-json"))
            {
                string taskText = task.ToString(Formatting.Indented);
                if (ImGui.InputTextMultiline("##selected-task-json", ref taskText, 256 * 1024, new NVector2(-float.Epsilon, 220f), ImGuiInputTextFlags.AllowTabInput))
                {
                    try
                    {
                        JToken replacement = JToken.Parse(taskText);
                        tasks[taskIndex] = replacement;
                        SetAiBehaviorCurrentRoot(root);
                    }
                    catch (Exception exception)
                    {
                        _aiBehaviorTextValid = false;
                        _aiBehaviorValidationStatus = $"Selected task JSON parse error: {exception.Message}";
                    }
                }
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    private void DrawAiBehaviorTasksByTypeEditor(JObject root, JObject behavior, JObject tasksByType)
    {
        if (!ImGui.CollapsingHeader("Typed tasks (aitasksByType)##entity-ai-typed-tasks", ImGuiTreeNodeFlags.DefaultOpen)) return;

        behavior["aitasksByType"] = tasksByType;

        ImGui.InputTextWithHint("##entity-ai-new-typed-key", "type key, e.g. idle", ref _aiBehaviorNewTaskTypeKey, 120);
        ImGui.SameLine();
        if (ImGui.Button("Add type##entity-ai-add-typed-key"))
        {
            string key = string.IsNullOrWhiteSpace(_aiBehaviorNewTaskTypeKey) ? "default" : _aiBehaviorNewTaskTypeKey.Trim();
            if (tasksByType[key] == null)
            {
                tasksByType[key] = new JArray();
                _aiBehaviorTypedTaskTypeIndex = tasksByType.Properties().Select(property => property.Name).ToList().FindIndex(name => name.Equals(key, StringComparison.OrdinalIgnoreCase));
                _aiBehaviorTypedTaskIndex = 0;
                _aiBehaviorNewTaskTypeKey = "";
                SetAiBehaviorCurrentRoot(root);
            }
            else
            {
                _aiBehaviorStatus = $"aitasksByType already contains '{key}'.";
            }
        }

        List<JProperty> typeProperties = tasksByType.Properties()
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (typeProperties.Count == 0)
        {
            ImGui.TextWrapped("No typed task groups configured.");
            return;
        }

        _aiBehaviorTypedTaskTypeIndex = Math.Clamp(_aiBehaviorTypedTaskTypeIndex, 0, typeProperties.Count - 1);
        string[] labels = typeProperties
            .Select(property => $"{property.Name} ({(property.Value as JArray)?.Count.ToString(CultureInfo.InvariantCulture) ?? "non-array"})")
            .ToArray();
        ImGui.ListBox("Types##entity-ai-typed-types", ref _aiBehaviorTypedTaskTypeIndex, labels, labels.Length);

        JProperty selectedProperty = typeProperties[_aiBehaviorTypedTaskTypeIndex];
        string selectedKey = selectedProperty.Name;

        ImGui.TextDisabled($"aitasksByType.{selectedKey}");
        ImGui.SameLine();
        if (ImGui.Button("Remove type##entity-ai-remove-typed-key"))
        {
            selectedProperty.Remove();
            _aiBehaviorTypedTaskTypeIndex = Math.Clamp(_aiBehaviorTypedTaskTypeIndex, 0, Math.Max(0, typeProperties.Count - 2));
            _aiBehaviorTypedTaskIndex = 0;
            SetAiBehaviorCurrentRoot(root);
            return;
        }

        if (selectedProperty.Value is not JArray typedTasks)
        {
            ImGui.TextWrapped("Selected aitasksByType value is not an array. Convert it to an empty task array or edit the full JSON.");
            if (ImGui.Button("Convert to empty task array##entity-ai-convert-typed-array"))
            {
                selectedProperty.Value = new JArray();
                _aiBehaviorTypedTaskIndex = 0;
                SetAiBehaviorCurrentRoot(root);
            }
            return;
        }

        DrawAiBehaviorTaskToolbar(root, typedTasks, ref _aiBehaviorTypedTaskIndex, $"typed-{selectedKey}");
        DrawAiBehaviorTaskListEditor(root, typedTasks, ref _aiBehaviorTypedTaskIndex, $"typed-{selectedKey}", "No typed AI tasks configured for this type.");
    }

    private void DrawAiBehaviorTaskToolbar(JObject root, JArray tasks)
    {
        DrawAiBehaviorTaskToolbar(root, tasks, ref _aiBehaviorTaskIndex, "base");
    }

    private void DrawAiBehaviorTaskToolbar(JObject root, JArray tasks, ref int taskIndex, string idSuffix)
    {
        ImGui.PushID($"entity-ai-task-toolbar-{idSuffix}");
        IReadOnlyList<string> knownCodes = GetKnownAiTaskCodes();
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X * 0.36f));
        ImGui.InputText("New task code##new-task-code", ref _aiBehaviorNewTaskCode, 128);
        if (knownCodes.Count > 0)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X * 0.36f));
            _aiBehaviorKnownTaskCodeIndex = Math.Clamp(_aiBehaviorKnownTaskCodeIndex, 0, knownCodes.Count - 1);
            if (ImGui.Combo("Known##known-task-code", ref _aiBehaviorKnownTaskCodeIndex, knownCodes.ToArray(), knownCodes.Count))
            {
                _aiBehaviorNewTaskCode = knownCodes[_aiBehaviorKnownTaskCodeIndex];
            }
        }

        if (ImGui.Button("Add task##add-task"))
        {
            string code = string.IsNullOrWhiteSpace(_aiBehaviorNewTaskCode) ? "wander" : _aiBehaviorNewTaskCode.Trim();
            tasks.Add(new JObject { ["code"] = code });
            taskIndex = tasks.Count - 1;
            SetAiBehaviorCurrentRoot(root);
        }
        ImGui.SameLine();

        bool hasTask = tasks.Count > 0 && taskIndex >= 0 && taskIndex < tasks.Count;
        if (!hasTask) ImGui.BeginDisabled();
        if (ImGui.Button("Remove selected##remove-task"))
        {
            tasks.RemoveAt(taskIndex);
            taskIndex = Math.Clamp(taskIndex, 0, Math.Max(0, tasks.Count - 1));
            SetAiBehaviorCurrentRoot(root);
        }
        ImGui.SameLine();
        if (ImGui.Button("Duplicate##duplicate-task"))
        {
            tasks.Insert(taskIndex + 1, tasks[taskIndex].DeepClone());
            taskIndex++;
            SetAiBehaviorCurrentRoot(root);
        }
        ImGui.SameLine();
        if (taskIndex <= 0) ImGui.BeginDisabled();
        if (ImGui.Button("Up##task-up"))
        {
            JToken task = tasks[taskIndex];
            tasks.RemoveAt(taskIndex);
            taskIndex--;
            tasks.Insert(taskIndex, task);
            SetAiBehaviorCurrentRoot(root);
        }
        if (taskIndex <= 0) ImGui.EndDisabled();
        ImGui.SameLine();
        if (taskIndex >= tasks.Count - 1) ImGui.BeginDisabled();
        if (ImGui.Button("Down##task-down"))
        {
            JToken task = tasks[taskIndex];
            tasks.RemoveAt(taskIndex);
            taskIndex++;
            tasks.Insert(taskIndex, task);
            SetAiBehaviorCurrentRoot(root);
        }
        if (taskIndex >= tasks.Count - 1) ImGui.EndDisabled();
        if (!hasTask) ImGui.EndDisabled();
        ImGui.PopID();
    }

    private bool DrawAiBehaviorTaskEditor(JObject task)
    {
        bool changed = false;
        ImGui.SeparatorText(GetAiBehaviorTaskCode(task) ?? "AI task");
        changed |= DrawAiBehaviorStringProperty(task, "code", "Code", required: true);
        changed |= DrawAiBehaviorStringProperty(task, "id", "Id", required: false);

        ImGui.SeparatorText("Common parameters");
        foreach (AiBehaviorParameterSpec spec in AiBehaviorParameterSpecs.Where(spec => spec.Scope == AiBehaviorParameterScope.Common))
        {
            changed |= DrawAiBehaviorSourceParameter(task, spec);
        }

        ImGui.SeparatorText("Task-specific parameters");
        int taskSpecificDrawn = 0;
        foreach (AiBehaviorParameterSpec spec in AiBehaviorParameterSpecs.Where(spec => spec.Scope == AiBehaviorParameterScope.TaskSpecific))
        {
            if (DrawAiBehaviorSourceParameter(task, spec))
            {
                changed = true;
            }
            if (HasAiBehaviorSourceProperty(task, spec.SourcePropertyNames))
            {
                taskSpecificDrawn++;
            }
        }
        if (taskSpecificDrawn == 0)
        {
            ImGui.TextWrapped("No schema-backed task-specific parameters are set on this task yet.");
        }

        if (ImGui.CollapsingHeader("Other parameters##entity-ai-other-params"))
        {
            changed |= DrawAiBehaviorOtherParameters(task);
        }

        return changed;
    }

    private bool DrawAiBehaviorOtherParameters(JObject task)
    {
        bool changed = false;
        List<JProperty> otherProperties = task.Properties()
            .Where(property => !AiBehaviorFirstClassProperties.Contains(property.Name))
            .ToList();

        if (otherProperties.Count == 0)
        {
            ImGui.TextDisabled("No unhandled parameters on this task.");
        }

        foreach (JProperty property in otherProperties)
        {
            ImGui.PushID($"entity-ai-other-{property.Name}");
            changed |= DrawAiBehaviorGenericJsonProperty(task, property);
            ImGui.PopID();
        }

        ImGui.SeparatorText("Add parameter");
        ImGui.InputTextWithHint("##entity-ai-other-name", "parameter name", ref _aiBehaviorNewOtherParameterName, 128);
        ImGui.InputTextMultiline("##entity-ai-other-json", ref _aiBehaviorNewOtherParameterJson, 64 * 1024, new NVector2(-float.Epsilon, 82f), ImGuiInputTextFlags.AllowTabInput);
        if (ImGui.Button("Add parameter##entity-ai-other-add"))
        {
            string propertyName = _aiBehaviorNewOtherParameterName.Trim();
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                _aiBehaviorValidationStatus = "Other parameter name is empty.";
            }
            else if (AiBehaviorFirstClassProperties.Contains(propertyName))
            {
                _aiBehaviorValidationStatus = $"{propertyName} is already handled by a structured control.";
            }
            else if (task[propertyName] != null)
            {
                _aiBehaviorValidationStatus = $"{propertyName} already exists on this task.";
            }
            else if (!DevToolsJson.TryParseToken(_aiBehaviorNewOtherParameterJson, out JToken? token, out string error) || token == null)
            {
                _aiBehaviorValidationStatus = $"Other parameter JSON parse error: {error}";
            }
            else
            {
                task[propertyName] = token;
                _aiBehaviorNewOtherParameterName = "";
                _aiBehaviorNewOtherParameterJson = "\"\"";
                changed = true;
            }
        }

        return changed;
    }

    private bool DrawAiBehaviorGenericJsonProperty(JObject task, JProperty property)
    {
        bool changed = false;
        JToken value = property.Value;
        ImGui.TextUnformatted(property.Name);

        switch (value.Type)
        {
            case JTokenType.Boolean:
            {
                bool boolValue = value.Value<bool>();
                if (ImGui.Checkbox($"##bool-{property.Name}", ref boolValue))
                {
                    property.Value = boolValue;
                    changed = true;
                }
                break;
            }
            case JTokenType.Integer:
            {
                int intValue = value.Value<int>();
                ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 90f));
                if (ImGui.InputInt($"##int-{property.Name}", ref intValue))
                {
                    property.Value = intValue;
                    changed = true;
                }
                break;
            }
            case JTokenType.Float:
            {
                float floatValue = value.Value<float>();
                ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 90f));
                if (ImGui.InputFloat($"##float-{property.Name}", ref floatValue, 0, 0, "%.4f"))
                {
                    property.Value = floatValue;
                    changed = true;
                }
                break;
            }
            case JTokenType.String:
            {
                string stringValue = value.ToString();
                ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 90f));
                if (ImGui.InputText($"##string-{property.Name}", ref stringValue, 1024))
                {
                    property.Value = stringValue;
                    changed = true;
                }
                break;
            }
            default:
            {
                string json = value.ToString(Formatting.Indented);
                if (ImGui.InputTextMultiline($"##json-{property.Name}", ref json, 256 * 1024, new NVector2(-float.Epsilon, 90f), ImGuiInputTextFlags.AllowTabInput))
                {
                    if (DevToolsJson.TryParseToken(json, out JToken? token, out string error) && token != null)
                    {
                        property.Value = token;
                        changed = true;
                    }
                    else
                    {
                        _aiBehaviorValidationStatus = $"{property.Name} JSON parse error: {error}";
                    }
                }
                break;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button($"Remove##remove-{property.Name}"))
        {
            task.Remove(property.Name);
            changed = true;
        }

        return changed;
    }

    private bool DrawAiBehaviorSourceParameter(JObject task, AiBehaviorParameterSpec spec)
    {
        return spec.Kind switch
        {
            AiBehaviorParameterKind.Int => DrawAiBehaviorIntProperty(task, GetAiBehaviorSourcePropertyName(task, spec), spec.Label, (int)spec.Min, (int)spec.Max),
            AiBehaviorParameterKind.Float => DrawAiBehaviorFloatProperty(task, GetAiBehaviorSourcePropertyName(task, spec), spec.Label, spec.Min, spec.Max, spec.Format),
            AiBehaviorParameterKind.Bool => DrawAiBehaviorBoolProperty(task, GetAiBehaviorSourcePropertyName(task, spec), spec.Label),
            AiBehaviorParameterKind.Range => DrawAiBehaviorRangeProperty(task, GetAiBehaviorSourcePropertyName(task, spec), spec.Label, spec.Min, spec.Max, spec.Format),
            _ => false
        };
    }

    private static string GetAiBehaviorSourcePropertyName(JObject task, AiBehaviorParameterSpec spec)
    {
        foreach (string propertyName in spec.SourcePropertyNames)
        {
            if (task[propertyName] != null) return propertyName;
        }

        return spec.SourcePropertyNames[0];
    }

    private static bool HasAiBehaviorSourceProperty(JObject task, IReadOnlyList<string> propertyNames)
    {
        return propertyNames.Any(propertyName => task[propertyName] != null);
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

    private bool DrawAiBehaviorBoolProperty(JObject task, string propertyName, string label)
    {
        bool exists = task[propertyName] != null;
        if (!exists)
        {
            if (ImGui.Button($"Add {label}##entity-ai-add-{propertyName}"))
            {
                task[propertyName] = true;
                return true;
            }
            return false;
        }

        bool value = task[propertyName]?.Type switch
        {
            JTokenType.Boolean => task[propertyName]!.Value<bool>(),
            JTokenType.Integer => task[propertyName]!.Value<int>() != 0,
            JTokenType.Float => Math.Abs(task[propertyName]!.Value<double>()) > double.Epsilon,
            _ => bool.TryParse(task[propertyName]?.ToString(), out bool parsed) && parsed
        };

        if (ImGui.Checkbox($"{label}##entity-ai-{propertyName}", ref value))
        {
            task[propertyName] = value;
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

    private bool DrawAiBehaviorRangeProperty(JObject task, string propertyName, string label, float min, float max, string format)
    {
        bool exists = task[propertyName] != null;
        if (!exists)
        {
            if (ImGui.Button($"Add {label}##entity-ai-add-{propertyName}"))
            {
                task[propertyName] = new JArray(min, max);
                return true;
            }
            return false;
        }

        NVector2 range = TryReadJsonRange(task[propertyName], out NVector2 parsed)
            ? parsed
            : new NVector2(min, max);
        range.X = Math.Clamp(range.X, min, max);
        range.Y = Math.Clamp(range.Y, min, max);

        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 90f));
        if (ImGui.SliderFloat2($"{label}##entity-ai-{propertyName}", ref range, min, max, format))
        {
            task[propertyName] = new JArray(Math.Clamp(range.X, min, max), Math.Clamp(range.Y, min, max));
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

    private static bool TryReadJsonRange(JToken? token, out NVector2 range)
    {
        range = default;
        if (token is JArray array && array.Count >= 2)
        {
            bool hasFirst = TryReadJsonFloat(array[0], out float first);
            bool hasSecond = TryReadJsonFloat(array[1], out float second);
            if (hasFirst && hasSecond)
            {
                range = new NVector2(first, second);
                return true;
            }
        }

        if (token is JObject obj)
        {
            JToken? minToken = obj["min"] ?? obj["Min"] ?? obj["x"] ?? obj["X"] ?? obj["from"];
            JToken? maxToken = obj["max"] ?? obj["Max"] ?? obj["y"] ?? obj["Y"] ?? obj["to"];
            if (TryReadJsonFloat(minToken, out float minValue) &&
                TryReadJsonFloat(maxToken, out float maxValue))
            {
                range = new NVector2(minValue, maxValue);
                return true;
            }
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

        ImGui.SeparatorText("Source scope");
        ImGui.TextWrapped("Source edits change authored entity JSON. They affect future spawns after the saved file is loaded.");
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
        ImGui.TextWrapped("Live tuning affects only the looked-at running entity in this singleplayer session. It is temporary and reverts when you leave or close this editor.");

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

        bool hasLiveEdits = HasAiBehaviorLiveEditsForCurrentTarget();
        if (!hasLiveEdits) ImGui.BeginDisabled();
        if (ImGui.Button("Revert live AI edits##entity-ai-live-revert-fields", new NVector2(-1, 0)))
        {
            RestoreAiBehaviorLiveFieldSnapshots(updateStatus: true);
            RestoreAiBehaviorLiveRemovedTaskSnapshots(updateStatus: true);
            RestoreAiBehaviorLiveAddedTaskSnapshots(updateStatus: true);
            RefreshAiBehaviorLiveSnapshot(recordTransitions: false);
        }
        if (!hasLiveEdits) ImGui.EndDisabled();

        if (hasLiveTarget)
        {
            ImGui.TextWrapped($"Live target: {_aiBehaviorLiveEntityCode} #{_aiBehaviorLiveEntityId}");
        }
        ImGui.TextWrapped(_aiBehaviorLiveServerStatus);
        ImGui.TextWrapped(_aiBehaviorLiveStatus);

        if (hasLiveTarget)
        {
            DrawAiBehaviorLiveSourceActions();
            DrawAiBehaviorLiveEmotionPanel();
        }

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

    private void DrawAiBehaviorLiveSourceActions()
    {
        ImGui.SeparatorText("Live source actions");
        bool hasSourceTask = TryGetSelectedAiBehaviorSourceTask(out JObject? sourceTask, out string sourceTaskStatus);
        if (!hasSourceTask) ImGui.BeginDisabled();
        if (ImGui.Button("Add selected source task live##entity-ai-live-add-source-task", new NVector2(-1, 0)))
        {
            AddSelectedAiBehaviorSourceTaskLive(sourceTask!);
        }
        if (!hasSourceTask) ImGui.EndDisabled();
        if (!hasSourceTask)
        {
            ImGui.TextWrapped(sourceTaskStatus);
        }
    }

    private void DrawAiBehaviorLiveEmotionPanel()
    {
        bool taskUsesEmotionGates = _aiBehaviorLiveTasks.Any(TaskHasAiBehaviorEmotionGate);
        if (!TryGetCurrentAiBehaviorServerEntity(out Entity? serverEntity, out _) || serverEntity == null)
        {
            return;
        }

        if (!TryGetAiBehaviorEmotionStatesBehavior(serverEntity, out object? behavior, out string source) || behavior == null)
        {
            if (taskUsesEmotionGates)
            {
                ImGui.SeparatorText("Emotion states");
                ImGui.TextWrapped("This entity has AI tasks with emotion gates, but no live emotionstates behavior was found.");
            }
            return;
        }

        List<AiBehaviorEmotionStateInfo> availableStates = BuildAiBehaviorAvailableEmotionStates(behavior);
        List<AiBehaviorActiveEmotionStateInfo> activeStates = BuildAiBehaviorActiveEmotionStates(behavior);
        if (availableStates.Count == 0 && activeStates.Count == 0 && !taskUsesEmotionGates)
        {
            return;
        }

        ImGui.SeparatorText("Emotion states");
        ImGui.TextWrapped($"{activeStates.Count} active / {availableStates.Count} available from {source}.");

        if (activeStates.Count > 0)
        {
            if (ImGui.BeginChild("##entity-ai-live-active-emotions", new NVector2(-float.Epsilon, Math.Clamp(activeStates.Count * 24f + 18f, 58f, 142f)), true))
            {
                foreach (AiBehaviorActiveEmotionStateInfo state in activeStates.OrderBy(state => state.Code, StringComparer.OrdinalIgnoreCase))
                {
                    ImGui.TextWrapped($"{state.Code}: {state.Duration}s remaining, source {state.SourceEntityId}");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip($"state id: {state.StateId}");
                    }
                }
            }
            ImGui.EndChild();
        }
        else
        {
            ImGui.TextWrapped("No active emotion states.");
        }

        if (availableStates.Count > 0 && ImGui.TreeNode("Available emotion states##entity-ai-live-available-emotions"))
        {
            foreach (AiBehaviorEmotionStateInfo state in availableStates.OrderBy(state => state.Code, StringComparer.OrdinalIgnoreCase))
            {
                ImGui.TextWrapped($"{state.Code}: duration {state.Duration}s, chance {state.Chance}, slot {state.Slot}, priority {state.Priority}");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"stress: {state.Stress}");
                }
            }
            ImGui.TreePop();
        }
    }

    private void DrawAiBehaviorLiveEditPanel()
    {
        if (_aiBehaviorLiveTasks.Count == 0) return;

        _aiBehaviorLiveTaskEditIndex = Math.Clamp(_aiBehaviorLiveTaskEditIndex, 0, _aiBehaviorLiveTasks.Count - 1);
        AiBehaviorLiveTaskInfo task = _aiBehaviorLiveTasks[_aiBehaviorLiveTaskEditIndex];
        ImGui.SeparatorText("Live edit selected task");
        ImGui.TextWrapped($"{task.Code} on {_aiBehaviorLiveEntityCode} #{_aiBehaviorLiveEntityId}");
        ImGui.TextWrapped("Live edits affect this one running entity instance. Use authored source save for future spawns.");

        ImGui.SeparatorText("Live actions");
        if (ImGui.Button("Force execute selected task##entity-ai-live-force-execute", new NVector2(-1, 0)))
        {
            ExecuteAiBehaviorLiveTaskAction(task, "ExecuteTask", "force executed");
        }

        if (ImGui.Button("Stop selected task##entity-ai-live-stop-task", new NVector2(-1, 0)))
        {
            ExecuteAiBehaviorLiveTaskAction(task, "StopTask", "stopped");
        }

        if (ImGui.Button("Remove selected task##entity-ai-live-remove-task", new NVector2(-1, 0)))
        {
            RemoveAiBehaviorLiveTask(task);
        }

        bool changed = false;
        int drawn = 0;

        ImGui.SeparatorText("Common");
        foreach (AiBehaviorParameterSpec spec in AiBehaviorParameterSpecs.Where(spec => spec.Scope == AiBehaviorParameterScope.Common))
        {
            drawn += DrawAiBehaviorLiveParameter(task.TaskObject, spec) ? 1 : 0;
        }

        ImGui.SeparatorText("Per-task numeric fields");
        int taskSpecificDrawn = 0;
        foreach (AiBehaviorParameterSpec spec in AiBehaviorParameterSpecs.Where(spec => spec.Scope == AiBehaviorParameterScope.TaskSpecific))
        {
            if (DrawAiBehaviorLiveParameter(task.TaskObject, spec))
            {
                taskSpecificDrawn++;
            }
        }

        int configDrawn = DrawAiBehaviorLiveConfigFields(task.TaskObject);

        changed = HasAiBehaviorLiveEditsForCurrentTarget();
        if (drawn == 0 && taskSpecificDrawn == 0 && configDrawn == 0)
        {
            ImGui.TextWrapped("No writable common live fields were found on this task.");
        }

        if (changed)
        {
            int fieldCount = _aiBehaviorLiveFieldSnapshots.Count(snapshot => snapshot.EntityId == _aiBehaviorLiveEntityId);
            int removedCount = _aiBehaviorLiveRemovedTaskSnapshots.Count(snapshot => snapshot.EntityId == _aiBehaviorLiveEntityId);
            int addedCount = _aiBehaviorLiveAddedTaskSnapshots.Count(snapshot => snapshot.EntityId == _aiBehaviorLiveEntityId);
            ImGui.TextWrapped($"{fieldCount} live field edit snapshot(s), {removedCount} removed task snapshot(s), {addedCount} added task snapshot(s) are available for revert.");
        }
    }

    private int DrawAiBehaviorLiveConfigFields(object task)
    {
        List<AiBehaviorLiveConfigObject> configs = FindAiBehaviorLiveConfigObjects(task).ToList();
        if (configs.Count == 0) return 0;

        ImGui.SeparatorText("Config object fields");
        int drawn = 0;
        foreach (AiBehaviorLiveConfigObject config in configs.Take(4))
        {
            ImGui.TextWrapped($"{config.Name}: {config.Value.GetType().Name}");
            int configDrawn = 0;
            foreach (AiBehaviorLiveMember member in EnumerateAiBehaviorLiveEditableMembers(config.Value).Take(80))
            {
                if (DrawAiBehaviorLiveMemberControl(config.Value, member))
                {
                    drawn++;
                    configDrawn++;
                }
            }

            if (configDrawn == 0)
            {
                ImGui.TextWrapped("No writable primitive config fields found.");
            }
        }

        return drawn;
    }

    private bool DrawAiBehaviorLiveParameter(object task, AiBehaviorParameterSpec spec)
    {
        return spec.Kind switch
        {
            AiBehaviorParameterKind.Int => DrawAiBehaviorLiveIntField(task, spec.Label, (int)spec.Min, (int)spec.Max, spec.LiveMemberNames),
            AiBehaviorParameterKind.Float => DrawAiBehaviorLiveFloatField(task, spec.Label, spec.Min, spec.Max, spec.Format, spec.LiveMemberNames),
            AiBehaviorParameterKind.Bool => DrawAiBehaviorLiveBoolField(task, spec.Label, spec.LiveMemberNames),
            AiBehaviorParameterKind.Range => DrawAiBehaviorLiveRangeField(task, spec.Label, spec.Min, spec.Max, spec.Format, spec.LiveMemberNames),
            _ => false
        };
    }

    private bool DrawAiBehaviorLiveMemberControl(object target, AiBehaviorLiveMember member)
    {
        if (!member.CanWrite || member.Value == null) return false;

        string id = $"##entity-ai-live-config-{RuntimeHelpers.GetHashCode(target)}-{member.Name}";
        if (member.Value is bool boolValue)
        {
            if (ImGui.Checkbox($"{member.Name}{id}", ref boolValue))
            {
                TrySetAiBehaviorLiveMember(target, member, boolValue);
                RefreshAiBehaviorLiveSnapshot(recordTransitions: false);
            }
            return true;
        }

        if (member.Value is string textValue)
        {
            string edited = textValue;
            if (ImGui.InputText($"{member.Name}{id}", ref edited, 1024))
            {
                TrySetAiBehaviorLiveMember(target, member, edited);
                RefreshAiBehaviorLiveSnapshot(recordTransitions: false);
            }
            return true;
        }

        if (TryExtractAiBehaviorRange(member.Value, out NVector2 range))
        {
            (float min, float max) = InferAiBehaviorLiveRangeBounds(member.Name);
            if (ImGui.SliderFloat2($"{member.Name}{id}", ref range, min, max, "%.3f"))
            {
                if (TryCreateAiBehaviorRangeValue(member.ValueType, range, out object? newValue))
                {
                    TrySetAiBehaviorLiveMember(target, member, newValue);
                    RefreshAiBehaviorLiveSnapshot(recordTransitions: false);
                }
            }
            return true;
        }

        if (TryConvertAiBehaviorNumber(member.Value, out double number))
        {
            if (IsAiBehaviorIntegerType(member.ValueType))
            {
                int edited = Math.Clamp((int)Math.Round(number), -1_000_000, 1_000_000);
                if (ImGui.InputInt($"{member.Name}{id}", ref edited))
                {
                    TrySetAiBehaviorLiveMember(target, member, edited);
                    RefreshAiBehaviorLiveSnapshot(recordTransitions: false);
                }
                return true;
            }

            float floatValue = (float)number;
            if (ImGui.InputFloat($"{member.Name}{id}", ref floatValue, 0.01f, 0.1f, "%.3f"))
            {
                TrySetAiBehaviorLiveMember(target, member, floatValue);
                RefreshAiBehaviorLiveSnapshot(recordTransitions: false);
            }
            return true;
        }

        return false;
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
        _aiBehaviorTextHistory.Record(_aiBehaviorCurrentText, ImGui.GetTime());
        if (DevToolsJsonTextTools.DrawEditToolbar("entity-ai-json-tools", ref _aiBehaviorCurrentText, _aiBehaviorTextHistory, out string toolStatus))
        {
            ValidateAiBehaviorCurrentText();
            RememberAiBehaviorDraft();
        }
        if (!string.IsNullOrEmpty(toolStatus))
        {
            _aiBehaviorStatus = toolStatus;
        }

        ImGui.SameLine();
        ImGui.Checkbox("Diff##entity-ai-json-diff-toggle", ref _aiBehaviorShowTextDiff);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Show the draft's line diff against the loaded source (both sides normalized when parseable).");
        }

        if (_aiBehaviorShowTextDiff)
        {
            DevToolsTextDiffView.Draw(
                "entity-ai-json",
                _aiBehaviorOriginalText,
                _aiBehaviorCurrentText,
                Math.Clamp(ImGui.GetContentRegionAvail().Y * 0.45f, 120f, 320f));
        }

        int textCapacity = Math.Max(_aiBehaviorCurrentText.Length + 8192, 2 * 1024 * 1024);
        if (ImGui.InputTextMultiline("##entity-ai-json-text", ref _aiBehaviorCurrentText, (uint)textCapacity, new NVector2(-float.Epsilon, Math.Max(180f, ImGui.GetContentRegionAvail().Y - 24f)), ImGuiInputTextFlags.AllowTabInput))
        {
            _aiBehaviorTextHistory.Record(_aiBehaviorCurrentText, ImGui.GetTime());
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
        _aiBehaviorTextHistory.Reset(_aiBehaviorCurrentText);
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
        IReadOnlyList<string> knownCodes = GetKnownAiTaskCodes();
        HashSet<string> known = knownCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tasks == null)
        {
            warnings.Add("base aitasks array missing");
        }
        else
        {
            ValidateAiBehaviorTaskArray(tasks, "task", known, warnings);
        }

        if (tasksByType != null)
        {
            foreach (JProperty property in tasksByType.Properties())
            {
                if (property.Value is JArray typedTasks)
                {
                    ValidateAiBehaviorTaskArray(typedTasks, $"aitasksByType.{property.Name} task", known, warnings);
                }
                else
                {
                    warnings.Add($"aitasksByType.{property.Name} is not an array");
                }
            }
        }

        _aiBehaviorTextValid = warnings.All(warning => !warning.Contains("not an object", StringComparison.OrdinalIgnoreCase) && !warning.Contains("has no code", StringComparison.OrdinalIgnoreCase));
        _aiBehaviorValidationStatus = warnings.Count == 0
            ? "Valid entity AI JSON."
            : $"{warnings.Count} warning(s): {string.Join("; ", warnings.Take(5))}{(warnings.Count > 5 ? $"; ...and {warnings.Count - 5} more" : "")}";
    }

    private static void ValidateAiBehaviorTaskArray(JArray tasks, string labelPrefix, IReadOnlySet<string> knownTaskCodes, List<string> warnings)
    {
        for (int index = 0; index < tasks.Count; index++)
        {
            if (tasks[index] is not JObject task)
            {
                warnings.Add($"{labelPrefix} {index} is not an object");
                continue;
            }

            string? code = GetAiBehaviorTaskCode(task);
            if (string.IsNullOrWhiteSpace(code))
            {
                warnings.Add($"{labelPrefix} {index} has no code");
            }
            else if (knownTaskCodes.Count > 0 && !knownTaskCodes.Contains(code))
            {
                warnings.Add($"{labelPrefix} {index} code '{code}' is not registered in AiTaskRegistry");
            }
        }
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
            RestoreAiBehaviorLiveRemovedTaskSnapshots(updateStatus: false);
            RestoreAiBehaviorLiveAddedTaskSnapshots(updateStatus: false);
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

    private bool HasAiBehaviorLiveEditsForCurrentTarget()
    {
        return _aiBehaviorLiveEntityId != 0 &&
            (_aiBehaviorLiveFieldSnapshots.Any(snapshot => snapshot.EntityId == _aiBehaviorLiveEntityId) ||
             _aiBehaviorLiveRemovedTaskSnapshots.Any(snapshot => snapshot.EntityId == _aiBehaviorLiveEntityId) ||
             _aiBehaviorLiveAddedTaskSnapshots.Any(snapshot => snapshot.EntityId == _aiBehaviorLiveEntityId));
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

    private void ExecuteAiBehaviorLiveTaskAction(AiBehaviorLiveTaskInfo task, string methodName, string actionText)
    {
        if (!TryGetCurrentAiBehaviorLiveTaskManager(out object? taskManager, out string status) || taskManager == null)
        {
            _aiBehaviorLiveStatus = status;
            return;
        }

        if (!TryInvokeAiBehaviorTaskManagerMethod(taskManager, methodName, task, out string invokeStatus))
        {
            _aiBehaviorLiveStatus = invokeStatus;
            _aiBehaviorDiagnostics.Warning(invokeStatus);
            return;
        }

        AddAiBehaviorLiveTransition($"manual: {actionText} {task.Code}");
        _aiBehaviorLiveStatus = $"Live action: {actionText} {task.Code}.";
        RefreshAiBehaviorLiveSnapshot(recordTransitions: true);
    }

    private void RemoveAiBehaviorLiveTask(AiBehaviorLiveTaskInfo task)
    {
        if (!TryGetCurrentAiBehaviorLiveTaskManager(out object? taskManager, out string status) || taskManager == null)
        {
            _aiBehaviorLiveStatus = status;
            return;
        }

        if (!TryInvokeAiBehaviorTaskManagerMethod(taskManager, "RemoveTask", task, out string removeStatus))
        {
            _aiBehaviorLiveStatus = removeStatus;
            _aiBehaviorDiagnostics.Warning(removeStatus);
            return;
        }

        if (!ForgetAiBehaviorLiveAddedTaskSnapshot(task))
        {
            CaptureAiBehaviorLiveRemovedTaskSnapshot(taskManager, task);
        }
        AddAiBehaviorLiveTransition($"manual: removed {task.Code}");
        _aiBehaviorLiveStatus = $"Live action: removed {task.Code}; use Revert live AI edits to re-add it.";
        RefreshAiBehaviorLiveSnapshot(recordTransitions: true);
    }

    private void AddSelectedAiBehaviorSourceTaskLive(JObject sourceTask)
    {
        string? code = GetAiBehaviorTaskCode(sourceTask);
        if (string.IsNullOrWhiteSpace(code))
        {
            _aiBehaviorLiveStatus = "Selected source task has no task code.";
            return;
        }

        if (!TryGetCurrentAiBehaviorLiveTaskManager(out object? taskManager, out string managerStatus) || taskManager == null)
        {
            _aiBehaviorLiveStatus = managerStatus;
            return;
        }

        if (!TryGetCurrentAiBehaviorServerEntity(out Entity? serverEntity, out string entityStatus) || serverEntity == null)
        {
            _aiBehaviorLiveStatus = entityStatus;
            return;
        }

        if (!TryGetAiBehaviorTaskType(code, out Type? taskType, out string taskTypeStatus) || taskType == null)
        {
            _aiBehaviorLiveStatus = taskTypeStatus;
            _aiBehaviorDiagnostics.Warning(taskTypeStatus);
            return;
        }

        if (!TryCreateAiBehaviorLiveTask(taskType, serverEntity, sourceTask, out object? taskObject, out string createStatus) || taskObject == null)
        {
            _aiBehaviorLiveStatus = createStatus;
            _aiBehaviorDiagnostics.Warning(createStatus);
            return;
        }

        if (!TryInvokeAiBehaviorTaskManagerMethod(taskManager, "AddTask", taskObject, code, out string addStatus))
        {
            _aiBehaviorLiveStatus = addStatus;
            _aiBehaviorDiagnostics.Warning(addStatus);
            return;
        }

        CaptureAiBehaviorLiveAddedTaskSnapshot(taskManager, taskObject, code);
        AddAiBehaviorLiveTransition($"manual: added {code}");
        _aiBehaviorLiveStatus = $"Live action: added {code}; use Revert live AI edits to remove it.";
        RefreshAiBehaviorLiveSnapshot(recordTransitions: true);
    }

    private void CaptureAiBehaviorLiveRemovedTaskSnapshot(object taskManager, AiBehaviorLiveTaskInfo task)
    {
        int taskRuntimeId = RuntimeHelpers.GetHashCode(task.TaskObject);
        bool exists = _aiBehaviorLiveRemovedTaskSnapshots.Any(snapshot =>
            snapshot.EntityId == _aiBehaviorLiveEntityId &&
            snapshot.TaskRuntimeId == taskRuntimeId);
        if (exists) return;

        _aiBehaviorLiveRemovedTaskSnapshots.Add(new AiBehaviorLiveRemovedTaskSnapshot(
            _aiBehaviorLiveEntityId,
            taskRuntimeId,
            new WeakReference<object>(taskManager),
            task.TaskObject,
            task.Code));
    }

    private void CaptureAiBehaviorLiveAddedTaskSnapshot(object taskManager, object taskObject, string code)
    {
        int taskRuntimeId = RuntimeHelpers.GetHashCode(taskObject);
        bool exists = _aiBehaviorLiveAddedTaskSnapshots.Any(snapshot =>
            snapshot.EntityId == _aiBehaviorLiveEntityId &&
            snapshot.TaskRuntimeId == taskRuntimeId);
        if (exists) return;

        _aiBehaviorLiveAddedTaskSnapshots.Add(new AiBehaviorLiveAddedTaskSnapshot(
            _aiBehaviorLiveEntityId,
            taskRuntimeId,
            new WeakReference<object>(taskManager),
            taskObject,
            code));
    }

    private bool ForgetAiBehaviorLiveAddedTaskSnapshot(AiBehaviorLiveTaskInfo task)
    {
        int taskRuntimeId = RuntimeHelpers.GetHashCode(task.TaskObject);
        int index = _aiBehaviorLiveAddedTaskSnapshots.FindIndex(snapshot =>
            snapshot.EntityId == _aiBehaviorLiveEntityId &&
            snapshot.TaskRuntimeId == taskRuntimeId);
        if (index < 0) return false;

        _aiBehaviorLiveAddedTaskSnapshots.RemoveAt(index);
        return true;
    }

    private void RestoreAiBehaviorLiveRemovedTaskSnapshots(bool updateStatus)
    {
        long targetEntityId = _aiBehaviorLiveEntityId;
        bool restoreAll = targetEntityId == 0;
        int restored = 0;
        int skipped = 0;

        for (int index = _aiBehaviorLiveRemovedTaskSnapshots.Count - 1; index >= 0; index--)
        {
            AiBehaviorLiveRemovedTaskSnapshot snapshot = _aiBehaviorLiveRemovedTaskSnapshots[index];
            if (!restoreAll && snapshot.EntityId != targetEntityId) continue;

            object? taskManager = null;
            if (!snapshot.TaskManager.TryGetTarget(out taskManager) && !TryGetCurrentAiBehaviorLiveTaskManager(out taskManager, out _))
            {
                taskManager = null;
            }

            if (taskManager != null && TryInvokeAiBehaviorTaskManagerMethod(taskManager, "AddTask", snapshot.TaskObject, snapshot.Code, out _))
            {
                restored++;
            }
            else
            {
                skipped++;
            }

            _aiBehaviorLiveRemovedTaskSnapshots.RemoveAt(index);
        }

        if (updateStatus && (restored > 0 || skipped > 0))
        {
            _aiBehaviorLiveStatus = skipped == 0
                ? $"Re-added {restored} removed live AI task(s)."
                : $"Re-added {restored} removed live AI task(s); skipped {skipped} stale task reference(s).";
        }
    }

    private void RestoreAiBehaviorLiveAddedTaskSnapshots(bool updateStatus)
    {
        long targetEntityId = _aiBehaviorLiveEntityId;
        bool restoreAll = targetEntityId == 0;
        int removed = 0;
        int skipped = 0;

        for (int index = _aiBehaviorLiveAddedTaskSnapshots.Count - 1; index >= 0; index--)
        {
            AiBehaviorLiveAddedTaskSnapshot snapshot = _aiBehaviorLiveAddedTaskSnapshots[index];
            if (!restoreAll && snapshot.EntityId != targetEntityId) continue;

            object? taskManager = null;
            if (!snapshot.TaskManager.TryGetTarget(out taskManager) && !TryGetCurrentAiBehaviorLiveTaskManager(out taskManager, out _))
            {
                taskManager = null;
            }

            if (taskManager != null && TryInvokeAiBehaviorTaskManagerMethod(taskManager, "RemoveTask", snapshot.TaskObject, snapshot.Code, out _))
            {
                removed++;
            }
            else
            {
                skipped++;
            }

            _aiBehaviorLiveAddedTaskSnapshots.RemoveAt(index);
        }

        if (updateStatus && (removed > 0 || skipped > 0))
        {
            _aiBehaviorLiveStatus = skipped == 0
                ? $"Removed {removed} added live AI task(s)."
                : $"Removed {removed} added live AI task(s); skipped {skipped} stale task reference(s).";
        }
    }

    private bool TryGetCurrentAiBehaviorLiveTaskManager(out object? taskManager, out string status)
    {
        taskManager = null;
        ICoreServerAPI? serverApi = InGameDevToolsModSystem.ActiveServerApi;
        if (serverApi == null)
        {
            status = "Live AI actions require an integrated singleplayer server.";
            return false;
        }

        if (_aiBehaviorLiveEntityId == 0)
        {
            status = "No live entity target selected.";
            return false;
        }

        Entity? serverEntity = serverApi.World.GetEntityById(_aiBehaviorLiveEntityId);
        if (serverEntity == null)
        {
            status = $"Server entity #{_aiBehaviorLiveEntityId} was not found. Look at the entity again and refresh.";
            return false;
        }

        if (!TryGetAiTaskManager(serverEntity, out taskManager, out string managerSource) || taskManager == null)
        {
            status = $"No live task manager found on {_aiBehaviorLiveEntityCode} #{_aiBehaviorLiveEntityId}.";
            return false;
        }

        status = managerSource;
        return true;
    }

    private bool TryGetCurrentAiBehaviorServerEntity(out Entity? serverEntity, out string status)
    {
        serverEntity = null;
        ICoreServerAPI? serverApi = InGameDevToolsModSystem.ActiveServerApi;
        if (serverApi == null)
        {
            status = "Live AI actions require an integrated singleplayer server.";
            return false;
        }

        if (_aiBehaviorLiveEntityId == 0)
        {
            status = "No live entity target selected.";
            return false;
        }

        serverEntity = serverApi.World.GetEntityById(_aiBehaviorLiveEntityId);
        if (serverEntity == null)
        {
            status = $"Server entity #{_aiBehaviorLiveEntityId} was not found. Look at the entity again and refresh.";
            return false;
        }

        status = "";
        return true;
    }

    private bool TryGetSelectedAiBehaviorSourceTask(out JObject? task, out string status)
    {
        task = null;
        if (!TryParseJsonObjectDetailed(_aiBehaviorCurrentText, out JObject? root, out string error) || root == null)
        {
            status = $"Selected source JSON is invalid: {error}";
            return false;
        }

        if (!TryFindAiTaskBehavior(root, out _, out JArray? tasks, out _, out _) || tasks == null || tasks.Count == 0)
        {
            status = "No source task is selected.";
            return false;
        }

        int index = Math.Clamp(_aiBehaviorTaskIndex, 0, tasks.Count - 1);
        if (tasks[index] is not JObject selectedTask)
        {
            status = "Selected source task is not a JSON object.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(GetAiBehaviorTaskCode(selectedTask)))
        {
            status = "Selected source task has no task code.";
            return false;
        }

        task = selectedTask;
        status = "";
        return true;
    }

    private bool TryGetAiBehaviorTaskType(string code, out Type? taskType, out string status)
    {
        taskType = null;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? registry = assembly.GetType("Vintagestory.GameContent.AiTaskRegistry", throwOnError: false);
            object? taskTypes = registry?.GetField("TaskTypes", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (taskTypes is not IDictionary dictionary) continue;

            foreach (DictionaryEntry entry in dictionary)
            {
                string? key = entry.Key?.ToString();
                if (!string.Equals(key, code, StringComparison.OrdinalIgnoreCase)) continue;

                taskType = entry.Value as Type;
                if (taskType == null)
                {
                    status = $"AiTaskRegistry entry for '{code}' is not a task type.";
                    return false;
                }

                status = "";
                return true;
            }
        }

        status = $"AI task code '{code}' was not found in AiTaskRegistry.TaskTypes.";
        return false;
    }

    private bool TryCreateAiBehaviorLiveTask(Type taskType, Entity serverEntity, JObject sourceTask, out object? taskObject, out string status)
    {
        taskObject = null;

        if (!TryBuildAiBehaviorLiveTaskJson(sourceTask, out JsonObject? taskConfig, out JsonObject? aiConfig, out string jsonStatus) ||
            taskConfig == null ||
            aiConfig == null)
        {
            status = jsonStatus;
            return false;
        }

        string code = GetAiBehaviorTaskCode(sourceTask) ?? taskType.Name;
        string lastError = "";
        ConstructorInfo[] constructors = taskType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(constructor => constructor.GetParameters().Length)
            .ToArray();

        foreach (ConstructorInfo constructor in constructors)
        {
            if (!TryBuildAiBehaviorLiveTaskConstructorArgs(constructor, serverEntity, taskConfig, aiConfig, sourceTask, out object?[] args))
            {
                continue;
            }

            try
            {
                taskObject = constructor.Invoke(args);
                if (!TryRunAiBehaviorLiveTaskAfterInitialize(taskObject, out string initStatus))
                {
                    taskObject = null;
                    status = initStatus;
                    return false;
                }

                status = "";
                return true;
            }
            catch (TargetInvocationException exception)
            {
                lastError = exception.InnerException?.Message ?? exception.Message;
            }
            catch (Exception exception)
            {
                lastError = exception.Message;
            }
        }

        status = string.IsNullOrWhiteSpace(lastError)
            ? $"No compatible constructor found for AI task '{code}' ({taskType.FullName})."
            : $"Could not construct AI task '{code}' ({taskType.FullName}): {lastError}";
        return false;
    }

    private bool TryBuildAiBehaviorLiveTaskJson(JObject sourceTask, out JsonObject? taskConfig, out JsonObject? aiConfig, out string status)
    {
        taskConfig = null;
        aiConfig = null;

        try
        {
            taskConfig = JsonObject.FromJson(sourceTask.ToString(Formatting.None));

            JObject behaviorClone;
            if (TryParseJsonObjectDetailed(_aiBehaviorCurrentText, out JObject? root, out _) &&
                root != null &&
                TryFindAiTaskBehavior(root, out JObject? behavior, out _, out _, out _) &&
                behavior != null)
            {
                behaviorClone = (JObject)behavior.DeepClone();
            }
            else
            {
                behaviorClone = new JObject { ["code"] = "taskai" };
            }

            behaviorClone["aitasks"] = new JArray((JObject)sourceTask.DeepClone());
            aiConfig = JsonObject.FromJson(behaviorClone.ToString(Formatting.None));
            status = "";
            return true;
        }
        catch (Exception exception)
        {
            status = $"Could not convert selected source task to runtime JsonObject: {exception.Message}";
            return false;
        }
    }

    private bool TryBuildAiBehaviorLiveTaskConstructorArgs(
        ConstructorInfo constructor,
        Entity serverEntity,
        JsonObject taskConfig,
        JsonObject aiConfig,
        JObject sourceTask,
        out object?[] args)
    {
        ParameterInfo[] parameters = constructor.GetParameters();
        args = new object?[parameters.Length];
        int jsonIndex = 0;

        for (int index = 0; index < parameters.Length; index++)
        {
            ParameterInfo parameter = parameters[index];
            Type parameterType = parameter.ParameterType;
            string parameterName = parameter.Name ?? "";

            if (parameterType.IsInstanceOfType(serverEntity))
            {
                args[index] = serverEntity;
                continue;
            }

            if (IsAiBehaviorJsonObjectType(parameterType))
            {
                args[index] = SelectAiBehaviorConstructorJsonObject(parameterName, jsonIndex++, taskConfig, aiConfig);
                continue;
            }

            ICoreServerAPI? serverApi = InGameDevToolsModSystem.ActiveServerApi;
            if (serverApi != null && parameterType.IsInstanceOfType(serverApi))
            {
                args[index] = serverApi;
                continue;
            }

            if (_api != null && parameterType.IsInstanceOfType(_api))
            {
                args[index] = _api;
                continue;
            }

            object? world = TryGetMemberValue(serverEntity, "World") ?? _api?.World;
            if (world != null && parameterType.IsInstanceOfType(world))
            {
                args[index] = world;
                continue;
            }

            if (parameterType == typeof(string))
            {
                args[index] = GetAiBehaviorTaskCode(sourceTask) ?? "";
                continue;
            }

            if (parameter.HasDefaultValue)
            {
                args[index] = parameter.DefaultValue;
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsAiBehaviorJsonObjectType(Type type)
    {
        return type == typeof(JsonObject) ||
            string.Equals(type.FullName, "Vintagestory.API.Datastructures.JsonObject", StringComparison.Ordinal);
    }

    private static JsonObject SelectAiBehaviorConstructorJsonObject(string parameterName, int jsonIndex, JsonObject taskConfig, JsonObject aiConfig)
    {
        if (parameterName.Contains("ai", StringComparison.OrdinalIgnoreCase) ||
            parameterName.Contains("attribute", StringComparison.OrdinalIgnoreCase) ||
            parameterName.Contains("behavior", StringComparison.OrdinalIgnoreCase))
        {
            return aiConfig;
        }

        if (parameterName.Contains("task", StringComparison.OrdinalIgnoreCase))
        {
            return taskConfig;
        }

        return jsonIndex == 0 ? taskConfig : aiConfig;
    }

    private static bool TryRunAiBehaviorLiveTaskAfterInitialize(object taskObject, out string status)
    {
        MethodInfo? method = taskObject.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, "AfterInitialize", StringComparison.Ordinal) &&
                candidate.GetParameters().Length == 0);

        if (method == null)
        {
            status = "";
            return true;
        }

        try
        {
            method.Invoke(taskObject, null);
            status = "";
            return true;
        }
        catch (TargetInvocationException exception)
        {
            status = $"AI task initialization failed: {exception.InnerException?.Message ?? exception.Message}";
            return false;
        }
        catch (Exception exception)
        {
            status = $"AI task initialization failed: {exception.Message}";
            return false;
        }
    }

    private static bool TryInvokeAiBehaviorTaskManagerMethod(object taskManager, string methodName, AiBehaviorLiveTaskInfo task, out string status)
    {
        List<MethodInfo> candidates = taskManager.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(method => ScoreAiBehaviorTaskManagerMethod(method, task))
            .ToList();

        foreach (MethodInfo method in candidates)
        {
            if (!TryBuildAiBehaviorTaskManagerArgs(method, task, out object?[] args)) continue;

            try
            {
                object? result = method.Invoke(taskManager, args);
                if (result is bool boolResult && !boolResult)
                {
                    status = $"{methodName} returned false for {task.Code}.";
                    return false;
                }

                status = $"{methodName} invoked via {method.Name}({string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name))}).";
                return true;
            }
            catch (TargetInvocationException exception)
            {
                status = $"{methodName} failed for {task.Code}: {exception.InnerException?.Message ?? exception.Message}";
                return false;
            }
            catch (Exception exception)
            {
                status = $"{methodName} failed for {task.Code}: {exception.Message}";
                return false;
            }
        }

        status = $"No compatible {methodName} overload found on {taskManager.GetType().Name}.";
        return false;
    }

    private static bool TryInvokeAiBehaviorTaskManagerMethod(object taskManager, string methodName, object taskObject, string code, out string status)
    {
        string slot = ReadAiBehaviorMemberString(taskObject, "Slot") ?? ReadAiBehaviorMemberString(taskObject, "slot") ?? "?";
        AiBehaviorLiveTaskInfo task = new(
            taskObject,
            string.IsNullOrWhiteSpace(code) ? taskObject.GetType().Name : code,
            taskObject.GetType().FullName ?? taskObject.GetType().Name,
            ReadAiBehaviorMemberString(taskObject, "Priority") ?? "?",
            slot,
            BuildAiBehaviorCooldownText(taskObject),
            false,
            "",
            "");
        return TryInvokeAiBehaviorTaskManagerMethod(taskManager, methodName, task, out status);
    }

    private static int ScoreAiBehaviorTaskManagerMethod(MethodInfo method, AiBehaviorLiveTaskInfo task)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(task.TaskObject)) return 0;
        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string)) return 1;
        if (parameters.Length == 2 && parameters[0].ParameterType.IsInstanceOfType(task.TaskObject)) return 2;
        if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string)) return 3;
        return 20 + parameters.Length;
    }

    private static bool TryBuildAiBehaviorTaskManagerArgs(MethodInfo method, AiBehaviorLiveTaskInfo task, out object?[] args)
    {
        ParameterInfo[] parameters = method.GetParameters();
        args = new object?[parameters.Length];
        if (parameters.Length == 0 || parameters.Length > 3) return false;

        for (int index = 0; index < parameters.Length; index++)
        {
            Type parameterType = parameters[index].ParameterType;
            string parameterName = parameters[index].Name ?? "";

            if (parameterType.IsInstanceOfType(task.TaskObject))
            {
                args[index] = task.TaskObject;
                continue;
            }

            if (parameterType == typeof(string))
            {
                args[index] = task.Code;
                continue;
            }

            if (parameterType == typeof(int))
            {
                args[index] = TryParseAiBehaviorSlot(task.Slot, out int slot) ? slot : 0;
                continue;
            }

            if (parameterType == typeof(bool))
            {
                args[index] = true;
                continue;
            }

            if (parameters[index].HasDefaultValue)
            {
                args[index] = parameters[index].DefaultValue;
                continue;
            }

            if (parameterName.Contains("slot", StringComparison.OrdinalIgnoreCase) &&
                TryConvertAiBehaviorLiveValue(TryParseAiBehaviorSlot(task.Slot, out int parsedSlot) ? parsedSlot : 0, parameterType, out object? convertedSlot))
            {
                args[index] = convertedSlot;
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryParseAiBehaviorSlot(string slotText, out int slot)
    {
        return int.TryParse(slotText, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot);
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

    private bool TryGetAiBehaviorEmotionStatesBehavior(Entity serverEntity, out object? behavior, out string source)
    {
        Type? behaviorType = FindAiBehaviorType("EntityBehaviorEmotionStates");
        if (behaviorType != null)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (MethodInfo method in serverEntity.GetType().GetMethods(flags))
            {
                if (!string.Equals(method.Name, "GetBehavior", StringComparison.Ordinal) ||
                    !method.IsGenericMethodDefinition ||
                    method.GetGenericArguments().Length != 1 ||
                    method.GetParameters().Length != 0)
                {
                    continue;
                }

                try
                {
                    behavior = method.MakeGenericMethod(behaviorType).Invoke(serverEntity, null);
                    if (behavior != null)
                    {
                        source = behavior.GetType().Name;
                        return true;
                    }
                }
                catch
                {
                    // Continue with the string lookup fallback below.
                }
            }
        }

        object? byCode = TryGetEntityBehaviorByCode(serverEntity, "emotionstates");
        if (byCode != null)
        {
            behavior = byCode;
            source = $"{byCode.GetType().Name} via behavior code";
            return true;
        }

        behavior = null;
        source = "";
        return false;
    }

    private static object? TryGetEntityBehaviorByCode(Entity serverEntity, string behaviorCode)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (MethodInfo method in serverEntity.GetType().GetMethods(flags))
        {
            if (!string.Equals(method.Name, "GetBehavior", StringComparison.Ordinal)) continue;

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string)) continue;

            try
            {
                object? behavior = method.Invoke(serverEntity, [behaviorCode]);
                if (behavior != null) return behavior;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static bool TaskHasAiBehaviorEmotionGate(AiBehaviorLiveTaskInfo task)
    {
        return IsMeaningfulAiBehaviorGateValue(TryGetMemberValue(task.TaskObject, "WhenInEmotionStates")) ||
            IsMeaningfulAiBehaviorGateValue(TryGetMemberValue(task.TaskObject, "whenInEmotionStates")) ||
            IsMeaningfulAiBehaviorGateValue(TryGetMemberValue(task.TaskObject, "WhenNotInEmotionStates")) ||
            IsMeaningfulAiBehaviorGateValue(TryGetMemberValue(task.TaskObject, "whenNotInEmotionStates"));
    }

    private static List<AiBehaviorEmotionStateInfo> BuildAiBehaviorAvailableEmotionStates(object behavior)
    {
        List<AiBehaviorEmotionStateInfo> states = [];
        object? rawStates = TryGetMemberValue(behavior, "availableStates");
        if (rawStates is not IEnumerable enumerable) return states;

        foreach (object? rawState in enumerable)
        {
            if (rawState == null) continue;
            string? code = ReadAiBehaviorMemberString(rawState, "Code") ?? ReadAiBehaviorMemberString(rawState, "code");
            if (string.IsNullOrWhiteSpace(code)) continue;

            states.Add(new AiBehaviorEmotionStateInfo(
                code,
                ReadAiBehaviorMemberString(rawState, "Slot") ?? "?",
                ReadAiBehaviorMemberString(rawState, "Priority") ?? "?",
                ReadAiBehaviorMemberString(rawState, "Chance") ?? "?",
                ReadAiBehaviorMemberString(rawState, "Duration") ?? "?",
                ReadAiBehaviorMemberString(rawState, "StressLevel") ?? "?"));
        }

        return states;
    }

    private static List<AiBehaviorActiveEmotionStateInfo> BuildAiBehaviorActiveEmotionStates(object behavior)
    {
        List<AiBehaviorActiveEmotionStateInfo> states = [];
        object? rawStates = TryGetMemberValue(behavior, "ActiveStatesByCode");
        if (rawStates is not IDictionary dictionary) return states;

        foreach (DictionaryEntry entry in dictionary)
        {
            string? code = entry.Key?.ToString();
            object? rawState = entry.Value;
            if (string.IsNullOrWhiteSpace(code) || rawState == null) continue;

            states.Add(new AiBehaviorActiveEmotionStateInfo(
                code,
                ReadAiBehaviorMemberString(rawState, "Duration") ?? "?",
                ReadAiBehaviorMemberString(rawState, "SourceEntityId") ?? "?",
                ReadAiBehaviorMemberString(rawState, "StateId") ?? "?"));
        }

        return states;
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

    private static IEnumerable<AiBehaviorLiveConfigObject> FindAiBehaviorLiveConfigObjects(object task)
    {
        HashSet<int> seen = [];
        foreach ((string memberName, object? value, Type valueType, _) in EnumerateAiBehaviorLiveRawMembers(task))
        {
            if (value == null || value is string || valueType.IsValueType) continue;
            if (value is IEnumerable) continue;

            string typeName = valueType.Name;
            bool looksLikeConfig = memberName.Contains("config", StringComparison.OrdinalIgnoreCase) ||
                typeName.EndsWith("Config", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Config", StringComparison.OrdinalIgnoreCase);
            if (!looksLikeConfig) continue;

            int id = RuntimeHelpers.GetHashCode(value);
            if (!seen.Add(id)) continue;

            yield return new AiBehaviorLiveConfigObject(memberName, value);
        }
    }

    private static IEnumerable<AiBehaviorLiveMember> EnumerateAiBehaviorLiveEditableMembers(object value)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string memberName, object? memberValue, Type valueType, bool canWrite) in EnumerateAiBehaviorLiveRawMembers(value))
        {
            if (!seen.Add(memberName) || !canWrite) continue;
            if (!IsAiBehaviorLiveEditableValue(valueType, memberValue)) continue;
            yield return new AiBehaviorLiveMember(memberName, valueType, memberValue, canWrite);
        }
    }

    private static IEnumerable<(string Name, object? Value, Type ValueType, bool CanWrite)> EnumerateAiBehaviorLiveRawMembers(object value)
    {
        Type type = value.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        for (Type? current = type; current != null; current = current.BaseType)
        {
            foreach (PropertyInfo property in current.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0) continue;
                object? propertyValue;
                try
                {
                    propertyValue = property.GetValue(value);
                }
                catch
                {
                    continue;
                }

                yield return (property.Name, propertyValue, property.PropertyType, property.GetSetMethod(nonPublic: true) != null);
            }

            foreach (FieldInfo field in current.GetFields(flags))
            {
                if (field.IsStatic) continue;
                object? fieldValue;
                try
                {
                    fieldValue = field.GetValue(value);
                }
                catch
                {
                    continue;
                }

                yield return (field.Name, fieldValue, field.FieldType, !field.IsInitOnly && !field.IsLiteral);
            }
        }
    }

    private static bool IsAiBehaviorLiveEditableValue(Type valueType, object? value)
    {
        Type type = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (type == typeof(bool) || type == typeof(string)) return true;
        if (IsAiBehaviorNumericType(type)) return true;
        if (value != null && TryExtractAiBehaviorRange(value, out _)) return true;
        return false;
    }

    private static bool IsAiBehaviorNumericType(Type type)
    {
        Type nullableType = Nullable.GetUnderlyingType(type) ?? type;
        return nullableType == typeof(byte) ||
            nullableType == typeof(sbyte) ||
            nullableType == typeof(short) ||
            nullableType == typeof(ushort) ||
            nullableType == typeof(int) ||
            nullableType == typeof(uint) ||
            nullableType == typeof(long) ||
            nullableType == typeof(ulong) ||
            nullableType == typeof(float) ||
            nullableType == typeof(double) ||
            nullableType == typeof(decimal);
    }

    private static bool IsAiBehaviorIntegerType(Type type)
    {
        Type nullableType = Nullable.GetUnderlyingType(type) ?? type;
        return nullableType == typeof(byte) ||
            nullableType == typeof(sbyte) ||
            nullableType == typeof(short) ||
            nullableType == typeof(ushort) ||
            nullableType == typeof(int) ||
            nullableType == typeof(uint) ||
            nullableType == typeof(long) ||
            nullableType == typeof(ulong);
    }

    private static (float Min, float Max) InferAiBehaviorLiveRangeBounds(string memberName)
    {
        string name = memberName.ToLowerInvariant();
        if (name.Contains("chance", StringComparison.Ordinal) ||
            name.Contains("day", StringComparison.Ordinal) ||
            name.Contains("hour", StringComparison.Ordinal))
        {
            return (0f, 1f);
        }

        if (name.Contains("light", StringComparison.Ordinal))
        {
            return (0f, 32f);
        }

        if (name.Contains("temp", StringComparison.Ordinal))
        {
            return (-50f, 100f);
        }

        if (name.Contains("range", StringComparison.Ordinal) ||
            name.Contains("dist", StringComparison.Ordinal))
        {
            return (0f, 256f);
        }

        return (-1000f, 1000f);
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

        if (root["server"] is not JObject server)
        {
            return false;
        }

        if (server["behaviors"] is JArray behaviors)
        {
            for (int index = 0; index < behaviors.Count; index++)
            {
                if (behaviors[index] is not JObject candidate) continue;
                string? code = candidate["code"]?.ToString() ?? candidate["name"]?.ToString();
                bool isTaskAi = string.Equals(code, "taskai", StringComparison.OrdinalIgnoreCase) ||
                    candidate["aitasks"] is JArray ||
                    candidate["aitasksByType"] is JObject;
                if (!isTaskAi) continue;

                behavior = ResolveAiTaskBehaviorConfig(server, candidate, code, out string configPath);
                tasks = behavior["aitasks"] as JArray;
                tasksByType = behavior["aitasksByType"] as JObject;
                behaviorPath = string.IsNullOrWhiteSpace(configPath) ? $"server.behaviors[{index}]" : configPath;
                return true;
            }
        }

        foreach (JProperty property in server.Properties())
        {
            if (property.Value is not JObject candidate) continue;
            if (candidate["aitasks"] is not JArray && candidate["aitasksByType"] is not JObject) continue;

            behavior = candidate;
            tasks = candidate["aitasks"] as JArray;
            tasksByType = candidate["aitasksByType"] as JObject;
            behaviorPath = $"server.{property.Name}";
            return true;
        }

        return false;
    }

    private static JObject ResolveAiTaskBehaviorConfig(JObject server, JObject behaviorStub, string? behaviorCode, out string path)
    {
        path = "";
        if (behaviorStub["aitasks"] is JArray || behaviorStub["aitasksByType"] is JObject)
        {
            return behaviorStub;
        }

        if (!string.IsNullOrWhiteSpace(behaviorCode) &&
            server[behaviorCode] is JObject sidecar &&
            (sidecar["aitasks"] is JArray || sidecar["aitasksByType"] is JObject))
        {
            path = $"server.{behaviorCode}";
            return sidecar;
        }

        return behaviorStub;
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

    private static readonly AiBehaviorParameterSpec[] AiBehaviorParameterSpecs =
    [
        new(AiBehaviorParameterScope.Common, AiBehaviorParameterKind.Int, "Priority", 0f, 10000f, "%.0f", ["priority"], ["Priority", "priority"]),
        new(AiBehaviorParameterScope.Common, AiBehaviorParameterKind.Int, "Slot", 0f, 64f, "%.0f", ["slot"], ["Slot", "slot"]),
        new(AiBehaviorParameterScope.Common, AiBehaviorParameterKind.Float, "Execution chance", 0f, 1f, "%.3f", ["executionChance", "chance"], ["ExecutionChance", "executionChance", "chance"]),
        new(AiBehaviorParameterScope.Common, AiBehaviorParameterKind.Int, "Min cooldown ms", 0f, 3_600_000f, "%.0f", ["mincooldown", "minCooldownMs"], ["MinCooldownMs", "mincooldown", "minCooldownMs"]),
        new(AiBehaviorParameterScope.Common, AiBehaviorParameterKind.Int, "Max cooldown ms", 0f, 3_600_000f, "%.0f", ["maxcooldown", "maxCooldownMs"], ["MaxCooldownMs", "maxcooldown", "maxCooldownMs"]),
        new(AiBehaviorParameterScope.Common, AiBehaviorParameterKind.Bool, "When swimming", 0f, 1f, "%.0f", ["whenSwimming"], ["WhenSwimming", "whenSwimming"]),
        new(AiBehaviorParameterScope.Common, AiBehaviorParameterKind.Range, "Day time frames", 0f, 1f, "%.3f", ["duringDayTimeFrames", "dayTimeFrames"], ["duringDayTimeFrames", "DuringDayTimeFrames", "DayTimeFrames", "dayTimeFrames"]),
        new(AiBehaviorParameterScope.Common, AiBehaviorParameterKind.Range, "Light levels", 0f, 32f, "%.1f", ["entityLightLevels", "lightLevelRange"], ["EntityLightLevels", "entityLightLevels", "LightLevelRange", "lightLevelRange"]),
        new(AiBehaviorParameterScope.Common, AiBehaviorParameterKind.Range, "Temperature range", -50f, 100f, "%.1f", ["temperatureRange"], ["TemperatureRange", "temperatureRange"]),
        new(AiBehaviorParameterScope.TaskSpecific, AiBehaviorParameterKind.Float, "Move speed", 0f, 10f, "%.3f", ["moveSpeed", "movespeed"], ["moveSpeed", "movespeed", "MoveSpeed"]),
        new(AiBehaviorParameterScope.TaskSpecific, AiBehaviorParameterKind.Float, "Seeking range", 0f, 256f, "%.2f", ["seekingRange", "seekRange", "range"], ["seekingRange", "SeekingRange", "seekRange", "range"]),
        new(AiBehaviorParameterScope.TaskSpecific, AiBehaviorParameterKind.Float, "Attack range", 0f, 64f, "%.2f", ["attackRange"], ["attackRange", "AttackRange"]),
        new(AiBehaviorParameterScope.TaskSpecific, AiBehaviorParameterKind.Float, "Damage", 0f, 500f, "%.2f", ["damage"], ["damage", "Damage"]),
        new(AiBehaviorParameterScope.TaskSpecific, AiBehaviorParameterKind.Float, "Knockback strength", 0f, 100f, "%.2f", ["knockbackStrength"], ["knockbackStrength", "KnockbackStrength"]),
        new(AiBehaviorParameterScope.TaskSpecific, AiBehaviorParameterKind.Float, "Attack angle deg", 0f, 360f, "%.1f", ["attackAngleRangeDeg"], ["attackAngleRangeDeg", "AttackAngleRangeDeg"]),
        new(AiBehaviorParameterScope.TaskSpecific, AiBehaviorParameterKind.Float, "Max follow time", 0f, 3600f, "%.1f", ["maxFollowTime"], ["maxFollowTime", "MaxFollowTime"]),
        new(AiBehaviorParameterScope.TaskSpecific, AiBehaviorParameterKind.Float, "Leap chance", 0f, 1f, "%.3f", ["leapChance"], ["leapChance", "LeapChance"]),
        new(AiBehaviorParameterScope.TaskSpecific, AiBehaviorParameterKind.Float, "Flee distance", 0f, 256f, "%.2f", ["fleeDistance"], ["fleeDistance", "FleeDistance"]),
        new(AiBehaviorParameterScope.TaskSpecific, AiBehaviorParameterKind.Float, "Retaliate range", 0f, 256f, "%.2f", ["retaliateRange"], ["retaliateRange", "RetaliateRange"])
    ];

    private static readonly HashSet<string> AiBehaviorFirstClassProperties = BuildAiBehaviorFirstClassProperties();

    private static HashSet<string> BuildAiBehaviorFirstClassProperties()
    {
        HashSet<string> properties = new(StringComparer.OrdinalIgnoreCase) { "code", "id" };
        foreach (AiBehaviorParameterSpec spec in AiBehaviorParameterSpecs)
        {
            foreach (string propertyName in spec.SourcePropertyNames)
            {
                properties.Add(propertyName);
            }
        }
        return properties;
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

    private sealed record AiBehaviorEmotionStateInfo(
        string Code,
        string Slot,
        string Priority,
        string Chance,
        string Duration,
        string Stress);

    private sealed record AiBehaviorActiveEmotionStateInfo(
        string Code,
        string Duration,
        string SourceEntityId,
        string StateId);

    private readonly record struct AiBehaviorLiveMember(string Name, Type ValueType, object? Value, bool CanWrite);

    private sealed record AiBehaviorLiveConfigObject(string Name, object Value);

    private sealed record AiBehaviorLiveFieldSnapshot(
        long EntityId,
        int TaskRuntimeId,
        WeakReference<object> Target,
        string MemberName,
        object? OriginalValue);

    private sealed record AiBehaviorLiveRemovedTaskSnapshot(
        long EntityId,
        int TaskRuntimeId,
        WeakReference<object> TaskManager,
        object TaskObject,
        string Code);

    private sealed record AiBehaviorLiveAddedTaskSnapshot(
        long EntityId,
        int TaskRuntimeId,
        WeakReference<object> TaskManager,
        object TaskObject,
        string Code);

    private sealed record AiBehaviorParameterSpec(
        AiBehaviorParameterScope Scope,
        AiBehaviorParameterKind Kind,
        string Label,
        float Min,
        float Max,
        string Format,
        string[] SourcePropertyNames,
        string[] LiveMemberNames);

    private enum AiBehaviorParameterScope
    {
        Common,
        TaskSpecific
    }

    private enum AiBehaviorParameterKind
    {
        Int,
        Float,
        Bool,
        Range
    }

    private sealed record AiBehaviorVariantGroup(string Code, IReadOnlyList<string> States);
}
