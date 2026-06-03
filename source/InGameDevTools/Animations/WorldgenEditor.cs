using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private const int WorldgenIndexBatchSize = 80;
    private const int WorldgenPreviewModeGradient = 0;
    private const int WorldgenPreviewModeClimate = 1;
    private const int WorldgenPreviewModeForest = 2;
    private const int WorldgenPreviewModeUpheaval = 3;
    private const int WorldgenPreviewModeOcean = 4;
    private const int WorldgenPreviewModeLandform = 5;
    private const int WorldgenPreviewModeProvince = 6;
    private const int WorldgenPreviewModeOre = 7;
    private const int WorldgenPreviewModeBlockPatch = 8;
    private const int WorldgenPreviewModeTerrainShape = 9;
    private const int WorldgenPreviewModeRegion3D = 10;
    private static readonly string[] WorldgenKindFilterLabels =
    [
        "All",
        "Deposits",
        "Block patches",
        "Landforms",
        "Rock strata",
        "Other"
    ];
    private static readonly string[] WorldgenPreviewModeLabels =
    [
        "Gradient test",
        "Climate",
        "Forest",
        "Upheaval",
        "Ocean",
        "Landform",
        "Province",
        "Ore",
        "Block patch suitability",
        "Terrain shape",
        "3D region"
    ];
    private static readonly string[] WorldgenPeekPassLabels =
    [
        "Terrain",
        "Terrain features",
        "Vegetation",
        "Neighbour sunlight",
        "Pre-done",
        "Done"
    ];
    private static readonly EnumWorldGenPass[] WorldgenPeekPasses =
    [
        EnumWorldGenPass.Terrain,
        EnumWorldGenPass.TerrainFeatures,
        EnumWorldGenPass.Vegetation,
        EnumWorldGenPass.NeighbourSunLightFlood,
        EnumWorldGenPass.PreDone,
        EnumWorldGenPass.Done
    ];

    private readonly List<WorldgenAssetEntry> _worldgenEntries = [];
    private readonly List<WorldgenAssetEntry> _visibleWorldgenEntries = [];
    private readonly List<IAsset> _worldgenIndexAssets = [];
    private readonly Dictionary<string, WorldgenDraftState> _worldgenDraftStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImGuiThreePanelLayoutState _worldgenLayout = new(0.26f, 0.30f);
    private readonly DevToolsEditorDiagnostics _worldgenDiagnostics = new("Worldgen");
    private WorldgenIndexState _worldgenIndexState;
    private int _worldgenIndexAssetIndex;
    private string _worldgenFilter = "";
    private string _worldgenDomainFilter = "";
    private int _worldgenKindFilter;
    private int _worldgenEntryIndex;
    private int _worldgenRowIndex;
    private bool _worldgenDirtyOnly;
    private string _worldgenLoadedKey = "";
    private string _worldgenOriginalText = "";
    private string _worldgenCurrentText = "";
    private string _worldgenStatus = "Worldgen editor ready.";
    private bool _worldgenTextValid;
    private string _worldgenValidationStatus = "No worldgen asset loaded.";
    private int _worldgenPreviewMode = WorldgenPreviewModeGradient;
    private bool _worldgenPreviewAutoMode = true;
    private string _worldgenPreviewSeedText = "";
    private string _worldgenPreviewConfigStatus = "World config not read yet.";
    private int _worldgenPreviewOriginX;
    private int _worldgenPreviewOriginZ;
    private int _worldgenPreviewResolution = 96;
    private float _worldgenPreviewPanX;
    private float _worldgenPreviewPanZ;
    private float _worldgenPreviewZoom = 1f;
    private float _worldgenPreview3DYaw = MathF.PI * 0.25f;
    private float _worldgenPreview3DPitch = 0.70f;
    private bool _worldgenPreviewInitialized;
    private ICoreServerAPI? _worldgenPreviewServerApi;
    private GenMaps? _worldgenPreviewGenMaps;
    private GenDeposits? _worldgenPreviewGenDeposits;
    private Dictionary<int, string>? _worldgenPreviewLandformCodes;
    private Dictionary<int, string>? _worldgenPreviewProvinceCodes;
    private string _worldgenPreviewServerStatus = "Singleplayer server API not checked.";
    private MapLayerBase? _worldgenPreviewMapLayer;
    private int _worldgenPreviewMapLayerMode = -1;
    private string _worldgenPreviewMapLayerStatus = "";
    private WorldgenPreviewRasterCacheKey? _worldgenPreviewRasterCacheKey;
    private uint[]? _worldgenPreviewRasterCache;
    private string _worldgenPreviewRasterStatus = "Raster cache empty.";
    private bool _worldgenPreviewPeekPending;
    private string _worldgenPreviewPeekStatus = "No real chunk peek requested yet.";
    private int _worldgenPreviewRegionSize = 1;
    private int _worldgenPreviewPassIndex;
    private WorldgenPeekRegionCacheKey? _worldgenPreviewPeekCacheKey;
    private WorldgenPeekRegionProfile? _worldgenPreviewPeekProfile;

    private void WorldgenEditorTab(float deltaSeconds, bool showDiagnostics)
    {
        _ = deltaSeconds;
        ClearActiveTransformGizmo();

        try
        {
            EnsureWorldgenEntriesIndexed();

            NVector2 available = ImGui.GetContentRegionAvail();
            float scale = Math.Max(0.75f, _devToolsUiScale);
            float splitterThickness = Math.Max(5f, 6f * scale);
            ImGuiLayoutHelper.CalculateThreePanelWidths(
                available.X,
                splitterThickness,
                _worldgenLayout,
                260f * scale,
                560f * scale,
                520f * scale,
                340f * scale,
                760f * scale,
                out float panelAvailableWidth,
                out float leftWidth,
                out float centerWidth,
                out float rightWidth);

            DrawWorldgenBrowser(new NVector2(leftWidth, available.Y));
            ImGui.SameLine(0, 0);
            ImGuiLayoutHelper.DrawVerticalSplitter("##worldgen-left-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _worldgenLayout.LeftFraction, 260f * scale, Math.Max(260f * scale, panelAvailableWidth - rightWidth - 520f * scale));
            ImGui.SameLine(0, 0);
            DrawWorldgenEditorPanel(new NVector2(centerWidth, available.Y));
            ImGui.SameLine(0, 0);
            ImGuiLayoutHelper.DrawVerticalSplitter("##worldgen-right-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _worldgenLayout.RightFraction, 340f * scale, Math.Max(340f * scale, panelAvailableWidth - leftWidth - 520f * scale), invertDrag: true);
            ImGui.SameLine(0, 0);
            DrawWorldgenInspector(new NVector2(rightWidth, available.Y), showDiagnostics);
        }
        catch (Exception exception)
        {
            _worldgenIndexState = WorldgenIndexState.Failed;
            _worldgenStatus = $"Worldgen editor error: {exception.Message}";
            _worldgenDiagnostics.Exception("Worldgen editor failed", exception);
            _api.Logger.Error("[InGameDevTools] Worldgen editor failed: {0}", exception);
            ImGui.TextWrapped(_worldgenStatus);
            _worldgenDiagnostics.Draw("worldgen-error", showDiagnostics);
        }
    }

    private void ResetWorldgenLayout()
    {
        _worldgenLayout.Reset();
    }

    private void ApplyWorldgenRuntime(bool force = false)
    {
        _ = force;
        _liveApplyManager.LastStatus = "Worldgen editor writes authored files; it has no runtime apply target in v1.";
    }

    private void ClearWorldgenLiveApplyState()
    {
    }

    private void EnsureWorldgenEntriesIndexed()
    {
        if (_worldgenIndexState == WorldgenIndexState.Ready || _worldgenIndexState == WorldgenIndexState.Failed) return;
        if (_worldgenIndexState == WorldgenIndexState.Idle)
        {
            StartWorldgenIndexing(clearLoaded: false);
        }

        ProcessWorldgenIndexBatch();
    }

    private void StartWorldgenIndexing(bool clearLoaded)
    {
        RememberWorldgenDraft();
        _worldgenIndexState = WorldgenIndexState.Indexing;
        _worldgenIndexAssetIndex = 0;
        _worldgenEntries.Clear();
        _visibleWorldgenEntries.Clear();
        _worldgenIndexAssets.Clear();
        _worldgenEntryIndex = 0;
        _worldgenRowIndex = 0;

        if (clearLoaded)
        {
            _worldgenLoadedKey = "";
            _worldgenOriginalText = "";
            _worldgenCurrentText = "";
            _worldgenTextValid = false;
            _worldgenValidationStatus = "No worldgen asset loaded.";
            _worldgenDraftStates.Clear();
        }

        HashSet<string> indexedLocations = new(StringComparer.OrdinalIgnoreCase);
        AddWorldgenIndexAssets(_api.Assets.GetManyInCategory("worldgen", ""), indexedLocations);
        AddWorldgenIndexAssets(_api.Assets.AllAssets.Values.Where(IsWorldgenJsonAsset), indexedLocations);

        _worldgenIndexAssets.Sort((left, right) => string.Compare(left.Location.ToString(), right.Location.ToString(), StringComparison.OrdinalIgnoreCase));
        _worldgenStatus = BuildWorldgenIndexProgressText();
    }

    private void AddWorldgenIndexAssets(IEnumerable<IAsset> assets, HashSet<string> indexedLocations)
    {
        foreach (IAsset asset in assets)
        {
            if (asset?.Location == null) continue;

            string path = asset.Location.Path.Replace('\\', '/');
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            string key = asset.Location.ToString();
            if (indexedLocations.Add(key))
            {
                _worldgenIndexAssets.Add(asset);
            }
        }
    }

    private void ProcessWorldgenIndexBatch()
    {
        if (_worldgenIndexState != WorldgenIndexState.Indexing) return;

        try
        {
            int processed = 0;
            while (processed < WorldgenIndexBatchSize && _worldgenIndexAssetIndex < _worldgenIndexAssets.Count)
            {
                IndexWorldgenAsset(_worldgenIndexAssets[_worldgenIndexAssetIndex++]);
                processed++;
            }

            if (_worldgenIndexAssetIndex >= _worldgenIndexAssets.Count)
            {
                CompleteWorldgenIndexing();
            }
            else
            {
                _worldgenStatus = BuildWorldgenIndexProgressText();
            }
        }
        catch (Exception exception)
        {
            _worldgenIndexState = WorldgenIndexState.Failed;
            _worldgenStatus = $"Worldgen indexing failed: {exception.Message}";
            _worldgenDiagnostics.Exception("Worldgen indexing failed", exception);
        }
    }

    private void CompleteWorldgenIndexing()
    {
        _worldgenEntries.Sort((left, right) => string.Compare(left.SortKey, right.SortKey, StringComparison.OrdinalIgnoreCase));
        _worldgenIndexState = WorldgenIndexState.Ready;
        RebuildVisibleWorldgenEntries();
        _worldgenStatus = $"Indexed {_worldgenEntries.Count} worldgen JSON asset(s).";
        if (_visibleWorldgenEntries.Count > 0 && string.IsNullOrWhiteSpace(_worldgenLoadedKey))
        {
            LoadWorldgenEntry(_visibleWorldgenEntries[Math.Clamp(_worldgenEntryIndex, 0, _visibleWorldgenEntries.Count - 1)]);
        }
    }

    private string BuildWorldgenIndexProgressText()
    {
        return $"Indexing worldgen assets {_worldgenIndexAssetIndex}/{_worldgenIndexAssets.Count}.";
    }

    private void IndexWorldgenAsset(IAsset asset)
    {
        string sourceText = ReadAssetText(asset);
        TryParseJsonToken(sourceText, out JToken? root, out string parseError);
        _worldgenEntries.Add(new WorldgenAssetEntry(asset, sourceText, root, parseError));
    }

    private static bool IsWorldgenJsonAsset(IAsset? asset)
    {
        if (asset?.Location == null) return false;

        string path = asset.Location.Path.Replace('\\', '/');
        return path.StartsWith("worldgen/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildVisibleWorldgenEntries()
    {
        WorldgenAssetEntry? selected = SelectedWorldgenEntry;
        string filter = _worldgenFilter.Trim();
        _visibleWorldgenEntries.Clear();

        foreach (WorldgenAssetEntry entry in _worldgenEntries)
        {
            if (!string.IsNullOrWhiteSpace(filter) && !entry.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ImGuiLayoutHelper.MatchesDomain(_worldgenDomainFilter, entry.Domain)) continue;
            if (_worldgenKindFilter > 0 && entry.Kind != FilterIndexToWorldgenKind(_worldgenKindFilter)) continue;
            if (_worldgenDirtyOnly && !IsWorldgenEntryDirty(entry)) continue;
            _visibleWorldgenEntries.Add(entry);
        }

        if (selected != null)
        {
            int index = _visibleWorldgenEntries.FindIndex(entry => entry.Key.Equals(selected.Key, StringComparison.OrdinalIgnoreCase));
            _worldgenEntryIndex = Math.Max(0, index);
        }
        else
        {
            _worldgenEntryIndex = Math.Clamp(_worldgenEntryIndex, 0, Math.Max(0, _visibleWorldgenEntries.Count - 1));
        }
    }

    private WorldgenAssetEntry? SelectedWorldgenEntry =>
        _visibleWorldgenEntries.Count == 0 ? null : _visibleWorldgenEntries[Math.Clamp(_worldgenEntryIndex, 0, _visibleWorldgenEntries.Count - 1)];

    private void DrawWorldgenBrowser(NVector2 size)
    {
        ImGui.BeginChild("##worldgen-browser", size, true);
        ImGui.TextUnformatted("Worldgen assets");
        ImGui.Separator();

        bool filterChanged = false;
        filterChanged |= ImGui.InputText("Filter##worldgen-filter", ref _worldgenFilter, 256);
        filterChanged |= ImGuiLayoutHelper.DrawDomainCombo("Domain##worldgen-domain", ref _worldgenDomainFilter, _worldgenEntries.Select(entry => entry.Domain));
        filterChanged |= ImGui.Combo("Kind##worldgen-kind", ref _worldgenKindFilter, WorldgenKindFilterLabels, WorldgenKindFilterLabels.Length);
        filterChanged |= ImGui.Checkbox("Dirty only##worldgen-dirty-only", ref _worldgenDirtyOnly);
        if (filterChanged)
        {
            RebuildVisibleWorldgenEntries();
        }

        if (ImGui.Button("Reload index##worldgen-reload"))
        {
            StartWorldgenIndexing(clearLoaded: true);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"{_visibleWorldgenEntries.Count}/{_worldgenEntries.Count}");
        ImGui.Separator();

        if (_worldgenIndexState == WorldgenIndexState.Indexing)
        {
            ImGui.TextWrapped(_worldgenStatus);
        }

        if (ImGui.BeginChild("##worldgen-entry-list", new NVector2(-float.Epsilon, -float.Epsilon), true))
        {
            for (int i = 0; i < _visibleWorldgenEntries.Count; i++)
            {
                WorldgenAssetEntry entry = _visibleWorldgenEntries[i];
                bool dirty = IsWorldgenEntryDirty(entry);
                string label = $"{entry.KindLabel}: {entry.Domain}:{entry.AssetPath}{(dirty ? " *" : "")}##worldgen-entry-{i}";
                if (ImGui.Selectable(label, i == _worldgenEntryIndex))
                {
                    _worldgenEntryIndex = i;
                    LoadWorldgenEntry(entry);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{entry.Domain}:{entry.AssetPath}\n{entry.KindLabel}\n{(dirty ? "Modified draft" : "Unmodified")}");
                }
            }
        }
        ImGui.EndChild();
        ImGui.EndChild();
    }

    private void DrawWorldgenEditorPanel(NVector2 size)
    {
        ImGui.BeginChild("##worldgen-editor", size, true);

        WorldgenAssetEntry? entry = SelectedWorldgenEntry;
        if (entry == null)
        {
            ImGui.TextWrapped(_worldgenIndexState == WorldgenIndexState.Indexing ? _worldgenStatus : "No worldgen JSON asset selected.");
            ImGui.EndChild();
            return;
        }

        EnsureWorldgenEntryLoaded(entry);
        ImGui.TextUnformatted($"{entry.KindLabel}: {entry.Domain}:{entry.AssetPath}");
        ImGui.SameLine();
        if (IsWorldgenEntryDirty(entry))
        {
            ImGui.TextColored(new NVector4(1f, 0.72f, 0.30f, 1f), "modified");
        }
        ImGui.Separator();

        if (!TryParseJsonToken(_worldgenCurrentText, out JToken? root, out string parseError) || root == null)
        {
            ImGui.TextColored(new NVector4(1f, 0.35f, 0.25f, 1f), $"Invalid JSON: {parseError}");
            DrawWorldgenRawJsonEditor();
            ImGui.EndChild();
            return;
        }

        if (TryGetWorldgenRows(root, entry.Kind, out JArray? rows, out string rowsLabel) && rows != null)
        {
            DrawWorldgenRowsEditor(entry, root, rows, rowsLabel);
        }
        else
        {
            ImGui.TextWrapped("This worldgen file is not one of the row shapes with first-class controls. Edit the structured JSON directly.");
            DrawWorldgenRawJsonEditor();
        }

        ImGui.EndChild();
    }

    private void DrawWorldgenRowsEditor(WorldgenAssetEntry entry, JToken root, JArray rows, string rowsLabel)
    {
        ImGui.TextUnformatted($"{rowsLabel}: {rows.Count} row(s)");

        float rowListHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y * 0.28f, 120f, 260f);
        if (ImGui.BeginChild("##worldgen-row-list", new NVector2(-float.Epsilon, rowListHeight), true))
        {
            _worldgenRowIndex = Math.Clamp(_worldgenRowIndex, 0, Math.Max(0, rows.Count - 1));
            for (int i = 0; i < rows.Count; i++)
            {
                string label = GetWorldgenRowLabel(entry.Kind, rows[i], i);
                if (ImGui.Selectable($"{label}##worldgen-row-{i}", i == _worldgenRowIndex))
                {
                    _worldgenRowIndex = i;
                    RememberWorldgenDraft();
                    InvalidateWorldgenPreviewRasterCache();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(rows[i].ToString(Formatting.Indented));
                }
            }
        }
        ImGui.EndChild();

        if (rows.Count == 0)
        {
            ImGui.TextWrapped("No rows in this asset.");
            return;
        }

        _worldgenRowIndex = Math.Clamp(_worldgenRowIndex, 0, rows.Count - 1);
        if (rows[_worldgenRowIndex] is not JObject row)
        {
            ImGui.TextWrapped("Selected row is not an object. Edit the whole file as JSON.");
            DrawWorldgenRawJsonEditor();
            return;
        }

        ImGui.Separator();
        bool changed = entry.Kind switch
        {
            WorldgenAssetKind.Deposits => DrawDepositWorldgenRow(row),
            WorldgenAssetKind.BlockPatches => DrawBlockPatchWorldgenRow(row),
            WorldgenAssetKind.Landforms => DrawLandformWorldgenRow(row),
            WorldgenAssetKind.RockStrata => DrawRockStrataWorldgenRow(row),
            _ => false
        };

        if (changed)
        {
            SetWorldgenCurrentRoot(root);
        }

        if (ImGui.CollapsingHeader("Selected row JSON##worldgen-row-json"))
        {
            string rowText = row.ToString(Formatting.Indented);
            if (ImGui.InputTextMultiline("##worldgen-row-json-text", ref rowText, 256 * 1024, new NVector2(-float.Epsilon, 180f), ImGuiInputTextFlags.AllowTabInput))
            {
                try
                {
                    JToken replacement = JToken.Parse(rowText);
                    rows[_worldgenRowIndex] = replacement;
                    SetWorldgenCurrentRoot(root);
                }
                catch (Exception exception)
                {
                    _worldgenValidationStatus = $"Selected row JSON parse error: {exception.Message}";
                    _worldgenTextValid = false;
                }
            }
        }

        if (ImGui.CollapsingHeader("Full file JSON##worldgen-full-json"))
        {
            DrawWorldgenRawJsonEditor();
        }
    }

    private bool DrawDepositWorldgenRow(JObject row)
    {
        bool changed = false;
        changed |= DrawWorldgenStringField(row, "code", "Code");
        changed |= DrawWorldgenStringField(row, "generator", "Generator");
        changed |= DrawWorldgenIntField(row, "triesPerChunk", "Tries/chunk");
        changed |= DrawWorldgenFloatField(row, "chance", "Chance");
        changed |= DrawWorldgenFloatField(row, "chanceMultiplier", "Chance multiplier");
        changed |= DrawWorldgenBoolField(row, "withOreMap", "With ore map");
        changed |= DrawWorldgenStringField(row, "handbookPageCode", "Handbook code");
        changed |= DrawWorldgenStringField(row, "oreMapCode", "Ore map code");
        changed |= DrawWorldgenObjectJsonField(row, "attributes", "Attributes JSON");
        return changed;
    }

    private bool DrawBlockPatchWorldgenRow(JObject row)
    {
        bool changed = false;
        changed |= DrawWorldgenStringArrayField(row, "blockCodes", "Block codes");
        changed |= DrawWorldgenFloatField(row, "chance", "Chance");
        changed |= DrawWorldgenNatFloatField(row, "quantity", "Quantity");
        changed |= DrawWorldgenNatFloatField(row, "offsetX", "Offset X");
        changed |= DrawWorldgenNatFloatField(row, "offsetY", "Offset Y");
        changed |= DrawWorldgenNatFloatField(row, "offsetZ", "Offset Z");
        changed |= DrawWorldgenFloatField(row, "minTemp", "Min temp");
        changed |= DrawWorldgenFloatField(row, "maxTemp", "Max temp");
        changed |= DrawWorldgenFloatField(row, "minRain", "Min rain");
        changed |= DrawWorldgenFloatField(row, "maxRain", "Max rain");
        changed |= DrawWorldgenFloatField(row, "minForest", "Min forest");
        changed |= DrawWorldgenFloatField(row, "maxForest", "Max forest");
        changed |= DrawWorldgenFloatField(row, "minFertility", "Min fertility");
        changed |= DrawWorldgenFloatField(row, "maxFertility", "Max fertility");
        changed |= DrawWorldgenFloatField(row, "minY", "Min Y");
        changed |= DrawWorldgenFloatField(row, "maxY", "Max Y");
        return changed;
    }

    private bool DrawLandformWorldgenRow(JObject row)
    {
        bool changed = false;
        changed |= DrawWorldgenStringField(row, "code", "Code");
        changed |= DrawWorldgenFloatField(row, "weight", "Weight");
        changed |= DrawWorldgenStringField(row, "group", "Group");
        changed |= DrawWorldgenStringField(row, "hexcolor", "Color");
        changed |= DrawWorldgenFloatArrayField(row, "terrainOctaves", "Terrain octaves");
        changed |= DrawWorldgenFloatArrayField(row, "terrainOctaveThresholds", "Octave thresholds");
        changed |= DrawWorldgenFloatArrayField(row, "terrainYKeyPositions", "Y key positions");
        changed |= DrawWorldgenFloatArrayField(row, "terrainYKeyThresholds", "Y key thresholds");
        return changed;
    }

    private bool DrawRockStrataWorldgenRow(JObject row)
    {
        bool changed = false;
        changed |= DrawWorldgenStringField(row, "blockcode", "Block code");
        changed |= DrawWorldgenFloatField(row, "weight", "Weight");
        changed |= DrawWorldgenStringField(row, "rockGroup", "Rock group");
        changed |= DrawWorldgenStringField(row, "genDir", "Generation direction");
        changed |= DrawWorldgenStringField(row, "hexcolor", "Color");
        changed |= DrawWorldgenFloatArrayField(row, "amplitudes", "Amplitudes");
        changed |= DrawWorldgenFloatArrayField(row, "thresholds", "Thresholds");
        changed |= DrawWorldgenFloatArrayField(row, "frequencies", "Frequencies");
        changed |= DrawWorldgenFloatArrayField(row, "yKeyPositions", "Y key positions");
        changed |= DrawWorldgenFloatArrayField(row, "yKeyThresholds", "Y key thresholds");
        return changed;
    }

    private bool DrawWorldgenStringField(JObject row, string propertyName, string label)
    {
        string value = row[propertyName]?.ToString() ?? "";
        if (!ImGui.InputText($"{label}##worldgen-{propertyName}", ref value, 512)) return false;

        SetOrRemoveString(row, propertyName, value);
        return true;
    }

    private bool DrawWorldgenStringArrayField(JObject row, string propertyName, string label)
    {
        string value = row[propertyName] is JArray array
            ? string.Join(", ", array.Select(token => token.ToString()))
            : row[propertyName]?.ToString() ?? "";
        if (!ImGui.InputText($"{label}##worldgen-{propertyName}", ref value, 4096)) return false;

        row[propertyName] = new JArray(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return true;
    }

    private bool DrawWorldgenFloatArrayField(JObject row, string propertyName, string label)
    {
        string value = row[propertyName] is JArray array
            ? string.Join(", ", array.Select(token => FormatJsonNumber(token)))
            : row[propertyName]?.ToString() ?? "";
        if (!ImGui.InputText($"{label}##worldgen-{propertyName}", ref value, 4096)) return false;

        JArray replacement = [];
        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (float.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
            {
                replacement.Add(parsed);
                continue;
            }

            _worldgenTextValid = false;
            _worldgenValidationStatus = $"{label} contains a non-numeric value: {part}";
            return false;
        }

        row[propertyName] = replacement;
        return true;
    }

    private bool DrawWorldgenIntField(JObject row, string propertyName, string label)
    {
        int value = row[propertyName]?.Value<int?>() ?? 0;
        if (!ImGui.InputInt($"{label}##worldgen-{propertyName}", ref value)) return false;

        row[propertyName] = value;
        return true;
    }

    private bool DrawWorldgenFloatField(JObject row, string propertyName, string label)
    {
        float value = row[propertyName]?.Value<float?>() ?? 0f;
        if (!ImGui.InputFloat($"{label}##worldgen-{propertyName}", ref value)) return false;

        row[propertyName] = value;
        return true;
    }

    private bool DrawWorldgenBoolField(JObject row, string propertyName, string label)
    {
        bool value = row[propertyName]?.Value<bool?>() ?? false;
        if (!ImGui.Checkbox($"{label}##worldgen-{propertyName}", ref value)) return false;

        row[propertyName] = value;
        return true;
    }

    private bool DrawWorldgenNatFloatField(JObject row, string propertyName, string label)
    {
        JObject natFloat = row[propertyName] as JObject ?? new JObject();
        bool exists = row[propertyName] is JObject;
        bool changed = false;

        ImGui.PushID($"worldgen-natfloat-{propertyName}");
        if (!exists)
        {
            if (ImGui.Button($"Add {label}"))
            {
                natFloat["avg"] = 0f;
                natFloat["var"] = 0f;
                natFloat["dist"] = "uniform";
                row[propertyName] = natFloat;
                changed = true;
            }

            ImGui.PopID();
            return changed;
        }

        if (ImGui.CollapsingHeader(label))
        {
            changed |= DrawWorldgenStringField(natFloat, "dist", "Distribution");
            changed |= DrawWorldgenFloatField(natFloat, "avg", "Average");
            changed |= DrawWorldgenFloatField(natFloat, "var", "Variance");
            changed |= DrawWorldgenFloatField(natFloat, "offset", "Offset");
            if (ImGui.Button("Remove"))
            {
                row.Remove(propertyName);
                changed = true;
            }
        }

        ImGui.PopID();
        return changed;
    }

    private bool DrawWorldgenObjectJsonField(JObject row, string propertyName, string label)
    {
        string value = row[propertyName]?.ToString(Formatting.Indented) ?? "{}";
        if (!ImGui.CollapsingHeader(label)) return false;

        if (!ImGui.InputTextMultiline($"##worldgen-object-json-{propertyName}", ref value, 256 * 1024, new NVector2(-float.Epsilon, 160f), ImGuiInputTextFlags.AllowTabInput)) return false;

        try
        {
            row[propertyName] = JToken.Parse(value);
            return true;
        }
        catch (Exception exception)
        {
            _worldgenTextValid = false;
            _worldgenValidationStatus = $"{label} parse error: {exception.Message}";
            return false;
        }
    }

    private void DrawWorldgenRawJsonEditor()
    {
        int textCapacity = Math.Max(_worldgenCurrentText.Length + 8192, 2 * 1024 * 1024);
        if (ImGui.InputTextMultiline("##worldgen-json-text", ref _worldgenCurrentText, (uint)textCapacity, new NVector2(-float.Epsilon, Math.Max(180f, ImGui.GetContentRegionAvail().Y - 24f)), ImGuiInputTextFlags.AllowTabInput))
        {
            ValidateWorldgenCurrentText();
            RememberWorldgenDraft();
            InvalidateWorldgenPreviewRasterCache();
        }
    }

    private void DrawWorldgenInspector(NVector2 size, bool showDiagnostics)
    {
        ImGui.BeginChild("##worldgen-inspector", size, true);
        DrawWorldgenPreviewViewport();
        ImGui.Separator();

        WorldgenAssetEntry? entry = SelectedWorldgenEntry;
        if (entry == null)
        {
            ImGui.TextWrapped(_worldgenStatus);
            _worldgenDiagnostics.Draw("worldgen-inspector-diagnostics", showDiagnostics);
            ImGui.EndChild();
            return;
        }

        EnsureWorldgenEntryLoaded(entry);
        bool dirty = IsWorldgenEntryDirty(entry);
        ImGui.TextWrapped($"Source: {entry.Domain}:{entry.AssetPath}");
        ImGui.TextWrapped($"Kind: {entry.KindLabel}");
        ImGui.TextWrapped(dirty ? "Draft: modified" : "Draft: clean");
        ImGui.TextWrapped(_worldgenValidationStatus);

        if (!string.IsNullOrWhiteSpace(_worldgenStatus))
        {
            ImGui.Separator();
            ImGui.TextWrapped(_worldgenStatus);
        }

        ImGui.Separator();
        bool canSave = dirty && _worldgenTextValid;
        if (!canSave) ImGui.BeginDisabled();
        if (ImGui.Button("Save authored file##worldgen-save"))
        {
            QueueSourceSave(TrySaveWorldgenToSource(entry), status => _worldgenStatus = status);
        }
        if (!canSave) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Revert draft##worldgen-revert"))
        {
            _worldgenCurrentText = _worldgenOriginalText;
            _worldgenDraftStates.Remove(entry.Key);
            ValidateWorldgenCurrentText();
            _worldgenStatus = $"Reverted draft for {entry.Domain}:{entry.AssetPath}.";
        }

        if (ImGui.Button("Copy JSON##worldgen-copy-json"))
        {
            ImGui.SetClipboardText(_worldgenCurrentText);
            _worldgenStatus = "Copied worldgen JSON to clipboard.";
        }

        ImGui.Separator();
        _worldgenDiagnostics.Draw("worldgen-inspector-diagnostics", showDiagnostics);
        ImGui.EndChild();
    }

    private void DrawWorldgenPreviewViewport()
    {
        EnsureWorldgenPreviewDefaults();

        ImGui.TextUnformatted("Worldgen preview");
        if (ImGui.Checkbox("Auto mode by asset##worldgen-preview-auto-mode", ref _worldgenPreviewAutoMode) && _worldgenPreviewAutoMode)
        {
            ApplyWorldgenPreviewModeForSelectedEntry();
        }
        ImGui.TextDisabled(GetWorldgenPreviewAutoModeStatus());
        ImGui.SetNextItemWidth(-float.Epsilon);
        int previewModeBefore = _worldgenPreviewMode;
        if (ImGui.Combo("##worldgen-preview-mode", ref _worldgenPreviewMode, WorldgenPreviewModeLabels, WorldgenPreviewModeLabels.Length))
        {
            _worldgenPreviewAutoMode = false;
            if (_worldgenPreviewMode != previewModeBefore)
            {
                _worldgenPreviewMapLayer = null;
                InvalidateWorldgenPreviewRasterCache();
            }
        }

        ImGui.SetNextItemWidth(-float.Epsilon);
        ImGui.InputText("Seed##worldgen-preview-seed", ref _worldgenPreviewSeedText, 64);

        float halfWidth = Math.Max(90f, ImGui.GetContentRegionAvail().X * 0.48f);
        ImGui.PushItemWidth(halfWidth);
        ImGui.InputInt("Origin X##worldgen-preview-origin-x", ref _worldgenPreviewOriginX);
        ImGui.SameLine();
        ImGui.InputInt("Z##worldgen-preview-origin-z", ref _worldgenPreviewOriginZ);
        if (ImGui.InputInt("Resolution##worldgen-preview-resolution", ref _worldgenPreviewResolution))
        {
            _worldgenPreviewResolution = Math.Clamp(_worldgenPreviewResolution, 32, 192);
            InvalidateWorldgenPreviewRasterCache();
        }
        ImGui.PopItemWidth();

        if (_worldgenPreviewMode == WorldgenPreviewModeClimate)
        {
            DrawWorldgenClimatePreviewControls();
        }
        else if (_worldgenPreviewMode == WorldgenPreviewModeOre)
        {
            DrawWorldgenOrePreviewControls();
        }
        else if (_worldgenPreviewMode == WorldgenPreviewModeBlockPatch)
        {
            DrawWorldgenBlockPatchPreviewControls();
        }
        else if (_worldgenPreviewMode == WorldgenPreviewModeTerrainShape)
        {
            DrawWorldgenTerrainShapePreviewControls();
        }
        else if (_worldgenPreviewMode == WorldgenPreviewModeRegion3D)
        {
            DrawWorldgenRegion3DPreviewControls();
        }
        else if (WorldgenPreviewModeUsesMapLayer(_worldgenPreviewMode))
        {
            DrawWorldgenMapLayerPreviewControls();
        }

        if (ImGui.Button("Use current world##worldgen-preview-current"))
        {
            UseCurrentWorldgenPreviewState();
        }
        ImGui.SameLine();
        if (ImGui.Button("Refresh SP##worldgen-preview-server-refresh"))
        {
            RefreshWorldgenServerApi();
        }
        ImGui.SameLine();
        if (ImGui.Button("Reset view##worldgen-preview-reset"))
        {
            _worldgenPreviewPanX = 0f;
            _worldgenPreviewPanZ = 0f;
            _worldgenPreviewZoom = 1f;
            _worldgenPreview3DYaw = MathF.PI * 0.25f;
            _worldgenPreview3DPitch = 0.70f;
        }

        float availableHeight = ImGui.GetContentRegionAvail().Y;
        float height = Math.Clamp(availableHeight * 0.46f, 220f, Math.Max(220f, availableHeight - 260f));
        ImGui.BeginChild("##worldgen-preview-viewport", new NVector2(-float.Epsilon, height), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        NVector2 min = ImGui.GetWindowPos();
        NVector2 actual = ImGui.GetWindowSize();
        NVector2 max = new(min.X + actual.X, min.Y + actual.Y);
        bool hovered = ImGui.IsWindowHovered();
        long seed = ParseWorldgenPreviewSeed();
        float pixelsPerBlock = Math.Clamp(2.5f * _worldgenPreviewZoom, 0.35f, 32f);
        float centerX = _worldgenPreviewOriginX + _worldgenPreviewPanX;
        float centerZ = _worldgenPreviewOriginZ + _worldgenPreviewPanZ;
        bool serverRequired = WorldgenPreviewModeRequiresServer(_worldgenPreviewMode);
        bool serverAvailable = _worldgenPreviewServerApi != null;

        if (hovered)
        {
            NVector2 delta = ImGui.GetIO().MouseDelta;
            bool pan = ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || ImGui.IsMouseDragging(ImGuiMouseButton.Right);
            if (pan)
            {
                _worldgenPreviewPanX = Math.Clamp(_worldgenPreviewPanX - delta.X / pixelsPerBlock, -200000f, 200000f);
                _worldgenPreviewPanZ = Math.Clamp(_worldgenPreviewPanZ - delta.Y / pixelsPerBlock, -200000f, 200000f);
                centerX = _worldgenPreviewOriginX + _worldgenPreviewPanX;
                centerZ = _worldgenPreviewOriginZ + _worldgenPreviewPanZ;
            }

            float wheel = ImGui.GetIO().MouseWheel;
            if (Math.Abs(wheel) > 0.001f)
            {
                _worldgenPreviewZoom = Math.Clamp(_worldgenPreviewZoom * (1f + wheel * 0.12f), 0.15f, 12f);
                pixelsPerBlock = Math.Clamp(2.5f * _worldgenPreviewZoom, 0.35f, 32f);
            }

            if (_worldgenPreviewMode == WorldgenPreviewModeRegion3D && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                _worldgenPreview3DYaw += delta.X * 0.012f;
                _worldgenPreview3DPitch = Math.Clamp(_worldgenPreview3DPitch + delta.Y * 0.006f, 0.24f, 1.12f);
            }
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(min, max, true);
        if (!serverRequired || serverAvailable)
        {
            if (_worldgenPreviewMode == WorldgenPreviewModeRegion3D)
            {
                DrawWorldgenLandformSurfacePreview(drawList, min, max, seed, centerX, centerZ, pixelsPerBlock);
            }
            else
            {
                DrawWorldgenPreviewRaster(drawList, min, max, seed, centerX, centerZ, pixelsPerBlock);
                DrawWorldgenPreviewGrid(drawList, min, max, centerX, centerZ, pixelsPerBlock);
            }
        }
        else
        {
            DrawWorldgenPreviewUnavailable(drawList, min, max);
        }
        drawList.PopClipRect();

        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 1f));
        uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.88f, 0.84f, 0.74f, 1f));
        uint muted = ImGui.ColorConvertFloat4ToU32(new NVector4(0.72f, 0.68f, 0.58f, 1f));
        drawList.AddRect(min, max, border, 4f);

        NVector2 mouse = ImGui.GetIO().MousePos;
        int hoverX = (int)MathF.Floor(centerX + (mouse.X - (min.X + actual.X * 0.5f)) / pixelsPerBlock);
        int hoverZ = (int)MathF.Floor(centerZ + (mouse.Y - (min.Y + actual.Y * 0.5f)) / pixelsPerBlock);
        string modeLabel = WorldgenPreviewModeLabels[Math.Clamp(_worldgenPreviewMode, 0, WorldgenPreviewModeLabels.Length - 1)];
        string hoverDetails = BuildWorldgenPreviewHoverText(_worldgenPreviewMode, seed, hoverX, hoverZ);
        string modeStatus = WorldgenPreviewModeUsesMapLayer(_worldgenPreviewMode)
            ? $"{modeLabel}: live server {GetWorldgenPreviewMapLayerFieldName(_worldgenPreviewMode)}"
            : _worldgenPreviewMode == WorldgenPreviewModeClimate
                ? $"{modeLabel}: live server climateGen"
                : _worldgenPreviewMode == WorldgenPreviewModeBlockPatch
                    ? $"{modeLabel}: selected draft row"
                    : _worldgenPreviewMode == WorldgenPreviewModeTerrainShape
                        ? $"{modeLabel}: selected draft landform"
                        : _worldgenPreviewMode == WorldgenPreviewModeRegion3D
                            ? _worldgenPreviewPeekProfile == null
                                ? $"{modeLabel}: draft landform surface"
                                : $"{modeLabel}: real peeked region"
                : $"{modeLabel}: viewport host";
        string inputStatus = WorldgenPreviewModeUsesMapLayer(_worldgenPreviewMode)
            ? "RMB/MMB pans. Mouse wheel zooms. Sampling live MapLayerBase.GenLayer."
            : _worldgenPreviewMode == WorldgenPreviewModeBlockPatch
                ? "RMB/MMB pans. Mouse wheel zooms. Evaluating draft climate/forest constraints."
                    : _worldgenPreviewMode == WorldgenPreviewModeTerrainShape
                        ? "RMB/MMB pans. Mouse wheel zooms. Evaluating selected landform draft shape."
                        : _worldgenPreviewMode == WorldgenPreviewModeRegion3D
                            ? "LMB orbits. RMB/MMB pans. Mouse wheel zooms. Peek renders the selected engine pass when available."
                : "RMB/MMB pans. Mouse wheel zooms. Simulation layers are deferred.";
        drawList.AddText(new NVector2(min.X + 12f, min.Y + 10f), text, modeStatus);
        drawList.AddText(new NVector2(min.X + 12f, min.Y + 30f), muted, inputStatus);
        drawList.AddText(new NVector2(min.X + 12f, min.Y + 50f), muted, $"Cursor block: X {hoverX}, Z {hoverZ}");
        drawList.AddText(new NVector2(min.X + 12f, min.Y + 70f), muted, _worldgenPreviewConfigStatus);
        drawList.AddText(new NVector2(min.X + 12f, min.Y + 90f), muted, serverRequired ? _worldgenPreviewServerStatus : "Singleplayer server API: not required for this mode.");
        if (!string.IsNullOrWhiteSpace(hoverDetails))
        {
            drawList.AddText(new NVector2(min.X + 12f, min.Y + 110f), muted, hoverDetails);
        }
        drawList.AddText(new NVector2(min.X + 12f, min.Y + 130f), muted, _worldgenPreviewRasterStatus);
        if (_worldgenPreviewMode == WorldgenPreviewModeRegion3D)
        {
            drawList.AddText(new NVector2(min.X + 12f, min.Y + 150f), muted, _worldgenPreviewPeekStatus);
        }

        ImGui.EndChild();
    }

    private void DrawWorldgenClimatePreviewControls()
    {
        ImGui.TextDisabled("Using the running server's climateGen layer; climate scale comes from the active world config.");
    }

    private void DrawWorldgenMapLayerPreviewControls()
    {
        ImGui.TextDisabled("Using the running server's initialized GenMaps layer; map scale comes from the active world config.");
    }

    private void DrawWorldgenOrePreviewControls()
    {
        if (TryGetWorldgenPreviewDepositVariant(out DepositVariant? variant, out string? code, out string source, out string status))
        {
            string generator = TryGetWorldgenPreviewDepositGenerator(variant!) ?? "unknown generator";
            ImGui.TextDisabled($"Using {source}: {code ?? "unnamed"} ({generator}).");
        }
        else
        {
            ImGui.TextDisabled(status);
        }
    }

    private void DrawWorldgenBlockPatchPreviewControls()
    {
        if (TryGetSelectedWorldgenBlockPatchRow(out JObject? row) && row != null)
        {
            string label = GetWorldgenRowLabel(WorldgenAssetKind.BlockPatches, row, _worldgenRowIndex);
            ImGui.TextDisabled($"Using selected draft block patch: {label}.");
            ImGui.TextDisabled("Suitability uses live climate/forest maps plus draft temp/rain/forest/chance constraints.");
            ImGui.TextDisabled("Fertility and Y constraints are shown in JSON but deferred until the terrain/surface pass.");
        }
        else
        {
            ImGui.TextDisabled("Select a block patch row to preview its draft suitability.");
        }
    }

    private void DrawWorldgenTerrainShapePreviewControls()
    {
        if (TryGetSelectedWorldgenLandformRow(out JObject? row) && row != null)
        {
            string label = GetWorldgenRowLabel(WorldgenAssetKind.Landforms, row, _worldgenRowIndex);
            ImGui.TextDisabled($"Using selected draft landform: {label}.");
            ImGui.TextDisabled("Heightmap uses terrainOctaves, terrainOctaveThresholds, terrainYKeyPositions, and terrainYKeyThresholds.");
            ImGui.TextDisabled("This is a Speed-1 draft shape visualization; exact ColumnNoise/GenTerra wiring remains for the next fidelity pass.");
        }
        else
        {
            ImGui.TextDisabled("Select a landform row to preview its draft terrain shape.");
        }
    }

    private void DrawWorldgenRegion3DPreviewControls()
    {
        if (TryGetSelectedWorldgenLandformRow(out JObject? row) && row != null)
        {
            string label = GetWorldgenRowLabel(WorldgenAssetKind.Landforms, row, _worldgenRowIndex);
            ImGui.TextDisabled($"3D draft surface: {label}.");
            ImGui.TextDisabled(_worldgenPreviewPeekProfile == null
                ? "Uses the selected landform height field until a real terrain region is peeked."
                : "Rendering the last real chunk region returned by PeekChunkColumn.");
        }
        else
        {
            ImGui.TextDisabled("Select a landform row, then choose 3D region to view its draft surface.");
        }

        int previousRegionSize = _worldgenPreviewRegionSize;
        ImGui.SetNextItemWidth(Math.Max(90f, ImGui.GetContentRegionAvail().X * 0.35f));
        if (ImGui.SliderInt("Region chunks##worldgen-peek-region-size", ref _worldgenPreviewRegionSize, 1, 3))
        {
            _worldgenPreviewRegionSize = Math.Clamp(_worldgenPreviewRegionSize, 1, 3);
            if (_worldgenPreviewRegionSize != previousRegionSize)
            {
                ClearWorldgenPeekProfile("Region size changed; peek again to refresh the real 3D preview.");
            }
        }

        int previousPassIndex = _worldgenPreviewPassIndex;
        ImGui.SetNextItemWidth(Math.Max(150f, ImGui.GetContentRegionAvail().X * 0.50f));
        if (ImGui.Combo("Pass##worldgen-peek-pass", ref _worldgenPreviewPassIndex, WorldgenPeekPassLabels, WorldgenPeekPassLabels.Length))
        {
            _worldgenPreviewPassIndex = Math.Clamp(_worldgenPreviewPassIndex, 0, WorldgenPeekPassLabels.Length - 1);
            if (_worldgenPreviewPassIndex != previousPassIndex)
            {
                ClearWorldgenPeekProfile("Worldgen pass changed; peek again to refresh the real 3D preview.");
            }
        }

        bool canPeek = _worldgenPreviewServerApi != null && !_worldgenPreviewPeekPending;
        if (!canPeek) ImGui.BeginDisabled();
        if (ImGui.Button("Peek region##worldgen-peek-region"))
        {
            RequestWorldgenPeekRegion();
        }
        if (!canPeek) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Clear peek##worldgen-clear-peek"))
        {
            ClearWorldgenPeekProfile("No real chunk peek requested yet.");
        }

        ImGui.TextDisabled(_worldgenPreviewPeekStatus);
        if (_worldgenPreviewPeekProfile is { } profile)
        {
            ImGui.TextDisabled($"Last real region: chunks {profile.OriginChunkX},{profile.OriginChunkZ} size {profile.RegionSizeChunks}x{profile.RegionSizeChunks}; pass {profile.PassLabel}; height {profile.MinHeight}-{profile.MaxHeight}; avg {profile.AverageHeight:0.0}.");
            ImGui.TextDisabled($"Sample row: {profile.SampleSummary}");
        }
    }

    private void EnsureWorldgenPreviewDefaults()
    {
        if (_worldgenPreviewInitialized) return;

        _worldgenPreviewInitialized = true;
        UseCurrentWorldgenPreviewState();
    }

    private void ApplyWorldgenPreviewModeForSelectedEntry()
    {
        WorldgenAssetEntry? entry = SelectedWorldgenEntry;
        if (entry != null)
        {
            ApplyWorldgenPreviewModeForEntry(entry);
        }
    }

    private void ApplyWorldgenPreviewModeForEntry(WorldgenAssetEntry entry)
    {
        if (!_worldgenPreviewAutoMode) return;

        int nextMode = GetWorldgenPreviewModeForKind(entry.Kind);
        if (_worldgenPreviewMode == nextMode) return;

        _worldgenPreviewMode = nextMode;
        _worldgenPreviewMapLayer = null;
        InvalidateWorldgenPreviewRasterCache();
    }

    private string GetWorldgenPreviewAutoModeStatus()
    {
        if (!_worldgenPreviewAutoMode)
        {
            return "Auto mode by asset: off";
        }

        WorldgenAssetEntry? entry = SelectedWorldgenEntry;
        if (entry == null)
        {
            return "Auto mode by asset: on; no asset selected.";
        }

        int mode = GetWorldgenPreviewModeForKind(entry.Kind);
        string modeLabel = WorldgenPreviewModeLabels[Math.Clamp(mode, 0, WorldgenPreviewModeLabels.Length - 1)];
        return $"Auto mode by asset: {entry.KindLabel} -> {modeLabel}";
    }

    private static int GetWorldgenPreviewModeForKind(WorldgenAssetKind kind)
    {
        return kind switch
        {
            WorldgenAssetKind.Deposits => WorldgenPreviewModeOre,
            WorldgenAssetKind.BlockPatches => WorldgenPreviewModeBlockPatch,
            WorldgenAssetKind.Landforms => WorldgenPreviewModeTerrainShape,
            WorldgenAssetKind.RockStrata => WorldgenPreviewModeTerrainShape,
            _ => WorldgenPreviewModeGradient
        };
    }

    private void UseCurrentWorldgenPreviewState()
    {
        _worldgenPreviewSeedText = GetCurrentWorldgenSeedText();
        _worldgenPreviewConfigStatus = GetCurrentWorldgenConfigSummary();
        RefreshWorldgenServerApi();

        try
        {
            EntityPlayer? player = _api.World.Player?.Entity;
            if (player != null)
            {
                _worldgenPreviewOriginX = (int)Math.Floor(player.Pos.X);
                _worldgenPreviewOriginZ = (int)Math.Floor(player.Pos.Z);
            }
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen preview could not read player position", exception);
        }
    }

    private string GetCurrentWorldgenSeedText()
    {
        object? seed = TryGetReflectedProperty(_api.World, "Seed");
        return FormatInvariant(seed, "0");
    }

    private string GetCurrentWorldgenConfigSummary()
    {
        List<string> parts = [];
        object? seaLevel = TryGetReflectedProperty(_api.World, "SeaLevel");
        if (seaLevel != null)
        {
            parts.Add($"sea {FormatInvariant(seaLevel, "?")}");
        }

        object? mapSizeY = TryGetReflectedProperty(_api.World, "MapSizeY");
        if (mapSizeY != null)
        {
            parts.Add($"height {FormatInvariant(mapSizeY, "?")}");
        }

        object? config = TryGetReflectedProperty(_api.World, "Config");
        if (config != null)
        {
            parts.Add($"config {config.GetType().Name}");
        }

        return parts.Count == 0
            ? "World config: not exposed by client API yet."
            : $"World config: {string.Join(", ", parts)}";
    }

    private static object? TryGetReflectedProperty(object? instance, string propertyName)
    {
        if (instance == null) return null;

        try
        {
            return instance.GetType()
                .GetProperty(propertyName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static object? TryGetReflectedMember(object? instance, string memberName)
    {
        if (instance == null) return null;

        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        Type type = instance.GetType();
        try
        {
            System.Reflection.PropertyInfo? property = type.GetProperty(memberName, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(instance);
            }
        }
        catch
        {
            // Field fallback below.
        }

        try
        {
            return type.GetField(memberName, flags)?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatInvariant(object? value, string fallback)
    {
        if (value == null) return fallback;
        return value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value.ToString() ?? fallback;
    }

    private long ParseWorldgenPreviewSeed()
    {
        return long.TryParse(_worldgenPreviewSeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seed)
            ? seed
            : 0L;
    }

    private string BuildWorldgenPreviewHoverText(int mode, long seed, int blockX, int blockZ)
    {
        _ = seed;

        if (mode == WorldgenPreviewModeOre)
        {
            return BuildWorldgenOreHoverText(blockX, blockZ);
        }

        if (mode == WorldgenPreviewModeBlockPatch)
        {
            return BuildWorldgenBlockPatchHoverText(blockX, blockZ);
        }

        if (mode == WorldgenPreviewModeTerrainShape)
        {
            return BuildWorldgenTerrainShapeHoverText(seed, blockX, blockZ);
        }

        if (WorldgenPreviewModeUsesMapLayer(mode))
        {
            MapLayerBase? layer = GetWorldgenPreviewMapLayer(mode);
            if (layer == null) return _worldgenPreviewMapLayerStatus;

            try
            {
                int value = layer.GenLayer(blockX, blockZ, 1, 1)[0];
                return FormatWorldgenMapLayerValue(mode, value, layer);
            }
            catch (Exception exception)
            {
                _worldgenPreviewMapLayer = null;
                _worldgenPreviewMapLayerStatus = $"{WorldgenPreviewModeLabels[Math.Clamp(mode, 0, WorldgenPreviewModeLabels.Length - 1)]} sample failed: {exception.Message}";
                _worldgenDiagnostics.Exception("Worldgen map layer hover sample failed", exception);
                return _worldgenPreviewMapLayerStatus;
            }
        }

        return "";
    }

    private MapLayerBase? GetWorldgenPreviewMapLayer(int mode)
    {
        if (_worldgenPreviewMapLayer != null &&
            _worldgenPreviewMapLayerMode == mode)
        {
            return _worldgenPreviewMapLayer;
        }

        try
        {
            GenMaps? genMaps = GetWorldgenPreviewGenMaps();
            if (genMaps == null)
            {
                _worldgenPreviewMapLayer = null;
                _worldgenPreviewMapLayerMode = mode;
                _worldgenPreviewMapLayerStatus = "Live GenMaps unavailable; open a singleplayer world and press Refresh SP.";
                return null;
            }

            _worldgenPreviewMapLayer = mode switch
            {
                WorldgenPreviewModeClimate => genMaps.climateGen,
                WorldgenPreviewModeForest => genMaps.forestGen,
                WorldgenPreviewModeUpheaval => genMaps.upheavelGen,
                WorldgenPreviewModeOcean => genMaps.oceanGen,
                WorldgenPreviewModeLandform => genMaps.landformsGen,
                WorldgenPreviewModeProvince => genMaps.geologicprovinceGen,
                _ => null
            };
            _worldgenPreviewMapLayerMode = mode;
            _worldgenPreviewMapLayerStatus = _worldgenPreviewMapLayer == null
                ? "Map layer mode has no live GenMaps layer yet."
                : $"{WorldgenPreviewModeLabels[Math.Clamp(mode, 0, WorldgenPreviewModeLabels.Length - 1)]}: live GenMaps.{GetWorldgenPreviewMapLayerFieldName(mode)}.";
            return _worldgenPreviewMapLayer;
        }
        catch (Exception exception)
        {
            _worldgenPreviewMapLayer = null;
            _worldgenPreviewMapLayerStatus = $"{WorldgenPreviewModeLabels[Math.Clamp(mode, 0, WorldgenPreviewModeLabels.Length - 1)]} live layer unavailable: {exception.Message}";
            _worldgenDiagnostics.Exception("Worldgen map layer construction failed", exception);
            return null;
        }
    }

    private GenMaps? GetWorldgenPreviewGenMaps()
    {
        if (_worldgenPreviewGenMaps != null) return _worldgenPreviewGenMaps;
        if (_worldgenPreviewServerApi == null) return null;

        try
        {
            _worldgenPreviewGenMaps = _worldgenPreviewServerApi.ModLoader.GetModSystem<GenMaps>();
            return _worldgenPreviewGenMaps;
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen GenMaps lookup failed", exception);
            return null;
        }
    }

    private static string GetWorldgenPreviewMapLayerFieldName(int mode)
    {
        return mode switch
        {
            WorldgenPreviewModeClimate => "climateGen",
            WorldgenPreviewModeForest => "forestGen",
            WorldgenPreviewModeUpheaval => "upheavelGen",
            WorldgenPreviewModeOcean => "oceanGen",
            WorldgenPreviewModeLandform => "landformsGen",
            WorldgenPreviewModeProvince => "geologicprovinceGen",
            _ => "unknown"
        };
    }

    private static string FormatWorldgenClimateMapValue(int value)
    {
        if ((value & ~0xff) == 0)
        {
            return $"Climate value: {value}";
        }

        int temp = (value >> 16) & 0xff;
        int rain = (value >> 8) & 0xff;
        int aux = value & 0xff;
        return $"Climate packed: temp {temp}, rain {rain}, aux {aux}";
    }

    private string BuildWorldgenOreHoverText(int blockX, int blockZ)
    {
        if (!TryGetWorldgenPreviewDepositVariant(out DepositVariant? variant, out string? code, out string source, out string status))
        {
            return status;
        }

        try
        {
            int chunkSize = GetWorldgenPreviewDepositChunkSize(variant!);
            int chunkX = FloorDiv(blockX, chunkSize);
            int chunkZ = FloorDiv(blockZ, chunkSize);
            float factor = variant!.GetOreMapFactor(chunkX, chunkZ);
            return $"Ore: {code ?? "unnamed"} {factor.ToString("0.###", CultureInfo.InvariantCulture)} at chunk {chunkX}, {chunkZ} ({source})";
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen ore hover sample failed", exception);
            return $"Ore sample failed: {exception.Message}";
        }
    }

    private string BuildWorldgenBlockPatchHoverText(int blockX, int blockZ)
    {
        if (!TryGetSelectedWorldgenBlockPatchRow(out JObject? row) || row == null)
        {
            return "Block patch: no selected draft row.";
        }

        GenMaps? genMaps = GetWorldgenPreviewGenMaps();
        if (genMaps?.climateGen == null)
        {
            return "Block patch: live climateGen unavailable.";
        }

        try
        {
            int climateValue = genMaps.climateGen.GenLayer(blockX, blockZ, 1, 1)[0];
            int forestValue = genMaps.forestGen?.GenLayer(blockX, blockZ, 1, 1)[0] ?? 0;
            WorldgenClimateSample sample = DecodeWorldgenClimateSample(climateValue, forestValue);
            WorldgenBlockPatchDraft draft = WorldgenBlockPatchDraft.FromJson(row);
            bool suitable = draft.IsSuitable(sample);
            string label = GetWorldgenRowLabel(WorldgenAssetKind.BlockPatches, row, _worldgenRowIndex);
            return $"Block patch {label}: {(suitable ? "suitable" : "rejected")}; temp {sample.TemperatureCelsius:0.#}C, rain {sample.Rain:0.###}, forest {sample.Forest:0.###}, chance {draft.Chance:0.###}";
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen block patch hover sample failed", exception);
            return $"Block patch sample failed: {exception.Message}";
        }
    }

    private string BuildWorldgenTerrainShapeHoverText(long seed, int blockX, int blockZ)
    {
        if (!TryGetSelectedWorldgenLandformRow(out JObject? row) || row == null)
        {
            return "Terrain shape: no selected landform draft row.";
        }

        try
        {
            WorldgenLandformDraft draft = WorldgenLandformDraft.FromJson(row);
            float height = draft.SampleHeight(seed, blockX, blockZ);
            string code = draft.Code ?? GetWorldgenRowLabel(WorldgenAssetKind.Landforms, row, _worldgenRowIndex);
            return $"Terrain shape {code}: height {height:0.000}, octaves {draft.Octaves.Length}, y keys {draft.YKeyPositions.Length}";
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen terrain shape hover sample failed", exception);
            return $"Terrain shape sample failed: {exception.Message}";
        }
    }

    private string FormatWorldgenMapLayerValue(int mode, int value, MapLayerBase layer)
    {
        if (mode == WorldgenPreviewModeClimate)
        {
            return FormatWorldgenClimateMapValue(value);
        }

        Dictionary<int, string>? codes = GetWorldgenPreviewLayerCodes(mode, layer);
        string modeLabel = WorldgenPreviewModeLabels[Math.Clamp(mode, 0, WorldgenPreviewModeLabels.Length - 1)];
        if (codes != null && codes.TryGetValue(value, out string? code) && !string.IsNullOrWhiteSpace(code))
        {
            return $"{modeLabel}: {code} ({value})";
        }

        return $"{modeLabel} value: {value}";
    }

    private Dictionary<int, string>? GetWorldgenPreviewLayerCodes(int mode, MapLayerBase layer)
    {
        if (mode == WorldgenPreviewModeLandform)
        {
            return _worldgenPreviewLandformCodes ??= BuildWorldgenPreviewIndexCodeMap(
                TryGetReflectedMember(TryGetReflectedMember(layer, "noiseLandforms"), "landforms"),
                ["LandFormsByIndex", "Variants"]);
        }

        if (mode == WorldgenPreviewModeProvince)
        {
            return _worldgenPreviewProvinceCodes ??= BuildWorldgenPreviewIndexCodeMap(
                TryGetReflectedMember(TryGetReflectedMember(layer, "noiseGeoProvince"), "provinces"),
                ["Variants"]);
        }

        return null;
    }

    private static Dictionary<int, string> BuildWorldgenPreviewIndexCodeMap(object? worldProperty, IReadOnlyList<string> variantMemberNames)
    {
        Dictionary<int, string> codes = [];
        if (worldProperty == null) return codes;

        foreach (string memberName in variantMemberNames)
        {
            AddWorldgenPreviewVariantCodes(codes, TryGetReflectedMember(worldProperty, memberName));
        }

        return codes;
    }

    private static void AddWorldgenPreviewVariantCodes(Dictionary<int, string> codes, object? variants)
    {
        if (variants is not System.Collections.IEnumerable enumerable || variants is string) return;

        int arrayIndex = 0;
        foreach (object? variant in enumerable)
        {
            if (variant != null)
            {
                int index = TryGetWorldgenPreviewVariantIndex(variant) ?? arrayIndex;
                string? code = TryGetWorldgenPreviewVariantCode(variant);
                if (!string.IsNullOrWhiteSpace(code) && !codes.ContainsKey(index))
                {
                    codes[index] = code;
                }
            }

            arrayIndex++;
        }
    }

    private static int? TryGetWorldgenPreviewVariantIndex(object variant)
    {
        object? raw = TryGetReflectedMember(variant, "Index") ?? TryGetReflectedMember(variant, "index");
        if (raw == null) return null;

        try
        {
            return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetWorldgenPreviewVariantCode(object variant)
    {
        object? raw = TryGetReflectedMember(variant, "Code") ?? TryGetReflectedMember(variant, "code");
        string? code = raw?.ToString();
        return string.IsNullOrWhiteSpace(code) ? null : code;
    }

    private bool TryGetWorldgenPreviewDepositVariant(out DepositVariant? variant, out string? code, out string source, out string status)
    {
        variant = null;
        code = GetSelectedWorldgenDepositCode();
        source = "";

        GenDeposits? genDeposits = GetWorldgenPreviewGenDeposits();
        if (genDeposits?.Deposits == null || genDeposits.Deposits.Length == 0)
        {
            status = "Live GenDeposits unavailable; open a singleplayer world and press Refresh SP.";
            return false;
        }

        if (TryBuildWorldgenPreviewDraftDepositVariant(genDeposits, out DepositVariant? draftVariant, out string? draftCode, out string draftStatus))
        {
            variant = draftVariant;
            code = draftCode ?? code;
            source = "draft deposit";
            status = draftStatus;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            string selectedCode = code;
            variant = EnumerateWorldgenPreviewDeposits(genDeposits.Deposits)
                .FirstOrDefault(candidate => string.Equals(GetWorldgenPreviewDepositCode(candidate), selectedCode, StringComparison.OrdinalIgnoreCase));

            if (variant == null)
            {
                status = $"Selected deposit '{code}' was not found in live GenDeposits.";
                return false;
            }
        }
        else
        {
            variant = EnumerateWorldgenPreviewDeposits(genDeposits.Deposits).FirstOrDefault();
            code = variant == null ? null : GetWorldgenPreviewDepositCode(variant);
        }

        if (variant == null)
        {
            status = "No live deposit variants are available.";
            return false;
        }

        source = "live deposit";
        status = string.IsNullOrWhiteSpace(draftStatus)
            ? $"Live deposit: {code ?? "unnamed"}."
            : $"Live deposit: {code ?? "unnamed"}; draft unavailable: {draftStatus}";
        return true;
    }

    private GenDeposits? GetWorldgenPreviewGenDeposits()
    {
        if (_worldgenPreviewGenDeposits != null) return _worldgenPreviewGenDeposits;
        if (_worldgenPreviewServerApi == null) return null;

        try
        {
            _worldgenPreviewGenDeposits = _worldgenPreviewServerApi.ModLoader.GetModSystem<GenDeposits>();
            return _worldgenPreviewGenDeposits;
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen GenDeposits lookup failed", exception);
            return null;
        }
    }

    private bool TryBuildWorldgenPreviewDraftDepositVariant(
        GenDeposits genDeposits,
        out DepositVariant? variant,
        out string? code,
        out string status)
    {
        variant = null;
        code = null;

        if (!TryGetSelectedWorldgenDepositRow(out JObject? row) || row == null)
        {
            status = "no selected deposit draft row";
            return false;
        }

        code = row["code"]?.ToString();

        try
        {
            DepositVariant? draft = row.ToObject<DepositVariant>();
            if (draft == null)
            {
                status = "selected deposit draft did not deserialize";
                return false;
            }

            if (!TryGetWorldgenPreviewDepositInitDependencies(
                genDeposits,
                out LCGRandom? depositRand,
                out NormalizedSimplexNoise? shapeNoise,
                out string dependencyStatus))
            {
                status = dependencyStatus;
                return false;
            }

            draft.Init(_worldgenPreviewServerApi!, depositRand!, shapeNoise!);
            variant = draft;
            code = GetWorldgenPreviewDepositCode(draft) ?? code;
            status = $"Draft deposit: {code ?? "unnamed"}.";
            return true;
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen draft deposit init failed", exception);
            status = exception.Message;
            return false;
        }
    }

    private static bool TryGetWorldgenPreviewDepositInitDependencies(
        GenDeposits genDeposits,
        out LCGRandom? depositRand,
        out NormalizedSimplexNoise? shapeNoise,
        out string status)
    {
        depositRand = TryGetReflectedMember(genDeposits, "depositRand") as LCGRandom;
        shapeNoise = TryGetReflectedMember(genDeposits, "depositShapeDistortNoise") as NormalizedSimplexNoise;

        if (depositRand == null || shapeNoise == null)
        {
            status = "live GenDeposits init dependencies are unavailable";
            return false;
        }

        status = "";
        return true;
    }

    private bool TryGetSelectedWorldgenDepositRow(out JObject? row)
    {
        return TryGetSelectedWorldgenRow(WorldgenAssetKind.Deposits, out row);
    }

    private bool TryGetSelectedWorldgenBlockPatchRow(out JObject? row)
    {
        return TryGetSelectedWorldgenRow(WorldgenAssetKind.BlockPatches, out row);
    }

    private bool TryGetSelectedWorldgenLandformRow(out JObject? row)
    {
        return TryGetSelectedWorldgenRow(WorldgenAssetKind.Landforms, out row);
    }

    private bool TryGetSelectedWorldgenRow(WorldgenAssetKind kind, out JObject? row)
    {
        row = null;
        WorldgenAssetEntry? entry = SelectedWorldgenEntry;
        if (entry?.Kind != kind) return false;
        if (!TryParseJsonToken(_worldgenCurrentText, out JToken? root, out _) || root == null) return false;
        if (!TryGetWorldgenRows(root, kind, out JArray? rows, out _) || rows == null || rows.Count == 0) return false;

        int index = Math.Clamp(_worldgenRowIndex, 0, rows.Count - 1);
        row = rows[index] as JObject;
        return row != null;
    }

    private string? GetSelectedWorldgenDepositCode()
    {
        return TryGetSelectedWorldgenDepositRow(out JObject? row) && row != null
            ? row["code"]?.ToString()
            : null;
    }

    private string GetSelectedWorldgenRowContext(WorldgenAssetKind kind)
    {
        return TryGetSelectedWorldgenRow(kind, out JObject? row) && row != null
            ? row.ToString(Formatting.None)
            : "";
    }

    private static IEnumerable<DepositVariant> EnumerateWorldgenPreviewDeposits(IEnumerable<DepositVariant> deposits)
    {
        foreach (DepositVariant deposit in deposits)
        {
            yield return deposit;

            DepositVariant[]? children = deposit.ChildDeposits;
            if (children == null) continue;

            foreach (DepositVariant child in EnumerateWorldgenPreviewDeposits(children))
            {
                yield return child;
            }
        }
    }

    private static string? GetWorldgenPreviewDepositCode(DepositVariant variant)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        foreach (System.Reflection.FieldInfo field in variant.GetType().GetFields(flags).Where(field => field.Name.Equals("Code", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                object? value = field.GetValue(variant);
                if (value is string code && !string.IsNullOrWhiteSpace(code))
                {
                    return code;
                }
            }
            catch
            {
                // Try the next reflected member.
            }
        }

        string? fallback = variant.Code?.ToString();
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    private static string? TryGetWorldgenPreviewDepositGenerator(DepositVariant variant)
    {
        string? generator = variant.Generator;
        return string.IsNullOrWhiteSpace(generator) ? null : generator;
    }

    private static int GetWorldgenPreviewDepositChunkSize(DepositVariant variant)
    {
        object? raw = TryGetReflectedMember(variant, "chunksize");
        if (raw != null)
        {
            try
            {
                int chunkSize = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                if (chunkSize > 0) return chunkSize;
            }
            catch
            {
                // Default below.
            }
        }

        return 32;
    }

    private void RefreshWorldgenServerApi()
    {
        try
        {
            _worldgenPreviewGenMaps = null;
            _worldgenPreviewGenDeposits = null;
            _worldgenPreviewMapLayer = null;
            _worldgenPreviewLandformCodes = null;
            _worldgenPreviewProvinceCodes = null;
            InvalidateWorldgenPreviewRasterCache();

            if (TryFindWorldgenServerApi(out ICoreServerAPI? serverApi, out string source))
            {
                _worldgenPreviewServerApi = serverApi;
                _worldgenPreviewServerStatus = $"Singleplayer server API: available ({source}).";
                return;
            }

            _worldgenPreviewServerApi = null;
            _worldgenPreviewServerStatus = "Singleplayer server API: unavailable; SP-only previews disabled.";
        }
        catch (Exception exception)
        {
            _worldgenPreviewServerApi = null;
            _worldgenPreviewServerStatus = $"Singleplayer server API probe failed: {exception.Message}";
            _worldgenDiagnostics.Exception("Worldgen singleplayer server probe failed", exception);
        }
    }

    private void ClearWorldgenPeekProfile(string status)
    {
        _worldgenPreviewPeekPending = false;
        _worldgenPreviewPeekProfile = null;
        _worldgenPreviewPeekCacheKey = null;
        _worldgenPreviewPeekStatus = status;
    }

    private EnumWorldGenPass GetSelectedWorldgenPeekPass()
    {
        int index = Math.Clamp(_worldgenPreviewPassIndex, 0, WorldgenPeekPasses.Length - 1);
        return WorldgenPeekPasses[index];
    }

    private string GetSelectedWorldgenPeekPassLabel()
    {
        int index = Math.Clamp(_worldgenPreviewPassIndex, 0, WorldgenPeekPassLabels.Length - 1);
        return WorldgenPeekPassLabels[index];
    }

    private void RequestWorldgenPeekRegion()
    {
        ICoreServerAPI? serverApi = _worldgenPreviewServerApi;
        if (serverApi == null)
        {
            RefreshWorldgenServerApi();
            serverApi = _worldgenPreviewServerApi;
        }

        if (serverApi == null)
        {
            _worldgenPreviewPeekStatus = "Real terrain peek requires an integrated singleplayer server.";
            return;
        }

        IWorldManagerAPI? worldManager = serverApi.WorldManager;
        if (worldManager == null)
        {
            _worldgenPreviewPeekStatus = "Real terrain peek failed: server WorldManager is unavailable.";
            return;
        }

        int chunkSize = worldManager.ChunkSize;
        if (chunkSize <= 0)
        {
            chunkSize = GlobalConstants.ChunkSize;
        }

        int regionSize = Math.Clamp(_worldgenPreviewRegionSize, 1, 3);
        int centerChunkX = FloorDiv(_worldgenPreviewOriginX, chunkSize);
        int centerChunkZ = FloorDiv(_worldgenPreviewOriginZ, chunkSize);
        int originChunkX = centerChunkX - regionSize / 2;
        int originChunkZ = centerChunkZ - regionSize / 2;
        EnumWorldGenPass untilPass = GetSelectedWorldgenPeekPass();
        string passLabel = GetSelectedWorldgenPeekPassLabel();
        WorldgenPeekRegionCacheKey cacheKey = new(ParseWorldgenPreviewSeed(), originChunkX, originChunkZ, regionSize, untilPass);
        if (_worldgenPreviewPeekProfile != null && _worldgenPreviewPeekCacheKey == cacheKey)
        {
            _worldgenPreviewPeekStatus = $"Using cached {passLabel} peek for {regionSize}x{regionSize} chunks at {originChunkX},{originChunkZ}.";
            return;
        }

        _worldgenPreviewPeekPending = true;
        _worldgenPreviewPeekProfile = null;
        _worldgenPreviewPeekCacheKey = null;
        _worldgenPreviewPeekStatus = $"Requesting real {passLabel} peek for {regionSize}x{regionSize} chunks at {originChunkX},{originChunkZ}...";

        bool restoreAutoGenerate = TryGetWorldgenAutoGenerateChunks(worldManager, out bool previousAutoGenerate)
            ? previousAutoGenerate
            : true;
        bool autoGenerateChanged = TrySetWorldgenAutoGenerateChunks(worldManager, false, out string? autoGenerateError);
        if (!autoGenerateChanged && !string.IsNullOrWhiteSpace(autoGenerateError))
        {
            _worldgenDiagnostics.Warning($"Worldgen peek could not pause AutoGenerateChunks: {autoGenerateError}");
        }

        int totalRequests = regionSize * regionSize;
        int remainingRequests = totalRequests;
        object gate = new();
        Dictionary<Vec2i, IServerChunk[]> regionColumns = new();
        Exception? firstFailure = null;

        try
        {
            void OnGenerated(Dictionary<Vec2i, IServerChunk[]> columns)
            {
                WorldgenPeekRegionProfile? profileToDispatch = null;
                Exception? failureToDispatch = null;
                bool shouldDispatch = false;

                lock (gate)
                {
                    try
                    {
                        foreach (KeyValuePair<Vec2i, IServerChunk[]> pair in columns)
                        {
                            if (pair.Key.X < originChunkX ||
                                pair.Key.Y < originChunkZ ||
                                pair.Key.X >= originChunkX + regionSize ||
                                pair.Key.Y >= originChunkZ + regionSize)
                            {
                                continue;
                            }

                            regionColumns[pair.Key] = pair.Value;
                        }
                    }
                    catch (Exception exception)
                    {
                        firstFailure ??= exception;
                    }

                    remainingRequests--;
                    if (remainingRequests <= 0)
                    {
                        shouldDispatch = true;
                        try
                        {
                            if (firstFailure != null)
                            {
                                failureToDispatch = firstFailure;
                            }
                            else
                            {
                                profileToDispatch = BuildWorldgenPeekRegionProfile(regionColumns, originChunkX, originChunkZ, regionSize, chunkSize, untilPass, passLabel);
                            }
                        }
                        catch (Exception exception)
                        {
                            failureToDispatch = exception;
                        }
                    }
                }

                if (!shouldDispatch) return;

                if (autoGenerateChanged)
                {
                    TrySetWorldgenAutoGenerateChunks(worldManager, restoreAutoGenerate, out _);
                }

                _api.Event.EnqueueMainThreadTask(() =>
                {
                    _worldgenPreviewPeekPending = false;
                    if (failureToDispatch != null)
                    {
                        _worldgenPreviewPeekStatus = $"Real {passLabel} region peek failed: {failureToDispatch.Message}";
                        _worldgenDiagnostics.Exception("Worldgen region peek failed", failureToDispatch);
                        return;
                    }

                    _worldgenPreviewPeekProfile = profileToDispatch;
                    _worldgenPreviewPeekCacheKey = profileToDispatch == null ? null : cacheKey;
                    _worldgenPreviewPeekStatus = profileToDispatch == null
                        ? $"Real {passLabel} region peek returned no chunks at {originChunkX},{originChunkZ}."
                        : $"Real {passLabel} region peek: {profileToDispatch.ColumnsReturned}/{totalRequests} column(s), {profileToDispatch.ChunksReturned} vertical chunk(s); AutoGenerateChunks paused during peek.";
                }, "ingamedevtools-worldgen-peek-region");
            }

            for (int dz = 0; dz < regionSize; dz++)
            {
                for (int dx = 0; dx < regionSize; dx++)
                {
                    int requestChunkX = originChunkX + dx;
                    int requestChunkZ = originChunkZ + dz;
                    ChunkPeekOptions options = new()
                    {
                        UntilPass = untilPass,
                        OnGenerated = OnGenerated
                    };

                    worldManager.PeekChunkColumn(requestChunkX, requestChunkZ, options);
                }
            }
        }
        catch (Exception exception)
        {
            if (autoGenerateChanged)
            {
                TrySetWorldgenAutoGenerateChunks(worldManager, restoreAutoGenerate, out _);
            }

            _worldgenPreviewPeekPending = false;
            _worldgenPreviewPeekStatus = $"Real {passLabel} region peek request failed: {exception.Message}";
            _worldgenDiagnostics.Exception("Worldgen region peek request failed", exception);
        }
    }

    private static bool TrySetWorldgenAutoGenerateChunks(IWorldManagerAPI worldManager, bool enabled, out string? error)
    {
        try
        {
            worldManager.AutoGenerateChunks = enabled;
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TryGetWorldgenAutoGenerateChunks(IWorldManagerAPI worldManager, out bool enabled)
    {
        try
        {
            enabled = worldManager.AutoGenerateChunks;
            return true;
        }
        catch
        {
            enabled = true;
            return false;
        }
    }

    private static WorldgenPeekRegionProfile? BuildWorldgenPeekRegionProfile(
        Dictionary<Vec2i, IServerChunk[]> columns,
        int originChunkX,
        int originChunkZ,
        int regionSize,
        int chunkSize,
        EnumWorldGenPass untilPass,
        string passLabel)
    {
        if (columns.Count == 0) return null;

        int safeRegionSize = Math.Clamp(regionSize, 1, 3);
        int width = chunkSize * safeRegionSize;
        int depth = chunkSize * safeRegionSize;
        int[] heights = new int[width * depth];
        int[] topBlockIds = new int[width * depth];
        Array.Fill(heights, -1);
        int minHeight = int.MaxValue;
        int maxHeight = int.MinValue;
        long totalHeight = 0;
        int solidColumns = 0;
        int verticalChunksReturned = 0;

        for (int dz = 0; dz < safeRegionSize; dz++)
        {
            for (int dx = 0; dx < safeRegionSize; dx++)
            {
                Vec2i key = new(originChunkX + dx, originChunkZ + dz);
                if (!columns.TryGetValue(key, out IServerChunk[]? chunks) || chunks == null || chunks.Length == 0) continue;

                verticalChunksReturned += chunks.Length;
                for (int z = 0; z < chunkSize; z++)
                {
                    for (int x = 0; x < chunkSize; x++)
                    {
                        int height = FindWorldgenPeekColumnHeight(chunks, x, z, chunkSize, out int topBlockId);
                        int globalX = dx * chunkSize + x;
                        int globalZ = dz * chunkSize + z;
                        int index = globalZ * width + globalX;
                        heights[index] = height;
                        topBlockIds[index] = topBlockId;
                        if (height < 0) continue;

                        minHeight = Math.Min(minHeight, height);
                        maxHeight = Math.Max(maxHeight, height);
                        totalHeight += height;
                        solidColumns++;
                    }
                }
            }
        }

        if (solidColumns == 0)
        {
            minHeight = -1;
            maxHeight = -1;
        }

        float averageHeight = solidColumns == 0 ? -1f : totalHeight / (float)solidColumns;
        return new WorldgenPeekRegionProfile(
            originChunkX,
            originChunkZ,
            safeRegionSize,
            untilPass,
            passLabel,
            columns.Count,
            verticalChunksReturned,
            minHeight,
            maxHeight,
            averageHeight,
            BuildWorldgenPeekSampleSummary(heights, width, depth),
            chunkSize,
            width,
            depth,
            heights,
            topBlockIds);
    }

    private static int FindWorldgenPeekColumnHeight(IServerChunk[] chunks, int localX, int localZ, int chunkSize, out int topBlockId)
    {
        topBlockId = 0;
        for (int chunkY = chunks.Length - 1; chunkY >= 0; chunkY--)
        {
            IServerChunk? chunk = chunks[chunkY];
            IChunkBlocks? data = chunk?.Data;
            if (data == null) continue;

            for (int localY = chunkSize - 1; localY >= 0; localY--)
            {
                int index = MapUtil.Index3d(localX, localY, localZ, chunkSize, chunkSize);
                int blockId = data[index];
                if (blockId != 0)
                {
                    topBlockId = blockId;
                    return chunkY * chunkSize + localY;
                }
            }
        }

        return -1;
    }

    private static string BuildWorldgenPeekSampleSummary(int[] heights, int width, int depth)
    {
        if (heights.Length == 0 || width <= 0 || depth <= 0) return "empty";

        int z = Math.Clamp(depth / 2, 0, depth - 1);
        int step = Math.Max(1, width / 8);
        List<string> samples = new();
        for (int x = 0; x < width; x += step)
        {
            samples.Add(heights[z * width + x].ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(", ", samples);
    }

    private bool TryFindWorldgenServerApi(out ICoreServerAPI? serverApi, out string source)
    {
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        if (TryExtractWorldgenServerApi(_api, "client api", 2, visited, out serverApi, out source))
        {
            return true;
        }

        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!ShouldProbeWorldgenAssembly(assembly)) continue;

            foreach (Type type in GetWorldgenLoadableTypes(assembly))
            {
                const System.Reflection.BindingFlags staticFlags =
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

                foreach (System.Reflection.FieldInfo field in type.GetFields(staticFlags))
                {
                    if (!ShouldProbeWorldgenMember(field.FieldType, field.Name)) continue;

                    object? value;
                    try
                    {
                        value = field.GetValue(null);
                    }
                    catch
                    {
                        continue;
                    }

                    if (TryExtractWorldgenServerApi(value, $"{type.FullName}.{field.Name}", 2, visited, out serverApi, out source))
                    {
                        return true;
                    }
                }

                foreach (System.Reflection.PropertyInfo property in type.GetProperties(staticFlags))
                {
                    if (property.GetIndexParameters().Length != 0 || !ShouldProbeWorldgenMember(property.PropertyType, property.Name)) continue;

                    object? value;
                    try
                    {
                        value = property.GetValue(null);
                    }
                    catch
                    {
                        continue;
                    }

                    if (TryExtractWorldgenServerApi(value, $"{type.FullName}.{property.Name}", 2, visited, out serverApi, out source))
                    {
                        return true;
                    }
                }
            }
        }

        serverApi = null;
        source = "";
        return false;
    }

    private static bool TryExtractWorldgenServerApi(object? value, string source, int depth, HashSet<object> visited, out ICoreServerAPI? serverApi, out string foundSource)
    {
        if (value is ICoreServerAPI typedServerApi)
        {
            serverApi = typedServerApi;
            foundSource = source;
            return true;
        }

        if (value == null || depth <= 0 || value is string || value.GetType().IsValueType || !visited.Add(value))
        {
            serverApi = null;
            foundSource = "";
            return false;
        }

        Type type = value.GetType();
        const System.Reflection.BindingFlags instanceFlags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        foreach (System.Reflection.FieldInfo field in type.GetFields(instanceFlags))
        {
            if (!ShouldProbeWorldgenMember(field.FieldType, field.Name)) continue;

            object? child;
            try
            {
                child = field.GetValue(value);
            }
            catch
            {
                continue;
            }

            if (TryExtractWorldgenServerApi(child, $"{source}.{field.Name}", depth - 1, visited, out serverApi, out foundSource))
            {
                return true;
            }
        }

        foreach (System.Reflection.PropertyInfo property in type.GetProperties(instanceFlags))
        {
            if (property.GetIndexParameters().Length != 0 || !ShouldProbeWorldgenMember(property.PropertyType, property.Name)) continue;

            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            if (TryExtractWorldgenServerApi(child, $"{source}.{property.Name}", depth - 1, visited, out serverApi, out foundSource))
            {
                return true;
            }
        }

        serverApi = null;
        foundSource = "";
        return false;
    }

    private static bool ShouldProbeWorldgenAssembly(System.Reflection.Assembly assembly)
    {
        string name = assembly.GetName().Name ?? "";
        return name.StartsWith("Vintagestory", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("VS", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Type> GetWorldgenLoadableTypes(System.Reflection.Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null)!;
        }
        catch
        {
            return [];
        }
    }

    private static bool ShouldProbeWorldgenMember(Type memberType, string memberName)
    {
        if (typeof(ICoreServerAPI).IsAssignableFrom(memberType)) return true;
        if (memberType.IsPrimitive || memberType.IsEnum || memberType == typeof(string)) return false;

        string name = memberName.ToLowerInvariant();
        string typeName = memberType.FullName?.ToLowerInvariant() ?? "";
        return name.Contains("server", StringComparison.Ordinal) ||
            name.Contains("sapi", StringComparison.Ordinal) ||
            name.Contains("api", StringComparison.Ordinal) ||
            typeName.Contains("server", StringComparison.Ordinal);
    }

    private static bool WorldgenPreviewModeUsesMapLayer(int mode)
    {
        return mode is WorldgenPreviewModeClimate or
            WorldgenPreviewModeForest or
            WorldgenPreviewModeUpheaval or
            WorldgenPreviewModeOcean or
            WorldgenPreviewModeLandform or
            WorldgenPreviewModeProvince;
    }

    private static bool WorldgenPreviewModeRequiresServer(int mode)
    {
        return mode != WorldgenPreviewModeGradient;
    }

    private static void DrawWorldgenPreviewUnavailable(
        ImDrawListPtr drawList,
        NVector2 min,
        NVector2 max,
        string primary = "This preview mode requires an integrated singleplayer server.",
        string secondary = "Use Climate/Ocean modes or open a singleplayer world, then press Refresh SP.")
    {
        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0.02f, 0.02f, 0.018f, 1f));
        uint fill = ImGui.ColorConvertFloat4ToU32(new NVector4(0.14f, 0.08f, 0.06f, 0.82f));
        uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.95f, 0.78f, 0.62f, 1f));
        drawList.AddRectFilled(min, max, background, 4f);
        drawList.AddRectFilled(new NVector2(min.X + 10f, min.Y + 110f), new NVector2(max.X - 10f, min.Y + 178f), fill, 4f);
        drawList.AddText(new NVector2(min.X + 18f, min.Y + 122f), text, primary);
        drawList.AddText(new NVector2(min.X + 18f, min.Y + 146f), text, secondary);
    }

    private void DrawWorldgenPreviewRaster(ImDrawListPtr drawList, NVector2 min, NVector2 max, long seed, float centerX, float centerZ, float pixelsPerBlock)
    {
        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0.02f, 0.02f, 0.018f, 1f));
        drawList.AddRectFilled(min, max, background, 4f);

        int cellsX = Math.Clamp(_worldgenPreviewResolution, 32, 192);
        int cellsZ = Math.Clamp(_worldgenPreviewResolution, 32, 192);
        float cellWidth = (max.X - min.X) / cellsX;
        float cellHeight = (max.Y - min.Y) / cellsZ;
        float halfWidthBlocks = (max.X - min.X) / (2f * pixelsPerBlock);
        float halfHeightBlocks = (max.Y - min.Y) / (2f * pixelsPerBlock);

        if (!TryGetWorldgenPreviewRasterColors(
            seed,
            centerX,
            centerZ,
            halfWidthBlocks,
            halfHeightBlocks,
            cellsX,
            cellsZ,
            out uint[]? colors,
            out string error))
        {
            DrawWorldgenPreviewUnavailable(
                drawList,
                min,
                max,
                error,
                "This layer needs more engine context; use another mode or continue the W1 setup.");
            return;
        }
        uint[] rasterColors = colors ?? [];

        for (int z = 0; z < cellsZ; z++)
        {
            for (int x = 0; x < cellsX; x++)
            {
                NVector2 a = new(min.X + x * cellWidth, min.Y + z * cellHeight);
                NVector2 b = new(min.X + (x + 1) * cellWidth + 0.5f, min.Y + (z + 1) * cellHeight + 0.5f);
                drawList.AddRectFilled(a, b, rasterColors[z * cellsX + x]);
            }
        }
    }

    private void DrawWorldgenLandformSurfacePreview(ImDrawListPtr drawList, NVector2 min, NVector2 max, long seed, float centerX, float centerZ, float pixelsPerBlock)
    {
        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0.018f, 0.017f, 0.015f, 1f));
        drawList.AddRectFilled(min, max, background, 4f);

        if (_worldgenPreviewPeekProfile is { } peekProfile)
        {
            DrawWorldgenPeekRegionPreview(drawList, min, max, peekProfile);
            return;
        }

        if (!TryGetSelectedWorldgenLandformRow(out JObject? row) || row == null)
        {
            DrawWorldgenPreviewUnavailable(
                drawList,
                min,
                max,
                "Select a landform row to render a draft 3D surface.",
                "The 3D region preview uses the selected landform's terrain arrays.");
            return;
        }

        WorldgenLandformDraft draft;
        try
        {
            draft = WorldgenLandformDraft.FromJson(row);
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen 3D landform draft parse failed", exception);
            DrawWorldgenPreviewUnavailable(drawList, min, max, $"Landform draft parse failed: {exception.Message}", "Fix the selected row JSON and try again.");
            return;
        }

        if (!draft.IsUsable)
        {
            DrawWorldgenPreviewUnavailable(drawList, min, max, "Selected landform has no usable terrain arrays.", "Add terrainOctaves and terrainYKey arrays to preview it.");
            return;
        }

        float width = max.X - min.X;
        float height = max.Y - min.Y;
        float viewportMinDimension = Math.Max(1f, Math.Min(width, height));
        int grid = Math.Clamp(_worldgenPreviewResolution / 4, 18, 48);
        float spanBlocks = Math.Clamp(viewportMinDimension / Math.Max(0.1f, pixelsPerBlock) * 1.85f, 80f, 4096f);
        float screenScale = viewportMinDimension * 0.74f / spanBlocks;
        float heightScale = viewportMinDimension * 0.38f;
        float cosYaw = MathF.Cos(_worldgenPreview3DYaw);
        float sinYaw = MathF.Sin(_worldgenPreview3DYaw);
        float pitch = MathF.Sin(_worldgenPreview3DPitch);
        NVector2 center = new(min.X + width * 0.5f, min.Y + height * 0.58f);

        float[,] heights = new float[grid, grid];
        NVector2[,] projected = new NVector2[grid, grid];
        float[,] depths = new float[grid, grid];
        float minHeight = float.PositiveInfinity;
        float maxHeight = float.NegativeInfinity;
        for (int z = 0; z < grid; z++)
        {
            float localZ = ((z / (float)(grid - 1)) - 0.5f) * spanBlocks;
            for (int x = 0; x < grid; x++)
            {
                float localX = ((x / (float)(grid - 1)) - 0.5f) * spanBlocks;
                float worldX = centerX + localX;
                float worldZ = centerZ + localZ;
                float h = draft.SampleHeight(seed, worldX, worldZ);
                float rx = localX * cosYaw - localZ * sinYaw;
                float rz = localX * sinYaw + localZ * cosYaw;
                projected[x, z] = new NVector2(
                    center.X + rx * screenScale,
                    center.Y + rz * screenScale * pitch - (h - 0.45f) * heightScale);
                depths[x, z] = rz;
                heights[x, z] = h;
                minHeight = Math.Min(minHeight, h);
                maxHeight = Math.Max(maxHeight, h);
            }
        }

        List<WorldgenSurfaceCell> cells = new((grid - 1) * (grid - 1));
        for (int z = 0; z < grid - 1; z++)
        {
            for (int x = 0; x < grid - 1; x++)
            {
                float depth = (depths[x, z] + depths[x + 1, z] + depths[x, z + 1] + depths[x + 1, z + 1]) * 0.25f;
                cells.Add(new WorldgenSurfaceCell(x, z, depth));
            }
        }
        cells.Sort(static (left, right) => left.Depth.CompareTo(right.Depth));

        uint contour = ImGui.ColorConvertFloat4ToU32(new NVector4(0.04f, 0.035f, 0.028f, 0.34f));
        foreach (WorldgenSurfaceCell cell in cells)
        {
            int x = cell.X;
            int z = cell.Z;
            NVector2 p00 = projected[x, z];
            NVector2 p10 = projected[x + 1, z];
            NVector2 p11 = projected[x + 1, z + 1];
            NVector2 p01 = projected[x, z + 1];
            float h = (heights[x, z] + heights[x + 1, z] + heights[x, z + 1] + heights[x + 1, z + 1]) * 0.25f;
            float east = (heights[x + 1, z] + heights[x + 1, z + 1]) * 0.5f;
            float south = (heights[x, z + 1] + heights[x + 1, z + 1]) * 0.5f;
            uint fill = BuildWorldgenTerrainShapePreviewColor(h, east, south, draft);
            drawList.AddQuadFilled(p00, p10, p11, p01, fill);
            if (grid <= 32)
            {
                drawList.AddLine(p00, p10, contour);
                drawList.AddLine(p10, p11, contour);
            }
        }

        uint axisX = ImGui.ColorConvertFloat4ToU32(new NVector4(0.78f, 0.22f, 0.15f, 0.90f));
        uint axisZ = ImGui.ColorConvertFloat4ToU32(new NVector4(0.22f, 0.38f, 0.92f, 0.90f));
        NVector2 origin = ProjectWorldgenSurfacePoint(0f, 0f, draft.SampleHeight(seed, centerX, centerZ), center, screenScale, heightScale, cosYaw, sinYaw, pitch);
        NVector2 xAxis = ProjectWorldgenSurfacePoint(spanBlocks * 0.16f, 0f, draft.SampleHeight(seed, centerX + spanBlocks * 0.16f, centerZ), center, screenScale, heightScale, cosYaw, sinYaw, pitch);
        NVector2 zAxis = ProjectWorldgenSurfacePoint(0f, spanBlocks * 0.16f, draft.SampleHeight(seed, centerX, centerZ + spanBlocks * 0.16f), center, screenScale, heightScale, cosYaw, sinYaw, pitch);
        drawList.AddLine(origin, xAxis, axisX, 2f);
        drawList.AddLine(origin, zAxis, axisZ, 2f);

        _worldgenPreviewRasterStatus = $"3D draft surface: {grid}x{grid}; height {minHeight:0.000}-{maxHeight:0.000}; yaw {_worldgenPreview3DYaw:0.00}, pitch {_worldgenPreview3DPitch:0.00}";
    }

    private void DrawWorldgenPeekRegionPreview(ImDrawListPtr drawList, NVector2 min, NVector2 max, WorldgenPeekRegionProfile profile)
    {
        int widthBlocks = profile.Width;
        int depthBlocks = profile.Depth;
        if (widthBlocks <= 0 ||
            depthBlocks <= 0 ||
            profile.Heights.Length < widthBlocks * depthBlocks ||
            profile.TopBlockIds.Length < widthBlocks * depthBlocks)
        {
            DrawWorldgenPreviewUnavailable(drawList, min, max, "Peeked terrain region data is incomplete.", "Press Peek region again.");
            return;
        }

        float width = max.X - min.X;
        float height = max.Y - min.Y;
        float viewportMinDimension = Math.Max(1f, Math.Min(width, height));
        float screenScale = viewportMinDimension * 0.66f / Math.Max(1, Math.Max(widthBlocks, depthBlocks));
        int baseHeight = profile.MinHeight < 0 ? 0 : Math.Max(0, profile.MinHeight - Math.Max(4, (profile.MaxHeight - profile.MinHeight) / 8));
        float heightSpan = Math.Max(8f, profile.MaxHeight - baseHeight + 2f);
        float heightScale = viewportMinDimension * 0.44f / heightSpan;
        float cosYaw = MathF.Cos(_worldgenPreview3DYaw);
        float sinYaw = MathF.Sin(_worldgenPreview3DYaw);
        float pitch = MathF.Sin(_worldgenPreview3DPitch);
        NVector2 center = new(min.X + width * 0.5f, min.Y + height * 0.68f);

        List<WorldgenVoxelFace> faces = new(widthBlocks * depthBlocks * 3);
        for (int z = 0; z < depthBlocks; z++)
        {
            for (int x = 0; x < widthBlocks; x++)
            {
                int index = z * widthBlocks + x;
                int topY = profile.Heights[index];
                if (topY < 0) continue;

                int topBlockId = profile.TopBlockIds[index];
                float yTop = topY + 1f;
                float localX = x - widthBlocks * 0.5f;
                float localZ = z - depthBlocks * 0.5f;
                float heightNorm = profile.MaxHeight <= profile.MinHeight
                    ? 0.5f
                    : (topY - profile.MinHeight) / (float)Math.Max(1, profile.MaxHeight - profile.MinHeight);

                AddWorldgenPeekTopFace(faces, localX, localZ, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch, BuildWorldgenPeekBlockColor(topBlockId, heightNorm, 1.00f));
                TryAddWorldgenPeekSideFace(faces, profile, x, z, x - 1, z, localX, localZ, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch, topBlockId, heightNorm, WorldgenPeekFaceSide.West);
                TryAddWorldgenPeekSideFace(faces, profile, x, z, x + 1, z, localX, localZ, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch, topBlockId, heightNorm, WorldgenPeekFaceSide.East);
                TryAddWorldgenPeekSideFace(faces, profile, x, z, x, z - 1, localX, localZ, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch, topBlockId, heightNorm, WorldgenPeekFaceSide.North);
                TryAddWorldgenPeekSideFace(faces, profile, x, z, x, z + 1, localX, localZ, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch, topBlockId, heightNorm, WorldgenPeekFaceSide.South);
            }
        }

        faces.Sort(static (left, right) => left.Depth.CompareTo(right.Depth));
        foreach (WorldgenVoxelFace face in faces)
        {
            drawList.AddQuadFilled(face.A, face.B, face.C, face.D, face.Color);
        }

        uint axisX = ImGui.ColorConvertFloat4ToU32(new NVector4(0.78f, 0.22f, 0.15f, 0.90f));
        uint axisZ = ImGui.ColorConvertFloat4ToU32(new NVector4(0.22f, 0.38f, 0.92f, 0.90f));
        NVector2 origin = ProjectWorldgenVoxelPoint(-widthBlocks * 0.5f, -depthBlocks * 0.5f, baseHeight, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
        NVector2 xAxis = ProjectWorldgenVoxelPoint(widthBlocks * 0.5f, -depthBlocks * 0.5f, baseHeight, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
        NVector2 zAxis = ProjectWorldgenVoxelPoint(-widthBlocks * 0.5f, depthBlocks * 0.5f, baseHeight, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
        drawList.AddLine(origin, xAxis, axisX, 2f);
        drawList.AddLine(origin, zAxis, axisZ, 2f);

        _worldgenPreviewRasterStatus = $"3D real {profile.PassLabel} region: chunks {profile.OriginChunkX},{profile.OriginChunkZ} size {profile.RegionSizeChunks}x{profile.RegionSizeChunks}; {faces.Count} visible face(s); height {profile.MinHeight}-{profile.MaxHeight}; yaw {_worldgenPreview3DYaw:0.00}, pitch {_worldgenPreview3DPitch:0.00}";
    }

    private void AddWorldgenPeekTopFace(
        List<WorldgenVoxelFace> faces,
        float localX,
        float localZ,
        float yTop,
        int baseHeight,
        NVector2 center,
        float screenScale,
        float heightScale,
        float cosYaw,
        float sinYaw,
        float pitch,
        uint color)
    {
        NVector2 a = ProjectWorldgenVoxelPoint(localX, localZ, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
        NVector2 b = ProjectWorldgenVoxelPoint(localX + 1f, localZ, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
        NVector2 c = ProjectWorldgenVoxelPoint(localX + 1f, localZ + 1f, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
        NVector2 d = ProjectWorldgenVoxelPoint(localX, localZ + 1f, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
        float depth = AverageWorldgenVoxelDepth(localX, localZ, localX + 1f, localZ + 1f, cosYaw, sinYaw);
        faces.Add(new WorldgenVoxelFace(a, b, c, d, depth, color));
    }

    private void TryAddWorldgenPeekSideFace(
        List<WorldgenVoxelFace> faces,
        WorldgenPeekRegionProfile profile,
        int x,
        int z,
        int neighborX,
        int neighborZ,
        float localX,
        float localZ,
        float yTop,
        int baseHeight,
        NVector2 center,
        float screenScale,
        float heightScale,
        float cosYaw,
        float sinYaw,
        float pitch,
        int topBlockId,
        float heightNorm,
        WorldgenPeekFaceSide side)
    {
        int neighborHeight = GetWorldgenPeekHeight(profile, neighborX, neighborZ);
        float yBottom = neighborHeight < 0 ? baseHeight : Math.Max(baseHeight, neighborHeight + 1);
        if (yBottom >= yTop - 0.001f) return;

        NVector2 a;
        NVector2 b;
        NVector2 c;
        NVector2 d;
        float depth;
        switch (side)
        {
            case WorldgenPeekFaceSide.West:
                a = ProjectWorldgenVoxelPoint(localX, localZ + 1f, yBottom, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                b = ProjectWorldgenVoxelPoint(localX, localZ + 1f, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                c = ProjectWorldgenVoxelPoint(localX, localZ, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                d = ProjectWorldgenVoxelPoint(localX, localZ, yBottom, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                depth = AverageWorldgenVoxelDepth(localX, localZ, localX, localZ + 1f, cosYaw, sinYaw);
                break;
            case WorldgenPeekFaceSide.East:
                a = ProjectWorldgenVoxelPoint(localX + 1f, localZ, yBottom, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                b = ProjectWorldgenVoxelPoint(localX + 1f, localZ, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                c = ProjectWorldgenVoxelPoint(localX + 1f, localZ + 1f, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                d = ProjectWorldgenVoxelPoint(localX + 1f, localZ + 1f, yBottom, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                depth = AverageWorldgenVoxelDepth(localX + 1f, localZ, localX + 1f, localZ + 1f, cosYaw, sinYaw);
                break;
            case WorldgenPeekFaceSide.North:
                a = ProjectWorldgenVoxelPoint(localX, localZ, yBottom, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                b = ProjectWorldgenVoxelPoint(localX, localZ, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                c = ProjectWorldgenVoxelPoint(localX + 1f, localZ, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                d = ProjectWorldgenVoxelPoint(localX + 1f, localZ, yBottom, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                depth = AverageWorldgenVoxelDepth(localX, localZ, localX + 1f, localZ, cosYaw, sinYaw);
                break;
            default:
                a = ProjectWorldgenVoxelPoint(localX + 1f, localZ + 1f, yBottom, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                b = ProjectWorldgenVoxelPoint(localX + 1f, localZ + 1f, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                c = ProjectWorldgenVoxelPoint(localX, localZ + 1f, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                d = ProjectWorldgenVoxelPoint(localX, localZ + 1f, yBottom, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch);
                depth = AverageWorldgenVoxelDepth(localX, localZ + 1f, localX + 1f, localZ + 1f, cosYaw, sinYaw);
                break;
        }

        float shade = side is WorldgenPeekFaceSide.West or WorldgenPeekFaceSide.East ? 0.68f : 0.78f;
        faces.Add(new WorldgenVoxelFace(a, b, c, d, depth, BuildWorldgenPeekBlockColor(topBlockId, heightNorm, shade)));
    }

    private bool TryGetWorldgenPreviewRasterColors(
        long seed,
        float centerX,
        float centerZ,
        float halfWidthBlocks,
        float halfHeightBlocks,
        int cellsX,
        int cellsZ,
        out uint[]? colors,
        out string error)
    {
        int sampleStartX = (int)MathF.Floor(centerX - halfWidthBlocks);
        int sampleStartZ = (int)MathF.Floor(centerZ - halfHeightBlocks);
        int sampleEndX = (int)MathF.Ceiling(centerX + halfWidthBlocks);
        int sampleEndZ = (int)MathF.Ceiling(centerZ + halfHeightBlocks);
        WorldgenPreviewRasterCacheKey key = new(
            _worldgenPreviewMode,
            GetWorldgenPreviewRasterContextKey(),
            seed,
            sampleStartX,
            sampleStartZ,
            sampleEndX,
            sampleEndZ,
            cellsX,
            cellsZ);

        if (_worldgenPreviewRasterCache != null &&
            _worldgenPreviewRasterCacheKey == key &&
            _worldgenPreviewRasterCache.Length == cellsX * cellsZ)
        {
            colors = _worldgenPreviewRasterCache;
            error = "";
            return true;
        }

        colors = new uint[cellsX * cellsZ];
        if (_worldgenPreviewMode == WorldgenPreviewModeOre)
        {
            if (!TryBuildWorldgenOreRaster(centerX, centerZ, halfWidthBlocks, halfHeightBlocks, cellsX, cellsZ, colors, out error))
            {
                colors = null;
                return false;
            }
        }
        else if (_worldgenPreviewMode == WorldgenPreviewModeBlockPatch)
        {
            if (!TryBuildWorldgenBlockPatchRaster(centerX, centerZ, halfWidthBlocks, halfHeightBlocks, cellsX, cellsZ, colors, out error))
            {
                colors = null;
                return false;
            }
        }
        else if (_worldgenPreviewMode == WorldgenPreviewModeTerrainShape)
        {
            if (!TryBuildWorldgenTerrainShapeRaster(seed, centerX, centerZ, halfWidthBlocks, halfHeightBlocks, cellsX, cellsZ, colors, out error))
            {
                colors = null;
                return false;
            }
        }
        else if (WorldgenPreviewModeUsesMapLayer(_worldgenPreviewMode))
        {
            if (!TryBuildWorldgenMapLayerRaster(centerX, centerZ, halfWidthBlocks, halfHeightBlocks, cellsX, cellsZ, colors, out error))
            {
                colors = null;
                return false;
            }
        }
        else
        {
            for (int z = 0; z < cellsZ; z++)
            {
                float worldZ = centerZ - halfHeightBlocks + (z + 0.5f) * (2f * halfHeightBlocks / cellsZ);
                for (int x = 0; x < cellsX; x++)
                {
                    float worldX = centerX - halfWidthBlocks + (x + 0.5f) * (2f * halfWidthBlocks / cellsX);
                    colors[z * cellsX + x] = BuildWorldgenPreviewColor(seed, worldX, worldZ, _worldgenPreviewMode);
                }
            }
        }

        _worldgenPreviewRasterCacheKey = key;
        _worldgenPreviewRasterCache = colors;
        _worldgenPreviewRasterStatus = $"Raster cache: {cellsX}x{cellsZ}";
        error = "";
        return true;
    }

    private bool TryBuildWorldgenOreRaster(
        float centerX,
        float centerZ,
        float halfWidthBlocks,
        float halfHeightBlocks,
        int cellsX,
        int cellsZ,
        uint[] colors,
        out string error)
    {
        if (!TryGetWorldgenPreviewDepositVariant(out DepositVariant? variant, out _, out _, out string status))
        {
            error = status;
            return false;
        }

        try
        {
            int chunkSize = GetWorldgenPreviewDepositChunkSize(variant!);
            for (int z = 0; z < cellsZ; z++)
            {
                int worldZ = (int)MathF.Floor(centerZ - halfHeightBlocks + (z + 0.5f) * (2f * halfHeightBlocks / cellsZ));
                int chunkZ = FloorDiv(worldZ, chunkSize);
                for (int x = 0; x < cellsX; x++)
                {
                    int worldX = (int)MathF.Floor(centerX - halfWidthBlocks + (x + 0.5f) * (2f * halfWidthBlocks / cellsX));
                    int chunkX = FloorDiv(worldX, chunkSize);
                    float factor = variant!.GetOreMapFactor(chunkX, chunkZ);
                    colors[z * cellsX + x] = BuildWorldgenOrePreviewColor(factor);
                }
            }

            error = "";
            return true;
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen ore raster failed", exception);
            error = $"Ore render failed: {exception.Message}";
            return false;
        }
    }

    private bool TryBuildWorldgenBlockPatchRaster(
        float centerX,
        float centerZ,
        float halfWidthBlocks,
        float halfHeightBlocks,
        int cellsX,
        int cellsZ,
        uint[] colors,
        out string error)
    {
        if (!TryGetSelectedWorldgenBlockPatchRow(out JObject? row) || row == null)
        {
            error = "Select a block patch row to preview suitability.";
            return false;
        }

        GenMaps? genMaps = GetWorldgenPreviewGenMaps();
        if (genMaps?.climateGen == null)
        {
            error = "Live climateGen unavailable; open a singleplayer world and press Refresh SP.";
            return false;
        }

        MapLayerBase climate = genMaps.climateGen;
        MapLayerBase? forest = genMaps.forestGen;
        WorldgenBlockPatchDraft draft = WorldgenBlockPatchDraft.FromJson(row);

        try
        {
            int suitable = 0;
            for (int z = 0; z < cellsZ; z++)
            {
                int worldZ = (int)MathF.Floor(centerZ - halfHeightBlocks + (z + 0.5f) * (2f * halfHeightBlocks / cellsZ));
                for (int x = 0; x < cellsX; x++)
                {
                    int worldX = (int)MathF.Floor(centerX - halfWidthBlocks + (x + 0.5f) * (2f * halfWidthBlocks / cellsX));
                    int climateValue = climate.GenLayer(worldX, worldZ, 1, 1)[0];
                    int forestValue = forest?.GenLayer(worldX, worldZ, 1, 1)[0] ?? 0;
                    WorldgenClimateSample sample = DecodeWorldgenClimateSample(climateValue, forestValue);
                    bool matches = draft.IsSuitable(sample);
                    if (matches) suitable++;
                    colors[z * cellsX + x] = BuildWorldgenBlockPatchPreviewColor(sample, draft, matches);
                }
            }

            _worldgenPreviewRasterStatus = $"Raster cache: {cellsX}x{cellsZ}; block-patch suitable {suitable}/{cellsX * cellsZ}";
            error = "";
            return true;
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen block patch raster failed", exception);
            error = $"Block patch render failed: {exception.Message}";
            return false;
        }
    }

    private bool TryBuildWorldgenTerrainShapeRaster(
        long seed,
        float centerX,
        float centerZ,
        float halfWidthBlocks,
        float halfHeightBlocks,
        int cellsX,
        int cellsZ,
        uint[] colors,
        out string error)
    {
        if (!TryGetSelectedWorldgenLandformRow(out JObject? row) || row == null)
        {
            error = "Select a landform row to preview terrain shape.";
            return false;
        }

        try
        {
            WorldgenLandformDraft draft = WorldgenLandformDraft.FromJson(row);
            if (!draft.IsUsable)
            {
                error = "Selected landform has no usable terrain octave/Y-key arrays.";
                return false;
            }

            float minHeight = float.PositiveInfinity;
            float maxHeight = float.NegativeInfinity;
            for (int z = 0; z < cellsZ; z++)
            {
                float worldZ = centerZ - halfHeightBlocks + (z + 0.5f) * (2f * halfHeightBlocks / cellsZ);
                for (int x = 0; x < cellsX; x++)
                {
                    float worldX = centerX - halfWidthBlocks + (x + 0.5f) * (2f * halfWidthBlocks / cellsX);
                    float height = draft.SampleHeight(seed, worldX, worldZ);
                    float east = draft.SampleHeight(seed, worldX + 8f, worldZ);
                    float south = draft.SampleHeight(seed, worldX, worldZ + 8f);
                    minHeight = Math.Min(minHeight, height);
                    maxHeight = Math.Max(maxHeight, height);
                    colors[z * cellsX + x] = BuildWorldgenTerrainShapePreviewColor(height, east, south, draft);
                }
            }

            _worldgenPreviewRasterStatus = $"Raster cache: {cellsX}x{cellsZ}; landform height {minHeight:0.000}-{maxHeight:0.000}";
            error = "";
            return true;
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen terrain shape raster failed", exception);
            error = $"Terrain shape render failed: {exception.Message}";
            return false;
        }
    }

    private bool TryBuildWorldgenMapLayerRaster(
        float centerX,
        float centerZ,
        float halfWidthBlocks,
        float halfHeightBlocks,
        int cellsX,
        int cellsZ,
        uint[] colors,
        out string error)
    {
        MapLayerBase? mapLayer = GetWorldgenPreviewMapLayer(_worldgenPreviewMode);
        if (mapLayer == null)
        {
            error = string.IsNullOrWhiteSpace(_worldgenPreviewMapLayerStatus) ? "Map layer unavailable." : _worldgenPreviewMapLayerStatus;
            return false;
        }

        try
        {
            for (int z = 0; z < cellsZ; z++)
            {
                int worldZ = (int)MathF.Floor(centerZ - halfHeightBlocks + (z + 0.5f) * (2f * halfHeightBlocks / cellsZ));
                for (int x = 0; x < cellsX; x++)
                {
                    int worldX = (int)MathF.Floor(centerX - halfWidthBlocks + (x + 0.5f) * (2f * halfWidthBlocks / cellsX));
                    int value = mapLayer.GenLayer(worldX, worldZ, 1, 1)[0];
                    colors[z * cellsX + x] = BuildWorldgenMapLayerPreviewColor(_worldgenPreviewMode, value);
                }
            }
            error = "";
            return true;
        }
        catch (Exception exception)
        {
            _worldgenPreviewMapLayer = null;
            _worldgenPreviewMapLayerStatus = $"{WorldgenPreviewModeLabels[Math.Clamp(_worldgenPreviewMode, 0, WorldgenPreviewModeLabels.Length - 1)]} render failed: {exception.Message}";
            _worldgenDiagnostics.Exception("Worldgen map layer render failed", exception);
            error = _worldgenPreviewMapLayerStatus;
            return false;
        }
    }

    private void InvalidateWorldgenPreviewRasterCache()
    {
        _worldgenPreviewRasterCacheKey = null;
        _worldgenPreviewRasterCache = null;
        _worldgenPreviewRasterStatus = "Raster cache invalidated.";
    }

    private string GetWorldgenPreviewRasterContextKey()
    {
        return _worldgenPreviewMode switch
        {
            WorldgenPreviewModeOre => GetSelectedWorldgenRowContext(WorldgenAssetKind.Deposits),
            WorldgenPreviewModeBlockPatch => GetSelectedWorldgenRowContext(WorldgenAssetKind.BlockPatches),
            WorldgenPreviewModeLandform => GetSelectedWorldgenRowContext(WorldgenAssetKind.Landforms),
            WorldgenPreviewModeTerrainShape => GetSelectedWorldgenRowContext(WorldgenAssetKind.RockStrata),
            _ => ""
        };
    }

    private static uint BuildWorldgenMapLayerPreviewColor(int mode, int value)
    {
        float normalized = Math.Clamp(value / 255f, 0f, 1f);
        NVector4 color = mode switch
        {
            WorldgenPreviewModeClimate => BuildWorldgenClimateMapLayerColor(value),
            WorldgenPreviewModeForest => new NVector4(0.04f + normalized * 0.14f, 0.16f + normalized * 0.66f, 0.07f + normalized * 0.18f, 1f),
            WorldgenPreviewModeUpheaval => new NVector4(0.10f + normalized * 0.52f, 0.11f + normalized * 0.36f, 0.12f + normalized * 0.22f, 1f),
            WorldgenPreviewModeOcean => new NVector4(0.08f + (1f - normalized) * 0.24f, 0.16f + (1f - normalized) * 0.26f, 0.28f + normalized * 0.64f, 1f),
            _ => new NVector4(0.12f + normalized * 0.45f, 0.14f + normalized * 0.45f, 0.16f + normalized * 0.45f, 1f)
        };
        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static NVector4 BuildWorldgenClimateMapLayerColor(int value)
    {
        if ((value & ~0xff) == 0)
        {
            float normalized = Math.Clamp(value / 255f, 0f, 1f);
            return new NVector4(0.10f + normalized * 0.45f, 0.20f + normalized * 0.38f, 0.76f - normalized * 0.42f, 1f);
        }

        float temp = ((value >> 16) & 0xff) / 255f;
        float rain = ((value >> 8) & 0xff) / 255f;
        float aux = (value & 0xff) / 255f;
        float dry = 1f - rain;
        return new NVector4(
            Math.Clamp(0.14f + temp * 0.58f + dry * 0.18f, 0f, 1f),
            Math.Clamp(0.16f + rain * 0.42f + temp * 0.18f + aux * 0.08f, 0f, 1f),
            Math.Clamp(0.18f + rain * 0.45f + (1f - temp) * 0.22f, 0f, 1f),
            1f);
    }

    private static uint BuildWorldgenOrePreviewColor(float factor)
    {
        if (float.IsNaN(factor) || float.IsInfinity(factor)) factor = 0f;

        float normalized = Math.Clamp(MathF.Sqrt(Math.Max(0f, factor)), 0f, 1f);
        NVector4 color = normalized <= 0.001f
            ? new NVector4(0.035f, 0.030f, 0.026f, 1f)
            : new NVector4(
                Math.Clamp(0.12f + normalized * 0.78f, 0f, 1f),
                Math.Clamp(0.08f + normalized * 0.42f, 0f, 1f),
                Math.Clamp(0.06f + normalized * 0.10f, 0f, 1f),
                1f);

        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static uint BuildWorldgenBlockPatchPreviewColor(WorldgenClimateSample sample, WorldgenBlockPatchDraft draft, bool suitable)
    {
        if (!suitable)
        {
            float miss = draft.RejectionStrength(sample);
            return ImGui.ColorConvertFloat4ToU32(new NVector4(
                Math.Clamp(0.10f + miss * 0.28f, 0f, 1f),
                Math.Clamp(0.055f + sample.Forest * 0.06f, 0f, 1f),
                Math.Clamp(0.045f + sample.Rain * 0.10f, 0f, 1f),
                1f));
        }

        float chance = Math.Clamp(draft.Chance, 0f, 1f);
        float tempWarmth = Math.Clamp((sample.TemperatureCelsius + 20f) / 60f, 0f, 1f);
        return ImGui.ColorConvertFloat4ToU32(new NVector4(
            Math.Clamp(0.10f + chance * 0.32f + tempWarmth * 0.12f, 0f, 1f),
            Math.Clamp(0.28f + chance * 0.48f + sample.Forest * 0.18f, 0f, 1f),
            Math.Clamp(0.08f + sample.Rain * 0.26f, 0f, 1f),
            1f));
    }

    private static WorldgenClimateSample DecodeWorldgenClimateSample(int climateValue, int forestValue)
    {
        int rawTemp;
        int rawRain;
        if ((climateValue & ~0xff) == 0)
        {
            rawTemp = climateValue;
            rawRain = climateValue;
        }
        else
        {
            rawTemp = (climateValue >> 16) & 0xff;
            rawRain = (climateValue >> 8) & 0xff;
        }

        float tempCelsius = rawTemp / 4f - 20f;
        float rain = Math.Clamp(rawRain / 255f, 0f, 1f);
        float forest = Math.Clamp(forestValue / 255f, 0f, 1f);
        return new WorldgenClimateSample(tempCelsius, rain, forest, 0f);
    }

    private static uint BuildWorldgenPreviewColor(long seed, float worldX, float worldZ, int mode)
    {
        float seedOffset = (seed % 100000) * 0.000017f;
        float broad = 0.5f + 0.5f * MathF.Sin(worldX * 0.022f + seedOffset * 19f);
        float bands = 0.5f + 0.5f * MathF.Cos(worldZ * 0.031f - seedOffset * 31f);
        float detail = 0.5f + 0.5f * MathF.Sin((worldX + worldZ) * 0.071f + seedOffset * 47f);
        float mix = Math.Clamp(broad * 0.48f + bands * 0.38f + detail * 0.14f, 0f, 1f);

        NVector4 color = mode switch
        {
            WorldgenPreviewModeClimate => new NVector4(0.10f + mix * 0.55f, 0.22f + bands * 0.35f, 0.78f - mix * 0.34f, 1f),
            WorldgenPreviewModeForest => new NVector4(0.05f + detail * 0.18f, 0.22f + mix * 0.55f, 0.10f + bands * 0.22f, 1f),
            WorldgenPreviewModeUpheaval => new NVector4(0.18f + mix * 0.42f, 0.16f + detail * 0.32f, 0.12f + bands * 0.22f, 1f),
            WorldgenPreviewModeOcean => new NVector4(0.10f + bands * 0.32f, 0.20f + mix * 0.35f, 0.32f + detail * 0.48f, 1f),
            WorldgenPreviewModeLandform => new NVector4(0.18f + detail * 0.62f, 0.16f + mix * 0.28f, 0.09f + bands * 0.14f, 1f),
            WorldgenPreviewModeProvince => new NVector4(0.12f + mix * 0.54f, 0.12f + mix * 0.50f, 0.10f + detail * 0.26f, 1f),
            WorldgenPreviewModeOre => new NVector4(0.12f + bands * 0.35f, 0.14f + detail * 0.31f, 0.12f + mix * 0.34f, 1f),
            WorldgenPreviewModeBlockPatch => new NVector4(0.10f + detail * 0.24f, 0.20f + mix * 0.44f, 0.10f + bands * 0.18f, 1f),
            _ => new NVector4(0.10f + mix * 0.48f, 0.18f + bands * 0.36f, 0.14f + detail * 0.30f, 1f)
        };

        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static uint BuildWorldgenTerrainShapePreviewColor(float height, float east, float south, WorldgenLandformDraft draft)
    {
        float slopeX = east - height;
        float slopeZ = south - height;
        float shade = Math.Clamp(0.72f + (slopeZ - slopeX) * 2.2f, 0.42f, 1.18f);
        float normalized = Math.Clamp(height, 0f, 1f);

        NVector4 low = normalized < 0.43f
            ? new NVector4(0.07f, 0.16f, 0.30f, 1f)
            : new NVector4(0.18f, 0.30f, 0.16f, 1f);
        NVector4 mid = normalized < 0.62f
            ? new NVector4(0.30f, 0.42f, 0.18f, 1f)
            : new NVector4(0.42f, 0.36f, 0.26f, 1f);
        NVector4 high = normalized < 0.78f
            ? new NVector4(0.48f, 0.43f, 0.34f, 1f)
            : new NVector4(0.78f, 0.76f, 0.70f, 1f);

        NVector4 color = normalized < 0.50f
            ? LerpColor(low, mid, Math.Clamp((normalized - 0.32f) / 0.18f, 0f, 1f))
            : LerpColor(mid, high, Math.Clamp((normalized - 0.50f) / 0.40f, 0f, 1f));

        if (TryParseHexColor(draft.HexColor, out NVector4 tint))
        {
            color = LerpColor(color, tint, 0.18f);
        }

        color.X = Math.Clamp(color.X * shade, 0f, 1f);
        color.Y = Math.Clamp(color.Y * shade, 0f, 1f);
        color.Z = Math.Clamp(color.Z * shade, 0f, 1f);
        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static NVector4 LerpColor(NVector4 a, NVector4 b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new NVector4(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t,
            a.W + (b.W - a.W) * t);
    }

    private static bool TryParseHexColor(string? hex, out NVector4 color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string value = hex.Trim().TrimStart('#');
        if (value.Length != 6) return false;
        if (!int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int packed)) return false;

        color = new NVector4(
            ((packed >> 16) & 0xff) / 255f,
            ((packed >> 8) & 0xff) / 255f,
            (packed & 0xff) / 255f,
            1f);
        return true;
    }

    private static NVector2 ProjectWorldgenSurfacePoint(
        float localX,
        float localZ,
        float height,
        NVector2 center,
        float screenScale,
        float heightScale,
        float cosYaw,
        float sinYaw,
        float pitch)
    {
        float rx = localX * cosYaw - localZ * sinYaw;
        float rz = localX * sinYaw + localZ * cosYaw;
        return new NVector2(
            center.X + rx * screenScale,
            center.Y + rz * screenScale * pitch - (height - 0.45f) * heightScale);
    }

    private static NVector2 ProjectWorldgenVoxelPoint(
        float localX,
        float localZ,
        float y,
        int baseHeight,
        NVector2 center,
        float screenScale,
        float heightScale,
        float cosYaw,
        float sinYaw,
        float pitch)
    {
        float rx = localX * cosYaw - localZ * sinYaw;
        float rz = localX * sinYaw + localZ * cosYaw;
        return new NVector2(
            center.X + rx * screenScale,
            center.Y + rz * screenScale * pitch - (y - baseHeight) * heightScale);
    }

    private static float AverageWorldgenVoxelDepth(float ax, float az, float bx, float bz, float cosYaw, float sinYaw)
    {
        float depthA = ax * sinYaw + az * cosYaw;
        float depthB = bx * sinYaw + bz * cosYaw;
        return (depthA + depthB) * 0.5f;
    }

    private static int GetWorldgenPeekHeight(WorldgenPeekRegionProfile profile, int x, int z)
    {
        if (x < 0 || z < 0 || x >= profile.Width || z >= profile.Depth) return -1;
        int index = z * profile.Width + x;
        return index < profile.Heights.Length ? profile.Heights[index] : -1;
    }

    private uint BuildWorldgenPeekBlockColor(int blockId, float heightNorm, float shade)
    {
        string material = "";
        string path = "";
        try
        {
            Block block = _api.World.GetBlock(blockId);
            material = block.BlockMaterial.ToString().ToLowerInvariant();
            path = block.Code?.Path?.ToLowerInvariant() ?? "";
        }
        catch
        {
            // Fall through to hash-based fallback.
        }

        NVector4 color;
        if (material.Contains("liquid", StringComparison.Ordinal) || path.Contains("water", StringComparison.Ordinal))
        {
            color = new NVector4(0.10f, 0.28f, 0.58f, 1f);
        }
        else if (path.Contains("snow", StringComparison.Ordinal) || material.Contains("ice", StringComparison.Ordinal))
        {
            color = new NVector4(0.78f, 0.80f, 0.76f, 1f);
        }
        else if (material.Contains("stone", StringComparison.Ordinal) || material.Contains("ore", StringComparison.Ordinal))
        {
            color = new NVector4(0.34f, 0.33f, 0.30f, 1f);
        }
        else if (material.Contains("soil", StringComparison.Ordinal) || material.Contains("sand", StringComparison.Ordinal) || material.Contains("gravel", StringComparison.Ordinal))
        {
            color = new NVector4(0.36f, 0.30f, 0.18f, 1f);
        }
        else if (material.Contains("plant", StringComparison.Ordinal) || material.Contains("leaves", StringComparison.Ordinal) || path.Contains("grass", StringComparison.Ordinal))
        {
            color = new NVector4(0.22f, 0.42f, 0.18f, 1f);
        }
        else if (material.Contains("wood", StringComparison.Ordinal))
        {
            color = new NVector4(0.34f, 0.22f, 0.12f, 1f);
        }
        else
        {
            float hash = ((blockId * 1103515245u + 12345u) & 0xffffu) / 65535f;
            color = new NVector4(0.22f + hash * 0.18f, 0.24f + hash * 0.16f, 0.20f + hash * 0.12f, 1f);
        }

        color = LerpColor(color, new NVector4(0.64f, 0.60f, 0.50f, 1f), Math.Clamp(heightNorm * 0.18f, 0f, 0.18f));
        color.X = Math.Clamp(color.X * shade, 0f, 1f);
        color.Y = Math.Clamp(color.Y * shade, 0f, 1f);
        color.Z = Math.Clamp(color.Z * shade, 0f, 1f);
        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static int FloorDiv(int value, int divisor)
    {
        if (divisor <= 0) return 0;
        return (int)Math.Floor(value / (double)divisor);
    }

    private static void DrawWorldgenPreviewGrid(ImDrawListPtr drawList, NVector2 min, NVector2 max, float centerX, float centerZ, float pixelsPerBlock)
    {
        uint grid = ImGui.ColorConvertFloat4ToU32(new NVector4(0.18f, 0.17f, 0.14f, 0.50f));
        uint originX = ImGui.ColorConvertFloat4ToU32(new NVector4(0.78f, 0.22f, 0.15f, 0.85f));
        uint originZ = ImGui.ColorConvertFloat4ToU32(new NVector4(0.22f, 0.38f, 0.92f, 0.85f));
        float width = max.X - min.X;
        float height = max.Y - min.Y;
        float worldLeft = centerX - width / (2f * pixelsPerBlock);
        float worldRight = centerX + width / (2f * pixelsPerBlock);
        float worldTop = centerZ - height / (2f * pixelsPerBlock);
        float worldBottom = centerZ + height / (2f * pixelsPerBlock);
        int step = pixelsPerBlock switch
        {
            >= 12f => 8,
            >= 6f => 16,
            >= 2f => 32,
            _ => 64
        };

        int firstX = (int)MathF.Floor(worldLeft / step) * step;
        for (int x = firstX; x <= worldRight; x += step)
        {
            float screenX = min.X + width * 0.5f + (x - centerX) * pixelsPerBlock;
            drawList.AddLine(new NVector2(screenX, min.Y), new NVector2(screenX, max.Y), x == 0 ? originX : grid, x == 0 ? 2f : 1f);
        }

        int firstZ = (int)MathF.Floor(worldTop / step) * step;
        for (int z = firstZ; z <= worldBottom; z += step)
        {
            float screenY = min.Y + height * 0.5f + (z - centerZ) * pixelsPerBlock;
            drawList.AddLine(new NVector2(min.X, screenY), new NVector2(max.X, screenY), z == 0 ? originZ : grid, z == 0 ? 2f : 1f);
        }
    }

    private void LoadWorldgenEntry(WorldgenAssetEntry entry)
    {
        RememberWorldgenDraft();
        _worldgenLoadedKey = entry.Key;
        _worldgenOriginalText = entry.SourceText;

        if (_worldgenDraftStates.TryGetValue(entry.Key, out WorldgenDraftState? draft))
        {
            _worldgenCurrentText = draft.Text;
            _worldgenRowIndex = draft.RowIndex;
        }
        else
        {
            _worldgenCurrentText = entry.Root == null ? entry.SourceText : entry.Root.ToString(Formatting.Indented);
            _worldgenRowIndex = 0;
        }

        ValidateWorldgenCurrentText();
        ApplyWorldgenPreviewModeForEntry(entry);
    }

    private void EnsureWorldgenEntryLoaded(WorldgenAssetEntry entry)
    {
        if (!_worldgenLoadedKey.Equals(entry.Key, StringComparison.OrdinalIgnoreCase))
        {
            LoadWorldgenEntry(entry);
        }
    }

    private void RememberWorldgenDraft()
    {
        if (string.IsNullOrWhiteSpace(_worldgenLoadedKey)) return;

        bool dirty = IsWorldgenTextDirty(_worldgenCurrentText, _worldgenOriginalText);
        if (dirty)
        {
            _worldgenDraftStates[_worldgenLoadedKey] = new WorldgenDraftState(_worldgenCurrentText, _worldgenRowIndex, _worldgenTextValid, _worldgenValidationStatus);
        }
        else
        {
            _worldgenDraftStates.Remove(_worldgenLoadedKey);
        }
    }

    private bool IsWorldgenEntryDirty(WorldgenAssetEntry entry)
    {
        if (_worldgenLoadedKey.Equals(entry.Key, StringComparison.OrdinalIgnoreCase))
        {
            return IsWorldgenTextDirty(_worldgenCurrentText, _worldgenOriginalText);
        }

        return _worldgenDraftStates.ContainsKey(entry.Key);
    }

    private static bool IsWorldgenTextDirty(string currentText, string originalText)
    {
        if (TryParseJsonToken(currentText, out JToken? current, out _) && TryParseJsonToken(originalText, out JToken? original, out _) && current != null && original != null)
        {
            return !JToken.DeepEquals(current, original);
        }

        return !string.Equals(currentText, originalText, StringComparison.Ordinal);
    }

    private void SetWorldgenCurrentRoot(JToken root)
    {
        _worldgenCurrentText = root.ToString(Formatting.Indented);
        ValidateWorldgenCurrentText();
        RememberWorldgenDraft();
        InvalidateWorldgenPreviewRasterCache();
    }

    private void ValidateWorldgenCurrentText()
    {
        if (!TryParseJsonToken(_worldgenCurrentText, out JToken? root, out string parseError) || root == null)
        {
            _worldgenTextValid = false;
            _worldgenValidationStatus = $"Invalid JSON: {parseError}";
            return;
        }

        _worldgenTextValid = true;
        List<string> warnings = [];
        WorldgenAssetEntry? entry = SelectedWorldgenEntry;
        WorldgenAssetKind kind = entry?.Kind ?? ClassifyWorldgenAssetKind("", root);

        ValidateWorldgenRows(root, kind, warnings);
        ValidateWorldgenReferences(root, warnings);
        ValidateWorldgenNatFloats(root, warnings);
        ValidateWorldgenRanges(root, warnings);
        ValidateWorldgenCurveArrays(root, kind, warnings);

        _worldgenValidationStatus = warnings.Count == 0
            ? "Valid worldgen JSON."
            : $"{warnings.Count} warning(s): {string.Join("; ", warnings.Take(5))}{(warnings.Count > 5 ? $"; ...and {warnings.Count - 5} more" : "")}";
    }

    private static void ValidateWorldgenRows(JToken root, WorldgenAssetKind kind, List<string> warnings)
    {
        if (!TryGetWorldgenRows(root, kind, out JArray? rows, out _) || rows == null) return;

        Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not JObject row) continue;
            string? code = kind == WorldgenAssetKind.RockStrata ? row["blockcode"]?.ToString() : row["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code)) continue;
            if (seen.TryGetValue(code, out int previous))
            {
                warnings.Add($"duplicate code '{code}' in rows {previous} and {i}");
            }
            else
            {
                seen[code] = i;
            }
        }
    }

    private void ValidateWorldgenReferences(JToken root, List<string> warnings)
    {
        foreach ((string Path, string Value) reference in EnumerateWorldgenReferenceStrings(root))
        {
            if (string.IsNullOrWhiteSpace(reference.Value) || IsWorldgenPatternCode(reference.Value)) continue;

            if (reference.Path.Contains("schematic", StringComparison.OrdinalIgnoreCase))
            {
                if (!WorldgenSchematicExists(reference.Value))
                {
                    warnings.Add($"unresolved schematic '{reference.Value}'");
                }
                continue;
            }

            if (!WorldgenBlockExists(reference.Value))
            {
                warnings.Add($"unresolved block code '{reference.Value}'");
            }
        }
    }

    private void ValidateWorldgenNatFloats(JToken root, List<string> warnings)
    {
        foreach (JObject obj in EnumerateObjects(root))
        {
            if (!LooksLikeNatFloat(obj)) continue;

            foreach (string property in new[] { "avg", "var", "offset" })
            {
                JToken? token = obj[property];
                if (token != null && token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                {
                    warnings.Add($"malformed NatFloat {GetWorldgenTokenPath(obj)}.{property}");
                }
            }
        }
    }

    private static void ValidateWorldgenRanges(JToken root, List<string> warnings)
    {
        foreach (JObject obj in EnumerateObjects(root))
        {
            ValidateWorldgenRange(obj, "minTemp", "maxTemp", warnings);
            ValidateWorldgenRange(obj, "minRain", "maxRain", warnings);
            ValidateWorldgenRange(obj, "minForest", "maxForest", warnings);
            ValidateWorldgenRange(obj, "minFertility", "maxFertility", warnings);
            ValidateWorldgenRange(obj, "minY", "maxY", warnings);
        }
    }

    private static void ValidateWorldgenCurveArrays(JToken root, WorldgenAssetKind kind, List<string> warnings)
    {
        if (!TryGetWorldgenRows(root, kind, out JArray? rows, out _) || rows == null) return;

        foreach (JObject row in rows.OfType<JObject>())
        {
            ValidateArrayLengths(row, "terrainYKeyPositions", "terrainYKeyThresholds", warnings);
            ValidateArrayLengths(row, "terrainOctaves", "terrainOctaveThresholds", warnings);
            ValidateArrayLengths(row, "yKeyPositions", "yKeyThresholds", warnings);
            ValidateArrayLengths(row, "amplitudes", "thresholds", warnings);
            ValidateArrayLengths(row, "thresholds", "frequencies", warnings);
        }
    }

    private static void ValidateWorldgenRange(JObject obj, string minName, string maxName, List<string> warnings)
    {
        if (!TryReadJsonFloat(obj[minName], out float min) || !TryReadJsonFloat(obj[maxName], out float max)) return;
        if (max < min)
        {
            warnings.Add($"bad range {GetWorldgenTokenPath(obj)}.{minName}/{maxName}: {min} > {max}");
        }
    }

    private static void ValidateArrayLengths(JObject obj, string leftName, string rightName, List<string> warnings)
    {
        if (obj[leftName] is not JArray left || obj[rightName] is not JArray right) return;
        if (left.Count != right.Count)
        {
            warnings.Add($"curve array length mismatch {GetWorldgenTokenPath(obj)}.{leftName}/{rightName}: {left.Count} != {right.Count}");
        }
    }

    private IEnumerable<(string Path, string Value)> EnumerateWorldgenReferenceStrings(JToken root)
    {
        foreach (JProperty property in EnumerateTokens(root).OfType<JProperty>())
        {
            if (property.Value.Type == JTokenType.String && IsWorldgenReferenceProperty(property.Name))
            {
                yield return (GetWorldgenTokenPath(property), property.Value.ToString());
            }
            else if (property.Value is JObject obj && IsWorldgenReferenceProperty(property.Name))
            {
                string? code = obj["code"]?.ToString();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    yield return (GetWorldgenTokenPath(obj["code"]!), code);
                }
            }
            else if (property.Value is JArray array && IsWorldgenReferenceProperty(property.Name))
            {
                foreach (JToken value in array)
                {
                    if (value.Type == JTokenType.String)
                    {
                        yield return (GetWorldgenTokenPath(value), value.ToString());
                    }
                }
            }
        }
    }

    private bool WorldgenBlockExists(string code)
    {
        try
        {
            AssetLocation location = AssetLocation.Create(code, "game");
            Block? block = _api.World.GetBlock(location);
            if (block != null) return true;

            return !location.Domain.Equals("game", StringComparison.OrdinalIgnoreCase) &&
                _api.World.GetBlock(new AssetLocation("game", location.Path)) != null;
        }
        catch
        {
            return false;
        }
    }

    private bool WorldgenSchematicExists(string value)
    {
        string normalized = value.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        List<string> candidates =
        [
            normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? normalized : normalized + ".json",
            normalized.StartsWith("worldgen/", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : "worldgen/schematics/" + (normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? normalized : normalized + ".json")
        ];

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (_api.Assets.TryGet(AssetLocation.Create(candidate, "game"), true) != null) return true;
            }
            catch
            {
            }
        }

        return false;
    }

    private SourceSaveResult TrySaveWorldgenToSource(WorldgenAssetEntry entry)
    {
        try
        {
            if (!TryParseJsonToken(_worldgenCurrentText, out JToken? root, out string error) || root == null)
            {
                return SourceSaveResult.Fail($"Worldgen save failed: invalid JSON: {error}");
            }

            string relativePath = Path.Combine("assets", entry.Domain, entry.AssetPath.Replace('/', Path.DirectorySeparatorChar));
            string outputPath = GetToolAuthoredAssetPath("worldgen", relativePath);
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : entry.SourceText;
            string newText = root.ToString(Formatting.Indented);
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved authored worldgen asset to {outputPath}.",
                () => WriteAuthoredFile(outputPath, newText));
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            return SourceSaveResult.Fail($"Worldgen save failed for {entry.Domain}:{entry.AssetPath}: {exception.Message}");
        }
    }

    private static bool TryGetWorldgenRows(JToken root, WorldgenAssetKind kind, out JArray? rows, out string label)
    {
        rows = null;
        label = "Rows";

        switch (kind)
        {
            case WorldgenAssetKind.Deposits:
                rows = root as JArray;
                label = "Deposit entries";
                return rows != null;
            case WorldgenAssetKind.BlockPatches:
                rows = root as JArray ?? (root as JObject)?["patches"] as JArray;
                label = "Block patches";
                return rows != null;
            case WorldgenAssetKind.Landforms:
                rows = (root as JObject)?["variants"] as JArray;
                label = "Landforms";
                return rows != null;
            case WorldgenAssetKind.RockStrata:
                rows = (root as JObject)?["variants"] as JArray;
                label = "Rock strata";
                return rows != null;
            default:
                return false;
        }
    }

    private static string GetWorldgenRowLabel(WorldgenAssetKind kind, JToken token, int index)
    {
        if (token is not JObject row) return $"{index}: {token.Type}";

        string? value = kind switch
        {
            WorldgenAssetKind.RockStrata => row["blockcode"]?.ToString(),
            WorldgenAssetKind.BlockPatches => row["code"]?.ToString() ?? FirstArrayString(row["blockCodes"] as JArray),
            _ => row["code"]?.ToString()
        };

        return string.IsNullOrWhiteSpace(value) ? $"{index}: row" : $"{index}: {value}";
    }

    private static string? FirstArrayString(JArray? array)
    {
        return array == null || array.Count == 0 ? null : array[0].ToString();
    }

    private static WorldgenAssetKind FilterIndexToWorldgenKind(int index)
    {
        return index switch
        {
            1 => WorldgenAssetKind.Deposits,
            2 => WorldgenAssetKind.BlockPatches,
            3 => WorldgenAssetKind.Landforms,
            4 => WorldgenAssetKind.RockStrata,
            _ => WorldgenAssetKind.Other
        };
    }

    private static WorldgenAssetKind ClassifyWorldgenAssetKind(string assetPath, JToken? root)
    {
        string path = assetPath.Replace('\\', '/');
        if (path.Contains("/deposits/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/deposits.json", StringComparison.OrdinalIgnoreCase)) return WorldgenAssetKind.Deposits;
        if (path.Contains("/blockpatches/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/blockpatches.json", StringComparison.OrdinalIgnoreCase)) return WorldgenAssetKind.BlockPatches;
        if (path.EndsWith("/landforms.json", StringComparison.OrdinalIgnoreCase)) return WorldgenAssetKind.Landforms;
        if (path.EndsWith("/rockstrata.json", StringComparison.OrdinalIgnoreCase)) return WorldgenAssetKind.RockStrata;

        if (root is JObject obj)
        {
            if (obj["patches"] is JArray) return WorldgenAssetKind.BlockPatches;
            if (obj["variants"] is JArray variants && variants.OfType<JObject>().Any(row => row["blockcode"] != null)) return WorldgenAssetKind.RockStrata;
            if (obj["variants"] is JArray) return WorldgenAssetKind.Landforms;
        }

        return WorldgenAssetKind.Other;
    }

    private static bool TryParseJsonToken(string text, out JToken? token, out string error)
    {
        token = null;
        error = "";

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty JSON";
            return false;
        }

        try
        {
            token = JToken.Parse(text);
            return true;
        }
        catch (Exception first)
        {
            try
            {
                token = JsonObject.FromJson(text).Token;
                return token != null;
            }
            catch (Exception second)
            {
                error = second.Message;
                if (!string.IsNullOrWhiteSpace(first.Message) && !first.Message.Equals(second.Message, StringComparison.Ordinal))
                {
                    error = $"{first.Message}; {second.Message}";
                }
                return false;
            }
        }
    }

    private static IEnumerable<JObject> EnumerateObjects(JToken root) => EnumerateTokens(root).OfType<JObject>();

    private static IEnumerable<JToken> EnumerateTokens(JToken root)
    {
        yield return root;
        if (root is not JContainer container) yield break;

        foreach (JToken child in container.Children())
        {
            foreach (JToken descendant in EnumerateTokens(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool LooksLikeNatFloat(JObject obj)
    {
        return obj["avg"] != null || obj["var"] != null || obj["dist"] != null || obj["offset"] != null;
    }

    private static bool IsWorldgenReferenceProperty(string propertyName)
    {
        return propertyName.Equals("blockCode", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("blockCodes", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("blockcode", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("inblock", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("placeblock", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("surfaceBlock", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("schematic", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorldgenPatternCode(string value)
    {
        return value.Contains('*', StringComparison.Ordinal) ||
            value.Contains('{', StringComparison.Ordinal) ||
            value.Contains('}', StringComparison.Ordinal) ||
            value.Contains('[', StringComparison.Ordinal) ||
            value.Contains(']', StringComparison.Ordinal) ||
            value.Contains('@', StringComparison.Ordinal);
    }

    private static bool TryReadJsonFloat(JToken? token, out float value)
    {
        value = 0f;
        if (token == null) return false;
        try
        {
            value = token.Value<float>();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatJsonNumber(JToken token)
    {
        return token.Type == JTokenType.Integer || token.Type == JTokenType.Float
            ? token.Value<double>().ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            : token.ToString();
    }

    private static string GetWorldgenTokenPath(JToken token)
    {
        return string.IsNullOrWhiteSpace(token.Path) ? "$" : token.Path;
    }

    private static void SetOrRemoveString(JObject obj, string propertyName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            obj.Remove(propertyName);
        }
        else
        {
            obj[propertyName] = value;
        }
    }

    private enum WorldgenIndexState
    {
        Idle,
        Indexing,
        Ready,
        Failed
    }

    private enum WorldgenAssetKind
    {
        Other,
        Deposits,
        BlockPatches,
        Landforms,
        RockStrata
    }

    private sealed class WorldgenAssetEntry
    {
        public WorldgenAssetEntry(IAsset asset, string sourceText, JToken? root, string parseError)
        {
            Asset = asset;
            SourceText = sourceText;
            Root = root;
            ParseError = parseError;
            Domain = asset.Location.Domain ?? "game";
            AssetPath = asset.Location.Path.Replace('\\', '/');
            Kind = ClassifyWorldgenAssetKind(AssetPath, root);
        }

        public IAsset Asset { get; }
        public string SourceText { get; }
        public JToken? Root { get; }
        public string ParseError { get; }
        public string Domain { get; }
        public string AssetPath { get; }
        public WorldgenAssetKind Kind { get; }
        public string Key => Asset.Location.ToString();
        public string SortKey => $"{KindLabel}:{Domain}:{AssetPath}";
        public string SearchText => $"{Domain}:{AssetPath} {KindLabel} {SourceText}";
        public string KindLabel => Kind switch
        {
            WorldgenAssetKind.Deposits => "Deposits",
            WorldgenAssetKind.BlockPatches => "Block patches",
            WorldgenAssetKind.Landforms => "Landforms",
            WorldgenAssetKind.RockStrata => "Rock strata",
            _ => "Other"
        };
    }

    private sealed record WorldgenDraftState(string Text, int RowIndex, bool IsValid, string ValidationStatus);

    private readonly record struct WorldgenSurfaceCell(int X, int Z, float Depth);

    private readonly record struct WorldgenVoxelFace(NVector2 A, NVector2 B, NVector2 C, NVector2 D, float Depth, uint Color);

    private enum WorldgenPeekFaceSide
    {
        West,
        East,
        North,
        South
    }

    private readonly record struct WorldgenPeekRegionCacheKey(
        long Seed,
        int OriginChunkX,
        int OriginChunkZ,
        int RegionSizeChunks,
        EnumWorldGenPass UntilPass);

    private sealed record WorldgenPeekRegionProfile(
        int OriginChunkX,
        int OriginChunkZ,
        int RegionSizeChunks,
        EnumWorldGenPass UntilPass,
        string PassLabel,
        int ColumnsReturned,
        int ChunksReturned,
        int MinHeight,
        int MaxHeight,
        float AverageHeight,
        string SampleSummary,
        int ChunkSize,
        int Width,
        int Depth,
        int[] Heights,
        int[] TopBlockIds);

    private readonly record struct WorldgenClimateSample(float TemperatureCelsius, float Rain, float Forest, float Fertility, bool HasFertility = false);

    private readonly record struct WorldgenValueRange(float? Min, float? Max)
    {
        public bool IsSet => Min.HasValue || Max.HasValue;

        public bool Contains(float value)
        {
            return (!Min.HasValue || value >= Min.Value) &&
                (!Max.HasValue || value <= Max.Value);
        }

        public float RejectionDistance(float value)
        {
            if (Min.HasValue && value < Min.Value) return Min.Value - value;
            if (Max.HasValue && value > Max.Value) return value - Max.Value;
            return 0f;
        }
    }

    private readonly record struct WorldgenBlockPatchDraft(
        WorldgenValueRange Temperature,
        WorldgenValueRange Rain,
        WorldgenValueRange Forest,
        WorldgenValueRange Fertility,
        float Chance)
    {
        public static WorldgenBlockPatchDraft FromJson(JObject row)
        {
            return new WorldgenBlockPatchDraft(
                ReadRange(row, "minTemp", "maxTemp"),
                ReadRange(row, "minRain", "maxRain"),
                ReadRange(row, "minForest", "maxForest"),
                ReadRange(row, "minFertility", "maxFertility"),
                TryReadJsonFloat(row["chance"], out float chance) ? chance : 1f);
        }

        public bool IsSuitable(WorldgenClimateSample sample)
        {
            if (!Temperature.Contains(sample.TemperatureCelsius)) return false;
            if (!Rain.Contains(sample.Rain)) return false;
            if (!Forest.Contains(sample.Forest)) return false;
            if (sample.HasFertility && !Fertility.Contains(sample.Fertility)) return false;
            return true;
        }

        public float RejectionStrength(WorldgenClimateSample sample)
        {
            float temp = Temperature.RejectionDistance(sample.TemperatureCelsius) / 60f;
            float rain = Rain.RejectionDistance(sample.Rain);
            float forest = Forest.RejectionDistance(sample.Forest);
            float fertility = sample.HasFertility ? Fertility.RejectionDistance(sample.Fertility) : 0f;
            return Math.Clamp(temp + rain + forest + fertility, 0f, 1f);
        }

        private static WorldgenValueRange ReadRange(JObject row, string minName, string maxName)
        {
            float? min = TryReadJsonFloat(row[minName], out float parsedMin) ? parsedMin : null;
            float? max = TryReadJsonFloat(row[maxName], out float parsedMax) ? parsedMax : null;
            return new WorldgenValueRange(min, max);
        }
    }

    private readonly record struct WorldgenLandformDraft(
        string? Code,
        string? HexColor,
        float[] Octaves,
        float[] OctaveThresholds,
        float[] YKeyPositions,
        float[] YKeyThresholds)
    {
        public bool IsUsable => Octaves.Length > 0 && YKeyPositions.Length > 0 && YKeyThresholds.Length > 0;

        public static WorldgenLandformDraft FromJson(JObject row)
        {
            return new WorldgenLandformDraft(
                row["code"]?.ToString(),
                row["hexcolor"]?.ToString(),
                ReadFloatArray(row["terrainOctaves"] as JArray),
                ReadFloatArray(row["terrainOctaveThresholds"] as JArray),
                ReadFloatArray(row["terrainYKeyPositions"] as JArray),
                ReadFloatArray(row["terrainYKeyThresholds"] as JArray));
        }

        public float SampleHeight(long seed, float worldX, float worldZ)
        {
            float terrainNoise = SampleTerrainNoise(seed, worldX, worldZ);
            return Math.Clamp(ResolveYPosition(terrainNoise), 0f, 1f);
        }

        private float SampleTerrainNoise(long seed, float worldX, float worldZ)
        {
            float total = 0f;
            float totalWeight = 0f;
            for (int i = 0; i < Octaves.Length; i++)
            {
                float weight = Octaves[i];
                if (Math.Abs(weight) < 0.0001f) continue;

                float threshold = i < OctaveThresholds.Length ? OctaveThresholds[i] : 0f;
                double frequency = Math.Pow(2.0, i) / 4096.0;
                float value = ValueNoise01(seed, worldX * frequency, worldZ * frequency, i);
                if (threshold > 0f)
                {
                    value = Math.Clamp((value - threshold) / Math.Max(0.0001f, 1f - threshold), 0f, 1f);
                }

                total += value * weight;
                totalWeight += Math.Abs(weight);
            }

            if (totalWeight <= 0.0001f)
            {
                return YKeyThresholds.Length > 0 ? Math.Clamp(YKeyThresholds[0], 0f, 1f) : 0.5f;
            }

            return Math.Clamp(total / totalWeight, 0f, 1f);
        }

        private float ResolveYPosition(float terrainNoise)
        {
            int count = Math.Min(YKeyPositions.Length, YKeyThresholds.Length);
            if (count <= 0) return terrainNoise;
            if (count == 1) return YKeyPositions[0];

            for (int i = 0; i < count - 1; i++)
            {
                float thresholdA = YKeyThresholds[i];
                float thresholdB = YKeyThresholds[i + 1];
                float min = Math.Min(thresholdA, thresholdB);
                float max = Math.Max(thresholdA, thresholdB);
                if (terrainNoise < min || terrainNoise > max) continue;

                float denominator = thresholdB - thresholdA;
                float t = Math.Abs(denominator) < 0.0001f
                    ? 0f
                    : (terrainNoise - thresholdA) / denominator;
                return YKeyPositions[i] + (YKeyPositions[i + 1] - YKeyPositions[i]) * Math.Clamp(t, 0f, 1f);
            }

            int nearestIndex = 0;
            float nearestDistance = Math.Abs(terrainNoise - YKeyThresholds[0]);
            for (int i = 1; i < count; i++)
            {
                float distance = Math.Abs(terrainNoise - YKeyThresholds[i]);
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearestIndex = i;
            }

            return YKeyPositions[nearestIndex];
        }

        private static float[] ReadFloatArray(JArray? array)
        {
            if (array == null || array.Count == 0) return [];

            List<float> values = new(array.Count);
            foreach (JToken token in array)
            {
                if (TryReadJsonFloat(token, out float value))
                {
                    values.Add(value);
                }
            }

            return values.ToArray();
        }

        private static float ValueNoise01(long seed, double x, double z, int octave)
        {
            int x0 = (int)Math.Floor(x);
            int z0 = (int)Math.Floor(z);
            double fx = x - x0;
            double fz = z - z0;
            double sx = fx * fx * (3.0 - 2.0 * fx);
            double sz = fz * fz * (3.0 - 2.0 * fz);

            double a = Lerp(Hash01(seed, x0, z0, octave), Hash01(seed, x0 + 1, z0, octave), sx);
            double b = Lerp(Hash01(seed, x0, z0 + 1, octave), Hash01(seed, x0 + 1, z0 + 1, octave), sx);
            return (float)Lerp(a, b, sz);
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static double Hash01(long seed, int x, int z, int octave)
        {
            unchecked
            {
                ulong hash = (ulong)seed;
                hash ^= (ulong)(uint)x * 0x9E3779B185EBCA87UL;
                hash ^= (ulong)(uint)z * 0xC2B2AE3D27D4EB4FUL;
                hash ^= (ulong)(uint)octave * 0x165667B19E3779F9UL;
                hash ^= hash >> 33;
                hash *= 0xff51afd7ed558ccdUL;
                hash ^= hash >> 33;
                hash *= 0xc4ceb9fe1a85ec53UL;
                hash ^= hash >> 33;
                return (hash & 0x00FFFFFFUL) / (double)0x01000000UL;
            }
        }
    }

    private readonly record struct WorldgenPreviewRasterCacheKey(
        int Mode,
        string Context,
        long Seed,
        int StartX,
        int StartZ,
        int EndX,
        int EndZ,
        int CellsX,
        int CellsZ);
}
