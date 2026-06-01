using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private readonly List<BlockItemJsonEntry> _blockItemJsonAssets = [];
    private readonly List<BlockItemJsonEntry> _visibleBlockItemJsonAssets = [];
    private readonly ImGuiThreePanelLayoutState _blockItemJsonLayout = new(0.28f, 0.30f);
    private readonly DevToolsEditorDiagnostics _blockItemJsonDiagnostics = new("Block/Item JSON");
    private string _blockItemJsonFilter = "";
    private string _blockItemJsonDomainFilter = "";
    private int _blockItemJsonTypeFilter;
    private int _blockItemJsonAssetIndex;
    private bool _blockItemJsonDirtyOnly;
    private bool _blockItemJsonIndexed;
    private string _blockItemJsonLoadedKey = "";
    private string _blockItemJsonText = "";
    private string _blockItemJsonOriginalText = "";
    private string _blockItemJsonStatus = "";
    private string _blockItemJsonLiveAppliedHash = "";

    private void BlockItemJsonEditorTab(float deltaSeconds, bool showDiagnostics)
    {
        _ = deltaSeconds;
        ClearActiveTransformGizmo();
        EnsureBlockItemJsonAssetsIndexed();

        NVector2 available = ImGui.GetContentRegionAvail();
        float scale = Math.Max(0.75f, _devToolsUiScale);
        float splitterThickness = Math.Max(5f, 6f * scale);
        ImGuiLayoutHelper.CalculateThreePanelWidths(
            available.X,
            splitterThickness,
            _blockItemJsonLayout,
            260f * scale,
            600f * scale,
            420f * scale,
            320f * scale,
            680f * scale,
            out float panelAvailableWidth,
            out float leftWidth,
            out float centerWidth,
            out float rightWidth);

        DrawBlockItemJsonBrowser(new NVector2(leftWidth, available.Y));
        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter("##block-json-left-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _blockItemJsonLayout.LeftFraction, 260f * scale, Math.Max(260f * scale, panelAvailableWidth - rightWidth - 420f * scale));
        ImGui.SameLine(0, 0);
        DrawBlockItemJsonTextEditor(new NVector2(centerWidth, available.Y));
        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter("##block-json-right-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _blockItemJsonLayout.RightFraction, 320f * scale, Math.Max(320f * scale, panelAvailableWidth - leftWidth - 420f * scale), invertDrag: true);
        ImGui.SameLine(0, 0);
        DrawBlockItemJsonInspector(new NVector2(rightWidth, available.Y), showDiagnostics);
    }

    private void ResetBlockItemJsonLayout()
    {
        _blockItemJsonLayout.Reset();
    }

    private void EnsureBlockItemJsonAssetsIndexed()
    {
        if (_blockItemJsonIndexed) return;

        _blockItemJsonAssets.Clear();
        foreach (Block block in _api.World.Blocks)
        {
            if (block?.Code == null) continue;
            _blockItemJsonAssets.Add(new(block, true));
        }

        foreach (Item item in _api.World.Items)
        {
            if (item?.Code == null) continue;
            _blockItemJsonAssets.Add(new(item, false));
        }

        _blockItemJsonAssets.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));
        RebuildVisibleBlockItemJsonAssets();
        _blockItemJsonIndexed = true;
        _blockItemJsonStatus = $"Indexed {_blockItemJsonAssets.Count} block/item runtime objects.";
    }

    private void RebuildVisibleBlockItemJsonAssets()
    {
        string filter = _blockItemJsonFilter.Trim();
        BlockItemJsonEntry? selected = SelectedBlockItemJsonAsset;
        _visibleBlockItemJsonAssets.Clear();

        foreach (BlockItemJsonEntry entry in _blockItemJsonAssets)
        {
            if (!ImGuiLayoutHelper.MatchesDomain(_blockItemJsonDomainFilter, entry.Domain)) continue;
            if (_blockItemJsonTypeFilter == 1 && !entry.IsBlock) continue;
            if (_blockItemJsonTypeFilter == 2 && entry.IsBlock) continue;
            if (_blockItemJsonDirtyOnly && !string.Equals(entry.Key, _blockItemJsonLoadedKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (_blockItemJsonDirtyOnly && !IsBlockItemJsonDirty) continue;
            if (!string.IsNullOrWhiteSpace(filter) && !entry.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            _visibleBlockItemJsonAssets.Add(entry);
        }

        if (selected != null)
        {
            int selectedIndex = _visibleBlockItemJsonAssets.FindIndex(entry => entry.Key == selected.Key);
            if (selectedIndex >= 0)
            {
                _blockItemJsonAssetIndex = selectedIndex;
                return;
            }
        }

        _blockItemJsonAssetIndex = Math.Clamp(_blockItemJsonAssetIndex, 0, Math.Max(0, _visibleBlockItemJsonAssets.Count - 1));
    }

    private BlockItemJsonEntry? SelectedBlockItemJsonAsset =>
        _visibleBlockItemJsonAssets.Count == 0
            ? null
            : _visibleBlockItemJsonAssets[Math.Clamp(_blockItemJsonAssetIndex, 0, _visibleBlockItemJsonAssets.Count - 1)];

    private bool IsBlockItemJsonDirty => !string.Equals(_blockItemJsonText, _blockItemJsonOriginalText, StringComparison.Ordinal);

    private void DrawBlockItemJsonBrowser(NVector2 size)
    {
        ImGui.BeginChild("##block-item-json-browser", size, true);

        ImGui.SeparatorText("Assets");
        if (ImGui.InputText("Filter##block-json-filter", ref _blockItemJsonFilter, 200))
        {
            RebuildVisibleBlockItemJsonAssets();
        }

        string[] typeOptions = ["All", "Blocks", "Items"];
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("Type##block-json-type", ref _blockItemJsonTypeFilter, typeOptions, typeOptions.Length))
        {
            RebuildVisibleBlockItemJsonAssets();
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("Dirty only##block-json-dirty", ref _blockItemJsonDirtyOnly))
        {
            RebuildVisibleBlockItemJsonAssets();
        }

        if (ImGui.InputText("Domain##block-json-domain", ref _blockItemJsonDomainFilter, 80))
        {
            RebuildVisibleBlockItemJsonAssets();
        }

        if (ImGui.Button("Reload index##block-json-reload", new NVector2(-1, 0)))
        {
            _blockItemJsonIndexed = false;
            _blockItemJsonLoadedKey = "";
            _blockItemJsonText = "";
            _blockItemJsonOriginalText = "";
            _blockItemJsonLiveAppliedHash = "";
            EnsureBlockItemJsonAssetsIndexed();
        }

        ImGui.TextDisabled($"{_visibleBlockItemJsonAssets.Count} / {_blockItemJsonAssets.Count}");

        if (_visibleBlockItemJsonAssets.Count == 0)
        {
            ImGui.TextDisabled("No matching block/item assets.");
            ImGui.EndChild();
            return;
        }

        _blockItemJsonAssetIndex = Math.Clamp(_blockItemJsonAssetIndex, 0, _visibleBlockItemJsonAssets.Count - 1);
        float listHeight = Math.Max(140f, ImGui.GetContentRegionAvail().Y);
        if (ImGui.BeginListBox("##block-json-assets", new NVector2(-float.Epsilon, listHeight)))
        {
            for (int index = 0; index < _visibleBlockItemJsonAssets.Count; index++)
            {
                BlockItemJsonEntry entry = _visibleBlockItemJsonAssets[index];
                bool selected = index == _blockItemJsonAssetIndex;
                string dirtyMarker = IsBlockItemJsonDirty && string.Equals(entry.Key, _blockItemJsonLoadedKey, StringComparison.OrdinalIgnoreCase) ? "*" : "";
                if (ImGui.Selectable($"{dirtyMarker}{entry.Label}##block-json-asset-{index}", selected))
                {
                    _blockItemJsonAssetIndex = index;
                    LoadBlockItemJsonEntry(entry, keepDirty: false);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(entry.Tooltip);
                }
            }

            ImGui.EndListBox();
        }

        ImGui.EndChild();
    }

    private void DrawBlockItemJsonTextEditor(NVector2 size)
    {
        ImGui.BeginChild("##block-item-json-text-editor", size, true);

        BlockItemJsonEntry? entry = SelectedBlockItemJsonAsset;
        if (entry == null)
        {
            ImGui.TextDisabled("Select a block or item.");
            ImGui.EndChild();
            return;
        }

        EnsureBlockItemJsonEntryLoaded(entry);

        ImGui.SeparatorText(entry.Label);
        ImGui.TextDisabled("Raw authoring JSON. Runtime apply currently patches attributes/transforms; source save writes the full JSON.");

        NVector2 editorSize = new(-float.Epsilon, Math.Max(220f, ImGui.GetContentRegionAvail().Y - 34f));
        if (ImGui.InputTextMultiline("##block-item-json-text", ref _blockItemJsonText, 1024 * 1024, editorSize, ImGuiInputTextFlags.AllowTabInput))
        {
            _blockItemJsonStatus = "JSON edited. Apply runtime or save authored file when ready.";
            RebuildVisibleBlockItemJsonAssets();
            if (_liveApplyManager.AutoApply)
            {
                ApplyBlockItemJsonRuntime(force: false);
            }
        }

        ImGui.EndChild();
    }

    private void DrawBlockItemJsonInspector(NVector2 size, bool showDiagnostics)
    {
        ImGui.BeginChild("##block-item-json-inspector", size, true);

        BlockItemJsonEntry? entry = SelectedBlockItemJsonAsset;
        if (entry == null)
        {
            ImGui.TextDisabled("Select a block or item.");
            ImGui.EndChild();
            return;
        }

        EnsureBlockItemJsonEntryLoaded(entry);

        ImGui.SeparatorText("Runtime");
        ImGui.TextDisabled($"{(entry.IsBlock ? "Block" : "Item")}: {entry.Code}");
        ImGui.TextDisabled($"Domain: {entry.Domain}");
        IAsset? sourceAsset = FindCollectibleSourceAsset(entry.Collectible);
        ImGui.TextWrapped(sourceAsset?.Location.ToString() ?? "Source asset: unresolved; save will create an authored override.");

        bool valid = TryParseJsonObject(_blockItemJsonText) != null;
        ImGui.TextColored(valid ? new NVector4(0.42f, 0.85f, 0.42f, 1f) : new NVector4(1f, 0.38f, 0.32f, 1f), valid ? "JSON valid" : "JSON invalid");

        if (ImGui.Button("Format JSON##block-json-format"))
        {
            FormatBlockItemJsonText();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reload selected##block-json-reload-selected"))
        {
            LoadBlockItemJsonEntry(entry, keepDirty: false);
        }

        if (!valid) ImGui.BeginDisabled();
        if (ImGui.Button("Apply runtime attributes##block-json-runtime"))
        {
            ApplyBlockItemJsonRuntime(force: true);
        }
        if (!valid) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Patches the selected runtime object's attributes JsonObject. Shape and texture JSON are saved for reload/export but are not retesselated in this first pass.");
        }

        ImGui.SameLine();
        bool canRevert = _liveApplyManager.CanRevert(GetBlockItemJsonLiveKey(entry));
        if (!canRevert) ImGui.BeginDisabled();
        if (ImGui.Button("Revert runtime##block-json-revert"))
        {
            _blockItemJsonLiveAppliedHash = "";
            _blockItemJsonStatus = _liveApplyManager.Revert(GetBlockItemJsonLiveKey(entry));
        }
        if (!canRevert) ImGui.EndDisabled();

        if (!valid) ImGui.BeginDisabled();
        if (ImGui.Button("Save authored JSON##block-json-save"))
        {
            QueueSourceSave(TrySaveBlockItemJsonToSource(entry), status => _blockItemJsonStatus = status);
        }
        if (!valid) ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(_blockItemJsonStatus))
        {
            ImGui.TextWrapped(_blockItemJsonStatus);
        }

        _blockItemJsonDiagnostics.Draw("block-item-json", showDiagnostics);

        ImGui.SeparatorText("Scope");
        ImGui.TextWrapped("Runtime apply is intentionally limited to attributes because those are mutable on loaded CollectibleObject instances. The full JSON save path is available for shape, texture, class, and other source-level edits.");

        ImGui.EndChild();
    }

    private void EnsureBlockItemJsonEntryLoaded(BlockItemJsonEntry entry)
    {
        if (string.Equals(_blockItemJsonLoadedKey, entry.Key, StringComparison.Ordinal)) return;
        LoadBlockItemJsonEntry(entry, keepDirty: true);
    }

    private void LoadBlockItemJsonEntry(BlockItemJsonEntry entry, bool keepDirty)
    {
        if (keepDirty && IsBlockItemJsonDirty && !string.IsNullOrWhiteSpace(_blockItemJsonLoadedKey)) return;

        try
        {
            JObject json = BuildBlockItemJsonDocument(entry);
            _blockItemJsonText = json.ToString(Formatting.Indented);
            _blockItemJsonOriginalText = _blockItemJsonText;
            _blockItemJsonLoadedKey = entry.Key;
            _blockItemJsonLiveAppliedHash = "";
            _blockItemJsonStatus = $"Loaded {entry.Label}.";
        }
        catch (Exception exception)
        {
            _blockItemJsonDiagnostics.Exception($"Failed to load {entry.Label}", exception);
            _blockItemJsonStatus = $"Failed to load {entry.Label}: {exception.Message}";
        }
    }

    private JObject BuildBlockItemJsonDocument(BlockItemJsonEntry entry)
    {
        IAsset? sourceAsset = FindCollectibleSourceAsset(entry.Collectible);
        JObject json = TryParseJsonObject(ReadAssetText(sourceAsset)) ?? CreateCollectibleAuthoringDocument(entry.Collectible);
        json["code"] ??= entry.Code;
        if (entry.Collectible.Attributes?.Token is JObject runtimeAttributes)
        {
            json["attributes"] = runtimeAttributes.DeepClone();
        }

        return json;
    }

    private void FormatBlockItemJsonText()
    {
        JObject? json = TryParseJsonObject(_blockItemJsonText);
        if (json == null)
        {
            _blockItemJsonStatus = "Cannot format invalid JSON.";
            return;
        }

        _blockItemJsonText = json.ToString(Formatting.Indented);
        _blockItemJsonStatus = "Formatted JSON.";
    }

    private void ApplyBlockItemJsonRuntime(bool force = false)
    {
        BlockItemJsonEntry? entry = SelectedBlockItemJsonAsset;
        if (entry == null)
        {
            _liveApplyManager.LastStatus = "No selected block/item JSON asset.";
            return;
        }

        EnsureBlockItemJsonEntryLoaded(entry);
        JObject? json = TryParseJsonObject(_blockItemJsonText);
        if (json == null)
        {
            _blockItemJsonStatus = "Runtime apply skipped: JSON is invalid.";
            _liveApplyManager.LastStatus = _blockItemJsonStatus;
            return;
        }

        if (json["attributes"] is not JObject attributes)
        {
            _blockItemJsonStatus = "Runtime apply skipped: JSON has no attributes object.";
            _liveApplyManager.LastStatus = _blockItemJsonStatus;
            return;
        }

        string hash = attributes.ToString(Formatting.None);
        if (!force && string.Equals(_blockItemJsonLiveAppliedHash, hash, StringComparison.Ordinal))
        {
            return;
        }

        string key = GetBlockItemJsonLiveKey(entry);
        _blockItemJsonStatus = _liveApplyManager.Apply(
            key,
            entry.Label,
            () => CaptureBlockItemJsonLiveSnapshot(entry),
            () => entry.Collectible.Attributes = new JsonObject(attributes.DeepClone()),
            $"Live applied attributes for {entry.Label}.");
        _blockItemJsonLiveAppliedHash = hash;
    }

    private void ClearBlockItemJsonLiveApplyState()
    {
        _blockItemJsonLiveAppliedHash = "";
    }

    private LivePatchSnapshot CaptureBlockItemJsonLiveSnapshot(BlockItemJsonEntry entry)
    {
        JToken? original = entry.Collectible.Attributes?.Token?.DeepClone();
        return new(
            () => entry.Collectible.Attributes = original == null ? null : new JsonObject(original.DeepClone()),
            Path.Combine("assets", entry.Domain, "runtime-json", entry.Collectible.Code.Path.Replace('/', '_') + ".json"),
            () => (original ?? new JObject()).ToString(Formatting.Indented),
            "block-item-json");
    }

    private static string GetBlockItemJsonLiveKey(BlockItemJsonEntry entry) => $"block-item-json:{entry.Key}";

    private SourceSaveResult TrySaveBlockItemJsonToSource(BlockItemJsonEntry entry)
    {
        try
        {
            JObject? json = TryParseJsonObject(_blockItemJsonText);
            if (json == null) return SourceSaveResult.Fail("Save failed: JSON is invalid.");

            IAsset? sourceAsset = FindCollectibleSourceAsset(entry.Collectible);
            string domain = sourceAsset?.Location.Domain ?? entry.Domain;
            string kind = entry.IsBlock ? "blocktypes" : "itemtypes";
            string assetPath = sourceAsset?.Location.Path ?? $"{kind}/{EnsureJsonFilePath(entry.Collectible.Code?.Path ?? "unknown")}";
            string outputPath = GetToolAuthoredAssetPath("block-item-json", Path.Combine("assets", domain, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            string sourceText = ReadAssetText(sourceAsset);
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : sourceText;
            string newText = json.ToString(Formatting.Indented);
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved authored JSON to {outputPath}.",
                () =>
                {
                    string result = WriteAuthoredFile(outputPath, newText);
                    _blockItemJsonOriginalText = newText;
                    RebuildVisibleBlockItemJsonAssets();
                    return result;
                });
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            _blockItemJsonDiagnostics.Exception($"Save failed for {entry.Label}", exception);
            return SourceSaveResult.Fail($"Save failed for {entry.Label}: {exception.Message}");
        }
    }

    private sealed record BlockItemJsonEntry(CollectibleObject Collectible, bool IsBlock)
    {
        public string Code => Collectible.Code?.ToString() ?? "unknown";
        public string Domain => Collectible.Code?.Domain ?? "game";
        public string Key => $"{(IsBlock ? "block" : "item")}:{Code}";
        public string Label => $"{(IsBlock ? "block" : "item")}:{ImGuiLayoutHelper.CompactAssetCode(Code)}";
        public string SearchText => $"{Label} {Code} {Domain}";
        public string Tooltip => $"{(IsBlock ? "Block" : "Item")}: {Code}";
    }
}
