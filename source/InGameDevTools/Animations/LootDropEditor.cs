using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
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
    private const int LootDropIndexBatchSize = 90;

    private static readonly HashSet<string> LootDropTradeFirstClassProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "type",
        "code",
        "stacksize",
        "stackSize",
        "quantity",
        "price",
        "stock",
        "attributes"
    };

    private static readonly string[] LootDropAdvancedFieldKindLabels =
    [
        "String",
        "Boolean",
        "Integer",
        "Float",
        "Object",
        "Array"
    ];

    private readonly List<LootDropEntry> _lootDropEntries = [];
    private readonly List<LootDropEntry> _visibleLootDropEntries = [];
    private readonly List<LootDropDraft> _lootDropDrafts = [];
    private readonly Dictionary<string, LootDropDraftState> _lootDropDraftStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Block> _lootDropIndexBlocks = [];
    private readonly List<EntityProperties> _lootDropIndexEntities = [];
    private readonly List<LootDropEntitySourceAsset> _lootDropIndexEntitySources = [];
    private readonly List<IAsset> _lootDropIndexTradeAssets = [];
    private readonly ImGuiThreePanelLayoutState _lootDropLayout = new(0.26f, 0.30f);
    private readonly DevToolsEditorDiagnostics _lootDropDiagnostics = new("Loot/Drops");
    private string _lootDropFilter = "";
    private string _lootDropDomainFilter = "";
    private int _lootDropKindFilter;
    private int _lootDropEntryIndex;
    private int _lootDropSelectedDraftIndex;
    private bool _lootDropDirtyOnly;
    private LootDropIndexState _lootDropIndexState;
    private LootDropIndexPhase _lootDropIndexPhase;
    private int _lootDropIndexBlockIndex;
    private int _lootDropIndexEntityIndex;
    private int _lootDropIndexEntityTradeIndex;
    private int _lootDropIndexTradeAssetIndex;
    private LootDropEntitySourceIndex? _lootDropEntitySourceIndex;
    private string _lootDropLoadedKey = "";
    private string _lootDropOriginalJson = "";
    private string _lootDropTradeJson = "";
    private string _lootDropStatus = "";
    private string _lootDropLiveAppliedHash = "";
    private string _lootDropCurrentJson = "";
    private bool _lootDropDirtyCached;
    private bool _lootDropDataValid;
    private string _lootDropValidationStatus = "No loot/drop source loaded.";
    private BlockDropItemStack[] _lootDropValidatedRuntimeDrops = [];
    private int _lootDropSimulationRuns = 1000;
    private string _lootDropSimulationText = "";
    private readonly Dictionary<string, string> _lootDropJsonFieldBuffers = new(StringComparer.Ordinal);
    private string _lootDropNewTradeFieldName = "";
    private int _lootDropNewTradeFieldKindIndex;

    private void LootDropEditorTab(float deltaSeconds, bool showDiagnostics)
    {
        _ = deltaSeconds;
        ClearActiveTransformGizmo();

        try
        {
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
        catch (Exception exception)
        {
            _lootDropIndexState = LootDropIndexState.Failed;
            _lootDropStatus = $"Loot/drop editor error: {exception.Message}";
            _lootDropDiagnostics.Exception("Loot/drop editor failed", exception);
            _api.Logger.Error("[InGameDevTools] Loot/drop editor failed: {0}", exception);
            ImGui.TextWrapped(_lootDropStatus);
            _lootDropDiagnostics.Draw("loot-drop-error", showDiagnostics);
        }
    }

    private void ResetLootDropLayout()
    {
        _lootDropLayout.Reset();
    }

    private void EnsureLootDropEntriesIndexed()
    {
        if (_lootDropIndexState == LootDropIndexState.Ready || _lootDropIndexState == LootDropIndexState.Failed) return;
        if (_lootDropIndexState == LootDropIndexState.Idle)
        {
            StartLootDropIndexing(clearLoaded: false);
        }

        ProcessLootDropIndexBatch();
    }

    private void StartLootDropIndexing(bool clearLoaded)
    {
        _lootDropIndexState = LootDropIndexState.Indexing;
        _lootDropIndexPhase = LootDropIndexPhase.Blocks;
        _lootDropIndexBlockIndex = 0;
        _lootDropIndexEntityIndex = 0;
        _lootDropIndexEntityTradeIndex = 0;
        _lootDropIndexTradeAssetIndex = 0;
        _lootDropEntries.Clear();
        _visibleLootDropEntries.Clear();
        _lootDropEntryIndex = 0;
        _lootDropIndexBlocks.Clear();
        _lootDropIndexEntities.Clear();
        _lootDropIndexEntitySources.Clear();
        _lootDropIndexTradeAssets.Clear();

        if (clearLoaded)
        {
            _lootDropLoadedKey = "";
            _lootDropOriginalJson = "";
            _lootDropTradeJson = "";
            _lootDropLiveAppliedHash = "";
            _lootDropCurrentJson = "";
            _lootDropDirtyCached = false;
            _lootDropDataValid = false;
            _lootDropValidationStatus = "No loot/drop source loaded.";
            _lootDropValidatedRuntimeDrops = [];
            _lootDropSimulationText = "";
            _lootDropDrafts.Clear();
            _lootDropDraftStates.Clear();
            _lootDropJsonFieldBuffers.Clear();
            _lootDropSelectedDraftIndex = 0;
        }

        foreach (Block block in _api.World.Blocks)
        {
            if (block?.Code != null) _lootDropIndexBlocks.Add(block);
        }

        foreach (EntityProperties entityType in _api.World.EntityTypes ?? [])
        {
            if (entityType?.Code != null) _lootDropIndexEntities.Add(entityType);
        }

        List<IAsset> authoredLootAssets = CollectToolAuthoredAssets("loot-drops");
        _lootDropEntitySourceIndex = LootDropEntitySourceIndex.Build(_api, _lootDropDiagnostics, authoredLootAssets);
        _lootDropIndexEntitySources.AddRange(_lootDropEntitySourceIndex.Sources);

        HashSet<string> authoredLootLocations = new(StringComparer.OrdinalIgnoreCase);
        foreach (IAsset asset in authoredLootAssets)
        {
            authoredLootLocations.Add(asset.Location.ToString());
            if (IsLootDropTradeCandidateAsset(asset))
            {
                _lootDropIndexTradeAssets.Add(asset);
            }
        }

        foreach (IAsset asset in _api.Assets.AllAssets.Values)
        {
            if (asset.Location != null && authoredLootLocations.Contains(asset.Location.ToString())) continue;
            if (IsLootDropTradeCandidateAsset(asset))
            {
                _lootDropIndexTradeAssets.Add(asset);
            }
        }

        _lootDropIndexTradeAssets.Sort((left, right) => string.Compare(left.Location.ToString(), right.Location.ToString(), StringComparison.OrdinalIgnoreCase));
        _lootDropStatus = BuildLootDropIndexProgressText();
    }

    private void ProcessLootDropIndexBatch()
    {
        if (_lootDropIndexState != LootDropIndexState.Indexing) return;

        try
        {
            int processed = 0;
            while (processed < LootDropIndexBatchSize && _lootDropIndexState == LootDropIndexState.Indexing)
            {
                switch (_lootDropIndexPhase)
                {
                    case LootDropIndexPhase.Blocks:
                        if (_lootDropIndexBlockIndex >= _lootDropIndexBlocks.Count)
                        {
                            _lootDropIndexPhase = LootDropIndexPhase.Entities;
                            continue;
                        }

                        IndexLootDropBlock(_lootDropIndexBlocks[_lootDropIndexBlockIndex++]);
                        processed++;
                        break;

                    case LootDropIndexPhase.Entities:
                        if (_lootDropIndexEntityIndex >= _lootDropIndexEntities.Count)
                        {
                            _lootDropIndexPhase = LootDropIndexPhase.EntityTrades;
                            continue;
                        }

                        IndexLootDropEntity(_lootDropIndexEntities[_lootDropIndexEntityIndex++]);
                        processed++;
                        break;

                    case LootDropIndexPhase.EntityTrades:
                        if (_lootDropIndexEntityTradeIndex >= _lootDropIndexEntitySources.Count)
                        {
                            _lootDropIndexPhase = LootDropIndexPhase.TradeAssets;
                            continue;
                        }

                        IndexLootDropTradeAsset(_lootDropIndexEntitySources[_lootDropIndexEntityTradeIndex].Asset, _lootDropIndexEntitySources[_lootDropIndexEntityTradeIndex].SourceJson);
                        _lootDropIndexEntityTradeIndex++;
                        processed++;
                        break;

                    case LootDropIndexPhase.TradeAssets:
                        if (_lootDropIndexTradeAssetIndex >= _lootDropIndexTradeAssets.Count)
                        {
                            CompleteLootDropIndexing();
                            continue;
                        }

                        IndexLootDropTradeAsset(_lootDropIndexTradeAssets[_lootDropIndexTradeAssetIndex++], sourceJson: null);
                        processed++;
                        break;

                    default:
                        CompleteLootDropIndexing();
                        break;
                }
            }

            if (_lootDropIndexState == LootDropIndexState.Indexing)
            {
                _lootDropStatus = BuildLootDropIndexProgressText();
                RebuildVisibleLootDropEntries();
            }
        }
        catch (Exception exception)
        {
            _lootDropIndexState = LootDropIndexState.Failed;
            _lootDropStatus = $"Loot/drop indexing failed: {exception.Message}";
            _lootDropDiagnostics.Exception("Loot/drop indexing failed", exception);
            _api.Logger.Error("[InGameDevTools] Loot/drop indexing failed: {0}", exception);
        }
    }

    private void CompleteLootDropIndexing()
    {
        _lootDropEntries.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));
        RebuildVisibleLootDropEntries();
        _lootDropIndexState = LootDropIndexState.Ready;
        _lootDropStatus = $"Indexed {_lootDropEntries.Count} loot/drop source(s).";
    }

    private void IndexLootDropBlock(Block block)
    {
        if (block.Code == null) return;

        bool hasRuntimeDrops = (block.Drops?.Length ?? 0) > 0;
        IAsset? sourceAsset = FindCollectibleSourceAsset(block);
        JObject? sourceJson = hasRuntimeDrops ? null : TryReadLootDropAssetJson(sourceAsset);
        if (hasRuntimeDrops || sourceJson?["drops"] is JArray)
        {
            _lootDropEntries.Add(LootDropEntry.ForBlock(block, sourceAsset, sourceJson));
        }
    }

    private void IndexLootDropEntity(EntityProperties entityType)
    {
        if (entityType.Code == null) return;

        bool hasRuntimeDrops = (entityType.Drops?.Length ?? 0) > 0;
        LootDropEntitySourceAsset? source = _lootDropEntitySourceIndex?.Resolve(entityType);
        JObject? sourceJson = source?.SourceJson;
        if (hasRuntimeDrops || sourceJson?["drops"] is JArray)
        {
            _lootDropEntries.Add(LootDropEntry.ForEntity(entityType, source?.Asset, sourceJson));
        }
    }

    private void IndexLootDropTradeAsset(IAsset asset, JObject? sourceJson)
    {
        JObject? json = sourceJson ?? TryReadLootDropAssetJson(asset);
        if (json == null) return;
        if (!TryFindTradeToken(json, out List<string> path, out JToken? tradeToken) || tradeToken == null) return;

        _lootDropEntries.Add(LootDropEntry.ForTrade(asset, json, path, tradeToken));
    }

    private static bool IsLootDropTradeCandidateAsset(IAsset? asset)
    {
        if (asset?.Location == null) return false;
        string assetPath = asset.Location.Path.Replace('\\', '/');
        if (!assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        if (assetPath.StartsWith("entities/", StringComparison.OrdinalIgnoreCase)) return false;
        return assetPath.StartsWith("config/", StringComparison.OrdinalIgnoreCase) ||
            assetPath.Contains("trade", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildLootDropIndexProgressText()
    {
        return $"Indexing loot/drop sources: blocks {_lootDropIndexBlockIndex}/{_lootDropIndexBlocks.Count}, entities {_lootDropIndexEntityIndex}/{_lootDropIndexEntities.Count}, entity trades {_lootDropIndexEntityTradeIndex}/{_lootDropIndexEntitySources.Count}, trade assets {_lootDropIndexTradeAssetIndex}/{_lootDropIndexTradeAssets.Count}.";
    }

    private void RebuildVisibleLootDropEntries()
    {
        string filter = _lootDropFilter.Trim();
        LootDropEntry? selected = SelectedLootDropEntry;
        string loadedKey = _lootDropLoadedKey;
        bool loadedDirty = _lootDropDirtyCached;
        _visibleLootDropEntries.Clear();

        foreach (LootDropEntry entry in _lootDropEntries)
        {
            if (!ImGuiLayoutHelper.MatchesDomain(_lootDropDomainFilter, entry.Domain)) continue;
            if (_lootDropKindFilter == 1 && entry.Kind != LootDropKind.BlockDrops) continue;
            if (_lootDropKindFilter == 2 && entry.Kind != LootDropKind.EntityDrops) continue;
            if (_lootDropKindFilter == 3 && entry.Kind != LootDropKind.TradeTable) continue;
            if (_lootDropDirtyOnly && !IsLootDropEntryDirty(entry, loadedKey, loadedDirty)) continue;
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

    private bool IsLootDropDirty => _lootDropDirtyCached;

    private bool IsLootDropEntryDirty(LootDropEntry entry, string loadedKey, bool loadedDirty)
    {
        if (string.Equals(entry.Key, loadedKey, StringComparison.OrdinalIgnoreCase)) return loadedDirty;
        return _lootDropDraftStates.TryGetValue(entry.Key, out LootDropDraftState? state) && state.IsDirty;
    }

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
            StartLootDropIndexing(clearLoaded: true);
        }

        ImGui.TextDisabled($"{_visibleLootDropEntries.Count} / {_lootDropEntries.Count}");
        if (_lootDropIndexState == LootDropIndexState.Indexing)
        {
            ImGui.TextWrapped(_lootDropStatus);
            ImGui.EndChild();
            return;
        }

        if (_lootDropIndexState == LootDropIndexState.Failed)
        {
            ImGui.TextColored(new NVector4(1f, 0.38f, 0.32f, 1f), "Loot/drop indexing failed.");
            ImGui.TextWrapped(_lootDropStatus);
            ImGui.EndChild();
            return;
        }

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
                string marker = IsLootDropEntryDirty(entry, _lootDropLoadedKey, _lootDropDirtyCached) ? "*" : "";
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
        if (_lootDropIndexState == LootDropIndexState.Indexing)
        {
            ImGui.TextWrapped(_lootDropStatus);
            ImGui.EndChild();
            return;
        }

        if (_lootDropIndexState == LootDropIndexState.Failed)
        {
            ImGui.TextColored(new NVector4(1f, 0.38f, 0.32f, 1f), "Loot/drop index unavailable.");
            ImGui.TextWrapped(_lootDropStatus);
            ImGui.EndChild();
            return;
        }

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

        DrawLootDropGroupingPanel();

        float leftWidth = Math.Min(270f * _devToolsUiScale, Math.Max(180f, ImGui.GetContentRegionAvail().X * 0.35f));
        ImGui.BeginChild("##loot-drop-list", new NVector2(leftWidth, Math.Max(160f, ImGui.GetContentRegionAvail().Y)), true);
        Dictionary<int, LootDropWeightedGroupInfo> weightedGroupMap = BuildLootDropWeightedGroupMap(_lootDropDrafts);
        for (int index = 0; index < _lootDropDrafts.Count; index++)
        {
            LootDropDraft draft = _lootDropDrafts[index];
            string weightLabel = "";
            if (weightedGroupMap.TryGetValue(index, out LootDropWeightedGroupInfo groupInfo))
            {
                weightLabel = $" G{groupInfo.GroupNumber}:{GetLootDropWeightPercent(draft, groupInfo):0.#}%";
            }
            else if (IsWeightedLootDrop(draft))
            {
                weightLabel = $" w{draft.Weight:0.###}";
            }

            if (ImGui.Selectable($"{index}: {draft.Type}:{draft.Code} x{draft.QuantityAvg:0.##}{weightLabel}##loot-drop-row-{index}", index == _lootDropSelectedDraftIndex))
            {
                _lootDropSelectedDraftIndex = index;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(BuildLootDropRowTooltip(draft, index, weightedGroupMap));
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
            if (!string.IsNullOrWhiteSpace(draft.AttributesJson) && TryParseJsonToken(draft.AttributesJson) == null)
            {
                ImGui.TextColored(new NVector4(1f, 0.38f, 0.32f, 1f), "Attributes JSON is malformed.");
            }
            ImGui.TreePop();
        }

        return changed;
    }

    private void DrawLootDropGroupingPanel()
    {
        if (_lootDropDrafts.Count == 0) return;

        ImGuiTreeNodeFlags flags = HasWeightedLootDrops(_lootDropDrafts) || _lootDropDrafts.Any(HasLootDropConditionOrFlow)
            ? ImGuiTreeNodeFlags.DefaultOpen
            : ImGuiTreeNodeFlags.None;
        if (!ImGui.TreeNodeEx("Groups and conditions##loot-drop-groups", flags)) return;

        List<LootDropWeightedGroupInfo> groups = BuildLootDropWeightedGroups(_lootDropDrafts);
        if (groups.Count == 0 && !_lootDropDrafts.Any(HasLootDropConditionOrFlow))
        {
            ImGui.TextDisabled("No weighted groups, tool/stat gates, attributes, or last-drop stops.");
            ImGui.TreePop();
            return;
        }

        int groupCursor = 0;
        for (int index = 0; index < _lootDropDrafts.Count;)
        {
            if (groupCursor < groups.Count && groups[groupCursor].StartIndex == index)
            {
                LootDropWeightedGroupInfo group = groups[groupCursor++];
                string groupLabel = $"Weighted group {group.GroupNumber}: rows {group.StartIndex}-{group.EndExclusive - 1}, total weight {group.TotalWeight:0.###}##loot-drop-group-{group.GroupNumber}";
                if (ImGui.TreeNodeEx(groupLabel, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    for (int row = group.StartIndex; row < group.EndExclusive; row++)
                    {
                        DrawLootDropGroupingRow(row, _lootDropDrafts[row], group);
                    }
                    ImGui.TreePop();
                }

                index = group.EndExclusive;
                continue;
            }

            DrawLootDropGroupingRow(index, _lootDropDrafts[index], null);
            index++;
        }

        ImGui.TreePop();
    }

    private void DrawLootDropGroupingRow(int index, LootDropDraft draft, LootDropWeightedGroupInfo? group)
    {
        string chance = group.HasValue ? $" ({GetLootDropWeightPercent(draft, group.Value):0.#}% pick)" : "";
        bool selected = index == _lootDropSelectedDraftIndex;
        if (ImGui.Selectable($"{index}: {draft.Type}:{draft.Code}{chance}##loot-drop-group-row-{index}", selected))
        {
            _lootDropSelectedDraftIndex = index;
        }

        string conditionSummary = BuildLootDropConditionSummary(draft);
        if (!string.IsNullOrWhiteSpace(conditionSummary))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(conditionSummary);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(BuildLootDropRowTooltip(draft, index, BuildLootDropWeightedGroupMap(_lootDropDrafts)));
        }
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
            changed |= DrawTradeItemAttributesEditor(item, propertyName, index);
            changed |= DrawTradeItemAdvancedFieldsEditor(item, propertyName, index);

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

    private bool DrawTradeItemAttributesEditor(JObject item, string propertyName, int index)
    {
        string bufferKey = $"{_lootDropLoadedKey}:trade:{propertyName}:{index}:attributes";
        if (item["attributes"] == null)
        {
            _lootDropJsonFieldBuffers.Remove(bufferKey);
            if (!ImGui.Button($"Add attributes##loot-trade-add-attrs-{propertyName}-{index}")) return false;

            item["attributes"] = new JObject();
            _lootDropJsonFieldBuffers[bufferKey] = "{}";
            return true;
        }

        bool changed = false;
        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.DefaultOpen;
        if (ImGui.TreeNodeEx($"Attributes JSON##loot-trade-attrs-{propertyName}-{index}", flags))
        {
            if (!_lootDropJsonFieldBuffers.TryGetValue(bufferKey, out string? buffer))
            {
                buffer = item["attributes"]?.ToString(Formatting.Indented) ?? "{}";
            }

            ImGui.InputTextMultiline($"##loot-trade-attrs-text-{propertyName}-{index}", ref buffer, 64 * 1024, new NVector2(-float.Epsilon, 104f), ImGuiInputTextFlags.AllowTabInput);
            _lootDropJsonFieldBuffers[bufferKey] = buffer;

            if (ImGui.Button($"Apply attributes##loot-trade-apply-attrs-{propertyName}-{index}"))
            {
                JToken? parsed = TryParseJsonToken(buffer);
                if (parsed == null)
                {
                    _lootDropStatus = "Trade item attributes JSON is malformed.";
                }
                else
                {
                    item["attributes"] = parsed;
                    _lootDropJsonFieldBuffers[bufferKey] = parsed.ToString(Formatting.Indented);
                    changed = true;
                }
            }

            ImGui.SameLine();
            if (ImGui.Button($"Format##loot-trade-format-attrs-{propertyName}-{index}"))
            {
                if (DevToolsJsonTextTools.TryFormat(buffer, out string formatted, out string formatError))
                {
                    _lootDropJsonFieldBuffers[bufferKey] = formatted;
                }
                else
                {
                    _lootDropStatus = $"Trade item attributes format failed: {formatError}";
                }
            }

            ImGui.SameLine();
            if (ImGui.Button($"Remove attributes##loot-trade-remove-attrs-{propertyName}-{index}"))
            {
                item.Remove("attributes");
                _lootDropJsonFieldBuffers.Remove(bufferKey);
                changed = true;
            }

            ImGui.TreePop();
        }

        return changed;
    }

    private bool DrawTradeItemAdvancedFieldsEditor(JObject item, string listPropertyName, int index)
    {
        List<JProperty> advancedProperties = item.Properties()
            .Where(property => !LootDropTradeFirstClassProperties.Contains(property.Name))
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ImGuiTreeNodeFlags flags = advancedProperties.Count > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!ImGui.TreeNodeEx($"Advanced fields ({advancedProperties.Count})##loot-trade-advanced-{listPropertyName}-{index}", flags)) return false;

        bool changed = false;
        foreach (JProperty property in advancedProperties)
        {
            ImGui.PushID($"loot-trade-advanced-{listPropertyName}-{index}-{property.Name}");
            ImGui.Separator();
            changed |= DrawTradeItemAdvancedField(item, listPropertyName, index, property);
            ImGui.PopID();
        }

        ImGui.SeparatorText("Add advanced field");
        ImGui.SetNextItemWidth(180);
        ImGui.InputTextWithHint($"##loot-trade-advanced-new-name-{listPropertyName}-{index}", "field name", ref _lootDropNewTradeFieldName, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.Combo($"Type##loot-trade-advanced-new-kind-{listPropertyName}-{index}", ref _lootDropNewTradeFieldKindIndex, LootDropAdvancedFieldKindLabels, LootDropAdvancedFieldKindLabels.Length);
        ImGui.SameLine();
        if (ImGui.Button($"Add##loot-trade-advanced-add-{listPropertyName}-{index}"))
        {
            changed |= TryAddTradeItemAdvancedField(item);
        }

        ImGui.TreePop();
        return changed;
    }

    private bool DrawTradeItemAdvancedField(JObject item, string listPropertyName, int index, JProperty property)
    {
        string fieldName = property.Name;
        bool changed = false;

        if (property.Value is JObject or JArray)
        {
            if (ImGui.TreeNodeEx($"{fieldName} JSON##loot-trade-advanced-json-node-{listPropertyName}-{index}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                changed |= EditTradeItemAdvancedJsonToken(item, listPropertyName, index, fieldName);
                if (ImGui.Button($"Remove##loot-trade-advanced-remove-{listPropertyName}-{index}-{fieldName}"))
                {
                    item.Remove(fieldName);
                    ClearTradeItemAdvancedFieldBuffer(listPropertyName, index, fieldName);
                    changed = true;
                }
                ImGui.TreePop();
            }

            return changed;
        }

        ImGui.TextUnformatted(fieldName);
        ImGui.SameLine();

        switch (property.Value.Type)
        {
            case JTokenType.Boolean:
                bool boolValue = property.Value.Value<bool?>() ?? false;
                if (ImGui.Checkbox($"##loot-trade-advanced-bool-{listPropertyName}-{index}-{fieldName}", ref boolValue))
                {
                    item[fieldName] = boolValue;
                    changed = true;
                }
                break;
            case JTokenType.Integer:
                int intValue = property.Value.Value<int?>() ?? 0;
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputInt($"##loot-trade-advanced-int-{listPropertyName}-{index}-{fieldName}", ref intValue))
                {
                    item[fieldName] = intValue;
                    changed = true;
                }
                break;
            case JTokenType.Float:
                float floatValue = property.Value.Value<float?>() ?? 0f;
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputFloat($"##loot-trade-advanced-float-{listPropertyName}-{index}-{fieldName}", ref floatValue, 0, 0, "%.###"))
                {
                    item[fieldName] = floatValue;
                    changed = true;
                }
                break;
            case JTokenType.String:
                string stringValue = property.Value.ToString();
                ImGui.SetNextItemWidth(Math.Max(160f, ImGui.GetContentRegionAvail().X - 92f));
                if (ImGui.InputText($"##loot-trade-advanced-string-{listPropertyName}-{index}-{fieldName}", ref stringValue, 4096))
                {
                    item[fieldName] = stringValue;
                    changed = true;
                }
                break;
            default:
                ImGui.NewLine();
                changed |= EditTradeItemAdvancedJsonToken(item, listPropertyName, index, fieldName);
                break;
        }

        ImGui.SameLine();
        if (ImGui.Button($"Remove##loot-trade-advanced-remove-{listPropertyName}-{index}-{fieldName}"))
        {
            item.Remove(fieldName);
            ClearTradeItemAdvancedFieldBuffer(listPropertyName, index, fieldName);
            changed = true;
        }

        return changed;
    }

    private bool EditTradeItemAdvancedJsonToken(JObject item, string listPropertyName, int index, string fieldName)
    {
        string bufferKey = GetTradeItemAdvancedFieldBufferKey(listPropertyName, index, fieldName);
        if (!_lootDropJsonFieldBuffers.TryGetValue(bufferKey, out string? buffer))
        {
            buffer = item[fieldName]?.ToString(Formatting.Indented) ?? "null";
        }

        ImGui.InputTextMultiline($"##loot-trade-advanced-json-{listPropertyName}-{index}-{fieldName}", ref buffer, 128 * 1024, new NVector2(-float.Epsilon, 96f), ImGuiInputTextFlags.AllowTabInput);
        _lootDropJsonFieldBuffers[bufferKey] = buffer;

        bool changed = false;
        if (ImGui.Button($"Apply##loot-trade-advanced-apply-{listPropertyName}-{index}-{fieldName}"))
        {
            JToken? parsed = TryParseJsonToken(buffer);
            if (parsed == null)
            {
                _lootDropStatus = $"{fieldName} JSON is malformed.";
            }
            else
            {
                item[fieldName] = parsed;
                _lootDropJsonFieldBuffers[bufferKey] = parsed.ToString(Formatting.Indented);
                changed = true;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button($"Format##loot-trade-advanced-format-{listPropertyName}-{index}-{fieldName}"))
        {
            if (DevToolsJsonTextTools.TryFormat(buffer, out string formatted, out string formatError))
            {
                _lootDropJsonFieldBuffers[bufferKey] = formatted;
            }
            else
            {
                _lootDropStatus = $"{fieldName} format failed: {formatError}";
            }
        }

        return changed;
    }

    private bool TryAddTradeItemAdvancedField(JObject item)
    {
        string fieldName = _lootDropNewTradeFieldName.Trim();
        if (fieldName.Length == 0)
        {
            _lootDropStatus = "Trade item field name is required.";
            return false;
        }

        if (LootDropTradeFirstClassProperties.Contains(fieldName))
        {
            _lootDropStatus = $"{fieldName} is already handled by structured trade controls.";
            return false;
        }

        if (item[fieldName] != null)
        {
            _lootDropStatus = $"Trade item field {fieldName} already exists.";
            return false;
        }

        item[fieldName] = CreateLootDropAdvancedFieldDefault(_lootDropNewTradeFieldKindIndex);
        _lootDropNewTradeFieldName = "";
        return true;
    }

    private static JToken CreateLootDropAdvancedFieldDefault(int fieldKindIndex)
    {
        return fieldKindIndex switch
        {
            1 => false,
            2 => 0,
            3 => 0f,
            4 => new JObject(),
            5 => new JArray(),
            _ => ""
        };
    }

    private string GetTradeItemAdvancedFieldBufferKey(string listPropertyName, int index, string fieldName)
    {
        return $"{_lootDropLoadedKey}:trade:{listPropertyName}:{index}:advanced:{fieldName}";
    }

    private void ClearTradeItemAdvancedFieldBuffer(string listPropertyName, int index, string fieldName)
    {
        _lootDropJsonFieldBuffers.Remove(GetTradeItemAdvancedFieldBufferKey(listPropertyName, index, fieldName));
    }

    private void DrawLootDropInspector(NVector2 size, bool showDiagnostics)
    {
        ImGui.BeginChild("##loot-drop-inspector", size, true);
        if (_lootDropIndexState == LootDropIndexState.Indexing)
        {
            ImGui.SeparatorText("Status");
            ImGui.TextWrapped(_lootDropStatus);
            _lootDropDiagnostics.Draw("loot-drop", showDiagnostics);
            ImGui.EndChild();
            return;
        }

        if (_lootDropIndexState == LootDropIndexState.Failed)
        {
            ImGui.SeparatorText("Status");
            ImGui.TextColored(new NVector4(1f, 0.38f, 0.32f, 1f), _lootDropStatus);
            _lootDropDiagnostics.Draw("loot-drop", showDiagnostics);
            ImGui.EndChild();
            return;
        }

        LootDropEntry? entry = SelectedLootDropEntry;
        if (entry == null)
        {
            ImGui.TextDisabled("Select a loot/drop source.");
            ImGui.EndChild();
            return;
        }

        EnsureLootDropEntryLoaded(entry);
        bool valid = _lootDropDataValid;

        ImGui.SeparatorText("Status");
        ImGui.TextDisabled($"{entry.KindLabel}: {entry.Code}");
        ImGui.TextDisabled($"Domain: {entry.Domain}");
        ImGui.TextColored(valid ? new NVector4(0.42f, 0.85f, 0.42f, 1f) : new NVector4(1f, 0.38f, 0.32f, 1f), valid ? "Data valid" : "Data invalid");
        if (!valid || !string.Equals(_lootDropValidationStatus, "Data valid", StringComparison.Ordinal))
        {
            ImGui.TextWrapped(_lootDropValidationStatus);
        }
        ImGui.TextWrapped(entry.SourceAsset?.Location.ToString() ?? "Source asset: unresolved; save will create an authored override.");
        if (entry.Kind != LootDropKind.TradeTable && HasWeightedLootDrops(_lootDropDrafts))
        {
            ImGui.TextColored(new NVector4(1f, 0.78f, 0.32f, 1f), "Weighted drops detected. Consecutive non-default weighted rows simulate as weighted groups.");
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
            SaveCurrentLootDropDraftState();
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
        _ = keepDirty;
        if (string.Equals(_lootDropLoadedKey, entry.Key, StringComparison.OrdinalIgnoreCase)) return;
        SaveCurrentLootDropDraftState();

        try
        {
            _lootDropDrafts.Clear();
            _lootDropTradeJson = "";
            _lootDropSimulationText = "";
            _lootDropSelectedDraftIndex = 0;

            if (_lootDropDraftStates.TryGetValue(entry.Key, out LootDropDraftState? retainedState))
            {
                foreach (LootDropDraft draft in retainedState.Drafts)
                {
                    _lootDropDrafts.Add(draft.Clone());
                }

                _lootDropTradeJson = retainedState.TradeJson;
                _lootDropOriginalJson = retainedState.OriginalJson;
                _lootDropSimulationText = retainedState.SimulationText;
                _lootDropLiveAppliedHash = retainedState.LiveAppliedHash;
            }
            else if (entry.Kind == LootDropKind.TradeTable)
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
            if (retainedState == null)
            {
                _lootDropOriginalJson = BuildCurrentLootDropJson(entry);
                _lootDropLiveAppliedHash = "";
            }

            RefreshLootDropCachedState(entry);
            _lootDropStatus = retainedState != null
                ? retainedState.IsDirty
                    ? $"Restored unsaved editor state for {entry.Label}."
                    : $"Restored editor state for {entry.Label}."
                : HasWeightedLootDrops(_lootDropDrafts)
                ? $"Loaded {entry.Label}. Weighted drops are editable and consecutive non-default weighted rows simulate as weighted groups."
                : $"Loaded {entry.Label}.";
        }
        catch (Exception exception)
        {
            _lootDropDiagnostics.Exception($"Failed to load {entry.Label}", exception);
            _lootDropStatus = $"Failed to load {entry.Label}: {exception.Message}";
        }
    }

    private void SaveCurrentLootDropDraftState()
    {
        if (string.IsNullOrWhiteSpace(_lootDropLoadedKey)) return;

        _lootDropDraftStates[_lootDropLoadedKey] = LootDropDraftState.Capture(
            _lootDropDrafts,
            _lootDropTradeJson,
            _lootDropOriginalJson,
            _lootDropSimulationText,
            _lootDropLiveAppliedHash,
            _lootDropCurrentJson);
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
        return _lootDropCurrentJson;
    }

    private string BuildCurrentLootDropJson(LootDropEntry? entry)
    {
        if (entry?.Kind == LootDropKind.TradeTable) return NormalizeJsonForHash(_lootDropTradeJson);
        return BuildDropDraftArray(includeInvalidAttributesMarker: true).ToString(Formatting.None);
    }

    private void RefreshLootDropCachedState(LootDropEntry? entry = null)
    {
        entry ??= SelectedLootDropEntry;
        _lootDropCurrentJson = BuildCurrentLootDropJson(entry);
        _lootDropDirtyCached = !string.IsNullOrWhiteSpace(_lootDropLoadedKey) &&
            !string.Equals(_lootDropCurrentJson, _lootDropOriginalJson, StringComparison.Ordinal);
        _lootDropDataValid = false;
        _lootDropValidatedRuntimeDrops = [];

        if (entry == null)
        {
            _lootDropValidationStatus = "No loot/drop source loaded.";
            return;
        }

        if (entry.Kind == LootDropKind.TradeTable)
        {
            _lootDropDataValid = TryParseJsonToken(_lootDropTradeJson) != null;
            _lootDropValidationStatus = _lootDropDataValid ? "Data valid" : "Trade JSON is invalid.";
            return;
        }

        if (!TryValidateLootDropDraftReferences(out string referenceError))
        {
            _lootDropValidationStatus = referenceError;
            return;
        }

        if (!TryBuildRuntimeDropsFromDrafts(out BlockDropItemStack[] drops, out string error))
        {
            _lootDropValidationStatus = error;
            return;
        }

        _lootDropValidatedRuntimeDrops = drops;
        _lootDropDataValid = true;
        _lootDropValidationStatus = "Data valid";
    }

    private void OnLootDropDraftChanged()
    {
        RefreshLootDropCachedState();
        SaveCurrentLootDropDraftState();
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

        RefreshLootDropCachedState(entry);
        if (!_lootDropDataValid)
        {
            _lootDropStatus = $"Runtime apply skipped: {_lootDropValidationStatus}";
            _liveApplyManager.LastStatus = _lootDropStatus;
            return;
        }

        BlockDropItemStack[] drops = _lootDropValidatedRuntimeDrops.Select(drop => drop.Clone()).ToArray();
        string hash = _lootDropCurrentJson;
        if (!force && string.Equals(_lootDropLiveAppliedHash, hash, StringComparison.Ordinal)) return;

        string key = GetLootDropLiveKey(entry);
        _lootDropStatus = _liveApplyManager.Apply(
            key,
            entry.Label,
            () => CaptureLootDropLiveSnapshot(entry),
            () => ApplyRuntimeDrops(entry, drops),
            $"Live applied {drops.Length} drop row(s) for {entry.Label}.");
        _lootDropLiveAppliedHash = hash;
        SaveCurrentLootDropDraftState();
    }

    private bool TryBuildRuntimeDropsFromDrafts(out BlockDropItemStack[] drops, out string error)
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

    private bool TryValidateLootDropDraftReferences(out string error)
    {
        List<string> issues = [];
        for (int index = 0; index < _lootDropDrafts.Count; index++)
        {
            LootDropDraft draft = _lootDropDrafts[index];
            string code = draft.Code.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                issues.Add($"Row {index}: drop code is blank.");
                continue;
            }

            if (!Enum.TryParse(draft.Type, ignoreCase: true, out EnumItemClass itemClass))
            {
                issues.Add($"Row {index}: unknown drop type '{draft.Type}'.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(draft.AttributesJson) && TryParseJsonToken(draft.AttributesJson) == null)
            {
                issues.Add($"Row {index}: malformed attributes JSON.");
            }

            if (HasRuntimeResolvedLootDropCode(code)) continue;

            if (itemClass == EnumItemClass.Block && ResolveLootDropBlock(code) == null)
            {
                issues.Add($"Row {index}: unresolved block '{code}'.");
            }
            else if (itemClass == EnumItemClass.Item && ResolveLootDropItem(code) == null)
            {
                issues.Add($"Row {index}: unresolved item '{code}'.");
            }
        }

        if (issues.Count == 0)
        {
            error = "";
            return true;
        }

        error = string.Join(Environment.NewLine, issues.Take(6));
        if (issues.Count > 6) error += Environment.NewLine + $"...and {issues.Count - 6} more unresolved row(s).";
        return false;
    }

    private static bool HasRuntimeResolvedLootDropCode(string code)
    {
        return code.Contains('*', StringComparison.Ordinal) ||
            code.Contains('{', StringComparison.Ordinal) ||
            code.Contains('}', StringComparison.Ordinal) ||
            code.Contains('[', StringComparison.Ordinal) ||
            code.Contains(']', StringComparison.Ordinal);
    }

    private Block? ResolveLootDropBlock(string code)
    {
        try
        {
            AssetLocation location = AssetLocation.Create(code, "game");
            Block? block = _api.World.GetBlock(location);
            if (block != null) return block;

            return location.Domain.Equals("game", StringComparison.OrdinalIgnoreCase)
                ? null
                : _api.World.GetBlock(new AssetLocation("game", location.Path));
        }
        catch
        {
            return null;
        }
    }

    private Item? ResolveLootDropItem(string code)
    {
        try
        {
            AssetLocation location = AssetLocation.Create(code, "game");
            Item? item = _api.World.GetItem(location);
            if (item != null) return item;

            return location.Domain.Equals("game", StringComparison.OrdinalIgnoreCase)
                ? null
                : _api.World.GetItem(new AssetLocation("game", location.Path));
        }
        catch
        {
            return null;
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
            RefreshLootDropCachedState(entry);
            if (!_lootDropDataValid)
            {
                return SourceSaveResult.Fail($"Save failed: {_lootDropValidationStatus}");
            }

            string sourceText = ReadAssetText(entry.SourceAsset);
            JObject json = entry.SourceJson?.DeepClone() as JObject ?? TryParseJsonObject(sourceText) ?? CreateLootDropAuthoringDocument(entry);
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
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : sourceText;
            string newText = json.ToString(Formatting.Indented);
            string savedJson = _lootDropCurrentJson;
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved authored loot/drop JSON to {outputPath}.",
                () =>
                {
                    string result = WriteAuthoredFile(outputPath, newText);
                    MarkLootDropEntrySaved(entry, savedJson);
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

    private void MarkLootDropEntrySaved(LootDropEntry entry, string savedJson)
    {
        if (string.Equals(_lootDropLoadedKey, entry.Key, StringComparison.OrdinalIgnoreCase))
        {
            _lootDropOriginalJson = savedJson;
            RefreshLootDropCachedState(entry);
            SaveCurrentLootDropDraftState();
            return;
        }

        if (_lootDropDraftStates.TryGetValue(entry.Key, out LootDropDraftState? state))
        {
            _lootDropDraftStates[entry.Key] = state.WithOriginalJson(savedJson);
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

    private JArray BuildDropDraftArray(bool includeInvalidAttributesMarker = false)
    {
        JArray array = new();
        foreach (LootDropDraft draft in _lootDropDrafts)
        {
            JObject token = draft.ToToken();
            if (includeInvalidAttributesMarker &&
                !string.IsNullOrWhiteSpace(draft.AttributesJson) &&
                TryParseJsonToken(draft.AttributesJson) == null)
            {
                token["__invalidAttributesJson"] = draft.AttributesJson;
            }

            array.Add(token);
        }

        return array;
    }

    private static string SimulateLootDrops(IReadOnlyList<LootDropDraft> drafts, int runs)
    {
        if (drafts.Count == 0) return "No drops configured.";

        int weightedGroupCount = CountWeightedLootDropGroups(drafts);
        Random random = new(8675309);
        Dictionary<string, double> totals = new(StringComparer.OrdinalIgnoreCase);
        for (int run = 0; run < runs; run++)
        {
            for (int index = 0; index < drafts.Count;)
            {
                LootDropDraft draft = drafts[index];
                if (IsWeightedLootDrop(draft))
                {
                    int groupStart = index;
                    while (index < drafts.Count && IsWeightedLootDrop(drafts[index]))
                    {
                        index++;
                    }

                    LootDropDraft? selected = PickWeightedLootDrop(drafts, groupStart, index, random);
                    if (selected == null) continue;

                    double selectedQuantity = SimulateLootDropQuantity(selected, random);
                    AddSimulatedLoot(totals, selected, selectedQuantity);
                    if (selected.LastDrop && selectedQuantity > 0) break;
                    continue;
                }

                index++;
                double unweightedQuantity = SimulateLootDropQuantity(draft, random);
                AddSimulatedLoot(totals, draft, unweightedQuantity);
                if (draft.LastDrop && unweightedQuantity > 0) break;
            }
        }

        List<string> notes = [];
        if (weightedGroupCount > 0)
        {
            notes.Add($"Weighted groups: simulated {weightedGroupCount} consecutive non-default weighted row group(s) as one weighted pick per run.");
            notes.Add("Rows with omitted/default weight are simulated as ordinary rows because the editor cannot distinguish omitted weight from unweighted rows.");
        }
        notes.Add("Simulation limitations: ignores tool filters, player stats/drop stat modifiers, and custom drop behavior code.");
        string prefix = string.Join(Environment.NewLine, notes) + Environment.NewLine;

        if (totals.Count == 0) return $"{prefix}Simulated {runs} run(s): no drops.";

        return prefix + string.Join(Environment.NewLine, totals
            .OrderByDescending(entry => entry.Value)
            .Select(entry => $"{entry.Key}: total {entry.Value:0.##}, avg/run {entry.Value / runs:0.###}"));
    }

    private static double SimulateLootDropQuantity(LootDropDraft draft, Random random)
    {
        return Math.Max(0, Math.Round(CreateNatFloat(draft).nextFloat(1f, random), MidpointRounding.AwayFromZero));
    }

    private static void AddSimulatedLoot(IDictionary<string, double> totals, LootDropDraft draft, double quantity)
    {
        if (quantity <= 0) return;

        string key = $"{draft.Type}:{draft.Code}";
        totals[key] = totals.TryGetValue(key, out double existing) ? existing + quantity : quantity;
    }

    private static LootDropDraft? PickWeightedLootDrop(IReadOnlyList<LootDropDraft> drafts, int startInclusive, int endExclusive, Random random)
    {
        double totalWeight = 0;
        for (int index = startInclusive; index < endExclusive; index++)
        {
            totalWeight += Math.Max(0, drafts[index].Weight);
        }

        if (totalWeight <= 0) return null;

        double pick = random.NextDouble() * totalWeight;
        for (int index = startInclusive; index < endExclusive; index++)
        {
            pick -= Math.Max(0, drafts[index].Weight);
            if (pick <= 0) return drafts[index];
        }

        return drafts[endExclusive - 1];
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

    private static List<LootDropWeightedGroupInfo> BuildLootDropWeightedGroups(IReadOnlyList<LootDropDraft> drafts)
    {
        List<LootDropWeightedGroupInfo> groups = [];
        int groupNumber = 1;
        for (int index = 0; index < drafts.Count;)
        {
            if (!IsWeightedLootDrop(drafts[index]))
            {
                index++;
                continue;
            }

            int start = index;
            float totalWeight = 0f;
            while (index < drafts.Count && IsWeightedLootDrop(drafts[index]))
            {
                totalWeight += Math.Max(0f, drafts[index].Weight);
                index++;
            }

            groups.Add(new LootDropWeightedGroupInfo(groupNumber++, start, index, totalWeight));
        }

        return groups;
    }

    private static Dictionary<int, LootDropWeightedGroupInfo> BuildLootDropWeightedGroupMap(IReadOnlyList<LootDropDraft> drafts)
    {
        Dictionary<int, LootDropWeightedGroupInfo> result = [];
        foreach (LootDropWeightedGroupInfo group in BuildLootDropWeightedGroups(drafts))
        {
            for (int index = group.StartIndex; index < group.EndExclusive; index++)
            {
                result[index] = group;
            }
        }

        return result;
    }

    private static float GetLootDropWeightPercent(LootDropDraft draft, LootDropWeightedGroupInfo group)
    {
        if (group.TotalWeight <= 0f) return 0f;
        return Math.Max(0f, draft.Weight) / group.TotalWeight * 100f;
    }

    private static bool HasLootDropConditionOrFlow(LootDropDraft draft)
    {
        return !string.IsNullOrWhiteSpace(draft.Tool) ||
            !string.IsNullOrWhiteSpace(draft.DropModbyStat) ||
            !string.IsNullOrWhiteSpace(draft.AttributesJson) ||
            draft.LastDrop;
    }

    private static string BuildLootDropConditionSummary(LootDropDraft draft)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(draft.Tool)) parts.Add($"tool {draft.Tool}");
        if (!string.IsNullOrWhiteSpace(draft.DropModbyStat)) parts.Add($"stat {draft.DropModbyStat}");
        if (!string.IsNullOrWhiteSpace(draft.AttributesJson)) parts.Add("attributes");
        if (draft.LastDrop) parts.Add("stops on drop");
        return parts.Count == 0 ? "" : string.Join(", ", parts);
    }

    private static string BuildLootDropRowTooltip(LootDropDraft draft, int index, IReadOnlyDictionary<int, LootDropWeightedGroupInfo> weightedGroupMap)
    {
        List<string> lines = [$"Row {index}: {draft.Type}:{draft.Code}"];
        if (weightedGroupMap.TryGetValue(index, out LootDropWeightedGroupInfo group))
        {
            lines.Add($"Weighted group {group.GroupNumber}, rows {group.StartIndex}-{group.EndExclusive - 1}");
            lines.Add($"Weight {draft.Weight:0.###} of {group.TotalWeight:0.###} ({GetLootDropWeightPercent(draft, group):0.#}% pick chance inside group)");
        }
        else
        {
            lines.Add("Runs as an ordinary ordered drop row.");
        }

        string conditions = BuildLootDropConditionSummary(draft);
        lines.Add(string.IsNullOrWhiteSpace(conditions) ? "No tool/stat gates, attributes, or last-drop stop." : $"Conditions/flow: {conditions}");
        return string.Join(Environment.NewLine, lines);
    }

    private static int CountWeightedLootDropGroups(IReadOnlyList<LootDropDraft> drafts)
    {
        int groups = 0;
        bool inGroup = false;
        foreach (LootDropDraft draft in drafts)
        {
            if (IsWeightedLootDrop(draft))
            {
                if (!inGroup) groups++;
                inGroup = true;
            }
            else
            {
                inGroup = false;
            }
        }

        return groups;
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
        return DevToolsJson.TryParseToken(text, useVintageStoryFallback: false);
    }

    private JObject? TryReadLootDropAssetJson(IAsset? asset)
    {
        if (asset == null) return null;

        string text = ReadAssetText(asset);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (TryParseJsonObjectDetailed(text, out JObject? json, out string error)) return json;

        _lootDropDiagnostics.Warning($"Skipped malformed loot/drop source {asset.Location}: {error}", text);
        return null;
    }

    private static bool TryParseJsonObjectDetailed(string text, out JObject? json, out string error)
    {
        return DevToolsJson.TryParseObject(text, out json, out error);
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

    private enum LootDropIndexState
    {
        Idle,
        Indexing,
        Ready,
        Failed
    }

    private enum LootDropIndexPhase
    {
        Blocks,
        Entities,
        EntityTrades,
        TradeAssets
    }

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

    private sealed class LootDropEntitySourceIndex
    {
        private readonly Dictionary<string, LootDropEntitySourceAsset> _sourcesByCode = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<LootDropEntitySourceAsset> _sources = [];

        public IReadOnlyList<LootDropEntitySourceAsset> Sources => _sources;

        public static LootDropEntitySourceIndex Build(ICoreClientAPI api, DevToolsEditorDiagnostics diagnostics, IReadOnlyList<IAsset>? authoredAssets = null)
        {
            LootDropEntitySourceIndex index = new();
            void IndexAsset(IAsset? asset)
            {
                if (asset?.Location == null) return;
                string assetPath = asset.Location.Path.Replace('\\', '/');
                if (!assetPath.StartsWith("entities/", StringComparison.OrdinalIgnoreCase) ||
                    !assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string text = ReadAssetText(asset);
                if (!TryParseJsonObjectDetailed(text, out JObject? json, out string error) || json == null)
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        diagnostics.Warning($"Skipped malformed entity source {asset.Location}: {error}", text);
                    }
                    return;
                }

                string? sourceCode = json["code"]?.ToString();
                if (string.IsNullOrWhiteSpace(sourceCode)) return;

                LootDropEntitySourceAsset source = new(asset, StripCodeDomain(sourceCode), json);
                index._sources.Add(source);
                index.Register(source, source.SourceCode);
                foreach (string entityCode in ExpandEntityCodes(api, asset.Location.Domain, json, source.SourceCode))
                {
                    index.Register(source, entityCode);
                }
            }

            foreach (IAsset asset in api.Assets.AllAssets.Values)
            {
                IndexAsset(asset);
            }

            // Authored copies are indexed last: code registration is last-wins, so the
            // user's saved files override the loaded game assets they were saved from.
            foreach (IAsset asset in authoredAssets ?? [])
            {
                IndexAsset(asset);
            }

            index._sources.Sort((left, right) => right.SourceCode.Length.CompareTo(left.SourceCode.Length));
            return index;
        }

        public LootDropEntitySourceAsset? Resolve(EntityProperties entityType)
        {
            if (entityType.Code == null) return null;
            string fullCode = NormalizeEntitySourceKey(entityType.Code.Domain, entityType.Code.Path);
            if (_sourcesByCode.TryGetValue(fullCode, out LootDropEntitySourceAsset? exact)) return exact;

            string path = entityType.Code.Path;
            foreach (LootDropEntitySourceAsset source in _sources)
            {
                if (string.Equals(path, source.SourceCode, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(source.SourceCode + "-", StringComparison.OrdinalIgnoreCase))
                {
                    return source;
                }
            }

            return null;
        }

        private void Register(LootDropEntitySourceAsset source, string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            _sourcesByCode[NormalizeEntitySourceKey(source.Asset.Location.Domain, code)] = source;
        }

        private static IEnumerable<string> ExpandEntityCodes(ICoreClientAPI api, string domain, JObject sourceJson, string sourceCode)
        {
            if (sourceJson["variantgroups"] is not JArray groups || groups.Count == 0)
            {
                yield return sourceCode;
                yield break;
            }

            List<LootDropVariantGroup> variantGroups = [];
            foreach (JObject group in groups.OfType<JObject>())
            {
                string? groupCode = group["code"]?.ToString();
                if (string.IsNullOrWhiteSpace(groupCode)) continue;
                List<string> states = ResolveVariantStates(api, domain, group).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (states.Count == 0) yield break;
                variantGroups.Add(new(groupCode, states));
            }

            if (variantGroups.Count == 0)
            {
                yield return sourceCode;
                yield break;
            }

            foreach (Dictionary<string, string> combination in BuildVariantCombinations(variantGroups))
            {
                yield return BuildVariantCode(sourceCode, variantGroups, combination);
            }
        }

        private static IEnumerable<string> ResolveVariantStates(ICoreClientAPI api, string domain, JObject group)
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
                foreach (string value in LoadWorldPropertyStates(api, domain, loadFromProperties))
                {
                    yield return value;
                }
            }
        }

        private static IEnumerable<string> LoadWorldPropertyStates(ICoreClientAPI api, string domain, string loadFromProperties)
        {
            string path = EnsureJsonFilePath($"worldproperties/{loadFromProperties.Trim().TrimStart('/')}");
            foreach (string candidateDomain in new[] { domain, "game" }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                IAsset? asset = api.Assets.TryGet(new AssetLocation(candidateDomain, path), true);
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

        private static IEnumerable<Dictionary<string, string>> BuildVariantCombinations(IReadOnlyList<LootDropVariantGroup> groups)
        {
            List<Dictionary<string, string>> combinations = [new(StringComparer.OrdinalIgnoreCase)];
            foreach (LootDropVariantGroup group in groups)
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

        private static string BuildVariantCode(string sourceCode, IReadOnlyList<LootDropVariantGroup> groups, IReadOnlyDictionary<string, string> states)
        {
            string code = sourceCode;
            List<string> suffixes = [];
            foreach (LootDropVariantGroup group in groups)
            {
                if (!states.TryGetValue(group.Code, out string? state)) continue;
                string placeholder = "{" + group.Code + "}";
                if (code.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
                {
                    code = ReplaceInvariant(code, placeholder, state);
                }
                else
                {
                    suffixes.Add(state);
                }
            }

            return suffixes.Count == 0 ? code : $"{code}-{string.Join('-', suffixes)}";
        }

        private static string NormalizeEntitySourceKey(string defaultDomain, string code)
        {
            string trimmed = StripCodeDomain(code);
            string domain = code.Contains(':', StringComparison.Ordinal) ? code[..code.IndexOf(':')] : defaultDomain;
            return $"{domain}:{trimmed}";
        }

        private static string StripCodeDomain(string code)
        {
            int separator = code.IndexOf(':');
            return separator >= 0 ? code[(separator + 1)..] : code;
        }

        private static string ReplaceInvariant(string value, string oldValue, string newValue)
        {
            int index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                value = value[..index] + newValue + value[(index + oldValue.Length)..];
                index = value.IndexOf(oldValue, index + newValue.Length, StringComparison.OrdinalIgnoreCase);
            }

            return value;
        }
    }

    private sealed record LootDropEntitySourceAsset(IAsset Asset, string SourceCode, JObject SourceJson);

    private sealed record LootDropVariantGroup(string Code, IReadOnlyList<string> States);

    private readonly record struct LootDropWeightedGroupInfo(int GroupNumber, int StartIndex, int EndExclusive, float TotalWeight);

    private sealed class LootDropDraftState
    {
        public List<LootDropDraft> Drafts { get; init; } = [];
        public string TradeJson { get; init; } = "";
        public string OriginalJson { get; init; } = "";
        public string SimulationText { get; init; } = "";
        public string LiveAppliedHash { get; init; } = "";
        public string CurrentJson { get; init; } = "";
        public bool IsDirty => !string.Equals(CurrentJson, OriginalJson, StringComparison.Ordinal);

        public LootDropDraftState WithOriginalJson(string originalJson)
        {
            return new()
            {
                Drafts = Drafts.Select(draft => draft.Clone()).ToList(),
                TradeJson = TradeJson,
                OriginalJson = originalJson,
                SimulationText = SimulationText,
                LiveAppliedHash = LiveAppliedHash,
                CurrentJson = CurrentJson
            };
        }

        public static LootDropDraftState Capture(
            IEnumerable<LootDropDraft> drafts,
            string tradeJson,
            string originalJson,
            string simulationText,
            string liveAppliedHash,
            string currentJson)
        {
            return new()
            {
                Drafts = drafts.Select(draft => draft.Clone()).ToList(),
                TradeJson = tradeJson,
                OriginalJson = originalJson,
                SimulationText = simulationText,
                LiveAppliedHash = liveAppliedHash,
                CurrentJson = currentJson
            };
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
