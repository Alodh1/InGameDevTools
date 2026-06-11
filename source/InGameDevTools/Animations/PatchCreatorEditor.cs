using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private static readonly string[] PatchCreatorKnownCategories =
    [
        "blocktypes",
        "itemtypes",
        "recipes",
        "worldgen",
        "entities",
        "config",
        "patches",
        "jsonpatches",
        "shapes",
        "lang",
        "sounds",
        "dialog"
    ];
    private static readonly string[] PatchCreatorOutputFormatLabels = ["JsonPatchesLib", "Vanilla patches"];
    private static readonly string[] PatchCreatorTargetModeLabels = ["Exact asset", "Wildcard @", "Regex @@"];
    private static readonly string[] PatchCreatorTemplateLabels = ["Set property", "Append to array", "Remove value/property", "Test value", "Merge list values", "Run expression", "Copy path", "Move path", "Raw operation"];
    private static readonly string[] PatchCreatorJsonPatchesLibOps = ["add", "replace", "remove", "copy", "move", "test", "addmerge", "addeach", "expression"];
    private static readonly string[] PatchCreatorVanillaOps = ["add", "replace", "remove", "copy", "move", "test"];
    private static readonly string[] PatchCreatorSideLabels = ["Server", "Client", "Universal"];
    private static readonly string[] PatchCreatorConditionModeLabels = ["No condition", "Use setting value", "Is true", "Is false", "Is custom value"];

    private readonly List<PatchCreatorAssetEntry> _patchCreatorAssets = [];
    private readonly List<PatchCreatorAssetEntry> _visiblePatchCreatorAssets = [];
    private readonly List<PatchCreatorOperationDraft> _patchCreatorOperations = [];
    private readonly ImGuiThreePanelLayoutState _patchCreatorLayout = new(0.25f, 0.34f);
    private readonly DevToolsEditorDiagnostics _patchCreatorDiagnostics = new("Patches");
    private readonly DevToolsAssetIndexer _patchCreatorIndexer = new(batchSize: 120);
    private string _patchCreatorFilter = "";
    private string _patchCreatorDomainFilter = "";
    private string _patchCreatorCategoryFilter = "";
    private int _patchCreatorAssetIndex;
    private int _patchCreatorOutputFormat;
    private int _patchCreatorTargetMode;
    private int _patchCreatorTemplate;
    private int _patchCreatorOperationIndex = -1;
    private int _patchCreatorOpIndex;
    private int _patchCreatorSideIndex;
    private string _patchCreatorOutputDomain = "ingamedevtools";
    private string _patchCreatorPatchName = "generated-patch";
    private string _patchCreatorFilePattern = "";
    private string _patchCreatorPath = "";
    private string _patchCreatorFromPath = "";
    private string _patchCreatorValueJson = "null";
    private string _patchCreatorConditionJson = "";
    private string _patchCreatorConditionWhen = "";
    private string _patchCreatorConditionValueJson = "true";
    private bool _patchCreatorEnabled = true;
    private int _patchCreatorPriority;
    private int _patchCreatorConditionMode;
    private int _patchCreatorPreviewMode;
    private string _patchCreatorStatus = "Patch creator ready.";
    private string _patchCreatorSelectedPath = "";
    private string _patchCreatorSelectedTokenJson = "";
    private string _patchCreatorDiffJson = "";
    private string _patchCreatorDiffStatus = "";

    private void PatchCreatorTab(float deltaSeconds, bool showDiagnostics)
    {
        _ = deltaSeconds;
        ClearActiveTransformGizmo();

        try
        {
            EnsurePatchCreatorAssetsIndexed();

            NVector2 available = ImGui.GetContentRegionAvail();
            float scale = Math.Max(0.75f, _devToolsUiScale);
            float splitterThickness = Math.Max(5f, 6f * scale);
            ImGuiLayoutHelper.CalculateThreePanelWidths(
                available.X,
                splitterThickness,
                _patchCreatorLayout,
                260f * scale,
                600f * scale,
                500f * scale,
                380f * scale,
                820f * scale,
                out float panelAvailableWidth,
                out float leftWidth,
                out float centerWidth,
                out float rightWidth);

            DrawPatchCreatorAssetBrowser(new NVector2(leftWidth, available.Y));
            ImGui.SameLine(0, 0);
            ImGuiLayoutHelper.DrawVerticalSplitter("##patch-creator-left-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _patchCreatorLayout.LeftFraction, 260f * scale, Math.Max(260f * scale, panelAvailableWidth - rightWidth - 500f * scale));
            ImGui.SameLine(0, 0);
            DrawPatchCreatorPathPanel(new NVector2(centerWidth, available.Y));
            ImGui.SameLine(0, 0);
            ImGuiLayoutHelper.DrawVerticalSplitter("##patch-creator-right-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _patchCreatorLayout.RightFraction, 380f * scale, Math.Max(380f * scale, panelAvailableWidth - leftWidth - 500f * scale), invertDrag: true);
            ImGui.SameLine(0, 0);
            DrawPatchCreatorOutputPanel(new NVector2(rightWidth, available.Y), showDiagnostics);
        }
        catch (Exception exception)
        {
            _patchCreatorIndexer.Fail();
            _patchCreatorStatus = $"Patch creator error: {exception.Message}";
            _patchCreatorDiagnostics.Exception("Patch creator failed", exception);
            _api.Logger.Error("[InGameDevTools] Patch creator failed: {0}", exception);
            ImGui.TextWrapped(_patchCreatorStatus);
            _patchCreatorDiagnostics.Draw("patch-creator-error", showDiagnostics);
        }
    }

    private void ResetPatchCreatorLayout()
    {
        _patchCreatorLayout.Reset();
    }

    private void ApplyPatchCreatorRuntime(bool force = false)
    {
        _ = force;
        _liveApplyManager.LastStatus = "Patch creator writes authored patch files; it has no runtime apply target in v1.";
    }

    private void ClearPatchCreatorLiveApplyState()
    {
    }

    private void EnsurePatchCreatorAssetsIndexed()
    {
        _patchCreatorIndexer.EnsureIndexed(StartPatchCreatorIndexing, ProcessPatchCreatorIndexBatch);
    }

    private void StartPatchCreatorIndexing()
    {
        _patchCreatorIndexer.Begin();
        _patchCreatorAssets.Clear();
        _visiblePatchCreatorAssets.Clear();
        _patchCreatorAssetIndex = 0;

        // Authored patch files first so the user's saved copies win the duplicate check.
        _patchCreatorIndexer.AddAssets(CollectToolAuthoredAssets("patches"), IsPatchCreatorJsonAsset);
        _patchCreatorIndexer.AddAssets(_api.Assets.AllAssets.Values, IsPatchCreatorJsonAsset);
        foreach (string category in PatchCreatorKnownCategories)
        {
            _patchCreatorIndexer.AddSource(
                $"asset category '{category}'",
                () => _api.Assets.GetManyInCategory(category, ""),
                IsPatchCreatorJsonAsset,
                _patchCreatorDiagnostics);
        }

        _patchCreatorIndexer.SortPendingByLocation();
        _patchCreatorStatus = BuildPatchCreatorIndexProgressText();
    }

    private static bool IsPatchCreatorJsonAsset(IAsset? asset)
    {
        if (asset?.Location == null) return false;
        string path = asset.Location.Path.Replace('\\', '/');
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private void ProcessPatchCreatorIndexBatch()
    {
        if (!_patchCreatorIndexer.TryProcessBatch(
                IndexPatchCreatorAsset,
                CompletePatchCreatorIndexing,
                () => _patchCreatorStatus = BuildPatchCreatorIndexProgressText(),
                out Exception? error))
        {
            _patchCreatorStatus = $"Patch creator indexing failed: {error?.Message}";
            _patchCreatorDiagnostics.Exception("Patch creator indexing failed", error!);
        }
    }

    private string BuildPatchCreatorIndexProgressText()
    {
        return $"Indexing JSON assets {_patchCreatorIndexer.Position}/{_patchCreatorIndexer.PendingAssets.Count}.";
    }

    private void IndexPatchCreatorAsset(IAsset asset)
    {
        string sourceText = ReadAssetText(asset);
        TryParsePatchCreatorJson(sourceText, out JToken? root, out string parseError);
        _patchCreatorAssets.Add(new PatchCreatorAssetEntry(asset, sourceText, root, parseError));
    }

    private void CompletePatchCreatorIndexing()
    {
        _patchCreatorAssets.Sort((left, right) => string.Compare(left.SortKey, right.SortKey, StringComparison.OrdinalIgnoreCase));
        RebuildVisiblePatchCreatorAssets();
        _patchCreatorStatus = $"Indexed {_patchCreatorAssets.Count} JSON asset(s).";
        SyncPatchCreatorSelection();
    }

    private void RebuildVisiblePatchCreatorAssets()
    {
        PatchCreatorAssetEntry? selected = SelectedPatchCreatorAsset;
        string filter = _patchCreatorFilter.Trim();
        _visiblePatchCreatorAssets.Clear();

        foreach (PatchCreatorAssetEntry entry in _patchCreatorAssets)
        {
            if (!string.IsNullOrWhiteSpace(filter) && !entry.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ImGuiLayoutHelper.MatchesDomain(_patchCreatorDomainFilter, entry.Domain)) continue;
            if (!string.IsNullOrWhiteSpace(_patchCreatorCategoryFilter) && !entry.Category.Equals(_patchCreatorCategoryFilter, StringComparison.OrdinalIgnoreCase)) continue;
            _visiblePatchCreatorAssets.Add(entry);
        }

        if (selected != null)
        {
            int index = _visiblePatchCreatorAssets.FindIndex(entry => entry.Key.Equals(selected.Key, StringComparison.OrdinalIgnoreCase));
            _patchCreatorAssetIndex = Math.Max(0, index);
        }
        else
        {
            _patchCreatorAssetIndex = Math.Clamp(_patchCreatorAssetIndex, 0, Math.Max(0, _visiblePatchCreatorAssets.Count - 1));
        }
    }

    private PatchCreatorAssetEntry? SelectedPatchCreatorAsset =>
        _visiblePatchCreatorAssets.Count == 0 ? null : _visiblePatchCreatorAssets[Math.Clamp(_patchCreatorAssetIndex, 0, _visiblePatchCreatorAssets.Count - 1)];

    private void DrawPatchCreatorAssetBrowser(NVector2 size)
    {
        ImGui.BeginChild("##patch-creator-browser", size, true);
        ImGui.SeparatorText("JSON assets");

        if (ImGui.Button("Reload index##patch-creator-reload", new NVector2(-1, 0)))
        {
            StartPatchCreatorIndexing();
        }

        bool changed = false;
        changed |= ImGui.InputText("Filter##patch-creator-filter", ref _patchCreatorFilter, 256);
        changed |= ImGuiLayoutHelper.DrawDomainCombo("Domain##patch-creator-domain", ref _patchCreatorDomainFilter, _patchCreatorAssets.Select(entry => entry.Domain));
        changed |= DrawPatchCreatorCategoryCombo();
        if (changed)
        {
            RebuildVisiblePatchCreatorAssets();
        }

        ImGui.TextDisabled($"{_visiblePatchCreatorAssets.Count}/{_patchCreatorAssets.Count}");
        if (_patchCreatorIndexer.IsIndexing)
        {
            ImGui.TextWrapped(_patchCreatorStatus);
        }

        if (ImGui.BeginChild("##patch-creator-asset-list", new NVector2(-float.Epsilon, -float.Epsilon), true))
        {
            for (int i = 0; i < _visiblePatchCreatorAssets.Count; i++)
            {
                PatchCreatorAssetEntry entry = _visiblePatchCreatorAssets[i];
                string suffix = entry.Root == null ? " !" : "";
                if (ImGui.Selectable($"{entry.Domain}:{entry.AssetPath}{suffix}##patch-creator-asset-{i}", i == _patchCreatorAssetIndex))
                {
                    _patchCreatorAssetIndex = i;
                    SyncPatchCreatorSelection();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{entry.Category}\n{entry.Domain}:{entry.AssetPath}\n{(entry.Root == null ? entry.ParseError : "valid JSON")}");
                }
            }
        }
        ImGui.EndChild();
        ImGui.EndChild();
    }

    private bool DrawPatchCreatorCategoryCombo()
    {
        string[] categories = _patchCreatorAssets
            .Select(entry => entry.Category)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .Prepend("All categories")
            .ToArray();
        int current = string.IsNullOrWhiteSpace(_patchCreatorCategoryFilter)
            ? 0
            : Math.Max(0, Array.FindIndex(categories, category => category.Equals(_patchCreatorCategoryFilter, StringComparison.OrdinalIgnoreCase)));
        bool changed = ImGui.Combo("Category##patch-creator-category", ref current, categories, categories.Length);
        if (changed)
        {
            _patchCreatorCategoryFilter = current <= 0 ? "" : categories[current];
        }
        return changed;
    }

    private void DrawPatchCreatorPathPanel(NVector2 size)
    {
        ImGui.BeginChild("##patch-creator-path", size, true);
        PatchCreatorAssetEntry? entry = SelectedPatchCreatorAsset;
        ImGui.SeparatorText("Target path");

        if (entry == null)
        {
            ImGui.TextWrapped(_patchCreatorIndexer.IsIndexing ? _patchCreatorStatus : "No JSON asset selected.");
            ImGui.EndChild();
            return;
        }

        ImGui.TextWrapped($"Sample: {entry.Domain}:{entry.AssetPath}");
        if (entry.Root == null)
        {
            ImGui.TextColored(new NVector4(1f, 0.35f, 0.25f, 1f), $"Invalid JSON: {entry.ParseError}");
            ImGui.EndChild();
            return;
        }

        if (ImGui.Button("Use exact target##patch-creator-use-exact"))
        {
            _patchCreatorTargetMode = 0;
            _patchCreatorFilePattern = GetPatchCreatorBuilderFile(entry);
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy target##patch-creator-copy-target"))
        {
            ImGui.SetClipboardText(GetPatchCreatorBuilderFile(entry));
            _patchCreatorStatus = "Copied target file path.";
        }

        ImGui.TextWrapped($"Selected path: {(_patchCreatorSelectedPath.Length == 0 ? "<root>" : _patchCreatorSelectedPath)}");
        if (ImGui.Button("Use selected path##patch-creator-use-path"))
        {
            _patchCreatorPath = _patchCreatorSelectedPath;
            if (!string.IsNullOrWhiteSpace(_patchCreatorSelectedTokenJson))
            {
                _patchCreatorValueJson = _patchCreatorSelectedTokenJson;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Use as frompath##patch-creator-use-from"))
        {
            _patchCreatorFromPath = _patchCreatorSelectedPath;
        }

        ImGui.SeparatorText("JSON tree");
        if (ImGui.BeginChild("##patch-creator-json-tree", new NVector2(-float.Epsilon, -float.Epsilon), true))
        {
            DrawPatchCreatorJsonTree(entry.Root, "", "$");
        }
        ImGui.EndChild();
        ImGui.EndChild();
    }

    private void DrawPatchCreatorJsonTree(JToken token, string path, string label)
    {
        ImGui.PushID($"patch-creator-tree-{path}-{label}");
        bool selected = path.Equals(_patchCreatorSelectedPath, StringComparison.Ordinal);
        string display = BuildPatchCreatorTreeLabel(token, label);
        bool container = token is JObject || token is JArray;
        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.SpanAvailWidth;
        if (!container) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        if (selected) flags |= ImGuiTreeNodeFlags.Selected;
        bool open = ImGui.TreeNodeEx(display, flags);
        if (ImGui.IsItemClicked())
        {
            _patchCreatorSelectedPath = path;
            _patchCreatorSelectedTokenJson = token.ToString(Formatting.Indented);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(path.Length == 0 ? "<root>" : path);
        }

        if (container && open)
        {
            if (token is JObject obj)
            {
                foreach (JProperty property in obj.Properties())
                {
                    DrawPatchCreatorJsonTree(property.Value, JoinPatchCreatorPath(path, property.Name), property.Name);
                }
            }
            else if (token is JArray array)
            {
                int shown = 0;
                for (int i = 0; i < array.Count; i++)
                {
                    if (shown >= 400)
                    {
                        ImGui.TextDisabled($"...{array.Count - shown} more array item(s)");
                        break;
                    }
                    DrawPatchCreatorJsonTree(array[i], JoinPatchCreatorPath(path, i.ToString(CultureInfo.InvariantCulture)), $"[{i}]");
                    shown++;
                }
            }
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    private static string BuildPatchCreatorTreeLabel(JToken token, string label)
    {
        return token switch
        {
            JObject obj => $"{label}  {{ {obj.Properties().Count()} }}",
            JArray array => $"{label}  [ {array.Count} ]",
            _ => $"{label}: {TrimPatchCreatorPreview(token.ToString(Formatting.None), 80)}"
        };
    }

    private void DrawPatchCreatorOutputPanel(NVector2 size, bool showDiagnostics)
    {
        ImGui.BeginChild("##patch-creator-output", size, true);
        DrawPatchCreatorBuilder();
        ImGui.SeparatorText("Operations");
        DrawPatchCreatorOperationList();
        ImGui.SeparatorText("Preview and save");
        DrawPatchCreatorPreviewAndSave(showDiagnostics);
        ImGui.EndChild();
    }

    private void DrawPatchCreatorBuilder()
    {
        PatchCreatorAssetEntry? entry = SelectedPatchCreatorAsset;
        ImGui.Combo("Output##patch-creator-output-format", ref _patchCreatorOutputFormat, PatchCreatorOutputFormatLabels, PatchCreatorOutputFormatLabels.Length);
        _patchCreatorOutputFormat = Math.Clamp(_patchCreatorOutputFormat, 0, PatchCreatorOutputFormatLabels.Length - 1);

        ImGui.InputText("Patch domain##patch-creator-domain-output", ref _patchCreatorOutputDomain, 120);
        ImGui.InputText("Patch name##patch-creator-name-output", ref _patchCreatorPatchName, 160);
        if (ImGui.Button("Use sample domain##patch-creator-sample-domain") && entry != null)
        {
            _patchCreatorOutputDomain = entry.Domain;
        }

        ImGui.Combo("Template##patch-creator-template", ref _patchCreatorTemplate, PatchCreatorTemplateLabels, PatchCreatorTemplateLabels.Length);
        if (ImGui.Button("Apply template##patch-creator-apply-template"))
        {
            ApplyPatchCreatorTemplate();
        }

        ImGui.SeparatorText("Target");
        ImGui.Combo("Target mode##patch-creator-target-mode", ref _patchCreatorTargetMode, PatchCreatorTargetModeLabels, PatchCreatorTargetModeLabels.Length);
        _patchCreatorTargetMode = Math.Clamp(_patchCreatorTargetMode, 0, PatchCreatorTargetModeLabels.Length - 1);
        if (_patchCreatorTargetMode == 0)
        {
            string exact = entry == null ? "" : GetPatchCreatorBuilderFile(entry);
            ImGui.TextWrapped($"Exact file: {exact}");
        }
        else
        {
            string label = _patchCreatorTargetMode == 1 ? "Wildcard file##patch-creator-file-pattern" : "Regex file##patch-creator-file-pattern";
            ImGui.InputText(label, ref _patchCreatorFilePattern, 512);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(_patchCreatorTargetMode == 1 ? "Example: @game:itemtypes/toolhead/*.json" : "Example: @@game:itemtypes/toolhead/.+.json");
            }
        }

        ImGui.SeparatorText("Operation");
        string[] ops = CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.JsonPatchesLib ? PatchCreatorJsonPatchesLibOps : PatchCreatorVanillaOps;
        _patchCreatorOpIndex = Math.Clamp(_patchCreatorOpIndex, 0, ops.Length - 1);
        ImGui.Combo("Op##patch-creator-op", ref _patchCreatorOpIndex, ops, ops.Length);
        ImGui.InputText("Path##patch-creator-path", ref _patchCreatorPath, 1024);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.JsonPatchesLib ? "JsonPatchesLib path without a leading slash." : "Enter without leading slash; vanilla output will add it.");
        }

        string op = ops[_patchCreatorOpIndex];
        if (PatchCreatorOpNeedsFromPath(op))
        {
            ImGui.InputText("From path##patch-creator-from-path", ref _patchCreatorFromPath, 1024);
        }

        if (PatchCreatorOpNeedsValue(op) || (op.Equals("remove", StringComparison.OrdinalIgnoreCase) && CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.JsonPatchesLib))
        {
            ImGui.TextUnformatted(op.Equals("expression", StringComparison.OrdinalIgnoreCase) ? "Expression" : "Value JSON");
            ImGui.InputTextMultiline("##patch-creator-value-json", ref _patchCreatorValueJson, 256 * 1024, new NVector2(-float.Epsilon, 92f), ImGuiInputTextFlags.AllowTabInput);
        }

        if (ImGui.CollapsingHeader("Advanced##patch-creator-advanced", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Checkbox("Enabled##patch-creator-enabled", ref _patchCreatorEnabled);
            ImGui.Combo("Side##patch-creator-side", ref _patchCreatorSideIndex, PatchCreatorSideLabels, PatchCreatorSideLabels.Length);
            ImGui.InputInt("Priority##patch-creator-priority", ref _patchCreatorPriority);
            if (CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.Vanilla)
            {
                DrawPatchCreatorConditionBuilder();
            }
            else
            {
                ImGui.TextWrapped("JsonPatchesLib output requires the consuming mod to depend on jsonpatcheslib. This tool does not edit modinfo.json.");
            }
        }

        bool canAdd = TryCreatePatchCreatorOperation(out PatchCreatorOperationDraft? draft, out string error);
        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button("Add operation##patch-creator-add-op"))
        {
            _patchCreatorOperations.Add(draft!);
            _patchCreatorOperationIndex = _patchCreatorOperations.Count - 1;
            _patchCreatorStatus = $"Added {draft!.Op} operation.";
        }
        if (!canAdd) ImGui.EndDisabled();
        if (!canAdd && !string.IsNullOrWhiteSpace(error))
        {
            ImGui.TextColored(new NVector4(1f, 0.45f, 0.30f, 1f), error);
        }

        if (_patchCreatorOperationIndex >= 0 && _patchCreatorOperationIndex < _patchCreatorOperations.Count)
        {
            ImGui.SameLine();
            if (!canAdd) ImGui.BeginDisabled();
            if (ImGui.Button("Update selected##patch-creator-update-op"))
            {
                _patchCreatorOperations[_patchCreatorOperationIndex] = draft!;
                _patchCreatorStatus = $"Updated operation {_patchCreatorOperationIndex}.";
            }
            if (!canAdd) ImGui.EndDisabled();
        }
    }

    private void DrawPatchCreatorConditionBuilder()
    {
        ImGui.SeparatorText("Condition");
        bool changed = false;
        _patchCreatorConditionMode = Math.Clamp(_patchCreatorConditionMode, 0, PatchCreatorConditionModeLabels.Length - 1);
        changed |= ImGui.Combo("Mode##patch-creator-condition-mode", ref _patchCreatorConditionMode, PatchCreatorConditionModeLabels, PatchCreatorConditionModeLabels.Length);

        if (_patchCreatorConditionMode > 0)
        {
            changed |= ImGui.InputText("When##patch-creator-condition-when", ref _patchCreatorConditionWhen, 160);
        }

        if (_patchCreatorConditionMode == 4)
        {
            ImGui.TextUnformatted("isValue JSON");
            changed |= ImGui.InputTextMultiline("##patch-creator-condition-is-value", ref _patchCreatorConditionValueJson, 64 * 1024, new NVector2(-float.Epsilon, 58f), ImGuiInputTextFlags.AllowTabInput);
        }

        if (changed)
        {
            if (TryBuildPatchCreatorConditionJson(out string conditionJson, out string error))
            {
                _patchCreatorConditionJson = conditionJson;
            }
            else
            {
                _patchCreatorStatus = error;
            }
        }

        if (ImGui.Button("Load builder from raw##patch-creator-condition-load"))
        {
            LoadPatchCreatorConditionBuilder(_patchCreatorConditionJson);
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear condition##patch-creator-condition-clear"))
        {
            _patchCreatorConditionMode = 0;
            _patchCreatorConditionWhen = "";
            _patchCreatorConditionValueJson = "true";
            _patchCreatorConditionJson = "";
        }

        if (ImGui.TreeNode("Raw condition JSON##patch-creator-condition-raw"))
        {
            ImGui.InputTextMultiline("##patch-creator-condition-json", ref _patchCreatorConditionJson, 64 * 1024, new NVector2(-float.Epsilon, 70f), ImGuiInputTextFlags.AllowTabInput);
            ImGui.TreePop();
        }
    }

    private bool TryBuildPatchCreatorConditionJson(out string conditionJson, out string error)
    {
        conditionJson = "";
        error = "";
        if (_patchCreatorConditionMode == 0)
        {
            return true;
        }

        string when = _patchCreatorConditionWhen.Trim();
        if (string.IsNullOrWhiteSpace(when))
        {
            error = "Condition 'when' setting key is required.";
            return false;
        }

        JObject condition = new()
        {
            ["when"] = when
        };

        switch (_patchCreatorConditionMode)
        {
            case 1:
                condition["useValue"] = true;
                break;
            case 2:
                condition["isValue"] = true;
                break;
            case 3:
                condition["isValue"] = false;
                break;
            case 4:
                if (!TryParsePatchCreatorJson(_patchCreatorConditionValueJson, out JToken? value, out string valueError) || value == null)
                {
                    error = $"Condition isValue JSON is invalid: {valueError}";
                    return false;
                }
                condition["isValue"] = value;
                break;
        }

        conditionJson = condition.ToString(Formatting.Indented);
        return true;
    }

    private void LoadPatchCreatorConditionBuilder(string conditionJson)
    {
        if (string.IsNullOrWhiteSpace(conditionJson))
        {
            _patchCreatorConditionMode = 0;
            _patchCreatorConditionWhen = "";
            _patchCreatorConditionValueJson = "true";
            return;
        }

        if (!TryParsePatchCreatorJson(conditionJson, out JToken? token, out _) || token is not JObject condition)
        {
            return;
        }

        _patchCreatorConditionWhen = condition["when"]?.ToString() ?? "";
        if (condition["useValue"] != null)
        {
            _patchCreatorConditionMode = 1;
            _patchCreatorConditionValueJson = "true";
            return;
        }

        JToken? isValue = condition["isValue"];
        if (isValue == null)
        {
            _patchCreatorConditionMode = 0;
            _patchCreatorConditionValueJson = "true";
            return;
        }

        if (isValue.Type == JTokenType.Boolean)
        {
            _patchCreatorConditionMode = isValue.Value<bool>() ? 2 : 3;
            _patchCreatorConditionValueJson = isValue.Value<bool>() ? "true" : "false";
            return;
        }

        _patchCreatorConditionMode = 4;
        _patchCreatorConditionValueJson = isValue.ToString(Formatting.Indented);
    }

    private void DrawPatchCreatorOperationList()
    {
        if (ImGui.BeginChild("##patch-creator-operation-list", new NVector2(-float.Epsilon, 120f), true))
        {
            for (int i = 0; i < _patchCreatorOperations.Count; i++)
            {
                PatchCreatorOperationDraft operation = _patchCreatorOperations[i];
                if (ImGui.Selectable($"{i}: {operation.Op} {operation.File} {operation.Path}##patch-op-{i}", i == _patchCreatorOperationIndex))
                {
                    _patchCreatorOperationIndex = i;
                    LoadPatchCreatorOperation(operation);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(operation.ToJson(CurrentPatchCreatorOutputFormat).ToString(Formatting.Indented));
                }
            }
        }
        ImGui.EndChild();

        bool hasSelection = _patchCreatorOperationIndex >= 0 && _patchCreatorOperationIndex < _patchCreatorOperations.Count;
        if (!hasSelection) ImGui.BeginDisabled();
        if (ImGui.Button("Duplicate##patch-creator-duplicate-op"))
        {
            _patchCreatorOperations.Insert(_patchCreatorOperationIndex + 1, _patchCreatorOperations[_patchCreatorOperationIndex].Clone());
            _patchCreatorOperationIndex++;
        }
        ImGui.SameLine();
        if (ImGui.Button("Remove##patch-creator-remove-op"))
        {
            _patchCreatorOperations.RemoveAt(_patchCreatorOperationIndex);
            _patchCreatorOperationIndex = Math.Clamp(_patchCreatorOperationIndex, -1, _patchCreatorOperations.Count - 1);
        }
        if (!hasSelection) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Clear##patch-creator-clear-ops"))
        {
            _patchCreatorOperations.Clear();
            _patchCreatorOperationIndex = -1;
        }
    }

    private void DrawPatchCreatorPreviewAndSave(bool showDiagnostics)
    {
        string patchPreview = BuildPatchCreatorPatchJson(CurrentPatchCreatorOutputFormat);
        List<string> issues = ValidatePatchCreatorOperations(CurrentPatchCreatorOutputFormat);
        bool hasBlockingIssues = issues.Any(issue => issue.StartsWith("Error:", StringComparison.OrdinalIgnoreCase));
        bool canSave = _patchCreatorOperations.Count > 0 && !hasBlockingIssues;

        if (issues.Count > 0)
        {
            foreach (string issue in issues.Take(6))
            {
                bool error = issue.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
                ImGui.TextColored(error ? new NVector4(1f, 0.32f, 0.22f, 1f) : new NVector4(1f, 0.78f, 0.35f, 1f), issue);
            }
        }
        else
        {
            ImGui.TextWrapped("Patch operations are valid for authored save.");
        }

        if (!TryPreviewApplyPatchCreatorOperations(out string appliedPreview, out string applyStatus))
        {
            ImGui.TextColored(new NVector4(1f, 0.45f, 0.30f, 1f), applyStatus);
        }
        else
        {
            ImGui.TextWrapped(applyStatus);
        }

        if (!canSave) ImGui.BeginDisabled();
        if (ImGui.Button("Save patch file##patch-creator-save", new NVector2(-1, 0)))
        {
            QueueSourceSave(TrySavePatchCreator(patchPreview), status => _patchCreatorStatus = status);
        }
        if (!canSave) ImGui.EndDisabled();

        ImGui.TextWrapped(_patchCreatorStatus);
        if (CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.JsonPatchesLib)
        {
            ImGui.TextWrapped("Dependency reminder: add jsonpatcheslib to the consuming mod's modinfo dependencies.");
        }

        if (ImGui.Button("Copy patch JSON##patch-creator-copy-patch"))
        {
            ImGui.SetClipboardText(patchPreview);
            _patchCreatorStatus = "Copied patch JSON.";
        }

        if (ImGui.CollapsingHeader("Optional diff helper##patch-creator-diff-helper"))
        {
            DrawPatchCreatorDiffHelper();
        }

        string[] previewModes = ["Patch file", "Applied sample"];
        _patchCreatorPreviewMode = string.IsNullOrWhiteSpace(appliedPreview) ? 0 : Math.Clamp(_patchCreatorPreviewMode, 0, previewModes.Length - 1);
        ImGui.Combo("Preview##patch-creator-preview-mode", ref _patchCreatorPreviewMode, previewModes, previewModes.Length);
        string previewText = _patchCreatorPreviewMode == 1 && !string.IsNullOrWhiteSpace(appliedPreview) ? appliedPreview : patchPreview;
        ImGui.InputTextMultiline("##patch-creator-preview-json", ref previewText, (uint)Math.Max(4096, previewText.Length + 1024), new NVector2(-float.Epsilon, Math.Max(150f, ImGui.GetContentRegionAvail().Y - 30f)), ImGuiInputTextFlags.ReadOnly);
        _patchCreatorDiagnostics.Draw("patch-creator", showDiagnostics);
    }

    private void DrawPatchCreatorDiffHelper()
    {
        PatchCreatorAssetEntry? entry = SelectedPatchCreatorAsset;
        if (entry?.Root == null)
        {
            ImGui.TextWrapped("Select a valid exact sample asset before generating operations from edited JSON.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_patchCreatorDiffJson))
        {
            _patchCreatorDiffJson = entry.Root.ToString(Formatting.Indented);
        }

        ImGui.TextWrapped("Edit or paste the desired final JSON. Generated operations are conservative add/remove/replace operations.");
        ImGui.InputTextMultiline("##patch-creator-diff-json", ref _patchCreatorDiffJson, 2 * 1024 * 1024, new NVector2(-float.Epsilon, 150f), ImGuiInputTextFlags.AllowTabInput);
        if (ImGui.Button("Reset to sample##patch-creator-diff-reset"))
        {
            _patchCreatorDiffJson = entry.Root.ToString(Formatting.Indented);
        }
        ImGui.SameLine();
        if (ImGui.Button("Generate operations##patch-creator-generate-diff"))
        {
            GeneratePatchCreatorDiffOperations(entry);
        }
        if (!string.IsNullOrWhiteSpace(_patchCreatorDiffStatus))
        {
            ImGui.TextWrapped(_patchCreatorDiffStatus);
        }
    }

    private void ApplyPatchCreatorTemplate()
    {
        PatchCreatorAssetEntry? entry = SelectedPatchCreatorAsset;
        _patchCreatorPath = _patchCreatorSelectedPath;
        _patchCreatorFromPath = _patchCreatorSelectedPath;

        switch (_patchCreatorTemplate)
        {
            case 0:
                SetPatchCreatorBuilderOp("replace");
                if (!string.IsNullOrWhiteSpace(_patchCreatorSelectedTokenJson)) _patchCreatorValueJson = _patchCreatorSelectedTokenJson;
                break;
            case 1:
                SetPatchCreatorBuilderOp("add");
                _patchCreatorPath = string.IsNullOrWhiteSpace(_patchCreatorSelectedPath) ? "-" : _patchCreatorSelectedPath.TrimEnd('/') + "/-";
                _patchCreatorValueJson = !string.IsNullOrWhiteSpace(_patchCreatorSelectedTokenJson) ? _patchCreatorSelectedTokenJson : "null";
                break;
            case 2:
                SetPatchCreatorBuilderOp("remove");
                _patchCreatorValueJson = "";
                break;
            case 3:
                SetPatchCreatorBuilderOp("test");
                if (!string.IsNullOrWhiteSpace(_patchCreatorSelectedTokenJson)) _patchCreatorValueJson = _patchCreatorSelectedTokenJson;
                break;
            case 4:
                SetPatchCreatorBuilderOp(CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.JsonPatchesLib ? "addmerge" : "add");
                _patchCreatorValueJson = "[]";
                break;
            case 5:
                SetPatchCreatorBuilderOp(CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.JsonPatchesLib ? "expression" : "replace");
                _patchCreatorValueJson = CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.JsonPatchesLib ? "value" : "0";
                break;
            case 6:
                SetPatchCreatorBuilderOp("copy");
                break;
            case 7:
                SetPatchCreatorBuilderOp("move");
                break;
        }

        if (entry != null && _patchCreatorTargetMode == 0)
        {
            _patchCreatorFilePattern = GetPatchCreatorBuilderFile(entry);
        }
    }

    private void SetPatchCreatorBuilderOp(string op)
    {
        string[] ops = CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.JsonPatchesLib ? PatchCreatorJsonPatchesLibOps : PatchCreatorVanillaOps;
        int index = Array.FindIndex(ops, candidate => candidate.Equals(op, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _patchCreatorOpIndex = index;
    }

    private bool TryCreatePatchCreatorOperation(out PatchCreatorOperationDraft? operation, out string error)
    {
        operation = null;
        error = "";
        string[] ops = CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.JsonPatchesLib ? PatchCreatorJsonPatchesLibOps : PatchCreatorVanillaOps;
        string op = ops[Math.Clamp(_patchCreatorOpIndex, 0, ops.Length - 1)];
        string file = GetPatchCreatorBuilderFile(SelectedPatchCreatorAsset);
        if (string.IsNullOrWhiteSpace(file))
        {
            error = "Target file is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_patchCreatorPath) && !op.Equals("replace", StringComparison.OrdinalIgnoreCase))
        {
            error = "Path is required.";
            return false;
        }

        if (PatchCreatorOpNeedsFromPath(op) && string.IsNullOrWhiteSpace(_patchCreatorFromPath))
        {
            error = "From path is required for copy/move.";
            return false;
        }

        string valueJson = _patchCreatorValueJson;
        if (PatchCreatorOpNeedsValue(op) && !op.Equals("expression", StringComparison.OrdinalIgnoreCase) && !TryParsePatchCreatorJson(valueJson, out _, out string valueError))
        {
            error = $"Value JSON is invalid: {valueError}";
            return false;
        }

        if (CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.Vanilla && !string.IsNullOrWhiteSpace(_patchCreatorConditionJson) && !TryParsePatchCreatorJson(_patchCreatorConditionJson, out _, out string conditionError))
        {
            error = $"Condition JSON is invalid: {conditionError}";
            return false;
        }

        operation = new PatchCreatorOperationDraft
        {
            Op = op,
            File = file,
            Path = NormalizePatchCreatorPath(_patchCreatorPath),
            FromPath = NormalizePatchCreatorPath(_patchCreatorFromPath),
            ValueJson = valueJson,
            ConditionJson = _patchCreatorConditionJson,
            Enabled = _patchCreatorEnabled,
            Side = PatchCreatorSideLabels[Math.Clamp(_patchCreatorSideIndex, 0, PatchCreatorSideLabels.Length - 1)],
            Priority = _patchCreatorPriority
        };
        return true;
    }

    private void LoadPatchCreatorOperation(PatchCreatorOperationDraft operation)
    {
        _patchCreatorFilePattern = operation.File;
        if (operation.File.StartsWith("@@", StringComparison.Ordinal))
        {
            _patchCreatorTargetMode = 2;
        }
        else if (operation.File.StartsWith("@", StringComparison.Ordinal))
        {
            _patchCreatorTargetMode = 1;
        }
        else
        {
            _patchCreatorTargetMode = 0;
        }

        SetPatchCreatorBuilderOp(operation.Op);
        _patchCreatorPath = operation.Path;
        _patchCreatorFromPath = operation.FromPath;
        _patchCreatorValueJson = operation.ValueJson;
        _patchCreatorConditionJson = operation.ConditionJson;
        LoadPatchCreatorConditionBuilder(_patchCreatorConditionJson);
        _patchCreatorEnabled = operation.Enabled;
        _patchCreatorPriority = operation.Priority;
        int sideIndex = Array.FindIndex(PatchCreatorSideLabels, side => side.Equals(operation.Side, StringComparison.OrdinalIgnoreCase));
        _patchCreatorSideIndex = Math.Max(0, sideIndex);
    }

    private string GetPatchCreatorBuilderFile(PatchCreatorAssetEntry? entry)
    {
        if (_patchCreatorTargetMode == 1)
        {
            return _patchCreatorFilePattern.StartsWith("@", StringComparison.Ordinal) ? _patchCreatorFilePattern : "@" + _patchCreatorFilePattern;
        }

        if (_patchCreatorTargetMode == 2)
        {
            return _patchCreatorFilePattern.StartsWith("@@", StringComparison.Ordinal) ? _patchCreatorFilePattern : "@@" + _patchCreatorFilePattern.TrimStart('@');
        }

        if (entry == null) return "";
        return CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.JsonPatchesLib
            ? $"{entry.Domain}:{entry.AssetPath}"
            : entry.AssetPath;
    }

    private string BuildPatchCreatorPatchJson(PatchCreatorOutputFormat format)
    {
        JArray array = [];
        foreach (PatchCreatorOperationDraft operation in _patchCreatorOperations)
        {
            array.Add(operation.ToJson(format));
        }

        return array.ToString(Formatting.Indented);
    }

    private List<string> ValidatePatchCreatorOperations(PatchCreatorOutputFormat format)
    {
        List<string> issues = [];
        if (_patchCreatorOperations.Count == 0)
        {
            issues.Add("Error: add at least one operation.");
            return issues;
        }

        foreach (PatchCreatorOperationDraft operation in _patchCreatorOperations)
        {
            if (format == PatchCreatorOutputFormat.Vanilla && !PatchCreatorVanillaOps.Any(op => op.Equals(operation.Op, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add($"Error: vanilla patches do not support '{operation.Op}'.");
            }

            if (string.IsNullOrWhiteSpace(operation.File))
            {
                issues.Add("Error: operation target file is empty.");
            }
            else if (operation.File.StartsWith("@", StringComparison.Ordinal))
            {
                int count = CountPatchCreatorMatchingAssets(operation.File);
                issues.Add(count == 0
                    ? $"Warning: '{operation.File}' matched no loaded sample assets."
                    : $"Warning: '{operation.File}' matched {count} loaded sample asset(s); wildcard/regex targets are not exhaustively validated.");
            }
            else if (!PatchCreatorExactTargetExists(operation.File))
            {
                issues.Add($"Error: target asset not found: {operation.File}");
            }

            if (PatchCreatorOpNeedsValue(operation.Op) && !operation.Op.Equals("expression", StringComparison.OrdinalIgnoreCase) && !TryParsePatchCreatorJson(operation.ValueJson, out _, out string error))
            {
                issues.Add($"Error: invalid value JSON for {operation.Op}: {error}");
            }
        }

        return issues;
    }

    private bool TryPreviewApplyPatchCreatorOperations(out string previewText, out string status)
    {
        previewText = "";
        PatchCreatorAssetEntry? entry = SelectedPatchCreatorAsset;
        if (entry?.Root == null)
        {
            status = "No valid sample asset selected for preview.";
            return false;
        }

        JToken working = entry.Root.DeepClone();
        int applied = 0;
        foreach (PatchCreatorOperationDraft operation in _patchCreatorOperations)
        {
            if (!PatchCreatorOperationAppliesToSample(operation, entry)) continue;
            if (!TryApplyPatchCreatorOperation(ref working, operation, out string error))
            {
                status = $"Preview failed at {operation.Op} {operation.Path}: {error}";
                return false;
            }
            applied++;
        }

        previewText = working.ToString(Formatting.Indented);
        status = applied == 0
            ? "Preview applied 0 operation(s) to the selected sample asset."
            : $"Preview applied {applied} operation(s) to sample {entry.Domain}:{entry.AssetPath}.";
        return true;
    }

    private bool PatchCreatorOperationAppliesToSample(PatchCreatorOperationDraft operation, PatchCreatorAssetEntry entry)
    {
        string exactJpl = $"{entry.Domain}:{entry.AssetPath}";
        if (operation.File.Equals(exactJpl, StringComparison.OrdinalIgnoreCase) ||
            operation.File.Equals(entry.AssetPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (operation.File.StartsWith("@@", StringComparison.Ordinal))
        {
            string pattern = operation.File[2..];
            return Regex.IsMatch(exactJpl, pattern, RegexOptions.IgnoreCase) || Regex.IsMatch(entry.AssetPath, pattern, RegexOptions.IgnoreCase);
        }

        if (operation.File.StartsWith("@", StringComparison.Ordinal))
        {
            return WildcardPatchCreatorMatches(operation.File[1..], exactJpl) || WildcardPatchCreatorMatches(operation.File[1..], entry.AssetPath);
        }

        return false;
    }

    private bool TryApplyPatchCreatorOperation(ref JToken root, PatchCreatorOperationDraft operation, out string error)
    {
        error = "";
        string op = operation.Op.ToLowerInvariant();
        try
        {
            switch (op)
            {
                case "add":
                    return TrySetPatchCreatorToken(ref root, operation.Path, ParsePatchCreatorValue(operation), add: true, out error);
                case "replace":
                    return TrySetPatchCreatorToken(ref root, operation.Path, ParsePatchCreatorValue(operation), add: false, out error);
                case "remove":
                    return TryRemovePatchCreatorToken(ref root, operation.Path, TryParsePatchCreatorJson(operation.ValueJson, out JToken? removeValue, out _) ? removeValue : null, out error);
                case "copy":
                    if (!TryGetPatchCreatorToken(root, operation.FromPath, out JToken? copyToken, out error)) return false;
                    return TrySetPatchCreatorToken(ref root, operation.Path, copyToken.DeepClone(), add: true, out error);
                case "move":
                    if (!TryGetPatchCreatorToken(root, operation.FromPath, out JToken? moveToken, out error)) return false;
                    if (!TrySetPatchCreatorToken(ref root, operation.Path, moveToken.DeepClone(), add: true, out error)) return false;
                    return TryRemovePatchCreatorToken(ref root, operation.FromPath, null, out error);
                case "test":
                    if (!TryGetPatchCreatorToken(root, operation.Path, out JToken? testToken, out error)) return false;
                    JToken expected = ParsePatchCreatorValue(operation);
                    if (JToken.DeepEquals(testToken, expected)) return true;
                    error = $"Test failed. Expected {expected.ToString(Formatting.None)}, found {testToken.ToString(Formatting.None)}.";
                    return false;
                case "addmerge":
                    return TryAddMergePatchCreatorToken(ref root, operation.Path, ParsePatchCreatorValue(operation), out error);
                case "addeach":
                    return TryAddEachPatchCreatorToken(ref root, operation.Path, ParsePatchCreatorValue(operation), out error);
                case "expression":
                    return TryApplyPatchCreatorExpression(ref root, operation.Path, operation.ValueJson, out error);
                default:
                    error = $"Unsupported preview operation '{operation.Op}'.";
                    return false;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static JToken ParsePatchCreatorValue(PatchCreatorOperationDraft operation)
    {
        if (operation.Op.Equals("expression", StringComparison.OrdinalIgnoreCase)) return new JValue(operation.ValueJson);
        if (TryParsePatchCreatorJson(operation.ValueJson, out JToken? token, out _) && token != null) return token;
        return new JValue(operation.ValueJson);
    }

    private static bool TrySetPatchCreatorToken(ref JToken root, string path, JToken value, bool add, out string error)
    {
        error = "";
        string[] parts = SplitPatchCreatorPath(path);
        if (parts.Length == 0)
        {
            root = value;
            return true;
        }

        if (!TryResolvePatchCreatorParent(root, parts, out JToken? parent, out string last, out error)) return false;

        if (parent is JObject obj)
        {
            if (!add && obj[last] == null)
            {
                error = $"Object property '{last}' does not exist.";
                return false;
            }
            obj[last] = value;
            return true;
        }

        if (parent is JArray array)
        {
            if (last == "-")
            {
                array.Add(value);
                return true;
            }

            if (!int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                error = $"Invalid array index '{last}'.";
                return false;
            }

            if (add && index == array.Count)
            {
                array.Add(value);
                return true;
            }

            if (index < 0 || index >= array.Count)
            {
                error = $"Array index {index} out of range.";
                return false;
            }

            array[index] = value;
            return true;
        }

        error = "Parent is not an object or array.";
        return false;
    }

    private static bool TryRemovePatchCreatorToken(ref JToken root, string path, JToken? value, out string error)
    {
        error = "";
        string[] parts = SplitPatchCreatorPath(path);
        if (parts.Length == 0)
        {
            root = JValue.CreateNull();
            return true;
        }

        if (!TryResolvePatchCreatorParent(root, parts, out JToken? parent, out string last, out error)) return false;
        if (parent is JObject obj)
        {
            return obj.Remove(last);
        }

        if (parent is JArray array)
        {
            if (last == "-" && value != null)
            {
                bool removed = false;
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    if (JToken.DeepEquals(array[i], value))
                    {
                        array.RemoveAt(i);
                        removed = true;
                    }
                }
                return removed;
            }

            if (int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index >= 0 && index < array.Count)
            {
                array.RemoveAt(index);
                return true;
            }
        }

        error = "Remove target not found.";
        return false;
    }

    private static bool TryAddMergePatchCreatorToken(ref JToken root, string path, JToken value, out string error)
    {
        if (!TryGetPatchCreatorToken(root, path, out JToken? target, out error)) return false;
        if (target is JArray targetArray && value is JArray sourceArray)
        {
            foreach (JToken item in sourceArray)
            {
                if (!targetArray.Any(existing => JToken.DeepEquals(existing, item)))
                {
                    targetArray.Add(item.DeepClone());
                }
            }
            return true;
        }

        if (target is JObject targetObj && value is JObject sourceObj)
        {
            targetObj.Merge(sourceObj, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Union });
            return true;
        }

        error = "addmerge preview supports array or object targets.";
        return false;
    }

    private static bool TryAddEachPatchCreatorToken(ref JToken root, string path, JToken value, out string error)
    {
        if (!TryGetPatchCreatorToken(root, path, out JToken? target, out error)) return false;
        if (target is not JArray targetArray)
        {
            error = "addeach target must be an array.";
            return false;
        }

        if (value is JArray sourceArray)
        {
            foreach (JToken item in sourceArray)
            {
                targetArray.Add(item.DeepClone());
            }
            return true;
        }

        targetArray.Add(value.DeepClone());
        return true;
    }

    private static bool TryApplyPatchCreatorExpression(ref JToken root, string path, string expression, out string error)
    {
        if (!TryGetPatchCreatorToken(root, path, out JToken? target, out error)) return false;
        if (target.Type != JTokenType.Integer && target.Type != JTokenType.Float)
        {
            error = "Expression preview supports numeric targets only.";
            return false;
        }

        string value = target.Value<double>().ToString(CultureInfo.InvariantCulture);
        string formula = Regex.Replace(expression, @"\bvalue\b", value, RegexOptions.IgnoreCase);
        object result = new DataTable().Compute(formula, "");
        double parsed = Convert.ToDouble(result, CultureInfo.InvariantCulture);
        return TrySetPatchCreatorToken(ref root, path, new JValue(parsed), add: false, out error);
    }

    private static bool TryGetPatchCreatorToken(JToken root, string path, out JToken token, out string error)
    {
        error = "";
        token = root;
        foreach (string part in SplitPatchCreatorPath(path))
        {
            if (token is JObject obj)
            {
                JToken? child = obj[part];
                if (child == null)
                {
                    error = $"Property '{part}' not found.";
                    return false;
                }
                token = child;
            }
            else if (token is JArray array)
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= array.Count)
                {
                    error = $"Array index '{part}' not found.";
                    return false;
                }
                token = array[index];
            }
            else
            {
                error = $"Cannot traverse through {token.Type}.";
                return false;
            }
        }
        return true;
    }

    private static bool TryResolvePatchCreatorParent(JToken root, string[] parts, out JToken? parent, out string last, out string error)
    {
        parent = null;
        last = parts.Length == 0 ? "" : parts[^1];
        error = "";
        JToken current = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (current is JObject obj)
            {
                JToken? next = obj[parts[i]];
                if (next == null)
                {
                    error = $"Property '{parts[i]}' not found.";
                    return false;
                }
                current = next;
            }
            else if (current is JArray array)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= array.Count)
                {
                    error = $"Array index '{parts[i]}' not found.";
                    return false;
                }
                current = array[index];
            }
            else
            {
                error = $"Cannot traverse through {current.Type}.";
                return false;
            }
        }

        parent = current;
        return true;
    }

    private void GeneratePatchCreatorDiffOperations(PatchCreatorAssetEntry entry)
    {
        if (!TryParsePatchCreatorJson(_patchCreatorDiffJson, out JToken? modified, out string error) || modified == null)
        {
            _patchCreatorDiffStatus = $"Edited JSON parse failed: {error}";
            return;
        }

        int before = _patchCreatorOperations.Count;
        AddPatchCreatorDiffOperations(entry, entry.Root!, modified, "");
        int added = _patchCreatorOperations.Count - before;
        _patchCreatorDiffStatus = $"Generated {added} operation(s).";
    }

    private void AddPatchCreatorDiffOperations(PatchCreatorAssetEntry entry, JToken original, JToken modified, string path)
    {
        if (JToken.DeepEquals(original, modified)) return;

        if (original is JObject originalObj && modified is JObject modifiedObj)
        {
            foreach (JProperty property in originalObj.Properties())
            {
                if (modifiedObj[property.Name] == null)
                {
                    _patchCreatorOperations.Add(BuildPatchCreatorDiffOperation(entry, "remove", JoinPatchCreatorPath(path, property.Name), null));
                }
            }

            foreach (JProperty property in modifiedObj.Properties())
            {
                string childPath = JoinPatchCreatorPath(path, property.Name);
                JToken? originalChild = originalObj[property.Name];
                if (originalChild == null)
                {
                    _patchCreatorOperations.Add(BuildPatchCreatorDiffOperation(entry, "add", childPath, property.Value));
                }
                else
                {
                    AddPatchCreatorDiffOperations(entry, originalChild, property.Value, childPath);
                }
            }
            return;
        }

        if (original is JArray && modified is JArray)
        {
            _patchCreatorOperations.Add(BuildPatchCreatorDiffOperation(entry, "replace", path, modified));
            return;
        }

        _patchCreatorOperations.Add(BuildPatchCreatorDiffOperation(entry, "replace", path, modified));
    }

    private PatchCreatorOperationDraft BuildPatchCreatorDiffOperation(PatchCreatorAssetEntry entry, string op, string path, JToken? value)
    {
        return new PatchCreatorOperationDraft
        {
            Op = op,
            File = CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.JsonPatchesLib ? $"{entry.Domain}:{entry.AssetPath}" : entry.AssetPath,
            Path = path,
            FromPath = "",
            ValueJson = value == null ? "" : value.ToString(Formatting.Indented),
            ConditionJson = "",
            Enabled = true,
            Side = "Server",
            Priority = 0
        };
    }

    private SourceSaveResult TrySavePatchCreator(string newText)
    {
        try
        {
            string targetDomain = SanitizePathSegment(string.IsNullOrWhiteSpace(_patchCreatorOutputDomain) ? "ingamedevtools" : _patchCreatorOutputDomain.Trim());
            string patchName = EnsureJsonFilePath(SanitizePathSegment(string.IsNullOrWhiteSpace(_patchCreatorPatchName) ? "generated-patch" : _patchCreatorPatchName.Trim()));
            string folder = CurrentPatchCreatorOutputFormat == PatchCreatorOutputFormat.JsonPatchesLib ? "jsonpatches" : "patches";
            string outputPath = GetToolAuthoredAssetPath("patches", Path.Combine("assets", targetDomain, folder, patchName));
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : "";
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved patch file to {outputPath}.",
                () => WriteAuthoredFile(outputPath, newText));
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            return SourceSaveResult.Fail($"Patch save failed: {exception.Message}");
        }
    }

    private bool PatchCreatorExactTargetExists(string file)
    {
        return _patchCreatorAssets.Any(entry =>
            file.Equals(entry.AssetPath, StringComparison.OrdinalIgnoreCase) ||
            file.Equals($"{entry.Domain}:{entry.AssetPath}", StringComparison.OrdinalIgnoreCase));
    }

    private int CountPatchCreatorMatchingAssets(string file)
    {
        return _patchCreatorAssets.Count(entry =>
        {
            string full = $"{entry.Domain}:{entry.AssetPath}";
            if (file.StartsWith("@@", StringComparison.Ordinal))
            {
                string pattern = file[2..];
                return Regex.IsMatch(full, pattern, RegexOptions.IgnoreCase) || Regex.IsMatch(entry.AssetPath, pattern, RegexOptions.IgnoreCase);
            }
            if (file.StartsWith("@", StringComparison.Ordinal))
            {
                string pattern = file[1..];
                return WildcardPatchCreatorMatches(pattern, full) || WildcardPatchCreatorMatches(pattern, entry.AssetPath);
            }
            return file.Equals(full, StringComparison.OrdinalIgnoreCase) || file.Equals(entry.AssetPath, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool WildcardPatchCreatorMatches(string pattern, string value)
    {
        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private void SyncPatchCreatorSelection()
    {
        PatchCreatorAssetEntry? entry = SelectedPatchCreatorAsset;
        if (entry?.Root == null) return;
        _patchCreatorSelectedPath = "";
        _patchCreatorSelectedTokenJson = entry.Root.ToString(Formatting.Indented);
        _patchCreatorDiffJson = entry.Root.ToString(Formatting.Indented);
        if (_patchCreatorTargetMode == 0)
        {
            _patchCreatorFilePattern = GetPatchCreatorBuilderFile(entry);
        }
    }

    private PatchCreatorOutputFormat CurrentPatchCreatorOutputFormat => _patchCreatorOutputFormat == 1 ? PatchCreatorOutputFormat.Vanilla : PatchCreatorOutputFormat.JsonPatchesLib;

    private static bool PatchCreatorOpNeedsFromPath(string op) => op.Equals("copy", StringComparison.OrdinalIgnoreCase) || op.Equals("move", StringComparison.OrdinalIgnoreCase);

    private static bool PatchCreatorOpNeedsValue(string op) =>
        op.Equals("add", StringComparison.OrdinalIgnoreCase) ||
        op.Equals("replace", StringComparison.OrdinalIgnoreCase) ||
        op.Equals("test", StringComparison.OrdinalIgnoreCase) ||
        op.Equals("addmerge", StringComparison.OrdinalIgnoreCase) ||
        op.Equals("addeach", StringComparison.OrdinalIgnoreCase) ||
        op.Equals("expression", StringComparison.OrdinalIgnoreCase);

    private static string JoinPatchCreatorPath(string basePath, string part)
    {
        string normalized = part.Replace("~", "~0").Replace("/", "~1");
        return string.IsNullOrWhiteSpace(basePath) ? normalized : $"{basePath}/{normalized}";
    }

    private static string NormalizePatchCreatorPath(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? "" : path.Trim().TrimStart('/');
    }

    private static string[] SplitPatchCreatorPath(string path)
    {
        string normalized = NormalizePatchCreatorPath(path);
        if (string.IsNullOrWhiteSpace(normalized)) return [];
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Replace("~1", "/").Replace("~0", "~"))
            .ToArray();
    }

    private static string FormatPatchCreatorOutputPath(string path, PatchCreatorOutputFormat format)
    {
        string normalized = NormalizePatchCreatorPath(path);
        return format == PatchCreatorOutputFormat.Vanilla ? "/" + normalized : normalized;
    }

    private static string TrimPatchCreatorPreview(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static bool TryParsePatchCreatorJson(string text, out JToken? token, out string error)
    {
        return DevToolsJson.TryParseToken(text, out token, out error);
    }

    private enum PatchCreatorOutputFormat
    {
        JsonPatchesLib,
        Vanilla
    }

    private sealed class PatchCreatorAssetEntry
    {
        public PatchCreatorAssetEntry(IAsset asset, string sourceText, JToken? root, string parseError)
        {
            SourceText = sourceText;
            Root = root;
            ParseError = parseError;
            Domain = asset.Location.Domain ?? "game";
            AssetPath = asset.Location.Path.Replace('\\', '/');
            Category = GetPatchCreatorCategory(AssetPath);
            Key = asset.Location.ToString();
        }

        public string SourceText { get; }
        public JToken? Root { get; }
        public string ParseError { get; }
        public string Domain { get; }
        public string AssetPath { get; }
        public string Category { get; }
        public string Key { get; }
        public string SortKey => $"{Category}:{Domain}:{AssetPath}";
        public string SearchText => $"{Domain}:{AssetPath} {Category} {SourceText}";

        private static string GetPatchCreatorCategory(string assetPath)
        {
            string path = assetPath.Replace('\\', '/');
            int slash = path.IndexOf('/');
            return slash > 0 ? path[..slash] : "root";
        }
    }

    private sealed class PatchCreatorOperationDraft
    {
        public string Op { get; set; } = "replace";
        public string File { get; set; } = "";
        public string Path { get; set; } = "";
        public string FromPath { get; set; } = "";
        public string ValueJson { get; set; } = "null";
        public string ConditionJson { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Side { get; set; } = "Server";
        public int Priority { get; set; }

        public JObject ToJson(PatchCreatorOutputFormat format)
        {
            JObject json = new()
            {
                ["op"] = Op,
                ["file"] = File,
                ["path"] = FormatPatchCreatorOutputPath(Path, format)
            };

            if (format == PatchCreatorOutputFormat.JsonPatchesLib)
            {
                json["enabled"] = Enabled;
                json["side"] = Side.ToLowerInvariant();
                if (Priority != 0) json["priority"] = Priority;
                if (!string.IsNullOrWhiteSpace(FromPath)) json["frompath"] = FormatPatchCreatorOutputPath(FromPath, format);
            }
            else
            {
                json["side"] = Side;
                if (!string.IsNullOrWhiteSpace(FromPath)) json["from"] = FormatPatchCreatorOutputPath(FromPath, format);
                if (!string.IsNullOrWhiteSpace(ConditionJson) && TryParsePatchCreatorJson(ConditionJson, out JToken? condition, out _) && condition != null)
                {
                    json["condition"] = condition;
                }
            }

            bool removeHasSpecificValue = Op.Equals("remove", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(ValueJson) &&
                !ValueJson.Trim().Equals("null", StringComparison.OrdinalIgnoreCase);
            if (PatchCreatorOpNeedsValue(Op) || removeHasSpecificValue)
            {
                if (Op.Equals("expression", StringComparison.OrdinalIgnoreCase))
                {
                    json["value"] = ValueJson;
                }
                else if (TryParsePatchCreatorJson(ValueJson, out JToken? value, out _) && value != null)
                {
                    json["value"] = value;
                }
                else
                {
                    json["value"] = ValueJson;
                }
            }

            return json;
        }

        public PatchCreatorOperationDraft Clone()
        {
            return new PatchCreatorOperationDraft
            {
                Op = Op,
                File = File,
                Path = Path,
                FromPath = FromPath,
                ValueJson = ValueJson,
                ConditionJson = ConditionJson,
                Enabled = Enabled,
                Side = Side,
                Priority = Priority
            };
        }
    }
}
