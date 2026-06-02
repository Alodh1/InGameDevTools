using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
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
    private const int WorldgenPreviewModeTerrainShape = 8;
    private const int WorldgenPreviewModeRegion3D = 9;
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
        "Terrain shape",
        "3D region"
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
    private bool _worldgenPreviewInitialized;
    private ICoreServerAPI? _worldgenPreviewServerApi;
    private GenMaps? _worldgenPreviewGenMaps;
    private Dictionary<int, string>? _worldgenPreviewLandformCodes;
    private Dictionary<int, string>? _worldgenPreviewProvinceCodes;
    private string _worldgenPreviewServerStatus = "Singleplayer server API not checked.";
    private MapLayerBase? _worldgenPreviewMapLayer;
    private int _worldgenPreviewMapLayerMode = -1;
    private string _worldgenPreviewMapLayerStatus = "";
    private WorldgenPreviewRasterCacheKey? _worldgenPreviewRasterCacheKey;
    private uint[]? _worldgenPreviewRasterCache;
    private string _worldgenPreviewRasterStatus = "Raster cache empty.";

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
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(min, max, true);
        if (!serverRequired || serverAvailable)
        {
            DrawWorldgenPreviewRaster(drawList, min, max, seed, centerX, centerZ, pixelsPerBlock);
            DrawWorldgenPreviewGrid(drawList, min, max, centerX, centerZ, pixelsPerBlock);
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
                : $"{modeLabel}: viewport host";
        string inputStatus = WorldgenPreviewModeUsesMapLayer(_worldgenPreviewMode)
            ? "RMB/MMB pans. Mouse wheel zooms. Sampling live MapLayerBase.GenLayer."
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
            WorldgenAssetKind.BlockPatches => WorldgenPreviewModeClimate,
            WorldgenAssetKind.Landforms => WorldgenPreviewModeLandform,
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

    private void RefreshWorldgenServerApi()
    {
        try
        {
            _worldgenPreviewGenMaps = null;
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
        if (WorldgenPreviewModeUsesMapLayer(_worldgenPreviewMode))
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
            _ => new NVector4(0.10f + mix * 0.48f, 0.18f + bands * 0.36f, 0.14f + detail * 0.30f, 1f)
        };

        return ImGui.ColorConvertFloat4ToU32(color);
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

    private readonly record struct WorldgenPreviewRasterCacheKey(
        int Mode,
        long Seed,
        int StartX,
        int StartZ,
        int EndX,
        int EndZ,
        int CellsX,
        int CellsZ);
}
