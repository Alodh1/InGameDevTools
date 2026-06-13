using System.Diagnostics;
using System.Text;
using InGameDevTools.Utils;
using InGameDevTools.Integration.Transpilers;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VanillaAnimation = Vintagestory.API.Common.Animation;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private void VanillaAnimationsTab(float deltaSeconds)
    {
        ClearActiveTransformGizmo();
        _vanillaIndex.EnsureEntityList(_api);
        _vanillaIndex.EnsureBlockList(_api);
        TrackVanillaLiveOriginals();
        FlushPendingVanillaAutoApply();

        NVector2 available = ImGui.GetContentRegionAvail();
        float splitterThickness = Math.Max(5f, 6f * _devToolsUiScale);
        float topBottomAvailableHeight = Math.Max(1f, available.Y - splitterThickness);
        float bottomMin = Math.Min(topBottomAvailableHeight * 0.45f, 160f * _devToolsUiScale);
        float topMin = Math.Min(topBottomAvailableHeight - bottomMin, Math.Max(_vanillaViewportMinHeight, 280f * _devToolsUiScale));
        float bottomMax = Math.Max(bottomMin, topBottomAvailableHeight - topMin);
        float bottomHeight = Math.Clamp(topBottomAvailableHeight * _vanillaLayoutBottomFraction, bottomMin, bottomMax);
        float topHeight = Math.Max(topMin, topBottomAvailableHeight - bottomHeight);

        float minCenterWidth = _vanillaViewportPoppedOut ? 260f * _devToolsUiScale : 420f * _devToolsUiScale;
        ImGuiLayoutHelper.CalculateThreePanelWidths(
            available.X,
            splitterThickness,
            _vanillaLayout,
            210f * _devToolsUiScale,
            620f * _devToolsUiScale,
            minCenterWidth,
            260f * _devToolsUiScale,
            680f * _devToolsUiScale,
            out float panelAvailableWidth,
            out float leftWidth,
            out float centerWidth,
            out float rightWidth);
        _vanillaLayoutBottomFraction = Math.Clamp(bottomHeight / topBottomAvailableHeight, 0.05f, 0.9f);

        IReadOnlyList<VanillaBrowserRow> rows = GetVanillaBrowserRows();
        VanillaBrowserRow? selected = FindVanillaBrowserRow(_vanillaSelection.RowKey);
        HandleVanillaHistoryShortcuts(selected);

        ImGui.BeginChild("##vanilla-animation-left-panel", new NVector2(leftWidth, topHeight), true);
        DrawVanillaBrowser(rows);
        ImGui.EndChild();

        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter("##vanilla-left-splitter", topHeight, splitterThickness, panelAvailableWidth, ref _vanillaLayout.LeftFraction, 210f * _devToolsUiScale, Math.Max(210f * _devToolsUiScale, panelAvailableWidth - rightWidth - minCenterWidth));
        ImGui.SameLine(0, 0);

        ImGui.BeginChild("##vanilla-animation-center-panel", new NVector2(centerWidth, topHeight), true);
        DrawVanillaCenterPanel(selected, deltaSeconds);
        ImGui.EndChild();

        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter("##vanilla-right-splitter", topHeight, splitterThickness, panelAvailableWidth, ref _vanillaLayout.RightFraction, 260f * _devToolsUiScale, Math.Max(260f * _devToolsUiScale, panelAvailableWidth - leftWidth - minCenterWidth), invertDrag: true);
        ImGui.SameLine(0, 0);

        ImGui.BeginChild("##vanilla-animation-right-panel", new NVector2(rightWidth, topHeight), true);
        DrawVanillaInspector(selected);
        ImGui.EndChild();

        ImGuiLayoutHelper.DrawHorizontalSplitter("##vanilla-timeline-splitter", available.X, splitterThickness, topBottomAvailableHeight, ref _vanillaLayoutBottomFraction, bottomMin, bottomMax);

        ImGui.BeginChild("##vanilla-animation-bottom-panel", new NVector2(available.X, bottomHeight), true);
        DrawVanillaTimeline(selected);
        ImGui.EndChild();

    }

    private void ResetVanillaLayout()
    {
        _vanillaLayout.Reset();
        _vanillaLayoutBottomFraction = 0.27f;
        _vanillaViewportMinHeight = 260f;
        _vanillaViewportPoppedOut = false;
    }

    private IReadOnlyList<VanillaBrowserRow> GetVanillaBrowserRows()
    {
        EnsureVanillaBrowserVisibleRows();
        return _vanillaBrowserVisibleRows;
    }

    private VanillaBrowserRow? FindVanillaBrowserRow(string rowKey)
    {
        if (string.IsNullOrWhiteSpace(rowKey)) return null;
        EnsureVanillaBrowserAllRows();
        return _vanillaBrowserAllRows.FirstOrDefault(row => string.Equals(row.Key, rowKey, StringComparison.Ordinal));
    }

    private void InvalidateVanillaBrowserRows()
    {
        _vanillaBrowserAllRowsDirty = true;
        _vanillaBrowserVisibleRowsDirty = true;
    }

    private void InvalidateVanillaBrowserFilter()
    {
        _vanillaBrowserVisibleRowsDirty = true;
    }

    private void EnsureVanillaBrowserAllRows()
    {
        if (!_vanillaBrowserAllRowsDirty) return;

        _vanillaBrowserAllRows.Clear();

        foreach (VanillaAnimationDocument document in _vanillaIndex.Documents)
        {
            foreach (VanillaShapeAnimationEntry entry in document.ShapeAnimations)
            {
                string code = entry.Animation.Code ?? entry.Animation.Name ?? $"animation-{entry.Index}";
                string name = entry.Animation.Name ?? "";
                string label = BuildVanillaAnimationRowLabel(code, name);
                string fullLabel = $"Shape | {document.DisplayPath} | {code}";
                string search = $"{label} {fullLabel} {name} {document.EntityCode} {document.Domain} {document.AssetPath} shape";
                _vanillaBrowserAllRows.Add(new(
                    $"shape:{document.DisplayPath}:{entry.Index}",
                    label,
                    fullLabel,
                    document,
                    entry,
                    null,
                    VanillaBrowserRowKind.Shape,
                    search,
                    IsUnresolved: false));
            }

            foreach (VanillaAnimationMetaEntry entry in document.MetadataEntries)
            {
                bool missing = entry.ResolveCurrentShape() == null;
                string code = entry.Metadata.Code ?? "";
                string animation = entry.Metadata.Animation ?? "";
                string label = string.IsNullOrWhiteSpace(code)
                    ? animation
                    : $"{code} -> {animation}";
                if (missing) label = $"{label} (unresolved)";
                string fullLabel = $"Meta | {document.DisplayPath} | {code} -> {animation}{(missing ? " | unresolved" : "")}";
                string search = $"{label} {fullLabel} {document.EntityCode} {document.Domain} {document.AssetPath} metadata meta {(missing ? "unresolved missing" : "")}";
                _vanillaBrowserAllRows.Add(new(
                    $"meta:{document.DisplayPath}:{entry.Index}",
                    label,
                    fullLabel,
                    document,
                    null,
                    entry,
                    VanillaBrowserRowKind.Metadata,
                    search,
                    missing));
            }
        }

        _vanillaBrowserAllRows.Sort(CompareVanillaBrowserRows);
        _vanillaBrowserAllRowsDirty = false;
        _vanillaBrowserVisibleRowsDirty = true;
    }

    private void EnsureVanillaBrowserVisibleRows()
    {
        EnsureVanillaBrowserAllRows();
        if (!_vanillaBrowserVisibleRowsDirty) return;

        string filter = _vanillaFilter.Trim();
        _vanillaBrowserVisibleRows.Clear();
        foreach (VanillaBrowserRow row in _vanillaBrowserAllRows)
        {
            if (!ImGuiLayoutHelper.MatchesDomain(_vanillaDomainFilter, row.Document.Domain)) continue;
            if (!PassesVanillaQuickFilter(row)) continue;
            if (_vanillaShowDirtyOnly && !row.Document.Dirty) continue;
            if (!PassesVanillaFilter(row.SearchText, filter)) continue;
            _vanillaBrowserVisibleRows.Add(row);
        }

        _vanillaBrowserVisibleRowsDirty = false;
    }

    private static int CompareVanillaBrowserRows(VanillaBrowserRow left, VanillaBrowserRow right)
    {
        int kind = GetVanillaBrowserKindOrder(left.Kind).CompareTo(GetVanillaBrowserKindOrder(right.Kind));
        return kind != 0 ? kind : string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetVanillaBrowserKindOrder(VanillaBrowserRowKind kind)
    {
        return kind == VanillaBrowserRowKind.Metadata ? 0 : 1;
    }

    private bool PassesVanillaQuickFilter(VanillaBrowserRow row)
    {
        return _vanillaBrowserQuickFilter switch
        {
            VanillaBrowserQuickFilter.Metadata => row.Kind == VanillaBrowserRowKind.Metadata,
            VanillaBrowserQuickFilter.Shape => row.Kind == VanillaBrowserRowKind.Shape,
            VanillaBrowserQuickFilter.Dirty => row.Document.Dirty,
            VanillaBrowserQuickFilter.Unresolved => row.IsUnresolved,
            _ => true
        };
    }

    private static bool PassesVanillaFilter(string value, string filter)
    {
        return string.IsNullOrWhiteSpace(filter) || value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void DrawVanillaBrowser(IReadOnlyList<VanillaBrowserRow> rows)
    {
        ImGui.SeparatorText("Vanilla animations");
        DrawVanillaSourceModeSelector();

        if (ImGuiLayoutHelper.DrawDomainCombo("Domain##vanilla-domain-filter", ref _vanillaDomainFilter, GetVanillaDomains()))
        {
            InvalidateVanillaBrowserFilter();
        }

        if (_vanillaSourceMode == VanillaAnimationSourceMode.Blocks)
        {
            DrawVanillaBlockSelector();
        }
        else
        {
            DrawVanillaEntitySelector();
        }

        string filterHint = _vanillaSourceMode == VanillaAnimationSourceMode.Blocks
            ? "filter animations by code, block, kind"
            : "filter animations by code, entity, kind";
        if (ImGui.InputTextWithHint("##vanilla-filter", filterHint, ref _vanillaFilter, 300))
        {
            InvalidateVanillaBrowserFilter();
        }

        DrawVanillaBrowserQuickFilters();

        if (ImGui.Checkbox("Dirty only##vanilla", ref _vanillaShowDirtyOnly))
        {
            InvalidateVanillaBrowserFilter();
        }

        ImGui.TextDisabled($"Showing {rows.Count} / {_vanillaBrowserAllRows.Count} indexed animations");

        if (ImGui.CollapsingHeader("Actions##vanilla-browser-actions"))
        {
            if (_vanillaSourceMode == VanillaAnimationSourceMode.Blocks)
            {
                if (_vanillaIndex.HasSelectedBlock && ImGui.Button("Reload selected block##vanilla", new NVector2(-1, 0)))
                {
                    CommitPendingVanillaHistory();
                    _vanillaIndex.ReloadSelectedBlock(_api);
                    ResetVanillaEntitySelectionState();
                }
            }
            else if (_vanillaIndex.HasSelectedEntity && ImGui.Button("Reload selected entity##vanilla", new NVector2(-1, 0)))
            {
                CommitPendingVanillaHistory();
                _vanillaIndex.ReloadSelectedEntity(_api, ShouldVanillaUseGroupEdit(_vanillaIndex.SelectedEntityOption));
                ResetVanillaEntitySelectionState();
            }

            ImGui.Checkbox("Overwrite exports##vanilla", ref _vanillaOverwriteExport);

            if (ImGui.Button("Export selected##vanilla", new NVector2(-1, 0)))
            {
                ExportSelectedVanillaDocument();
            }

            if (ImGui.Button("Export all dirty##vanilla", new NVector2(-1, 0)))
            {
                ExportDirtyVanillaDocuments();
            }
        }

        DrawVanillaNewAnimationControls();

        if (ImGui.CollapsingHeader("Index / diagnostics##vanilla-browser-index"))
        {
            ImGui.TextWrapped(_vanillaIndex.Status);
            if (!string.IsNullOrWhiteSpace(_vanillaStatus))
            {
                ImGui.TextWrapped(_vanillaStatus);
            }
            _animationDiagnostics.Draw("vanilla-browser", _showEditorDiagnostics);
        }

        ImGui.Separator();
        ImGui.BeginChild("##vanilla-browser-list", new NVector2(0, 0), false);
        DrawClippedVanillaBrowserRows(rows);
        ImGui.EndChild();
    }

    private void DrawVanillaBrowserQuickFilters()
    {
        DrawVanillaBrowserQuickFilter("All", VanillaBrowserQuickFilter.All);
        ImGui.SameLine();
        DrawVanillaBrowserQuickFilter("Metadata", VanillaBrowserQuickFilter.Metadata);
        ImGui.SameLine();
        DrawVanillaBrowserQuickFilter("Shape", VanillaBrowserQuickFilter.Shape);
        ImGui.SameLine();
        DrawVanillaBrowserQuickFilter("Dirty", VanillaBrowserQuickFilter.Dirty);
        ImGui.SameLine();
        DrawVanillaBrowserQuickFilter("Unresolved", VanillaBrowserQuickFilter.Unresolved);
    }

    private void DrawVanillaBrowserQuickFilter(string label, VanillaBrowserQuickFilter filter)
    {
        if (ImGui.RadioButton($"{label}##vanilla-browser-filter-{filter}", _vanillaBrowserQuickFilter == filter))
        {
            _vanillaBrowserQuickFilter = filter;
            InvalidateVanillaBrowserFilter();
        }
    }

    private void DrawVanillaNewAnimationControls()
    {
        VanillaAnimationDocument? shapeDocument = GetVanillaTargetShapeDocument();
        bool canCreate = shapeDocument?.Shape != null;

        if (!ImGui.CollapsingHeader("New animation##vanilla-new-animation-header")) return;

        ImGui.InputTextWithHint("Code##vanilla-new-animation-code", "animation-code", ref _vanillaNewAnimationCode, 120);
        ImGui.InputTextWithHint("Name##vanilla-new-animation-name", "display name", ref _vanillaNewAnimationName, 120);
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Frames##vanilla-new-animation-frames", ref _vanillaNewAnimationFrames))
        {
            _vanillaNewAnimationFrames = Math.Clamp(_vanillaNewAnimationFrames, 1, 10000);
        }

        if (GetVanillaMetadataDocument() == null)
        {
            _vanillaNewAnimationMetadata = false;
            ImGui.TextDisabled(_vanillaSourceMode == VanillaAnimationSourceMode.Blocks
                ? "Block animations are shape animations. Placed playback setup is written separately when possible."
                : "No entity metadata document is available for this selection.");
        }
        else
        {
            ImGui.Checkbox("Add entity metadata##vanilla-new-animation-meta", ref _vanillaNewAnimationMetadata);
        }

        if (!canCreate) ImGui.BeginDisabled();
        if (ImGui.Button("Create animation##vanilla-new-animation", new NVector2(-1, 0)))
        {
            CreateVanillaAnimation(shapeDocument!);
        }
        if (!canCreate) ImGui.EndDisabled();
    }

    private VanillaAnimationDocument? GetVanillaTargetShapeDocument()
    {
        VanillaBrowserRow? selected = FindVanillaBrowserRow(_vanillaSelection.RowKey);
        if (selected?.ShapeAnimation != null) return selected.ShapeAnimation.Document;

        VanillaShapeAnimationEntry? linked = selected?.MetadataEntry?.ResolveCurrentShape();
        if (linked != null) return linked.Document;

        return _vanillaIndex.Documents.FirstOrDefault(document => document.Kind == VanillaDocumentKind.Shape && document.Shape != null);
    }

    private VanillaAnimationDocument? GetVanillaMetadataDocument()
    {
        VanillaBrowserRow? selected = FindVanillaBrowserRow(_vanillaSelection.RowKey);
        if (selected?.Document.Kind == VanillaDocumentKind.EntityMetadata) return selected.Document;

        string? entityCode = selected?.Document.EntityCode ?? GetVanillaTargetShapeDocument()?.EntityCode;
        return _vanillaIndex.Documents.FirstOrDefault(document =>
            document.Kind == VanillaDocumentKind.EntityMetadata &&
            (entityCode == null || string.Equals(document.EntityCode, entityCode, StringComparison.OrdinalIgnoreCase)));
    }

    private void CreateVanillaAnimation(VanillaAnimationDocument shapeDocument)
    {
        if (shapeDocument.Shape == null)
        {
            _vanillaStatus = "Cannot create animation: selected entity has no loaded shape.";
            return;
        }

        string code = BuildUniqueVanillaAnimationCode(shapeDocument, _vanillaNewAnimationCode);
        string name = string.IsNullOrWhiteSpace(_vanillaNewAnimationName) ? code : _vanillaNewAnimationName.Trim();
        int frames = Math.Clamp(_vanillaNewAnimationFrames, 1, 10000);
        VanillaAnimation animation = new()
        {
            Code = code,
            Name = name,
            QuantityFrames = frames,
            Version = 0,
            EaseAnimationSpeed = true,
            OnActivityStopped = EnumEntityActivityStoppedHandling.Rewind,
            OnAnimationEnd = EnumEntityAnimationEndHandling.Repeat,
            KeyFrames =
            [
                new AnimationKeyFrame
                {
                    Frame = 0,
                    Elements = new Dictionary<string, AnimationKeyFrameElement>(StringComparer.OrdinalIgnoreCase)
                }
            ]
        };

        int index = shapeDocument.ShapeAnimations.Count;
        VanillaShapeAnimationEntry shapeEntry = new(shapeDocument, index, animation, null);
        shapeDocument.ShapeAnimations.Add(shapeEntry);
        MarkVanillaDirty(shapeDocument);

        VanillaBrowserRow? selectedRow = null;
        if (_vanillaNewAnimationMetadata && GetVanillaMetadataDocument() is { } metadataDocument)
        {
            AnimationMetaData metadata = new()
            {
                Code = code,
                Animation = code,
                AnimationSpeed = 1f,
                Weight = 1f,
                BlendMode = EnumAnimationBlendMode.Average,
                EaseInSpeed = 10f,
                EaseOutSpeed = 10f,
                ClientSide = true
            };
            int metadataIndex = metadataDocument.MetadataEntries.Count;
            VanillaAnimationMetaEntry metadataEntry = new(metadataDocument, metadataIndex, metadata, shapeEntry, null);
            metadataDocument.MetadataEntries.Add(metadataEntry);
            MarkVanillaDirty(metadataDocument);
            selectedRow = BuildVanillaBrowserRow(metadataEntry);
        }

        _vanillaIndex.RebuildLinks();
        InvalidateVanillaBrowserRows();
        _vanillaBrowserQuickFilter = VanillaBrowserQuickFilter.All;
        _vanillaFilter = code;
        _vanillaShowDirtyOnly = false;
        EnsureVanillaBrowserVisibleRows();

        selectedRow ??= BuildVanillaBrowserRow(shapeEntry);
        SelectVanillaRow(selectedRow);
        BuildVanillaPreviewScene(selectedRow, rebuildMesh: true);
        string setupStatus = TryApplyBlockAnimationSetup(shapeDocument, code);
        _vanillaStatus = string.IsNullOrWhiteSpace(setupStatus)
            ? $"Created animation '{code}' in {shapeDocument.DisplayPath}. Export the dirty document to save a copied JSON asset."
            : $"Created animation '{code}' in {shapeDocument.DisplayPath}. {setupStatus} Export the dirty shape document to save the animations array.";

        _vanillaNewAnimationCode = NextVanillaAnimationDraftCode(code);
        _vanillaNewAnimationName = "";
    }

    private string TryApplyBlockAnimationSetup(VanillaAnimationDocument shapeDocument, string animationCode)
    {
        if (shapeDocument.Block == null) return "";

        try
        {
            Block block = shapeDocument.Block;
            IAsset? sourceAsset = FindCollectibleSourceAsset(block);
            string domain = sourceAsset?.Location.Domain ?? block.Code?.Domain ?? shapeDocument.Domain;
            string assetPath = sourceAsset?.Location.Path ?? $"blocktypes/{EnsureJsonFilePath(block.Code?.Path ?? "unknown")}";
            string outputPath = GetToolAuthoredAssetPath("block-item-json", Path.Combine("assets", domain, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            string sourceText = ReadAssetText(sourceAsset);
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : sourceText;
            JObject json = TryParseJsonObject(oldText) ?? TryParseJsonObject(sourceText) ?? CreateCollectibleAuthoringDocument(block);

            DevToolsBlockAnimationSetupResult result = DevToolsBlockAnimationSetup.Apply(json, animationCode);
            if (!result.Success)
            {
                return result.Status;
            }

            if (!result.Changed)
            {
                return "Placed block playback setup already exists.";
            }

            string newText = JsonConvert.SerializeObject(json, Formatting.Indented);
            WriteAuthoredFile(outputPath, newText);
            return $"Saved authored block playback setup to {outputPath}.";
        }
        catch (Exception exception)
        {
            _animationDiagnostics.Exception("Block animation setup failed", exception);
            return $"Block playback setup failed: {exception.Message}";
        }
    }

    private static VanillaBrowserRow BuildVanillaBrowserRow(VanillaShapeAnimationEntry entry)
    {
        string code = entry.Animation.Code ?? entry.Animation.Name ?? $"animation-{entry.Index}";
        string name = entry.Animation.Name ?? "";
        string label = BuildVanillaAnimationRowLabel(code, name);
        string fullLabel = $"Shape | {entry.Document.DisplayPath} | {code}";
        string search = $"{label} {fullLabel} {name} {entry.Document.EntityCode} {entry.Document.Domain} {entry.Document.AssetPath} shape";
        return new(
            $"shape:{entry.Document.DisplayPath}:{entry.Index}",
            label,
            fullLabel,
            entry.Document,
            entry,
            null,
            VanillaBrowserRowKind.Shape,
            search,
            IsUnresolved: false);
    }

    private static VanillaBrowserRow BuildVanillaBrowserRow(VanillaAnimationMetaEntry entry)
    {
        bool missing = entry.ResolveCurrentShape() == null;
        string code = entry.Metadata.Code ?? "";
        string animation = entry.Metadata.Animation ?? "";
        string label = string.IsNullOrWhiteSpace(code)
            ? animation
            : $"{code} -> {animation}";
        if (missing) label = $"{label} (unresolved)";
        string fullLabel = $"Meta | {entry.Document.DisplayPath} | {code} -> {animation}{(missing ? " | unresolved" : "")}";
        string search = $"{label} {fullLabel} {entry.Document.EntityCode} {entry.Document.Domain} {entry.Document.AssetPath} metadata meta {(missing ? "unresolved missing" : "")}";
        return new(
            $"meta:{entry.Document.DisplayPath}:{entry.Index}",
            label,
            fullLabel,
            entry.Document,
            null,
            entry,
            VanillaBrowserRowKind.Metadata,
            search,
            missing);
    }

    private static string BuildUniqueVanillaAnimationCode(VanillaAnimationDocument document, string requestedCode)
    {
        string baseCode = SanitizeVanillaAnimationCode(requestedCode);
        HashSet<string> existing = document.ShapeAnimations
            .Select(entry => entry.Animation.Code ?? entry.Animation.Name ?? "")
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseCode)) return baseCode;

        for (int index = 2; index < 10000; index++)
        {
            string candidate = $"{baseCode}-{index}";
            if (!existing.Contains(candidate)) return candidate;
        }

        return $"{baseCode}-{DateTime.UtcNow:yyyyMMddHHmmss}";
    }

    private static string BuildVanillaAnimationRowLabel(string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.IsNullOrWhiteSpace(name) ? "unnamed animation" : name;
        if (string.IsNullOrWhiteSpace(name) || string.Equals(code, name, StringComparison.OrdinalIgnoreCase)) return code;
        return $"{code} ({name})";
    }

    private static string SanitizeVanillaAnimationCode(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "new-animation" : value.Trim();
        char[] chars = value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
            .ToArray();
        string result = new(chars);
        while (result.Contains("--", StringComparison.Ordinal)) result = result.Replace("--", "-", StringComparison.Ordinal);
        result = result.Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "new-animation" : result;
    }

    private static string NextVanillaAnimationDraftCode(string previousCode)
    {
        const string suffix = "-2";
        if (previousCode.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return previousCode[..^suffix.Length] + "-3";
        return previousCode + suffix;
    }

    private void DrawClippedVanillaBrowserRows(IReadOnlyList<VanillaBrowserRow> rows)
    {
        if (rows.Count == 0)
        {
            ImGui.TextDisabled("No animations match the current filters.");
            return;
        }

        float rowHeight = Math.Max(1f, ImGui.GetTextLineHeightWithSpacing());
        float visibleHeight = Math.Max(rowHeight, ImGui.GetContentRegionAvail().Y);
        float scrollY = Math.Max(0f, ImGui.GetScrollY());
        int first = Math.Clamp((int)Math.Floor(scrollY / rowHeight) - 2, 0, rows.Count);
        int visibleCount = Math.Max(1, (int)Math.Ceiling(visibleHeight / rowHeight) + 5);
        int last = Math.Clamp(first + visibleCount, first, rows.Count);

        if (first > 0)
        {
            ImGui.Dummy(new NVector2(1f, first * rowHeight));
        }

        for (int index = first; index < last; index++)
        {
            VanillaBrowserRow row = rows[index];
            bool selected = row.Key == _vanillaSelection.RowKey;
            string dirty = row.Document.Dirty ? "* " : "";
            if (ImGui.Selectable($"{dirty}{row.Label}##{row.Key}", selected))
            {
                SelectVanillaRow(row);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(row.FullLabel);
            }
        }

        if (last < rows.Count)
        {
            ImGui.Dummy(new NVector2(1f, (rows.Count - last) * rowHeight));
        }
    }

    private void DrawVanillaSourceModeSelector()
    {
        bool blocks = _vanillaSourceMode == VanillaAnimationSourceMode.Blocks;
        if (ImGui.RadioButton("Entities##vanilla-source-mode", !blocks))
        {
            CommitPendingVanillaHistory();
            _vanillaSourceMode = VanillaAnimationSourceMode.Entities;
            _vanillaIndex.ClearSelection();
            ResetVanillaEntitySelectionState();
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Blocks##vanilla-source-mode", blocks))
        {
            CommitPendingVanillaHistory();
            _vanillaSourceMode = VanillaAnimationSourceMode.Blocks;
            _vanillaIndex.ClearSelection();
            ResetVanillaEntitySelectionState();
        }
    }

    private void DrawVanillaBlockSelector()
    {
        ImGui.SeparatorText("Block");

        ImGui.InputTextWithHint("##vanilla-block-filter", "filter blocks", ref _vanillaBlockFilter, 240);

        IReadOnlyList<VanillaBlockOption> options = _vanillaIndex.GetBlockOptions();
        string blockFilter = _vanillaBlockFilter.Trim();
        List<int> visible = [];
        for (int index = 0; index < options.Count; index++)
        {
            if (!ImGuiLayoutHelper.MatchesDomain(_vanillaDomainFilter, options[index].Domain)) continue;
            if (string.IsNullOrWhiteSpace(blockFilter) || options[index].SearchText.Contains(blockFilter, StringComparison.OrdinalIgnoreCase))
            {
                visible.Add(index);
            }
        }

        string preview = _vanillaIndex.SelectedBlockLabel ?? "Select block";
        if (ImGui.BeginCombo("Block##vanilla-block", preview))
        {
            foreach (int index in visible)
            {
                bool selected = _vanillaIndex.IsSelectedBlockOption(options[index]);
                if (ImGui.Selectable($"{options[index].Label}##vanilla-block-{index}", selected))
                {
                    CommitPendingVanillaHistory();
                    _vanillaIndex.SelectBlock(_api, options, index);
                    ResetVanillaEntitySelectionState();
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }

                if (ImGui.IsItemHovered())
                {
                    VanillaBlockSourceInfo? source = options[index].Source;
                    ImGui.SetTooltip(source == null
                        ? $"{options[index].FullLabel}\nShape: {options[index].Block.Shape?.Base}"
                        : $"{options[index].FullLabel}\nSource: {source.Key}\nShape: {options[index].Block.Shape?.Base}");
                }
            }

            ImGui.EndCombo();
        }

        if (_vanillaIndex.HasSelectedBlock)
        {
            ImGui.TextDisabled("Existing block entity classes are preserved. Plain blocks can be wired for InGameDevTools placed playback when creating an animation.");
        }
    }

    private void DrawVanillaEntitySelector()
    {
        ImGui.SeparatorText("Entity");

        ImGui.InputTextWithHint("##vanilla-entity-filter", "filter entities", ref _vanillaEntityFilter, 240);

        IReadOnlyList<VanillaEntityOption> options = _vanillaIndex.GetEntityOptions(_vanillaEntitySelectorMode, _vanillaShowHiddenEntities);
        string entityFilter = _vanillaEntityFilter.Trim();
        List<int> visible = [];
        for (int index = 0; index < options.Count; index++)
        {
            if (!ImGuiLayoutHelper.MatchesDomain(_vanillaDomainFilter, options[index].Domain)) continue;
            if (string.IsNullOrWhiteSpace(entityFilter) || options[index].SearchText.Contains(entityFilter, StringComparison.OrdinalIgnoreCase))
            {
                visible.Add(index);
            }
        }

        string preview = _vanillaIndex.SelectedEntityLabel ?? "Select entity";
        if (ImGui.BeginCombo("Entity##vanilla-entity", preview))
        {
            foreach (int index in visible)
            {
                bool selected = _vanillaIndex.IsSelectedEntityOption(options[index]);
                if (ImGui.Selectable($"{options[index].Label}##vanilla-entity-{index}", selected))
                {
                    CommitPendingVanillaHistory();
                    _vanillaIndex.SelectEntity(_api, options, index, 0, ShouldVanillaUseGroupEdit(options[index]));
                    ResetVanillaEntitySelectionState();
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(options[index].Tooltip);
                }
            }

            ImGui.EndCombo();
        }

        if (_vanillaIndex.SelectedEntityOption is { } selectedOption && selectedOption.Members.Count > 1)
        {
            bool singleVariant = _vanillaSingleVariantEdit || _vanillaEntitySelectorMode == VanillaEntitySelectorMode.Exact;
            bool canGroupEdit = _vanillaEntitySelectorMode == VanillaEntitySelectorMode.Grouped;
            if (!canGroupEdit) ImGui.BeginDisabled();
            if (ImGui.RadioButton("Group edit##vanilla-edit-scope", !singleVariant))
            {
                CommitPendingVanillaHistory();
                _vanillaSingleVariantEdit = false;
                _vanillaIndex.ReloadSelectedEntity(_api, groupEdit: true);
                ResetVanillaEntitySelectionState();
            }
            if (!canGroupEdit) ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.RadioButton("Single variant##vanilla-edit-scope", singleVariant))
            {
                CommitPendingVanillaHistory();
                _vanillaSingleVariantEdit = true;
                _vanillaIndex.ReloadSelectedEntity(_api, groupEdit: false);
                ResetVanillaEntitySelectionState();
            }

            string[] memberLabels = selectedOption.Members.Select(member => member.Label).ToArray();
            int memberIndex = Math.Clamp(_vanillaIndex.SelectedMemberIndex, 0, Math.Max(0, memberLabels.Length - 1));
            if (memberLabels.Length > 0 && ImGui.Combo("Preview variant##vanilla-entity-member", ref memberIndex, memberLabels, memberLabels.Length))
            {
                CommitPendingVanillaHistory();
                _vanillaIndex.SelectEntity(_api, selectedOption, memberIndex, ShouldVanillaUseGroupEdit(selectedOption));
                ResetVanillaEntitySelectionState();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("The viewport uses this variant. Group edit still applies compatible edits to the whole group.");
            }
        }

        if (ImGui.CollapsingHeader("Entity options##vanilla-entity-options"))
        {
            bool grouped = _vanillaEntitySelectorMode == VanillaEntitySelectorMode.Grouped;
            if (ImGui.RadioButton("Grouped##vanilla-entity-mode", grouped))
            {
                CommitPendingVanillaHistory();
                _vanillaEntitySelectorMode = VanillaEntitySelectorMode.Grouped;
                _vanillaSingleVariantEdit = false;
                _vanillaIndex.ClearSelection();
                ResetVanillaEntitySelectionState();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Collapse variants using source assets and animation compatibility.");
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("Exact##vanilla-entity-mode", !grouped))
            {
                CommitPendingVanillaHistory();
                _vanillaEntitySelectorMode = VanillaEntitySelectorMode.Exact;
                _vanillaSingleVariantEdit = true;
                _vanillaIndex.ClearSelection();
                ResetVanillaEntitySelectionState();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Show one row per loaded runtime entity type.");
            }

            bool showHidden = _vanillaShowHiddenEntities;
            if (ImGui.Checkbox("Show hidden/helper##vanilla-show-hidden-entities", ref showHidden))
            {
                CommitPendingVanillaHistory();
                _vanillaShowHiddenEntities = showHidden;
                _vanillaIndex.ClearSelection();
                ResetVanillaEntitySelectionState();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Include entities marked by their source metadata as hidden, helper, debug, test, internal, technical, or bot-like.");
            }
        }
    }

    private bool ShouldVanillaUseGroupEdit(VanillaEntityOption? option)
    {
        return _vanillaEntitySelectorMode == VanillaEntitySelectorMode.Grouped &&
            !_vanillaSingleVariantEdit &&
            option?.Members.Count > 1;
    }

    private void ResetVanillaEntitySelectionState()
    {
        InvalidateVanillaBrowserRows();
        _vanillaHistory.ClearAll();
        _vanillaInspectorSnapshotCache.Clear();
        _vanillaLiveOriginalsTrackedCount = -1;
        _vanillaUniverseCacheDocument = null;
        _vanillaUniverseCacheAnimation = null;
        _vanillaUniverseCacheKeyFrame = null;
        _vanillaUniverseCache = [];
        _vanillaUniverseLookupCache = null;
        _vanillaLastEditedDocumentKey = "";
        _vanillaSelection.Clear();
        DisposeVanillaPreviewScene();
        _vanillaStatus = "Preview not loaded. Select an animation and press Load preview when ready.";
    }

    private IEnumerable<string> GetVanillaDomains()
    {
        return _vanillaIndex.AllEntityDomains
            .Concat(_vanillaIndex.AllBlockDomains)
            .Concat(_vanillaIndex.Documents.Select(document => document.Domain));
    }

    private void SelectVanillaRow(VanillaBrowserRow row)
    {
        CommitPendingVanillaHistory();
        _vanillaSelection.RowKey = row.Key;
        _vanillaSelection.KeyFrameIndex = 0;
        _vanillaSelection.ElementName = "";
        _vanillaSelection.LoopStartFrame = 0;
        _vanillaSelection.LoopEndFrame = Math.Max(1, GetVanillaAnimation(row)?.QuantityFrames ?? 1) - 1;
        _vanillaTimelineDragKeyframe = -1;
        if (_vanillaPreviewScene?.Key != row.Key)
        {
            DisposeVanillaPreviewScene();
            _vanillaStatus = "Preview not loaded. Press Load preview when ready.";
        }
    }
}
