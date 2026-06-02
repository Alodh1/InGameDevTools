using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private const int WorldgenIndexBatchSize = 80;
    private static readonly string[] WorldgenKindFilterLabels =
    [
        "All",
        "Deposits",
        "Block patches",
        "Landforms",
        "Rock strata",
        "Other"
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

        foreach (IAsset asset in _api.Assets.AllAssets.Values)
        {
            if (IsWorldgenJsonAsset(asset))
            {
                _worldgenIndexAssets.Add(asset);
            }
        }

        _worldgenIndexAssets.Sort((left, right) => string.Compare(left.Location.ToString(), right.Location.ToString(), StringComparison.OrdinalIgnoreCase));
        _worldgenStatus = BuildWorldgenIndexProgressText();
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
        DrawWorldgenReservedViewport();
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

    private void DrawWorldgenReservedViewport()
    {
        float height = Math.Clamp(ImGui.GetContentRegionAvail().Y * 0.34f, 150f, 300f);
        NVector2 size = new(-float.Epsilon, height);
        ImGui.BeginChild("##worldgen-blank-viewport", size, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        NVector2 min = ImGui.GetWindowPos();
        NVector2 actual = ImGui.GetWindowSize();
        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0.02f, 0.02f, 0.018f, 1f));
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.35f, 0.30f, 0.22f, 1f));
        ImGui.GetWindowDrawList().AddRectFilled(min, new NVector2(min.X + actual.X, min.Y + actual.Y), background);
        ImGui.GetWindowDrawList().AddRect(min, new NVector2(min.X + actual.X, min.Y + actual.Y), border);
        ImGui.SetCursorPos(new NVector2(12f, 14f));
        ImGui.TextWrapped("Worldgen preview reserved for later.");
        ImGui.TextWrapped("V1 edits source JSON only; it does not simulate terrain or mutate generated chunks.");
        ImGui.EndChild();
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
}
