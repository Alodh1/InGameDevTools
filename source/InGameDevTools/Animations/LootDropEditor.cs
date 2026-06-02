using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private const float DefaultLootDropWeight = 1f;
    private const float LootDropWeightEpsilon = 0.0001f;

    private readonly List<LootDropEntry> _lootDropEntries = [];
    private readonly List<LootDropEntry> _visibleLootDropEntries = [];
    private readonly List<LootDropDraft> _lootDropDrafts = [];
    private readonly ImGuiThreePanelLayoutState _lootDropLayout = new(0.26f, 0.30f);
    private readonly DevToolsEditorDiagnostics _lootDropDiagnostics = new("Loot/Drops");
    private string _lootDropFilter = "";
    private string _lootDropDomainFilter = "";
    private int _lootDropKindFilter;
    private int _lootDropEntryIndex;
    private int _lootDropSelectedDraftIndex;
    private bool _lootDropDirtyOnly;
    private bool _lootDropIndexed;
    private string _lootDropLoadedKey = "";
    private string _lootDropOriginalJson = "";
    private string _lootDropTradeJson = "";
    private string _lootDropStatus = "";
    private string _lootDropLiveAppliedHash = "";
    private int _lootDropSimulationRuns = 1000;
    private string _lootDropSimulationText = "";

    private void LootDropEditorTab(float deltaSeconds, bool showDiagnostics)
    {
        _ = deltaSeconds;
        ClearActiveTransformGizmo();
        EnsureLootDropEntriesIndexed();

        NVector2 available = ImGui.GetContentRegionAvail();
        float scale = Math.Max(0.75f, _devToolsUiScale);
        float splitterThickness = Math.Max(5f, 6f * scale);
        ImGuiLayoutHelper.CalculateThreePanelWidths(
            available.X,
            splitterThickness,
            _lootDropLayout,
            260f * scale,
            620f * scale,
            440f * scale,
            340f * scale,
            780f * scale,
            out float panelAvailableWidth,
            out float leftWidth,
            out float centerWidth,
            out float rightWidth);

        DrawLootDropBrowser(new NVector2(leftWidth, available.Y));
        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter("##loot-drop-left-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _lootDropLayout.LeftFraction, 260f * scale, Math.Max(260f * scale, panelAvailableWidth - rightWidth - 440f * scale));
        ImGui.SameLine(0, 0);
        DrawLootDropEditorPanel(new NVector2(centerWidth, available.Y));
        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter("##loot-drop-right-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _lootDropLayout.RightFraction, 340f * scale, Math.Max(340f * scale, panelAvailableWidth - leftWidth - 440f * scale), invertDrag: true);
        ImGui.SameLine(0, 0);
        DrawLootDropInspector(new NVector2(rightWidth, available.Y), showDiagnostics);
    }

    private void ResetLootDropLayout()
    {
        _lootDropLayout.Reset();
    }

    private void EnsureLootDropEntriesIndexed()
    {
        if (_lootDropIndexed) return;

        _lootDropEntries.Clear();
        foreach (Block block in _api.World.Blocks)
        {
            if (block?.Code == null) continue;
            IAsset? sourceAsset = FindCollectibleSourceAsset(block);
            JObject? sourceJson = TryParseJsonObject(ReadAssetText(sourceAsset));
            if ((block.Drops?.Length ?? 0) > 0 || sourceJson?["drops"] is JArray)
            {
                _lootDropEntries.Add(LootDropEntry.ForBlock(block, sourceAsset, sourceJson));
            }
        }

        foreach (EntityProperties entityType in _api.World.EntityTypes ?? [])
        {
            if (entityType?.Code == null) continue;
            IAsset? sourceAsset = FindEntitySourceAsset(entityType);
            JObject? sourceJson = TryParseJsonObject(ReadAssetText(sourceAsset));
            if ((entityType.Drops?.Length ?? 0) > 0 || sourceJson?["drops"] is JArray)
            {
                _lootDropEntries.Add(LootDropEntry.ForEntity(entityType, sourceAsset, sourceJson));
            }
        }

        foreach (IAsset asset in _api.Assets.AllAssets.Values)
        {
            if (asset?.Location == null) continue;
            string assetPath = asset.Location.Path.Replace('\\', '/');
            if (!assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (!assetPath.StartsWith("entities/", StringComparison.OrdinalIgnoreCase) &&
                !assetPath.StartsWith("config/", StringComparison.OrdinalIgnoreCase) &&
                !assetPath.Contains("trade", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            JObject? sourceJson = TryParseJsonObject(ReadAssetText(asset));
            if (sourceJson == null) continue;
            if (!TryFindTradeToken(sourceJson, out List<string> path, out JToken? tradeToken) || tradeToken == null) continue;

            _lootDropEntries.Add(LootDropEntry.ForTrade(asset, sourceJson, path, tradeToken));
        }

        _lootDropEntries.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));
        RebuildVisibleLootDropEntries();
        _lootDropIndexed = true;
        _lootDropStatus = $"Indexed {_lootDropEntries.Count} loot/drop source(s).";
    }

    private void RebuildVisibleLootDropEntries()
    {
        string filter = _lootDropFilter.Trim();
        LootDropEntry? selected = SelectedLootDropEntry;
        _visibleLootDropEntries.Clear();

        foreach (LootDropEntry entry in _lootDropEntries)
        {
            if (!ImGuiLayoutHelper.MatchesDomain(_lootDropDomainFilter, entry.Domain)) continue;
            if (_lootDropKindFilter == 1 && entry.Kind != LootDropKind.BlockDrops) continue;
            if (_lootDropKindFilter == 2 && entry.Kind != LootDropKind.EntityDrops) continue;
            if (_lootDropKindFilter == 3 && entry.Kind != LootDropKind.TradeTable) continue;
            if (_lootDropDirtyOnly && (!string.Equals(entry.Key, _lootDropLoadedKey, StringComparison.OrdinalIgnoreCase) || !IsLootDropDirty)) continue;
            if (!string.IsNullOrWhiteSpace(filter) && !entry.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            _visibleLootDropEntries.Add(entry);
        }

        if (selected != null)
        {
            int selectedIndex = _visibleLootDropEntries.FindIndex(entry => entry.Key == selected.Key);
            if (selectedIndex >= 0)
            {
                _lootDropEntryIndex = selectedIndex;
                return;
            }
        }

        _lootDropEntryIndex = Math.Clamp(_lootDropEntryIndex, 0, Math.Max(0, _visibleLootDropEntries.Count - 1));
    }

    private LootDropEntry? SelectedLootDropEntry =>
        _visibleLootDropEntries.Count == 0
            ? null
            : _visibleLootDropEntries[Math.Clamp(_lootDropEntryIndex, 0, _visibleLootDropEntries.Count - 1)];

    private bool IsLootDropDirty => !string.Equals(CurrentLootDropJson(), _lootDropOriginalJson, StringComparison.Ordinal);

    private void DrawLootDropBrowser(NVector2 size)
    {
        ImGui.BeginChild("##loot-drop-browser", size, true);
        ImGui.SeparatorText("Loot sources");

        if (ImGui.InputText("Filter##loot-drop-filter", ref _lootDropFilter, 200))
        {
            RebuildVisibleLootDropEntries();
        }

        string[] kindOptions = ["All", "Blocks", "Entities", "Trades"];
        ImGui.SetNextItemWidth(130);
        if (ImGui.Combo("Kind##loot-drop-kind", ref _lootDropKindFilter, kindOptions, kindOptions.Length))
        {
            RebuildVisibleLootDropEntries();
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("Dirty only##loot-drop-dirty", ref _lootDropDirtyOnly))
        {
            RebuildVisibleLootDropEntries();
        }

        if (ImGui.InputText("Domain##loot-drop-domain", ref _lootDropDomainFilter, 80))
        {
            RebuildVisibleLootDropEntries();
        }

        if (ImGui.Button("Reload index##loot-drop-reload", new NVector2(-1, 0)))
        {
            _lootDropIndexed = false;
            _lootDropLoadedKey = "";
            _lootDropOriginalJson = "";
            _lootDropTradeJson = "";
            _lootDropLiveAppliedHash = "";
            _lootDropDrafts.Clear();
            EnsureLootDropEntriesIndexed();
        }

        ImGui.TextDisabled($"{_visibleLootDropEntries.Count} / {_lootDropEntries.Count}");
        if (_visibleLootDropEntries.Count == 0)
        {
            ImGui.TextDisabled("No matching loot/drop sources.");
            ImGui.EndChild();
            return;
        }

        _lootDropEntryIndex = Math.Clamp(_lootDropEntryIndex, 0, _visibleLootDropEntries.Count - 1);
        if (ImGui.BeginListBox("##loot-drop-sources", new NVector2(-float.Epsilon, Math.Max(140f, ImGui.GetContentRegionAvail().Y))))
        {
            for (int index = 0; index < _visibleLootDropEntries.Count; index++)
            {
                LootDropEntry entry = _visibleLootDropEntries[index];
                bool selected = index == _lootDropEntryIndex;
                string marker = IsLootDropDirty && string.Equals(entry.Key, _lootDropLoadedKey, StringComparison.OrdinalIgnoreCase) ? "*" : "";
                if (ImGui.Selectable($"{marker}{entry.Label}##loot-drop-entry-{index}", selected))
                {
                    _lootDropEntryIndex = index;
                    LoadLootDropEntry(entry, keepDirty: false);
                }

                if (ImGui.IsItemHovered()) ImGui.SetTooltip(entry.Tooltip);
            }

            ImGui.EndListBox();
        }

        ImGui.EndChild();
    }

    private void DrawLootDropEditorPanel(NVector2 size)
    {
        ImGui.BeginChild("##loot-drop-editor", size, true);
        LootDropEntry? entry = SelectedLootDropEntry;
        if (entry == null)
        {
            ImGui.TextDisabled("Select a loot/drop source.");
            ImGui.EndChild();
            return;
        }

        EnsureLootDropEntryLoaded(entry);
        ImGui.SeparatorText(entry.Label);

        if (entry.Kind == LootDropKind.TradeTable)
        {
            DrawTradeTableEditor(entry);
        }
        else
        {
            DrawBlockEntityDropEditor(entry);
        }

        ImGui.EndChild();
    }

    private void DrawBlockEntityDropEditor(LootDropEntry entry)
    {
        ImGui.TextDisabled("Block/entity drops. Quantities use Vintage Story NatFloat fields.");
        if (ImGui.Button("Add drop##loot-drop-add"))
        {
            _lootDropDrafts.Add(LootDropDraft.CreateDefault());
            _lootDropSelectedDraftIndex = _lootDropDrafts.Count - 1;
            OnLootDropDraftChanged();
        }

        ImGui.SameLine();
        bool canRemove = _lootDropDrafts.Count > 0 && _lootDropSelectedDraftIndex >= 0 && _lootDropSelectedDraftIndex < _lootDropDrafts.Count;
        if (!canRemove) ImGui.BeginDisabled();
        if (ImGui.Button("Remove selected##loot-drop-remove"))
        {
            _lootDropDrafts.RemoveAt(_lootDropSelectedDraftIndex);
            _lootDropSelectedDraftIndex = Math.Clamp(_lootDropSelectedDraftIndex, 0, Math.Max(0, _lootDropDrafts.Count - 1));
            OnLootDropDraftChanged();
        }
        if (!canRemove) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!canRemove) ImGui.BeginDisabled();
        if (ImGui.Button("Duplicate##loot-drop-duplicate"))
        {
            _lootDropDrafts.Insert(_lootDropSelectedDraftIndex + 1, _lootDropDrafts[_lootDropSelectedDraftIndex].Clone());
            _lootDropSelectedDraftIndex++;
            OnLootDropDraftChanged();
        }
        if (!canRemove) ImGui.EndDisabled();

        float leftWidth = Math.Min(270f * _devToolsUiScale, Math.Max(180f, ImGui.GetContentRegionAvail().X * 0.35f));
        ImGui.BeginChild("##loot-drop-list", new NVector2(leftWidth, Math.Max(160f, ImGui.GetContentRegionAvail().Y)), true);
        for (int index = 0; index < _lootDropDrafts.Count; index++)
        {
            LootDropDraft draft = _lootDropDrafts[index];
            string weightLabel = IsWeightedLootDrop(draft) ? $" w{draft.Weight:0.###}" : "";
            if (ImGui.Selectable($"{index}: {draft.Type}:{draft.Code} x{draft.QuantityAvg:0.##}{weightLabel}##loot-drop-row-{index}", index == _lootDropSelectedDraftIndex))
            {
                _lootDropSelectedDraftIndex = index;
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##loot-drop-row-editor", new NVector2(0, Math.Max(160f, ImGui.GetContentRegionAvail().Y)), true);
        if (_lootDropDrafts.Count == 0)
        {
            ImGui.TextDisabled("No drops configured.");
        }
        else
        {
            _lootDropSelectedDraftIndex = Math.Clamp(_lootDropSelectedDraftIndex, 0, _lootDropDrafts.Count - 1);
            LootDropDraft draft = _lootDropDrafts[_lootDropSelectedDraftIndex];
            if (DrawLootDropDraftEditor(draft, _lootDropSelectedDraftIndex))
            {
                _lootDropDrafts[_lootDropSelectedDraftIndex] = draft;
                OnLootDropDraftChanged();
            }
        }
        ImGui.EndChild();
    }

    private bool DrawLootDropDraftEditor(LootDropDraft draft, int index)
    {
        bool changed = false;
        string[] typeNames = Enum.GetNames<EnumItemClass>();
        int typeIndex = Math.Max(0, Array.FindIndex(typeNames, name => string.Equals(name, draft.Type, StringComparison.OrdinalIgnoreCase)));
        ImGui.SetNextItemWidth(140);
        if (ImGui.Combo($"Type##loot-drop-type-{index}", ref typeIndex, typeNames, typeNames.Length))
        {
            draft.Type = typeNames[Math.Clamp(typeIndex, 0, typeNames.Length - 1)];
            changed = true;
        }

        ImGui.SetNextItemWidth(-float.Epsilon);
        changed |= ImGui.InputText($"Code##loot-drop-code-{index}", ref draft.Code, 240);

        if (DrawFloatField($"Weight##loot-drop-weight-{index}", ref draft.Weight, 0.01f))
        {
            draft.Weight = Math.Max(0f, draft.Weight);
            changed = true;
        }
        if (IsWeightedLootDrop(draft))
        {
            ImGui.TextColored(new NVector4(1f, 0.78f, 0.32f, 1f), "Weighted drop. Weight is preserved in duplicate, runtime apply, and authored save.");
        }

        changed |= DrawFloatField($"Quantity avg##loot-drop-qavg-{index}", ref draft.QuantityAvg, 0.05f);
        changed |= DrawFloatField($"Quantity var##loot-drop-qvar-{index}", ref draft.QuantityVar, 0.05f);
        changed |= DrawFloatField($"Quantity offset##loot-drop-qoffset-{index}", ref draft.QuantityOffset, 0.05f);

        string[] distributions = Enum.GetNames<EnumDistribution>();
        int distIndex = Math.Max(0, Array.FindIndex(distributions, name => string.Equals(name, draft.QuantityDist, StringComparison.OrdinalIgnoreCase)));
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo($"Distribution##loot-drop-dist-{index}", ref distIndex, distributions, distributions.Length))
        {
            draft.QuantityDist = distributions[Math.Clamp(distIndex, 0, distributions.Length - 1)];
            changed = true;
        }

        string[] toolOptions = ["", .. Enum.GetNames<EnumTool>()];
        int toolIndex = Math.Max(0, Array.FindIndex(toolOptions, name => string.Equals(name, draft.Tool, StringComparison.OrdinalIgnoreCase)));
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo($"Tool##loot-drop-tool-{index}", ref toolIndex, toolOptions, toolOptions.Length))
        {
            draft.Tool = toolOptions[Math.Clamp(toolIndex, 0, toolOptions.Length - 1)];
            changed = true;
        }

        changed |= ImGui.Checkbox($"Last drop##loot-drop-last-{index}", ref draft.LastDrop);
        changed |= ImGui.InputText($"Drop stat modifier##loot-drop-stat-{index}", ref draft.DropModbyStat, 160);

        if (ImGui.TreeNode($"Attributes JSON##loot-drop-attrs-{index}"))
        {
            changed |= ImGui.InputTextMultiline($"##loot-drop-attrs-text-{index}", ref draft.AttributesJson, 64 * 1024, new NVector2(-float.Epsilon, 100f), ImGuiInputTextFlags.AllowTabInput);
            ImGui.TreePop();
        }

        return changed;
    }

    private void DrawTradeTableEditor(LootDropEntry entry)
    {
        ImGui.TextDisabled($"Trade token path: {string.Join(".", entry.TradePath)}");
        JToken? tradeToken = TryParseJsonToken(_lootDropTradeJson);
        if (tradeToken is JObject tradeObject)
        {
            DrawTradeListEditor(tradeObject, "Buying", "buying");
            DrawTradeListEditor(tradeObject, "Selling", "selling");
        }

        ImGui.SeparatorText("Trade JSON");
        if (ImGui.InputTextMultiline("##loot-trade-json", ref _lootDropTradeJson, 1024 * 1024, new NVector2(-float.Epsilon, Math.Max(180f, ImGui.GetContentRegionAvail().Y)), ImGuiInputTextFlags.AllowTabInput))
        {
            OnLootDropDraftChanged();
        }
    }

    private void DrawTradeListEditor(JObject tradeObject, string label, string propertyName)
    {
        JObject listObject = tradeObject[propertyName] as JObject ?? new JObject();
        tradeObject[propertyName] = listObject;
        if (!ImGui.TreeNode($"{label}##loot-trade-{propertyName}")) return;

        int maxItems = listObject["maxItems"]?.Value<int?>() ?? 0;
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputInt($"Max items##loot-trade-max-{propertyName}", ref maxItems))
        {
            listObject["maxItems"] = Math.Max(0, maxItems);
            _lootDropTradeJson = tradeObject.ToString(Formatting.Indented);
            OnLootDropDraftChanged();
        }

        JArray list = listObject["list"] as JArray ?? new JArray();
        listObject["list"] = list;
        if (ImGui.Button($"Add {label.ToLowerInvariant()} item##loot-trade-add-{propertyName}"))
        {
            list.Add(new JObject
            {
                ["type"] = "item",
                ["code"] = "",
                ["stacksize"] = 1,
                ["price"] = new JObject { ["avg"] = 1, ["var"] = 0 },
                ["stock"] = new JObject { ["avg"] = 1, ["var"] = 0 }
            });
            _lootDropTradeJson = tradeObject.ToString(Formatting.Indented);
            OnLootDropDraftChanged();
        }

        for (int index = 0; index < list.Count; index++)
        {
            if (list[index] is not JObject item) continue;
            if (!ImGui.TreeNode($"{index}: {item["type"]}:{item["code"]}##loot-trade-item-{propertyName}-{index}")) continue;

            bool changed = false;
            changed |= EditJsonString(item, "type", $"Type##loot-trade-type-{propertyName}-{index}", 80);
            changed |= EditJsonString(item, "code", $"Code##loot-trade-code-{propertyName}-{index}", 240);
            changed |= EditJsonInt(item, "stacksize", $"Stack size##loot-trade-stack-{propertyName}-{index}", 1, 9999);
            JObject price = item["price"] as JObject ?? new JObject();
            item["price"] = price;
            JObject stock = item["stock"] as JObject ?? new JObject();
            item["stock"] = stock;
            changed |= EditJsonFloat(price, "avg", $"Price avg##loot-trade-price-avg-{propertyName}-{index}", 0, 1000000);
            changed |= EditJsonFloat(price, "var", $"Price var##loot-trade-price-var-{propertyName}-{index}", 0, 1000000);
            changed |= EditJsonFloat(stock, "avg", $"Stock avg##loot-trade-stock-avg-{propertyName}-{index}", 0, 1000000);
            changed |= EditJsonFloat(stock, "var", $"Stock var##loot-trade-stock-var-{propertyName}-{index}", 0, 1000000);

            if (ImGui.Button($"Remove##loot-trade-remove-{propertyName}-{index}"))
            {
                list.RemoveAt(index);
                changed = true;
                ImGui.TreePop();
                _lootDropTradeJson = tradeObject.ToString(Formatting.Indented);
                OnLootDropDraftChanged();
                break;
            }

            if (changed)
            {
                _lootDropTradeJson = tradeObject.ToString(Formatting.Indented);
                OnLootDropDraftChanged();
            }

            ImGui.TreePop();
        }

        ImGui.TreePop();
    }

    private void DrawLootDropInspector(NVector2 size, bool showDiagnostics)
    {
        ImGui.BeginChild("##loot-drop-inspector", size, true);
        LootDropEntry? entry = SelectedLootDropEntry;
        if (entry == null)
        {
            ImGui.TextDisabled("Select a loot/drop source.");
            ImGui.EndChild();
            return;
        }

        EnsureLootDropEntryLoaded(entry);
        bool valid = entry.Kind == LootDropKind.TradeTable
            ? TryParseJsonToken(_lootDropTradeJson) != null
            : TryBuildRuntimeDrops(out _, out _);

        ImGui.SeparatorText("Status");
        ImGui.TextDisabled($"{entry.KindLabel}: {entry.Code}");
        ImGui.TextDisabled($"Domain: {entry.Domain}");
        ImGui.TextColored(valid ? new NVector4(0.42f, 0.85f, 0.42f, 1f) : new NVector4(1f, 0.38f, 0.32f, 1f), valid ? "Data valid" : "Data invalid");
        ImGui.TextWrapped(entry.SourceAsset?.Location.ToString() ?? "Source asset: unresolved; save will create an authored override.");
        if (entry.Kind != LootDropKind.TradeTable && HasWeightedLootDrops(_lootDropDrafts))
        {
            ImGui.TextColored(new NVector4(1f, 0.78f, 0.32f, 1f), "Weighted drops detected. Weight is editable and preserved; simulation still treats rows as independent rolls.");
        }

        if (entry.Kind != LootDropKind.TradeTable)
        {
            DrawLootDropSimulationControls();
        }

        ImGui.SeparatorText("Runtime");
        bool runtimeAvailable = entry.Kind != LootDropKind.TradeTable;
        if (!runtimeAvailable) ImGui.BeginDisabled();
        if (ImGui.Button("Apply runtime##loot-drop-runtime"))
        {
            ApplyLootDropRuntime(force: true);
        }
        if (!runtimeAvailable) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Applies block/entity Drops arrays for this session. Trade table editing is source-backed in this pass.");
        }

        ImGui.SameLine();
        bool canRevert = runtimeAvailable && _liveApplyManager.CanRevert(GetLootDropLiveKey(entry));
        if (!canRevert) ImGui.BeginDisabled();
        if (ImGui.Button("Revert runtime##loot-drop-revert"))
        {
            _lootDropLiveAppliedHash = "";
            _lootDropStatus = _liveApplyManager.Revert(GetLootDropLiveKey(entry));
        }
        if (!canRevert) ImGui.EndDisabled();

        if (!valid) ImGui.BeginDisabled();
        if (ImGui.Button("Save authored JSON##loot-drop-save"))
        {
            QueueSourceSave(TrySaveLootDropToSource(entry), status => _lootDropStatus = status);
        }
        if (!valid) ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(_lootDropStatus))
        {
            ImGui.TextWrapped(_lootDropStatus);
        }

        _lootDropDiagnostics.Draw("loot-drop", showDiagnostics);
        ImGui.EndChild();
    }

    private void DrawLootDropSimulationControls()
    {
        ImGui.SeparatorText("Simulation");
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputInt("Runs##loot-drop-runs", ref _lootDropSimulationRuns))
        {
            _lootDropSimulationRuns = Math.Clamp(_lootDropSimulationRuns, 1, 100000);
        }

        if (ImGui.Button("Simulate drops##loot-drop-simulate"))
        {
            _lootDropSimulationText = SimulateLootDrops(_lootDropDrafts, _lootDropSimulationRuns);
        }

        if (!string.IsNullOrWhiteSpace(_lootDropSimulationText))
        {
            ImGui.InputTextMultiline("##loot-drop-simulation-output", ref _lootDropSimulationText, (uint)Math.Max(_lootDropSimulationText.Length + 1, 1024), new NVector2(-float.Epsilon, 150f), ImGuiInputTextFlags.ReadOnly);
        }
    }

    private void EnsureLootDropEntryLoaded(LootDropEntry entry)
    {
        if (string.Equals(_lootDropLoadedKey, entry.Key, StringComparison.Ordinal)) return;
        LoadLootDropEntry(entry, keepDirty: true);
    }

    private void LoadLootDropEntry(LootDropEntry entry, bool keepDirty)
    {
        if (keepDirty && IsLootDropDirty && !string.IsNullOrWhiteSpace(_lootDropLoadedKey)) return;

        try
        {
            _lootDropDrafts.Clear();
            _lootDropTradeJson = "";
            _lootDropSimulationText = "";
            if (entry.Kind == LootDropKind.TradeTable)
            {
                _lootDropTradeJson = (entry.TradeToken?.DeepClone() ?? new JObject()).ToString(Formatting.Indented);
            }
            else
            {
                foreach (JToken token in BuildSourceDropArray(entry))
                {
                    _lootDropDrafts.Add(LootDropDraft.FromToken(token));
                }
            }

            _lootDropLoadedKey = entry.Key;
            _lootDropOriginalJson = CurrentLootDropJson();
            _lootDropSelectedDraftIndex = Math.Clamp(_lootDropSelectedDraftIndex, 0, Math.Max(0, _lootDropDrafts.Count - 1));
            _lootDropLiveAppliedHash = "";
            _lootDropStatus = HasWeightedLootDrops(_lootDropDrafts)
                ? $"Loaded {entry.Label}. Weighted drops are editable and preserved; simulation remains approximate."
                : $"Loaded {entry.Label}.";
        }
        catch (Exception exception)
        {
            _lootDropDiagnostics.Exception($"Failed to load {entry.Label}", exception);
            _lootDropStatus = $"Failed to load {entry.Label}: {exception.Message}";
        }
    }

    private JArray BuildSourceDropArray(LootDropEntry entry)
    {
        if (entry.SourceJson?["drops"] is JArray sourceDrops)
        {
            return (JArray)sourceDrops.DeepClone();
        }

        if (entry.Kind == LootDropKind.BlockDrops && entry.Block?.Drops != null)
        {
            return BuildRuntimeDropArray(entry.Block.Drops);
        }

        if (entry.Kind == LootDropKind.EntityDrops && entry.Entity?.Drops != null)
        {
            return BuildRuntimeDropArray(entry.Entity.Drops);
        }

        return new JArray();
    }

    private static JArray BuildRuntimeDropArray(IEnumerable<BlockDropItemStack> drops)
    {
        JArray result = new();
        foreach (BlockDropItemStack drop in drops)
        {
            result.Add(BuildRuntimeDropToken(drop));
        }

        return result;
    }

    private static JObject BuildRuntimeDropToken(BlockDropItemStack drop)
    {
        NatFloat quantity = drop.Quantity ?? NatFloat.One;
        string code = GetRuntimeDropCode(drop);
        JObject token = new()
        {
            ["type"] = GetRuntimeDropType(drop, code),
            ["code"] = code,
            ["quantity"] = new JObject
            {
                ["offset"] = quantity.offset,
                ["avg"] = quantity.avg,
                ["var"] = quantity.var,
                ["dist"] = quantity.dist.ToString()
            }
        };

        if (drop.Attributes?.Token != null)
        {
            token["attributes"] = drop.Attributes.Token.DeepClone();
        }
        if (drop.LastDrop) token["lastDrop"] = true;
        if (drop.Tool != null) token["tool"] = drop.Tool.Value.ToString();
        if (!string.IsNullOrWhiteSpace(drop.DropModbyStat)) token["dropModbyStat"] = drop.DropModbyStat;
        if (drop is WeightedBlockDropItemstack weighted && Math.Abs(weighted.Weight - 1f) > 0.0001f)
        {
            token["weight"] = weighted.Weight;
        }

        return token;
    }

    private static string GetRuntimeDropCode(BlockDropItemStack drop)
    {
        string? code = drop.Code?.ToString();
        if (!string.IsNullOrWhiteSpace(code)) return code;

        return drop.ResolvedItemstack?.Collectible?.Code?.ToString() ?? "";
    }

    private static string GetRuntimeDropType(BlockDropItemStack drop, string resolvedCode)
    {
        if (drop.ResolvedItemstack != null && string.IsNullOrWhiteSpace(drop.Code?.ToString()) && !string.IsNullOrWhiteSpace(resolvedCode))
        {
            return drop.ResolvedItemstack.Class.ToString();
        }

        return drop.Type.ToString();
    }

    private string CurrentLootDropJson()
    {
        LootDropEntry? entry = SelectedLootDropEntry;
        if (entry?.Kind == LootDropKind.TradeTable) return NormalizeJsonForHash(_lootDropTradeJson);
        return BuildDropDraftArray().ToString(Formatting.None);
    }

    private void OnLootDropDraftChanged()
    {
        RebuildVisibleLootDropEntries();
        _lootDropStatus = "Loot/drop data edited.";
        if (_liveApplyManager.AutoApply)
        {
            ApplyLootDropRuntime(force: false);
        }
    }

    private void ApplyLootDropRuntime(bool force = false)
    {
        LootDropEntry? entry = SelectedLootDropEntry;
        if (entry == null)
        {
            _liveApplyManager.LastStatus = "No selected loot/drop source.";
            return;
        }

        EnsureLootDropEntryLoaded(entry);
        if (entry.Kind == LootDropKind.TradeTable)
        {
            _lootDropStatus = "Runtime apply skipped: trade table editing is source-backed in this pass.";
            _liveApplyManager.LastStatus = _lootDropStatus;
            return;
        }

        if (!TryBuildRuntimeDrops(out BlockDropItemStack[] drops, out string error))
        {
            _lootDropStatus = $"Runtime apply skipped: {error}";
            _liveApplyManager.LastStatus = _lootDropStatus;
            return;
        }

        string hash = BuildDropDraftArray().ToString(Formatting.None);
        if (!force && string.Equals(_lootDropLiveAppliedHash, hash, StringComparison.Ordinal)) return;

        string key = GetLootDropLiveKey(entry);
        _lootDropStatus = _liveApplyManager.Apply(
            key,
            entry.Label,
            () => CaptureLootDropLiveSnapshot(entry),
            () => ApplyRuntimeDrops(entry, drops),
            $"Live applied {drops.Length} drop row(s) for {entry.Label}.");
        _lootDropLiveAppliedHash = hash;
    }

    private bool TryBuildRuntimeDrops(out BlockDropItemStack[] drops, out string error)
    {
        try
        {
            JArray token = BuildDropDraftArray();
            drops = new JsonObject(token).AsObject<BlockDropItemStack[]>() ?? [];
            foreach (BlockDropItemStack drop in drops)
            {
                if (drop.Code != null)
                {
                    drop.Resolve(_api.World, "InGameDevTools loot editor", drop.Code);
                }
            }

            error = "";
            return true;
        }
        catch (Exception exception)
        {
            drops = [];
            error = exception.Message;
            return false;
        }
    }

    private void ApplyRuntimeDrops(LootDropEntry entry, BlockDropItemStack[] drops)
    {
        if (entry.Kind == LootDropKind.BlockDrops && entry.Block != null)
        {
            entry.Block.Drops = drops;
            return;
        }

        if (entry.Kind == LootDropKind.EntityDrops && entry.Entity != null)
        {
            entry.Entity.Drops = drops;
            entry.Entity.DropsPacket = null;
        }
    }

    private void ClearLootDropLiveApplyState()
    {
        _lootDropLiveAppliedHash = "";
    }

    private LivePatchSnapshot CaptureLootDropLiveSnapshot(LootDropEntry entry)
    {
        BlockDropItemStack[]? original = entry.Kind == LootDropKind.BlockDrops
            ? entry.Block?.Drops?.Select(drop => drop.Clone()).ToArray()
            : entry.Entity?.Drops?.Select(drop => drop.Clone()).ToArray();
        byte[]? entityDropsPacket = entry.Entity?.DropsPacket?.ToArray();

        return new(
            () =>
            {
                if (entry.Kind == LootDropKind.BlockDrops && entry.Block != null) entry.Block.Drops = original;
                if (entry.Kind == LootDropKind.EntityDrops && entry.Entity != null)
                {
                    entry.Entity.Drops = original;
                    entry.Entity.DropsPacket = entityDropsPacket;
                }
            },
            Path.Combine("assets", entry.Domain, "runtime-loot-drops", entry.Code.Replace(':', '_').Replace('/', '_') + ".json"),
            () => BuildRuntimeDropArray(original ?? []).ToString(Formatting.Indented),
            "loot-drops");
    }

    private SourceSaveResult TrySaveLootDropToSource(LootDropEntry entry)
    {
        try
        {
            JObject json = entry.SourceJson?.DeepClone() as JObject ?? CreateLootDropAuthoringDocument(entry);
            if (entry.Kind == LootDropKind.TradeTable)
            {
                JToken? tradeToken = TryParseJsonToken(_lootDropTradeJson);
                if (tradeToken == null) return SourceSaveResult.Fail("Save failed: trade JSON is invalid.");
                SetTokenAtPath(json, entry.TradePath, tradeToken);
            }
            else
            {
                json["drops"] = BuildDropDraftArray();
            }

            string domain = entry.SourceAsset?.Location.Domain ?? entry.Domain;
            string assetPath = entry.SourceAsset?.Location.Path ?? BuildLootDropFallbackAssetPath(entry);
            string outputPath = GetToolAuthoredAssetPath("loot-drops", Path.Combine("assets", domain, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            string sourceText = ReadAssetText(entry.SourceAsset);
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : sourceText;
            string newText = json.ToString(Formatting.Indented);
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved authored loot/drop JSON to {outputPath}.",
                () =>
                {
                    string result = WriteAuthoredFile(outputPath, newText);
                    _lootDropOriginalJson = CurrentLootDropJson();
                    RebuildVisibleLootDropEntries();
                    return result;
                });
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            _lootDropDiagnostics.Exception($"Save failed for {entry.Label}", exception);
            return SourceSaveResult.Fail($"Save failed for {entry.Label}: {exception.Message}");
        }
    }

    private static JObject CreateLootDropAuthoringDocument(LootDropEntry entry)
    {
        JObject json = new();
        if (!string.IsNullOrWhiteSpace(entry.Code)) json["code"] = entry.Code;
        return json;
    }

    private static string BuildLootDropFallbackAssetPath(LootDropEntry entry)
    {
        string code = entry.Code.Contains(':', StringComparison.Ordinal) ? entry.Code[(entry.Code.IndexOf(':') + 1)..] : entry.Code;
        string root = entry.Kind switch
        {
            LootDropKind.BlockDrops => "blocktypes",
            LootDropKind.EntityDrops => "entities",
            _ => "config/trades"
        };
        return $"{root}/{EnsureJsonFilePath(code.Replace(':', '_'))}";
    }

    private JArray BuildDropDraftArray()
    {
        JArray array = new();
        foreach (LootDropDraft draft in _lootDropDrafts)
        {
            array.Add(draft.ToToken());
        }

        return array;
    }

    private static string SimulateLootDrops(IReadOnlyList<LootDropDraft> drafts, int runs)
    {
        if (drafts.Count == 0) return "No drops configured.";

        bool hasWeightedDrops = HasWeightedLootDrops(drafts);
        Random random = new(8675309);
        Dictionary<string, double> totals = new(StringComparer.OrdinalIgnoreCase);
        for (int run = 0; run < runs; run++)
        {
            foreach (LootDropDraft draft in drafts)
            {
                double quantity = Math.Max(0, Math.Round(CreateNatFloat(draft).nextFloat(1f, random), MidpointRounding.AwayFromZero));
                if (quantity > 0)
                {
                    string key = $"{draft.Type}:{draft.Code}";
                    totals[key] = totals.TryGetValue(key, out double existing) ? existing + quantity : quantity;
                }

                if (draft.LastDrop && quantity > 0) break;
            }
        }

        string prefix = hasWeightedDrops
            ? "Weighted drops are preserved, but this simulation still treats rows as independent rolls until weighted-group simulation is implemented." + Environment.NewLine
            : "";

        if (totals.Count == 0) return $"{prefix}Simulated {runs} run(s): no drops.";

        return prefix + string.Join(Environment.NewLine, totals
            .OrderByDescending(entry => entry.Value)
            .Select(entry => $"{entry.Key}: total {entry.Value:0.##}, avg/run {entry.Value / runs:0.###}"));
    }

    private static NatFloat CreateNatFloat(LootDropDraft draft)
    {
        EnumDistribution dist = Enum.TryParse(draft.QuantityDist, ignoreCase: true, out EnumDistribution parsed)
            ? parsed
            : EnumDistribution.UNIFORM;
        NatFloat value = NatFloat.create(dist, draft.QuantityAvg, draft.QuantityVar);
        value.offset = draft.QuantityOffset;
        return value;
    }

    private static bool HasWeightedLootDrops(IEnumerable<LootDropDraft> drafts)
    {
        return drafts.Any(IsWeightedLootDrop);
    }

    private static bool IsWeightedLootDrop(LootDropDraft draft)
    {
        return Math.Abs(draft.Weight - DefaultLootDropWeight) > LootDropWeightEpsilon;
    }

    private static bool TryFindTradeToken(JObject sourceJson, out List<string> path, out JToken? token)
    {
        foreach (string key in new[] { "tradeProps", "tradeProperties", "trades" })
        {
            if (TryFindPropertyRecursive(sourceJson, key, [], out path, out token)) return true;
        }

        path = [];
        token = null;
        return false;
    }

    private static bool TryFindPropertyRecursive(JToken token, string propertyName, List<string> currentPath, out List<string> path, out JToken? value)
    {
        if (token is JObject obj)
        {
            foreach (JProperty property in obj.Properties())
            {
                List<string> nextPath = [.. currentPath, property.Name];
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    path = nextPath;
                    value = property.Value;
                    return true;
                }

                if (TryFindPropertyRecursive(property.Value, propertyName, nextPath, out path, out value)) return true;
            }
        }
        else if (token is JArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                if (TryFindPropertyRecursive(array[index], propertyName, [.. currentPath, index.ToString()], out path, out value)) return true;
            }
        }

        path = [];
        value = null;
        return false;
    }

    private static JToken? TryParseJsonToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JToken.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    private static void SetTokenAtPath(JObject root, IReadOnlyList<string> path, JToken value)
    {
        if (path.Count == 0) return;
        JToken current = root;
        for (int index = 0; index < path.Count - 1; index++)
        {
            string part = path[index];
            if (current is JObject obj)
            {
                current = obj[part] ??= new JObject();
            }
            else if (current is JArray array && int.TryParse(part, out int arrayIndex) && arrayIndex >= 0 && arrayIndex < array.Count)
            {
                current = array[arrayIndex];
            }
            else
            {
                return;
            }
        }

        string final = path[^1];
        if (current is JObject finalObject)
        {
            finalObject[final] = value;
        }
        else if (current is JArray finalArray && int.TryParse(final, out int finalIndex) && finalIndex >= 0 && finalIndex < finalArray.Count)
        {
            finalArray[finalIndex] = value;
        }
    }

    private IAsset? FindEntitySourceAsset(EntityProperties entityType)
    {
        if (entityType.Code == null) return null;
        string domain = entityType.Code.Domain;
        string path = entityType.Code.Path;
        IAsset? bestAsset = null;
        int bestScore = -1;

        foreach (IAsset asset in _api.Assets.AllAssets.Values)
        {
            if (asset?.Location == null) continue;
            string assetPath = asset.Location.Path.Replace('\\', '/');
            if (!assetPath.StartsWith("entities/", StringComparison.OrdinalIgnoreCase) ||
                !assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(asset.Location.Domain, domain, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            JObject? json = TryParseJsonObject(ReadAssetText(asset));
            string? code = json?["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code)) continue;
            if (code.Contains(':', StringComparison.Ordinal)) code = code[(code.IndexOf(':') + 1)..];

            int score = -1;
            if (string.Equals(code, path, StringComparison.OrdinalIgnoreCase)) score = 10000 + code.Length;
            else if (path.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase)) score = 1000 + code.Length;
            else if (path.Contains(code, StringComparison.OrdinalIgnoreCase)) score = 100 + code.Length;
            if (score > bestScore)
            {
                bestScore = score;
                bestAsset = asset;
            }
        }

        return bestAsset;
    }

    private static bool EditJsonString(JObject obj, string property, string label, int maxLength)
    {
        string value = obj[property]?.ToString() ?? "";
        if (!ImGui.InputText(label, ref value, (uint)maxLength)) return false;
        obj[property] = value;
        return true;
    }

    private static bool EditJsonInt(JObject obj, string property, string label, int min, int max)
    {
        int value = obj[property]?.Value<int?>() ?? min;
        ImGui.SetNextItemWidth(110);
        if (!ImGui.InputInt(label, ref value)) return false;
        obj[property] = Math.Clamp(value, min, max);
        return true;
    }

    private static bool EditJsonFloat(JObject obj, string property, string label, float min, float max)
    {
        float value = obj[property]?.Value<float?>() ?? min;
        ImGui.SetNextItemWidth(110);
        if (!ImGui.DragFloat(label, ref value, 0.05f)) return false;
        obj[property] = Math.Clamp(value, min, max);
        return true;
    }

    private static bool DrawFloatField(string label, ref float value, float speed)
    {
        ImGui.SetNextItemWidth(120);
        return ImGui.DragFloat(label, ref value, speed);
    }

    private static string NormalizeJsonForHash(string text)
    {
        return TryParseJsonToken(text)?.ToString(Formatting.None) ?? text;
    }

    private static string GetLootDropLiveKey(LootDropEntry entry) => $"loot-drop:{entry.Key}";

    private enum LootDropKind
    {
        BlockDrops,
        EntityDrops,
        TradeTable
    }

    private sealed record LootDropEntry(
        LootDropKind Kind,
        string Key,
        string Label,
        string Code,
        string Domain,
        string SearchText,
        string Tooltip,
        Block? Block,
        EntityProperties? Entity,
        IAsset? SourceAsset,
        JObject? SourceJson,
        List<string> TradePath,
        JToken? TradeToken)
    {
        public string KindLabel => Kind switch
        {
            LootDropKind.BlockDrops => "Block drops",
            LootDropKind.EntityDrops => "Entity drops",
            LootDropKind.TradeTable => "Trade table",
            _ => "Loot"
        };

        public static LootDropEntry ForBlock(Block block, IAsset? sourceAsset, JObject? sourceJson)
        {
            string code = block.Code?.ToString() ?? "unknown";
            string label = $"block:{ImGuiLayoutHelper.CompactAssetCode(code)}";
            return new(
                LootDropKind.BlockDrops,
                $"block:{code}",
                label,
                code,
                block.Code?.Domain ?? "game",
                $"{label} {code} block drops",
                $"Block drops: {code}",
                block,
                null,
                sourceAsset,
                sourceJson,
                [],
                null);
        }

        public static LootDropEntry ForEntity(EntityProperties entityType, IAsset? sourceAsset, JObject? sourceJson)
        {
            string code = entityType.Code?.ToString() ?? "unknown";
            string label = $"entity:{ImGuiLayoutHelper.CompactAssetCode(code)}";
            return new(
                LootDropKind.EntityDrops,
                $"entity:{code}",
                label,
                code,
                entityType.Code?.Domain ?? "game",
                $"{label} {code} entity drops",
                $"Entity drops: {code}",
                null,
                entityType,
                sourceAsset,
                sourceJson,
                [],
                null);
        }

        public static LootDropEntry ForTrade(IAsset sourceAsset, JObject sourceJson, List<string> tradePath, JToken tradeToken)
        {
            string code = sourceJson["code"]?.ToString() ?? sourceAsset.Location.Path;
            string label = $"trade:{ImGuiLayoutHelper.CompactAssetCode($"{sourceAsset.Location.Domain}:{code}")}";
            string pathText = string.Join(".", tradePath);
            return new(
                LootDropKind.TradeTable,
                $"trade:{sourceAsset.Location}:{pathText}",
                label,
                code,
                sourceAsset.Location.Domain,
                $"{label} {sourceAsset.Location} {code} {pathText} trade table",
                $"Trade table: {sourceAsset.Location}\nPath: {pathText}",
                null,
                null,
                sourceAsset,
                sourceJson,
                tradePath,
                tradeToken);
        }
    }

    private sealed class LootDropDraft
    {
        public string Type = EnumItemClass.Item.ToString();
        public string Code = "";
        public float QuantityOffset;
        public float QuantityAvg = 1;
        public float QuantityVar;
        public string QuantityDist = EnumDistribution.UNIFORM.ToString();
        public float Weight = DefaultLootDropWeight;
        public string AttributesJson = "";
        public bool LastDrop;
        public string Tool = "";
        public string DropModbyStat = "";

        public static LootDropDraft CreateDefault() => new();

        public LootDropDraft Clone()
        {
            return new()
            {
                Type = Type,
                Code = Code,
                QuantityOffset = QuantityOffset,
                QuantityAvg = QuantityAvg,
                QuantityVar = QuantityVar,
                QuantityDist = QuantityDist,
                Weight = Weight,
                AttributesJson = AttributesJson,
                LastDrop = LastDrop,
                Tool = Tool,
                DropModbyStat = DropModbyStat
            };
        }

        public static LootDropDraft FromToken(JToken token)
        {
            JObject obj = token as JObject ?? new JObject();
            JObject quantity = obj["quantity"] as JObject ?? new JObject();
            return new()
            {
                Type = obj["type"]?.ToString() ?? EnumItemClass.Item.ToString(),
                Code = obj["code"]?.ToString() ?? "",
                QuantityOffset = quantity["offset"]?.Value<float?>() ?? 0,
                QuantityAvg = quantity["avg"]?.Value<float?>() ?? 1,
                QuantityVar = quantity["var"]?.Value<float?>() ?? 0,
                QuantityDist = quantity["dist"]?.ToString() ?? EnumDistribution.UNIFORM.ToString(),
                Weight = Math.Max(0f, obj["weight"]?.Value<float?>() ?? DefaultLootDropWeight),
                AttributesJson = obj["attributes"]?.ToString(Formatting.Indented) ?? "",
                LastDrop = obj["lastDrop"]?.Value<bool?>() ?? false,
                Tool = obj["tool"]?.ToString() ?? "",
                DropModbyStat = obj["dropModbyStat"]?.ToString() ?? ""
            };
        }

        public JObject ToToken()
        {
            JObject obj = new()
            {
                ["type"] = Type,
                ["code"] = Code,
                ["quantity"] = new JObject
                {
                    ["offset"] = QuantityOffset,
                    ["avg"] = QuantityAvg,
                    ["var"] = QuantityVar,
                    ["dist"] = QuantityDist
                }
            };
            if (!string.IsNullOrWhiteSpace(AttributesJson) && TryParseJsonToken(AttributesJson) is JToken attributes)
            {
                obj["attributes"] = attributes;
            }
            if (Math.Abs(Weight - DefaultLootDropWeight) > LootDropWeightEpsilon)
            {
                obj["weight"] = Math.Max(0f, Weight);
            }
            if (LastDrop) obj["lastDrop"] = true;
            if (!string.IsNullOrWhiteSpace(Tool)) obj["tool"] = Tool;
            if (!string.IsNullOrWhiteSpace(DropModbyStat)) obj["dropModbyStat"] = DropModbyStat;
            return obj;
        }
    }
}
