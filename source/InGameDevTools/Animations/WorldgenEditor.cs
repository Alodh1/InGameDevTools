using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private const double WorldgenPeekWatchdogSeconds = 10.0;
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
    private const int WorldgenPreviewModeRockStrata = 11;
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
        "3D region",
        "Rock strata"
    ];
    private static readonly string[] WorldgenAdvancedFieldKindLabels =
    [
        "String",
        "Boolean",
        "Integer",
        "Float",
        "Object",
        "Array"
    ];
    private static readonly HashSet<string> WorldgenDepositFirstClassKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "generator", "triesPerChunk", "chance", "chanceMultiplier", "withOreMap", "handbookPageCode", "oreMapCode", "attributes"
    };
    private static readonly HashSet<string> WorldgenBlockPatchFirstClassKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "blockCodes", "chance", "quantity", "offsetX", "offsetY", "offsetZ",
        "minTemp", "maxTemp", "minRain", "maxRain", "minForest", "maxForest", "minShrub", "maxShrub", "minFertility", "maxFertility", "minY", "maxY"
    };
    private static readonly HashSet<string> WorldgenLandformFirstClassKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "weight", "group", "hexcolor", "terrainOctaves", "terrainOctaveThresholds", "terrainYKeyPositions", "terrainYKeyThresholds"
    };
    private static readonly HashSet<string> WorldgenRockStrataFirstClassKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "blockcode", "weight", "rockGroup", "genDir", "hexcolor", "amplitudes", "thresholds", "frequencies", "yKeyPositions", "yKeyThresholds"
    };
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

    private readonly record struct WorldgenPreviewParityInfo(string State, string Detail, string Action);

    private readonly List<WorldgenAssetEntry> _worldgenEntries = [];
    private readonly List<WorldgenAssetEntry> _visibleWorldgenEntries = [];
    private readonly Dictionary<string, WorldgenDraftState> _worldgenDraftStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImGuiThreePanelLayoutState _worldgenLayout = new(0.26f, 0.30f);
    private readonly DevToolsEditorDiagnostics _worldgenDiagnostics = new("Worldgen");
    private readonly DevToolsAssetIndexer _worldgenIndexer = new(batchSize: 80);
    private readonly DevToolsTextHistory _worldgenTextHistory = new();
    private bool _worldgenShowTextDiff;
    private bool _worldgenPreviewPoppedOut;
    private float _worldgenPoppedViewportWidth = 1100f;
    private float _worldgenPoppedViewportHeight = 760f;
    private bool _worldgenIndexIncludedServerAssets;
    private string _worldgenFilter = "";
    private string _worldgenDomainFilter = "";
    private string[] _worldgenDomainOptions = ["All domains"];
    private int _worldgenKindFilter;
    private int _worldgenEntryIndex;
    private int _worldgenRowIndex;
    private string _worldgenNewAdvancedFieldName = "";
    private int _worldgenNewAdvancedFieldKindIndex;
    private bool _worldgenDirtyOnly;
    private string _worldgenLoadedKey = "";
    private string _worldgenOriginalText = "";
    private string _worldgenCleanText = "";
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
    private bool _worldgenViewportScreenshotRequested;
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
    private WorldgenPreviewRasterCacheKey? _worldgenPreviewTextureCacheKey;
    private int _worldgenPreviewTextureId;
    private int _worldgenPreviewTextureWidth;
    private int _worldgenPreviewTextureHeight;
    private bool _worldgenPreviewPeekPending;
    private string _worldgenPreviewPeekStatus = "No real chunk peek requested yet.";
    private readonly object _worldgenActivePeekGate = new();
    private WorldgenActivePeek? _worldgenActivePeek;
    private long _worldgenPeekSerial;
    private int _worldgenPreviewRegionSize = 1;
    private int _worldgenPreviewPassIndex;
    private bool _worldgenPreviewAutoPeekOnEdit = true;
    private bool _worldgenPreviewPeekDirty;
    private DateTime _worldgenPreviewPeekDueUtc;
    private string _worldgenPreviewAutoPeekStatus = "Auto 3D refresh waits for draft edits.";
    private WorldgenPeekRegionCacheKey? _worldgenPreviewPeekCacheKey;
    private WorldgenPeekRegionProfile? _worldgenPreviewPeekProfile;
    private WorldgenLoadedWorldOracleProfile? _worldgenPreviewOracleProfile;
    private string _worldgenPreviewOracleStatus = "No loaded-world comparison yet.";
    private bool _worldgenPreviewShowOracleDiff = true;
    private double _worldgenPreview3DDrawAverageMs;
    private string _worldgenPreview3DDrawProfileStatus = "3D draw profile not sampled yet.";
    private string _worldgenPreviewSaveIntegrityStatus = "Save-integrity audit not run.";

    private void WorldgenEditorTab(float deltaSeconds, bool showDiagnostics)
    {
        _ = deltaSeconds;
        ClearActiveTransformGizmo();

        try
        {
            ProcessWorldgenPeekWatchdog();
            EnsureWorldgenEntriesIndexed();

            NVector2 available = ImGui.GetContentRegionAvail();
            float scale = Math.Max(0.75f, _devToolsUiScale);
            float splitterThickness = Math.Max(5f, 6f * scale);

            ImGuiLayoutHelper.CalculateThreePanelWidths(
                available.X,
                splitterThickness,
                _worldgenLayout,
                240f * scale,
                520f * scale,
                640f * scale,
                380f * scale,
                820f * scale,
                out float panelAvailableWidth,
                out float leftWidth,
                out float centerWidth,
                out float rightWidth);

            DrawWorldgenBrowser(new NVector2(leftWidth, available.Y));
            ImGui.SameLine(0, 0);
            ImGuiLayoutHelper.DrawVerticalSplitter("##worldgen-left-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _worldgenLayout.LeftFraction, 240f * scale, Math.Max(240f * scale, panelAvailableWidth - rightWidth - 640f * scale));
            ImGui.SameLine(0, 0);
            DrawWorldgenPreviewPanel(new NVector2(centerWidth, available.Y));
            ImGui.SameLine(0, 0);
            ImGuiLayoutHelper.DrawVerticalSplitter("##worldgen-right-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _worldgenLayout.RightFraction, 380f * scale, Math.Max(380f * scale, panelAvailableWidth - leftWidth - 640f * scale), invertDrag: true);
            ImGui.SameLine(0, 0);
            DrawWorldgenRightPanel(new NVector2(rightWidth, available.Y), showDiagnostics);
            DrawWorldgenPoppedOutViewport();
        }
        catch (Exception exception)
        {
            _worldgenIndexer.Fail();
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
        if (_worldgenIndexer.IsReady &&
            _worldgenEntries.Count == 0 &&
            !_worldgenIndexIncludedServerAssets &&
            InGameDevToolsModSystem.ActiveServerApi != null)
        {
            StartWorldgenIndexing(clearLoaded: false);
        }

        _worldgenIndexer.EnsureIndexed(() => StartWorldgenIndexing(clearLoaded: false), ProcessWorldgenIndexBatch);
    }

    private void StartWorldgenIndexing(bool clearLoaded)
    {
        RememberWorldgenDraft();
        _worldgenIndexer.Begin();
        _worldgenIndexIncludedServerAssets = false;
        _worldgenEntries.Clear();
        _visibleWorldgenEntries.Clear();
        _worldgenDomainOptions = ["All domains"];
        _worldgenEntryIndex = 0;
        _worldgenRowIndex = 0;

        if (clearLoaded)
        {
            _worldgenLoadedKey = "";
            _worldgenOriginalText = "";
            _worldgenCleanText = "";
            _worldgenCurrentText = "";
            _worldgenTextValid = false;
            _worldgenValidationStatus = "No worldgen asset loaded.";
            _worldgenDraftStates.Clear();
        }

        // Authored files first: same-location entries then win the duplicate check, so the
        // editor resumes from the user's saved copy instead of the pristine game asset.
        _worldgenIndexer.AddSource("authored worldgen files", () => CollectToolAuthoredAssets("worldgen"), IsWorldgenJsonAsset, _worldgenDiagnostics);
        _worldgenIndexer.AddSource("client worldgen category", () => _api.Assets.GetManyInCategory("worldgen", ""), IsWorldgenJsonAsset, _worldgenDiagnostics);
        _worldgenIndexer.AddSource("client loaded assets", () => _api.Assets.AllAssets.Values, IsWorldgenJsonAsset, _worldgenDiagnostics);

        ICoreServerAPI? serverApi = InGameDevToolsModSystem.ActiveServerApi;
        if (serverApi != null)
        {
            _worldgenIndexIncludedServerAssets = true;
            _worldgenIndexer.AddSource("server worldgen category", () => serverApi.Assets.GetManyInCategory("worldgen", ""), IsWorldgenJsonAsset, _worldgenDiagnostics);
            _worldgenIndexer.AddSource("server loaded assets", () => serverApi.Assets.AllAssets.Values, IsWorldgenJsonAsset, _worldgenDiagnostics);
        }

        _worldgenIndexer.SortPendingByLocation();
        _worldgenStatus = BuildWorldgenIndexProgressText();
    }

    private void ProcessWorldgenIndexBatch()
    {
        if (!_worldgenIndexer.TryProcessBatch(
                IndexWorldgenAsset,
                CompleteWorldgenIndexing,
                () => _worldgenStatus = BuildWorldgenIndexProgressText(),
                out Exception? error))
        {
            _worldgenStatus = $"Worldgen indexing failed: {error?.Message}";
            _worldgenDiagnostics.Exception("Worldgen indexing failed", error!);
        }
    }

    private void CompleteWorldgenIndexing()
    {
        _worldgenEntries.Sort((left, right) => string.Compare(left.SortKey, right.SortKey, StringComparison.OrdinalIgnoreCase));
        _worldgenDomainOptions = _worldgenEntries
            .Select(entry => entry.Domain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain.Equals("game", StringComparison.OrdinalIgnoreCase) ? "" : domain, StringComparer.OrdinalIgnoreCase)
            .Prepend("All domains")
            .ToArray();
        RebuildVisibleWorldgenEntries();
        _worldgenStatus = $"Indexed {_worldgenEntries.Count} worldgen JSON asset(s).";
        if (_visibleWorldgenEntries.Count > 0 && string.IsNullOrWhiteSpace(_worldgenLoadedKey))
        {
            LoadWorldgenEntry(_visibleWorldgenEntries[Math.Clamp(_worldgenEntryIndex, 0, _visibleWorldgenEntries.Count - 1)]);
        }
    }

    private string BuildWorldgenIndexProgressText()
    {
        string serverSuffix = _worldgenIndexIncludedServerAssets ? " including server assets" : " client assets only";
        return $"Indexing worldgen assets {_worldgenIndexer.Position}/{_worldgenIndexer.PendingAssets.Count}{serverSuffix}.";
    }

    private void IndexWorldgenAsset(IAsset asset)
    {
        _worldgenEntries.Add(new WorldgenAssetEntry(asset));
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
            if (_worldgenKindFilter > 0 &&
                !MatchesWorldgenKindFilter(entry.Kind, entry.IsContentClassified, FilterIndexToWorldgenKind(_worldgenKindFilter))) continue;
            if (_worldgenDirtyOnly && !IsWorldgenEntryDirty(entry)) continue;
            if (!IsWorldgenEntryCompatibleWithCurrentPreview(entry, out _)) continue;
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
        filterChanged |= DrawWorldgenDomainCombo();
        filterChanged |= ImGui.Combo("Kind##worldgen-kind", ref _worldgenKindFilter, WorldgenKindFilterLabels, WorldgenKindFilterLabels.Length);
        filterChanged |= ImGui.Checkbox("Dirty only##worldgen-dirty-only", ref _worldgenDirtyOnly);
        ImGui.TextDisabled(SanitizeWorldgenPreviewText(GetWorldgenPreviewAssetRestrictionStatus(), 90));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(SanitizeWorldgenPreviewText(GetWorldgenPreviewAssetRestrictionStatus(), 260));
        }

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

        if (_worldgenIndexer.IsIndexing)
        {
            ImGui.TextWrapped(_worldgenStatus);
        }

        if (ImGui.BeginChild("##worldgen-entry-list", new NVector2(-float.Epsilon, -float.Epsilon), true))
        {
            DrawClippedWorldgenEntryRows();
        }
        ImGui.EndChild();
        ImGui.EndChild();
    }

    private bool DrawWorldgenDomainCombo()
    {
        int current = string.IsNullOrWhiteSpace(_worldgenDomainFilter)
            ? 0
            : Math.Max(0, Array.FindIndex(_worldgenDomainOptions, domain => domain.Equals(_worldgenDomainFilter, StringComparison.OrdinalIgnoreCase)));
        bool changed = ImGui.Combo("Domain##worldgen-domain", ref current, _worldgenDomainOptions, _worldgenDomainOptions.Length);
        if (changed)
        {
            _worldgenDomainFilter = current <= 0 ? "" : _worldgenDomainOptions[current];
        }
        return changed;
    }

    private void DrawClippedWorldgenEntryRows()
    {
        float rowHeight = Math.Max(1f, ImGui.GetTextLineHeightWithSpacing());
        float visibleHeight = Math.Max(rowHeight, ImGui.GetContentRegionAvail().Y);
        float scrollY = Math.Max(0f, ImGui.GetScrollY());
        int first = Math.Clamp((int)Math.Floor(scrollY / rowHeight) - 2, 0, _visibleWorldgenEntries.Count);
        int visibleCount = Math.Max(1, (int)Math.Ceiling(visibleHeight / rowHeight) + 5);
        int last = Math.Clamp(first + visibleCount, first, _visibleWorldgenEntries.Count);

        if (first > 0)
        {
            ImGui.Dummy(new NVector2(1f, first * rowHeight));
        }

        for (int i = first; i < last; i++)
        {
            WorldgenAssetEntry entry = _visibleWorldgenEntries[i];
            bool dirty = IsWorldgenEntryDirty(entry);
            bool compatible = IsWorldgenEntryCompatibleWithCurrentPreview(entry, out string compatibilityReason);
            string label = $"{entry.KindLabel}: {entry.Domain}:{entry.AssetPath}{(dirty ? " *" : "")}##worldgen-entry-{i}";
            if (!compatible) ImGui.BeginDisabled();
            if (ImGui.Selectable(label, i == _worldgenEntryIndex) && compatible)
            {
                _worldgenEntryIndex = i;
                LoadWorldgenEntry(entry);
            }
            if (!compatible) ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{entry.Domain}:{entry.AssetPath}\n{entry.KindLabel}\n{(dirty ? "Modified draft" : "Unmodified")}\n{compatibilityReason}");
            }
        }

        if (last < _visibleWorldgenEntries.Count)
        {
            ImGui.Dummy(new NVector2(1f, (_visibleWorldgenEntries.Count - last) * rowHeight));
        }
    }

    private void DrawWorldgenEditorPanel(NVector2 size)
    {
        ImGui.BeginChild("##worldgen-editor", size, true);

        WorldgenAssetEntry? entry = SelectedWorldgenEntry;
        if (entry == null)
        {
            ImGui.TextWrapped(_worldgenIndexer.IsIndexing ? _worldgenStatus : "No worldgen JSON asset selected.");
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

    private void DrawWorldgenRightPanel(NVector2 size, bool showDiagnostics)
    {
        ImGui.BeginChild("##worldgen-right-panel", size, false);
        float availableHeight = Math.Max(1f, ImGui.GetContentRegionAvail().Y);
        float inspectorHeight = Math.Clamp(availableHeight * 0.28f, 170f * Math.Max(0.75f, _devToolsUiScale), Math.Max(170f, availableHeight * 0.45f));
        float editorHeight = Math.Max(220f, availableHeight - inspectorHeight - 6f);

        DrawWorldgenEditorPanel(new NVector2(-float.Epsilon, editorHeight));
        ImGui.Separator();
        DrawWorldgenInspector(new NVector2(-float.Epsilon, Math.Max(140f, ImGui.GetContentRegionAvail().Y)), showDiagnostics);
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
                    ScheduleWorldgenRealtimePeek("selected row changed");
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
            if (ImGui.InputTextMultiline("##worldgen-row-json-text", ref rowText, DevToolsImGuiTextBuffer.Capacity(rowText, growthLimit: 256 * 1024), new NVector2(-float.Epsilon, 180f), ImGuiInputTextFlags.AllowTabInput))
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
        changed |= DrawWorldgenIntDragField(row, "triesPerChunk", "Tries/chunk", 0, 10000, 1f);
        changed |= DrawWorldgenProbabilityField(row, "chance", "Chance");
        changed |= DrawWorldgenFloatDragField(row, "chanceMultiplier", "Chance multiplier", 0f, 100f, 0.01f, "%.3f");
        changed |= DrawWorldgenBoolField(row, "withOreMap", "With ore map");
        changed |= DrawWorldgenStringField(row, "handbookPageCode", "Handbook code");
        changed |= DrawWorldgenStringField(row, "oreMapCode", "Ore map code");
        changed |= DrawWorldgenNatFloatObjects(row, "NatFloat fields");
        changed |= DrawWorldgenObjectJsonField(row, "attributes", "Attributes JSON");
        changed |= DrawWorldgenAdvancedRowFields(row, WorldgenDepositFirstClassKeys, skipNatFloatObjects: true);
        return changed;
    }

    private bool DrawBlockPatchWorldgenRow(JObject row)
    {
        bool changed = false;
        changed |= DrawWorldgenStringArrayField(row, "blockCodes", "Block codes");
        changed |= DrawWorldgenProbabilityField(row, "chance", "Chance");
        changed |= DrawWorldgenNatFloatField(row, "quantity", "Quantity");
        changed |= DrawWorldgenNatFloatField(row, "offsetX", "Offset X");
        changed |= DrawWorldgenNatFloatField(row, "offsetY", "Offset Y");
        changed |= DrawWorldgenNatFloatField(row, "offsetZ", "Offset Z");
        ImGui.SeparatorText("Climate and height");
        changed |= DrawWorldgenRangeField(row, "minTemp", "maxTemp", "Temperature", -60f, 60f, "%.1f");
        changed |= DrawWorldgenRangeField(row, "minRain", "maxRain", "Rain", 0f, 1f, "%.3f");
        changed |= DrawWorldgenRangeField(row, "minForest", "maxForest", "Forest", 0f, 1f, "%.3f");
        changed |= DrawWorldgenRangeField(row, "minShrub", "maxShrub", "Shrub", 0f, 1f, "%.3f");
        changed |= DrawWorldgenRangeField(row, "minFertility", "maxFertility", "Fertility", 0f, 1f, "%.3f");
        changed |= DrawWorldgenRangeField(row, "minY", "maxY", "Y", 0f, 1f, "%.3f");
        changed |= DrawWorldgenAdvancedRowFields(row, WorldgenBlockPatchFirstClassKeys);
        return changed;
    }

    private bool DrawLandformWorldgenRow(JObject row)
    {
        bool changed = false;
        changed |= DrawWorldgenStringField(row, "code", "Code");
        changed |= DrawWorldgenFloatDragField(row, "weight", "Weight", 0f, 100000f, 0.1f, "%.3f");
        changed |= DrawWorldgenStringField(row, "group", "Group");
        changed |= DrawWorldgenHexColorField(row, "hexcolor", "Color");
        changed |= DrawWorldgenFloatArrayField(row, "terrainOctaves", "Terrain octaves");
        changed |= DrawWorldgenFloatArrayField(row, "terrainOctaveThresholds", "Octave thresholds");
        changed |= DrawWorldgenFloatArrayField(row, "terrainYKeyPositions", "Y key positions");
        changed |= DrawWorldgenFloatArrayField(row, "terrainYKeyThresholds", "Y key thresholds");
        changed |= DrawWorldgenAdvancedRowFields(row, WorldgenLandformFirstClassKeys);
        return changed;
    }

    private bool DrawRockStrataWorldgenRow(JObject row)
    {
        bool changed = false;
        changed |= DrawWorldgenStringField(row, "blockcode", "Block code");
        changed |= DrawWorldgenFloatDragField(row, "weight", "Weight", 0f, 100000f, 0.1f, "%.3f");
        changed |= DrawWorldgenStringField(row, "rockGroup", "Rock group");
        changed |= DrawWorldgenStringField(row, "genDir", "Generation direction");
        changed |= DrawWorldgenHexColorField(row, "hexcolor", "Color");
        changed |= DrawWorldgenFloatArrayField(row, "amplitudes", "Amplitudes");
        changed |= DrawWorldgenFloatArrayField(row, "thresholds", "Thresholds");
        changed |= DrawWorldgenFloatArrayField(row, "frequencies", "Frequencies");
        changed |= DrawWorldgenFloatArrayField(row, "yKeyPositions", "Y key positions");
        changed |= DrawWorldgenFloatArrayField(row, "yKeyThresholds", "Y key thresholds");
        changed |= DrawWorldgenAdvancedRowFields(row, WorldgenRockStrataFirstClassKeys);
        return changed;
    }

    private bool DrawWorldgenAdvancedRowFields(JObject row, IReadOnlySet<string> firstClassKeys, bool skipNatFloatObjects = false)
    {
        List<JProperty> advancedProperties = row.Properties()
            .Where(property => !firstClassKeys.Contains(property.Name))
            .Where(property => !skipNatFloatObjects || property.Value is not JObject obj || !LooksLikeNatFloat(obj))
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!ImGui.CollapsingHeader($"Advanced fields ({advancedProperties.Count})##worldgen-advanced-row-fields")) return false;

        bool changed = false;
        if (advancedProperties.Count == 0)
        {
            ImGui.TextDisabled("No unhandled fields on this row.");
        }

        foreach (JProperty property in advancedProperties)
        {
            ImGui.PushID($"worldgen-advanced-{property.Name}");
            changed |= DrawWorldgenAdvancedRowField(row, property);
            ImGui.PopID();
        }

        ImGui.SeparatorText("Add advanced field");
        ImGui.SetNextItemWidth(180f);
        ImGui.InputTextWithHint("##worldgen-advanced-new-name", "field name", ref _worldgenNewAdvancedFieldName, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        ImGui.Combo("Type##worldgen-advanced-new-kind", ref _worldgenNewAdvancedFieldKindIndex, WorldgenAdvancedFieldKindLabels, WorldgenAdvancedFieldKindLabels.Length);
        ImGui.SameLine();
        if (ImGui.Button("Add##worldgen-advanced-add"))
        {
            changed |= TryAddWorldgenAdvancedRowField(row, firstClassKeys);
        }

        return changed;
    }

    private bool DrawWorldgenAdvancedRowField(JObject row, JProperty property)
    {
        bool changed = false;
        JToken value = property.Value;

        if (value is JObject obj && LooksLikeNatFloat(obj))
        {
            ImGui.TextUnformatted(property.Name);
            changed |= DrawWorldgenNatFloatObject(obj, property.Name);
            ImGui.SameLine();
            if (ImGui.Button($"Remove##remove-{property.Name}"))
            {
                row.Remove(property.Name);
                changed = true;
            }
        }
        else if (value is JObject objectValue)
        {
            if (ImGui.Button($"Remove##remove-{property.Name}"))
            {
                row.Remove(property.Name);
                return true;
            }
            changed |= DrawWorldgenSemanticObject(property.Name, objectValue);
        }
        else if (value is JArray arrayValue)
        {
            if (ImGui.Button($"Remove##remove-{property.Name}"))
            {
                row.Remove(property.Name);
                return true;
            }
            changed |= DrawWorldgenSemanticArray(property.Name, arrayValue);
        }
        else
        {
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
                    if (ImGui.InputText($"##string-{property.Name}", ref stringValue, 2048))
                    {
                        SetOrRemoveString(row, property.Name, stringValue);
                        changed = true;
                    }
                    break;
                }
            }

            ImGui.SameLine();
            if (ImGui.Button($"Remove##remove-{property.Name}"))
            {
                row.Remove(property.Name);
                changed = true;
            }
        }

        return changed;
    }

    private bool DrawWorldgenSemanticObject(string label, JObject obj)
    {
        bool changed = false;
        if (!ImGui.TreeNodeEx($"{label} object##worldgen-object-{label}", ImGuiTreeNodeFlags.DefaultOpen)) return false;

        foreach (JProperty child in obj.Properties().OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase).ToList())
        {
            ImGui.PushID($"worldgen-object-field-{label}-{child.Name}");
            changed |= DrawWorldgenSemanticObjectProperty(obj, child);
            ImGui.PopID();
        }

        ImGui.TreePop();
        return changed;
    }

    private bool DrawWorldgenSemanticObjectProperty(JObject obj, JProperty property)
    {
        JToken value = property.Value;
        bool changed = false;
        if (value is JObject childObject)
        {
            if (ImGui.Button($"Remove##remove-object-{property.Name}"))
            {
                obj.Remove(property.Name);
                return true;
            }
            changed |= DrawWorldgenSemanticObject(property.Name, childObject);
            return changed;
        }

        if (value is JArray childArray)
        {
            if (ImGui.Button($"Remove##remove-array-{property.Name}"))
            {
                obj.Remove(property.Name);
                return true;
            }
            changed |= DrawWorldgenSemanticArray(property.Name, childArray);
            return changed;
        }

        ImGui.TextUnformatted(property.Name);
        ImGui.SameLine();
        changed |= DrawWorldgenSemanticScalar(
            value,
            property.Name,
            replacement => property.Value = replacement,
            () => obj.Remove(property.Name));
        return changed;
    }

    private bool DrawWorldgenSemanticArray(string label, JArray array)
    {
        bool changed = false;
        if (!ImGui.TreeNodeEx($"{label} array ({array.Count})##worldgen-array-{label}", ImGuiTreeNodeFlags.DefaultOpen)) return false;

        int removeIndex = -1;
        for (int index = 0; index < array.Count; index++)
        {
            JToken value = array[index];
            ImGui.PushID($"worldgen-array-{label}-{index}");
            if (value is JObject childObject)
            {
                changed |= DrawWorldgenSemanticObject($"[{index}]", childObject);
            }
            else if (value is JArray childArray)
            {
                changed |= DrawWorldgenSemanticArray($"[{index}]", childArray);
            }
            else
            {
                ImGui.TextUnformatted($"[{index}]");
                ImGui.SameLine();
                changed |= DrawWorldgenSemanticScalar(
                    value,
                    $"item-{index}",
                    replacement => array[index] = replacement,
                    () => removeIndex = index);
            }

            if (value is JObject or JArray)
            {
                ImGui.SameLine();
                if (ImGui.Button("Remove##remove-complex-array-item"))
                {
                    removeIndex = index;
                }
            }
            ImGui.PopID();
        }

        if (removeIndex >= 0)
        {
            array.RemoveAt(removeIndex);
            changed = true;
        }

        if (ImGui.Button($"Add value##worldgen-array-add-{label}"))
        {
            array.Add(CreateWorldgenAdvancedFieldDefault(InferWorldgenArrayDefaultKind(array)));
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.Button($"Add object##worldgen-array-add-object-{label}"))
        {
            array.Add(new JObject());
            changed = true;
        }

        ImGui.TreePop();
        return changed;
    }

    private bool DrawWorldgenSemanticScalar(JToken value, string label, Action<JToken> replace, Action remove)
    {
        switch (value.Type)
        {
            case JTokenType.Boolean:
            {
                bool boolValue = value.Value<bool>();
                if (ImGui.Checkbox($"##worldgen-semantic-bool-{label}", ref boolValue))
                {
                    replace(boolValue);
                    return true;
                }
                break;
            }
            case JTokenType.Integer:
            {
                int intValue = value.Value<int>();
                ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 90f));
                if (ImGui.InputInt($"##worldgen-semantic-int-{label}", ref intValue))
                {
                    replace(intValue);
                    return true;
                }
                break;
            }
            case JTokenType.Float:
            {
                float floatValue = value.Value<float>();
                ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 90f));
                if (ImGui.InputFloat($"##worldgen-semantic-float-{label}", ref floatValue, 0, 0, "%.4f"))
                {
                    replace(floatValue);
                    return true;
                }
                break;
            }
            default:
            {
                string stringValue = value.Type == JTokenType.Null ? "" : value.ToString();
                ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 90f));
                if (ImGui.InputText($"##worldgen-semantic-string-{label}", ref stringValue, 4096))
                {
                    replace(stringValue);
                    return true;
                }
                break;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button($"Remove##worldgen-semantic-remove-{label}"))
        {
            remove();
            return true;
        }

        return false;
    }

    private bool TryAddWorldgenAdvancedRowField(JObject row, IReadOnlySet<string> firstClassKeys)
    {
        string propertyName = _worldgenNewAdvancedFieldName.Trim();
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            _worldgenValidationStatus = "Advanced worldgen field name is required.";
            return false;
        }
        if (firstClassKeys.Contains(propertyName))
        {
            _worldgenValidationStatus = $"{propertyName} is already handled by a structured worldgen control.";
            return false;
        }
        if (row[propertyName] != null)
        {
            _worldgenValidationStatus = $"Worldgen row field {propertyName} already exists.";
            return false;
        }

        row[propertyName] = CreateWorldgenAdvancedFieldDefault(_worldgenNewAdvancedFieldKindIndex);
        _worldgenNewAdvancedFieldName = "";
        return true;
    }

    private static JToken CreateWorldgenAdvancedFieldDefault(int fieldKindIndex)
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

    private static int InferWorldgenArrayDefaultKind(JArray array)
    {
        JToken? first = array.FirstOrDefault(token => token.Type != JTokenType.Null);
        return first?.Type switch
        {
            JTokenType.Boolean => 1,
            JTokenType.Integer => 2,
            JTokenType.Float => 3,
            JTokenType.Object => 4,
            JTokenType.Array => 5,
            _ => 0
        };
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
        JArray array = row[propertyName] as JArray ?? [];
        bool exists = row[propertyName] is JArray;
        bool changed = false;

        ImGui.PushID($"worldgen-float-array-{propertyName}");
        if (!exists)
        {
            if (ImGui.Button($"Add {label}"))
            {
                row[propertyName] = new JArray(0f);
                changed = true;
            }

            ImGui.PopID();
            return changed;
        }

        if (ImGui.CollapsingHeader($"{label} ({array.Count})"))
        {
            int removeIndex = -1;
            for (int index = 0; index < array.Count; index++)
            {
                float value = TryReadJsonFloat(array[index], out float parsed) ? parsed : 0f;
                ImGui.SetNextItemWidth(Math.Max(100f, ImGui.GetContentRegionAvail().X - 76f));
                if (ImGui.DragFloat($"[{index}]##worldgen-array-value-{index}", ref value, 0.01f, -100000f, 100000f, "%.4f"))
                {
                    array[index] = value;
                    changed = true;
                }

                ImGui.SameLine();
                if (ImGui.Button($"-##worldgen-array-remove-{index}"))
                {
                    removeIndex = index;
                }
            }

            if (removeIndex >= 0)
            {
                array.RemoveAt(removeIndex);
                changed = true;
            }

            if (ImGui.Button($"Add value##worldgen-array-add-{propertyName}"))
            {
                array.Add(0f);
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.Button($"Clear##worldgen-array-clear-{propertyName}"))
            {
                row.Remove(propertyName);
                changed = true;
            }
        }

        ImGui.PopID();
        return changed;
    }

    private bool DrawWorldgenIntField(JObject row, string propertyName, string label)
    {
        int value = TryReadJsonDouble(row[propertyName], out double parsed) ? (int)Math.Round(parsed) : 0;
        if (!ImGui.InputInt($"{label}##worldgen-{propertyName}", ref value)) return false;

        row[propertyName] = value;
        return true;
    }

    private bool DrawWorldgenIntDragField(JObject row, string propertyName, string label, int min, int max, float speed)
    {
        int value = TryReadJsonDouble(row[propertyName], out double parsed) ? (int)Math.Round(parsed) : min;
        ImGui.SetNextItemWidth(-float.Epsilon);
        if (!ImGui.DragInt($"{label}##worldgen-{propertyName}", ref value, speed, min, max)) return false;

        row[propertyName] = Math.Clamp(value, min, max);
        return true;
    }

    private bool DrawWorldgenFloatField(JObject row, string propertyName, string label)
    {
        float value = TryReadJsonFloat(row[propertyName], out float parsed) ? parsed : 0f;
        if (!ImGui.InputFloat($"{label}##worldgen-{propertyName}", ref value)) return false;

        row[propertyName] = value;
        return true;
    }

    private bool DrawWorldgenFloatDragField(JObject row, string propertyName, string label, float min, float max, float speed, string format)
    {
        float value = TryReadJsonFloat(row[propertyName], out float parsed) ? parsed : min;
        ImGui.SetNextItemWidth(-float.Epsilon);
        if (!ImGui.DragFloat($"{label}##worldgen-{propertyName}", ref value, speed, min, max, format)) return false;

        row[propertyName] = Math.Clamp(value, min, max);
        return true;
    }

    private bool DrawWorldgenProbabilityField(JObject row, string propertyName, string label)
    {
        float value = TryReadJsonFloat(row[propertyName], out float parsed) ? parsed : 0f;
        float max = Math.Max(1f, value);
        ImGui.SetNextItemWidth(-float.Epsilon);
        if (!ImGui.SliderFloat($"{label}##worldgen-{propertyName}", ref value, 0f, max, "%.3f")) return false;

        row[propertyName] = Math.Clamp(value, 0f, max);
        return true;
    }

    private bool DrawWorldgenRangeField(JObject row, string minPropertyName, string maxPropertyName, string label, float defaultMin, float defaultMax, string format)
    {
        float minValue = TryReadJsonFloat(row[minPropertyName], out float parsedMin) ? parsedMin : defaultMin;
        float maxValue = TryReadJsonFloat(row[maxPropertyName], out float parsedMax) ? parsedMax : defaultMax;
        float lower = Math.Min(defaultMin, Math.Min(minValue, maxValue));
        float upper = Math.Max(defaultMax, Math.Max(minValue, maxValue));
        bool changed = false;

        ImGui.PushID($"worldgen-range-{minPropertyName}-{maxPropertyName}");
        ImGui.TextUnformatted(label);
        float width = Math.Max(90f, (ImGui.GetContentRegionAvail().X - 10f) * 0.5f);
        ImGui.SetNextItemWidth(width);
        changed |= ImGui.SliderFloat($"Min##{minPropertyName}", ref minValue, lower, upper, format);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width);
        changed |= ImGui.SliderFloat($"Max##{maxPropertyName}", ref maxValue, lower, upper, format);

        if (changed)
        {
            if (minValue > maxValue)
            {
                float previousMin = TryReadJsonFloat(row[minPropertyName], out float previousMinValue) ? previousMinValue : minValue;
                float previousMax = TryReadJsonFloat(row[maxPropertyName], out float previousMaxValue) ? previousMaxValue : maxValue;
                if (Math.Abs(previousMin - minValue) > Math.Abs(previousMax - maxValue))
                {
                    maxValue = minValue;
                }
                else
                {
                    minValue = maxValue;
                }
            }

            row[minPropertyName] = minValue;
            row[maxPropertyName] = maxValue;
        }

        ImGui.PopID();
        return changed;
    }

    private bool DrawWorldgenHexColorField(JObject row, string propertyName, string label)
    {
        string original = row[propertyName]?.ToString() ?? "";
        NVector3 color = TryParseWorldgenHexColor(original, out NVector3 parsed)
            ? parsed
            : new NVector3(0.55f, 0.55f, 0.55f);

        ImGui.SetNextItemWidth(-float.Epsilon);
        if (!ImGui.ColorEdit3($"{label}##worldgen-{propertyName}", ref color)) return false;

        int r = Math.Clamp((int)MathF.Round(color.X * 255f), 0, 255);
        int g = Math.Clamp((int)MathF.Round(color.Y * 255f), 0, 255);
        int b = Math.Clamp((int)MathF.Round(color.Z * 255f), 0, 255);
        row[propertyName] = $"{r:X2}{g:X2}{b:X2}";
        return true;
    }

    private bool DrawWorldgenBoolField(JObject row, string propertyName, string label)
    {
        bool value = TryReadJsonBool(row[propertyName], out bool parsed) && parsed;
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
            changed |= DrawWorldgenNatFloatDistributionField(natFloat);
            changed |= DrawWorldgenFloatDragField(natFloat, "avg", "Average", -100000f, 100000f, 0.01f, "%.4f");
            changed |= DrawWorldgenFloatDragField(natFloat, "var", "Variance", 0f, 100000f, 0.01f, "%.4f");
            changed |= DrawWorldgenFloatDragField(natFloat, "offset", "Offset", -100000f, 100000f, 0.01f, "%.4f");
            if (ImGui.Button("Remove"))
            {
                row.Remove(propertyName);
                changed = true;
            }
        }

        ImGui.PopID();
        return changed;
    }

    private bool DrawWorldgenNatFloatObjects(JObject row, string label)
    {
        List<JProperty> natFloatProperties = row.Properties()
            .Where(property => property.Value is JObject obj && LooksLikeNatFloat(obj))
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (natFloatProperties.Count == 0) return false;

        bool changed = false;
        if (ImGui.CollapsingHeader($"{label} ({natFloatProperties.Count})##worldgen-natfloat-objects"))
        {
            foreach (JProperty property in natFloatProperties)
            {
                if (property.Value is JObject natFloat)
                {
                    changed |= DrawWorldgenNatFloatObject(natFloat, property.Name);
                }
            }
        }

        return changed;
    }

    private bool DrawWorldgenNatFloatObject(JObject natFloat, string label)
    {
        bool changed = false;
        ImGui.PushID($"worldgen-natfloat-object-{label}");
        if (ImGui.TreeNode(label))
        {
            changed |= DrawWorldgenNatFloatDistributionField(natFloat);
            changed |= DrawWorldgenFloatDragField(natFloat, "avg", "Average", -100000f, 100000f, 0.01f, "%.4f");
            changed |= DrawWorldgenFloatDragField(natFloat, "var", "Variance", 0f, 100000f, 0.01f, "%.4f");
            changed |= DrawWorldgenFloatDragField(natFloat, "offset", "Offset", -100000f, 100000f, 0.01f, "%.4f");
            ImGui.TreePop();
        }

        ImGui.PopID();
        return changed;
    }

    private bool DrawWorldgenNatFloatDistributionField(JObject natFloat)
    {
        string[] options = ["uniform", "gaussian", "narrowgaussian", "invexp", "triangle"];
        string current = natFloat["dist"]?.ToString() ?? "uniform";
        int index = Array.FindIndex(options, option => string.Equals(option, current, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 0;

        ImGui.SetNextItemWidth(-float.Epsilon);
        if (!ImGui.Combo("Distribution##worldgen-natfloat-dist", ref index, options, options.Length)) return false;

        natFloat["dist"] = options[Math.Clamp(index, 0, options.Length - 1)];
        return true;
    }

    private static bool TryParseWorldgenHexColor(string raw, out NVector3 color)
    {
        color = default;
        string value = (raw ?? "").Trim().TrimStart('#');
        if (value.Length != 6 ||
            !int.TryParse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r) ||
            !int.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g) ||
            !int.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b))
        {
            return false;
        }

        color = new NVector3(r / 255f, g / 255f, b / 255f);
        return true;
    }

    private bool DrawWorldgenObjectJsonField(JObject row, string propertyName, string label)
    {
        string value = row[propertyName]?.ToString(Formatting.Indented) ?? "{}";
        if (!ImGui.CollapsingHeader(label)) return false;

        if (!ImGui.InputTextMultiline($"##worldgen-object-json-{propertyName}", ref value, DevToolsImGuiTextBuffer.Capacity(value, growthLimit: 256 * 1024), new NVector2(-float.Epsilon, 160f), ImGuiInputTextFlags.AllowTabInput)) return false;

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
        _worldgenTextHistory.Record(_worldgenCurrentText, ImGui.GetTime());
        if (DevToolsJsonTextTools.DrawEditToolbar("worldgen-json-tools", ref _worldgenCurrentText, _worldgenTextHistory, out string toolStatus))
        {
            ValidateWorldgenCurrentText();
            RememberWorldgenDraft();
            InvalidateWorldgenPreviewRasterCache();
            ScheduleWorldgenRealtimePeek("raw JSON tools changed text");
        }
        if (!string.IsNullOrEmpty(toolStatus))
        {
            _worldgenStatus = toolStatus;
        }

        ImGui.SameLine();
        ImGui.Checkbox("Diff##worldgen-json-diff-toggle", ref _worldgenShowTextDiff);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Show the draft's line diff against the loaded source (both sides normalized when parseable).");
        }

        if (_worldgenShowTextDiff)
        {
            DevToolsTextDiffView.Draw(
                "worldgen-json",
                _worldgenOriginalText,
                _worldgenCurrentText,
                Math.Clamp(ImGui.GetContentRegionAvail().Y * 0.45f, 120f, 320f));
        }

        if (ImGui.InputTextMultiline("##worldgen-json-text", ref _worldgenCurrentText, DevToolsImGuiTextBuffer.Capacity(_worldgenCurrentText), new NVector2(-float.Epsilon, Math.Max(180f, ImGui.GetContentRegionAvail().Y - 24f)), ImGuiInputTextFlags.AllowTabInput))
        {
            _worldgenTextHistory.Record(_worldgenCurrentText, ImGui.GetTime());
            ValidateWorldgenCurrentText();
            RememberWorldgenDraft();
            InvalidateWorldgenPreviewRasterCache();
            ScheduleWorldgenRealtimePeek("raw JSON draft changed");
        }
    }

    private void DrawWorldgenInspector(NVector2 size, bool showDiagnostics)
    {
        ImGui.BeginChild("##worldgen-inspector", size, true);
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
            _worldgenCurrentText = _worldgenCleanText;
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

    private void DrawWorldgenPreviewPanel(NVector2 size)
    {
        ImGui.BeginChild("##worldgen-preview-panel", size, true);
        if (_worldgenPreviewPoppedOut)
        {
            ImGui.TextWrapped("Worldgen preview is popped out into a separate resizable window.");
            if (ImGui.Button("Return viewport here##worldgen-preview-pop-in", new NVector2(-1, 0)))
            {
                _worldgenPreviewPoppedOut = false;
            }

            ImGui.TextWrapped(GetWorldgenPreviewAutoModeStatus());
            ImGui.EndChild();
            return;
        }

        DrawWorldgenPreviewViewport();
        ImGui.EndChild();
    }

    private void DrawWorldgenPoppedOutViewport()
    {
        if (!_worldgenPreviewPoppedOut) return;

        bool open = true;
        NVector2 displaySize = GetVanillaImGuiDisplaySize();
        _worldgenPoppedViewportWidth = Math.Clamp(_worldgenPoppedViewportWidth, 520f, Math.Max(520f, displaySize.X - 24f));
        _worldgenPoppedViewportHeight = Math.Clamp(_worldgenPoppedViewportHeight, 360f, Math.Max(360f, displaySize.Y - 36f));
        ImGui.SetNextWindowSize(new NVector2(_worldgenPoppedViewportWidth, _worldgenPoppedViewportHeight), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new NVector2(520f, 360f), new NVector2(Math.Max(520f, displaySize.X), Math.Max(360f, displaySize.Y)));

        if (ImGui.Begin("Worldgen viewport##worldgen-popped-viewport", ref open, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.SetWindowFontScale(_devToolsUiScale);
            if (ImGui.Button("Dock back##worldgen-popout-dock-back"))
            {
                _worldgenPreviewPoppedOut = false;
            }
            ImGui.SameLine();
            if (ImGui.Button("Fill##worldgen-popout-place-fill"))
            {
                SetWorldgenPoppedViewportRect(10f, 44f, displaySize.X - 20f, displaySize.Y - 54f);
            }
            ImGui.SameLine();
            if (ImGui.Button("Center##worldgen-popout-place-center"))
            {
                float width = Math.Min(_worldgenPoppedViewportWidth, displaySize.X - 20f);
                float height = Math.Min(_worldgenPoppedViewportHeight, displaySize.Y - 54f);
                SetWorldgenPoppedViewportRect((displaySize.X - width) * 0.5f, (displaySize.Y - height) * 0.5f, width, height);
            }

            float requestedWidth = _worldgenPoppedViewportWidth;
            float requestedHeight = _worldgenPoppedViewportHeight;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            bool resize = ImGui.InputFloat("W##worldgen-popout-width", ref requestedWidth, 0, 0, "%.0f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            resize |= ImGui.InputFloat("H##worldgen-popout-height", ref requestedHeight, 0, 0, "%.0f");
            if (resize)
            {
                _worldgenPoppedViewportWidth = Math.Clamp(requestedWidth, 520f, Math.Max(520f, displaySize.X));
                _worldgenPoppedViewportHeight = Math.Clamp(requestedHeight, 360f, Math.Max(360f, displaySize.Y));
                ImGui.SetWindowSize(new NVector2(_worldgenPoppedViewportWidth, _worldgenPoppedViewportHeight), ImGuiCond.Always);
            }

            DrawWorldgenPreviewViewport();

            if (!ImGui.IsAnyItemActive())
            {
                NVector2 windowSize = ImGui.GetWindowSize();
                _worldgenPoppedViewportWidth = windowSize.X;
                _worldgenPoppedViewportHeight = windowSize.Y;
            }

            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();

        if (!open)
        {
            _worldgenPreviewPoppedOut = false;
        }
    }

    private void SetWorldgenPoppedViewportRect(float x, float y, float width, float height)
    {
        _worldgenPoppedViewportWidth = Math.Max(520f, width);
        _worldgenPoppedViewportHeight = Math.Max(360f, height);
        ImGui.SetWindowPos(new NVector2(Math.Max(0f, x), Math.Max(0f, y)), ImGuiCond.Always);
        ImGui.SetWindowSize(new NVector2(_worldgenPoppedViewportWidth, _worldgenPoppedViewportHeight), ImGuiCond.Always);
    }

    private void DrawWorldgenPreviewViewport()
    {
        EnsureWorldgenPreviewDefaults();
        DrawWorldgenPreviewToolbar();
        ProcessWorldgenRealtimePeek();

        float availableHeight = ImGui.GetContentRegionAvail().Y;
        float height = Math.Max(240f * Math.Max(0.75f, _devToolsUiScale), availableHeight);
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
        string previewError = "";
        try
        {
            if (!serverRequired || serverAvailable)
            {
                if (_worldgenPreviewMode == WorldgenPreviewModeRegion3D)
                {
                    DrawWorldgenLandformSurfacePreview(drawList, min, max, seed, centerX, centerZ, pixelsPerBlock);
                    if (_worldgenViewportScreenshotRequested)
                    {
                        _worldgenViewportScreenshotRequested = false;
                        _worldgenStatus = "3D worldgen preview is drawn as ImGui geometry; switch to a 2D preview mode for PNG capture.";
                    }
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
        }
        catch (Exception exception)
        {
            previewError = $"Worldgen preview failed: {exception.Message}";
            _worldgenStatus = previewError;
            _worldgenDiagnostics.Exception("Worldgen preview failed", exception);
            DrawWorldgenPreviewUnavailable(drawList, min, max, previewError, "Fix the selected draft or switch preview mode.");
        }
        if (_worldgenViewportScreenshotRequested)
        {
            _worldgenViewportScreenshotRequested = false;
            _worldgenStatus = "Worldgen screenshot failed: no preview texture was rendered this frame.";
        }
        drawList.PopClipRect();

        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 1f));
        drawList.AddRect(min, max, border, 4f);

        NVector2 mouse = ImGui.GetIO().MousePos;
        int hoverX = (int)MathF.Floor(centerX + (mouse.X - (min.X + actual.X * 0.5f)) / pixelsPerBlock);
        int hoverZ = (int)MathF.Floor(centerZ + (mouse.Y - (min.Y + actual.Y * 0.5f)) / pixelsPerBlock);
        string modeLabel = WorldgenPreviewModeLabels[Math.Clamp(_worldgenPreviewMode, 0, WorldgenPreviewModeLabels.Length - 1)];
        string hoverDetails = BuildWorldgenPreviewHoverText(_worldgenPreviewMode, seed, hoverX, hoverZ);
        string modeStatus = WorldgenPreviewModeUsesMapLayer(_worldgenPreviewMode)
            ? _worldgenPreviewServerApi != null
                ? $"{modeLabel}: live server {GetWorldgenPreviewMapLayerFieldName(_worldgenPreviewMode)}"
                : $"{modeLabel}: procedural fallback; live server layer unavailable"
            : _worldgenPreviewMode == WorldgenPreviewModeClimate
                ? $"{modeLabel}: live server climateGen"
                : _worldgenPreviewMode == WorldgenPreviewModeBlockPatch
                    ? $"{modeLabel}: selected draft row"
                    : _worldgenPreviewMode == WorldgenPreviewModeTerrainShape
                        ? $"{modeLabel}: selected draft landform"
                        : _worldgenPreviewMode == WorldgenPreviewModeRockStrata
                            ? $"{modeLabel}: selected draft stratum"
                            : _worldgenPreviewMode == WorldgenPreviewModeRegion3D
                            ? _worldgenPreviewPeekProfile == null
                                ? $"{modeLabel}: draft landform surface"
                                : $"{modeLabel}: real peeked region"
                : $"{modeLabel}: viewport host";
        string serverLine = serverRequired
            ? _worldgenPreviewServerStatus
            : WorldgenPreviewModeUsesMapLayer(_worldgenPreviewMode)
                ? _worldgenPreviewServerStatus
                : "Singleplayer server API: not required for this mode.";
        DrawWorldgenPreviewOverlay(drawList, min, max, actual.X, modeStatus, hoverX, hoverZ, hoverDetails, serverLine, previewError);

        ImGui.EndChild();
    }

    private void DrawWorldgenPreviewToolbar()
    {
        ImGui.TextUnformatted("Worldgen preview");
        ImGui.SameLine();
        if (ImGui.Checkbox("Auto##worldgen-preview-auto-mode", ref _worldgenPreviewAutoMode))
        {
            if (_worldgenPreviewAutoMode)
            {
                RebuildVisibleWorldgenEntries();
                ApplyWorldgenPreviewModeForSelectedEntry();
            }
            else
            {
                EnsureWorldgenSelectionMatchesPreviewMode();
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(SanitizeWorldgenPreviewText(GetWorldgenPreviewAutoModeStatus(), 240));
        }

        ImGui.SameLine();
        ImGui.TextDisabled(SanitizeWorldgenPreviewText(GetWorldgenPreviewAutoModeStatus(), 96));

        float availableWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        int previewModeBefore = _worldgenPreviewMode;
        ImGui.SetNextItemWidth(Math.Clamp(availableWidth * 0.24f, 180f, 320f));
        if (ImGui.Combo("Mode##worldgen-preview-mode", ref _worldgenPreviewMode, WorldgenPreviewModeLabels, WorldgenPreviewModeLabels.Length))
        {
            _worldgenPreviewAutoMode = false;
            if (_worldgenPreviewMode != previewModeBefore)
            {
                if (previewModeBefore == WorldgenPreviewModeRegion3D && _worldgenPreviewMode != WorldgenPreviewModeRegion3D)
                {
                    RestoreActiveWorldgenPeek("preview mode changed");
                }
                _worldgenPreviewMapLayer = null;
                InvalidateWorldgenPreviewRasterCache();
                ScheduleWorldgenRealtimePeek("preview mode changed");
                EnsureWorldgenSelectionMatchesPreviewMode();
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Clamp(availableWidth * 0.16f, 130f, 230f));
        _worldgenPreviewSeedText ??= "";
        ImGui.InputText("Seed##worldgen-preview-seed", ref _worldgenPreviewSeedText, 64);

        ImGui.SameLine();
        if (ImGui.Button("Use world##worldgen-preview-current"))
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
            ResetWorldgenPreviewView();
        }
        ImGui.SameLine();
        if (ImGui.Button("Screenshot##worldgen-preview-screenshot"))
        {
            _worldgenViewportScreenshotRequested = true;
            _worldgenStatus = "Worldgen preview screenshot queued.";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Save the current 2D worldgen preview texture as a PNG.");
        }
        ImGui.SameLine();
        bool poppedOut = _worldgenPreviewPoppedOut;
        if (ImGui.Checkbox("Pop out##worldgen-preview-popout", ref poppedOut))
        {
            _worldgenPreviewPoppedOut = poppedOut;
        }

        int previousOriginX = _worldgenPreviewOriginX;
        int previousOriginZ = _worldgenPreviewOriginZ;
        ImGui.SetNextItemWidth(Math.Clamp(availableWidth * 0.18f, 120f, 260f));
        ImGui.InputInt("Origin X##worldgen-preview-origin-x", ref _worldgenPreviewOriginX);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Clamp(availableWidth * 0.18f, 120f, 260f));
        ImGui.InputInt("Z##worldgen-preview-origin-z", ref _worldgenPreviewOriginZ);
        if (_worldgenPreviewOriginX != previousOriginX || _worldgenPreviewOriginZ != previousOriginZ)
        {
            ClearWorldgenPeekProfile("Preview origin changed; peek again to refresh the real 3D preview.");
            ScheduleWorldgenRealtimePeek("preview origin changed");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(96f);
        if (ImGui.InputInt("Resolution##worldgen-preview-resolution", ref _worldgenPreviewResolution))
        {
            _worldgenPreviewResolution = Math.Clamp(_worldgenPreviewResolution, 32, 192);
            InvalidateWorldgenPreviewRasterCache();
        }

        string summary = BuildWorldgenPreviewDetailsSummary();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(SanitizeWorldgenPreviewText(summary, 110));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(SanitizeWorldgenPreviewText(summary, 300));
            }
        }

        if (ImGui.CollapsingHeader("Preview details##worldgen-preview-details"))
        {
            DrawWorldgenPreviewModeDetails();
        }
    }

    private void ResetWorldgenPreviewView()
    {
        _worldgenPreviewPanX = 0f;
        _worldgenPreviewPanZ = 0f;
        _worldgenPreviewZoom = 1f;
        _worldgenPreview3DYaw = MathF.PI * 0.25f;
        _worldgenPreview3DPitch = 0.70f;
    }

    private void DrawWorldgenPreviewModeDetails()
    {
        DrawWorldgenPreviewParityPanel();

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
        else if (_worldgenPreviewMode == WorldgenPreviewModeRockStrata)
        {
            DrawWorldgenRockStrataPreviewControls();
        }
        else if (_worldgenPreviewMode == WorldgenPreviewModeRegion3D)
        {
            DrawWorldgenRegion3DPreviewControls();
        }
        else if (WorldgenPreviewModeUsesMapLayer(_worldgenPreviewMode))
        {
            DrawWorldgenMapLayerPreviewControls();
        }
    }

    private string BuildWorldgenPreviewDetailsSummary()
    {
        WorldgenPreviewParityInfo parity = BuildWorldgenPreviewParityInfo(_worldgenPreviewMode);
        return _worldgenPreviewMode switch
        {
            WorldgenPreviewModeClimate => $"{parity.State}: live climate map from the active world config.",
            WorldgenPreviewModeOre => TryGetWorldgenPreviewDepositVariant(out _, out string? code, out string source, out string status)
                ? $"{parity.State}: ore preview uses {source} {code ?? "unnamed"}."
                : status,
            WorldgenPreviewModeBlockPatch => TryGetSelectedWorldgenBlockPatchRow(out JObject? blockPatchRow) && blockPatchRow != null
                ? $"{parity.State}: block patch {GetWorldgenRowLabel(WorldgenAssetKind.BlockPatches, blockPatchRow, _worldgenRowIndex)}."
                : "Select a block patch row to preview suitability.",
            WorldgenPreviewModeTerrainShape => TryGetSelectedWorldgenLandformRow(out JObject? landformRow) && landformRow != null
                ? $"{parity.State}: terrain shape {GetWorldgenRowLabel(WorldgenAssetKind.Landforms, landformRow, _worldgenRowIndex)}."
                : "Select a landform row to preview terrain shape.",
            WorldgenPreviewModeRegion3D => _worldgenPreviewPeekProfile == null
                ? $"{parity.State}: draft landform surface."
                : $"{parity.State}: real peek, pass {_worldgenPreviewPeekProfile.PassLabel}.",
            WorldgenPreviewModeRockStrata => TryGetSelectedWorldgenRockStrataRow(out JObject? stratumRow) && stratumRow != null
                ? $"{parity.State}: rock strata {GetWorldgenRowLabel(WorldgenAssetKind.RockStrata, stratumRow, _worldgenRowIndex)}."
                : "Select a rock-strata row to preview thickness.",
            _ when WorldgenPreviewModeUsesMapLayer(_worldgenPreviewMode) => $"{parity.State}: live GenMaps layer when an integrated server is available.",
            _ => ""
        } ?? "";
    }

    private void DrawWorldgenPreviewParityPanel()
    {
        WorldgenPreviewParityInfo parity = BuildWorldgenPreviewParityInfo(_worldgenPreviewMode);
        ImGui.SeparatorText("Preview parity");
        ImGui.TextDisabled($"{parity.State}: {parity.Detail}");
        if (!string.IsNullOrWhiteSpace(parity.Action))
        {
            ImGui.TextWrapped(parity.Action);
        }
    }

    private WorldgenPreviewParityInfo BuildWorldgenPreviewParityInfo(int mode)
    {
        if (WorldgenPreviewModeUsesMapLayer(mode))
        {
            return GetWorldgenPreviewMapLayer(mode) != null
                ? new("Exact", $"Sampling live GenMaps.{GetWorldgenPreviewMapLayerFieldName(mode)} from the active server world.", "")
                : new("Approximate", "No live GenMaps layer is available, so the preview uses the deterministic draft color sampler.", "Start or join a singleplayer world for exact engine map-layer parity.");
        }

        return mode switch
        {
            WorldgenPreviewModeOre => GetWorldgenPreviewGenDeposits() != null
                ? new("Engine-exact ore map", "Sampling the initialized GenDeposits ore map for the selected deposit; propick text becomes exact when a 3D peek covers the cursor.", "Use 3D region peek on the same area for exact block-column propick parity.")
                : new("Draft-derived", "GenDeposits is unavailable; only draft row data can be sampled.", "Start a singleplayer world to initialize the engine ore sampler."),
            WorldgenPreviewModeBlockPatch => _worldgenPreviewPeekProfile != null
                ? new("Peek-exact", $"Comparing against the last real generated region through pass {_worldgenPreviewPeekProfile.PassLabel}.", "")
                : new("Approximate", "2D suitability uses live climate maps plus draft constraints; terrain/surface/category collision checks need generated terrain.", "Press Peek region for exact generated-world parity around the cursor."),
            WorldgenPreviewModeTerrainShape => _worldgenPreviewServerApi?.ModLoader.GetModSystem<GenTerra>() != null
                ? new("Engine-exact terrain noise", "Sampling GenTerra ColumnNoise with the selected draft landform.", "")
                : new("Draft-derived", "GenTerra is unavailable; the preview falls back to the landform draft arrays.", "Start a singleplayer world to initialize GenTerra for exact terrain shape sampling."),
            WorldgenPreviewModeRegion3D => _worldgenPreviewPeekProfile != null
                ? new("Peek-exact", $"Rendering generated terrain returned by PeekChunkColumn through {_worldgenPreviewPeekProfile.PassLabel}.", "")
                : new("Draft-derived", "Rendering the selected landform height field before generated terrain has been peeked.", "Press Peek region for exact generated terrain parity."),
            WorldgenPreviewModeRockStrata => _worldgenPreviewPeekProfile != null
                ? new("Peek-exact", "Using the generated block columns from the last real 3D peek.", "")
                : new("Draft-exact thickness", "Sampling the selected stratum's thickness map; final province stack placement still needs a generated column.", "Press Peek region for exact block-column placement parity."),
            _ => new("Diagnostic", "Synthetic gradient/noise preview; no engine generation pass is associated with this mode.", "")
        };
    }

    private void DrawWorldgenPreviewOverlay(
        ImDrawListPtr drawList,
        NVector2 min,
        NVector2 max,
        float viewportWidth,
        string? modeStatus,
        int hoverX,
        int hoverZ,
        string? hoverDetails,
        string? serverLine,
        string? previewError)
    {
        List<string> lines = [];
        AddOverlayLine(modeStatus);
        AddOverlayLine(BuildWorldgenPreviewParityOverlayLine());
        AddOverlayLine($"X {hoverX}, Z {hoverZ}");
        AddOverlayLine(hoverDetails);
        if (!string.IsNullOrWhiteSpace(previewError))
        {
            AddOverlayLine(previewError);
        }
        else if (WorldgenPreviewModeRequiresServer(_worldgenPreviewMode) || WorldgenPreviewModeUsesMapLayer(_worldgenPreviewMode))
        {
            AddOverlayLine(serverLine);
        }
        AddOverlayLine(_worldgenPreviewRasterStatus);
        if (_worldgenPreviewMode == WorldgenPreviewModeRegion3D)
        {
            AddOverlayLine(_worldgenPreviewPeekStatus);
            if (_worldgenPreviewOracleProfile != null || !string.Equals(_worldgenPreviewOracleStatus, "No loaded-world comparison yet.", StringComparison.Ordinal))
            {
                AddOverlayLine(_worldgenPreviewOracleStatus);
            }
        }

        if (lines.Count == 0) return;

        int maxChars = Math.Clamp((int)(viewportWidth / 8f), 48, 160);
        List<string> sanitized = lines.Select(line => SanitizeWorldgenPreviewText(line, maxChars)).Where(line => line.Length > 0).Take(5).ToList();
        if (sanitized.Count == 0) return;

        float lineHeight = ImGui.GetTextLineHeight() + 2f;
        float panelWidth = Math.Min(max.X - min.X - 24f, Math.Max(260f, sanitized.Max(line => ImGui.CalcTextSize(line).X) + 18f));
        float panelHeight = sanitized.Count * lineHeight + 14f;
        NVector2 panelMin = new(min.X + 10f, min.Y + 10f);
        NVector2 panelMax = new(panelMin.X + panelWidth, panelMin.Y + panelHeight);
        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0f, 0f, 0f, 0.64f));
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 0.65f));
        uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.88f, 0.84f, 0.74f, 1f));
        uint error = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.34f, 0.25f, 1f));
        drawList.AddRectFilled(panelMin, panelMax, background, 4f);
        drawList.AddRect(panelMin, panelMax, border, 4f);

        for (int index = 0; index < sanitized.Count; index++)
        {
            bool errorLine = !string.IsNullOrWhiteSpace(previewError) && sanitized[index].Contains("failed", StringComparison.OrdinalIgnoreCase);
            AddWorldgenPreviewText(drawList, new NVector2(panelMin.X + 9f, panelMin.Y + 7f + index * lineHeight), errorLine ? error : text, sanitized[index]);
        }

        void AddOverlayLine(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add(value);
            }
        }
    }

    private string BuildWorldgenPreviewParityOverlayLine()
    {
        WorldgenPreviewParityInfo parity = BuildWorldgenPreviewParityInfo(_worldgenPreviewMode);
        return $"Parity: {parity.State}";
    }

    private static void AddWorldgenPreviewText(ImDrawListPtr drawList, NVector2 position, uint color, string? text)
    {
        string safe = SanitizeWorldgenPreviewText(text, 240);
        if (safe.Length == 0) return;
        drawList.AddText(position, color, safe);
    }

    private static string SanitizeWorldgenPreviewText(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string safe = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        while (safe.Contains("  ", StringComparison.Ordinal))
        {
            safe = safe.Replace("  ", " ", StringComparison.Ordinal);
        }

        safe = safe.Trim();
        if (safe.Length <= maxChars) return safe;
        return maxChars <= 3 ? safe[..maxChars] : safe[..(maxChars - 3)] + "...";
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
            ImGui.TextDisabled("Ore map preview generates the selected deposit's region ore map in memory; exact propick appears when a real 3D peek covers the cursor.");
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
            ImGui.TextDisabled("2D suitability is approximate: live climate/forest/shrub maps plus draft temp/rain/forest/shrub constraints.");
            ImGui.TextDisabled("Exact engine checks still require generated terrain Y, surface/liquid block, substrate, map density, and category collisions.");
            ImGui.TextDisabled("Chance affects expected attempt density, not the suitability mask.");
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
            ImGui.TextDisabled("Uses live GenTerra ColumnNoise when a singleplayer server is available; otherwise falls back to the draft-only approximation.");
        }
        else
        {
            ImGui.TextDisabled("Select a landform row to preview its draft terrain shape.");
        }
    }

    private void DrawWorldgenRockStrataPreviewControls()
    {
        if (TryGetSelectedWorldgenRockStrataRow(out JObject? row) && row != null)
        {
            WorldgenRockStrataDraft draft = WorldgenRockStrataDraft.FromJson(row);
            string label = draft.BlockCode ?? GetWorldgenRowLabel(WorldgenAssetKind.RockStrata, row, _worldgenRowIndex);
            ImGui.TextDisabled($"Using selected draft rock stratum: {label}.");
            ImGui.TextDisabled("Uses the draft stratum's MapLayerCustomPerlin thickness map with engine scaling.");
            ImGui.TextDisabled("Final province stack order and exact block-column placement still require the real 3D peek path.");
        }
        else
        {
            ImGui.TextDisabled("Select a rock-strata row to preview its draft thickness map.");
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
                ScheduleWorldgenRealtimePeek("region size changed");
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
                ScheduleWorldgenRealtimePeek("worldgen pass changed");
            }
        }

        if (ImGui.Checkbox("Auto refresh real peek##worldgen-auto-peek", ref _worldgenPreviewAutoPeekOnEdit))
        {
            if (_worldgenPreviewAutoPeekOnEdit)
            {
                ScheduleWorldgenRealtimePeek("auto refresh enabled");
            }
            else
            {
                _worldgenPreviewPeekDirty = false;
                _worldgenPreviewAutoPeekStatus = "Auto 3D refresh disabled.";
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Debounces real PeekChunkColumn refreshes after edits. Landform rows are temporarily injected into the real 3D pipeline for the preview and restored afterward.");
        }
        ImGui.TextDisabled(_worldgenPreviewAutoPeekStatus);

        bool canPeek = _worldgenPreviewServerApi != null && !_worldgenPreviewPeekPending;
        if (!canPeek) ImGui.BeginDisabled();
        if (ImGui.Button("Peek region##worldgen-peek-region"))
        {
            RequestWorldgenPeekRegion(forceRefresh: false, reason: "manual");
        }
        if (!canPeek) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Clear peek##worldgen-clear-peek"))
        {
            ClearWorldgenPeekProfile("No real chunk peek requested yet.");
        }

        bool canCompare = _worldgenPreviewPeekProfile != null && _worldgenPreviewServerApi != null && !_worldgenPreviewPeekPending;
        if (!canCompare) ImGui.BeginDisabled();
        if (ImGui.Button("Compare loaded world##worldgen-compare-loaded-world"))
        {
            CompareWorldgenLoadedWorldOracle();
        }
        if (!canCompare) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Compares the current peek against already-loaded world columns at the same coordinates. Missing columns are skipped instead of loaded.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear compare##worldgen-clear-compare"))
        {
            ClearWorldgenLoadedWorldOracle("No loaded-world comparison yet.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Run safety audit##worldgen-save-integrity-audit"))
        {
            RunWorldgenSaveIntegrityAudit("manual audit");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Checks for active preview peeks, forces restore if needed, and verifies generation flags against the last captured peek state.");
        }

        ImGui.Checkbox("Show diff overlay##worldgen-show-oracle-diff", ref _worldgenPreviewShowOracleDiff);
        ImGui.TextDisabled(_worldgenPreviewOracleStatus);
        ImGui.TextDisabled(_worldgenPreviewSaveIntegrityStatus);

        ImGui.TextDisabled(_worldgenPreviewPeekStatus);
        if (_worldgenPreviewPeekProfile is { } profile)
        {
            ImGui.TextDisabled($"Last real region: chunks {profile.OriginChunkX},{profile.OriginChunkZ} size {profile.RegionSizeChunks}x{profile.RegionSizeChunks}; pass {profile.PassLabel}; height {profile.MinHeight}-{profile.MaxHeight}; avg {profile.AverageHeight:0.0}.");
            ImGui.TextDisabled($"Sample row: {profile.SampleSummary}");
            if (!string.IsNullOrWhiteSpace(profile.CleanupSummary))
            {
                ImGui.TextDisabled(profile.CleanupSummary);
            }
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

    private void EnsureWorldgenSelectionMatchesPreviewMode()
    {
        RebuildVisibleWorldgenEntries();

        WorldgenAssetEntry? entry = SelectedWorldgenEntry;
        if (entry == null)
        {
            _worldgenStatus = $"{WorldgenPreviewModeLabels[Math.Clamp(_worldgenPreviewMode, 0, WorldgenPreviewModeLabels.Length - 1)]} preview has no compatible asset in the current filters.";
            return;
        }

        if (!_worldgenLoadedKey.Equals(entry.Key, StringComparison.OrdinalIgnoreCase))
        {
            LoadWorldgenEntry(entry);
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
            WorldgenAssetKind.RockStrata => WorldgenPreviewModeRockStrata,
            _ => WorldgenPreviewModeGradient
        };
    }

    private bool IsWorldgenEntryCompatibleWithCurrentPreview(WorldgenAssetEntry entry, out string reason)
    {
        if (_worldgenPreviewAutoMode)
        {
            reason = "Compatible: auto mode switches the viewport to match this asset.";
            return true;
        }

        return IsWorldgenEntryCompatibleWithPreviewMode(entry.Kind, entry.IsContentClassified, _worldgenPreviewMode, out reason);
    }

    private string GetWorldgenPreviewAssetRestrictionStatus()
    {
        if (_worldgenPreviewAutoMode)
        {
            return "Asset filter: auto mode allows all assets.";
        }

        string modeLabel = WorldgenPreviewModeLabels[Math.Clamp(_worldgenPreviewMode, 0, WorldgenPreviewModeLabels.Length - 1)];
        return GetWorldgenPreviewCompatibleKindText(_worldgenPreviewMode, out string compatibleKinds)
            ? $"Asset filter: {modeLabel} uses {compatibleKinds}."
            : $"Asset filter: {modeLabel} does not require a selected asset kind.";
    }

    private static bool IsWorldgenEntryCompatibleWithPreviewMode(
        WorldgenAssetKind kind,
        bool isContentClassified,
        int previewMode,
        out string reason)
    {
        string modeLabel = WorldgenPreviewModeLabels[Math.Clamp(previewMode, 0, WorldgenPreviewModeLabels.Length - 1)];
        if (!GetWorldgenPreviewCompatibleKindText(previewMode, out string compatibleKinds))
        {
            reason = $"Compatible: {modeLabel} preview does not use the selected asset.";
            return true;
        }

        if (kind == WorldgenAssetKind.Other && !isContentClassified)
        {
            reason = $"Eligible: content classification is pending; selecting this asset will check compatibility with {modeLabel}.";
            return true;
        }

        bool compatible = previewMode switch
        {
            WorldgenPreviewModeOre => kind == WorldgenAssetKind.Deposits,
            WorldgenPreviewModeBlockPatch => kind == WorldgenAssetKind.BlockPatches,
            WorldgenPreviewModeTerrainShape => kind == WorldgenAssetKind.Landforms,
            WorldgenPreviewModeRockStrata => kind == WorldgenAssetKind.RockStrata,
            WorldgenPreviewModeRegion3D => kind is WorldgenAssetKind.Deposits or WorldgenAssetKind.BlockPatches or WorldgenAssetKind.Landforms,
            _ => true
        };

        reason = compatible
            ? $"Compatible: {modeLabel} preview uses {compatibleKinds}."
            : $"Not compatible: {modeLabel} preview only uses {compatibleKinds}.";
        return compatible;
    }

    private static bool GetWorldgenPreviewCompatibleKindText(int previewMode, out string compatibleKinds)
    {
        compatibleKinds = previewMode switch
        {
            WorldgenPreviewModeOre => "deposit assets",
            WorldgenPreviewModeBlockPatch => "block-patch assets",
            WorldgenPreviewModeTerrainShape => "landform assets",
            WorldgenPreviewModeRockStrata => "rock-strata assets",
            WorldgenPreviewModeRegion3D => "deposit, block-patch, or landform assets",
            _ => ""
        };

        return !string.IsNullOrWhiteSpace(compatibleKinds);
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

        ScheduleWorldgenRealtimePeek("current world state loaded");
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

    private static bool TrySetReflectedMember(object? instance, string memberName, object? value, out string error)
    {
        error = "";
        if (instance == null)
        {
            error = "instance is null";
            return false;
        }

        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        Type type = instance.GetType();
        try
        {
            System.Reflection.PropertyInfo? property = type.GetProperty(memberName, flags);
            if (property != null && property.GetIndexParameters().Length == 0 && property.CanWrite)
            {
                property.SetValue(instance, value);
                return true;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        try
        {
            System.Reflection.FieldInfo? field = type.GetField(memberName, flags);
            if (field != null)
            {
                field.SetValue(instance, value);
                return true;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        error = $"member {memberName} not found";
        return false;
    }

    private static object? TryGetReflectedStaticMember(Type type, string memberName)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        try
        {
            System.Reflection.PropertyInfo? property = type.GetProperty(memberName, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(null);
            }
        }
        catch
        {
            // Field fallback below.
        }

        try
        {
            return type.GetField(memberName, flags)?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static bool TrySetReflectedStaticMember(Type type, string memberName, object? value, out string error)
    {
        error = "";
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        try
        {
            System.Reflection.PropertyInfo? property = type.GetProperty(memberName, flags);
            if (property != null && property.GetIndexParameters().Length == 0 && property.CanWrite)
            {
                property.SetValue(null, value);
                return true;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        try
        {
            System.Reflection.FieldInfo? field = type.GetField(memberName, flags);
            if (field != null)
            {
                field.SetValue(null, value);
                return true;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        error = $"static member {memberName} not found";
        return false;
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

    private int GetWorldgenPreviewMapHeight()
    {
        if (_worldgenPreviewServerApi?.WorldManager.MapSizeY > 0)
        {
            return _worldgenPreviewServerApi.WorldManager.MapSizeY;
        }

        object? raw = TryGetReflectedProperty(_api.World, "MapSizeY");
        if (raw != null && int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int height) && height > 0)
        {
            return height;
        }

        return 256;
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

        if (mode == WorldgenPreviewModeRockStrata)
        {
            return BuildWorldgenRockStrataHoverText(seed, blockX, blockZ);
        }

        if (WorldgenPreviewModeUsesMapLayer(mode))
        {
            MapLayerBase? layer = GetWorldgenPreviewMapLayer(mode);
            if (layer == null)
            {
                string fallbackStatus = string.IsNullOrWhiteSpace(_worldgenPreviewMapLayerStatus)
                    ? "Live map layer unavailable."
                    : _worldgenPreviewMapLayerStatus;
                return $"{fallbackStatus} Showing procedural fallback.";
            }

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
            Dictionary<WorldgenOreMapRegionCacheKey, IntDataMap2D> oreMapCache = [];
            bool sampledOreMap = TrySampleWorldgenPreviewOreMapValue(variant!, chunkX, chunkZ, oreMapCache, out int oreMapValue, out string samplerStatus);
            float factor = sampledOreMap
                ? (oreMapValue & 0xff) / 255f
                : variant!.GetOreMapFactor(chunkX, chunkZ);
            string exactStatus = sampledOreMap && TryGetWorldgenExactPropickHoverText(variant!, code, blockX, blockZ, oreMapValue, out string exactText)
                ? exactText
                : "Exact propick unavailable; run a real 3D peek covering this cursor.";
            return $"Ore: {code ?? "unnamed"} factor {factor.ToString("0.###", CultureInfo.InvariantCulture)} at chunk {chunkX}, {chunkZ} ({source}); {samplerStatus}. {exactStatus}";
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen ore hover sample failed", exception);
            return $"Ore sample failed: {exception.Message}";
        }
    }

    private bool TryGetWorldgenExactPropickHoverText(DepositVariant variant, string? code, int blockX, int blockZ, int oreMapValue, out string text)
    {
        text = "";
        if (_worldgenPreviewPeekProfile is not { } profile)
        {
            return false;
        }

        if (!TryGetWorldgenPeekLocalColumn(profile, blockX, blockZ, out int localX, out int localZ))
        {
            return false;
        }

        int height = GetWorldgenPeekHeight(profile, localX, localZ);
        if (height <= 0)
        {
            text = "Exact propick: peeked column has no solid terrain.";
            return true;
        }

        if (!TryGetWorldgenPeekBlockColumn(profile, localX, localZ, height, out int[] blockColumn))
        {
            return false;
        }

        try
        {
            variant.GetPropickReading(new BlockPos(blockX, height, blockZ), oreMapValue, blockColumn, out double ppt, out double totalFactor);
            text = $"Exact propick from latest {profile.PassLabel} peek: {code ?? "unnamed"} {ppt:0.###} ppt, total {totalFactor:0.###}, column height {blockColumn.Length}, ore map value {oreMapValue & 0xff}.";
            return true;
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen exact propick hover failed", exception);
            text = $"Exact propick failed against latest peek: {exception.Message}";
            return true;
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
            int? shrubValue = genMaps.bushGen?.GenLayer(blockX, blockZ, 1, 1)[0];
            WorldgenClimateSample sample = DecodeWorldgenClimateSample(climateValue, forestValue, shrubValue);
            WorldgenBlockPatchDraft draft = WorldgenBlockPatchDraft.FromJson(row);
            bool suitable = draft.IsSuitable(sample);
            string label = GetWorldgenRowLabel(WorldgenAssetKind.BlockPatches, row, _worldgenRowIndex);
            string shrub = sample.HasShrub ? $", shrub {sample.Shrub:0.###}" : "";
            return $"Block patch {label}: {(suitable ? "approx suitable" : "approx rejected")}; temp {sample.TemperatureCelsius:0.#}C, rain {sample.Rain:0.###}, forest {sample.Forest:0.###}{shrub}, chance {draft.Chance:0.###}. Unchecked: terrain Y, surface/liquid block, substrate, map density, category collisions.";
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

    private string BuildWorldgenRockStrataHoverText(long seed, int blockX, int blockZ)
    {
        if (!TryGetSelectedWorldgenRockStrataRow(out JObject? row) || row == null)
        {
            return "Rock strata: no selected draft row.";
        }

        try
        {
            WorldgenRockStrataDraft draft = WorldgenRockStrataDraft.FromJson(row);
            if (!draft.IsUsable)
            {
                return "Rock strata: selected row needs matching amplitudes, frequencies, and thresholds.";
            }

            WorldgenRockStrataSampler sampler = CreateWorldgenRockStrataSampler(seed, draft, out string samplerStatus);
            float thickness = sampler.SampleThickness(blockX, blockZ);
            string label = draft.BlockCode ?? GetWorldgenRowLabel(WorldgenAssetKind.RockStrata, row, _worldgenRowIndex);
            return $"Rock strata {label}: draft thickness {thickness:0.#} blocks; {draft.RockGroup ?? "unknown group"}; {draft.GenDir ?? "default direction"}; {samplerStatus}";
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen rock strata hover sample failed", exception);
            return $"Rock strata sample failed: {exception.Message}";
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
            JObject draftRow = (JObject)row.DeepClone();
            SanitizeWorldgenToken(draftRow);
            DepositVariant? draft = draftRow.ToObject<DepositVariant>();
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

    private bool TryGetSelectedWorldgenRockStrataRow(out JObject? row)
    {
        return TryGetSelectedWorldgenRow(WorldgenAssetKind.RockStrata, out row);
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

    private WorldgenPeekDraftScope CreateWorldgenPeekDraftScope(ICoreServerAPI serverApi, out string status)
    {
        status = "live engine config";
        if (_worldgenPreviewMode != WorldgenPreviewModeRegion3D)
        {
            return WorldgenPeekDraftScope.Empty;
        }

        WorldgenAssetEntry? entry = SelectedWorldgenEntry;
        try
        {
            if (entry?.Kind == WorldgenAssetKind.Landforms)
            {
                if (!TryGetSelectedWorldgenLandformRow(out JObject? landformRow) || landformRow == null)
                {
                    status = "live engine config; no selected landform draft row";
                    return WorldgenPeekDraftScope.Empty;
                }

                GenTerra? genTerra = serverApi.ModLoader.GetModSystem<GenTerra>();
                if (genTerra == null)
                {
                    status = "live engine config; GenTerra unavailable";
                    return WorldgenPeekDraftScope.Empty;
                }

                if (TryCreateWorldgenLandformDraftScope(genTerra, landformRow, _worldgenRowIndex, out WorldgenPeekDraftScope scope, out status))
                {
                    return scope;
                }

                return WorldgenPeekDraftScope.Empty;
            }

            if (entry?.Kind == WorldgenAssetKind.Deposits)
            {
                if (!TryGetSelectedWorldgenDepositRow(out JObject? depositRow) || depositRow == null)
                {
                    status = "live engine config; no selected deposit draft row";
                    return WorldgenPeekDraftScope.Empty;
                }

                GenDeposits? genDeposits = serverApi.ModLoader.GetModSystem<GenDeposits>();
                if (genDeposits == null)
                {
                    status = "live engine config; GenDeposits unavailable";
                    return WorldgenPeekDraftScope.Empty;
                }

                if (TryCreateWorldgenDepositDraftScope(genDeposits, depositRow, out WorldgenPeekDraftScope scope, out status))
                {
                    return scope;
                }

                return WorldgenPeekDraftScope.Empty;
            }

            if (entry?.Kind == WorldgenAssetKind.BlockPatches)
            {
                if (!TryGetSelectedWorldgenBlockPatchRow(out JObject? blockPatchRow) || blockPatchRow == null)
                {
                    status = "live engine config; no selected block-patch draft row";
                    return WorldgenPeekDraftScope.Empty;
                }

                GenVegetationAndPatches? genVegetation = serverApi.ModLoader.GetModSystem<GenVegetationAndPatches>();
                if (genVegetation == null)
                {
                    status = "live engine config; GenVegetationAndPatches unavailable";
                    return WorldgenPeekDraftScope.Empty;
                }

                if (TryCreateWorldgenBlockPatchDraftScope(serverApi, genVegetation, blockPatchRow, _worldgenRowIndex, out WorldgenPeekDraftScope scope, out status))
                {
                    return scope;
                }

                return WorldgenPeekDraftScope.Empty;
            }

            status = "live engine config; draft 3D mutation is currently available for landform, deposit, and block-patch rows";
            return WorldgenPeekDraftScope.Empty;
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen draft 3D scope failed", exception);
            status = $"live engine config; draft scope failed: {exception.Message}";
            return WorldgenPeekDraftScope.Empty;
        }
    }

    private bool TryCreateWorldgenDepositDraftScope(GenDeposits genDeposits, JObject draftRow, out WorldgenPeekDraftScope scope, out string status)
    {
        scope = WorldgenPeekDraftScope.Empty;
        status = "";

        DepositVariant[]? originalDeposits = genDeposits.Deposits;
        if (originalDeposits == null || originalDeposits.Length == 0)
        {
            status = "live engine config; GenDeposits has no deposit variants";
            return false;
        }

        if (!TryBuildWorldgenPreviewDraftDepositVariant(genDeposits, out DepositVariant? draftVariant, out string? draftCode, out string draftStatus) || draftVariant == null)
        {
            status = $"live engine config; deposit draft unavailable: {draftStatus}";
            return false;
        }

        if (!TryCloneWorldgenDepositVariants(originalDeposits, out DepositVariant[] draftDeposits))
        {
            status = "live engine config; could not clone GenDeposits variants safely";
            return false;
        }

        string? selectedCode = draftCode ?? draftRow["code"]?.ToString();
        if (!ReplaceWorldgenDepositByCode(draftDeposits, draftVariant, selectedCode))
        {
            status = string.IsNullOrWhiteSpace(selectedCode)
                ? "live engine config; selected deposit draft has no code to match in GenDeposits"
                : $"live engine config; deposit draft '{selectedCode}' was not found in GenDeposits";
            return false;
        }

        object? originalSubDeposits = TryGetReflectedMember(genDeposits, "subDepositsToPlace");
        object? previewSubDeposits = TryCreateEmptyCollectionLike(originalSubDeposits);

        genDeposits.Deposits = draftDeposits;
        bool subDepositsChanged = previewSubDeposits != null && TrySetReflectedMember(genDeposits, "subDepositsToPlace", previewSubDeposits, out _);

        string code = selectedCode ?? GetWorldgenPreviewDepositCode(draftVariant) ?? "unnamed";
        scope = new WorldgenPeekDraftScope(
            applied: true,
            status: $"deposit draft applied to real 3D peek: {code}",
            restore: () =>
            {
                genDeposits.Deposits = originalDeposits;
                if (subDepositsChanged)
                {
                    TrySetReflectedMember(genDeposits, "subDepositsToPlace", originalSubDeposits, out _);
                }
            });
        status = scope.Status;
        return true;
    }

    private static bool TryCloneWorldgenDepositVariants(DepositVariant[] source, out DepositVariant[] clones)
    {
        clones = new DepositVariant[source.Length];
        for (int index = 0; index < source.Length; index++)
        {
            try
            {
                clones[index] = source[index].Clone();
            }
            catch
            {
                clones = [];
                return false;
            }
        }

        return true;
    }

    private static bool ReplaceWorldgenDepositByCode(DepositVariant[] deposits, DepositVariant replacement, string? selectedCode)
    {
        if (string.IsNullOrWhiteSpace(selectedCode)) return false;

        for (int index = 0; index < deposits.Length; index++)
        {
            DepositVariant deposit = deposits[index];
            if (string.Equals(GetWorldgenPreviewDepositCode(deposit), selectedCode, StringComparison.OrdinalIgnoreCase))
            {
                deposits[index] = replacement;
                return true;
            }

            if (deposit.ChildDeposits != null && ReplaceWorldgenDepositByCode(deposit.ChildDeposits, replacement, selectedCode))
            {
                return true;
            }
        }

        return false;
    }

    private static object? TryCreateEmptyCollectionLike(object? collection)
    {
        if (collection == null) return null;

        try
        {
            return Activator.CreateInstance(collection.GetType());
        }
        catch
        {
            return null;
        }
    }

    private bool TryCreateWorldgenBlockPatchDraftScope(
        ICoreServerAPI serverApi,
        GenVegetationAndPatches genVegetation,
        JObject draftRow,
        int selectedRowIndex,
        out WorldgenPeekDraftScope scope,
        out string status)
    {
        scope = WorldgenPeekDraftScope.Empty;
        status = "";

        BlockPatchConfig? originalConfig = genVegetation.bpc;
        if (originalConfig?.Patches == null || originalConfig.Patches.Length == 0)
        {
            status = "live engine config; GenVegetationAndPatches has no block patches";
            return false;
        }

        int replaceIndex = FindWorldgenBlockPatchIndex(originalConfig.Patches, draftRow, selectedRowIndex);
        if (replaceIndex < 0 || replaceIndex >= originalConfig.Patches.Length)
        {
            status = "live engine config; selected block-patch draft was not found in GenVegetationAndPatches";
            return false;
        }

        int originalPatchesHashCode = BlockPatch.PatchesHashCode;
        BlockPatch originalPatch = originalConfig.Patches[replaceIndex];
        BlockPatch draftPatch = CloneWorldgenBlockPatch(originalPatch);
        ApplyWorldgenBlockPatchDraftRow(draftPatch, draftRow);
        TryInitializeWorldgenBlockPatch(serverApi, genVegetation, draftPatch, replaceIndex);

        BlockPatchConfig draftConfig = CloneWorldgenBlockPatchConfig(originalConfig);
        draftConfig.Patches[replaceIndex] = draftPatch;
        ReplaceWorldgenBlockPatchBySignature(draftConfig.PatchesNonTree, originalPatch, draftPatch);

        object? originalMapGens = genVegetation.blockPatchMapGens;
        object? previewMapGens = TryCreateEmptyCollectionLike(originalMapGens);

        genVegetation.bpc = draftConfig;
        bool mapGensChanged = previewMapGens != null && TrySetReflectedMember(genVegetation, "blockPatchMapGens", previewMapGens, out _);

        string label = GetWorldgenBlockPatchLabel(draftPatch, draftRow, selectedRowIndex);
        scope = new WorldgenPeekDraftScope(
            applied: true,
            status: $"block-patch draft applied to real 3D peek: {label}",
            restore: () =>
            {
                genVegetation.bpc = originalConfig;
                BlockPatch.PatchesHashCode = originalPatchesHashCode;
                if (mapGensChanged)
                {
                    TrySetReflectedMember(genVegetation, "blockPatchMapGens", originalMapGens, out _);
                }
            });
        status = scope.Status;
        return true;
    }

    private static BlockPatchConfig CloneWorldgenBlockPatchConfig(BlockPatchConfig source)
    {
        return new BlockPatchConfig
        {
            ChanceMultiplier = source.ChanceMultiplier?.Clone(),
            Patches = source.Patches?.Select(CloneWorldgenBlockPatch).ToArray() ?? [],
            PatchesNonTree = source.PatchesNonTree?.Select(CloneWorldgenBlockPatch).ToArray() ?? []
        };
    }

    private static BlockPatch CloneWorldgenBlockPatch(BlockPatch source)
    {
        return new BlockPatch
        {
            Attributes = source.Attributes,
            Biome = source.Biome,
            Biomes = source.Biomes?.ToArray(),
            BlockCodeIndex = source.BlockCodeIndex?.Clone(),
            blockCodes = source.blockCodes?.ToArray(),
            Blocks = source.Blocks?.ToArray(),
            BlocksByRockType = source.BlocksByRockType?.ToDictionary(pair => pair.Key, pair => pair.Value?.ToArray() ?? []),
            CategoryHashCode = source.CategoryHashCode,
            Chance = source.Chance,
            MapCode = source.MapCode,
            MaxFertility = source.MaxFertility,
            MaxForest = source.MaxForest,
            MaxHeightDifferential = source.MaxHeightDifferential,
            MaxRain = source.MaxRain,
            MaxShrub = source.MaxShrub,
            MaxTemp = source.MaxTemp,
            MaxWaterDepth = source.MaxWaterDepth,
            MaxWaterDepthP = source.MaxWaterDepthP,
            MaxY = source.MaxY,
            MinFertility = source.MinFertility,
            MinForest = source.MinForest,
            MinRain = source.MinRain,
            MinShrub = source.MinShrub,
            MinTemp = source.MinTemp,
            MinWaterDepth = source.MinWaterDepth,
            MinWaterDepthP = source.MinWaterDepthP,
            MinY = source.MinY,
            OffsetX = source.OffsetX?.Clone(),
            OffsetZ = source.OffsetZ?.Clone(),
            Placement = source.Placement,
            PostPass = source.PostPass,
            PrePass = source.PrePass,
            Quantity = source.Quantity?.Clone(),
            RandomMapCodePool = source.RandomMapCodePool?.ToArray(),
            TreeType = source.TreeType
        };
    }

    private static int FindWorldgenBlockPatchIndex(BlockPatch[] patches, JObject draftRow, int selectedRowIndex)
    {
        string? firstDraftCode = FirstArrayString(draftRow["blockCodes"] as JArray);
        if (!string.IsNullOrWhiteSpace(firstDraftCode))
        {
            for (int index = 0; index < patches.Length; index++)
            {
                string? firstPatchCode = patches[index].blockCodes?.FirstOrDefault()?.ToString();
                if (string.Equals(firstPatchCode, firstDraftCode, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }

        return selectedRowIndex >= 0 && selectedRowIndex < patches.Length ? selectedRowIndex : -1;
    }

    private static void ReplaceWorldgenBlockPatchBySignature(BlockPatch[]? patches, BlockPatch originalPatch, BlockPatch draftPatch)
    {
        if (patches == null || patches.Length == 0) return;

        string? originalCode = originalPatch.blockCodes?.FirstOrDefault()?.ToString();
        for (int index = 0; index < patches.Length; index++)
        {
            string? patchCode = patches[index].blockCodes?.FirstOrDefault()?.ToString();
            if (!string.IsNullOrWhiteSpace(originalCode) && string.Equals(patchCode, originalCode, StringComparison.OrdinalIgnoreCase))
            {
                patches[index] = draftPatch;
                return;
            }
        }
    }

    private static void ApplyWorldgenBlockPatchDraftRow(BlockPatch patch, JObject row)
    {
        if (row["blockCodes"] is JArray blockCodes)
        {
            patch.blockCodes = blockCodes
                .Select(token => token.ToString())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => new AssetLocation(code))
                .ToArray();
        }

        patch.Chance = ReadWorldgenFloat(row, "chance", patch.Chance);
        patch.Quantity = ReadWorldgenNatFloat(row, "quantity", patch.Quantity);
        patch.OffsetX = ReadWorldgenNatFloat(row, "offsetX", patch.OffsetX);
        patch.OffsetZ = ReadWorldgenNatFloat(row, "offsetZ", patch.OffsetZ);
        patch.MinTemp = ReadWorldgenInt(row, "minTemp", patch.MinTemp);
        patch.MaxTemp = ReadWorldgenInt(row, "maxTemp", patch.MaxTemp);
        patch.MinRain = ReadWorldgenFloat(row, "minRain", patch.MinRain);
        patch.MaxRain = ReadWorldgenFloat(row, "maxRain", patch.MaxRain);
        patch.MinForest = ReadWorldgenFloat(row, "minForest", patch.MinForest);
        patch.MaxForest = ReadWorldgenFloat(row, "maxForest", patch.MaxForest);
        patch.MinFertility = ReadWorldgenFloat(row, "minFertility", patch.MinFertility);
        patch.MaxFertility = ReadWorldgenFloat(row, "maxFertility", patch.MaxFertility);
        patch.MinY = ReadWorldgenFloat(row, "minY", patch.MinY);
        patch.MaxY = ReadWorldgenFloat(row, "maxY", patch.MaxY);
    }

    private static NatFloat? ReadWorldgenNatFloat(JObject row, string name, NatFloat? fallback)
    {
        if (!row.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out JToken? token) || token.Type == JTokenType.Null)
        {
            return fallback?.Clone();
        }

        try
        {
            return token.ToObject<NatFloat>() ?? fallback?.Clone();
        }
        catch
        {
            return fallback?.Clone();
        }
    }

    private static void TryInitializeWorldgenBlockPatch(ICoreServerAPI serverApi, GenVegetationAndPatches genVegetation, BlockPatch patch, int patchIndex)
    {
        RockStrataConfig? rockStrata = null;
        try
        {
            GenRockStrataNew? genRockStrata = serverApi.ModLoader.GetModSystem<GenRockStrataNew>();
            rockStrata = TryGetReflectedMember(genRockStrata, "strata") as RockStrataConfig;
        }
        catch
        {
            // Direct block-code resolution below still covers non-rock-type patches.
        }

        try
        {
            LCGRandom? rnd = TryGetReflectedMember(genVegetation, "rnd") as LCGRandom;
            if (rockStrata != null && rnd != null)
            {
                patch.Init(serverApi, rockStrata, rnd, patchIndex);
                return;
            }
        }
        catch
        {
            // Fall back to direct resolution.
        }

        if (patch.blockCodes == null || patch.blockCodes.Length == 0) return;

        patch.Blocks = patch.blockCodes
            .Select(code => serverApi.World.GetBlock(code))
            .Where(block => block != null)
            .ToArray();
    }

    private static string GetWorldgenBlockPatchLabel(BlockPatch patch, JObject row, int selectedRowIndex)
    {
        return patch.blockCodes?.FirstOrDefault()?.ToString()
            ?? FirstArrayString(row["blockCodes"] as JArray)
            ?? $"row {selectedRowIndex}";
    }

    private bool TryCreateWorldgenLandformDraftScope(GenTerra genTerra, JObject draftRow, int selectedRowIndex, out WorldgenPeekDraftScope scope, out string status)
    {
        scope = WorldgenPeekDraftScope.Empty;
        status = "";

        if (TryGetReflectedMember(genTerra, "landforms") is not LandformsWorldProperty originalLandforms)
        {
            status = "live engine config; GenTerra.landforms unavailable";
            return false;
        }

        if (originalLandforms.Variants == null || originalLandforms.Variants.Length == 0)
        {
            status = "live engine config; GenTerra has no landform variants";
            return false;
        }

        int replaceIndex = FindWorldgenLandformVariantIndex(originalLandforms, draftRow, selectedRowIndex);
        if (replaceIndex < 0 || replaceIndex >= originalLandforms.Variants.Length)
        {
            status = "live engine config; selected landform draft was not found in GenTerra";
            return false;
        }

        LandformsWorldProperty draftLandforms = CloneWorldgenLandforms(originalLandforms);
        LandformVariant originalVariant = originalLandforms.Variants[replaceIndex];
        LandformVariant draftVariant = CloneWorldgenLandformVariant(originalVariant);
        ApplyWorldgenLandformDraftRow(draftVariant, draftRow);
        draftVariant.index = originalVariant.index;
        draftLandforms.Variants[replaceIndex] = draftVariant;

        int landformIndex = originalVariant.index;
        if (draftLandforms.LandFormsByIndex != null && landformIndex >= 0 && landformIndex < draftLandforms.LandFormsByIndex.Length)
        {
            draftLandforms.LandFormsByIndex[landformIndex] = draftVariant;
        }
        ReplaceWorldgenLandformsByMatchingCode(draftLandforms.LandFormsByIndex, draftVariant);

        object? originalNoiseLandforms = TryGetReflectedStaticMember(typeof(NoiseLandforms), "landforms");
        if (!TrySetReflectedMember(genTerra, "landforms", draftLandforms, out string setError))
        {
            status = $"live engine config; could not set GenTerra.landforms: {setError}";
            return false;
        }

        bool noiseLandformsChanged = TrySetReflectedStaticMember(typeof(NoiseLandforms), "landforms", draftLandforms, out _);
        ClearWorldgenGenTerraLandformCaches(genTerra);

        string code = draftVariant.Code?.ToString() ?? draftRow["code"]?.ToString() ?? $"row {selectedRowIndex}";
        scope = new WorldgenPeekDraftScope(
            applied: true,
            status: $"landform draft applied to real 3D peek: {code}",
            restore: () =>
            {
                TrySetReflectedMember(genTerra, "landforms", originalLandforms, out _);
                if (noiseLandformsChanged)
                {
                    TrySetReflectedStaticMember(typeof(NoiseLandforms), "landforms", originalNoiseLandforms, out _);
                }
                ClearWorldgenGenTerraLandformCaches(genTerra);
            });
        status = scope.Status;
        return true;
    }

    private static LandformsWorldProperty CloneWorldgenLandforms(LandformsWorldProperty source)
    {
        return new LandformsWorldProperty
        {
            Code = source.Code,
            Variants = source.Variants?.Select(CloneWorldgenLandformVariant).ToArray() ?? [],
            LandFormsByIndex = source.LandFormsByIndex?.Select(CloneWorldgenLandformVariant).ToArray() ?? []
        };
    }

    private static LandformVariant CloneWorldgenLandformVariant(LandformVariant source)
    {
        return new LandformVariant
        {
            Chance = source.Chance,
            Code = source.Code,
            ColorInt = source.ColorInt,
            HexColor = source.HexColor,
            index = source.index,
            MaxRain = source.MaxRain,
            MaxTemp = source.MaxTemp,
            MaxWindStrength = source.MaxWindStrength,
            MinRain = source.MinRain,
            MinTemp = source.MinTemp,
            MinWindStrength = source.MinWindStrength,
            Mutations = source.Mutations?.Select(CloneWorldgenLandformVariant).ToArray(),
            TerrainOctaves = source.TerrainOctaves?.ToArray(),
            TerrainOctaveThresholds = source.TerrainOctaveThresholds?.ToArray(),
            TerrainYKeyPositions = source.TerrainYKeyPositions?.ToArray(),
            TerrainYKeyThresholds = source.TerrainYKeyThresholds?.ToArray(),
            TerrainYThresholds = source.TerrainYThresholds?.ToArray(),
            UseClimateMap = source.UseClimateMap,
            UseWindMap = source.UseWindMap,
            Weight = source.Weight,
            WeightTmp = source.WeightTmp
        };
    }

    private static int FindWorldgenLandformVariantIndex(LandformsWorldProperty landforms, JObject draftRow, int selectedRowIndex)
    {
        string? draftCode = draftRow["code"]?.ToString();
        if (!string.IsNullOrWhiteSpace(draftCode) && landforms.Variants != null)
        {
            for (int index = 0; index < landforms.Variants.Length; index++)
            {
                string? code = landforms.Variants[index].Code?.ToString();
                if (string.Equals(code, draftCode, StringComparison.OrdinalIgnoreCase)) return index;
            }
        }

        if (landforms.Variants != null && selectedRowIndex >= 0 && selectedRowIndex < landforms.Variants.Length)
        {
            return selectedRowIndex;
        }

        return -1;
    }

    private static void ReplaceWorldgenLandformsByMatchingCode(LandformVariant[]? variants, LandformVariant replacement)
    {
        if (variants == null || replacement.Code == null) return;

        string code = replacement.Code.ToString();
        for (int index = 0; index < variants.Length; index++)
        {
            if (variants[index].Code != null && string.Equals(variants[index].Code.ToString(), code, StringComparison.OrdinalIgnoreCase))
            {
                variants[index] = replacement;
            }
        }
    }

    private static void ApplyWorldgenLandformDraftRow(LandformVariant variant, JObject row)
    {
        string? code = row["code"]?.ToString();
        if (!string.IsNullOrWhiteSpace(code)) variant.Code = new AssetLocation(code);

        variant.Chance = ReadWorldgenFloat(row, "chance", variant.Chance);
        variant.Weight = ReadWorldgenDouble(row, "weight", variant.Weight);
        variant.WeightTmp = variant.Weight;
        variant.HexColor = row["hexcolor"]?.ToString() ?? variant.HexColor;
        variant.MinTemp = ReadWorldgenFloat(row, "minTemp", variant.MinTemp);
        variant.MaxTemp = ReadWorldgenFloat(row, "maxTemp", variant.MaxTemp);
        variant.MinRain = ReadWorldgenInt(row, "minRain", variant.MinRain);
        variant.MaxRain = ReadWorldgenInt(row, "maxRain", variant.MaxRain);
        variant.MinWindStrength = ReadWorldgenInt(row, "minWindStrength", variant.MinWindStrength);
        variant.MaxWindStrength = ReadWorldgenInt(row, "maxWindStrength", variant.MaxWindStrength);
        variant.UseClimateMap = ReadWorldgenBool(row, "useClimateMap", variant.UseClimateMap);
        variant.UseWindMap = ReadWorldgenBool(row, "useWindMap", variant.UseWindMap);

        if (row["terrainOctaves"] is JArray terrainOctaves) variant.TerrainOctaves = ReadWorldgenDoubleArray(terrainOctaves);
        if (row["terrainOctaveThresholds"] is JArray terrainOctaveThresholds) variant.TerrainOctaveThresholds = ReadWorldgenDoubleArray(terrainOctaveThresholds);
        if (row["terrainYKeyPositions"] is JArray terrainYKeyPositions) variant.TerrainYKeyPositions = ReadWorldgenFloatArray(terrainYKeyPositions);
        if (row["terrainYKeyThresholds"] is JArray terrainYKeyThresholds) variant.TerrainYKeyThresholds = ReadWorldgenFloatArray(terrainYKeyThresholds);
        if (row["terrainYThresholds"] is JArray terrainYThresholds) variant.TerrainYThresholds = ReadWorldgenFloatArray(terrainYThresholds);

        if (row["mutations"] is JArray mutations)
        {
            variant.Mutations = mutations
                .OfType<JObject>()
                .Select(mutation =>
                {
                    LandformVariant clone = CloneWorldgenLandformVariant(variant);
                    ApplyWorldgenLandformDraftRow(clone, mutation);
                    return clone;
                })
                .ToArray();
        }
    }

    private static void ClearWorldgenGenTerraLandformCaches(GenTerra genTerra)
    {
        if (TryGetReflectedMember(genTerra, "LandformMapByRegion") is System.Collections.IDictionary landformMapByRegion)
        {
            landformMapByRegion.Clear();
        }
    }

    private static float ReadWorldgenFloat(JObject row, string name, float fallback)
    {
        return row.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out JToken? token) && TryReadJsonFloat(token, out float value)
            ? value
            : fallback;
    }

    private static double ReadWorldgenDouble(JObject row, string name, double fallback)
    {
        return row.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out JToken? token) && TryReadJsonDouble(token, out double value)
            ? value
            : fallback;
    }

    private static int ReadWorldgenInt(JObject row, string name, int fallback)
    {
        return row.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out JToken? token) && TryReadJsonDouble(token, out double value)
            ? (int)Math.Round(value)
            : fallback;
    }

    private static bool ReadWorldgenBool(JObject row, string name, bool fallback)
    {
        return row.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out JToken? token) && TryReadJsonBool(token, out bool value)
            ? value
            : fallback;
    }

    private static float[] ReadWorldgenFloatArray(JArray array)
    {
        return array
            .Select(token => TryReadJsonFloat(token, out float value) ? value : 0f)
            .ToArray();
    }

    private static double[] ReadWorldgenDoubleArray(JArray array)
    {
        return array
            .Select(token => TryReadJsonDouble(token, out double value) ? value : 0d)
            .ToArray();
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
            RestoreActiveWorldgenPeek("singleplayer server API refresh");
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
        RestoreActiveWorldgenPeek(status);
        _worldgenPreviewPeekPending = false;
        _worldgenPreviewPeekProfile = null;
        _worldgenPreviewPeekCacheKey = null;
        _worldgenPreviewPeekStatus = status;
        ClearWorldgenLoadedWorldOracle("No loaded-world comparison yet.");
    }

    private void ClearWorldgenLoadedWorldOracle(string status)
    {
        _worldgenPreviewOracleProfile = null;
        _worldgenPreviewOracleStatus = status;
    }

    private void CompareWorldgenLoadedWorldOracle()
    {
        if (_worldgenPreviewPeekProfile == null)
        {
            ClearWorldgenLoadedWorldOracle("Compare requires a real peeked region first.");
            return;
        }

        ICoreServerAPI? serverApi = _worldgenPreviewServerApi;
        if (serverApi == null)
        {
            RefreshWorldgenServerApi();
            serverApi = _worldgenPreviewServerApi;
        }

        IWorldManagerAPI? worldManager = serverApi?.WorldManager;
        if (worldManager == null)
        {
            ClearWorldgenLoadedWorldOracle("Compare requires an integrated singleplayer server.");
            return;
        }

        try
        {
            _worldgenPreviewOracleProfile = BuildWorldgenLoadedWorldOracleProfile(worldManager, _worldgenPreviewPeekProfile);
            _worldgenPreviewOracleStatus = _worldgenPreviewOracleProfile.Summary;
        }
        catch (Exception exception)
        {
            ClearWorldgenLoadedWorldOracle($"Loaded-world compare failed: {exception.Message}");
            _worldgenDiagnostics.Exception("Worldgen loaded-world compare failed", exception);
        }
    }

    private static WorldgenLoadedWorldOracleProfile BuildWorldgenLoadedWorldOracleProfile(IWorldManagerAPI worldManager, WorldgenPeekRegionProfile peekProfile)
    {
        int chunkSize = Math.Max(1, peekProfile.ChunkSize);
        int chunkCountY = Math.Max(1, (int)Math.Ceiling(worldManager.MapSizeY / (double)chunkSize));
        int expectedColumns = peekProfile.RegionSizeChunks * peekProfile.RegionSizeChunks;
        int[] loadedHeights = new int[peekProfile.Width * peekProfile.Depth];
        int[] loadedTopBlockIds = new int[peekProfile.Width * peekProfile.Depth];
        int[] heightDeltas = new int[peekProfile.Width * peekProfile.Depth];
        bool[] compared = new bool[peekProfile.Width * peekProfile.Depth];
        bool[] topBlockMatches = new bool[peekProfile.Width * peekProfile.Depth];
        Array.Fill(loadedHeights, -1);
        Array.Fill(heightDeltas, int.MinValue);

        int loadedColumns = 0;
        int partialColumns = 0;
        int missingColumns = 0;
        int comparedCells = 0;
        int missingCells = 0;
        int heightMismatchCells = 0;
        int topBlockMismatchCells = 0;
        int maxAbsHeightDelta = 0;
        long totalAbsHeightDelta = 0;

        for (int dz = 0; dz < peekProfile.RegionSizeChunks; dz++)
        {
            for (int dx = 0; dx < peekProfile.RegionSizeChunks; dx++)
            {
                IServerChunk[] chunks = new IServerChunk[chunkCountY];
                int verticalLoaded = 0;
                for (int chunkY = 0; chunkY < chunkCountY; chunkY++)
                {
                    IServerChunk? chunk = worldManager.GetChunk(peekProfile.OriginChunkX + dx, chunkY, peekProfile.OriginChunkZ + dz);
                    if (chunk == null) continue;

                    chunks[chunkY] = chunk;
                    verticalLoaded++;
                }

                if (verticalLoaded == 0)
                {
                    missingColumns++;
                    missingCells += chunkSize * chunkSize;
                    continue;
                }

                loadedColumns++;
                if (verticalLoaded < chunkCountY) partialColumns++;

                for (int z = 0; z < chunkSize; z++)
                {
                    for (int x = 0; x < chunkSize; x++)
                    {
                        int loadedHeight = FindWorldgenPeekColumnHeight(chunks, x, z, chunkSize, out int loadedTopBlockId);
                        int globalX = dx * chunkSize + x;
                        int globalZ = dz * chunkSize + z;
                        int index = globalZ * peekProfile.Width + globalX;
                        if (index < 0 || index >= loadedHeights.Length || index >= peekProfile.Heights.Length || index >= peekProfile.TopBlockIds.Length) continue;

                        loadedHeights[index] = loadedHeight;
                        loadedTopBlockIds[index] = loadedTopBlockId;
                        int peekHeight = peekProfile.Heights[index];
                        if (loadedHeight < 0 || peekHeight < 0)
                        {
                            missingCells++;
                            continue;
                        }

                        int delta = loadedHeight - peekHeight;
                        heightDeltas[index] = delta;
                        compared[index] = true;
                        comparedCells++;

                        int absDelta = Math.Abs(delta);
                        if (absDelta > 0) heightMismatchCells++;
                        maxAbsHeightDelta = Math.Max(maxAbsHeightDelta, absDelta);
                        totalAbsHeightDelta += absDelta;

                        bool blockMatches = loadedTopBlockId == peekProfile.TopBlockIds[index];
                        topBlockMatches[index] = blockMatches;
                        if (!blockMatches) topBlockMismatchCells++;
                    }
                }
            }
        }

        float averageAbsDelta = comparedCells == 0 ? 0f : totalAbsHeightDelta / (float)comparedCells;
        string verdict;
        if (comparedCells == 0)
        {
            verdict = "INCOMPLETE";
        }
        else if (heightMismatchCells == 0 && topBlockMismatchCells == 0 && missingColumns == 0 && partialColumns == 0)
        {
            verdict = "PASS";
        }
        else if (heightMismatchCells == 0 && topBlockMismatchCells == 0)
        {
            verdict = "PASS for loaded cells, incomplete columns";
        }
        else
        {
            verdict = "DIFF";
        }

        string summary = comparedCells == 0
            ? $"Loaded-world oracle {verdict}: no loaded columns in {peekProfile.OriginChunkX},{peekProfile.OriginChunkZ} size {peekProfile.RegionSizeChunks}x{peekProfile.RegionSizeChunks}; no chunks were loaded."
            : $"Loaded-world oracle {verdict}: {heightMismatchCells}/{comparedCells} height diff(s), {topBlockMismatchCells} top-block diff(s), max |dy| {maxAbsHeightDelta}, avg |dy| {averageAbsDelta:0.00}; loaded {loadedColumns}/{expectedColumns} column(s), missing {missingColumns}, partial {partialColumns}.";

        return new WorldgenLoadedWorldOracleProfile(
            peekProfile.OriginChunkX,
            peekProfile.OriginChunkZ,
            peekProfile.RegionSizeChunks,
            peekProfile.ChunkSize,
            peekProfile.Width,
            peekProfile.Depth,
            loadedColumns,
            missingColumns,
            partialColumns,
            comparedCells,
            missingCells,
            heightMismatchCells,
            topBlockMismatchCells,
            maxAbsHeightDelta,
            averageAbsDelta,
            summary,
            BuildWorldgenPeekSampleSummary(loadedHeights, peekProfile.Width, peekProfile.Depth),
            loadedHeights,
            loadedTopBlockIds,
            heightDeltas,
            compared,
            topBlockMatches);
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

    private string GetWorldgenPreviewDraftFingerprint()
    {
        return $"{_worldgenLoadedKey}:{_worldgenRowIndex}:{_worldgenCurrentText.Length}:{StringComparer.Ordinal.GetHashCode(_worldgenCurrentText)}";
    }

    private void ProcessWorldgenPeekWatchdog()
    {
        WorldgenActivePeek? activePeek = _worldgenActivePeek;
        if (activePeek == null) return;

        double elapsedSeconds = (DateTime.UtcNow - activePeek.StartedUtc).TotalSeconds;
        if (elapsedSeconds < WorldgenPeekWatchdogSeconds) return;

        string restoreSummary = RestoreActiveWorldgenPeek($"real terrain peek timed out after {elapsedSeconds:0.0}s");
        _worldgenPreviewPeekStatus = $"Real terrain peek timed out after {elapsedSeconds:0.0}s; restored live worldgen state. {restoreSummary}";
        _worldgenDiagnostics.Warning("Worldgen real terrain peek timed out", _worldgenPreviewPeekStatus);
    }

    private void RestoreWorldgenPreviewForEditorTeardown(string reason)
    {
        RestoreActiveWorldgenPeek(reason);
    }

    private string RestoreActiveWorldgenPeek(string reason)
    {
        WorldgenActivePeek? activePeek;
        lock (_worldgenActivePeekGate)
        {
            activePeek = _worldgenActivePeek;
            _worldgenActivePeek = null;
        }

        if (activePeek == null) return "No active real terrain peek.";

        string restoreStatus = activePeek.RestoreLiveState();
        WorldgenPeekCleanupResult cleanup = activePeek.CleanupPreviewColumns();
        if (cleanup.FailedColumns > 0)
        {
            _worldgenDiagnostics.Warning($"Worldgen preview cleanup had failures after {reason}", cleanup.Details);
        }

        _worldgenPreviewPeekPending = false;
        _worldgenPreviewPeekDirty = false;
        _worldgenPreviewAutoPeekStatus = $"Auto 3D refresh stopped: {reason}.";
        UpdateWorldgenSaveIntegrityStatus(activePeek, cleanup, reason);
        return $"{restoreStatus}; {cleanup.Summary}";
    }

    private void TrackActiveWorldgenPeek(WorldgenActivePeek activePeek)
    {
        RestoreActiveWorldgenPeek("new real terrain peek replaced the previous one");
        lock (_worldgenActivePeekGate)
        {
            _worldgenActivePeek = activePeek;
        }
    }

    private bool TryCompleteActiveWorldgenPeek(WorldgenActivePeek activePeek, out string restoreStatus, out WorldgenPeekCleanupResult cleanup)
    {
        lock (_worldgenActivePeekGate)
        {
            if (!ReferenceEquals(_worldgenActivePeek, activePeek))
            {
                restoreStatus = "peek was already cancelled";
                cleanup = WorldgenPeekCleanupResult.Empty;
                return false;
            }

            _worldgenActivePeek = null;
        }

        restoreStatus = activePeek.RestoreLiveState();
        cleanup = activePeek.CleanupPreviewColumns();
        UpdateWorldgenSaveIntegrityStatus(activePeek, cleanup, "peek completed");
        return true;
    }

    private void RunWorldgenSaveIntegrityAudit(string reason)
    {
        string restoreSummary = RestoreActiveWorldgenPeek(reason);
        if (!string.Equals(restoreSummary, "No active real terrain peek.", StringComparison.Ordinal))
        {
            _worldgenPreviewPeekStatus = $"Safety audit restored an active real terrain peek: {restoreSummary}";
            return;
        }

        if (_worldgenPreviewServerApi == null)
        {
            RefreshWorldgenServerApi();
        }

        IWorldManagerAPI? worldManager = _worldgenPreviewServerApi?.WorldManager;
        if (worldManager == null)
        {
            _worldgenPreviewSaveIntegrityStatus = "Save-integrity audit INCOMPLETE: integrated singleplayer WorldManager unavailable.";
            return;
        }

        List<string> parts = ["no active real terrain peek"];
        if (TryGetWorldgenAutoGenerateChunks(worldManager, out bool autoGenerate))
        {
            parts.Add($"AutoGenerateChunks={autoGenerate}");
        }
        else
        {
            parts.Add("AutoGenerateChunks unreadable");
        }

        if (TryGetWorldgenSendChunks(worldManager, out bool sendChunks))
        {
            parts.Add($"SendChunks={sendChunks}");
        }
        else
        {
            parts.Add("SendChunks unreadable");
        }

        string? cleanupStatus = _worldgenPreviewPeekProfile?.CleanupSummary;
        if (!string.IsNullOrWhiteSpace(cleanupStatus))
        {
            parts.Add(cleanupStatus);
        }

        _worldgenPreviewSaveIntegrityStatus = "Save-integrity audit READY: " + string.Join("; ", parts) + ". Full byte-identical save validation still requires an external save snapshot.";
    }

    private void UpdateWorldgenSaveIntegrityStatus(WorldgenActivePeek activePeek, WorldgenPeekCleanupResult cleanup, string reason)
    {
        IWorldManagerAPI worldManager = activePeek.WorldManager;
        List<string> failures = [];
        List<string> parts = [$"restored after {reason}", cleanup.Summary];

        if (activePeek.AutoGenerateChanged)
        {
            if (TryGetWorldgenAutoGenerateChunks(worldManager, out bool autoGenerate))
            {
                parts.Add($"AutoGenerateChunks={autoGenerate}");
                if (autoGenerate != activePeek.ExpectedAutoGenerate)
                {
                    failures.Add($"AutoGenerateChunks expected {activePeek.ExpectedAutoGenerate} but is {autoGenerate}");
                }
            }
            else
            {
                failures.Add("AutoGenerateChunks unreadable after restore");
            }
        }

        if (activePeek.SendChunksChanged)
        {
            if (TryGetWorldgenSendChunks(worldManager, out bool sendChunks))
            {
                parts.Add($"SendChunks={sendChunks}");
                if (sendChunks != activePeek.ExpectedSendChunks)
                {
                    failures.Add($"SendChunks expected {activePeek.ExpectedSendChunks} but is {sendChunks}");
                }
            }
            else
            {
                failures.Add("SendChunks unreadable after restore");
            }
        }

        if (cleanup.FailedColumns > 0)
        {
            failures.Add($"{cleanup.FailedColumns} preview column cleanup failure(s)");
        }

        _worldgenPreviewSaveIntegrityStatus = failures.Count == 0
            ? "Save-integrity audit PASS: " + string.Join("; ", parts) + "."
            : "Save-integrity audit DIFF: " + string.Join("; ", failures) + "; " + string.Join("; ", parts) + ".";
    }

    private bool IsActiveWorldgenPeek(WorldgenActivePeek activePeek)
    {
        lock (_worldgenActivePeekGate)
        {
            return ReferenceEquals(_worldgenActivePeek, activePeek);
        }
    }

    private void EnqueueWorldgenLatePeekCleanup(WorldgenActivePeek activePeek, Dictionary<Vec2i, IServerChunk[]> columns)
    {
        if (columns.Count == 0) return;

        Dictionary<Vec2i, IServerChunk[]> returnedColumns = new(columns);
        _api.Event.EnqueueMainThreadTask(() =>
        {
            WorldgenPeekCleanupResult lateCleanup = activePeek.CleanupReturnedColumns(returnedColumns);
            if (lateCleanup.FailedColumns > 0)
            {
                _worldgenDiagnostics.Warning("Worldgen late preview cleanup had failures", lateCleanup.Details);
            }
        }, "ingamedevtools-worldgen-late-peek-cleanup");
    }

    private void RequestWorldgenPeekRegion(bool forceRefresh = false, string reason = "manual")
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
        WorldgenPeekRegionCacheKey cacheKey = new(ParseWorldgenPreviewSeed(), originChunkX, originChunkZ, regionSize, untilPass, GetWorldgenPreviewDraftFingerprint());
        if (!forceRefresh && _worldgenPreviewPeekProfile != null && _worldgenPreviewPeekCacheKey == cacheKey)
        {
            _worldgenPreviewPeekStatus = $"Using cached {passLabel} peek for {regionSize}x{regionSize} chunks at {originChunkX},{originChunkZ}.";
            return;
        }

        _worldgenPreviewPeekPending = true;
        _worldgenPreviewPeekProfile = null;
        _worldgenPreviewPeekCacheKey = null;
        _worldgenPreviewPeekStatus = $"Requesting real {passLabel} peek for {regionSize}x{regionSize} chunks at {originChunkX},{originChunkZ} ({reason})...";
        ClearWorldgenLoadedWorldOracle("No loaded-world comparison yet.");
        WorldgenPeekDraftScope draftScope = CreateWorldgenPeekDraftScope(serverApi, out string draftScopeStatus);

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
        List<Vec2i> requestedColumns = new(totalRequests);
        Dictionary<Vec2i, bool> initiallyLoadedColumns = new();
        Dictionary<Vec2i, IServerChunk[]> regionColumns = new();
        Exception? firstFailure = null;
        bool restoreSendChunks = true;
        bool sendChunksChanged = false;
        WorldgenActivePeek? activePeek = null;

        for (int dz = 0; dz < regionSize; dz++)
        {
            for (int dx = 0; dx < regionSize; dx++)
            {
                Vec2i key = new(originChunkX + dx, originChunkZ + dz);
                requestedColumns.Add(key);
                initiallyLoadedColumns[key] = TryIsWorldgenChunkColumnLoaded(worldManager, key.X, key.Y, chunkSize, out bool wasLoaded, out string? loadStateError)
                    ? wasLoaded
                    : true;
                if (!string.IsNullOrWhiteSpace(loadStateError))
                {
                    _worldgenDiagnostics.Warning($"Worldgen peek could not read pre-peek chunk state for {key.X},{key.Y}: {loadStateError}");
                }
            }
        }

        try
        {
            restoreSendChunks = TryGetWorldgenSendChunks(worldManager, out bool previousSendChunks)
                ? previousSendChunks
                : true;
            sendChunksChanged = TrySetWorldgenSendChunks(worldManager, false, out string? sendChunksError);
            if (!sendChunksChanged && !string.IsNullOrWhiteSpace(sendChunksError))
            {
                _worldgenDiagnostics.Warning($"Worldgen peek could not pause SendChunks: {sendChunksError}");
            }

            activePeek = new WorldgenActivePeek(
                ++_worldgenPeekSerial,
                worldManager,
                draftScope,
                draftScopeStatus,
                autoGenerateChanged,
                restoreAutoGenerate,
                sendChunksChanged,
                restoreSendChunks,
                requestedColumns,
                initiallyLoadedColumns,
                $"{passLabel} {regionSize}x{regionSize} at {originChunkX},{originChunkZ}");
            TrackActiveWorldgenPeek(activePeek);

            void OnGenerated(Dictionary<Vec2i, IServerChunk[]> columns)
            {
                if (!IsActiveWorldgenPeek(activePeek))
                {
                    EnqueueWorldgenLatePeekCleanup(activePeek, columns);
                    return;
                }

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

                _api.Event.EnqueueMainThreadTask(() =>
                {
                    if (!TryCompleteActiveWorldgenPeek(activePeek, out string restoreStatusToDispatch, out WorldgenPeekCleanupResult cleanupToDispatch))
                    {
                        EnqueueWorldgenLatePeekCleanup(activePeek, columns);
                        return;
                    }

                    _worldgenPreviewPeekPending = false;
                    if (cleanupToDispatch.FailedColumns > 0)
                    {
                        _worldgenDiagnostics.Warning("Worldgen preview cleanup had failures", cleanupToDispatch.Details);
                    }

                    if (failureToDispatch != null)
                    {
                        _worldgenPreviewPeekStatus = $"Real {passLabel} region peek failed: {failureToDispatch.Message}";
                        _worldgenDiagnostics.Exception("Worldgen region peek failed", failureToDispatch);
                        return;
                    }

                    if (profileToDispatch != null)
                    {
                        string draftSummary = activePeek.DraftApplied
                            ? $"{cleanupToDispatch.Summary}; {activePeek.DraftStatus}; {restoreStatusToDispatch}"
                            : $"{cleanupToDispatch.Summary}; {activePeek.DraftFallbackStatus}; {restoreStatusToDispatch}";
                        profileToDispatch = profileToDispatch with { CleanupSummary = draftSummary };
                    }

                    _worldgenPreviewPeekProfile = profileToDispatch;
                    _worldgenPreviewPeekCacheKey = profileToDispatch == null ? null : cacheKey;
                    _worldgenPreviewPeekStatus = profileToDispatch == null
                        ? $"Real {passLabel} region peek returned no chunks at {originChunkX},{originChunkZ}."
                        : $"Real {passLabel} region peek: {profileToDispatch.ColumnsReturned}/{totalRequests} column(s), {profileToDispatch.ChunksReturned} vertical chunk(s); {profileToDispatch.CleanupSummary}";
                }, "ingamedevtools-worldgen-peek-region");
            }

            foreach (Vec2i key in requestedColumns)
            {
                ChunkPeekOptions options = new()
                {
                    UntilPass = untilPass,
                    OnGenerated = OnGenerated
                };

                worldManager.PeekChunkColumn(key.X, key.Y, options);
            }
        }
        catch (Exception exception)
        {
            string restoreSummary;
            if (activePeek != null)
            {
                restoreSummary = RestoreActiveWorldgenPeek($"real {passLabel} region peek request failed");
            }
            else
            {
                draftScope.Dispose();
                if (autoGenerateChanged)
                {
                    TrySetWorldgenAutoGenerateChunks(worldManager, restoreAutoGenerate, out _);
                }
                if (sendChunksChanged)
                {
                    TrySetWorldgenSendChunks(worldManager, restoreSendChunks, out _);
                }
                restoreSummary = "Restored local peek setup.";
            }

            _worldgenPreviewPeekPending = false;
            _worldgenPreviewPeekStatus = $"Real {passLabel} region peek request failed: {exception.Message}; {restoreSummary}";
            _worldgenDiagnostics.Exception("Worldgen region peek request failed", exception);
        }
    }

    private static WorldgenPeekCleanupResult CleanupWorldgenPeekColumns(IWorldManagerAPI worldManager, IReadOnlyList<Vec2i> requestedColumns, IReadOnlyDictionary<Vec2i, bool> initiallyLoadedColumns)
    {
        int unloaded = 0;
        int keptLoaded = 0;
        int failed = 0;
        StringBuilder details = new();

        foreach (Vec2i key in requestedColumns)
        {
            if (initiallyLoadedColumns.TryGetValue(key, out bool wasLoaded) && wasLoaded)
            {
                keptLoaded++;
                continue;
            }

            try
            {
                worldManager.UnloadChunkColumn(key.X, key.Y);
                unloaded++;
            }
            catch (Exception exception)
            {
                failed++;
                details.AppendLine($"{key.X},{key.Y}: {exception.Message}");
            }
        }

        string summary = failed == 0
            ? $"Cleanup unloaded {unloaded} preview-only column(s), kept {keptLoaded} already-loaded column(s); no delete used."
            : $"Cleanup unloaded {unloaded} preview-only column(s), kept {keptLoaded} already-loaded column(s), failed {failed}; no delete used.";
        return new WorldgenPeekCleanupResult(unloaded, keptLoaded, failed, summary, details.ToString());
    }

    private static bool TryIsWorldgenChunkColumnLoaded(IWorldManagerAPI worldManager, int chunkX, int chunkZ, int chunkSize, out bool loaded, out string? error)
    {
        loaded = false;
        error = null;

        try
        {
            int chunkCountY = Math.Max(1, (int)Math.Ceiling(worldManager.MapSizeY / (double)Math.Max(1, chunkSize)));
            for (int chunkY = 0; chunkY < chunkCountY; chunkY++)
            {
                if (worldManager.GetChunk(chunkX, chunkY, chunkZ) == null) continue;

                loaded = true;
                return true;
            }

            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
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

    private static bool TrySetWorldgenSendChunks(IWorldManagerAPI worldManager, bool enabled, out string? error)
    {
        try
        {
            worldManager.SendChunks = enabled;
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TryGetWorldgenSendChunks(IWorldManagerAPI worldManager, out bool enabled)
    {
        try
        {
            enabled = worldManager.SendChunks;
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
        int mapHeight = Math.Max(0, columns.Values.Where(chunks => chunks != null).Select(chunks => chunks.Length).DefaultIfEmpty(0).Max() * chunkSize);
        int[] columnBlockIds = mapHeight > 0 ? new int[width * depth * mapHeight] : [];
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
                        CopyWorldgenPeekColumnBlockIds(chunks, x, z, chunkSize, mapHeight, columnBlockIds, index);
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
            mapHeight,
            heights,
            topBlockIds,
            columnBlockIds);
    }

    private static void CopyWorldgenPeekColumnBlockIds(
        IServerChunk[] chunks,
        int localX,
        int localZ,
        int chunkSize,
        int mapHeight,
        int[] columnBlockIds,
        int columnIndex)
    {
        if (mapHeight <= 0 || columnBlockIds.Length == 0) return;

        int baseIndex = columnIndex * mapHeight;
        for (int y = 0; y < mapHeight; y++)
        {
            int chunkY = y / chunkSize;
            if (chunkY < 0 || chunkY >= chunks.Length) continue;

            IChunkBlocks? data = chunks[chunkY]?.Data;
            if (data == null) continue;

            int localY = y - chunkY * chunkSize;
            int blockIndex = MapUtil.Index3d(localX, localY, localZ, chunkSize, chunkSize);
            columnBlockIds[baseIndex + y] = data[blockIndex];
        }
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
        if (InGameDevToolsModSystem.ActiveServerApi != null)
        {
            serverApi = InGameDevToolsModSystem.ActiveServerApi;
            source = "InGameDevTools server-side mod system";
            return true;
        }

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
        return mode is WorldgenPreviewModeOre or WorldgenPreviewModeBlockPatch;
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

        if (TryEnsureWorldgenPreviewRasterTexture(rasterColors, cellsX, cellsZ, out int textureId, out string textureError))
        {
            drawList.AddImage(new IntPtr(textureId), min, max, NVector2.Zero, NVector2.One);
            SaveWorldgenViewportScreenshotIfRequested(textureId, cellsX, cellsZ);
            return;
        }

        _worldgenPreviewRasterStatus = $"Raster texture unavailable ({textureError}); drawing {cellsX * cellsZ} cells.";
        if (_worldgenViewportScreenshotRequested)
        {
            _worldgenViewportScreenshotRequested = false;
            _worldgenStatus = $"Worldgen screenshot failed: raster texture unavailable ({textureError}).";
        }
        DrawWorldgenPreviewRasterCells(drawList, min, max, rasterColors, cellsX, cellsZ, cellWidth, cellHeight);
    }

    private void SaveWorldgenViewportScreenshotIfRequested(int textureId, int width, int height)
    {
        if (!_worldgenViewportScreenshotRequested) return;
        _worldgenViewportScreenshotRequested = false;
        string modeLabel = WorldgenPreviewModeLabels[Math.Clamp(_worldgenPreviewMode, 0, WorldgenPreviewModeLabels.Length - 1)];
        DevToolsTextureCapture.SaveTexture2D(textureId, width, height, $"worldgen-{modeLabel}", out _worldgenStatus);
    }

    private static void DrawWorldgenPreviewRasterCells(ImDrawListPtr drawList, NVector2 min, NVector2 max, uint[] rasterColors, int cellsX, int cellsZ, float cellWidth, float cellHeight)
    {
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

    private bool TryEnsureWorldgenPreviewRasterTexture(uint[] colors, int width, int height, out int textureId, out string error)
    {
        textureId = 0;
        error = "";

        if (_worldgenPreviewRasterCacheKey is not { } key)
        {
            error = "no raster cache key";
            return false;
        }

        if (colors.Length < width * height)
        {
            error = $"raster buffer too small ({colors.Length} for {width}x{height})";
            return false;
        }

        if (_worldgenPreviewTextureId > 0 &&
            _worldgenPreviewTextureCacheKey == key &&
            _worldgenPreviewTextureWidth == width &&
            _worldgenPreviewTextureHeight == height)
        {
            textureId = _worldgenPreviewTextureId;
            return true;
        }

        int restoreActiveTexture = 0;
        int restoreTexture2D = 0;
        int restoreUnpackAlignment = 4;
        GCHandle pinned = default;

        try
        {
            GL.GetInteger(GetPName.ActiveTexture, out restoreActiveTexture);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.GetInteger(GetPName.TextureBinding2D, out restoreTexture2D);
            GL.GetInteger(GetPName.UnpackAlignment, out restoreUnpackAlignment);

            if (_worldgenPreviewTextureId <= 0)
            {
                GL.GenTextures(1, out _worldgenPreviewTextureId);
                GL.BindTexture(TextureTarget.Texture2D, _worldgenPreviewTextureId);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            }
            else
            {
                GL.BindTexture(TextureTarget.Texture2D, _worldgenPreviewTextureId);
            }

            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            pinned = GCHandle.Alloc(colors, GCHandleType.Pinned);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba8,
                width,
                height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                pinned.AddrOfPinnedObject());

            _worldgenPreviewTextureCacheKey = key;
            _worldgenPreviewTextureWidth = width;
            _worldgenPreviewTextureHeight = height;
            _worldgenPreviewRasterStatus = $"Raster texture: {width}x{height}; one ImGui image.";
            textureId = _worldgenPreviewTextureId;
            return textureId > 0;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            if (pinned.IsAllocated)
            {
                pinned.Free();
            }

            try
            {
                GL.PixelStore(PixelStoreParameter.UnpackAlignment, restoreUnpackAlignment);
                GL.BindTexture(TextureTarget.Texture2D, restoreTexture2D);
                GL.ActiveTexture((TextureUnit)restoreActiveTexture);
            }
            catch
            {
                // The fallback cell renderer can still draw this frame if GL state restore fails.
            }
        }
    }

    private void DisposeWorldgenPreviewRasterTexture()
    {
        if (_worldgenPreviewTextureId <= 0) return;

        try
        {
            GL.DeleteTexture(_worldgenPreviewTextureId);
        }
        catch
        {
            // The GL context may already be gone during game shutdown.
        }

        _worldgenPreviewTextureId = 0;
        _worldgenPreviewTextureCacheKey = null;
        _worldgenPreviewTextureWidth = 0;
        _worldgenPreviewTextureHeight = 0;
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

        long drawProfileStart = Stopwatch.GetTimestamp();
        WorldgenTerrainShapeSampler sampler = CreateWorldgenTerrainShapeSampler(row, draft, out string samplerStatus);

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
                float h = sampler.SampleHeight(seed, worldX, worldZ);
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
        NVector2 origin = ProjectWorldgenSurfacePoint(0f, 0f, sampler.SampleHeight(seed, centerX, centerZ), center, screenScale, heightScale, cosYaw, sinYaw, pitch);
        NVector2 xAxis = ProjectWorldgenSurfacePoint(spanBlocks * 0.16f, 0f, sampler.SampleHeight(seed, centerX + spanBlocks * 0.16f, centerZ), center, screenScale, heightScale, cosYaw, sinYaw, pitch);
        NVector2 zAxis = ProjectWorldgenSurfacePoint(0f, spanBlocks * 0.16f, sampler.SampleHeight(seed, centerX, centerZ + spanBlocks * 0.16f), center, screenScale, heightScale, cosYaw, sinYaw, pitch);
        drawList.AddLine(origin, xAxis, axisX, 2f);
        drawList.AddLine(origin, zAxis, axisZ, 2f);

        string drawProfile = UpdateWorldgen3DDrawProfile("draft surface draw-list", cells.Count, grid * grid, drawProfileStart);
        _worldgenPreviewRasterStatus = $"3D draft surface: {grid}x{grid}; height {minHeight:0.000}-{maxHeight:0.000}; {samplerStatus}; yaw {_worldgenPreview3DYaw:0.00}, pitch {_worldgenPreview3DPitch:0.00}; {drawProfile}";
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

        long drawProfileStart = Stopwatch.GetTimestamp();
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
        WorldgenLoadedWorldOracleProfile? oracleProfile = _worldgenPreviewShowOracleDiff && IsWorldgenOracleProfileFor(profile, _worldgenPreviewOracleProfile)
            ? _worldgenPreviewOracleProfile
            : null;

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

                uint topColor = oracleProfile == null
                    ? BuildWorldgenPeekBlockColor(topBlockId, heightNorm, 1.00f)
                    : BuildWorldgenOracleDiffColor(oracleProfile, index, 1.00f);
                AddWorldgenPeekTopFace(faces, localX, localZ, yTop, baseHeight, center, screenScale, heightScale, cosYaw, sinYaw, pitch, topColor);
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

        string oracleSuffix = oracleProfile == null ? "" : $"; oracle diff {oracleProfile.HeightMismatchCells}/{oracleProfile.ComparedCells} height, {oracleProfile.TopBlockMismatchCells} block";
        string drawProfile = UpdateWorldgen3DDrawProfile("real region draw-list", faces.Count, widthBlocks * depthBlocks, drawProfileStart);
        _worldgenPreviewRasterStatus = $"3D real {profile.PassLabel} region: chunks {profile.OriginChunkX},{profile.OriginChunkZ} size {profile.RegionSizeChunks}x{profile.RegionSizeChunks}; {faces.Count} visible face(s); height {profile.MinHeight}-{profile.MaxHeight}{oracleSuffix}; yaw {_worldgenPreview3DYaw:0.00}, pitch {_worldgenPreview3DPitch:0.00}; {drawProfile}";
    }

    private string UpdateWorldgen3DDrawProfile(string path, int primitives, int samples, long startedTimestamp)
    {
        double elapsedMs = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        _worldgenPreview3DDrawAverageMs = _worldgenPreview3DDrawAverageMs <= 0.0
            ? elapsedMs
            : _worldgenPreview3DDrawAverageMs * 0.85 + elapsedMs * 0.15;

        string recommendation = _worldgenPreview3DDrawAverageMs >= 8.0 || primitives >= 12000
            ? "mesh/FBO recommended"
            : "draw-list OK";
        _worldgenPreview3DDrawProfileStatus = $"{path}: {primitives} primitive(s), {samples} sample(s), {elapsedMs:0.00} ms last, {_worldgenPreview3DDrawAverageMs:0.00} ms avg; {recommendation}";
        return _worldgenPreview3DDrawProfileStatus;
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
        _worldgenPreviewRasterStatus = "";
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
        else if (_worldgenPreviewMode == WorldgenPreviewModeRockStrata)
        {
            if (!TryBuildWorldgenRockStrataRaster(seed, centerX, centerZ, halfWidthBlocks, halfHeightBlocks, cellsX, cellsZ, colors, out error))
            {
                colors = null;
                return false;
            }
        }
        else if (WorldgenPreviewModeUsesMapLayer(_worldgenPreviewMode))
        {
            if (!TryBuildWorldgenMapLayerRaster(seed, centerX, centerZ, halfWidthBlocks, halfHeightBlocks, cellsX, cellsZ, colors, out error))
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
        if (string.IsNullOrWhiteSpace(_worldgenPreviewRasterStatus))
        {
            _worldgenPreviewRasterStatus = $"Raster cache: {cellsX}x{cellsZ}";
        }
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
            Dictionary<WorldgenOreMapRegionCacheKey, IntDataMap2D> oreMapCache = [];
            string samplerStatus = "";
            float minFactor = float.PositiveInfinity;
            float maxFactor = float.NegativeInfinity;
            for (int z = 0; z < cellsZ; z++)
            {
                int worldZ = (int)MathF.Floor(centerZ - halfHeightBlocks + (z + 0.5f) * (2f * halfHeightBlocks / cellsZ));
                int chunkZ = FloorDiv(worldZ, chunkSize);
                for (int x = 0; x < cellsX; x++)
                {
                    int worldX = (int)MathF.Floor(centerX - halfWidthBlocks + (x + 0.5f) * (2f * halfWidthBlocks / cellsX));
                    int chunkX = FloorDiv(worldX, chunkSize);
                    float factor = TrySampleWorldgenPreviewOreMapFactor(variant!, chunkX, chunkZ, oreMapCache, out float sampledFactor, out samplerStatus)
                        ? sampledFactor
                        : variant!.GetOreMapFactor(chunkX, chunkZ);
                    minFactor = Math.Min(minFactor, factor);
                    maxFactor = Math.Max(maxFactor, factor);
                    colors[z * cellsX + x] = BuildWorldgenOrePreviewColor(factor);
                }
            }

            _worldgenPreviewRasterStatus = $"Raster cache: {cellsX}x{cellsZ}; ore factor {minFactor:0.###}-{maxFactor:0.###}; {samplerStatus}; exact propick uses the latest matching 3D peek";
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

    private bool TrySampleWorldgenPreviewOreMapFactor(
        DepositVariant variant,
        int chunkX,
        int chunkZ,
        Dictionary<WorldgenOreMapRegionCacheKey, IntDataMap2D> oreMapCache,
        out float factor,
        out string status)
    {
        factor = 0f;
        if (!TrySampleWorldgenPreviewOreMapValue(variant, chunkX, chunkZ, oreMapCache, out int oreMapValue, out status))
        {
            return false;
        }

        factor = (oreMapValue & 0xff) / 255f;
        return true;
    }

    private bool TrySampleWorldgenPreviewOreMapValue(
        DepositVariant variant,
        int chunkX,
        int chunkZ,
        Dictionary<WorldgenOreMapRegionCacheKey, IntDataMap2D> oreMapCache,
        out int oreMapValue,
        out string status)
    {
        oreMapValue = 0;
        status = "ore map sampler unavailable";

        if (!variant.WithOreMap)
        {
            oreMapValue = 255;
            status = "deposit has no ore map; factor is 1";
            return true;
        }

        ICoreServerAPI? serverApi = _worldgenPreviewServerApi;
        if (serverApi == null)
        {
            status = "live server unavailable; using deposit fallback";
            return false;
        }

        string? code = GetWorldgenPreviewDepositCode(variant);
        if (string.IsNullOrWhiteSpace(code))
        {
            status = "deposit has no ore map code; using deposit fallback";
            return false;
        }

        if (variant.OreMapLayer == null)
        {
            try
            {
                int hash = StringUtil.GetNonRandomizedHashCode(code);
                NoiseOre oreNoise = new(serverApi.WorldManager.Seed + hash);
                variant.OreMapLayer = GenMaps.GetOreMap(
                    serverApi.WorldManager.Seed + hash + 1,
                    oreNoise,
                    variant.OreMapScale,
                    variant.OreMapContrast,
                    variant.OreMapSub);
            }
            catch (Exception exception)
            {
                status = $"ore map layer creation failed: {exception.Message}";
                return false;
            }
        }

        int regionSize = Math.Max(1, serverApi.WorldManager.RegionSize);
        int chunkSize = GetWorldgenPreviewDepositChunkSize(variant);
        int noiseSizeOre = Math.Max(1, regionSize / Math.Max(1, TerraGenConfig.oreMapScale));
        int regionX = FloorDiv(chunkX * chunkSize, regionSize);
        int regionZ = FloorDiv(chunkZ * chunkSize, regionSize);
        WorldgenOreMapRegionCacheKey key = new(code, regionX, regionZ, noiseSizeOre);

        if (!oreMapCache.TryGetValue(key, out IntDataMap2D? map))
        {
            map = new IntDataMap2D
            {
                Size = noiseSizeOre + 1,
                BottomRightPadding = 1,
                Data = variant.OreMapLayer.GenLayer(regionX * noiseSizeOre, regionZ * noiseSizeOre, noiseSizeOre + 1, noiseSizeOre + 1)
            };
            oreMapCache[key] = map;
        }

        int blockX = chunkX * chunkSize + chunkSize / 2;
        int blockZ = chunkZ * chunkSize + chunkSize / 2;
        int localX = GameMath.Mod(blockX, regionSize);
        int localZ = GameMath.Mod(blockZ, regionSize);
        float sampleX = GameMath.Clamp((float)localX / regionSize * noiseSizeOre, 0f, noiseSizeOre - 1);
        float sampleZ = GameMath.Clamp((float)localZ / regionSize * noiseSizeOre, 0f, noiseSizeOre - 1);
        oreMapValue = map.GetUnpaddedColorLerped(sampleX, sampleZ);
        status = "in-memory OnMapRegionGen ore map sampler";
        return true;
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
        MapLayerBase? shrub = genMaps.bushGen;
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
                    int? shrubValue = shrub?.GenLayer(worldX, worldZ, 1, 1)[0];
                    WorldgenClimateSample sample = DecodeWorldgenClimateSample(climateValue, forestValue, shrubValue);
                    bool matches = draft.IsSuitable(sample);
                    if (matches) suitable++;
                    colors[z * cellsX + x] = BuildWorldgenBlockPatchPreviewColor(sample, draft, matches);
                }
            }

            string shrubStatus = shrub == null ? "; shrub unavailable" : "; shrub sampled";
            _worldgenPreviewRasterStatus = $"Raster cache: {cellsX}x{cellsZ}; approximate block-patch suitable {suitable}/{cellsX * cellsZ}{shrubStatus}; exact terrain/surface checks deferred";
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

            WorldgenTerrainShapeSampler sampler = CreateWorldgenTerrainShapeSampler(row, draft, out string samplerStatus);
            float minHeight = float.PositiveInfinity;
            float maxHeight = float.NegativeInfinity;
            for (int z = 0; z < cellsZ; z++)
            {
                float worldZ = centerZ - halfHeightBlocks + (z + 0.5f) * (2f * halfHeightBlocks / cellsZ);
                for (int x = 0; x < cellsX; x++)
                {
                    float worldX = centerX - halfWidthBlocks + (x + 0.5f) * (2f * halfWidthBlocks / cellsX);
                    float height = sampler.SampleHeight(seed, worldX, worldZ);
                    float east = sampler.SampleHeight(seed, worldX + 8f, worldZ);
                    float south = sampler.SampleHeight(seed, worldX, worldZ + 8f);
                    minHeight = Math.Min(minHeight, height);
                    maxHeight = Math.Max(maxHeight, height);
                    colors[z * cellsX + x] = BuildWorldgenTerrainShapePreviewColor(height, east, south, draft);
                }
            }

            _worldgenPreviewRasterStatus = $"Raster cache: {cellsX}x{cellsZ}; landform height {minHeight:0.000}-{maxHeight:0.000}; {samplerStatus}";
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

    private bool TryBuildWorldgenRockStrataRaster(
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
        if (!TryGetSelectedWorldgenRockStrataRow(out JObject? row) || row == null)
        {
            error = "Select a rock-strata row to preview thickness.";
            return false;
        }

        try
        {
            WorldgenRockStrataDraft draft = WorldgenRockStrataDraft.FromJson(row);
            if (!draft.IsUsable)
            {
                error = "Selected rock stratum needs matching amplitudes, frequencies, and thresholds.";
                return false;
            }

            WorldgenRockStrataSampler sampler = CreateWorldgenRockStrataSampler(seed, draft, out string samplerStatus);
            float[] thicknesses = new float[cellsX * cellsZ];
            float minThickness = float.PositiveInfinity;
            float maxThickness = float.NegativeInfinity;
            for (int z = 0; z < cellsZ; z++)
            {
                float worldZ = centerZ - halfHeightBlocks + (z + 0.5f) * (2f * halfHeightBlocks / cellsZ);
                for (int x = 0; x < cellsX; x++)
                {
                    float worldX = centerX - halfWidthBlocks + (x + 0.5f) * (2f * halfWidthBlocks / cellsX);
                    float thickness = sampler.SampleThickness(worldX, worldZ);
                    int index = z * cellsX + x;
                    thicknesses[index] = thickness;
                    minThickness = Math.Min(minThickness, thickness);
                    maxThickness = Math.Max(maxThickness, thickness);
                }
            }

            float range = Math.Max(1f, maxThickness - minThickness);
            for (int index = 0; index < thicknesses.Length; index++)
            {
                float normalized = (thicknesses[index] - minThickness) / range;
                colors[index] = BuildWorldgenRockStrataPreviewColor(normalized, draft);
            }

            _worldgenPreviewRasterStatus = $"Raster cache: {cellsX}x{cellsZ}; draft stratum thickness {minThickness:0.#}-{maxThickness:0.#} blocks; {samplerStatus}; final province stack not simulated";
            error = "";
            return true;
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen rock strata raster failed", exception);
            error = $"Rock strata render failed: {exception.Message}";
            return false;
        }
    }

    private WorldgenRockStrataSampler CreateWorldgenRockStrataSampler(long seed, WorldgenRockStrataDraft draft, out string status)
    {
        int mapSizeY = GetWorldgenPreviewMapHeight();
        try
        {
            status = "engine MapLayerCustomPerlin sampler";
            return WorldgenRockStrataSampler.CreateEngine(seed, _worldgenRowIndex, mapSizeY, draft);
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen rock strata engine sampler failed", exception);
            status = $"fallback value-noise sampler; engine sampler failed: {exception.Message}";
            return WorldgenRockStrataSampler.CreateFallback(seed, mapSizeY, draft);
        }
    }

    private WorldgenTerrainShapeSampler CreateWorldgenTerrainShapeSampler(JObject row, WorldgenLandformDraft fallbackDraft, out string status)
    {
        try
        {
            if (TryCreateEngineWorldgenTerrainShapeSampler(row, fallbackDraft, out WorldgenTerrainShapeSampler? sampler, out status) && sampler != null)
            {
                return sampler;
            }
        }
        catch (Exception exception)
        {
            _worldgenDiagnostics.Exception("Worldgen terrain shape engine sampler failed", exception);
            status = $"approximate draft sampler; engine ColumnNoise failed: {exception.Message}";
        }

        return new WorldgenTerrainShapeSampler(fallbackDraft, status);
    }

    private bool TryCreateEngineWorldgenTerrainShapeSampler(JObject row, WorldgenLandformDraft fallbackDraft, out WorldgenTerrainShapeSampler? sampler, out string status)
    {
        sampler = null;
        ICoreServerAPI? serverApi = _worldgenPreviewServerApi;
        if (serverApi == null)
        {
            RefreshWorldgenServerApi();
            serverApi = _worldgenPreviewServerApi;
        }

        if (serverApi?.WorldManager == null)
        {
            status = "approximate draft sampler; singleplayer server API unavailable";
            return false;
        }

        GenTerra? genTerra = serverApi.ModLoader.GetModSystem<GenTerra>();
        if (genTerra == null)
        {
            status = "approximate draft sampler; GenTerra unavailable";
            return false;
        }

        if (TryGetReflectedMember(genTerra, "terrainNoise") is not NewNormalizedSimplexFractalNoise terrainNoise)
        {
            status = "approximate draft sampler; GenTerra terrainNoise unavailable";
            return false;
        }

        if (TryGetReflectedMember(genTerra, "distort2dx") is not SimplexNoise distortX ||
            TryGetReflectedMember(genTerra, "distort2dz") is not SimplexNoise distortZ)
        {
            status = "approximate draft sampler; GenTerra distortion noise unavailable";
            return false;
        }

        int mapSizeY = serverApi.WorldManager.MapSizeY;
        if (mapSizeY < 8)
        {
            status = "approximate draft sampler; invalid server map height";
            return false;
        }

        LandformVariant variant = CreateWorldgenPreviewLandformVariantForSampling(genTerra, row, _worldgenRowIndex, serverApi.WorldManager);
        if (variant.TerrainOctaves == null || variant.TerrainOctaves.Length == 0 || variant.TerrainOctaveThresholds == null || variant.TerrainYThresholds == null || variant.TerrainYThresholds.Length < mapSizeY)
        {
            status = "approximate draft sampler; initialized landform thresholds unavailable";
            return false;
        }

        double verticalFrequency = 0.5 / TerraGenConfig.terrainNoiseVerticalScale;
        sampler = new WorldgenTerrainShapeSampler(
            fallbackDraft,
            terrainNoise,
            distortX,
            distortZ,
            variant.TerrainOctaves,
            variant.TerrainOctaveThresholds,
            variant.TerrainYThresholds,
            mapSizeY,
            verticalFrequency,
            "engine ColumnNoise sampler");
        status = "engine ColumnNoise sampler";
        return true;
    }

    private static LandformVariant CreateWorldgenPreviewLandformVariantForSampling(GenTerra genTerra, JObject row, int selectedRowIndex, IWorldManagerAPI worldManager)
    {
        LandformVariant variant = new();
        if (TryGetReflectedMember(genTerra, "landforms") is LandformsWorldProperty landforms)
        {
            int index = FindWorldgenLandformVariantIndex(landforms, row, selectedRowIndex);
            if (index >= 0 && landforms.Variants != null && index < landforms.Variants.Length)
            {
                variant = CloneWorldgenLandformVariant(landforms.Variants[index]);
            }
        }

        ApplyWorldgenLandformDraftRow(variant, row);
        variant.Init(worldManager, Math.Max(0, variant.index));
        return variant;
    }

    private bool TryBuildWorldgenMapLayerRaster(
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
        MapLayerBase? mapLayer = GetWorldgenPreviewMapLayer(_worldgenPreviewMode);
        if (mapLayer == null)
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

            string fallbackReason = string.IsNullOrWhiteSpace(_worldgenPreviewMapLayerStatus)
                ? "live map layer unavailable"
                : _worldgenPreviewMapLayerStatus;
            _worldgenPreviewRasterStatus = $"Raster cache: {cellsX}x{cellsZ}; procedural fallback ({fallbackReason})";
            error = "";
            return true;
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
        _worldgenPreviewTextureCacheKey = null;
        _worldgenPreviewRasterStatus = "Raster cache invalidated.";
    }

    private string GetWorldgenPreviewRasterContextKey()
    {
        return _worldgenPreviewMode switch
        {
            WorldgenPreviewModeOre => GetSelectedWorldgenRowContext(WorldgenAssetKind.Deposits),
            WorldgenPreviewModeBlockPatch => GetSelectedWorldgenRowContext(WorldgenAssetKind.BlockPatches),
            WorldgenPreviewModeLandform => GetSelectedWorldgenRowContext(WorldgenAssetKind.Landforms),
            WorldgenPreviewModeTerrainShape => SelectedWorldgenEntry?.Key + ":" + _worldgenRowIndex + ":" + _worldgenCurrentText,
            WorldgenPreviewModeRockStrata => SelectedWorldgenEntry?.Key + ":" + _worldgenRowIndex + ":" + _worldgenCurrentText,
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

    private static WorldgenClimateSample DecodeWorldgenClimateSample(int climateValue, int forestValue, int? shrubValue = null)
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
        float shrub = shrubValue == null ? 0f : Math.Clamp(shrubValue.Value / 255f, 0f, 1f);
        return new WorldgenClimateSample(tempCelsius, rain, forest, shrub, shrubValue != null, 0f);
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

    private static uint BuildWorldgenRockStrataPreviewColor(float normalized, WorldgenRockStrataDraft draft)
    {
        normalized = Math.Clamp(normalized, 0f, 1f);
        NVector4 low = new(0.10f, 0.095f, 0.085f, 1f);
        NVector4 high = new(0.62f, 0.58f, 0.48f, 1f);
        NVector4 color = LerpColor(low, high, normalized);

        if (TryParseHexColor(draft.HexColor, out NVector4 tint))
        {
            color = LerpColor(color, tint, 0.35f);
        }

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

    private static bool TryGetWorldgenPeekLocalColumn(WorldgenPeekRegionProfile profile, int blockX, int blockZ, out int localX, out int localZ)
    {
        localX = blockX - profile.OriginChunkX * profile.ChunkSize;
        localZ = blockZ - profile.OriginChunkZ * profile.ChunkSize;
        return localX >= 0 && localZ >= 0 && localX < profile.Width && localZ < profile.Depth;
    }

    private static bool TryGetWorldgenPeekBlockColumn(WorldgenPeekRegionProfile profile, int localX, int localZ, int topY, out int[] blockColumn)
    {
        blockColumn = [];
        if (profile.MapHeight <= 0 || profile.ColumnBlockIds.Length == 0) return false;
        if (localX < 0 || localZ < 0 || localX >= profile.Width || localZ >= profile.Depth) return false;

        int length = Math.Clamp(topY + 1, 1, profile.MapHeight);
        int columnIndex = localZ * profile.Width + localX;
        int baseIndex = columnIndex * profile.MapHeight;
        if (baseIndex < 0 || baseIndex + length > profile.ColumnBlockIds.Length) return false;

        blockColumn = new int[length];
        Array.Copy(profile.ColumnBlockIds, baseIndex, blockColumn, 0, length);
        return true;
    }

    private static bool IsWorldgenOracleProfileFor(WorldgenPeekRegionProfile profile, WorldgenLoadedWorldOracleProfile? oracleProfile)
    {
        return oracleProfile != null &&
            oracleProfile.OriginChunkX == profile.OriginChunkX &&
            oracleProfile.OriginChunkZ == profile.OriginChunkZ &&
            oracleProfile.RegionSizeChunks == profile.RegionSizeChunks &&
            oracleProfile.ChunkSize == profile.ChunkSize &&
            oracleProfile.Width == profile.Width &&
            oracleProfile.Depth == profile.Depth;
    }

    private uint BuildWorldgenOracleDiffColor(WorldgenLoadedWorldOracleProfile oracleProfile, int index, float shade)
    {
        NVector4 color;
        if (index < 0 || index >= oracleProfile.Compared.Length || !oracleProfile.Compared[index])
        {
            color = new NVector4(0.10f, 0.10f, 0.10f, 1f);
        }
        else
        {
            int delta = index < oracleProfile.HeightDeltas.Length ? oracleProfile.HeightDeltas[index] : 0;
            bool topBlockMatches = index < oracleProfile.TopBlockMatches.Length && oracleProfile.TopBlockMatches[index];
            if (delta == 0 && topBlockMatches)
            {
                color = new NVector4(0.16f, 0.48f, 0.22f, 1f);
            }
            else if (delta == 0)
            {
                color = new NVector4(0.86f, 0.66f, 0.18f, 1f);
            }
            else if (delta > 0)
            {
                float strength = Math.Clamp(Math.Abs(delta) / 12f, 0.25f, 1f);
                color = new NVector4(0.16f, 0.30f + 0.22f * strength, 0.66f + 0.24f * strength, 1f);
            }
            else
            {
                float strength = Math.Clamp(Math.Abs(delta) / 12f, 0.25f, 1f);
                color = new NVector4(0.66f + 0.24f * strength, 0.18f, 0.12f, 1f);
            }
        }

        color.X *= shade;
        color.Y *= shade;
        color.Z *= shade;
        return ImGui.ColorConvertFloat4ToU32(color);
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
        bool wasLoaded = entry.Asset.IsLoaded();
        try
        {
            if (!wasLoaded && !entry.Asset.Origin.TryLoadAsset(entry.Asset))
            {
                throw new InvalidOperationException("The asset origin could not load the selected file.");
            }

            _worldgenOriginalText = ReadAssetText(entry.Asset);
        }
        catch (Exception exception)
        {
            _worldgenOriginalText = "";
            _worldgenStatus = $"Could not load {entry.Domain}:{entry.AssetPath}: {exception.Message}";
            _worldgenDiagnostics.Exception($"Could not load {entry.Key}", exception);
        }
        finally
        {
            if (!wasLoaded && !entry.Asset.IsPatched)
            {
                entry.Asset.Data = null!;
            }
        }

        TryParseJsonToken(_worldgenOriginalText, out JToken? sourceRoot, out _);
        entry.UpdateKind(sourceRoot);
        _worldgenCleanText = sourceRoot == null ? _worldgenOriginalText : sourceRoot.ToString(Formatting.Indented);

        if (_worldgenDraftStates.TryGetValue(entry.Key, out WorldgenDraftState? draft))
        {
            _worldgenCurrentText = draft.Text;
            _worldgenRowIndex = draft.RowIndex;
        }
        else
        {
            _worldgenCurrentText = _worldgenCleanText;
            _worldgenRowIndex = 0;
        }

        ValidateWorldgenCurrentText();
        _worldgenTextHistory.Reset(_worldgenCurrentText);
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

        bool dirty = IsWorldgenTextDirty(_worldgenCurrentText, _worldgenCleanText);
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
        return _worldgenDraftStates.ContainsKey(entry.Key);
    }

    private static bool IsWorldgenTextDirty(string currentText, string cleanText)
    {
        return !string.Equals(currentText, cleanText, StringComparison.Ordinal);
    }

    private void SetWorldgenCurrentRoot(JToken root)
    {
        _worldgenCurrentText = root.ToString(Formatting.Indented);
        ValidateWorldgenCurrentText();
        RememberWorldgenDraft();
        InvalidateWorldgenPreviewRasterCache();
        ScheduleWorldgenRealtimePeek("structured draft changed");
    }

    private void ScheduleWorldgenRealtimePeek(string reason)
    {
        if (_worldgenPreviewMode != WorldgenPreviewModeRegion3D || !_worldgenPreviewAutoPeekOnEdit) return;

        _worldgenPreviewPeekDirty = true;
        _worldgenPreviewPeekDueUtc = DateTime.UtcNow.AddMilliseconds(750);
        _worldgenPreviewAutoPeekStatus = $"Auto 3D refresh queued: {reason}.";
    }

    private void ProcessWorldgenRealtimePeek()
    {
        if (!_worldgenPreviewAutoPeekOnEdit ||
            !_worldgenPreviewPeekDirty ||
            _worldgenPreviewMode != WorldgenPreviewModeRegion3D ||
            _worldgenPreviewPeekPending)
        {
            return;
        }

        if (DateTime.UtcNow < _worldgenPreviewPeekDueUtc) return;

        if (_worldgenPreviewServerApi == null)
        {
            _worldgenPreviewAutoPeekStatus = "Auto 3D refresh waiting for singleplayer server API.";
            return;
        }

        if (!_worldgenTextValid)
        {
            _worldgenPreviewAutoPeekStatus = "Auto 3D refresh waiting for valid JSON.";
            return;
        }

        _worldgenPreviewPeekDirty = false;
        _worldgenPreviewAutoPeekStatus = "Auto 3D refresh requested from draft-aware real engine preview.";
        RequestWorldgenPeekRegion(forceRefresh: true, reason: "auto");
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
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : _worldgenOriginalText;
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

    private static bool MatchesWorldgenKindFilter(
        WorldgenAssetKind entryKind,
        bool isContentClassified,
        WorldgenAssetKind filterKind)
    {
        return entryKind == filterKind ||
            (entryKind == WorldgenAssetKind.Other && !isContentClassified);
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
        if (!DevToolsJson.TryParseToken(text, out token, out error)) return false;

        if (token != null)
        {
            SanitizeWorldgenToken(token);
        }

        return token != null;
    }

    private static void SanitizeWorldgenToken(JToken token)
    {
        switch (token)
        {
            case JObject obj:
                SanitizeWorldgenObject(obj);
                break;
            case JArray array:
                SanitizeWorldgenArray(array);
                break;
            case JValue value:
                SanitizeWorldgenValue(value);
                break;
        }
    }

    private static void SanitizeWorldgenObject(JObject obj)
    {
        bool natFloat = LooksLikeNatFloat(obj);
        foreach (JProperty property in obj.Properties().ToList())
        {
            if (property.Value.Type == JTokenType.Undefined)
            {
                if (natFloat && IsWorldgenNatFloatNumberProperty(property.Name))
                {
                    property.Value = new JValue(0f);
                }
                else if (natFloat && property.Name.Equals("dist", StringComparison.OrdinalIgnoreCase))
                {
                    property.Value = new JValue("uniform");
                }
                else
                {
                    property.Remove();
                }
                continue;
            }

            SanitizeWorldgenToken(property.Value);
        }
    }

    private static void SanitizeWorldgenArray(JArray array)
    {
        for (int index = 0; index < array.Count; index++)
        {
            JToken token = array[index];
            if (token.Type == JTokenType.Undefined)
            {
                array[index] = new JValue(0f);
                continue;
            }

            SanitizeWorldgenToken(token);
        }
    }

    private static void SanitizeWorldgenValue(JValue value)
    {
        if (value.Type != JTokenType.Float) return;

        try
        {
            double number = value.Value<double>();
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                value.Value = 0d;
            }
        }
        catch
        {
            value.Value = 0d;
        }
    }

    private static bool IsWorldgenNatFloatNumberProperty(string propertyName)
    {
        return propertyName.Equals("avg", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("var", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("offset", StringComparison.OrdinalIgnoreCase);
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
        if (!TryReadJsonDouble(token, out double doubleValue)) return false;

        value = (float)doubleValue;
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool TryReadJsonDouble(JToken? token, out double value)
    {
        value = 0d;
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return false;

        try
        {
            value = token.Value<double>();
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadJsonBool(JToken? token, out bool value)
    {
        value = false;
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return false;

        try
        {
            value = token.Value<bool>();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatJsonNumber(JToken token)
    {
        return TryReadJsonDouble(token, out double value)
            ? value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
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
}
