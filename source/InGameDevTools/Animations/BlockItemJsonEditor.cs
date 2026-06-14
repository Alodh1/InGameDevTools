using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private static readonly string[] BlockItemJsonBrowserModeLabels = ["Runtime collectables", "Source JSON assets", "Authored overrides"];
    private static readonly string[] BlockItemJsonPreviewModeLabels = ["Validation", "Authored JSON", "Diff", "Render preview"];
    private readonly List<BlockItemJsonEntry> _blockItemJsonAssets = [];
    private readonly List<BlockItemJsonEntry> _visibleBlockItemJsonAssets = [];
    private readonly List<BlockItemJsonSourceEntry> _blockItemJsonSourceAssets = [];
    private readonly List<BlockItemJsonSourceEntry> _visibleBlockItemJsonSourceAssets = [];
    private readonly ImGuiThreePanelLayoutState _blockItemJsonLayout = new(0.28f, 0.30f);
    private readonly DevToolsEditorDiagnostics _blockItemJsonDiagnostics = new("Block/Item JSON");
    private readonly DevToolsTextHistory _blockItemJsonTextHistory = new();
    private string _blockItemJsonFilter = "";
    private string _blockItemJsonDomainFilter = "";
    private int _blockItemJsonBrowserMode;
    private int _blockItemJsonTypeFilter;
    private int _blockItemJsonAssetIndex;
    private int _blockItemJsonSourceAssetIndex;
    private int _blockItemJsonPreviewMode;
    private bool _blockItemJsonDirtyOnly;
    private bool _blockItemJsonShowTextDiff;
    private bool _blockItemJsonIndexed;
    private string _blockItemJsonLoadedKey = "";
    private string _blockItemJsonLoadedLabel = "";
    private string _blockItemJsonLoadedDomain = "game";
    private string _blockItemJsonLoadedAssetPath = "blocktypes/unknown.json";
    private string _blockItemJsonLoadedSourceText = "";
    private bool _blockItemJsonLoadedIsRuntime;
    private bool _blockItemJsonLoadedIsAuthored;
    private DevToolsCollectibleKind _blockItemJsonLoadedKind = DevToolsCollectibleKind.Block;
    private string _blockItemJsonText = "";
    private string _blockItemJsonOriginalText = "";
    private string _blockItemJsonStatus = "";
    private string _blockItemJsonLiveAppliedHash = "";
    private string _blockItemJsonOutputDomain = "game";
    private string _blockItemJsonOutputPath = "blocktypes/unknown.json";
    private string _blockItemJsonAttributePath = "";
    private string _blockItemJsonAttributeValueJson = "null";
    private DevToolsPreview3DRenderer? _blockItemJsonPreviewRenderer;
    private DevToolsPreviewMesh? _blockItemJsonPreviewMesh;
    private string _blockItemJsonPreviewKey = "";
    private string? _blockItemJsonPreviewSkipReason;
    private float _blockItemJsonPreviewYaw = MathF.PI * 0.25f;
    private float _blockItemJsonPreviewPitch = 0.38f;
    private float _blockItemJsonPreviewDistance = 5.0f;
    private readonly Dictionary<string, string> _blockItemJsonFieldBuffers = new(StringComparer.Ordinal);

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

        _blockItemJsonSourceAssets.Clear();
        foreach (IAsset asset in CollectToolAuthoredAssets("block-item-json"))
        {
            AddBlockItemJsonSourceAsset(asset, authored: true);
        }

        foreach (IAsset asset in _api.Assets.AllAssets.Values)
        {
            AddBlockItemJsonSourceAsset(asset, authored: false);
        }

        _blockItemJsonAssets.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));
        _blockItemJsonSourceAssets.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));
        RebuildVisibleBlockItemJsonAssets();
        _blockItemJsonIndexed = true;
        _blockItemJsonStatus = $"Indexed {_blockItemJsonAssets.Count} runtime collectables and {_blockItemJsonSourceAssets.Count} source JSON assets.";
    }

    private void AddBlockItemJsonSourceAsset(IAsset asset, bool authored)
    {
        if (asset?.Location == null) return;

        string path = asset.Location.Path.Replace('\\', '/');
        bool isBlock = path.StartsWith("blocktypes/", StringComparison.OrdinalIgnoreCase);
        bool isItem = path.StartsWith("itemtypes/", StringComparison.OrdinalIgnoreCase);
        if (!isBlock && !isItem) return;
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;

        _blockItemJsonSourceAssets.Add(new(asset, isBlock, authored));
    }

    private void RebuildVisibleBlockItemJsonAssets()
    {
        string filter = _blockItemJsonFilter.Trim();
        BlockItemJsonEntry? selected = SelectedBlockItemJsonAsset;
        BlockItemJsonSourceEntry? selectedSource = SelectedBlockItemJsonSourceAsset;
        _visibleBlockItemJsonAssets.Clear();
        _visibleBlockItemJsonSourceAssets.Clear();

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

        foreach (BlockItemJsonSourceEntry entry in _blockItemJsonSourceAssets)
        {
            if (_blockItemJsonBrowserMode == 2 && !entry.Authored) continue;
            if (_blockItemJsonBrowserMode == 1 && entry.Authored) continue;
            if (!ImGuiLayoutHelper.MatchesDomain(_blockItemJsonDomainFilter, entry.Domain)) continue;
            if (_blockItemJsonTypeFilter == 1 && !entry.IsBlock) continue;
            if (_blockItemJsonTypeFilter == 2 && entry.IsBlock) continue;
            if (_blockItemJsonDirtyOnly && !string.Equals(entry.Key, _blockItemJsonLoadedKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (_blockItemJsonDirtyOnly && !IsBlockItemJsonDirty) continue;
            if (!string.IsNullOrWhiteSpace(filter) && !entry.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            _visibleBlockItemJsonSourceAssets.Add(entry);
        }

        if (selected != null)
        {
            int selectedIndex = _visibleBlockItemJsonAssets.FindIndex(entry => entry.Key == selected.Key);
            if (selectedIndex >= 0)
            {
                _blockItemJsonAssetIndex = selectedIndex;
            }
        }

        _blockItemJsonAssetIndex = Math.Clamp(_blockItemJsonAssetIndex, 0, Math.Max(0, _visibleBlockItemJsonAssets.Count - 1));

        if (selectedSource != null)
        {
            int selectedIndex = _visibleBlockItemJsonSourceAssets.FindIndex(entry => entry.Key == selectedSource.Key);
            if (selectedIndex >= 0)
            {
                _blockItemJsonSourceAssetIndex = selectedIndex;
            }
        }

        _blockItemJsonSourceAssetIndex = Math.Clamp(_blockItemJsonSourceAssetIndex, 0, Math.Max(0, _visibleBlockItemJsonSourceAssets.Count - 1));
    }

    private BlockItemJsonEntry? SelectedBlockItemJsonAsset =>
        _visibleBlockItemJsonAssets.Count == 0
            ? null
            : _visibleBlockItemJsonAssets[Math.Clamp(_blockItemJsonAssetIndex, 0, _visibleBlockItemJsonAssets.Count - 1)];

    private BlockItemJsonSourceEntry? SelectedBlockItemJsonSourceAsset =>
        _visibleBlockItemJsonSourceAssets.Count == 0
            ? null
            : _visibleBlockItemJsonSourceAssets[Math.Clamp(_blockItemJsonSourceAssetIndex, 0, _visibleBlockItemJsonSourceAssets.Count - 1)];

    private bool IsBlockItemJsonDirty => !string.Equals(_blockItemJsonText, _blockItemJsonOriginalText, StringComparison.Ordinal);

    private bool CanReplaceBlockItemJsonDocument(string nextKey)
    {
        if (string.IsNullOrWhiteSpace(_blockItemJsonLoadedKey)) return true;
        if (string.Equals(_blockItemJsonLoadedKey, nextKey, StringComparison.Ordinal)) return true;
        if (!IsBlockItemJsonDirty) return true;

        _blockItemJsonStatus = "Current Block/Item JSON has unsaved edits. Save authored JSON or reload selected before opening another document.";
        return false;
    }

    private void DrawBlockItemJsonBrowser(NVector2 size)
    {
        ImGui.BeginChild("##block-item-json-browser", size, true);

        ImGui.SeparatorText("Assets");
        ImGui.SetNextItemWidth(-float.Epsilon);
        if (ImGui.Combo("Mode##block-json-mode", ref _blockItemJsonBrowserMode, BlockItemJsonBrowserModeLabels, BlockItemJsonBrowserModeLabels.Length))
        {
            RebuildVisibleBlockItemJsonAssets();
        }

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
            _blockItemJsonLoadedSourceText = "";
            _blockItemJsonLiveAppliedHash = "";
            EnsureBlockItemJsonAssetsIndexed();
        }

        bool sourceMode = _blockItemJsonBrowserMode != 0;
        ImGui.TextDisabled(sourceMode
            ? $"{_visibleBlockItemJsonSourceAssets.Count} / {_blockItemJsonSourceAssets.Count}"
            : $"{_visibleBlockItemJsonAssets.Count} / {_blockItemJsonAssets.Count}");

        if (!sourceMode && _visibleBlockItemJsonAssets.Count == 0)
        {
            ImGui.TextDisabled("No matching block/item assets.");
            ImGui.EndChild();
            return;
        }

        if (sourceMode && _visibleBlockItemJsonSourceAssets.Count == 0)
        {
            ImGui.TextDisabled(_blockItemJsonBrowserMode == 2 ? "No authored overrides yet." : "No matching source JSON assets.");
            if (ImGui.Button("New authored block JSON##block-json-new-block", new NVector2(-1, 0)))
            {
                LoadNewBlockItemJsonDocument(DevToolsCollectibleKind.Block);
            }
            if (ImGui.Button("New authored item JSON##block-json-new-item", new NVector2(-1, 0)))
            {
                LoadNewBlockItemJsonDocument(DevToolsCollectibleKind.Item);
            }
            ImGui.EndChild();
            return;
        }

        if (ImGui.Button("New authored block JSON##block-json-new-block", new NVector2(-1, 0)))
        {
            LoadNewBlockItemJsonDocument(DevToolsCollectibleKind.Block);
        }
        if (ImGui.Button("New authored item JSON##block-json-new-item", new NVector2(-1, 0)))
        {
            LoadNewBlockItemJsonDocument(DevToolsCollectibleKind.Item);
        }

        _blockItemJsonAssetIndex = Math.Clamp(_blockItemJsonAssetIndex, 0, Math.Max(0, _visibleBlockItemJsonAssets.Count - 1));
        _blockItemJsonSourceAssetIndex = Math.Clamp(_blockItemJsonSourceAssetIndex, 0, Math.Max(0, _visibleBlockItemJsonSourceAssets.Count - 1));
        float listHeight = Math.Max(140f, ImGui.GetContentRegionAvail().Y);
        if (ImGui.BeginListBox("##block-json-assets", new NVector2(-float.Epsilon, listHeight)))
        {
            if (sourceMode)
            {
                for (int index = 0; index < _visibleBlockItemJsonSourceAssets.Count; index++)
                {
                    BlockItemJsonSourceEntry entry = _visibleBlockItemJsonSourceAssets[index];
                    bool selected = index == _blockItemJsonSourceAssetIndex;
                    string dirtyMarker = IsBlockItemJsonDirty && string.Equals(entry.Key, _blockItemJsonLoadedKey, StringComparison.OrdinalIgnoreCase) ? "*" : "";
                    if (ImGui.Selectable($"{dirtyMarker}{entry.Label}##block-json-source-{index}", selected))
                    {
                        _blockItemJsonSourceAssetIndex = index;
                        if (CanReplaceBlockItemJsonDocument(entry.Key))
                        {
                            LoadBlockItemJsonSourceEntry(entry, keepDirty: false);
                        }
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(entry.Tooltip);
                    }
                }
            }
            else
            {
                for (int index = 0; index < _visibleBlockItemJsonAssets.Count; index++)
                {
                    BlockItemJsonEntry entry = _visibleBlockItemJsonAssets[index];
                    bool selected = index == _blockItemJsonAssetIndex;
                    string dirtyMarker = IsBlockItemJsonDirty && string.Equals(entry.Key, _blockItemJsonLoadedKey, StringComparison.OrdinalIgnoreCase) ? "*" : "";
                    if (ImGui.Selectable($"{dirtyMarker}{entry.Label}##block-json-asset-{index}", selected))
                    {
                        _blockItemJsonAssetIndex = index;
                        if (CanReplaceBlockItemJsonDocument(entry.Key))
                        {
                            LoadBlockItemJsonEntry(entry, keepDirty: false);
                        }
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(entry.Tooltip);
                    }
                }
            }

            ImGui.EndListBox();
        }

        ImGui.EndChild();
    }

    private void DrawBlockItemJsonTextEditor(NVector2 size)
    {
        ImGui.BeginChild("##block-item-json-text-editor", size, true);

        if (string.IsNullOrWhiteSpace(_blockItemJsonLoadedKey))
        {
            if (_blockItemJsonBrowserMode == 0)
            {
                BlockItemJsonEntry? entry = SelectedBlockItemJsonAsset;
                if (entry != null) EnsureBlockItemJsonEntryLoaded(entry);
            }
            else
            {
                BlockItemJsonSourceEntry? entry = SelectedBlockItemJsonSourceAsset;
                if (entry != null) EnsureBlockItemJsonSourceEntryLoaded(entry);
            }
        }

        if (string.IsNullOrWhiteSpace(_blockItemJsonLoadedKey))
        {
            ImGui.TextDisabled("Select or create a block/item JSON document.");
            ImGui.EndChild();
            return;
        }

        ImGui.SeparatorText(_blockItemJsonLoadedLabel);
        ImGui.TextDisabled("Structured source fields are above the raw editor. Runtime apply patches attributes only; source save writes the full JSON.");

        JObject? structuredJson = TryParseJsonObject(_blockItemJsonText);
        if (structuredJson != null)
        {
            if (DrawBlockItemJsonStructuredEditor(structuredJson))
            {
                _blockItemJsonText = JsonConvert.SerializeObject(structuredJson, Formatting.Indented);
                _blockItemJsonStatus = "Structured JSON edited. Apply runtime attributes or save authored file when ready.";
                _blockItemJsonFieldBuffers.Clear();
                _blockItemJsonTextHistory.Record(_blockItemJsonText, ImGui.GetTime());
                InvalidateBlockItemJsonPreview();
                RebuildVisibleBlockItemJsonAssets();
            }
        }
        else
        {
            ImGui.TextColored(new NVector4(1f, 0.45f, 0.30f, 1f), "Structured controls disabled until raw JSON parses.");
        }

        ImGui.SeparatorText("Raw JSON");
        _blockItemJsonTextHistory.Record(_blockItemJsonText, ImGui.GetTime());
        if (DevToolsJsonTextTools.DrawEditToolbar("block-item-json-tools", ref _blockItemJsonText, _blockItemJsonTextHistory, out string toolStatus))
        {
            _blockItemJsonStatus = toolStatus;
            _blockItemJsonFieldBuffers.Clear();
            InvalidateBlockItemJsonPreview();
            RebuildVisibleBlockItemJsonAssets();
        }
        ImGui.SameLine();
        ImGui.Checkbox("Diff##block-json-diff-toggle", ref _blockItemJsonShowTextDiff);

        if (_blockItemJsonShowTextDiff)
        {
            DevToolsTextDiffView.Draw(
                "block-item-json-raw-diff",
                _blockItemJsonOriginalText,
                _blockItemJsonText,
                Math.Max(120f, Math.Min(260f, ImGui.GetContentRegionAvail().Y * 0.35f)));
        }

        NVector2 editorSize = new(-float.Epsilon, Math.Max(220f, ImGui.GetContentRegionAvail().Y - 34f));
        if (ImGui.InputTextMultiline("##block-item-json-text", ref _blockItemJsonText, 1024 * 1024, editorSize, ImGuiInputTextFlags.AllowTabInput))
        {
            _blockItemJsonStatus = "JSON edited. Apply runtime or save authored file when ready.";
            _blockItemJsonFieldBuffers.Clear();
            _blockItemJsonTextHistory.Record(_blockItemJsonText, ImGui.GetTime());
            InvalidateBlockItemJsonPreview();
            RebuildVisibleBlockItemJsonAssets();
            if (_liveApplyManager.AutoApply)
            {
                ApplyBlockItemJsonRuntime(force: false);
            }
        }

        ImGui.EndChild();
    }

    private bool DrawBlockItemJsonStructuredEditor(JObject json)
    {
        bool changed = false;

        if (ImGui.CollapsingHeader("Identity and variants##block-json-identity", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            changed |= EditBlockItemJsonString(json, "code", "Code##block-json-code", saveOnly: true, maxLength: 240);
            changed |= EditBlockItemJsonOptionalString(json, "class", "Class##block-json-class", saveOnly: true);
            changed |= EditBlockItemJsonOptionalBool(json, "enabled", "Enabled##block-json-enabled", saveOnly: true, defaultValue: true);
            changed |= DrawBlockItemJsonVariantGroupsEditor(json);
            changed |= EditBlockItemJsonTokenField(json, "byType", "By-type rules JSON##block-json-bytype", new JObject(), saveOnly: true);
            changed |= EditBlockItemJsonTokenField(json, "creativeinventory", "Creative inventory JSON##block-json-creative", new JObject(), saveOnly: true);
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Shape, textures, and render##block-json-shape", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            changed |= DrawBlockItemJsonShapeEditor(json);
            changed |= DrawBlockItemJsonTexturesEditor(json);
            changed |= EditBlockItemJsonOptionalString(json, "drawtype", "Draw type##block-json-drawtype", saveOnly: true);
            changed |= EditBlockItemJsonOptionalString(json, "renderpass", "Render pass##block-json-renderpass", saveOnly: true);
            changed |= EditBlockItemJsonOptionalBool(json, "ambientocclusion", "Ambient occlusion##block-json-ao", saveOnly: true, defaultValue: true);
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Behaviors, drops, and attributes##block-json-behaviors", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            changed |= DrawBlockItemJsonBehaviorsEditor(json);
            changed |= DrawBlockItemJsonDropsEditor(json);
            changed |= DrawBlockItemJsonAttributesEditor(json);
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Gameplay properties##block-json-gameplay"))
        {
            ImGui.Indent();
            changed |= EditBlockItemJsonTokenField(json, "combustibleProps", "Combustible props JSON##block-json-combustible", new JObject(), saveOnly: true);
            changed |= EditBlockItemJsonTokenField(json, "nutritionProps", "Nutrition props JSON##block-json-nutrition", new JObject(), saveOnly: true);
            changed |= EditBlockItemJsonTokenField(json, "transitionableProps", "Transitionable props JSON##block-json-transitionable", new JArray(), saveOnly: true);
            changed |= EditBlockItemJsonTokenField(json, "storageFlags", "Storage flags JSON##block-json-storage-flags", new JArray(), saveOnly: true);
            changed |= EditBlockItemJsonTokenField(json, "heldTpIdleAnimation", "Held animation JSON##block-json-held-animation", new JObject(), saveOnly: true);
            ImGui.Unindent();
        }

        return changed;
    }

    private bool DrawBlockItemJsonVariantGroupsEditor(JObject json)
    {
        DevToolsCollectibleDocumentDraft document = CreateBlockItemJsonDraft(json);
        List<DevToolsCollectibleVariantGroupDraft> groups = document.GetVariantGroups().Select(CloneBlockItemJsonVariantGroup).ToList();
        bool changed = false;

        if (ImGui.TreeNodeEx("Variant groups##block-json-variantgroups", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawBlockItemJsonScopeLabel(saveOnly: true);
            if (ImGui.SmallButton("Add group##block-json-add-variantgroup"))
            {
                groups.Add(new DevToolsCollectibleVariantGroupDraft { Code = "type" });
                changed = true;
            }

            for (int index = 0; index < groups.Count; index++)
            {
                DevToolsCollectibleVariantGroupDraft group = groups[index];
                ImGui.PushID($"block-json-variant-{index}");
                ImGui.Separator();
                ImGui.TextDisabled($"#{index + 1}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Up") && index > 0)
                {
                    (groups[index - 1], groups[index]) = (groups[index], groups[index - 1]);
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Down") && index + 1 < groups.Count)
                {
                    (groups[index + 1], groups[index]) = (groups[index], groups[index + 1]);
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Duplicate"))
                {
                    groups.Insert(index + 1, CloneBlockItemJsonVariantGroup(group));
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                {
                    groups.RemoveAt(index);
                    changed = true;
                    ImGui.PopID();
                    break;
                }

                string code = group.Code;
                ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X * 0.38f));
                if (ImGui.InputText("Code", ref code, 128))
                {
                    group.Code = code;
                    changed = true;
                }

                string states = string.Join(", ", group.States);
                ImGui.SetNextItemWidth(-float.Epsilon);
                if (ImGui.InputText("States", ref states, 1024))
                {
                    group.States.Clear();
                    group.States.AddRange(states.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                    changed = true;
                }

                string loadFromProperties = group.LoadFromProperties;
                ImGui.SetNextItemWidth(-float.Epsilon);
                if (ImGui.InputText("loadFromProperties", ref loadFromProperties, 240))
                {
                    group.LoadFromProperties = loadFromProperties;
                    changed = true;
                }

                JObject groupExtra = group.Extra;
                if (DrawBlockItemJsonExtraObjectEditor($"Variant extra fields##block-json-variant-extra-{index}", $"variant-extra-{index}", ref groupExtra))
                {
                    group.Extra = groupExtra;
                    changed = true;
                }
                ImGui.PopID();
            }

            if (groups.Count > 0)
            {
                DevToolsCollectibleDocumentDraft previewDoc = CreateBlockItemJsonDraft(json);
                previewDoc.SetVariantGroups(groups);
                List<string> expanded = previewDoc.ExpandVariantCodes(limit: 24);
                if (expanded.Count > 0)
                {
                    ImGui.TextDisabled($"Expanded preview: {string.Join(", ", expanded.Take(8))}{(expanded.Count > 8 ? " ..." : "")}");
                }
            }

            ImGui.TreePop();
        }

        if (changed)
        {
            document.SetVariantGroups(groups);
            ApplyBlockItemJsonPropertyFromDraft(json, document, "variantgroups");
        }

        return changed;
    }

    private bool DrawBlockItemJsonShapeEditor(JObject json)
    {
        bool changed = false;
        JObject shape = json["shape"] as JObject ?? [];
        if (ImGui.TreeNodeEx("Shape##block-json-shape-field", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawBlockItemJsonScopeLabel(saveOnly: true);
            string shapeBase = shape["base"]?.ToString() ?? "";
            ImGui.SetNextItemWidth(-float.Epsilon);
            if (ImGui.InputText("Base##block-json-shape-base", ref shapeBase, 512))
            {
                shape["base"] = shapeBase.Trim();
                json["shape"] = shape;
                changed = true;
            }

            string rotateX = shape["rotateX"]?.ToString() ?? "";
            string rotateY = shape["rotateY"]?.ToString() ?? "";
            string rotateZ = shape["rotateZ"]?.ToString() ?? "";
            changed |= EditBlockItemJsonOptionalNumberToken(shape, "rotateX", "Rotate X##block-json-shape-rotatex", ref rotateX);
            changed |= EditBlockItemJsonOptionalNumberToken(shape, "rotateY", "Rotate Y##block-json-shape-rotatey", ref rotateY);
            changed |= EditBlockItemJsonOptionalNumberToken(shape, "rotateZ", "Rotate Z##block-json-shape-rotatez", ref rotateZ);
            if (changed) json["shape"] = shape;

            changed |= DrawBlockItemJsonExtraObjectEditor("Shape extra fields##block-json-shape-extra", "shape-extra", ref shape, knownKeys: ["base", "rotateX", "rotateY", "rotateZ", "offsetX", "offsetY", "offsetZ"]);
            if (changed) json["shape"] = shape;
            if (ImGui.SmallButton("Remove shape##block-json-remove-shape"))
            {
                json.Remove("shape");
                changed = true;
            }
            ImGui.TreePop();
        }

        return changed;
    }

    private bool DrawBlockItemJsonTexturesEditor(JObject json)
    {
        DevToolsCollectibleDocumentDraft document = CreateBlockItemJsonDraft(json);
        List<KeyValuePair<string, JToken>> textures = document.GetTextures().ToList();
        bool changed = false;

        if (ImGui.TreeNodeEx("Textures##block-json-textures", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawBlockItemJsonScopeLabel(saveOnly: true);
            if (ImGui.SmallButton("Add texture##block-json-add-texture"))
            {
                textures.Add(new("all", ""));
                changed = true;
            }

            for (int index = 0; index < textures.Count; index++)
            {
                string key = textures[index].Key;
                JToken value = textures[index].Value;
                ImGui.PushID($"block-json-texture-{index}");
                ImGui.Separator();
                ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X * 0.28f));
                if (ImGui.InputText("Slot", ref key, 128))
                {
                    textures[index] = new(key, value);
                    changed = true;
                }

                string bufferKey = GetBlockItemJsonFieldBufferKey($"texture-{index}");
                if (!_blockItemJsonFieldBuffers.TryGetValue(bufferKey, out string? valueJson))
                {
                    valueJson = JsonConvert.SerializeObject(value, Formatting.Indented);
                }

                ImGui.InputTextMultiline("Value JSON", ref valueJson, 32 * 1024, new NVector2(-float.Epsilon, 72f), ImGuiInputTextFlags.AllowTabInput);
                _blockItemJsonFieldBuffers[bufferKey] = valueJson;
                if (ImGui.SmallButton("Apply texture value"))
                {
                    JToken? parsed = DevToolsJson.TryParseToken(valueJson, useVintageStoryFallback: false);
                    if (parsed == null && !valueJson.TrimStart().StartsWith('{') && !valueJson.TrimStart().StartsWith('['))
                    {
                        parsed = valueJson.Trim();
                    }

                    if (parsed == null)
                    {
                        _blockItemJsonStatus = $"Texture '{key}' JSON is malformed.";
                    }
                    else
                    {
                        textures[index] = new(key, parsed);
                        changed = true;
                    }
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove texture"))
                {
                    textures.RemoveAt(index);
                    _blockItemJsonFieldBuffers.Remove(bufferKey);
                    changed = true;
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }

            ImGui.TreePop();
        }

        if (changed)
        {
            document.SetTextures(textures);
            ApplyBlockItemJsonPropertyFromDraft(json, document, "textures");
        }

        return changed;
    }

    private bool DrawBlockItemJsonBehaviorsEditor(JObject json)
    {
        DevToolsCollectibleDocumentDraft document = CreateBlockItemJsonDraft(json);
        List<DevToolsCollectibleBehaviorDraft> behaviors = document.GetBehaviors().Select(behavior => behavior.Clone()).ToList();
        bool changed = false;

        if (ImGui.TreeNodeEx("Behaviors##block-json-behaviors-field", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawBlockItemJsonScopeLabel(saveOnly: true);
            if (ImGui.SmallButton("Add behavior##block-json-add-behavior"))
            {
                behaviors.Add(new DevToolsCollectibleBehaviorDraft { Name = "BehaviorName" });
                changed = true;
            }

            for (int index = 0; index < behaviors.Count; index++)
            {
                DevToolsCollectibleBehaviorDraft behavior = behaviors[index];
                ImGui.PushID($"block-json-behavior-{index}");
                ImGui.Separator();
                ImGui.TextDisabled($"#{index + 1}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Top") && index > 0)
                {
                    behaviors.RemoveAt(index);
                    behaviors.Insert(0, behavior);
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Up") && index > 0)
                {
                    (behaviors[index - 1], behaviors[index]) = (behaviors[index], behaviors[index - 1]);
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Down") && index + 1 < behaviors.Count)
                {
                    (behaviors[index + 1], behaviors[index]) = (behaviors[index], behaviors[index + 1]);
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Bottom") && index + 1 < behaviors.Count)
                {
                    behaviors.RemoveAt(index);
                    behaviors.Add(behavior);
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Duplicate"))
                {
                    behaviors.Insert(index + 1, behavior.Clone());
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                {
                    behaviors.RemoveAt(index);
                    changed = true;
                    ImGui.PopID();
                    break;
                }

                string name = behavior.Name;
                ImGui.SetNextItemWidth(-float.Epsilon);
                if (ImGui.InputText("Name", ref name, 160))
                {
                    behavior.Name = name;
                    changed = true;
                }

                JObject behaviorExtra = behavior.Extra;
                if (DrawBlockItemJsonExtraObjectEditor($"Behavior extra fields##block-json-behavior-extra-{index}", $"behavior-extra-{index}", ref behaviorExtra))
                {
                    behavior.Extra = behaviorExtra;
                    changed = true;
                }
                ImGui.PopID();
            }

            ImGui.TreePop();
        }

        if (changed)
        {
            document.SetBehaviors(behaviors);
            ApplyBlockItemJsonPropertyFromDraft(json, document, "behaviors");
        }

        return changed;
    }

    private bool DrawBlockItemJsonDropsEditor(JObject json)
    {
        DevToolsCollectibleDocumentDraft document = CreateBlockItemJsonDraft(json);
        List<DevToolsCollectibleDropDraft> drops = document.GetDrops().Select(drop => drop.Clone()).ToList();
        bool changed = false;

        if (ImGui.TreeNodeEx("Drops##block-json-drops", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawBlockItemJsonScopeLabel(saveOnly: true);
            if (ImGui.SmallButton("Add drop##block-json-add-drop"))
            {
                drops.Add(new DevToolsCollectibleDropDraft { Type = "item" });
                changed = true;
            }

            for (int index = 0; index < drops.Count; index++)
            {
                DevToolsCollectibleDropDraft drop = drops[index];
                ImGui.PushID($"block-json-drop-{index}");
                ImGui.Separator();
                if (ImGui.SmallButton("Up") && index > 0)
                {
                    (drops[index - 1], drops[index]) = (drops[index], drops[index - 1]);
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Down") && index + 1 < drops.Count)
                {
                    (drops[index + 1], drops[index]) = (drops[index], drops[index + 1]);
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Duplicate"))
                {
                    drops.Insert(index + 1, drop.Clone());
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                {
                    drops.RemoveAt(index);
                    changed = true;
                    ImGui.PopID();
                    break;
                }

                string type = drop.Type;
                ImGui.SetNextItemWidth(120f);
                if (ImGui.InputText("Type", ref type, 64))
                {
                    drop.Type = type;
                    changed = true;
                }
                string code = drop.Code;
                ImGui.SetNextItemWidth(-float.Epsilon);
                if (ImGui.InputText("Code", ref code, 240))
                {
                    drop.Code = code;
                    changed = true;
                }
                string quantity = drop.QuantityJson;
                ImGui.SetNextItemWidth(-float.Epsilon);
                if (ImGui.InputText("Quantity JSON", ref quantity, 128))
                {
                    drop.QuantityJson = quantity;
                    changed = true;
                }

                JObject dropExtra = drop.Extra;
                if (DrawBlockItemJsonExtraObjectEditor($"Drop extra fields##block-json-drop-extra-{index}", $"drop-extra-{index}", ref dropExtra))
                {
                    drop.Extra = dropExtra;
                    changed = true;
                }
                ImGui.PopID();
            }

            ImGui.TreePop();
        }

        if (changed)
        {
            document.SetDrops(drops);
            ApplyBlockItemJsonPropertyFromDraft(json, document, "drops");
        }

        return changed;
    }

    private bool DrawBlockItemJsonAttributesEditor(JObject json)
    {
        bool changed = false;
        if (ImGui.TreeNodeEx("Attributes##block-json-attributes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawBlockItemJsonScopeLabel(saveOnly: false, runtimeAttributes: true);
            ImGui.TextDisabled("Path uses / separators, for example handbook/group or combustion/burnTemperature.");
            ImGui.SetNextItemWidth(-float.Epsilon);
            ImGui.InputText("Path##block-json-attribute-path", ref _blockItemJsonAttributePath, 512);
            ImGui.InputTextMultiline("Value JSON##block-json-attribute-value", ref _blockItemJsonAttributeValueJson, 64 * 1024, new NVector2(-float.Epsilon, 70f), ImGuiInputTextFlags.AllowTabInput);
            if (ImGui.SmallButton("Set attribute##block-json-set-attribute"))
            {
                if (!DevToolsJson.TryParseToken(_blockItemJsonAttributeValueJson, out JToken? token, out string error, useVintageStoryFallback: false) || token == null)
                {
                    _blockItemJsonStatus = $"Attribute value is malformed: {error}";
                }
                else
                {
                    DevToolsCollectibleDocumentDraft document = CreateBlockItemJsonDraft(json);
                    document.SetAttribute(_blockItemJsonAttributePath.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), token);
                    ApplyBlockItemJsonPropertyFromDraft(json, document, "attributes");
                    changed = true;
                }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Format value##block-json-format-attribute-value"))
            {
                if (DevToolsJsonTextTools.TryFormat(_blockItemJsonAttributeValueJson, out string formatted, out string error))
                {
                    _blockItemJsonAttributeValueJson = formatted;
                }
                else
                {
                    _blockItemJsonStatus = $"Attribute value format failed: {error}";
                }
            }

            changed |= EditBlockItemJsonTokenField(json, "attributes", "Attributes raw JSON##block-json-attributes-raw", new JObject(), saveOnly: false, runtimeAttributes: true);
            ImGui.TreePop();
        }

        return changed;
    }

    private DevToolsCollectibleDocumentDraft CreateBlockItemJsonDraft(JObject json)
    {
        return new()
        {
            Kind = _blockItemJsonLoadedKind,
            Domain = _blockItemJsonOutputDomain,
            AssetPath = _blockItemJsonOutputPath,
            Root = (JObject)json.DeepClone()
        };
    }

    private static DevToolsCollectibleVariantGroupDraft CloneBlockItemJsonVariantGroup(DevToolsCollectibleVariantGroupDraft source)
    {
        DevToolsCollectibleVariantGroupDraft clone = new()
        {
            Code = source.Code,
            LoadFromProperties = source.LoadFromProperties,
            Extra = (JObject)source.Extra.DeepClone()
        };
        clone.States.AddRange(source.States);
        return clone;
    }

    private static void ApplyBlockItemJsonPropertyFromDraft(JObject target, DevToolsCollectibleDocumentDraft document, string propertyName)
    {
        if (document.Root[propertyName] == null)
        {
            target.Remove(propertyName);
        }
        else
        {
            target[propertyName] = document.Root[propertyName]!.DeepClone();
        }
    }

    private bool EditBlockItemJsonOptionalNumberToken(JObject json, string propertyName, string label, ref string buffer)
    {
        bool changed = false;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputText(label, ref buffer, 64))
        {
            if (string.IsNullOrWhiteSpace(buffer))
            {
                json.Remove(propertyName);
                changed = true;
            }
            else if (double.TryParse(buffer, CultureInfo.InvariantCulture, out double value))
            {
                json[propertyName] = value;
                changed = true;
            }
            else
            {
                _blockItemJsonStatus = $"{propertyName} must be numeric.";
            }
        }

        return changed;
    }

    private bool DrawBlockItemJsonExtraObjectEditor(string label, string bufferSuffix, ref JObject obj, IReadOnlyCollection<string>? knownKeys = null)
    {
        bool changed = false;
        if (!ImGui.TreeNode(label)) return false;

        JObject extra = [];
        HashSet<string>? known = knownKeys == null ? null : new HashSet<string>(knownKeys, StringComparer.OrdinalIgnoreCase);
        foreach (JProperty property in obj.Properties())
        {
            if (known != null && known.Contains(property.Name)) continue;
            extra[property.Name] = property.Value.DeepClone();
        }

        string bufferKey = GetBlockItemJsonFieldBufferKey(bufferSuffix);
        if (!_blockItemJsonFieldBuffers.TryGetValue(bufferKey, out string? buffer))
        {
            buffer = JsonConvert.SerializeObject(extra, Formatting.Indented);
        }

        ImGui.InputTextMultiline("##extra-json", ref buffer, 128 * 1024, new NVector2(-float.Epsilon, 86f), ImGuiInputTextFlags.AllowTabInput);
        _blockItemJsonFieldBuffers[bufferKey] = buffer;
        if (ImGui.SmallButton("Apply extra fields"))
        {
            if (!DevToolsJson.TryParseToken(buffer, out JToken? token, out string error, useVintageStoryFallback: false) || token is not JObject parsed)
            {
                _blockItemJsonStatus = $"Extra fields JSON is malformed: {error}";
            }
            else
            {
                if (known != null)
                {
                    foreach (JProperty property in obj.Properties().ToList())
                    {
                        if (!known.Contains(property.Name))
                        {
                            property.Remove();
                        }
                    }
                }
                else
                {
                    obj.RemoveAll();
                }

                foreach (JProperty property in parsed.Properties())
                {
                    if (known != null && known.Contains(property.Name)) continue;
                    obj[property.Name] = property.Value.DeepClone();
                }

                changed = true;
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Format extra fields"))
        {
            if (DevToolsJsonTextTools.TryFormat(buffer, out string formatted, out string formatError))
            {
                _blockItemJsonFieldBuffers[bufferKey] = formatted;
            }
            else
            {
                _blockItemJsonStatus = $"Extra fields format failed: {formatError}";
            }
        }

        ImGui.TreePop();
        return changed;
    }

    private static void DrawBlockItemJsonScopeLabel(bool saveOnly, bool runtimeAttributes = false)
    {
        ImGui.SameLine();
        ImGui.TextDisabled(runtimeAttributes ? "live attributes + source save" : saveOnly ? "source save" : "runtime + source");
    }

    private bool EditBlockItemJsonString(JObject json, string propertyName, string label, bool saveOnly, int maxLength)
    {
        string value = json[propertyName]?.ToString() ?? "";
        ImGui.SetNextItemWidth(Math.Max(180f, ImGui.GetContentRegionAvail().X - 170f));
        if (!ImGui.InputText(label, ref value, (uint)maxLength))
        {
            DrawBlockItemJsonScopeLabel(saveOnly);
            return false;
        }

        if (string.IsNullOrWhiteSpace(value)) json.Remove(propertyName);
        else json[propertyName] = value;
        DrawBlockItemJsonScopeLabel(saveOnly);
        return true;
    }

    private bool EditBlockItemJsonOptionalString(JObject json, string propertyName, string label, bool saveOnly)
    {
        if (json[propertyName] == null)
        {
            if (!ImGui.Button($"Add {label}")) return false;
            json[propertyName] = "";
            DrawBlockItemJsonScopeLabel(saveOnly);
            return true;
        }

        bool changed = EditBlockItemJsonString(json, propertyName, label, saveOnly, maxLength: 512);
        ImGui.SameLine();
        if (ImGui.Button($"Remove##block-json-remove-{propertyName}"))
        {
            json.Remove(propertyName);
            return true;
        }
        return changed;
    }

    private bool EditBlockItemJsonOptionalBool(JObject json, string propertyName, string label, bool saveOnly, bool defaultValue)
    {
        if (json[propertyName] == null)
        {
            if (!ImGui.Button($"Add {label}")) return false;
            json[propertyName] = defaultValue;
            DrawBlockItemJsonScopeLabel(saveOnly);
            return true;
        }

        bool value = json[propertyName]?.Value<bool?>() ?? defaultValue;
        if (ImGui.Checkbox(label, ref value))
        {
            json[propertyName] = value;
            DrawBlockItemJsonScopeLabel(saveOnly);
            return true;
        }

        DrawBlockItemJsonScopeLabel(saveOnly);
        ImGui.SameLine();
        if (ImGui.Button($"Remove##block-json-remove-{propertyName}"))
        {
            json.Remove(propertyName);
            return true;
        }

        return false;
    }

    private bool EditBlockItemJsonTokenField(JObject json, string propertyName, string label, JToken defaultToken, bool saveOnly, bool runtimeAttributes = false)
    {
        if (json[propertyName] == null)
        {
            if (!ImGui.Button($"Add {label}")) return false;
            json[propertyName] = defaultToken.DeepClone();
            DrawBlockItemJsonScopeLabel(saveOnly, runtimeAttributes);
            return true;
        }

        bool changed = false;
        ImGuiTreeNodeFlags flags = propertyName.Equals("attributes", StringComparison.OrdinalIgnoreCase) ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (ImGui.TreeNodeEx(label, flags))
        {
            DrawBlockItemJsonScopeLabel(saveOnly, runtimeAttributes);
            string bufferKey = GetBlockItemJsonFieldBufferKey(propertyName);
            if (!_blockItemJsonFieldBuffers.TryGetValue(bufferKey, out string? buffer))
            {
                buffer = JsonConvert.SerializeObject(json[propertyName] ?? defaultToken, Formatting.Indented);
            }

            ImGui.InputTextMultiline($"##block-json-field-{propertyName}", ref buffer, 256 * 1024, new NVector2(-float.Epsilon, 110f), ImGuiInputTextFlags.AllowTabInput);
            _blockItemJsonFieldBuffers[bufferKey] = buffer;

            if (ImGui.Button($"Apply##block-json-apply-{propertyName}"))
            {
                JToken? parsed = DevToolsJson.TryParseToken(buffer, useVintageStoryFallback: false);
                if (parsed == null)
                {
                    _blockItemJsonStatus = $"{propertyName} JSON is malformed.";
                }
                else
                {
                    json[propertyName] = parsed;
                    _blockItemJsonFieldBuffers[bufferKey] = JsonConvert.SerializeObject(parsed, Formatting.Indented);
                    changed = true;
                }
            }
            ImGui.SameLine();
            if (ImGui.Button($"Format##block-json-format-{propertyName}"))
            {
                if (DevToolsJsonTextTools.TryFormat(buffer, out string formatted, out string formatError))
                {
                    _blockItemJsonFieldBuffers[bufferKey] = formatted;
                }
                else
                {
                    _blockItemJsonStatus = $"{propertyName} format failed: {formatError}";
                }
            }
            ImGui.SameLine();
            if (ImGui.Button($"Remove##block-json-remove-{propertyName}"))
            {
                json.Remove(propertyName);
                _blockItemJsonFieldBuffers.Remove(bufferKey);
                changed = true;
            }

            ImGui.TreePop();
        }

        return changed;
    }

    private static string GetBlockItemJsonFieldBufferKey(BlockItemJsonEntry entry, string propertyName)
    {
        return $"{entry.Key}:{propertyName}";
    }

    private string GetBlockItemJsonFieldBufferKey(string propertyName)
    {
        string key = string.IsNullOrWhiteSpace(_blockItemJsonLoadedKey) ? "unloaded" : _blockItemJsonLoadedKey;
        return $"{key}:{propertyName}";
    }

    private void DrawBlockItemJsonInspector(NVector2 size, bool showDiagnostics)
    {
        ImGui.BeginChild("##block-item-json-inspector", size, true);

        if (string.IsNullOrWhiteSpace(_blockItemJsonLoadedKey))
        {
            ImGui.TextDisabled("Select or create a block/item JSON document.");
            ImGui.EndChild();
            return;
        }

        ImGui.SeparatorText("Document");
        ImGui.TextDisabled($"{(_blockItemJsonLoadedKind == DevToolsCollectibleKind.Block ? "Block" : "Item")}: {_blockItemJsonLoadedLabel}");
        ImGui.TextDisabled($"Loaded from: {(_blockItemJsonLoadedIsRuntime ? "runtime collectable" : _blockItemJsonLoadedIsAuthored ? "authored override" : "source JSON")}");

        bool valid = TryParseJsonObject(_blockItemJsonText) != null;
        ImGui.TextColored(valid ? new NVector4(0.42f, 0.85f, 0.42f, 1f) : new NVector4(1f, 0.38f, 0.32f, 1f), valid ? "JSON valid" : "JSON invalid");

        ImGui.SetNextItemWidth(-float.Epsilon);
        ImGui.InputText("Output domain##block-json-output-domain", ref _blockItemJsonOutputDomain, 120);
        ImGui.SetNextItemWidth(-float.Epsilon);
        ImGui.InputText("Asset path##block-json-output-path", ref _blockItemJsonOutputPath, 512);

        if (ImGui.Button("Format JSON##block-json-format"))
        {
            FormatBlockItemJsonText();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reload selected##block-json-reload-selected"))
        {
            ReloadSelectedBlockItemJsonDocument();
        }

        if (!_blockItemJsonLoadedIsRuntime) ImGui.BeginDisabled();
        if (!valid) ImGui.BeginDisabled();
        if (ImGui.Button("Apply runtime attributes##block-json-runtime"))
        {
            ApplyBlockItemJsonRuntime(force: true);
        }
        if (!valid) ImGui.EndDisabled();
        if (!_blockItemJsonLoadedIsRuntime) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Patches runtime Attributes only. Shape and texture edits are authored-source changes and preview locally without retessellating the running object.");
        }

        ImGui.SameLine();
        BlockItemJsonEntry? runtimeEntry = FindLoadedBlockItemRuntimeEntry();
        bool canRevert = runtimeEntry != null && _liveApplyManager.CanRevert(GetBlockItemJsonLiveKey(runtimeEntry));
        if (!canRevert) ImGui.BeginDisabled();
        if (ImGui.Button("Revert runtime##block-json-revert"))
        {
            _blockItemJsonLiveAppliedHash = "";
            _blockItemJsonStatus = runtimeEntry == null ? "No loaded runtime object to revert." : _liveApplyManager.Revert(GetBlockItemJsonLiveKey(runtimeEntry));
        }
        if (!canRevert) ImGui.EndDisabled();

        if (!valid) ImGui.BeginDisabled();
        if (ImGui.Button("Save authored JSON##block-json-save"))
        {
            QueueSourceSave(TrySaveBlockItemJsonToSource(), status => _blockItemJsonStatus = status);
        }
        if (!valid) ImGui.EndDisabled();

        if (ImGui.Button("New authored copy##block-json-new-copy"))
        {
            LoadBlockItemJsonCurrentAsAuthoredCopy();
        }

        if (_blockItemJsonLoadedKind == DevToolsCollectibleKind.Block)
        {
            if (ImGui.Button("Animate this block##block-json-animate-block"))
            {
                OpenSelectedBlockInAnimationEditor();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Open this block shape in the animation editor. If the shape has no animations yet, the animation editor can create the first one.");
            }
        }

        if (!string.IsNullOrWhiteSpace(_blockItemJsonStatus))
        {
            ImGui.TextWrapped(_blockItemJsonStatus);
        }

        _blockItemJsonDiagnostics.Draw("block-item-json", showDiagnostics);

        ImGui.SeparatorText("Preview");
        ImGui.SetNextItemWidth(-float.Epsilon);
        ImGui.Combo("Mode##block-json-preview-mode", ref _blockItemJsonPreviewMode, BlockItemJsonPreviewModeLabels, BlockItemJsonPreviewModeLabels.Length);
        DrawBlockItemJsonPreviewPanel();

        ImGui.EndChild();
    }

    private void EnsureBlockItemJsonEntryLoaded(BlockItemJsonEntry entry)
    {
        if (string.Equals(_blockItemJsonLoadedKey, entry.Key, StringComparison.Ordinal)) return;
        LoadBlockItemJsonEntry(entry, keepDirty: true);
    }

    private void EnsureBlockItemJsonSourceEntryLoaded(BlockItemJsonSourceEntry entry)
    {
        if (string.Equals(_blockItemJsonLoadedKey, entry.Key, StringComparison.Ordinal)) return;
        LoadBlockItemJsonSourceEntry(entry, keepDirty: true);
    }

    private void LoadBlockItemJsonEntry(BlockItemJsonEntry entry, bool keepDirty)
    {
        if (keepDirty && IsBlockItemJsonDirty && !string.IsNullOrWhiteSpace(_blockItemJsonLoadedKey)) return;

        try
        {
            JObject json = BuildBlockItemJsonDocument(entry);
            IAsset? sourceAsset = FindCollectibleSourceAsset(entry.Collectible);
            _blockItemJsonLoadedKind = entry.IsBlock ? DevToolsCollectibleKind.Block : DevToolsCollectibleKind.Item;
            _blockItemJsonLoadedDomain = sourceAsset?.Location.Domain ?? entry.Domain;
            _blockItemJsonLoadedAssetPath = sourceAsset?.Location.Path ?? $"{(entry.IsBlock ? "blocktypes" : "itemtypes")}/{EnsureJsonFilePath(entry.Collectible.Code?.Path ?? "unknown")}";
            _blockItemJsonLoadedSourceText = ReadAssetText(sourceAsset);
            _blockItemJsonOutputDomain = _blockItemJsonLoadedDomain;
            _blockItemJsonOutputPath = _blockItemJsonLoadedAssetPath;
            _blockItemJsonLoadedIsRuntime = true;
            _blockItemJsonLoadedIsAuthored = false;
            _blockItemJsonLoadedLabel = entry.Label;
            _blockItemJsonText = JsonConvert.SerializeObject(json, Formatting.Indented);
            _blockItemJsonOriginalText = _blockItemJsonText;
            _blockItemJsonLoadedKey = entry.Key;
            _blockItemJsonLiveAppliedHash = "";
            _blockItemJsonFieldBuffers.Clear();
            _blockItemJsonTextHistory.Reset(_blockItemJsonText);
            DevToolsTextDiffView.Invalidate("block-item-json-raw-diff");
            InvalidateBlockItemJsonPreview();
            _blockItemJsonStatus = $"Loaded {entry.Label}.";
        }
        catch (Exception exception)
        {
            _blockItemJsonDiagnostics.Exception($"Failed to load {entry.Label}", exception);
            _blockItemJsonStatus = $"Failed to load {entry.Label}: {exception.Message}";
        }
    }

    private void LoadBlockItemJsonSourceEntry(BlockItemJsonSourceEntry entry, bool keepDirty)
    {
        if (keepDirty && IsBlockItemJsonDirty && !string.IsNullOrWhiteSpace(_blockItemJsonLoadedKey)) return;

        try
        {
            string text = ReadAssetText(entry.Asset);
            DevToolsCollectibleDocumentDraft document = DevToolsCollectibleDocumentDraft.FromJson(text, entry.IsBlock ? DevToolsCollectibleKind.Block : DevToolsCollectibleKind.Item, entry.Domain, entry.AssetPath);
            _blockItemJsonLoadedKind = document.Kind;
            _blockItemJsonLoadedDomain = document.Domain;
            _blockItemJsonLoadedAssetPath = document.AssetPath;
            _blockItemJsonLoadedSourceText = text;
            _blockItemJsonOutputDomain = document.Domain;
            _blockItemJsonOutputPath = document.AssetPath;
            _blockItemJsonLoadedIsRuntime = false;
            _blockItemJsonLoadedIsAuthored = entry.Authored;
            _blockItemJsonLoadedLabel = entry.Label;
            _blockItemJsonText = document.ToJson();
            _blockItemJsonOriginalText = _blockItemJsonText;
            _blockItemJsonLoadedKey = entry.Key;
            _blockItemJsonLiveAppliedHash = "";
            _blockItemJsonFieldBuffers.Clear();
            _blockItemJsonTextHistory.Reset(_blockItemJsonText);
            DevToolsTextDiffView.Invalidate("block-item-json-raw-diff");
            InvalidateBlockItemJsonPreview();
            _blockItemJsonStatus = $"Loaded {entry.Label}.";
        }
        catch (Exception exception)
        {
            _blockItemJsonDiagnostics.Exception($"Failed to load {entry.Label}", exception);
            _blockItemJsonStatus = $"Failed to load {entry.Label}: {exception.Message}";
        }
    }

    private void LoadNewBlockItemJsonDocument(DevToolsCollectibleKind kind)
    {
        if (IsBlockItemJsonDirty && !string.IsNullOrWhiteSpace(_blockItemJsonLoadedKey))
        {
            _blockItemJsonStatus = "Current document has unsaved edits. Save or reload it before creating a new document.";
            return;
        }

        DevToolsCollectibleDocumentDraft document = DevToolsCollectibleDocumentDraft.Empty(kind, "game", kind == DevToolsCollectibleKind.Block ? "new-block" : "new-item");
        _blockItemJsonLoadedKind = kind;
        _blockItemJsonLoadedDomain = document.Domain;
        _blockItemJsonLoadedAssetPath = document.AssetPath;
        _blockItemJsonLoadedSourceText = "{}";
        _blockItemJsonOutputDomain = document.Domain;
        _blockItemJsonOutputPath = document.AssetPath;
        _blockItemJsonLoadedIsRuntime = false;
        _blockItemJsonLoadedIsAuthored = true;
        _blockItemJsonLoadedLabel = kind == DevToolsCollectibleKind.Block ? "New block JSON" : "New item JSON";
        _blockItemJsonText = document.ToJson();
        _blockItemJsonOriginalText = _blockItemJsonText;
        _blockItemJsonLoadedKey = $"new:{kind}:{Guid.NewGuid():N}";
        _blockItemJsonLiveAppliedHash = "";
        _blockItemJsonFieldBuffers.Clear();
        _blockItemJsonTextHistory.Reset(_blockItemJsonText);
        DevToolsTextDiffView.Invalidate("block-item-json-raw-diff");
        InvalidateBlockItemJsonPreview();
        _blockItemJsonStatus = $"Created {_blockItemJsonLoadedLabel}.";
    }

    private void LoadBlockItemJsonCurrentAsAuthoredCopy()
    {
        if (string.IsNullOrWhiteSpace(_blockItemJsonLoadedKey)) return;
        _blockItemJsonLoadedIsRuntime = false;
        _blockItemJsonLoadedIsAuthored = true;
        _blockItemJsonLoadedKey = $"copy:{_blockItemJsonLoadedKey}";
        _blockItemJsonLoadedLabel = $"Authored copy: {_blockItemJsonLoadedLabel}";
        _blockItemJsonOriginalText = _blockItemJsonText;
        _blockItemJsonTextHistory.Reset(_blockItemJsonText);
        _blockItemJsonStatus = "Prepared an authored copy. Edit output domain/path, then save.";
    }

    private void ReloadSelectedBlockItemJsonDocument()
    {
        if (_blockItemJsonBrowserMode == 0 && SelectedBlockItemJsonAsset is BlockItemJsonEntry runtime)
        {
            LoadBlockItemJsonEntry(runtime, keepDirty: false);
            return;
        }

        if (_blockItemJsonBrowserMode != 0 && SelectedBlockItemJsonSourceAsset is BlockItemJsonSourceEntry source)
        {
            LoadBlockItemJsonSourceEntry(source, keepDirty: false);
            return;
        }

        _blockItemJsonStatus = "No selected document to reload.";
    }

    private void DrawBlockItemJsonPreviewPanel()
    {
        JObject? json = TryParseJsonObject(_blockItemJsonText);
        if (json == null)
        {
            ImGui.TextColored(new NVector4(1f, 0.38f, 0.32f, 1f), "Preview unavailable until JSON parses.");
            return;
        }

        DevToolsCollectibleDocumentDraft document = new()
        {
            Kind = _blockItemJsonLoadedKind,
            Domain = _blockItemJsonOutputDomain,
            AssetPath = _blockItemJsonOutputPath,
            Root = json
        };

        switch (_blockItemJsonPreviewMode)
        {
            case 1:
            {
                string preview = document.ToJson();
                ImGui.InputTextMultiline("##block-json-authored-preview", ref preview, (uint)Math.Max(4096, preview.Length + 1), new NVector2(-float.Epsilon, Math.Max(140f, ImGui.GetContentRegionAvail().Y)), ImGuiInputTextFlags.ReadOnly);
                break;
            }
            case 2:
                DevToolsTextDiffView.Draw("block-item-json-preview-diff", _blockItemJsonOriginalText, document.ToJson(), Math.Max(160f, ImGui.GetContentRegionAvail().Y));
                break;
            case 3:
                DrawBlockItemJsonRenderPreview(document);
                break;
            default:
                DrawBlockItemJsonValidationSummary(document);
                break;
        }
    }

    private void DrawBlockItemJsonValidationSummary(DevToolsCollectibleDocumentDraft document)
    {
        IReadOnlyList<DevToolsCollectibleValidationIssue> issues = BuildBlockItemJsonValidationIssues(document);
        if (issues.Count == 0)
        {
            ImGui.TextColored(new NVector4(0.42f, 0.85f, 0.42f, 1f), "No validation issues found.");
            return;
        }

        foreach (DevToolsCollectibleValidationIssue issue in issues)
        {
            NVector4 color = issue.Severity == DevToolsCollectibleIssueSeverity.Error
                ? new NVector4(1f, 0.38f, 0.32f, 1f)
                : new NVector4(1f, 0.72f, 0.32f, 1f);
            ImGui.TextColored(color, $"{issue.Severity}: {issue.Message}");
        }
    }

    private IReadOnlyList<DevToolsCollectibleValidationIssue> BuildBlockItemJsonValidationIssues(DevToolsCollectibleDocumentDraft document)
    {
        return document.Validate(
            shapeExists: reference => TryGetBlockItemJsonAssetReference(reference, "shapes", ".json") != null,
            textureExists: reference => TryGetBlockItemJsonAssetReference(reference, "textures", ".png") != null,
            stackExists: BlockItemJsonStackExists);
    }

    private bool BlockItemJsonStackExists(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        AssetLocation location = AssetLocation.Create(code, "game");
        return _api.World.GetBlock(location) != null || _api.World.GetItem(location) != null;
    }

    private IAsset? TryGetBlockItemJsonAssetReference(string reference, string folder, string extension)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        AssetLocation location = AssetLocation.Create(reference.Trim(), _blockItemJsonOutputDomain);
        string path = location.Path.Replace('\\', '/').TrimStart('/');
        if (!path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
        {
            path = $"{folder}/{path}";
        }

        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            path += extension;
        }

        IAsset? asset = _api.Assets.TryGet(new AssetLocation(location.Domain, path), true);
        if (asset != null) return asset;
        return location.Domain.Equals("game", StringComparison.OrdinalIgnoreCase)
            ? null
            : _api.Assets.TryGet(new AssetLocation("game", path), true);
    }

    private void DrawBlockItemJsonRenderPreview(DevToolsCollectibleDocumentDraft document)
    {
        if (document.Root["shape"] is not JObject shape)
        {
            ImGui.TextDisabled("No shape object to preview.");
            return;
        }

        string shapeBase = shape["base"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(shapeBase))
        {
            ImGui.TextDisabled("shape.base is empty.");
            return;
        }

        IAsset? shapeAsset = TryGetBlockItemJsonAssetReference(shapeBase, "shapes", ".json");
        if (shapeAsset == null)
        {
            ImGui.TextColored(new NVector4(1f, 0.38f, 0.32f, 1f), $"Shape asset not found: {shapeBase}");
            return;
        }

        string cacheKey = $"{shapeAsset.Location}:{shape.ToString(Formatting.None)}";
        if (!string.Equals(_blockItemJsonPreviewKey, cacheKey, StringComparison.Ordinal))
        {
            _blockItemJsonPreviewKey = cacheKey;
            _blockItemJsonPreviewMesh?.Dispose();
            _blockItemJsonPreviewMesh = null;
            _blockItemJsonPreviewSkipReason = null;
            try
            {
                _blockItemJsonPreviewMesh = ModelBuildShapePreviewMesh(ReadAssetText(shapeAsset), shapeAsset.Location.Domain, shapeAsset.Location.ToString(), out _blockItemJsonPreviewSkipReason);
            }
            catch (Exception exception)
            {
                _blockItemJsonPreviewSkipReason = $"Preview failed: {exception.Message}";
                _blockItemJsonDiagnostics.Exception("Block/item render preview failed", exception);
            }
        }

        if (_blockItemJsonPreviewMesh == null)
        {
            ImGui.TextDisabled(_blockItemJsonPreviewSkipReason ?? "Preview produced no mesh.");
            return;
        }

        NVector2 available = ImGui.GetContentRegionAvail();
        NVector2 size = new(Math.Max(220f, available.X), Math.Max(180f, Math.Min(available.Y, 320f)));
        NVector2 min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##block-json-render-preview-viewport", size);
        bool hovered = ImGui.IsItemHovered();
        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            NVector2 delta = ImGui.GetIO().MouseDelta;
            _blockItemJsonPreviewYaw += delta.X * 0.012f;
            _blockItemJsonPreviewPitch = Math.Clamp(_blockItemJsonPreviewPitch + delta.Y * 0.006f, -1.15f, 1.15f);
        }

        if (hovered)
        {
            float wheel = ImGui.GetIO().MouseWheel;
            if (Math.Abs(wheel) > 0.01f)
            {
                _blockItemJsonPreviewDistance = Math.Clamp(_blockItemJsonPreviewDistance * (wheel > 0 ? 0.90f : 1.10f), 1.0f, 28f);
            }
        }

        NVector2 max = min + size;
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0.10f, 0.10f, 0.11f, 1f));
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 1f));
        uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.78f, 0.76f, 0.70f, 1f));
        drawList.AddRectFilled(min, max, background, 4f);

        OpenTK.Mathematics.Vector3 target = _blockItemJsonPreviewMesh.Bounds.Center;
        float distance = Math.Max(_blockItemJsonPreviewDistance, _blockItemJsonPreviewMesh.Bounds.Radius * 2.8f);
        DevToolsPreviewCamera camera = DevToolsPreviewCamera.Orbit(min, max, target, _blockItemJsonPreviewYaw, _blockItemJsonPreviewPitch, distance);
        List<DevToolsPreviewMeshInstance> instances = [new(_blockItemJsonPreviewMesh, CreateIdentityMatrix())];
        int textureId = EnsureBlockItemJsonPreviewRenderer().RenderToTexture(max.X - min.X, max.Y - min.Y, camera, instances, out string? skipReason);
        if (textureId > 0)
        {
            drawList.AddImage(new IntPtr(textureId), min, max, new NVector2(0f, 1f), new NVector2(1f, 0f));
        }
        else if (!string.IsNullOrWhiteSpace(skipReason))
        {
            drawList.AddText(min + new NVector2(12f, 36f), text, $"Preview skipped: {skipReason}");
        }

        drawList.AddText(min + new NVector2(12f, 10f), text, shapeAsset.Location.ToString());
        drawList.AddRect(min, max, border, 4f);
    }

    private DevToolsPreview3DRenderer EnsureBlockItemJsonPreviewRenderer()
    {
        return _blockItemJsonPreviewRenderer ??= new DevToolsPreview3DRenderer(_api);
    }

    private void InvalidateBlockItemJsonPreview()
    {
        _blockItemJsonPreviewKey = "";
        _blockItemJsonPreviewMesh?.Dispose();
        _blockItemJsonPreviewMesh = null;
        _blockItemJsonPreviewSkipReason = null;
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

        _blockItemJsonText = JsonConvert.SerializeObject(json, Formatting.Indented);
        _blockItemJsonStatus = "Formatted JSON.";
    }

    private void ApplyBlockItemJsonRuntime(bool force = false)
    {
        BlockItemJsonEntry? entry = FindLoadedBlockItemRuntimeEntry();
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

    private BlockItemJsonEntry? FindLoadedBlockItemRuntimeEntry()
    {
        if (!_blockItemJsonLoadedIsRuntime) return null;
        return _blockItemJsonAssets.FirstOrDefault(entry => string.Equals(entry.Key, _blockItemJsonLoadedKey, StringComparison.Ordinal));
    }

    private void OpenSelectedBlockInAnimationEditor()
    {
        string code = "";
        if (FindLoadedBlockItemRuntimeEntry() is { IsBlock: true, Collectible: Block runtimeBlock })
        {
            code = runtimeBlock.Code?.ToString() ?? "";
        }
        else if (TryParseJsonObject(_blockItemJsonText) is JObject json)
        {
            string domain = string.IsNullOrWhiteSpace(_blockItemJsonOutputDomain) ? _blockItemJsonLoadedDomain : _blockItemJsonOutputDomain;
            string path = json["code"]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(path))
            {
                code = path.Contains(':', StringComparison.Ordinal) ? path : $"{domain}:{path}";
            }
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            _blockItemJsonStatus = "Cannot open animation editor: block code is empty.";
            return;
        }

        _vanillaIndex.EnsureBlockList(_api);
        if (!_vanillaIndex.SelectBlockByCode(_api, code))
        {
            _blockItemJsonStatus = $"Cannot open animation editor: no loaded runtime block matches {code}.";
            return;
        }

        CommitPendingVanillaHistory();
        RequestVanillaAnimationSourceTab(VanillaAnimationSourceMode.Blocks);
        _vanillaDomainFilter = "";
        _vanillaBlockFilter = code;
        ResetVanillaEntitySelectionState();
        _activeDevToolsTab = DevToolsTab.Animations;
        _blockItemJsonStatus = $"Opened {code} in the animation editor.";
    }

    private SourceSaveResult TrySaveBlockItemJsonToSource()
    {
        try
        {
            JObject? json = TryParseJsonObject(_blockItemJsonText);
            if (json == null) return SourceSaveResult.Fail("Save failed: JSON is invalid.");

            DevToolsCollectibleDocumentDraft document = new()
            {
                Kind = _blockItemJsonLoadedKind,
                Domain = _blockItemJsonOutputDomain,
                AssetPath = _blockItemJsonOutputPath,
                Root = json
            };
            IReadOnlyList<DevToolsCollectibleValidationIssue> issues = BuildBlockItemJsonValidationIssues(document);
            DevToolsCollectibleValidationIssue? error = issues.FirstOrDefault(issue => issue.Severity == DevToolsCollectibleIssueSeverity.Error);
            if (error != null)
            {
                return SourceSaveResult.Fail($"Save failed: {error.Message}");
            }

            string outputPath = GetToolAuthoredAssetPath("block-item-json", document.BuildAssetRelativePath());
            string sourceText = string.IsNullOrEmpty(_blockItemJsonLoadedSourceText) ? "{}" : _blockItemJsonLoadedSourceText;
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : sourceText;
            string newText = document.ToJson();
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved authored JSON to {outputPath}.",
                () =>
                {
                    string result = WriteAuthoredFile(outputPath, newText);
                    _blockItemJsonOriginalText = newText;
                    _blockItemJsonLoadedSourceText = newText;
                    _blockItemJsonLoadedIsAuthored = true;
                    _blockItemJsonLoadedIsRuntime = false;
                    _blockItemJsonOutputDomain = document.Domain;
                    _blockItemJsonOutputPath = document.AssetPath;
                    _blockItemJsonLoadedDomain = document.Domain;
                    _blockItemJsonLoadedAssetPath = document.AssetPath;
                    RebuildVisibleBlockItemJsonAssets();
                    return result;
                });
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            _blockItemJsonDiagnostics.Exception($"Save failed for {_blockItemJsonLoadedLabel}", exception);
            return SourceSaveResult.Fail($"Save failed for {_blockItemJsonLoadedLabel}: {exception.Message}");
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

    private sealed record BlockItemJsonSourceEntry(IAsset Asset, bool IsBlock, bool Authored)
    {
        public string Domain => Asset.Location.Domain;
        public string AssetPath => Asset.Location.Path.Replace('\\', '/');
        public string Key => $"{(Authored ? "authored" : "source")}:{Domain}:{AssetPath}";
        public string Label => $"{(Authored ? "authored" : "source")}:{(IsBlock ? "block" : "item")}:{Domain}:{ImGuiLayoutHelper.CompactAssetCode(AssetPath)}";
        public string SearchText => $"{Label} {Domain} {AssetPath}";
        public string Tooltip => $"{(Authored ? "Authored" : "Source")} {(IsBlock ? "blocktype" : "itemtype")}: {Domain}:{AssetPath}";
    }
}
