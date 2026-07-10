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
    private const int PatchCreatorSourcePreviewCharacterLimit = 64 * 1024;

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
    private static readonly string[] PatchCreatorBrowserModeLabels = ["Target JSON assets", "Patch files"];
    private static readonly string[] PatchCreatorPreviewSideLabels = ["Server", "Client", "Universal"];

    private readonly List<PatchCreatorAssetEntry> _patchCreatorAssets = [];
    private readonly List<PatchCreatorAssetEntry> _visiblePatchCreatorAssets = [];
    private readonly List<DevToolsPatchOperationDraft> _patchCreatorOperations = [];
    private readonly ImGuiThreePanelLayoutState _patchCreatorLayout = new(0.25f, 0.34f);
    private readonly DevToolsEditorDiagnostics _patchCreatorDiagnostics = new("Patches");
    private readonly DevToolsAssetIndexer _patchCreatorIndexer = new(batchSize: 120);
    private int _patchCreatorBrowserMode;
    private string _patchCreatorFilter = "";
    private string _patchCreatorDomainFilter = "";
    private string _patchCreatorCategoryFilter = "";
    private string[] _patchCreatorDomainOptions = ["All domains"];
    private string[] _patchCreatorCategoryOptions = ["All categories"];
    private int _patchCreatorAssetIndex;
    private int _patchCreatorOutputFormat;
    private int _patchCreatorTargetMode;
    private int _patchCreatorTemplate;
    private int _patchCreatorOperationIndex = -1;
    private int _patchCreatorOpIndex;
    private int _patchCreatorSideIndex;
    private string _patchCreatorOutputDomain = "ingamedevtools";
    private string _patchCreatorPatchName = "generated-patch.json";
    private string _patchCreatorFilePattern = "";
    private string _patchCreatorPath = "";
    private string _patchCreatorFromPath = "";
    private string _patchCreatorValueJson = "null";
    private string _patchCreatorConditionJson = "";
    private string _patchCreatorConditionWhen = "";
    private string _patchCreatorConditionValueJson = "true";
    private string _patchCreatorDependsOnJson = "";
    private string _patchCreatorExtraJson = "{}";
    private string _patchCreatorRawOperationJson = "";
    private string _patchCreatorPreviewSettingsJson = "{\n}";
    private bool _patchCreatorEnabled = true;
    private int _patchCreatorPriority;
    private int _patchCreatorConditionMode;
    private int _patchCreatorPreviewMode;
    private int _patchCreatorPreviewSideIndex;
    private string _patchCreatorStatus = "Patch creator ready.";
    private string _patchCreatorSelectedPath = "";
    private string _patchCreatorSelectedTokenJson = "";
    private string _patchCreatorDiffJson = "";
    private string _patchCreatorDiffStatus = "";
    private string _patchCreatorSampleAssetKey = "";
    private string _patchCreatorLoadedPatchKey = "";
    private bool _patchCreatorDocumentDirty;
    private string _patchCreatorPendingDocumentAction = "";
    private string _patchCreatorPendingPatchImportKey = "";
    private bool _patchCreatorOpenDiscardPopup;

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
            DrawPatchCreatorDiscardPopup();
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

    private void RequestPatchCreatorNewDocument()
    {
        if (_patchCreatorDocumentDirty)
        {
            _patchCreatorPendingDocumentAction = "new";
            _patchCreatorOpenDiscardPopup = true;
            return;
        }

        ExecutePatchCreatorNewDocument();
    }

    private void RequestPatchCreatorClearDocument()
    {
        if (_patchCreatorDocumentDirty)
        {
            _patchCreatorPendingDocumentAction = "clear";
            _patchCreatorOpenDiscardPopup = true;
            return;
        }

        ExecutePatchCreatorClearDocument();
    }

    private void RequestPatchCreatorImport(PatchCreatorAssetEntry entry)
    {
        if (_patchCreatorDocumentDirty)
        {
            _patchCreatorPendingDocumentAction = "import";
            _patchCreatorPendingPatchImportKey = entry.Key;
            _patchCreatorOpenDiscardPopup = true;
            return;
        }

        ExecutePatchCreatorImport(entry);
    }

    private void DrawPatchCreatorDiscardPopup()
    {
        const string popupId = "Discard patch document changes?";
        if (_patchCreatorOpenDiscardPopup)
        {
            ImGui.OpenPopup(popupId);
            _patchCreatorOpenDiscardPopup = false;
        }

        bool open = true;
        if (!ImGui.BeginPopupModal(popupId, ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped("The current patch document has unsaved changes.");
        ImGui.TextWrapped("Discard them and continue?");
        if (ImGui.Button("Discard changes##patch-discard-yes"))
        {
            ExecutePendingPatchCreatorDocumentAction();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep editing##patch-discard-no"))
        {
            _patchCreatorPendingDocumentAction = "";
            _patchCreatorPendingPatchImportKey = "";
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void ExecutePendingPatchCreatorDocumentAction()
    {
        string action = _patchCreatorPendingDocumentAction;
        string importKey = _patchCreatorPendingPatchImportKey;
        _patchCreatorPendingDocumentAction = "";
        _patchCreatorPendingPatchImportKey = "";

        if (action.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            ExecutePatchCreatorNewDocument();
            return;
        }

        if (action.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            ExecutePatchCreatorClearDocument();
            return;
        }

        if (action.Equals("import", StringComparison.OrdinalIgnoreCase))
        {
            PatchCreatorAssetEntry? entry = _patchCreatorAssets.FirstOrDefault(asset => asset.Key.Equals(importKey, StringComparison.OrdinalIgnoreCase));
            if (entry != null) ExecutePatchCreatorImport(entry);
        }
    }

    private void ExecutePatchCreatorNewDocument()
    {
        _patchCreatorOperations.Clear();
        _patchCreatorOperationIndex = -1;
        _patchCreatorLoadedPatchKey = "";
        _patchCreatorOutputDomain = "ingamedevtools";
        _patchCreatorPatchName = "generated-patch.json";
        _patchCreatorOutputFormat = 0;
        _patchCreatorDocumentDirty = false;
        _patchCreatorStatus = "Started a new patch document.";
    }

    private void ExecutePatchCreatorClearDocument()
    {
        _patchCreatorOperations.Clear();
        _patchCreatorOperationIndex = -1;
        _patchCreatorLoadedPatchKey = "";
        _patchCreatorDocumentDirty = true;
        _patchCreatorStatus = "Cleared patch document.";
    }

    private void ExecutePatchCreatorImport(PatchCreatorAssetEntry entry)
    {
        if (!EnsurePatchCreatorAssetLoaded(entry) || entry.Root == null)
        {
            _patchCreatorStatus = $"Cannot import invalid patch JSON: {entry.ParseError}";
            return;
        }

        try
        {
            DevToolsPatchOutputFormat format = DevToolsPatchDocumentDraft.InferFormatFromAssetPath(entry.AssetPath);
            DevToolsPatchDocumentDraft document = DevToolsPatchDocumentDraft.FromJson(
                entry.SourceText,
                format,
                entry.Domain,
                DevToolsPatchDocumentDraft.ExtractRelativePatchPath(entry.AssetPath));
            _patchCreatorOperations.Clear();
            _patchCreatorOperations.AddRange(document.Operations);
            _patchCreatorOperationIndex = _patchCreatorOperations.Count > 0 ? 0 : -1;
            _patchCreatorOutputFormat = document.Format == DevToolsPatchOutputFormat.Vanilla ? 1 : 0;
            _patchCreatorOutputDomain = document.Domain;
            _patchCreatorPatchName = document.RelativePath;
            _patchCreatorLoadedPatchKey = entry.Key;
            _patchCreatorDocumentDirty = false;
            _patchCreatorStatus = $"Imported {entry.Domain}:{entry.AssetPath}.";
            if (_patchCreatorOperationIndex >= 0)
            {
                LoadPatchCreatorOperation(_patchCreatorOperations[_patchCreatorOperationIndex]);
            }
        }
        catch (Exception exception)
        {
            _patchCreatorStatus = $"Patch import failed: {exception.Message}";
            _patchCreatorDiagnostics.Exception("Patch import failed", exception);
        }
    }

    private void MarkPatchCreatorDocumentDirty()
    {
        _patchCreatorDocumentDirty = true;
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
        _patchCreatorDomainOptions = ["All domains"];
        _patchCreatorCategoryOptions = ["All categories"];
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
        _patchCreatorAssets.Add(new PatchCreatorAssetEntry(asset));
    }

    private bool EnsurePatchCreatorAssetLoaded(PatchCreatorAssetEntry entry)
    {
        if (entry.PayloadLoaded) return true;
        if (entry.JsonState == PatchCreatorJsonState.Invalid) return false;

        bool wasLoaded = entry.Asset.IsLoaded();
        try
        {
            if (!wasLoaded && !entry.Asset.Origin.TryLoadAsset(entry.Asset))
            {
                throw new InvalidOperationException("The asset origin could not load the selected file.");
            }

            string sourceText = ReadAssetText(entry.Asset);
            TryParsePatchCreatorJson(sourceText, out JToken? root, out string parseError);
            entry.SetPayload(sourceText, root, parseError);
            return root != null;
        }
        catch (Exception exception)
        {
            entry.SetPayload("", null, exception.Message);
            _patchCreatorDiagnostics.Exception($"Could not load {entry.Key}", exception);
            return false;
        }
        finally
        {
            if (!wasLoaded && !entry.Asset.IsPatched)
            {
                entry.Asset.Data = null!;
            }
        }
    }

    private void ReleasePatchCreatorPayloadsExcept(PatchCreatorAssetEntry selected, string? retainedKey = null)
    {
        foreach (PatchCreatorAssetEntry entry in _patchCreatorAssets)
        {
            if (ReferenceEquals(entry, selected) ||
                (!string.IsNullOrWhiteSpace(retainedKey) && entry.Key.Equals(retainedKey, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            entry.ReleasePayload();
        }
    }

    private void CompletePatchCreatorIndexing()
    {
        _patchCreatorAssets.Sort((left, right) => string.Compare(left.SortKey, right.SortKey, StringComparison.OrdinalIgnoreCase));
        _patchCreatorDomainOptions = _patchCreatorAssets
            .Select(entry => entry.Domain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain.Equals("game", StringComparison.OrdinalIgnoreCase) ? "" : domain, StringComparer.OrdinalIgnoreCase)
            .Prepend("All domains")
            .ToArray();
        _patchCreatorCategoryOptions = _patchCreatorAssets
            .Select(entry => entry.Category)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .Prepend("All categories")
            .ToArray();
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
            if (_patchCreatorBrowserMode == 0 && entry.IsPatchFile) continue;
            if (_patchCreatorBrowserMode == 1 && !entry.IsPatchFile) continue;
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

    private PatchCreatorAssetEntry? SelectedPatchCreatorSampleAsset
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_patchCreatorSampleAssetKey))
            {
                PatchCreatorAssetEntry? remembered = _patchCreatorAssets.FirstOrDefault(entry =>
                    entry.Key.Equals(_patchCreatorSampleAssetKey, StringComparison.OrdinalIgnoreCase) && !entry.IsPatchFile);
                if (remembered != null) return remembered;
            }

            PatchCreatorAssetEntry? selected = SelectedPatchCreatorAsset;
            return selected?.IsPatchFile == false ? selected : null;
        }
    }

    private void DrawPatchCreatorAssetBrowser(NVector2 size)
    {
        ImGui.BeginChild("##patch-creator-browser", size, true);
        ImGui.SeparatorText("Assets");

        if (ImGui.Button("Reload index##patch-creator-reload", new NVector2(-1, 0)))
        {
            StartPatchCreatorIndexing();
        }

        bool changed = false;
        changed |= ImGui.Combo("Browse##patch-creator-browser-mode", ref _patchCreatorBrowserMode, PatchCreatorBrowserModeLabels, PatchCreatorBrowserModeLabels.Length);
        changed |= ImGui.InputText("Filter##patch-creator-filter", ref _patchCreatorFilter, 256);
        changed |= DrawPatchCreatorDomainCombo();
        changed |= DrawPatchCreatorCategoryCombo();
        if (changed)
        {
            _patchCreatorBrowserMode = Math.Clamp(_patchCreatorBrowserMode, 0, PatchCreatorBrowserModeLabels.Length - 1);
            RebuildVisiblePatchCreatorAssets();
        }

        ImGui.TextDisabled($"{_visiblePatchCreatorAssets.Count}/{_patchCreatorAssets.Count}");
        if (_patchCreatorIndexer.IsIndexing)
        {
            ImGui.TextWrapped(_patchCreatorStatus);
        }

        if (ImGui.BeginChild("##patch-creator-asset-list", new NVector2(-float.Epsilon, -float.Epsilon), true))
        {
            DrawClippedPatchCreatorAssetRows();
        }
        ImGui.EndChild();
        ImGui.EndChild();
    }

    private void DrawClippedPatchCreatorAssetRows()
    {
        float rowHeight = Math.Max(1f, ImGui.GetTextLineHeightWithSpacing());
        float visibleHeight = Math.Max(rowHeight, ImGui.GetContentRegionAvail().Y);
        float scrollY = Math.Max(0f, ImGui.GetScrollY());
        int first = Math.Clamp((int)Math.Floor(scrollY / rowHeight) - 2, 0, _visiblePatchCreatorAssets.Count);
        int visibleCount = Math.Max(1, (int)Math.Ceiling(visibleHeight / rowHeight) + 5);
        int last = Math.Clamp(first + visibleCount, first, _visiblePatchCreatorAssets.Count);

        if (first > 0)
        {
            ImGui.Dummy(new NVector2(1f, first * rowHeight));
        }

        for (int i = first; i < last; i++)
        {
            PatchCreatorAssetEntry entry = _visiblePatchCreatorAssets[i];
            string suffix = entry.JsonState == PatchCreatorJsonState.Invalid ? " !" : "";
            if (entry.Authored) suffix += " [authored]";
            if (ImGui.Selectable($"{entry.Domain}:{entry.AssetPath}{suffix}##patch-creator-asset-{i}", i == _patchCreatorAssetIndex))
            {
                _patchCreatorAssetIndex = i;
                SyncPatchCreatorSelection();
            }
            if (ImGui.IsItemHovered())
            {
                string kind = entry.IsPatchFile ? "patch file" : "target JSON";
                ImGui.SetTooltip($"{kind}\n{entry.Category}\n{entry.Domain}:{entry.AssetPath}\n{entry.JsonStatusText}");
            }
        }

        if (last < _visiblePatchCreatorAssets.Count)
        {
            ImGui.Dummy(new NVector2(1f, (_visiblePatchCreatorAssets.Count - last) * rowHeight));
        }
    }

    private bool DrawPatchCreatorCategoryCombo()
    {
        int current = string.IsNullOrWhiteSpace(_patchCreatorCategoryFilter)
            ? 0
            : Math.Max(0, Array.FindIndex(_patchCreatorCategoryOptions, category => category.Equals(_patchCreatorCategoryFilter, StringComparison.OrdinalIgnoreCase)));
        bool changed = ImGui.Combo("Category##patch-creator-category", ref current, _patchCreatorCategoryOptions, _patchCreatorCategoryOptions.Length);
        if (changed)
        {
            _patchCreatorCategoryFilter = current <= 0 ? "" : _patchCreatorCategoryOptions[current];
        }
        return changed;
    }

    private bool DrawPatchCreatorDomainCombo()
    {
        int current = string.IsNullOrWhiteSpace(_patchCreatorDomainFilter)
            ? 0
            : Math.Max(0, Array.FindIndex(_patchCreatorDomainOptions, domain => domain.Equals(_patchCreatorDomainFilter, StringComparison.OrdinalIgnoreCase)));
        bool changed = ImGui.Combo("Domain##patch-creator-domain", ref current, _patchCreatorDomainOptions, _patchCreatorDomainOptions.Length);
        if (changed)
        {
            _patchCreatorDomainFilter = current <= 0 ? "" : _patchCreatorDomainOptions[current];
        }
        return changed;
    }

    private void DrawPatchCreatorPathPanel(NVector2 size)
    {
        ImGui.BeginChild("##patch-creator-path", size, true);
        PatchCreatorAssetEntry? entry = _patchCreatorBrowserMode == 1 ? SelectedPatchCreatorAsset : SelectedPatchCreatorSampleAsset;
        ImGui.SeparatorText(_patchCreatorBrowserMode == 1 ? "Patch file" : "Target path");

        if (entry == null)
        {
            ImGui.TextWrapped(_patchCreatorIndexer.IsIndexing ? _patchCreatorStatus : "No JSON asset selected.");
            ImGui.EndChild();
            return;
        }

        EnsurePatchCreatorAssetLoaded(entry);

        if (entry.IsPatchFile)
        {
            DrawPatchCreatorPatchFilePanel(entry);
            ImGui.EndChild();
            return;
        }

        ImGui.TextWrapped($"Sample: {entry.Domain}:{entry.AssetPath}");
        JToken? root = entry.Root;
        if (root == null)
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
            string selectedTokenJson = _patchCreatorSelectedTokenJson;
            if (string.IsNullOrWhiteSpace(selectedTokenJson) &&
                _patchCreatorSelectedPath.Length == 0 &&
                entry.Root != null)
            {
                selectedTokenJson = DevToolsPatchJson.ToString(entry.Root, Formatting.Indented);
            }
            if (!string.IsNullOrWhiteSpace(selectedTokenJson))
            {
                _patchCreatorValueJson = selectedTokenJson;
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
            DrawPatchCreatorJsonTree(root, "", "$");
        }
        ImGui.EndChild();
        ImGui.EndChild();
    }

    private void DrawPatchCreatorPatchFilePanel(PatchCreatorAssetEntry entry)
    {
        EnsurePatchCreatorAssetLoaded(entry);
        ImGui.TextWrapped($"Patch: {entry.Domain}:{entry.AssetPath}");
        ImGui.TextWrapped(entry.IsJsonPatchesFile ? "Detected format: JsonPatchesLib" : "Detected format: Vanilla patches");
        if (entry.Root == null)
        {
            ImGui.TextColored(new NVector4(1f, 0.35f, 0.25f, 1f), $"Invalid JSON: {entry.ParseError}");
            return;
        }

        if (ImGui.Button("Import selected patch##patch-creator-import-patch", new NVector2(-1f, 0f)))
        {
            RequestPatchCreatorImport(entry);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Load this patch file into the operation editor. Unknown/custom fields are preserved.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy patch source##patch-creator-copy-patch-source"))
        {
            ImGui.SetClipboardText(entry.SourceText);
            _patchCreatorStatus = "Copied selected patch source.";
        }

        ImGui.SeparatorText("Source preview");
        if (entry.SourcePreviewTruncated)
        {
            ImGui.TextDisabled($"Showing the first {PatchCreatorSourcePreviewCharacterLimit:N0} of {entry.SourceText.Length:N0} characters. Copy source still copies the complete file.");
        }
        if (ImGui.BeginChild("##patch-creator-source-preview", new NVector2(-float.Epsilon, -float.Epsilon), true, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.TextUnformatted(entry.SourcePreview);
        }
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
            _patchCreatorSelectedTokenJson = DevToolsPatchJson.ToString(token, Formatting.Indented);
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
            _ => $"{label}: {TrimPatchCreatorPreview(DevToolsPatchJson.ToString(token, Formatting.None), 80)}"
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
        PatchCreatorAssetEntry? entry = SelectedPatchCreatorSampleAsset;
        if (ImGui.Button("New patch##patch-creator-new-document"))
        {
            RequestPatchCreatorNewDocument();
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear document##patch-creator-clear-document"))
        {
            RequestPatchCreatorClearDocument();
        }
        if (_patchCreatorDocumentDirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(new NVector4(1f, 0.78f, 0.35f, 1f), "Unsaved changes");
        }

        int outputFormat = _patchCreatorOutputFormat;
        if (ImGui.Combo("Output##patch-creator-output-format", ref outputFormat, PatchCreatorOutputFormatLabels, PatchCreatorOutputFormatLabels.Length))
        {
            _patchCreatorOutputFormat = Math.Clamp(outputFormat, 0, PatchCreatorOutputFormatLabels.Length - 1);
            MarkPatchCreatorDocumentDirty();
        }

        if (ImGui.InputText("Patch domain##patch-creator-domain-output", ref _patchCreatorOutputDomain, 120))
        {
            MarkPatchCreatorDocumentDirty();
        }
        if (ImGui.InputText("Patch path##patch-creator-name-output", ref _patchCreatorPatchName, 260))
        {
            MarkPatchCreatorDocumentDirty();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Relative path under patches/ or jsonpatches/. Nested paths are allowed, e.g. compatibility/meteoricsteel/fixes.json.");
        }
        if (ImGui.Button("Use sample domain##patch-creator-sample-domain") && entry != null)
        {
            _patchCreatorOutputDomain = entry.Domain;
            MarkPatchCreatorDocumentDirty();
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
        string[] ops = CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.JsonPatchesLib ? PatchCreatorJsonPatchesLibOps : PatchCreatorVanillaOps;
        _patchCreatorOpIndex = Math.Clamp(_patchCreatorOpIndex, 0, ops.Length - 1);
        bool rawTemplate = _patchCreatorTemplate == PatchCreatorTemplateLabels.Length - 1;
        if (rawTemplate)
        {
            ImGui.TextUnformatted("Raw operation JSON");
            ImGui.InputTextMultiline("##patch-creator-raw-operation-json", ref _patchCreatorRawOperationJson, DevToolsImGuiTextBuffer.Capacity(_patchCreatorRawOperationJson, growthLimit: 512 * 1024), new NVector2(-float.Epsilon, 160f), ImGuiInputTextFlags.AllowTabInput);
            if (ImGui.Button("Build raw from fields##patch-creator-build-raw"))
            {
                if (TryCreateStructuredPatchCreatorOperation(out DevToolsPatchOperationDraft? structured, out string rawError))
                {
                    _patchCreatorRawOperationJson = DevToolsPatchJson.ToString(structured!.ToJson(CurrentPatchCreatorOutputFormat), Formatting.Indented);
                }
                else
                {
                    _patchCreatorStatus = rawError;
                }
            }
        }
        else
        {
            ImGui.Combo("Op##patch-creator-op", ref _patchCreatorOpIndex, ops, ops.Length);
            ImGui.InputText("Path##patch-creator-path", ref _patchCreatorPath, 1024);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.JsonPatchesLib ? "JsonPatchesLib path without a leading slash." : "Enter without leading slash; vanilla output will add it.");
            }

            string op = ops[_patchCreatorOpIndex];
            if (PatchCreatorOpNeedsFromPath(op))
            {
                ImGui.InputText("From path##patch-creator-from-path", ref _patchCreatorFromPath, 1024);
            }

            if (PatchCreatorOpNeedsValue(op) || (op.Equals("remove", StringComparison.OrdinalIgnoreCase) && CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.JsonPatchesLib))
            {
                ImGui.TextUnformatted(op.Equals("expression", StringComparison.OrdinalIgnoreCase) ? "Expression" : "Value JSON");
                ImGui.InputTextMultiline("##patch-creator-value-json", ref _patchCreatorValueJson, DevToolsImGuiTextBuffer.Capacity(_patchCreatorValueJson, growthLimit: 256 * 1024), new NVector2(-float.Epsilon, 92f), ImGuiInputTextFlags.AllowTabInput);
            }
        }

        if (ImGui.CollapsingHeader("Advanced##patch-creator-advanced", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Checkbox("Enabled##patch-creator-enabled", ref _patchCreatorEnabled);
            ImGui.Combo("Side##patch-creator-side", ref _patchCreatorSideIndex, PatchCreatorSideLabels, PatchCreatorSideLabels.Length);
            ImGui.InputInt("Priority##patch-creator-priority", ref _patchCreatorPriority);
            DrawPatchCreatorDependsOnEditor();
            DrawPatchCreatorExtraFieldsEditor();
            if (CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.Vanilla)
            {
                DrawPatchCreatorConditionBuilder();
            }
            else
            {
                ImGui.TextWrapped("JsonPatchesLib output requires the consuming mod to depend on jsonpatcheslib. This tool does not edit modinfo.json.");
            }
        }

        bool canAdd = TryCreatePatchCreatorOperation(out DevToolsPatchOperationDraft? draft, out string error);
        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button("Add operation##patch-creator-add-op"))
        {
            _patchCreatorOperations.Add(draft!);
            _patchCreatorOperationIndex = _patchCreatorOperations.Count - 1;
            MarkPatchCreatorDocumentDirty();
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
                MarkPatchCreatorDocumentDirty();
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
            changed |= ImGui.InputTextMultiline("##patch-creator-condition-is-value", ref _patchCreatorConditionValueJson, DevToolsImGuiTextBuffer.Capacity(_patchCreatorConditionValueJson, growthLimit: 64 * 1024), new NVector2(-float.Epsilon, 58f), ImGuiInputTextFlags.AllowTabInput);
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
            ImGui.InputTextMultiline("##patch-creator-condition-json", ref _patchCreatorConditionJson, DevToolsImGuiTextBuffer.Capacity(_patchCreatorConditionJson, growthLimit: 64 * 1024), new NVector2(-float.Epsilon, 70f), ImGuiInputTextFlags.AllowTabInput);
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

        conditionJson = DevToolsPatchJson.ToString(condition, Formatting.Indented);
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
        _patchCreatorConditionValueJson = DevToolsPatchJson.ToString(isValue, Formatting.Indented);
    }

    private void DrawPatchCreatorDependsOnEditor()
    {
        if (!ImGui.TreeNode("dependsOn##patch-creator-dependson"))
        {
            return;
        }

        try
        {
            JArray dependsOn = ParsePatchCreatorDependsOnArray();
            bool changed = false;
            for (int i = 0; i < dependsOn.Count; i++)
            {
                JObject dependency = dependsOn[i] as JObject ?? [];
                if (dependsOn[i] is not JObject) dependsOn[i] = dependency;

                ImGui.PushID($"patch-creator-dependson-{i}");
                string modid = dependency["modid"]?.ToString() ?? "";
                ImGui.SetNextItemWidth(140f);
                if (ImGui.InputText("modid", ref modid, 120))
                {
                    dependency["modid"] = modid;
                    changed = true;
                }
                ImGui.SameLine();
                string version = dependency["version"]?.ToString() ?? "";
                ImGui.SetNextItemWidth(110f);
                if (ImGui.InputText("version", ref version, 80))
                {
                    if (string.IsNullOrWhiteSpace(version)) dependency.Remove("version");
                    else dependency["version"] = version;
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                {
                    dependsOn.RemoveAt(i);
                    i--;
                    changed = true;
                }
                ImGui.PopID();
            }

            if (ImGui.Button("Add dependency##patch-creator-add-dependson"))
            {
                dependsOn.Add(new JObject { ["modid"] = "" });
                changed = true;
            }

            if (changed)
            {
                _patchCreatorDependsOnJson = dependsOn.Count == 0 ? "" : DevToolsPatchJson.ToString(dependsOn, Formatting.Indented);
            }

            ImGui.TextUnformatted("Raw dependsOn JSON");
            ImGui.InputTextMultiline("##patch-creator-dependson-json", ref _patchCreatorDependsOnJson, DevToolsImGuiTextBuffer.Capacity(_patchCreatorDependsOnJson, growthLimit: 128 * 1024), new NVector2(-float.Epsilon, 80f), ImGuiInputTextFlags.AllowTabInput);
        }
        finally
        {
            ImGui.TreePop();
        }
    }

    private JArray ParsePatchCreatorDependsOnArray()
    {
        if (string.IsNullOrWhiteSpace(_patchCreatorDependsOnJson)) return [];
        if (TryParsePatchCreatorJson(_patchCreatorDependsOnJson, out JToken? token, out _) && token is JArray array)
        {
            return (JArray)array.DeepClone();
        }

        return [];
    }

    private void DrawPatchCreatorExtraFieldsEditor()
    {
        if (!ImGui.TreeNode("Extra fields##patch-creator-extra-fields"))
        {
            return;
        }

        try
        {
            ImGui.TextWrapped("Unknown/custom operation keys are emitted alongside the structured fields.");
            ImGui.InputTextMultiline("##patch-creator-extra-json", ref _patchCreatorExtraJson, DevToolsImGuiTextBuffer.Capacity(_patchCreatorExtraJson, growthLimit: 128 * 1024), new NVector2(-float.Epsilon, 90f), ImGuiInputTextFlags.AllowTabInput);
        }
        finally
        {
            ImGui.TreePop();
        }
    }

    private void DrawPatchCreatorOperationList()
    {
        if (ImGui.BeginChild("##patch-creator-operation-list", new NVector2(-float.Epsilon, 120f), true))
        {
            for (int i = 0; i < _patchCreatorOperations.Count; i++)
            {
                DevToolsPatchOperationDraft operation = _patchCreatorOperations[i];
                if (ImGui.Selectable($"{i}: {operation.Op} {operation.File} {operation.Path}##patch-op-{i}", i == _patchCreatorOperationIndex))
                {
                    _patchCreatorOperationIndex = i;
                    LoadPatchCreatorOperation(operation);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(DevToolsPatchJson.ToString(operation.ToJson(CurrentPatchCreatorOutputFormat), Formatting.Indented));
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
            MarkPatchCreatorDocumentDirty();
        }
        ImGui.SameLine();
        if (ImGui.Button("Remove##patch-creator-remove-op"))
        {
            _patchCreatorOperations.RemoveAt(_patchCreatorOperationIndex);
            _patchCreatorOperationIndex = Math.Clamp(_patchCreatorOperationIndex, -1, _patchCreatorOperations.Count - 1);
            MarkPatchCreatorDocumentDirty();
        }
        ImGui.SameLine();
        if (ImGui.Button("Top##patch-creator-top-op"))
        {
            MovePatchCreatorOperation(_patchCreatorOperationIndex, 0);
        }
        ImGui.SameLine();
        if (ImGui.Button("Up##patch-creator-up-op"))
        {
            MovePatchCreatorOperation(_patchCreatorOperationIndex, _patchCreatorOperationIndex - 1);
        }
        ImGui.SameLine();
        if (ImGui.Button("Down##patch-creator-down-op"))
        {
            MovePatchCreatorOperation(_patchCreatorOperationIndex, _patchCreatorOperationIndex + 1);
        }
        ImGui.SameLine();
        if (ImGui.Button("Bottom##patch-creator-bottom-op"))
        {
            MovePatchCreatorOperation(_patchCreatorOperationIndex, _patchCreatorOperations.Count - 1);
        }
        if (!hasSelection) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Clear##patch-creator-clear-ops"))
        {
            _patchCreatorOperations.Clear();
            _patchCreatorOperationIndex = -1;
            MarkPatchCreatorDocumentDirty();
        }
    }

    private void MovePatchCreatorOperation(int from, int to)
    {
        if (from < 0 || from >= _patchCreatorOperations.Count) return;
        to = Math.Clamp(to, 0, _patchCreatorOperations.Count - 1);
        if (from == to) return;

        DevToolsPatchOperationDraft operation = _patchCreatorOperations[from];
        _patchCreatorOperations.RemoveAt(from);
        _patchCreatorOperations.Insert(to, operation);
        _patchCreatorOperationIndex = to;
        MarkPatchCreatorDocumentDirty();
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

        ImGui.Combo("Preview side##patch-creator-preview-side", ref _patchCreatorPreviewSideIndex, PatchCreatorPreviewSideLabels, PatchCreatorPreviewSideLabels.Length);
        if (ImGui.TreeNode("Preview settings JSON##patch-creator-preview-settings"))
        {
            ImGui.InputTextMultiline("##patch-creator-preview-settings-json", ref _patchCreatorPreviewSettingsJson, DevToolsImGuiTextBuffer.Capacity(_patchCreatorPreviewSettingsJson, growthLimit: 64 * 1024), new NVector2(-float.Epsilon, 80f), ImGuiInputTextFlags.AllowTabInput);
            ImGui.TreePop();
        }

        DevToolsPatchPreviewResult previewResult = TryPreviewApplyPatchCreatorOperations();
        string appliedPreview = previewResult.PreviewText;
        if (!previewResult.Success)
        {
            ImGui.TextColored(new NVector4(1f, 0.45f, 0.30f, 1f), previewResult.Status);
        }
        else
        {
            ImGui.TextWrapped(previewResult.Status);
            foreach (string warning in previewResult.Warnings.Take(4))
            {
                ImGui.TextColored(new NVector4(1f, 0.78f, 0.35f, 1f), warning);
            }
        }

        if (!canSave) ImGui.BeginDisabled();
        if (ImGui.Button("Save patch file##patch-creator-save", new NVector2(-1, 0)))
        {
            QueueSourceSave(TrySavePatchCreator(patchPreview), status => _patchCreatorStatus = status);
        }
        if (!canSave) ImGui.EndDisabled();

        ImGui.TextWrapped(_patchCreatorStatus);
        if (CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.JsonPatchesLib)
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
        bool previewTruncated = previewText.Length > PatchCreatorSourcePreviewCharacterLimit;
        if (previewTruncated)
        {
            ImGui.TextDisabled($"Showing the first {PatchCreatorSourcePreviewCharacterLimit:N0} of {previewText.Length:N0} preview characters.");
            previewText = previewText[..PatchCreatorSourcePreviewCharacterLimit];
        }
        if (ImGui.BeginChild("##patch-creator-preview-json", new NVector2(-float.Epsilon, Math.Max(150f, ImGui.GetContentRegionAvail().Y - 30f)), true, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.TextUnformatted(previewText);
        }
        ImGui.EndChild();
        _patchCreatorDiagnostics.Draw("patch-creator", showDiagnostics);
    }

    private void DrawPatchCreatorDiffHelper()
    {
        PatchCreatorAssetEntry? entry = SelectedPatchCreatorSampleAsset;
        if (entry?.Root == null)
        {
            ImGui.TextWrapped("Select a valid exact sample asset before generating operations from edited JSON.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_patchCreatorDiffJson))
        {
            _patchCreatorDiffJson = DevToolsPatchJson.ToString(entry.Root, Formatting.Indented);
        }

        ImGui.TextWrapped("Edit or paste the desired final JSON. Generated operations are conservative add/remove/replace operations.");
        ImGui.InputTextMultiline("##patch-creator-diff-json", ref _patchCreatorDiffJson, DevToolsImGuiTextBuffer.Capacity(_patchCreatorDiffJson), new NVector2(-float.Epsilon, 150f), ImGuiInputTextFlags.AllowTabInput);
        if (ImGui.Button("Reset to sample##patch-creator-diff-reset"))
        {
            _patchCreatorDiffJson = DevToolsPatchJson.ToString(entry.Root, Formatting.Indented);
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
        PatchCreatorAssetEntry? entry = SelectedPatchCreatorSampleAsset;
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
                SetPatchCreatorBuilderOp(CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.JsonPatchesLib ? "addmerge" : "add");
                _patchCreatorValueJson = "[]";
                break;
            case 5:
                SetPatchCreatorBuilderOp(CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.JsonPatchesLib ? "expression" : "replace");
                _patchCreatorValueJson = CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.JsonPatchesLib ? "value" : "0";
                break;
            case 6:
                SetPatchCreatorBuilderOp("copy");
                break;
            case 7:
                SetPatchCreatorBuilderOp("move");
                break;
            case 8:
                if (TryCreateStructuredPatchCreatorOperation(out DevToolsPatchOperationDraft? structured, out string error))
                {
                    _patchCreatorRawOperationJson = DevToolsPatchJson.ToString(structured!.ToJson(CurrentPatchCreatorOutputFormat), Formatting.Indented);
                }
                else
                {
                    _patchCreatorStatus = error;
                }
                break;
        }

        if (entry != null && _patchCreatorTargetMode == 0)
        {
            _patchCreatorFilePattern = GetPatchCreatorBuilderFile(entry);
        }
    }

    private void SetPatchCreatorBuilderOp(string op)
    {
        string[] ops = CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.JsonPatchesLib ? PatchCreatorJsonPatchesLibOps : PatchCreatorVanillaOps;
        int index = Array.FindIndex(ops, candidate => candidate.Equals(op, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _patchCreatorOpIndex = index;
    }

    private bool TryCreatePatchCreatorOperation(out DevToolsPatchOperationDraft? operation, out string error)
    {
        if (_patchCreatorTemplate == PatchCreatorTemplateLabels.Length - 1)
        {
            return TryCreateRawPatchCreatorOperation(out operation, out error);
        }

        return TryCreateStructuredPatchCreatorOperation(out operation, out error);
    }

    private bool TryCreateRawPatchCreatorOperation(out DevToolsPatchOperationDraft? operation, out string error)
    {
        operation = null;
        error = "";
        if (!TryParsePatchCreatorJson(_patchCreatorRawOperationJson, out JToken? raw, out string rawError) || raw is not JObject rawObject)
        {
            error = $"Raw operation JSON is invalid: {rawError}";
            return false;
        }

        operation = DevToolsPatchOperationDraft.FromJson(rawObject, CurrentPatchCreatorOutputFormat);
        return true;
    }

    private bool TryCreateStructuredPatchCreatorOperation(out DevToolsPatchOperationDraft? operation, out string error)
    {
        operation = null;
        error = "";
        string[] ops = CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.JsonPatchesLib ? PatchCreatorJsonPatchesLibOps : PatchCreatorVanillaOps;
        string op = ops[Math.Clamp(_patchCreatorOpIndex, 0, ops.Length - 1)];
        string file = GetPatchCreatorBuilderFile(SelectedPatchCreatorSampleAsset);
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

        if (CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.Vanilla && !string.IsNullOrWhiteSpace(_patchCreatorConditionJson) && !TryParsePatchCreatorJson(_patchCreatorConditionJson, out _, out string conditionError))
        {
            error = $"Condition JSON is invalid: {conditionError}";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_patchCreatorDependsOnJson) &&
            (!TryParsePatchCreatorJson(_patchCreatorDependsOnJson, out JToken? dependsOn, out string dependsOnError) || dependsOn is not JArray))
        {
            error = $"dependsOn JSON must be an array: {dependsOnError}";
            return false;
        }

        JObject extra = [];
        if (!string.IsNullOrWhiteSpace(_patchCreatorExtraJson) &&
            (!TryParsePatchCreatorJson(_patchCreatorExtraJson, out JToken? extraToken, out string extraError) || extraToken is not JObject extraObject))
        {
            error = $"Extra fields JSON must be an object: {extraError}";
            return false;
        }
        else if (!string.IsNullOrWhiteSpace(_patchCreatorExtraJson) && TryParsePatchCreatorJson(_patchCreatorExtraJson, out JToken? parsedExtra, out _) && parsedExtra is JObject parsedExtraObject)
        {
            extra = parsedExtraObject;
        }

        operation = new DevToolsPatchOperationDraft
        {
            Op = op,
            File = file,
            Path = NormalizePatchCreatorPath(_patchCreatorPath),
            FromPath = NormalizePatchCreatorPath(_patchCreatorFromPath),
            ValueJson = valueJson,
            HasValue = PatchCreatorOpNeedsValue(op) || (op.Equals("remove", StringComparison.OrdinalIgnoreCase) && CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.JsonPatchesLib && !string.IsNullOrWhiteSpace(valueJson)),
            DependsOnJson = _patchCreatorDependsOnJson,
            Extra = extra,
            ConditionJson = _patchCreatorConditionJson,
            Enabled = _patchCreatorEnabled,
            Side = PatchCreatorSideLabels[Math.Clamp(_patchCreatorSideIndex, 0, PatchCreatorSideLabels.Length - 1)],
            Priority = _patchCreatorPriority == 0 ? null : _patchCreatorPriority
        };
        return true;
    }

    private void LoadPatchCreatorOperation(DevToolsPatchOperationDraft operation)
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
        _patchCreatorDependsOnJson = operation.DependsOnJson;
        _patchCreatorExtraJson = operation.Extra.Count == 0 ? "{}" : DevToolsPatchJson.ToString(operation.Extra, Formatting.Indented);
        _patchCreatorRawOperationJson = DevToolsPatchJson.ToString(operation.ToJson(CurrentPatchCreatorOutputFormat), Formatting.Indented);
        LoadPatchCreatorConditionBuilder(_patchCreatorConditionJson);
        _patchCreatorEnabled = operation.Enabled ?? true;
        _patchCreatorPriority = operation.Priority ?? 0;
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
        return CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.JsonPatchesLib
            ? $"{entry.Domain}:{entry.AssetPath}"
            : entry.AssetPath;
    }

    private string BuildPatchCreatorPatchJson(DevToolsPatchOutputFormat format)
    {
        JArray array = [];
        foreach (DevToolsPatchOperationDraft operation in _patchCreatorOperations)
        {
            array.Add(operation.ToJson(format));
        }

        return DevToolsPatchJson.ToString(array, Formatting.Indented);
    }

    private List<string> ValidatePatchCreatorOperations(DevToolsPatchOutputFormat format)
    {
        List<string> issues = [];
        if (_patchCreatorOperations.Count == 0)
        {
            issues.Add("Error: add at least one operation.");
            return issues;
        }

        foreach (DevToolsPatchOperationDraft operation in _patchCreatorOperations)
        {
            if (format == DevToolsPatchOutputFormat.Vanilla && !PatchCreatorVanillaOps.Any(op => op.Equals(operation.Op, StringComparison.OrdinalIgnoreCase)))
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

            if (!string.IsNullOrWhiteSpace(operation.DependsOnJson) &&
                (!TryParsePatchCreatorJson(operation.DependsOnJson, out JToken? dependsOn, out string dependsOnError) || dependsOn is not JArray))
            {
                issues.Add($"Error: dependsOn for {operation.Op} must be an array: {dependsOnError}");
            }
        }

        return issues;
    }

    private DevToolsPatchPreviewResult TryPreviewApplyPatchCreatorOperations()
    {
        PatchCreatorAssetEntry? entry = SelectedPatchCreatorSampleAsset;
        if (entry?.Root == null)
        {
            return new DevToolsPatchPreviewResult(false, "", "No valid sample asset selected for preview.", []);
        }

        JObject previewSettings = [];
        if (!string.IsNullOrWhiteSpace(_patchCreatorPreviewSettingsJson))
        {
            if (!TryParsePatchCreatorJson(_patchCreatorPreviewSettingsJson, out JToken? settingsToken, out string settingsError) || settingsToken is not JObject settingsObject)
            {
                return new DevToolsPatchPreviewResult(false, "", $"Preview settings JSON is invalid: {settingsError}", []);
            }

            previewSettings = settingsObject;
        }

        string side = PatchCreatorPreviewSideLabels[Math.Clamp(_patchCreatorPreviewSideIndex, 0, PatchCreatorPreviewSideLabels.Length - 1)];
        return DevToolsPatchPreview.Apply(
            entry.Root,
            _patchCreatorOperations,
            entry.Domain,
            entry.AssetPath,
            new DevToolsPatchPreviewOptions(CurrentPatchCreatorOutputFormat, side, previewSettings, CountPatchCreatorMatchingAssets));
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
                    error = $"Test failed. Expected {DevToolsPatchJson.ToString(expected, Formatting.None)}, found {DevToolsPatchJson.ToString(testToken, Formatting.None)}.";
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
        if (added > 0) MarkPatchCreatorDocumentDirty();
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

    private DevToolsPatchOperationDraft BuildPatchCreatorDiffOperation(PatchCreatorAssetEntry entry, string op, string path, JToken? value)
    {
        return new DevToolsPatchOperationDraft
        {
            Op = op,
            File = CurrentPatchCreatorOutputFormat == DevToolsPatchOutputFormat.JsonPatchesLib ? $"{entry.Domain}:{entry.AssetPath}" : entry.AssetPath,
            Path = path,
            FromPath = "",
            ValueJson = value == null ? "" : DevToolsPatchJson.ToString(value, Formatting.Indented),
            HasValue = value != null,
            ConditionJson = "",
            Enabled = true,
            Side = "Server",
            Priority = null
        };
    }

    private SourceSaveResult TrySavePatchCreator(string newText)
    {
        try
        {
            string targetDomain = SanitizePathSegment(string.IsNullOrWhiteSpace(_patchCreatorOutputDomain) ? "ingamedevtools" : _patchCreatorOutputDomain.Trim());
            string assetPath = DevToolsPatchDocumentDraft.BuildAssetPath(CurrentPatchCreatorOutputFormat, _patchCreatorPatchName);
            string outputPath = GetToolAuthoredAssetPath("patches", Path.Combine("assets", targetDomain, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : "";
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved patch file to {outputPath}.",
                () =>
                {
                    string result = WriteAuthoredFile(outputPath, newText);
                    _patchCreatorPatchName = DevToolsPatchDocumentDraft.NormalizeRelativePath(_patchCreatorPatchName);
                    _patchCreatorDocumentDirty = false;
                    _patchCreatorLoadedPatchKey = $"{targetDomain}:{assetPath}";
                    return result;
                });
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
            !entry.IsPatchFile &&
            file.Equals(entry.AssetPath, StringComparison.OrdinalIgnoreCase) ||
            (!entry.IsPatchFile && file.Equals($"{entry.Domain}:{entry.AssetPath}", StringComparison.OrdinalIgnoreCase)));
    }

    private int CountPatchCreatorMatchingAssets(string file)
    {
        return _patchCreatorAssets.Count(entry =>
        {
            if (entry.IsPatchFile) return false;
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
        return DevToolsPatchPreview.WildcardMatches(pattern, value);
    }

    private void SyncPatchCreatorSelection()
    {
        PatchCreatorAssetEntry? entry = SelectedPatchCreatorAsset;
        if (entry == null || !EnsurePatchCreatorAssetLoaded(entry) || entry.Root == null) return;
        if (entry.IsPatchFile)
        {
            ReleasePatchCreatorPayloadsExcept(entry, _patchCreatorSampleAssetKey);
            return;
        }

        _patchCreatorSampleAssetKey = entry.Key;
        ReleasePatchCreatorPayloadsExcept(entry);
        _patchCreatorSelectedPath = "";
        _patchCreatorSelectedTokenJson = "";
        _patchCreatorDiffJson = "";
        if (_patchCreatorTargetMode == 0)
        {
            _patchCreatorFilePattern = GetPatchCreatorBuilderFile(entry);
        }
    }

    private DevToolsPatchOutputFormat CurrentPatchCreatorOutputFormat => _patchCreatorOutputFormat == 1 ? DevToolsPatchOutputFormat.Vanilla : DevToolsPatchOutputFormat.JsonPatchesLib;

    private static bool PatchCreatorOpNeedsFromPath(string op) => DevToolsPatchOperations.NeedsFromPath(op);

    private static bool PatchCreatorOpNeedsValue(string op) => DevToolsPatchOperations.NeedsValue(op);

    private static string JoinPatchCreatorPath(string basePath, string part) => DevToolsPatchPaths.Join(basePath, part);

    private static string NormalizePatchCreatorPath(string path) => DevToolsPatchPaths.Normalize(path);

    private static string[] SplitPatchCreatorPath(string path) => DevToolsPatchPaths.Split(path);

    private static string FormatPatchCreatorOutputPath(string path, DevToolsPatchOutputFormat format) => DevToolsPatchPaths.Format(path, format);

    private static string FormatPatchCreatorOutputPath(string path, PatchCreatorOutputFormat format) =>
        DevToolsPatchPaths.Format(path, format == PatchCreatorOutputFormat.Vanilla ? DevToolsPatchOutputFormat.Vanilla : DevToolsPatchOutputFormat.JsonPatchesLib);

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

    private enum PatchCreatorJsonState
    {
        Unknown,
        Valid,
        Invalid
    }

    private sealed class PatchCreatorAssetEntry
    {
        private string? _sourceText;
        private string _sourcePreview = "";

        public PatchCreatorAssetEntry(IAsset asset)
        {
            Asset = asset;
            Domain = asset.Location.Domain ?? "game";
            AssetPath = asset.Location.Path.Replace('\\', '/');
            Category = GetPatchCreatorCategory(AssetPath);
            Key = asset.Location.ToString();
            IsPatchFile = Category.Equals("patches", StringComparison.OrdinalIgnoreCase) ||
                Category.Equals("jsonpatches", StringComparison.OrdinalIgnoreCase);
            IsJsonPatchesFile = Category.Equals("jsonpatches", StringComparison.OrdinalIgnoreCase);
            Authored = asset.Origin?.GetType().Name.Contains("ToolAuthoredAssetOrigin", StringComparison.OrdinalIgnoreCase) == true;
            SearchText = $"{Domain}:{AssetPath} {Category}";
        }

        public IAsset Asset { get; }
        public string SourceText => _sourceText ?? "";
        public string SourcePreview => _sourcePreview;
        public bool SourcePreviewTruncated => _sourceText != null && _sourceText.Length > _sourcePreview.Length;
        public JToken? Root { get; private set; }
        public string ParseError { get; private set; } = "";
        public PatchCreatorJsonState JsonState { get; private set; }
        public bool PayloadLoaded => Root != null;
        public string Domain { get; }
        public string AssetPath { get; }
        public string Category { get; }
        public string Key { get; }
        public bool IsPatchFile { get; }
        public bool IsJsonPatchesFile { get; }
        public bool Authored { get; }
        public string SortKey => $"{Category}:{Domain}:{AssetPath}";
        public string SearchText { get; }
        public string JsonStatusText => JsonState switch
        {
            PatchCreatorJsonState.Valid => "valid JSON",
            PatchCreatorJsonState.Invalid => $"invalid JSON: {ParseError}",
            _ => "JSON is loaded when selected"
        };

        public void SetPayload(string sourceText, JToken? root, string parseError)
        {
            Root = root;
            ParseError = parseError;
            JsonState = root == null ? PatchCreatorJsonState.Invalid : PatchCreatorJsonState.Valid;
            _sourceText = root != null && IsPatchFile ? sourceText : null;
            _sourcePreview = _sourceText == null
                ? ""
                : _sourceText.Length <= PatchCreatorSourcePreviewCharacterLimit
                    ? _sourceText
                    : _sourceText[..PatchCreatorSourcePreviewCharacterLimit];
        }

        public void ReleasePayload()
        {
            Root = null;
            _sourceText = null;
            _sourcePreview = "";
        }

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
