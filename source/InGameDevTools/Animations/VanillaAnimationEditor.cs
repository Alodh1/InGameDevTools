#if DEBUG
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
    private readonly VanillaAnimationIndexService _vanillaIndex = new();
    private readonly VanillaAnimationSelection _vanillaSelection = new();
    private readonly VanillaAnimationExportService _vanillaExportService = new();
    private readonly VanillaAnimationEditorHistory _vanillaHistory = new();
    private VanillaAnimationPreviewScene? _vanillaPreviewScene;
    private VanillaAnimationViewport3DRenderer? _vanillaPreviewRenderer;
    private string _vanillaFilter = "";
    private bool _vanillaShowDirtyOnly;
    private bool _vanillaOverwriteExport;
    private string _vanillaEntityFilter = "";
    private string _vanillaStatus = "";
    private string _vanillaLastEditedDocumentKey = "";
    private int _vanillaTimelineDragKeyframe = -1;
    private readonly List<VanillaBrowserRow> _vanillaBrowserAllRows = [];
    private readonly List<VanillaBrowserRow> _vanillaBrowserVisibleRows = [];
    private bool _vanillaBrowserAllRowsDirty = true;
    private bool _vanillaBrowserVisibleRowsDirty = true;
    private VanillaBrowserQuickFilter _vanillaBrowserQuickFilter = VanillaBrowserQuickFilter.All;
    private readonly ImGuiThreePanelLayoutState _vanillaLayout = new(0.22f, 0.28f);
    private float _vanillaLayoutBottomFraction = 0.27f;
    private float _vanillaViewportMinHeight = 260f;
    private bool _vanillaViewportPoppedOut;
    private string _vanillaDomainFilter = "";
    private float _vanillaPoppedViewportWidth = 980f;
    private float _vanillaPoppedViewportHeight = 720f;
    private float _vanillaViewportYaw;
    private float _vanillaViewportPitch;
    private float _vanillaViewportZoom = 1f;
    private float _vanillaViewportPanX;
    private float _vanillaViewportPanY;
    private bool _vanillaViewportWorldLighting;
    private bool _vanillaVerbosePreviewLogs;
    private VanillaPreviewMode _vanillaViewportMode = VanillaPreviewMode.Orbit;
    private TransformGizmoAxis _vanillaViewportGizmoDragAxis = TransformGizmoAxis.None;
    private TransformGizmoMode _vanillaViewportGizmoDragMode = TransformGizmoMode.None;
    private NVector2 _vanillaViewportGizmoDragMouseStart;
    private NVector2 _vanillaViewportGizmoDragVector = new(1f, 0f);
    private NVector2 _vanillaViewportGizmoDragCenter;
    private double _vanillaViewportGizmoDragLastAngleRadians;
    private double _vanillaViewportGizmoDragAccumulatedDegrees;
    private double _vanillaViewportGizmoDragStartValue;
    private string _vanillaViewportGizmoDragRowKey = "";
    private int _vanillaViewportGizmoDragKeyFrameIndex = -1;
    private string _vanillaViewportGizmoDragElementName = "";
    private float _vanillaRotationStepDegrees = 1f;
    private string _vanillaNewAnimationCode = "new-animation";
    private string _vanillaNewAnimationName = "";
    private int _vanillaNewAnimationFrames = 30;
    private bool _vanillaNewAnimationMetadata = true;

    private void VanillaAnimationsTab(float deltaSeconds)
    {
        ClearActiveTransformGizmo();
        _vanillaIndex.EnsureEntityList(_api);
        TrackVanillaLiveOriginals();

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
                string label = $"Shape | {document.DisplayPath} | {code}";
                string search = $"{label} {name} {document.EntityCode} {document.Domain} {document.AssetPath} shape";
                _vanillaBrowserAllRows.Add(new(
                    $"shape:{document.DisplayPath}:{entry.Index}",
                    label,
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
                string label = $"Meta | {document.DisplayPath} | {code} -> {animation}{(missing ? " | unresolved" : "")}";
                string search = $"{label} {document.EntityCode} {document.Domain} {document.AssetPath} metadata meta {(missing ? "unresolved missing" : "")}";
                _vanillaBrowserAllRows.Add(new(
                    $"meta:{document.DisplayPath}:{entry.Index}",
                    label,
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
        if (ImGuiLayoutHelper.DrawDomainCombo("Domain##vanilla-domain-filter", ref _vanillaDomainFilter, GetVanillaDomains()))
        {
            InvalidateVanillaBrowserFilter();
        }

        DrawVanillaEntitySelector();
        if (ImGui.InputTextWithHint("##vanilla-filter", "filter animations by code, entity, kind", ref _vanillaFilter, 300))
        {
            InvalidateVanillaBrowserFilter();
        }

        DrawVanillaBrowserQuickFilters();

        if (ImGui.Checkbox("Dirty only##vanilla", ref _vanillaShowDirtyOnly))
        {
            InvalidateVanillaBrowserFilter();
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

        if (ImGui.Button("Apply all dirty live##vanilla", new NVector2(-1, 0)))
        {
            ApplyAllDirtyVanillaLive();
        }

        DrawVanillaNewAnimationControls();

        ImGui.SeparatorText("Index");
        ImGui.TextWrapped(_vanillaIndex.Status);
        if (!string.IsNullOrWhiteSpace(_vanillaStatus))
        {
            ImGui.TextWrapped(_vanillaStatus);
        }

        ImGui.Separator();
        ImGui.TextDisabled($"Showing {rows.Count} / {_vanillaBrowserAllRows.Count} indexed animations");
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
            ImGui.TextDisabled("No entity metadata document is available for this selection.");
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
        _vanillaStatus = $"Created animation '{code}' in {shapeDocument.DisplayPath}. Export the dirty document to save a copied JSON asset.";

        _vanillaNewAnimationCode = NextVanillaAnimationDraftCode(code);
        _vanillaNewAnimationName = "";
    }

    private static VanillaBrowserRow BuildVanillaBrowserRow(VanillaShapeAnimationEntry entry)
    {
        string code = entry.Animation.Code ?? entry.Animation.Name ?? $"animation-{entry.Index}";
        string name = entry.Animation.Name ?? "";
        string label = $"Shape | {entry.Document.DisplayPath} | {code}";
        string search = $"{label} {name} {entry.Document.EntityCode} {entry.Document.Domain} {entry.Document.AssetPath} shape";
        return new(
            $"shape:{entry.Document.DisplayPath}:{entry.Index}",
            label,
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
        string label = $"Meta | {entry.Document.DisplayPath} | {code} -> {animation}{(missing ? " | unresolved" : "")}";
        string search = $"{label} {entry.Document.EntityCode} {entry.Document.Domain} {entry.Document.AssetPath} metadata meta {(missing ? "unresolved missing" : "")}";
        return new(
            $"meta:{entry.Document.DisplayPath}:{entry.Index}",
            label,
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
            if (ImGui.Selectable($"{dirty}{ImGuiLayoutHelper.CompactAssetCode(row.Label)}##{row.Key}", selected))
            {
                SelectVanillaRow(row);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(row.Label);
            }
        }

        if (last < rows.Count)
        {
            ImGui.Dummy(new NVector2(1f, (rows.Count - last) * rowHeight));
        }
    }

    private void DrawVanillaEntitySelector()
    {
        ImGui.SeparatorText("Entity");
        ImGui.InputTextWithHint("##vanilla-entity-filter", "filter entities", ref _vanillaEntityFilter, 240);

        IReadOnlyList<VanillaEntityOption> options = _vanillaIndex.EntityOptions;
        string entityFilter = _vanillaEntityFilter.Trim();
        List<int> visible = [];
        for (int index = 0; index < options.Count; index++)
        {
            if (!ImGuiLayoutHelper.MatchesDomain(_vanillaDomainFilter, options[index].Domain)) continue;
            if (string.IsNullOrWhiteSpace(entityFilter) || options[index].Label.Contains(entityFilter, StringComparison.OrdinalIgnoreCase))
            {
                visible.Add(index);
            }
        }

        string preview = _vanillaIndex.SelectedEntityLabel ?? "Select entity";
        if (ImGui.BeginCombo("Entity##vanilla-entity", preview))
        {
            foreach (int index in visible)
            {
                bool selected = index == _vanillaIndex.SelectedEntityIndex;
                if (ImGui.Selectable($"{options[index].Label}##vanilla-entity-{index}", selected))
                {
                    CommitPendingVanillaHistory();
                    _vanillaIndex.SelectEntity(_api, index);
                    InvalidateVanillaBrowserRows();
                    _vanillaHistory.ClearAll();
                    _vanillaLastEditedDocumentKey = "";
                    _vanillaSelection.Clear();
                    DisposeVanillaPreviewScene();
                    _vanillaStatus = "Preview not loaded. Select an animation and press Load preview when ready.";
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(options[index].FullLabel);
                }
            }

            ImGui.EndCombo();
        }

        if (_vanillaIndex.HasSelectedEntity && ImGui.Button("Reload selected entity##vanilla", new NVector2(-1, 0)))
        {
            CommitPendingVanillaHistory();
            _vanillaIndex.ReloadSelectedEntity(_api);
            InvalidateVanillaBrowserRows();
            _vanillaHistory.ClearAll();
            _vanillaLastEditedDocumentKey = "";
            _vanillaSelection.Clear();
            DisposeVanillaPreviewScene();
            _vanillaStatus = "Preview not loaded. Select an animation and press Load preview when ready.";
        }
    }

    private IEnumerable<string> GetVanillaDomains()
    {
        return _vanillaIndex.EntityOptions.Select(option => option.Domain)
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

    private void DrawVanillaCenterPanel(VanillaBrowserRow? row, float deltaSeconds)
    {
        if (row == null)
        {
            ImGui.TextDisabled(_vanillaIndex.HasSelectedEntity ? "Select a vanilla animation." : "Select an entity first.");
            return;
        }

        VanillaAnimation? animation = GetVanillaAnimation(row);
        if (animation == null)
        {
            ImGui.TextWrapped("No linked shape animation is available for this metadata entry.");
            return;
        }

        VanillaAnimationPreviewScene? scene = _vanillaPreviewScene?.Key == row.Key ? _vanillaPreviewScene : null;
        if (scene == null)
        {
            ImGui.TextWrapped(row.Label);
            ImGui.TextWrapped("Preview is not loaded. Loading a preview prepares the selected shape and uploads its mesh, so it runs only when requested.");
            if (ImGui.Button("Load preview##vanilla-preview", new NVector2(-1, 0)))
            {
                BuildVanillaPreviewScene(row, rebuildMesh: true);
            }

            if (!string.IsNullOrWhiteSpace(_vanillaStatus))
            {
                ImGui.TextWrapped(_vanillaStatus);
            }
            return;
        }

        ImGui.TextWrapped(row.Label);
        if (!string.IsNullOrWhiteSpace(scene.Status))
        {
            ImGui.TextWrapped(scene.Status);
        }

        DrawVanillaPlaybackControls(row, scene, deltaSeconds);

        NVector2 centerAvailable = ImGui.GetContentRegionAvail();
        if (_vanillaViewportPoppedOut)
        {
            ImGui.Separator();
            ImGui.TextWrapped("Viewport is popped out into a separate resizable window.");
            if (ImGui.Button("Return viewport here##vanilla-preview-pop-in", new NVector2(-1, 0)))
            {
                _vanillaViewportPoppedOut = false;
            }
        }
        else
        {
            DrawVanillaViewport(row, scene, new NVector2(centerAvailable.X, Math.Max(_vanillaViewportMinHeight, centerAvailable.Y)));
        }
    }

    private void DrawVanillaPlaybackControls(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, float deltaSeconds)
    {
        scene.SetPreviewMode(row, GetVanillaEffectivePreviewMode(scene));

        if (scene.Playing)
        {
            scene.Tick(deltaSeconds);
            ApplyVanillaLoop(scene);
        }

        if (ImGui.Button("Play##vanilla-playback"))
        {
            scene.Play();
        }

        ImGui.SameLine();
        if (ImGui.Button(scene.Playing ? "Pause##vanilla-playback" : "Resume##vanilla-playback"))
        {
            if (scene.Playing)
            {
                scene.Playing = false;
            }
            else
            {
                scene.Play();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Step keyframe <##vanilla-playback"))
        {
            StepVanillaKeyframe(row, -1);
        }

        ImGui.SameLine();
        if (ImGui.Button("Step keyframe >##vanilla-playback"))
        {
            StepVanillaKeyframe(row, 1);
        }

        ImGui.SameLine();
        if (ImGui.Button("Step frame <##vanilla-playback"))
        {
            ScrubVanillaPreview(scene, Math.Max(0, scene.CurrentFrame - 1));
        }

        ImGui.SameLine();
        if (ImGui.Button("Step frame >##vanilla-playback"))
        {
            ScrubVanillaPreview(scene, Math.Min(Math.Max(0, scene.QuantityFrames - 1), scene.CurrentFrame + 1));
        }

        int maxFrame = Math.Max(0, scene.QuantityFrames - 1);
        int loopStart = Math.Clamp(_vanillaSelection.LoopStartFrame, 0, maxFrame);
        int loopEnd = Math.Clamp(_vanillaSelection.LoopEndFrame, 0, maxFrame);
        if (loopEnd < loopStart) loopEnd = loopStart;

        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderInt("Loop start frame##vanilla-playback", ref loopStart, 0, maxFrame))
        {
            _vanillaSelection.LoopStartFrame = Math.Min(loopStart, _vanillaSelection.LoopEndFrame);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderInt("Loop end frame##vanilla-playback", ref loopEnd, 0, maxFrame))
        {
            _vanillaSelection.LoopEndFrame = Math.Max(loopEnd, _vanillaSelection.LoopStartFrame);
        }

        int frame = (int)Math.Clamp(scene.CurrentFrame, 0, maxFrame);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("Frame##vanilla-playback", ref frame, 0, maxFrame))
        {
            ScrubVanillaPreview(scene, frame);
        }

        bool worldLighting = _vanillaViewportWorldLighting;
        if (ImGui.Checkbox("World lighting##vanilla-preview-lighting", ref worldLighting))
        {
            _vanillaViewportWorldLighting = worldLighting;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Use current world light and fog instead of stable editor lighting.");
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("Mode:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Orbit##vanilla-preview-mode", _vanillaViewportMode == VanillaPreviewMode.Orbit))
        {
            SetVanillaViewportMode(VanillaPreviewMode.Orbit);
        }

        ImGui.SameLine();
        bool firstPersonAvailable = scene.ClassicFirstPersonAvailable;
        if (!firstPersonAvailable) ImGui.BeginDisabled();
        if (ImGui.RadioButton("First person##vanilla-preview-mode", _vanillaViewportMode == VanillaPreviewMode.FirstPerson))
        {
            SetVanillaViewportMode(VanillaPreviewMode.FirstPerson);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(firstPersonAvailable
                ? "Classic Vintage Story first person: arms/hands mesh, first-person FOV, and -fp animation variants."
                : "First-person preview is only available for player-style meshes with arm joints.");
        }
        if (!firstPersonAvailable) ImGui.EndDisabled();

        ImGui.SameLine();
        bool immersiveFirstPersonAvailable = scene.ImmersiveFirstPersonAvailable;
        if (!immersiveFirstPersonAvailable) ImGui.BeginDisabled();
        if (ImGui.RadioButton("Immersive FP##vanilla-preview-mode", _vanillaViewportMode == VanillaPreviewMode.ImmersiveFirstPerson))
        {
            SetVanillaViewportMode(VanillaPreviewMode.ImmersiveFirstPerson);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(immersiveFirstPersonAvailable
                ? "Opt-in immersive first person: body mesh with the neck/head subtree hidden and -ifp animation variants."
                : "Immersive first-person preview is only available for player-style meshes.");
        }
        if (!immersiveFirstPersonAvailable) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Reset view##vanilla-preview-camera-reset"))
        {
            _vanillaViewportYaw = 0;
            _vanillaViewportPitch = 0;
            _vanillaViewportZoom = 1f;
            _vanillaViewportPanX = 0;
            _vanillaViewportPanY = 0;
        }

        ImGui.SameLine();
        bool verbosePreviewLogs = _vanillaVerbosePreviewLogs;
        if (ImGui.Checkbox("Verbose preview logs##vanilla-preview-verbose", ref verbosePreviewLogs))
        {
            _vanillaVerbosePreviewLogs = verbosePreviewLogs;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Write vanilla preview framebuffer, shader, mesh, texture, and skip diagnostics to verbose debug logs.");
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("Pop out viewport##vanilla-preview-popout", ref _vanillaViewportPoppedOut) && !_vanillaViewportPoppedOut)
        {
            ClearVanillaViewportGizmoDrag();
        }
    }

    private void DrawVanillaPoppedOutViewport()
    {
        DrawVanillaPoppedOutViewport(FindVanillaBrowserRow(_vanillaSelection.RowKey));
    }

    private void DrawVanillaPoppedOutViewport(VanillaBrowserRow? row)
    {
        if (!_vanillaViewportPoppedOut) return;

        bool open = true;
        NVector2 displaySize = GetVanillaImGuiDisplaySize();
        _vanillaPoppedViewportWidth = Math.Clamp(_vanillaPoppedViewportWidth, 420f, Math.Max(420f, displaySize.X - 24f));
        _vanillaPoppedViewportHeight = Math.Clamp(_vanillaPoppedViewportHeight, 300f, Math.Max(300f, displaySize.Y - 36f));
        ImGui.SetNextWindowSize(new NVector2(_vanillaPoppedViewportWidth, _vanillaPoppedViewportHeight), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new NVector2(420f, 300f), new NVector2(Math.Max(420f, displaySize.X), Math.Max(300f, displaySize.Y)));
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoSavedSettings;
        if (ImGui.Begin("Animation viewport##vanilla-popped-viewport", ref open, flags))
        {
            ImGui.SetWindowFontScale(_devToolsUiScale);
            DrawVanillaPoppedViewportControls(displaySize);

            if (row == null)
            {
                ImGui.TextDisabled(_vanillaIndex.HasSelectedEntity ? "Select a vanilla animation." : "Select an entity first.");
            }
            else if (_vanillaPreviewScene?.Key != row.Key)
            {
                ImGui.TextWrapped(row.Label);
                ImGui.TextWrapped("Preview is not loaded.");
                if (ImGui.Button("Load preview##vanilla-popped-load-preview", new NVector2(-1, 0)))
                {
                    BuildVanillaPreviewScene(row, rebuildMesh: true);
                }
            }
            else
            {
                VanillaAnimationPreviewScene scene = _vanillaPreviewScene;
                NVector2 available = ImGui.GetContentRegionAvail();
                DrawVanillaViewport(row, scene, new NVector2(available.X, Math.Max(_vanillaViewportMinHeight, available.Y)));
            }

            if (!ImGui.IsAnyItemActive())
            {
                NVector2 windowSize = ImGui.GetWindowSize();
                _vanillaPoppedViewportWidth = windowSize.X;
                _vanillaPoppedViewportHeight = windowSize.Y;
            }

            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();

        if (!open)
        {
            _vanillaViewportPoppedOut = false;
            ClearVanillaViewportGizmoDrag();
        }
    }

    private NVector2 GetVanillaImGuiDisplaySize()
    {
        NVector2 displaySize = ImGui.GetIO().DisplaySize;
        if (displaySize.X <= 0 || displaySize.Y <= 0)
        {
            displaySize = new NVector2(_api.Render.FrameWidth, _api.Render.FrameHeight);
        }

        return new NVector2(Math.Max(640f, displaySize.X), Math.Max(480f, displaySize.Y));
    }

    private void DrawVanillaPoppedViewportControls(NVector2 displaySize)
    {
        const float margin = 10f;
        float toolbarHeight = 34f * _devToolsUiScale;
        NVector2 topLeft = new(margin, margin + toolbarHeight);
        NVector2 usable = new(Math.Max(420f, displaySize.X - margin * 2f), Math.Max(300f, displaySize.Y - margin * 2f - toolbarHeight));
        float halfWidth = Math.Max(420f, usable.X * 0.5f - margin * 0.5f);
        float halfHeight = Math.Max(300f, usable.Y * 0.5f - margin * 0.5f);

        if (ImGui.Button("Left half##vanilla-popout-place-left"))
        {
            SetVanillaPoppedViewportRect(topLeft.X, topLeft.Y, halfWidth, usable.Y);
        }

        ImGui.SameLine();
        if (ImGui.Button("Right half##vanilla-popout-place-right"))
        {
            SetVanillaPoppedViewportRect(topLeft.X + usable.X - halfWidth, topLeft.Y, halfWidth, usable.Y);
        }

        ImGui.SameLine();
        if (ImGui.Button("Top half##vanilla-popout-place-top"))
        {
            SetVanillaPoppedViewportRect(topLeft.X, topLeft.Y, usable.X, halfHeight);
        }

        ImGui.SameLine();
        if (ImGui.Button("Bottom half##vanilla-popout-place-bottom"))
        {
            SetVanillaPoppedViewportRect(topLeft.X, topLeft.Y + usable.Y - halfHeight, usable.X, halfHeight);
        }

        ImGui.SameLine();
        if (ImGui.Button("Fill##vanilla-popout-place-fill"))
        {
            SetVanillaPoppedViewportRect(topLeft.X, topLeft.Y, usable.X, usable.Y);
        }

        ImGui.SameLine();
        if (ImGui.Button("Center##vanilla-popout-place-center"))
        {
            float width = Math.Min(_vanillaPoppedViewportWidth, usable.X);
            float height = Math.Min(_vanillaPoppedViewportHeight, usable.Y);
            SetVanillaPoppedViewportRect(topLeft.X + (usable.X - width) * 0.5f, topLeft.Y + (usable.Y - height) * 0.5f, width, height);
        }

        ImGui.SameLine();
        if (ImGui.Button("Dock back##vanilla-popout-dock-back"))
        {
            _vanillaViewportPoppedOut = false;
            ClearVanillaViewportGizmoDrag();
        }

        float requestedWidth = _vanillaPoppedViewportWidth;
        float requestedHeight = _vanillaPoppedViewportHeight;
        ImGui.SetNextItemWidth(110);
        bool resize = ImGui.InputFloat("Width##vanilla-popout-width", ref requestedWidth, 0, 0, "%.0f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        resize |= ImGui.InputFloat("Height##vanilla-popout-height", ref requestedHeight, 0, 0, "%.0f");
        if (resize)
        {
            _vanillaPoppedViewportWidth = Math.Clamp(requestedWidth, 420f, Math.Max(420f, displaySize.X));
            _vanillaPoppedViewportHeight = Math.Clamp(requestedHeight, 300f, Math.Max(300f, displaySize.Y));
            ImGui.SetWindowSize(new NVector2(_vanillaPoppedViewportWidth, _vanillaPoppedViewportHeight), ImGuiCond.Always);
        }
    }

    private void SetVanillaPoppedViewportRect(float x, float y, float width, float height)
    {
        _vanillaPoppedViewportWidth = Math.Max(420f, width);
        _vanillaPoppedViewportHeight = Math.Max(300f, height);
        ImGui.SetWindowPos(new NVector2(Math.Max(0f, x), Math.Max(0f, y)), ImGuiCond.Always);
        ImGui.SetWindowSize(new NVector2(_vanillaPoppedViewportWidth, _vanillaPoppedViewportHeight), ImGuiCond.Always);
        ClearVanillaViewportGizmoDrag();
    }

    private void SetVanillaViewportMode(VanillaPreviewMode mode)
    {
        if (_vanillaViewportMode == mode) return;
        _vanillaViewportMode = mode;
        _vanillaViewportPanX = 0;
        _vanillaViewportPanY = 0;
        _vanillaViewportZoom = 1f;
    }

    private void ApplyVanillaLoop(VanillaAnimationPreviewScene scene)
    {
        int maxFrame = Math.Max(0, scene.QuantityFrames - 1);
        int loopStart = Math.Clamp(_vanillaSelection.LoopStartFrame, 0, maxFrame);
        int loopEnd = Math.Clamp(_vanillaSelection.LoopEndFrame, 0, maxFrame);
        if (loopEnd <= loopStart) return;
        if (scene.CurrentFrame > loopEnd)
        {
            ScrubVanillaPreview(scene, loopStart);
        }
    }

    private void StepVanillaKeyframe(VanillaBrowserRow row, int direction)
    {
        VanillaAnimation? animation = GetVanillaAnimation(row);
        if (animation?.KeyFrames == null || animation.KeyFrames.Length == 0 || _vanillaPreviewScene == null) return;

        _vanillaSelection.KeyFrameIndex = Math.Clamp(_vanillaSelection.KeyFrameIndex + direction, 0, animation.KeyFrames.Length - 1);
        ScrubVanillaPreview(_vanillaPreviewScene, animation.KeyFrames[_vanillaSelection.KeyFrameIndex].Frame);
    }

    private void ScrubVanillaPreview(VanillaAnimationPreviewScene scene, float frame)
    {
        scene.Scrub(frame);
    }

    private void DrawVanillaViewport(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, NVector2 requestedSize)
    {
        NVector2 size = new(Math.Max(420f, requestedSize.X), Math.Max(240f, requestedSize.Y));
        ImGui.InvisibleButton($"##vanilla-viewport-{scene.Key}", size);
        NVector2 min = ImGui.GetItemRectMin();
        NVector2 max = ImGui.GetItemRectMax();
        bool hovered = ImGui.IsItemHovered();

        if (hovered)
        {
            NVector2 delta = ImGui.GetIO().MouseDelta;
            bool pan = ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
                (ImGui.IsMouseDragging(ImGuiMouseButton.Right) &&
                    (ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift)));

            if (pan)
            {
                _vanillaViewportPanX = Math.Clamp(_vanillaViewportPanX + delta.X, -size.X, size.X);
                _vanillaViewportPanY = Math.Clamp(_vanillaViewportPanY + delta.Y, -size.Y, size.Y);
            }
            else if (ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                _vanillaViewportYaw = NormalizeRadians(_vanillaViewportYaw + delta.X * 0.01f);
                _vanillaViewportPitch = Math.Clamp(_vanillaViewportPitch + delta.Y * 0.01f, -1.52f, 1.52f);
            }

            float wheel = ImGui.GetIO().MouseWheel;
            if (Math.Abs(wheel) > 0.001f)
            {
                _vanillaViewportZoom = Math.Clamp(_vanillaViewportZoom + wheel * 0.06f, 0.25f, 3.0f);
            }
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0.055f, 0.052f, 0.045f, 1f));
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 1f));
        uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.86f, 0.82f, 0.72f, 1f));
        drawList.AddRectFilled(min, max, background, 4f);

        VanillaPreviewMode effectiveMode = GetVanillaEffectivePreviewMode(scene);
        float viewportWidth = Math.Max(1f, max.X - min.X);
        float viewportHeight = Math.Max(1f, max.Y - min.Y);

        VanillaAnimationViewport3DRenderer renderer = EnsureVanillaPreviewRenderer();
        int textureId = renderer.RenderToTexture(
            scene,
            viewportWidth,
            viewportHeight,
            _vanillaViewportYaw,
            _vanillaViewportPitch,
            _vanillaViewportZoom,
            _vanillaViewportPanX,
            _vanillaViewportPanY,
            effectiveMode,
            _vanillaViewportWorldLighting,
            _vanillaVerbosePreviewLogs,
            out string? previewSkipReason);
        if (textureId > 0)
        {
            drawList.AddImage(new IntPtr(textureId), min, max, new NVector2(0f, 1f), new NVector2(1f, 0f));
        }
        else if (!string.IsNullOrWhiteSpace(previewSkipReason))
        {
            uint warning = ImGui.ColorConvertFloat4ToU32(new NVector4(0.95f, 0.72f, 0.43f, 1f));
            drawList.AddText(new NVector2(min.X + 12f, min.Y + 54f), warning, $"Preview skipped: {previewSkipReason}");
        }

        drawList.AddRect(min, max, border, 4f);
        drawList.AddText(new NVector2(min.X + 12f, min.Y + 10f), text, $"Preview: {scene.DisplayName}");
        drawList.AddText(new NVector2(min.X + 12f, min.Y + 30f), text, GetVanillaViewportHelpText(effectiveMode, scene));

        if (effectiveMode == VanillaPreviewMode.Orbit)
        {
            DrawVanillaViewportGizmo(row, scene, drawList, min, max, hovered);
        }
        else
        {
            if (_vanillaViewportGizmoDragAxis != TransformGizmoAxis.None)
            {
                ClearVanillaViewportGizmoDrag();
            }

            if (GizmoMode != TransformGizmoMode.None)
            {
                uint hint = ImGui.ColorConvertFloat4ToU32(new NVector4(0.76f, 0.72f, 0.64f, 1f));
                drawList.AddText(new NVector2(min.X + 12f, min.Y + 50f), hint, "Edit gizmos are available in Orbit mode.");
            }
        }
    }

    private VanillaPreviewMode GetVanillaEffectivePreviewMode(VanillaAnimationPreviewScene scene)
    {
        return _vanillaViewportMode switch
        {
            VanillaPreviewMode.FirstPerson when scene.ClassicFirstPersonAvailable => VanillaPreviewMode.FirstPerson,
            VanillaPreviewMode.ImmersiveFirstPerson when scene.ImmersiveFirstPersonAvailable => VanillaPreviewMode.ImmersiveFirstPerson,
            _ => VanillaPreviewMode.Orbit
        };
    }

    private static string GetVanillaViewportHelpText(VanillaPreviewMode mode, VanillaAnimationPreviewScene scene)
    {
        if (!scene.FirstPersonAvailable && mode != VanillaPreviewMode.Orbit)
        {
            return "First-person preview is only available for player-style meshes. RMB orbits. MMB or Shift+RMB pans. Mouse wheel zooms.";
        }

        return mode == VanillaPreviewMode.Orbit
            ? "RMB orbits. MMB or Shift+RMB pans. Mouse wheel zooms."
            : "First person: RMB adjusts preview yaw/pitch. MMB or Shift+RMB offsets. Mouse wheel changes hand FOV.";
    }

    private void DrawVanillaViewportGizmo(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, ImDrawListPtr drawList, NVector2 min, NVector2 max, bool hovered)
    {
        if (GizmoMode == TransformGizmoMode.None) return;
        if (!TryGetVanillaViewportGizmoTarget(row, out VanillaShapeAnimationEntry? entry, out VanillaAnimation? animation, out AnimationKeyFrame? keyFrame, out AnimationKeyFrameElement? element)) return;
        if (!TryGetVanillaGizmoProjection(scene, element, _vanillaSelection.ElementName, min, max, out VanillaGizmoProjection projection)) return;

        TransformGizmoAxis hoveredAxis = hovered ? PickVanillaViewportGizmoAxis(projection) : TransformGizmoAxis.None;
        if (hoveredAxis != TransformGizmoAxis.None)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && hoveredAxis != TransformGizmoAxis.None)
        {
            _vanillaViewportGizmoDragAxis = hoveredAxis;
            _vanillaViewportGizmoDragMode = GizmoMode;
            _vanillaViewportGizmoDragMouseStart = ImGui.GetMousePos();
            _vanillaViewportGizmoDragVector = GetVanillaGizmoDragVector(projection, hoveredAxis, _vanillaViewportGizmoDragMouseStart);
            _vanillaViewportGizmoDragCenter = projection.Center;
            _vanillaViewportGizmoDragLastAngleRadians = GetVanillaViewportGizmoMouseAngle(projection.Center, _vanillaViewportGizmoDragMouseStart);
            _vanillaViewportGizmoDragAccumulatedDegrees = 0;
            _vanillaViewportGizmoDragStartValue = GetVanillaGizmoAxisValue(element, GizmoMode, hoveredAxis);
            _vanillaViewportGizmoDragRowKey = row.Key;
            _vanillaViewportGizmoDragKeyFrameIndex = _vanillaSelection.KeyFrameIndex;
            _vanillaViewportGizmoDragElementName = _vanillaSelection.ElementName;
            _vanillaHistory.BeginEdit(entry.Document, _vanillaHistory.Capture(entry.Document, $"Gizmo {_vanillaSelection.ElementName}", row));
        }

        if (_vanillaViewportGizmoDragAxis != TransformGizmoAxis.None)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) ||
                _vanillaViewportGizmoDragRowKey != row.Key ||
                _vanillaViewportGizmoDragKeyFrameIndex != _vanillaSelection.KeyFrameIndex ||
                !string.Equals(_vanillaViewportGizmoDragElementName, _vanillaSelection.ElementName, StringComparison.OrdinalIgnoreCase))
            {
                CommitPendingVanillaHistory(entry.Document);
                ClearVanillaViewportGizmoDrag();
            }
            else
            {
                ApplyVanillaViewportGizmoDrag(row, entry, element, _vanillaViewportGizmoDragMode, _vanillaViewportGizmoDragAxis, _vanillaViewportGizmoDragVector, projection.Scale);
            }
        }

        drawList.PushClipRect(min, max, true);
        uint boundsColor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.15f, 0.78f, 1f, 0.72f));
        uint helperColor = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 0.58f));
        uint labelColor = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f));
        DrawVanillaViewportGizmoBounds(drawList, projection, boundsColor, helperColor);
        DrawVanillaViewportGizmoAxes(drawList, projection, hoveredAxis);
        drawList.AddText(projection.Center + new NVector2(8f, 8f), labelColor, _vanillaSelection.ElementName);
        drawList.PopClipRect();
    }

    private static void DrawVanillaViewportGizmoBounds(ImDrawListPtr drawList, VanillaGizmoProjection projection, uint boundsColor, uint helperColor)
    {
        if (projection.BoundsCorners.Length >= 8)
        {
            NVector2[] points = projection.BoundsCorners;
            DrawVanillaViewportLine(drawList, points[0], points[1], boundsColor, 2f);
            DrawVanillaViewportLine(drawList, points[1], points[2], boundsColor, 2f);
            DrawVanillaViewportLine(drawList, points[2], points[3], boundsColor, 2f);
            DrawVanillaViewportLine(drawList, points[3], points[0], boundsColor, 2f);
            DrawVanillaViewportLine(drawList, points[4], points[5], boundsColor, 2f);
            DrawVanillaViewportLine(drawList, points[5], points[6], boundsColor, 2f);
            DrawVanillaViewportLine(drawList, points[6], points[7], boundsColor, 2f);
            DrawVanillaViewportLine(drawList, points[7], points[4], boundsColor, 2f);
            DrawVanillaViewportLine(drawList, points[0], points[4], boundsColor, 2f);
            DrawVanillaViewportLine(drawList, points[1], points[5], boundsColor, 2f);
            DrawVanillaViewportLine(drawList, points[2], points[6], boundsColor, 2f);
            DrawVanillaViewportLine(drawList, points[3], points[7], boundsColor, 2f);
        }

        if (projection.HasVisualCenter && (projection.VisualCenter - projection.Center).Length() > 12f)
        {
            DrawVanillaViewportLine(drawList, projection.Center, projection.VisualCenter, helperColor, 2f);
            drawList.AddCircleFilled(projection.VisualCenter, 4f, helperColor, 16);
        }
    }

    private void DrawVanillaViewportGizmoAxes(ImDrawListPtr drawList, VanillaGizmoProjection projection, TransformGizmoAxis hoveredAxis)
    {
        uint red = ImGui.ColorConvertFloat4ToU32(new NVector4(0.95f, 0.18f, 0.14f, 1f));
        uint green = ImGui.ColorConvertFloat4ToU32(new NVector4(0.20f, 0.84f, 0.28f, 1f));
        uint blue = ImGui.ColorConvertFloat4ToU32(new NVector4(0.22f, 0.48f, 1f, 1f));
        uint white = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f));
        uint xColor = hoveredAxis == TransformGizmoAxis.X || _vanillaViewportGizmoDragAxis == TransformGizmoAxis.X ? white : red;
        uint yColor = hoveredAxis == TransformGizmoAxis.Y || _vanillaViewportGizmoDragAxis == TransformGizmoAxis.Y ? white : green;
        uint zColor = hoveredAxis == TransformGizmoAxis.Z || _vanillaViewportGizmoDragAxis == TransformGizmoAxis.Z ? white : blue;

        drawList.AddCircleFilled(projection.Center, 4.5f, white, 16);

        if (GizmoMode == TransformGizmoMode.Rotate)
        {
            DrawVanillaViewportGizmoRing(drawList, projection.RingX, xColor);
            DrawVanillaViewportGizmoRing(drawList, projection.RingY, yColor);
            DrawVanillaViewportGizmoRing(drawList, projection.RingZ, zColor);
            return;
        }

        DrawVanillaViewportGizmoAxis(drawList, projection.Center, projection.AxisX, xColor);
        DrawVanillaViewportGizmoAxis(drawList, projection.Center, projection.AxisY, yColor);
        DrawVanillaViewportGizmoAxis(drawList, projection.Center, projection.AxisZ, zColor);

        if (GizmoMode == TransformGizmoMode.Scale)
        {
            DrawVanillaViewportGizmoCube(drawList, projection.Center + projection.AxisX, xColor);
            DrawVanillaViewportGizmoCube(drawList, projection.Center + projection.AxisY, yColor);
            DrawVanillaViewportGizmoCube(drawList, projection.Center + projection.AxisZ, zColor);
        }
        else
        {
            DrawVanillaViewportGizmoArrow(drawList, projection.Center, projection.AxisX, xColor);
            DrawVanillaViewportGizmoArrow(drawList, projection.Center, projection.AxisY, yColor);
            DrawVanillaViewportGizmoArrow(drawList, projection.Center, projection.AxisZ, zColor);
        }
    }

    private static void DrawVanillaViewportGizmoAxis(ImDrawListPtr drawList, NVector2 center, NVector2 axis, uint color)
    {
        DrawVanillaViewportLine(drawList, center, center + axis, color, 2.4f);
    }

    private static void DrawVanillaViewportGizmoArrow(ImDrawListPtr drawList, NVector2 center, NVector2 axis, uint color)
    {
        NVector2 tip = center + axis;
        NVector2 dir = NormalizeOrDefault(axis, new NVector2(1f, 0f));
        NVector2 normal = new(-dir.Y, dir.X);
        drawList.AddTriangleFilled(tip, tip - dir * 13f + normal * 5.5f, tip - dir * 13f - normal * 5.5f, color);
    }

    private static void DrawVanillaViewportGizmoCube(ImDrawListPtr drawList, NVector2 center, uint color)
    {
        NVector2 half = new(5.5f, 5.5f);
        drawList.AddRectFilled(center - half, center + half, color, 1.5f);
    }

    private static void DrawVanillaViewportGizmoRing(ImDrawListPtr drawList, NVector2[] points, uint color)
    {
        for (int i = 1; i < points.Length; i++)
        {
            DrawVanillaViewportLine(drawList, points[i - 1], points[i], color, 2.4f);
        }
    }

    private static void DrawVanillaViewportLine(ImDrawListPtr drawList, NVector2 start, NVector2 end, uint color, float thickness)
    {
        if (!IsFinite(start.X) || !IsFinite(start.Y) || !IsFinite(end.X) || !IsFinite(end.Y)) return;
        drawList.AddLine(start, end, color, thickness);
    }

    private TransformGizmoAxis PickVanillaViewportGizmoAxis(VanillaGizmoProjection projection)
    {
        NVector2 mouse = ImGui.GetMousePos();
        if (GizmoMode == TransformGizmoMode.Rotate)
        {
            float dx = DistanceToPolyline(mouse, projection.RingX);
            float dy = DistanceToPolyline(mouse, projection.RingY);
            float dz = DistanceToPolyline(mouse, projection.RingZ);
            float min = Math.Min(dx, Math.Min(dy, dz));
            if (min > 14f) return TransformGizmoAxis.None;
            if (min == dx) return TransformGizmoAxis.X;
            if (min == dy) return TransformGizmoAxis.Y;
            return TransformGizmoAxis.Z;
        }

        TransformGizmoAxis picked = TransformGizmoAxis.None;
        float best = 14f;
        TestAxis(TransformGizmoAxis.X, projection.AxisX);
        TestAxis(TransformGizmoAxis.Y, projection.AxisY);
        TestAxis(TransformGizmoAxis.Z, projection.AxisZ);
        return picked;

        void TestAxis(TransformGizmoAxis axis, NVector2 vector)
        {
            float distance = DistanceToSegment(mouse, projection.Center, projection.Center + vector);
            if (distance < best)
            {
                best = distance;
                picked = axis;
            }
        }
    }

    private NVector2 GetVanillaGizmoDragVector(VanillaGizmoProjection projection, TransformGizmoAxis axis, NVector2 mouse)
    {
        if (GizmoMode == TransformGizmoMode.Rotate)
        {
            NVector2 radial = mouse - projection.Center;
            NVector2 tangent = new(-radial.Y, radial.X);
            return NormalizeOrDefault(tangent, GetVanillaProjectedAxis(projection, axis));
        }

        return NormalizeOrDefault(GetVanillaProjectedAxis(projection, axis), new NVector2(1f, 0f));
    }

    private static NVector2 GetVanillaProjectedAxis(VanillaGizmoProjection projection, TransformGizmoAxis axis)
    {
        return axis switch
        {
            TransformGizmoAxis.X => projection.AxisX,
            TransformGizmoAxis.Y => projection.AxisY,
            TransformGizmoAxis.Z => projection.AxisZ,
            _ => projection.AxisX
        };
    }

    private bool ApplyVanillaViewportGizmoDrag(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrameElement element, TransformGizmoMode mode, TransformGizmoAxis axis, NVector2 axisVector, float scale)
    {
        NVector2 direction = NormalizeOrDefault(axisVector, new NVector2(1f, 0f));
        NVector2 mouseDelta = ImGui.GetMousePos() - _vanillaViewportGizmoDragMouseStart;
        double projected = NVector2.Dot(mouseDelta, direction);
        double value = _vanillaViewportGizmoDragStartValue;

        switch (mode)
        {
            case TransformGizmoMode.Move:
                value += projected / Math.Max(1f, scale) * 16.0;
                value = SnapVanillaGizmoValue(value, Math.Max(0.001, TransformGizmoIncrement * 16.0));
                break;
            case TransformGizmoMode.Scale:
                value += projected / Math.Max(1f, scale) * 16.0;
                value = SnapVanillaGizmoValue(value, Math.Max(0.001, TransformGizmoIncrement * 16.0));
                break;
            case TransformGizmoMode.Rotate:
                value += UpdateVanillaViewportGizmoRingDrag();
                value = NormalizeVanillaDegrees(SnapVanillaGizmoValue(value, Math.Max(0.001, TransformGizmoIncrement)));
                break;
            default:
                return false;
        }

        if (Math.Abs(value - GetVanillaGizmoAxisValue(element, mode, axis)) < 0.0001) return false;
        SetVanillaGizmoAxisValue(element, mode, axis, value);
        MarkVanillaDirty(entry.Document);
        RefreshVanillaPreviewAfterEdit(row);
        return true;
    }

    private double UpdateVanillaViewportGizmoRingDrag()
    {
        NVector2 radial = ImGui.GetMousePos() - _vanillaViewportGizmoDragCenter;
        if (radial.LengthSquared() < 16f)
        {
            return _vanillaViewportGizmoDragAccumulatedDegrees;
        }

        double angle = Math.Atan2(radial.Y, radial.X);
        double delta = NormalizeVanillaRadians(angle - _vanillaViewportGizmoDragLastAngleRadians);
        _vanillaViewportGizmoDragLastAngleRadians = angle;
        _vanillaViewportGizmoDragAccumulatedDegrees -= delta * 180.0 / Math.PI;
        return _vanillaViewportGizmoDragAccumulatedDegrees;
    }

    private void DrawVanillaElementGizmoControls()
    {
        ImGui.SeparatorText("Gizmo");
        if (ImGui.RadioButton("Move##vanilla-gizmo-mode", GizmoMode == TransformGizmoMode.Move)) GizmoMode = TransformGizmoMode.Move;
        ImGui.SameLine();
        if (ImGui.RadioButton("Rotate##vanilla-gizmo-mode", GizmoMode == TransformGizmoMode.Rotate)) GizmoMode = TransformGizmoMode.Rotate;
        ImGui.SameLine();
        if (ImGui.RadioButton("Scale##vanilla-gizmo-mode", GizmoMode == TransformGizmoMode.Scale)) GizmoMode = TransformGizmoMode.Scale;
        ImGui.SameLine();
        if (ImGui.RadioButton("Off##vanilla-gizmo-mode", GizmoMode == TransformGizmoMode.None)) GizmoMode = TransformGizmoMode.None;

        if (GizmoSpace == TransformGizmoSpace.Parent) GizmoSpace = TransformGizmoSpace.Local;
        if (ImGui.RadioButton("World axes##vanilla-gizmo-space", GizmoSpace == TransformGizmoSpace.World)) GizmoSpace = TransformGizmoSpace.World;
        ImGui.SameLine();
        if (ImGui.RadioButton("Local axes##vanilla-gizmo-space", GizmoSpace == TransformGizmoSpace.Local)) GizmoSpace = TransformGizmoSpace.Local;

        bool snap = IncludeGizmoInIncrement;
        if (ImGui.Checkbox("Snap drag##vanilla-gizmo-snap", ref snap))
        {
            IncludeGizmoInIncrement = snap;
        }

        ImGui.SameLine();
        float increment = TransformGizmoIncrement;
        ImGui.SetNextItemWidth(90);
        if (ImGui.DragFloat("Increment##vanilla-gizmo-increment", ref increment, 0.01f, 0.001f, 90f))
        {
            TransformGizmoIncrement = Math.Max(0.001f, increment);
        }

        ImGui.TextDisabled("Drag the colored viewport handles to edit the selected element.");
    }

    private bool TryGetVanillaViewportGizmoTarget(VanillaBrowserRow row, out VanillaShapeAnimationEntry entry, out VanillaAnimation animation, out AnimationKeyFrame keyFrame, out AnimationKeyFrameElement element)
    {
        entry = null!;
        animation = null!;
        keyFrame = null!;
        element = null!;

        VanillaShapeAnimationEntry? selectedEntry = row.ShapeAnimation ?? row.MetadataEntry?.ResolveCurrentShape();
        if (selectedEntry == null || selectedEntry.Animation.KeyFrames == null || selectedEntry.Animation.KeyFrames.Length == 0) return false;

        entry = selectedEntry;
        animation = selectedEntry.Animation;
        _vanillaSelection.KeyFrameIndex = Math.Clamp(_vanillaSelection.KeyFrameIndex, 0, animation.KeyFrames.Length - 1);
        keyFrame = animation.KeyFrames[_vanillaSelection.KeyFrameIndex];
        if (keyFrame.Elements == null || keyFrame.Elements.Count == 0) return false;
        if (string.IsNullOrWhiteSpace(_vanillaSelection.ElementName) || !keyFrame.Elements.ContainsKey(_vanillaSelection.ElementName))
        {
            _vanillaSelection.ElementName = keyFrame.Elements.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).First();
        }
        if (!keyFrame.Elements.TryGetValue(_vanillaSelection.ElementName, out AnimationKeyFrameElement? found) || found == null) return false;
        element = found;
        return true;
    }

    private bool TryGetVanillaGizmoProjection(VanillaAnimationPreviewScene scene, AnimationKeyFrameElement keyFrameElement, string elementName, NVector2 min, NVector2 max, out VanillaGizmoProjection projection)
    {
        projection = default;
        float width = Math.Max(1f, max.X - min.X);
        float height = Math.Max(1f, max.Y - min.Y);

        ShapeElement? shapeElement = FindShapeElement(scene.Shape, elementName);
        ElementPose? pose = scene.Animator.GetPosebyName(elementName);
        if (pose?.ForElement == null && shapeElement != null)
        {
            pose = scene.Animator.GetPosebyName(shapeElement.Name);
        }
        if (pose?.ForElement == null) return false;

        VanillaPreviewCameraState camera = BuildVanillaPreviewCamera(scene, width, height, _vanillaViewportYaw, _vanillaViewportPitch, _vanillaViewportZoom, _vanillaViewportPanX, _vanillaViewportPanY, GetVanillaEffectivePreviewMode(scene));
        Matrixf elementModel = BuildVanillaElementModelMatrix(camera.Model, pose);
        NVector3 elementPoint = GetVanillaGizmoLocalPoint(pose);
        if (!ProjectVanillaPreviewPoint(elementModel, camera, elementPoint, min, width, height, out NVector2 center)) return false;

        float modelAxisLength = Math.Clamp(Math.Max(Math.Max(scene.ModelWidth, scene.ModelHeight), scene.ModelDepth) * 0.16f, 0.12f, 0.85f);
        if (!ProjectVanillaPreviewPoint(elementModel, camera, elementPoint + new NVector3(modelAxisLength, 0f, 0f), min, width, height, out NVector2 axisXEnd)) return false;
        if (!ProjectVanillaPreviewPoint(elementModel, camera, elementPoint + new NVector3(0f, modelAxisLength, 0f), min, width, height, out NVector2 axisYEnd)) return false;
        if (!ProjectVanillaPreviewPoint(elementModel, camera, elementPoint + new NVector3(0f, 0f, modelAxisLength), min, width, height, out NVector2 axisZEnd)) return false;
        NVector2 axisX = axisXEnd - center;
        NVector2 axisY = axisYEnd - center;
        NVector2 axisZ = axisZEnd - center;
        float pixelScale = Math.Max(1f, (axisX.Length() + axisY.Length() + axisZ.Length()) / Math.Max(0.001f, modelAxisLength * 3f));
        float modelRingRadius = Math.Clamp(modelAxisLength * 0.95f, 0.10f, 0.80f);
        NVector2[] bounds = BuildVanillaElementBounds3D(camera, elementModel, pose.ForElement, min, width, height, out bool hasVisualCenter, out NVector2 visualCenter);

        NVector2[] ringX = BuildVanillaViewportGizmoRing(camera, elementModel, elementPoint, modelRingRadius, min, width, height, TransformGizmoAxis.X);
        NVector2[] ringY = BuildVanillaViewportGizmoRing(camera, elementModel, elementPoint, modelRingRadius, min, width, height, TransformGizmoAxis.Y);
        NVector2[] ringZ = BuildVanillaViewportGizmoRing(camera, elementModel, elementPoint, modelRingRadius, min, width, height, TransformGizmoAxis.Z);
        if (GizmoMode == TransformGizmoMode.Rotate && (ringX.Length == 0 || ringY.Length == 0 || ringZ.Length == 0)) return false;

        projection = new(
            center,
            pixelScale,
            axisX,
            axisY,
            axisZ,
            ringX,
            ringY,
            ringZ,
            bounds,
            hasVisualCenter,
            visualCenter);
        return true;
    }

    private static Matrixf BuildVanillaElementModelMatrix(Matrixf model, ElementPose pose)
    {
        Matrixf result = new();
        result.Set(model.Values);
        result.Mul(pose.AnimModelMatrix);
        return result;
    }

    private static Matrixf BuildVanillaElementModelViewMatrix(Matrixf modelView, ElementPose pose)
    {
        Matrixf result = new();
        result.Set(modelView.Values);
        result.Mul(pose.AnimModelMatrix);
        return result;
    }

    private static NVector3 GetVanillaGizmoLocalPoint(ElementPose pose)
    {
        ShapeElement element = pose.ForElement;
        double[]? rotationOrigin = element.RotationOrigin;
        double originX = rotationOrigin != null && rotationOrigin.Length > 0 ? rotationOrigin[0] : element.From?[0] ?? 0;
        double originY = rotationOrigin != null && rotationOrigin.Length > 1 ? rotationOrigin[1] : element.From?[1] ?? 0;
        double originZ = rotationOrigin != null && rotationOrigin.Length > 2 ? rotationOrigin[2] : element.From?[2] ?? 0;
        double fromX = element.From != null && element.From.Length > 0 ? element.From[0] : 0;
        double fromY = element.From != null && element.From.Length > 1 ? element.From[1] : 0;
        double fromZ = element.From != null && element.From.Length > 2 ? element.From[2] : 0;

        return new NVector3(
            (float)((originX - fromX) / 16.0 - pose.translateX),
            (float)((originY - fromY) / 16.0 - pose.translateY),
            (float)((originZ - fromZ) / 16.0 - pose.translateZ));
    }

    private static bool TryGetShapeElementRotationOrigin(ShapeElement element, out double x, out double y, out double z)
    {
        x = 0;
        y = 0;
        z = 0;
        if (element.RotationOrigin == null || element.RotationOrigin.Length < 3) return false;

        x = element.RotationOrigin[0] / 16.0;
        y = element.RotationOrigin[1] / 16.0;
        z = element.RotationOrigin[2] / 16.0;
        return true;
    }

    private static NVector2[] BuildVanillaElementBounds(Matrixf elementModelView, ShapeElement? element, out bool hasVisualCenter, out NVector2 visualCenter)
    {
        hasVisualCenter = false;
        visualCenter = default;
        if (element?.From == null || element.To == null || element.From.Length < 3 || element.To.Length < 3) return [];

        float centerX = (float)((element.To[0] - element.From[0]) / 32.0);
        float centerY = (float)((element.To[1] - element.From[1]) / 32.0);
        float centerZ = (float)((element.To[2] - element.From[2]) / 32.0);
        float halfX = Math.Max(0.08f, (float)Math.Abs(element.To[0] - element.From[0]) / 32f);
        float halfY = Math.Max(0.08f, (float)Math.Abs(element.To[1] - element.From[1]) / 32f);
        float halfZ = Math.Max(0.08f, (float)Math.Abs(element.To[2] - element.From[2]) / 32f);

        const float padding = 0.035f;
        halfX += padding;
        halfY += padding;
        halfZ += padding;

        float fromX = centerX - halfX;
        float fromY = centerY - halfY;
        float fromZ = centerZ - halfZ;
        float toX = centerX + halfX;
        float toY = centerY + halfY;
        float toZ = centerZ + halfZ;

        NVector3[] corners =
        {
            new(fromX, fromY, fromZ),
            new(toX, fromY, fromZ),
            new(toX, toY, fromZ),
            new(fromX, toY, fromZ),
            new(fromX, fromY, toZ),
            new(toX, fromY, toZ),
            new(toX, toY, toZ),
            new(fromX, toY, toZ)
        };

        visualCenter = ProjectVanillaGuiPoint(elementModelView, new NVector3(centerX, centerY, centerZ));
        hasVisualCenter = true;
        NVector2[] projected = new NVector2[corners.Length];
        for (int index = 0; index < corners.Length; index++)
        {
            projected[index] = ProjectVanillaGuiPoint(elementModelView, corners[index]);
        }

        return projected;
    }

    private static NVector2[] BuildVanillaElementBounds3D(VanillaPreviewCameraState camera, Matrixf elementModel, ShapeElement? element, NVector2 min, float width, float height, out bool hasVisualCenter, out NVector2 visualCenter)
    {
        hasVisualCenter = false;
        visualCenter = default;
        if (element?.From == null || element.To == null || element.From.Length < 3 || element.To.Length < 3) return [];

        float centerX = (float)((element.To[0] - element.From[0]) / 32.0);
        float centerY = (float)((element.To[1] - element.From[1]) / 32.0);
        float centerZ = (float)((element.To[2] - element.From[2]) / 32.0);
        float halfX = Math.Max(0.08f, (float)Math.Abs(element.To[0] - element.From[0]) / 32f);
        float halfY = Math.Max(0.08f, (float)Math.Abs(element.To[1] - element.From[1]) / 32f);
        float halfZ = Math.Max(0.08f, (float)Math.Abs(element.To[2] - element.From[2]) / 32f);

        const float padding = 0.035f;
        halfX += padding;
        halfY += padding;
        halfZ += padding;

        float fromX = centerX - halfX;
        float fromY = centerY - halfY;
        float fromZ = centerZ - halfZ;
        float toX = centerX + halfX;
        float toY = centerY + halfY;
        float toZ = centerZ + halfZ;

        NVector3[] corners =
        {
            new(fromX, fromY, fromZ),
            new(toX, fromY, fromZ),
            new(toX, toY, fromZ),
            new(fromX, toY, fromZ),
            new(fromX, fromY, toZ),
            new(toX, fromY, toZ),
            new(toX, toY, toZ),
            new(fromX, toY, toZ)
        };

        hasVisualCenter = ProjectVanillaPreviewPoint(elementModel, camera, new NVector3(centerX, centerY, centerZ), min, width, height, out visualCenter);
        NVector2[] projected = new NVector2[corners.Length];
        for (int index = 0; index < corners.Length; index++)
        {
            if (!ProjectVanillaPreviewPoint(elementModel, camera, corners[index], min, width, height, out projected[index]))
            {
                hasVisualCenter = false;
                visualCenter = default;
                return [];
            }
        }

        return projected;
    }

    private static NVector2[] BuildVanillaViewportGizmoRing(Matrixf modelView, NVector3 center, float modelRadius, TransformGizmoAxis axis)
    {
        const int segments = 72;
        NVector2[] points = new NVector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float angle = (float)(i / (double)segments * Math.PI * 2.0);
            float cos = (float)Math.Cos(angle) * modelRadius;
            float sin = (float)Math.Sin(angle) * modelRadius;
            NVector3 local = axis switch
            {
                TransformGizmoAxis.X => new NVector3(0f, cos, sin),
                TransformGizmoAxis.Y => new NVector3(cos, 0f, sin),
                TransformGizmoAxis.Z => new NVector3(cos, sin, 0f),
                _ => new NVector3(cos, sin, 0f)
            };
            points[i] = ProjectVanillaGuiPoint(modelView, center + local);
        }

        return points;
    }

    private static NVector2[] BuildVanillaViewportGizmoRing(VanillaPreviewCameraState camera, Matrixf elementModel, NVector3 center, float modelRadius, NVector2 min, float width, float height, TransformGizmoAxis axis)
    {
        const int segments = 72;
        NVector2[] points = new NVector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float angle = (float)(i / (double)segments * Math.PI * 2.0);
            float cos = (float)Math.Cos(angle) * modelRadius;
            float sin = (float)Math.Sin(angle) * modelRadius;
            NVector3 local = axis switch
            {
                TransformGizmoAxis.X => new NVector3(0f, cos, sin),
                TransformGizmoAxis.Y => new NVector3(cos, 0f, sin),
                TransformGizmoAxis.Z => new NVector3(cos, sin, 0f),
                _ => new NVector3(cos, sin, 0f)
            };
            if (!ProjectVanillaPreviewPoint(elementModel, camera, center + local, min, width, height, out points[i]))
            {
                return [];
            }
        }

        return points;
    }

    private static Matrixf BuildVanillaGuiModelMatrix(float posX, float posY, float posZ, float guiSize, float entityScale, float rotX, float rotY, float rotZ)
    {
        Matrixf matrix = new();
        matrix.Identity();
        matrix.Translate(posX, posY, posZ);
        matrix.Translate(guiSize, 2f * guiSize, 0f);
        matrix.RotateX(rotX);
        matrix.RotateY(rotY);
        matrix.RotateZ(rotZ);
        matrix.Scale(entityScale, entityScale, entityScale);
        matrix.Translate(-0.5f, 0f, -0.5f);
        return matrix;
    }

    private static Matrixf BuildVanillaGuiModelViewMatrix(float posX, float posY, float posZ, float guiSize, float entityScale, float rotX, float rotY, float rotZ)
    {
        Matrixf matrix = BuildVanillaGuiModelMatrix(posX, posY, posZ, guiSize, entityScale, rotX, rotY, rotZ);
        ApplyVanillaGuiModelViewFlip(matrix);
        return matrix;
    }

    private static void ApplyVanillaGuiModelViewFlip(Matrixf matrix)
    {
        matrix.Translate(0.5f, 0f, 0.5f);
        matrix.Scale(1f, 1f, -1f);
        matrix.Translate(-0.5f, 0f, -0.5f);
    }

    private static NVector2 ProjectVanillaGuiPoint(Matrixf matrix, NVector3 point)
    {
        Vec4f transformed = matrix.TransformVector(new Vec4f(point.X, point.Y, point.Z, 1f));
        return new NVector2(transformed.X, transformed.Y);
    }

    private static VanillaPreviewCameraState BuildVanillaPreviewCamera(VanillaAnimationPreviewScene scene, float width, float height, float yaw, float pitch, float zoom, float panX, float panY, VanillaPreviewMode mode)
    {
        return mode == VanillaPreviewMode.FirstPerson || mode == VanillaPreviewMode.ImmersiveFirstPerson
            ? BuildVanillaFirstPersonPreviewCamera(scene, width, height, yaw, pitch, zoom, panX, panY, mode)
            : BuildVanillaOrbitPreviewCamera(scene, width, height, yaw, pitch, zoom, panX, panY);
    }

    private static VanillaPreviewCameraState BuildVanillaOrbitPreviewCamera(VanillaAnimationPreviewScene scene, float width, float height, float yaw, float pitch, float zoom, float panX, float panY)
    {
        float aspect = Math.Max(0.1f, width / Math.Max(1f, height));
        float fov = 35f * GameMath.DEG2RAD;
        float entitySize = Math.Max(0.001f, scene.GuiEntitySize);
        float radius = Math.Max(Math.Max(scene.ModelWidth, scene.ModelHeight), scene.ModelDepth) * entitySize * 0.62f;
        radius = Math.Max(0.35f, radius);
        float distance = Math.Clamp(radius / Math.Max(0.05f, (float)Math.Tan(fov * 0.5f)) * 1.45f / Math.Clamp(zoom, 0.25f, 3f), radius + 0.25f, radius * 10f + 16f);

        Matrixf model = BuildVanillaPreviewModelMatrix(scene);
        Vec4f modelCenter = model.TransformVector(new Vec4f(scene.ModelCenterX, scene.ModelCenterY, scene.ModelCenterZ, 1f));
        NVector3 target = new(modelCenter.X, modelCenter.Y, modelCenter.Z);

        pitch = Math.Clamp(pitch, -1.52f, 1.52f);
        float cosPitch = (float)Math.Cos(pitch);
        NVector3 outward = NormalizeOrDefault(new NVector3(
            (float)Math.Sin(yaw) * cosPitch,
            (float)Math.Sin(pitch),
            (float)Math.Cos(yaw) * cosPitch), new NVector3(0f, 0f, 1f));
        NVector3 right = NormalizeOrDefault(NVector3.Cross(NVector3.UnitY, outward), NVector3.UnitX);
        NVector3 up = NormalizeOrDefault(NVector3.Cross(outward, right), NVector3.UnitY);
        float panScale = 2f * distance * (float)Math.Tan(fov * 0.5f) / Math.Max(1f, height);
        target += -right * panX * panScale + up * panY * panScale;
        NVector3 eye = target + outward * distance;

        float near = Math.Max(0.01f, distance - radius * 6f);
        near = Math.Min(near, 0.05f);
        float far = Math.Max(64f, distance + radius * 8f + 8f);

        Matrixf projection = new();
        projection.Set(Mat4f.Perspective(Mat4f.Create(), fov, aspect, near, far));
        Matrixf view = new();
        view.Set(Mat4f.LookAt(Mat4f.Create(), [eye.X, eye.Y, eye.Z], [target.X, target.Y, target.Z], [up.X, up.Y, up.Z]));
        Matrixf projectionView = new();
        projectionView.Set(view.Values);
        projectionView.ReverseMul(projection.Values);

        return new(projection, view, projectionView, model, eye, target, distance);
    }

    private static VanillaPreviewCameraState BuildVanillaFirstPersonPreviewCamera(VanillaAnimationPreviewScene scene, float width, float height, float yaw, float pitch, float zoom, float panX, float panY, VanillaPreviewMode mode)
    {
        float aspect = Math.Max(0.1f, width / Math.Max(1f, height));
        float handsFov = Math.Clamp(scene.FirstPersonFovDegrees * PlayerRenderingPatches.HandsFovMultiplier, 25f, 130f);
        float fov = Math.Clamp(handsFov / Math.Clamp(zoom, 0.25f, 3f), 25f, 130f) * GameMath.DEG2RAD;
        float entitySize = Math.Max(0.001f, scene.GuiEntitySize);
        float radius = Math.Max(Math.Max(scene.ModelWidth, scene.ModelHeight), scene.ModelDepth) * entitySize * 0.62f;
        radius = Math.Max(0.35f, radius);

        pitch = Math.Clamp(pitch, -1.52f, 1.52f);
        float cosPitch = (float)Math.Cos(pitch);
        NVector3 forward = NormalizeOrDefault(new NVector3(
            (float)Math.Sin(yaw) * cosPitch,
            -(float)Math.Sin(pitch),
            -(float)Math.Cos(yaw) * cosPitch), new NVector3(0f, 0f, -1f));
        NVector3 right = NormalizeOrDefault(NVector3.Cross(forward, NVector3.UnitY), NVector3.UnitX);
        NVector3 up = NormalizeOrDefault(NVector3.Cross(right, forward), NVector3.UnitY);

        Matrixf model = BuildVanillaFirstPersonModelMatrix(scene, yaw, pitch, panX, panY, width, height, fov, mode);
        NVector3 eye = NVector3.Zero;
        NVector3 target = forward * Math.Max(1f, radius * 2f);

        Matrixf projection = new();
        projection.Set(Mat4f.Perspective(Mat4f.Create(), fov, aspect, 0.005f, Math.Max(64f, radius * 18f + 16f)));
        Matrixf view = new();
        view.Set(Mat4f.LookAt(Mat4f.Create(), [eye.X, eye.Y, eye.Z], [target.X, target.Y, target.Z], [up.X, up.Y, up.Z]));
        Matrixf projectionView = new();
        projectionView.Set(view.Values);
        projectionView.ReverseMul(projection.Values);

        return new(projection, view, projectionView, model, eye, target, 0f);
    }

    private static Matrixf BuildVanillaFirstPersonModelMatrix(VanillaAnimationPreviewScene scene, float yaw, float pitch, float panX, float panY, float width, float height, float fov, VanillaPreviewMode mode)
    {
        float entitySize = Math.Max(0.001f, scene.GuiEntitySize);
        float localEyeHeight = scene.EntityEyeHeight / entitySize;
        localEyeHeight = Math.Clamp(localEyeHeight, 0.05f, Math.Max(scene.ModelHeight + 1f, 0.25f));
        float panScale = 2f * (float)Math.Tan(fov * 0.5f) / Math.Max(1f, Math.Min(width, height));

        Matrixf model = new();
        model.Identity();
        model.Translate(-panX * panScale, panY * panScale, 0f);
        model.RotateX(scene.GuiShapeRotateX * GameMath.DEG2RAD);
        model.RotateY(yaw + (90f + scene.GuiShapeRotateY) * GameMath.DEG2RAD);

        if (mode == VanillaPreviewMode.FirstPerson)
        {
            model.RotateZ(scene.GuiShapeRotateZ * GameMath.DEG2RAD);
            model.Translate(0f, localEyeHeight, 0f);
            model.RotateZ(pitch * 0.75f);
            model.Translate(0f, -localEyeHeight, 0f);
            model.Translate(0f, scene.FirstPersonYOffset, 0f);
        }
        else
        {
            model.RotateZ(scene.GuiShapeRotateZ * GameMath.DEG2RAD);
        }

        model.Scale(entitySize, entitySize, entitySize);
        model.Translate(-0.5f, -localEyeHeight, -0.5f);
        return model;
    }

    private static Matrixf BuildVanillaPreviewModelMatrix(VanillaAnimationPreviewScene scene)
    {
        float entitySize = Math.Max(0.001f, scene.GuiEntitySize);
        Matrixf model = new();
        model.Identity();
        model.Scale(entitySize, entitySize, entitySize);
        model.Translate(-0.5f, 0f, -0.5f);
        return model;
    }

    private static bool ProjectVanillaPreviewPoint(Matrixf localToWorld, VanillaPreviewCameraState camera, NVector3 point, NVector2 min, float width, float height, out NVector2 screen)
    {
        Matrixf mvp = new();
        mvp.Set(localToWorld.Values);
        mvp.ReverseMul(camera.ProjectionView.Values);
        Vec4f clip = mvp.TransformVector(new Vec4f(point.X, point.Y, point.Z, 1f));
        if (!IsFinite(clip.W) || clip.W <= 0.001f)
        {
            screen = default;
            return false;
        }

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        if (!IsFinite(ndcX) || !IsFinite(ndcY))
        {
            screen = default;
            return false;
        }

        screen = new NVector2(
            min.X + (ndcX * 0.5f + 0.5f) * width,
            min.Y + (0.5f - ndcY * 0.5f) * height);
        if (!IsFinite(screen.X) || !IsFinite(screen.Y))
        {
            screen = default;
            return false;
        }

        return ndcX > -2f && ndcX < 2f && ndcY > -2f && ndcY < 2f;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static ShapeElement? FindShapeElement(Shape shape, string elementName)
    {
        if (shape.Elements == null || string.IsNullOrWhiteSpace(elementName)) return null;
        foreach (ShapeElement element in shape.Elements)
        {
            ShapeElement? found = FindShapeElementRecursive(element, elementName);
            if (found != null) return found;
        }

        return null;
    }

    private static ShapeElement? FindShapeElementRecursive(ShapeElement element, string elementName)
    {
        if (string.Equals(element.Name, elementName, StringComparison.OrdinalIgnoreCase)) return element;
        if (element.Children == null) return null;
        foreach (ShapeElement child in element.Children)
        {
            ShapeElement? found = FindShapeElementRecursive(child, elementName);
            if (found != null) return found;
        }

        return null;
    }

    private static void GetShapeElementCenter(ShapeElement element, out double x, out double y, out double z)
    {
        double fromX = element.From is { Length: >= 3 } from ? from[0] : 0;
        double fromY = element.From is { Length: >= 3 } from2 ? from2[1] : 0;
        double fromZ = element.From is { Length: >= 3 } from3 ? from3[2] : 0;
        double toX = element.To is { Length: >= 3 } to ? to[0] : fromX;
        double toY = element.To is { Length: >= 3 } to2 ? to2[1] : fromY;
        double toZ = element.To is { Length: >= 3 } to3 ? to3[2] : fromZ;
        x = (fromX + toX) / 32.0;
        y = (fromY + toY) / 32.0;
        z = (fromZ + toZ) / 32.0;
    }

    private static double GetVanillaGizmoAxisValue(AnimationKeyFrameElement element, TransformGizmoMode mode, TransformGizmoAxis axis)
    {
        return mode switch
        {
            TransformGizmoMode.Move => axis switch
            {
                TransformGizmoAxis.X => element.OffsetX ?? 0,
                TransformGizmoAxis.Y => element.OffsetY ?? 0,
                TransformGizmoAxis.Z => element.OffsetZ ?? 0,
                _ => 0
            },
            TransformGizmoMode.Rotate => axis switch
            {
                TransformGizmoAxis.X => element.RotationX ?? 0,
                TransformGizmoAxis.Y => element.RotationY ?? 0,
                TransformGizmoAxis.Z => element.RotationZ ?? 0,
                _ => 0
            },
            TransformGizmoMode.Scale => axis switch
            {
                TransformGizmoAxis.X => element.StretchX ?? 1,
                TransformGizmoAxis.Y => element.StretchY ?? 1,
                TransformGizmoAxis.Z => element.StretchZ ?? 1,
                _ => 0
            },
            _ => 0
        };
    }

    private static void SetVanillaGizmoAxisValue(AnimationKeyFrameElement element, TransformGizmoMode mode, TransformGizmoAxis axis, double value)
    {
        switch (mode)
        {
            case TransformGizmoMode.Move:
                if (axis == TransformGizmoAxis.X) element.OffsetX = value;
                if (axis == TransformGizmoAxis.Y) element.OffsetY = value;
                if (axis == TransformGizmoAxis.Z) element.OffsetZ = value;
                CompleteVanillaPositionGroup(element);
                break;
            case TransformGizmoMode.Rotate:
                if (axis == TransformGizmoAxis.X) element.RotationX = value;
                if (axis == TransformGizmoAxis.Y) element.RotationY = value;
                if (axis == TransformGizmoAxis.Z) element.RotationZ = value;
                CompleteVanillaRotationGroup(element);
                break;
            case TransformGizmoMode.Scale:
                if (axis == TransformGizmoAxis.X) element.StretchX = value;
                if (axis == TransformGizmoAxis.Y) element.StretchY = value;
                if (axis == TransformGizmoAxis.Z) element.StretchZ = value;
                CompleteVanillaStretchGroup(element);
                break;
        }
    }

    private static void CompleteVanillaElementTransformGroups(AnimationKeyFrameElement element)
    {
        CompleteVanillaPositionGroup(element);
        CompleteVanillaRotationGroup(element);
        CompleteVanillaStretchGroup(element);
    }

    private static void CompleteVanillaPositionGroup(AnimationKeyFrameElement element)
    {
        if (!element.PositionSet) return;
        element.OffsetX ??= 0;
        element.OffsetY ??= 0;
        element.OffsetZ ??= 0;
    }

    private static void CompleteVanillaRotationGroup(AnimationKeyFrameElement element)
    {
        if (!element.RotationSet) return;
        element.RotationX ??= 0;
        element.RotationY ??= 0;
        element.RotationZ ??= 0;
    }

    private static void CompleteVanillaStretchGroup(AnimationKeyFrameElement element)
    {
        if (!element.StretchSet) return;
        element.StretchX ??= 1;
        element.StretchY ??= 1;
        element.StretchZ ??= 1;
    }

    private void ClearVanillaViewportGizmoDrag()
    {
        _vanillaViewportGizmoDragAxis = TransformGizmoAxis.None;
        _vanillaViewportGizmoDragMode = TransformGizmoMode.None;
        _vanillaViewportGizmoDragVector = new NVector2(1f, 0f);
        _vanillaViewportGizmoDragCenter = NVector2.Zero;
        _vanillaViewportGizmoDragLastAngleRadians = 0;
        _vanillaViewportGizmoDragAccumulatedDegrees = 0;
        _vanillaViewportGizmoDragRowKey = "";
        _vanillaViewportGizmoDragKeyFrameIndex = -1;
        _vanillaViewportGizmoDragElementName = "";
    }

    private double SnapVanillaGizmoValue(double value, double step)
    {
        return IncludeGizmoInIncrement ? Math.Round(value / step) * step : value;
    }

    private static double NormalizeVanillaDegrees(double degrees)
    {
        while (degrees > 180) degrees -= 360;
        while (degrees < -180) degrees += 360;
        return degrees;
    }

    private static double NormalizeVanillaRadians(double radians)
    {
        const double twoPi = Math.PI * 2.0;
        while (radians > Math.PI) radians -= twoPi;
        while (radians < -Math.PI) radians += twoPi;
        return radians;
    }

    private static double GetVanillaViewportGizmoMouseAngle(NVector2 center, NVector2 mouse)
    {
        NVector2 radial = mouse - center;
        return Math.Atan2(radial.Y, radial.X);
    }

    private static float NormalizeRadians(float radians)
    {
        const float twoPi = (float)(Math.PI * 2.0);
        while (radians > Math.PI) radians -= twoPi;
        while (radians < -Math.PI) radians += twoPi;
        return radians;
    }

    private static NVector2 NormalizeOrDefault(NVector2 vector, NVector2 fallback)
    {
        float length = vector.Length();
        return length <= 0.0001f ? fallback : vector / length;
    }

    private static NVector3 NormalizeOrDefault(NVector3 vector, NVector3 fallback)
    {
        float length = vector.Length();
        return length <= 0.0001f ? fallback : vector / length;
    }

    private static float DistanceToSegment(NVector2 point, NVector2 start, NVector2 end)
    {
        NVector2 segment = end - start;
        float lengthSquared = segment.LengthSquared();
        if (lengthSquared <= 0.0001f) return (point - start).Length();
        float t = Math.Clamp(NVector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        return (point - (start + segment * t)).Length();
    }

    private static float DistanceToPolyline(NVector2 point, NVector2[] points)
    {
        if (points.Length == 0) return float.MaxValue;
        float best = float.MaxValue;
        for (int i = 1; i < points.Length; i++)
        {
            best = Math.Min(best, DistanceToSegment(point, points[i - 1], points[i]));
        }

        return best;
    }

    private VanillaAnimationViewport3DRenderer EnsureVanillaPreviewRenderer()
    {
        return _vanillaPreviewRenderer ??= new VanillaAnimationViewport3DRenderer(_api);
    }

    private VanillaAnimationPreviewScene? EnsureVanillaPreviewScene(VanillaBrowserRow row)
    {
        if (_vanillaPreviewScene == null || _vanillaPreviewScene.Key != row.Key)
        {
            BuildVanillaPreviewScene(row, rebuildMesh: true);
        }

        return _vanillaPreviewScene;
    }

    private void BuildVanillaPreviewScene(VanillaBrowserRow row, bool rebuildMesh)
    {
        try
        {
            if (_vanillaPreviewScene == null || _vanillaPreviewScene.Key != row.Key || rebuildMesh)
            {
                DisposeVanillaPreviewScene();
                _vanillaPreviewScene = VanillaAnimationPreviewScene.Create(_api, row);
            }
            else
            {
                _vanillaPreviewScene.ReloadAnimator(row);
            }

            if (_vanillaPreviewScene != null)
            {
                _vanillaPreviewScene.Scrub(Math.Clamp(_vanillaPreviewScene.CurrentFrame, 0, Math.Max(0, _vanillaPreviewScene.QuantityFrames - 1)));
                _vanillaStatus = _vanillaPreviewScene.Status;
            }
        }
        catch (Exception exception)
        {
            DisposeVanillaPreviewScene();
            _vanillaStatus = $"Preview failed for {row.Label}: {exception.Message}";
            LoggerUtil.Warn(_api, this, $"Vanilla preview failed for '{row.Label}' ({row.Key}): {exception}");
        }
    }

    private void RefreshVanillaPreviewAfterEdit(VanillaBrowserRow row)
    {
        if (_vanillaPreviewScene?.Key != row.Key) return;
        BuildVanillaPreviewScene(row, rebuildMesh: false);
    }

    private void DisposeVanillaPreviewScene()
    {
        _vanillaPreviewScene?.Dispose();
        _vanillaPreviewScene = null;
        _vanillaPreviewRenderer?.SetVisible(false);
    }

    private static VanillaAnimation? GetVanillaAnimation(VanillaBrowserRow row)
    {
        return row.ShapeAnimation?.Animation ?? row.MetadataEntry?.ResolveCurrentShape()?.Animation;
    }

    private void DrawVanillaInspector(VanillaBrowserRow? row)
    {
        if (row == null)
        {
            ImGui.TextDisabled("Select a vanilla animation.");
            return;
        }

        if (row.ShapeAnimation != null)
        {
            DrawVanillaHistoryControls(row.Document, row);
            DrawVanillaLiveControls(row.Document, row);
            VanillaAnimationDocumentSnapshot? before = _vanillaHistory.HasPendingEdit(row.Document)
                ? null
                : _vanillaHistory.Capture(row.Document, $"Edit {row.ShapeAnimation.Animation.Code ?? row.ShapeAnimation.Animation.Name ?? "animation"}", row);
            DrawVanillaShapeAnimationInspector(row, row.ShapeAnimation);
            TrackVanillaDocumentChanges(row.Document, before, row);
        }
        else if (row.MetadataEntry != null)
        {
            DrawVanillaHistoryControls(row.Document, row);
            DrawVanillaLiveControls(row.Document, row);
            VanillaAnimationDocumentSnapshot? before = _vanillaHistory.HasPendingEdit(row.Document)
                ? null
                : _vanillaHistory.Capture(row.Document, $"Edit metadata {row.MetadataEntry.Metadata.Code ?? row.MetadataEntry.Metadata.Animation ?? "animation"}", row);
            DrawVanillaMetadataInspector(row, row.MetadataEntry);
            TrackVanillaDocumentChanges(row.Document, before, row);

            VanillaShapeAnimationEntry? linked = row.MetadataEntry.ResolveCurrentShape();
            if (linked != null)
            {
                ImGui.SeparatorText("Linked shape animation");
                VanillaBrowserRow linkedRow = row with { ShapeAnimation = linked };
                if (!ReferenceEquals(linked.Document, row.Document))
                {
                    DrawVanillaHistoryControls(linked.Document, linkedRow);
                    DrawVanillaLiveControls(linked.Document, linkedRow);
                }

                VanillaAnimationDocumentSnapshot? linkedBefore = _vanillaHistory.HasPendingEdit(linked.Document)
                    ? null
                    : _vanillaHistory.Capture(linked.Document, $"Edit {linked.Animation.Code ?? linked.Animation.Name ?? "animation"}", linkedRow);
                DrawVanillaShapeAnimationInspector(linkedRow, linked);
                TrackVanillaDocumentChanges(linked.Document, linkedBefore, linkedRow);
            }
        }
    }

    private void DrawVanillaHistoryControls(VanillaAnimationDocument document, VanillaBrowserRow row)
    {
        ImGui.SeparatorText(document.Kind == VanillaDocumentKind.Shape ? "Shape history" : "Metadata history");

        bool canUndo = _vanillaHistory.UndoCount(document) > 0;
        bool canRedo = _vanillaHistory.RedoCount(document) > 0;

        if (!canUndo) ImGui.BeginDisabled();
        if (ImGui.Button($"Undo##vanilla-history-{document.HistoryKey}"))
        {
            CommitPendingVanillaHistory(document);
            RestoreVanillaHistory(document, row, undo: true);
        }
        if (!canUndo) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!canRedo) ImGui.BeginDisabled();
        if (ImGui.Button($"Redo##vanilla-history-{document.HistoryKey}"))
        {
            CommitPendingVanillaHistory(document);
            RestoreVanillaHistory(document, row, undo: false);
        }
        if (!canRedo) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button($"Clear history##vanilla-history-{document.HistoryKey}"))
        {
            _vanillaHistory.Clear(document);
            _vanillaStatus = "Vanilla edit history cleared.";
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"Undo: {_vanillaHistory.UndoCount(document)}  Redo: {_vanillaHistory.RedoCount(document)}");
    }

    private void HandleVanillaHistoryShortcuts(VanillaBrowserRow? row)
    {
        if (row == null) return;

        ImGuiIOPtr io = ImGui.GetIO();
        if (io.WantTextInput || !io.KeyCtrl) return;

        VanillaAnimationDocument document = GetVanillaHistoryShortcutDocument(row);
        if (ImGui.IsKeyPressed(ImGuiKey.Z))
        {
            CommitPendingVanillaHistory(document);
            RestoreVanillaHistory(document, row, undo: true);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Y))
        {
            CommitPendingVanillaHistory(document);
            RestoreVanillaHistory(document, row, undo: false);
        }
    }

    private VanillaAnimationDocument GetVanillaHistoryShortcutDocument(VanillaBrowserRow row)
    {
        if (!string.IsNullOrWhiteSpace(_vanillaLastEditedDocumentKey))
        {
            VanillaAnimationDocument? lastEdited = FindVanillaDocument(_vanillaLastEditedDocumentKey);
            if (lastEdited != null) return lastEdited;
        }

        return row.Document;
    }

    private VanillaAnimationDocument? FindVanillaDocument(string historyKey)
    {
        return _vanillaIndex.Documents.FirstOrDefault(document => string.Equals(document.HistoryKey, historyKey, StringComparison.Ordinal));
    }

    private void TrackVanillaDocumentChanges(VanillaAnimationDocument document, VanillaAnimationDocumentSnapshot? before, VanillaBrowserRow row)
    {
        bool anyItemActive = ImGui.IsAnyItemActive();

        if (_vanillaHistory.HasPendingEdit(document))
        {
            if (!anyItemActive)
            {
                CommitPendingVanillaHistory(document);
            }
            return;
        }

        if (before == null || before.Matches(document)) return;

        if (anyItemActive)
        {
            _vanillaHistory.BeginEdit(document, before);
        }
        else if (_vanillaHistory.RecordSnapshot(document, before))
        {
            OnVanillaDocumentChanged(document, row);
        }
    }

    private void CommitPendingVanillaHistory()
    {
        if (!_vanillaHistory.TryGetPendingDocumentKey(out string? historyKey)) return;
        if (string.IsNullOrWhiteSpace(historyKey)) return;
        VanillaAnimationDocument? document = FindVanillaDocument(historyKey);
        if (document != null)
        {
            CommitPendingVanillaHistory(document);
        }
        else
        {
            _vanillaHistory.CancelPendingEdit();
        }
    }

    private void CommitPendingVanillaHistory(VanillaAnimationDocument document)
    {
        if (_vanillaHistory.CommitEdit(document))
        {
            document.UpdateDirtyState();
            _vanillaLastEditedDocumentKey = document.HistoryKey;
            InvalidateVanillaBrowserFilter();
            AutoApplyVanillaDocument(document);
        }
    }

    private void RestoreVanillaHistory(VanillaAnimationDocument document, VanillaBrowserRow row, bool undo)
    {
        bool restored = undo
            ? _vanillaHistory.Undo(document, out string status)
            : _vanillaHistory.Redo(document, out status);

        _vanillaStatus = status;
        if (!restored) return;

        OnVanillaDocumentChanged(document, row);
        ClampVanillaSelection(row);
        ClearVanillaViewportGizmoDrag();
        _vanillaTimelineDragKeyframe = -1;
        RefreshVanillaPreviewAfterEdit(row);
    }

    private void OnVanillaDocumentChanged(VanillaAnimationDocument document, VanillaBrowserRow row)
    {
        document.UpdateDirtyState();
        _vanillaLastEditedDocumentKey = document.HistoryKey;
        _vanillaIndex.RebuildLinks();
        InvalidateVanillaBrowserRows();
        ClampVanillaSelection(row);
        AutoApplyVanillaDocument(document);
    }

    private void ClampVanillaSelection(VanillaBrowserRow row)
    {
        VanillaAnimation? animation = GetVanillaAnimation(row);
        if (animation?.KeyFrames == null || animation.KeyFrames.Length == 0)
        {
            _vanillaSelection.KeyFrameIndex = 0;
            _vanillaSelection.ElementName = "";
            return;
        }

        _vanillaSelection.KeyFrameIndex = Math.Clamp(_vanillaSelection.KeyFrameIndex, 0, animation.KeyFrames.Length - 1);
        AnimationKeyFrame keyFrame = animation.KeyFrames[_vanillaSelection.KeyFrameIndex];
        if (keyFrame.Elements == null || keyFrame.Elements.Count == 0)
        {
            _vanillaSelection.ElementName = "";
            return;
        }

        if (string.IsNullOrWhiteSpace(_vanillaSelection.ElementName) || !keyFrame.Elements.ContainsKey(_vanillaSelection.ElementName))
        {
            _vanillaSelection.ElementName = keyFrame.Elements.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).First();
        }
    }

    private void DrawVanillaShapeAnimationInspector(VanillaBrowserRow row, VanillaShapeAnimationEntry entry)
    {
        VanillaAnimation animation = entry.Animation;
        bool changed = false;

        ImGui.SeparatorText("Animation");
        string code = animation.Code ?? "";
        if (ImGui.InputText("Code##vanilla-animation", ref code, 200))
        {
            animation.Code = code;
            changed = true;
        }

        string name = animation.Name ?? "";
        if (ImGui.InputText("Name##vanilla-animation", ref name, 200))
        {
            animation.Name = name;
            changed = true;
        }

        int quantityFrames = animation.QuantityFrames;
        if (ImGui.InputInt("Quantity frames##vanilla-animation", ref quantityFrames))
        {
            animation.QuantityFrames = Math.Max(1, quantityFrames);
            _vanillaSelection.LoopEndFrame = Math.Min(_vanillaSelection.LoopEndFrame, animation.QuantityFrames - 1);
            changed = true;
        }

        int version = animation.Version;
        if (ImGui.InputInt("Version##vanilla-animation", ref version))
        {
            animation.Version = version;
            changed = true;
        }

        bool ease = animation.EaseAnimationSpeed;
        if (ImGui.Checkbox("Ease animation speed##vanilla-animation", ref ease))
        {
            animation.EaseAnimationSpeed = ease;
            changed = true;
        }

        changed |= DrawEnumCombo("On activity stopped##vanilla-animation", ref animation.OnActivityStopped);
        changed |= DrawEnumCombo("On animation end##vanilla-animation", ref animation.OnAnimationEnd);

        DrawVanillaKeyframeEditor(row, entry);

        if (changed)
        {
            MarkVanillaDirty(entry.Document);
            RefreshVanillaPreviewAfterEdit(row);
        }
    }

    private void DrawVanillaKeyframeEditor(VanillaBrowserRow row, VanillaShapeAnimationEntry entry)
    {
        VanillaAnimation animation = entry.Animation;
        animation.KeyFrames ??= [];

        ImGui.SeparatorText("Keyframes");
        if (ImGui.Button("Add keyframe##vanilla-keyframe"))
        {
            int frame = animation.KeyFrames.Length == 0 ? 0 : Math.Min(animation.QuantityFrames - 1, animation.KeyFrames.Max(keyFrame => keyFrame.Frame) + 1);
            animation.KeyFrames = animation.KeyFrames.Append(new AnimationKeyFrame { Frame = frame, Elements = new(StringComparer.OrdinalIgnoreCase) }).ToArray();
            _vanillaSelection.KeyFrameIndex = animation.KeyFrames.Length - 1;
            MarkVanillaDirty(entry.Document);
            RefreshVanillaPreviewAfterEdit(row);
        }

        ImGui.SameLine();
        bool hasKeyframe = animation.KeyFrames.Length > 0;
        if (!hasKeyframe) ImGui.BeginDisabled();
        if (ImGui.Button("Clone##vanilla-keyframe"))
        {
            AnimationKeyFrame clone = CloneKeyFrame(animation.KeyFrames[_vanillaSelection.KeyFrameIndex]);
            clone.Frame = Math.Min(animation.QuantityFrames - 1, clone.Frame + 1);
            animation.KeyFrames = animation.KeyFrames.Append(clone).ToArray();
            _vanillaSelection.KeyFrameIndex = animation.KeyFrames.Length - 1;
            MarkVanillaDirty(entry.Document);
            RefreshVanillaPreviewAfterEdit(row);
        }

        ImGui.SameLine();
        if (ImGui.Button("Delete##vanilla-keyframe"))
        {
            List<AnimationKeyFrame> keyFrames = animation.KeyFrames.ToList();
            keyFrames.RemoveAt(_vanillaSelection.KeyFrameIndex);
            animation.KeyFrames = keyFrames.ToArray();
            _vanillaSelection.KeyFrameIndex = Math.Clamp(_vanillaSelection.KeyFrameIndex, 0, Math.Max(0, animation.KeyFrames.Length - 1));
            MarkVanillaDirty(entry.Document);
            RefreshVanillaPreviewAfterEdit(row);
        }
        if (!hasKeyframe) ImGui.EndDisabled();

        if (ImGui.Button("Sort by frame##vanilla-keyframe"))
        {
            animation.KeyFrames = animation.KeyFrames.OrderBy(keyFrame => keyFrame.Frame).ToArray();
            MarkVanillaDirty(entry.Document);
            RefreshVanillaPreviewAfterEdit(row);
        }

        if (animation.KeyFrames.Length == 0)
        {
            ImGui.TextDisabled("No keyframes.");
            return;
        }

        _vanillaSelection.KeyFrameIndex = Math.Clamp(_vanillaSelection.KeyFrameIndex, 0, animation.KeyFrames.Length - 1);
        string[] labels = animation.KeyFrames.Select((keyFrame, index) => $"{index}: frame {keyFrame.Frame} ({keyFrame.Elements?.Count ?? 0} elements)").ToArray();
        ImGui.ListBox("Keyframe##vanilla-keyframes", ref _vanillaSelection.KeyFrameIndex, labels, labels.Length);

        AnimationKeyFrame selected = animation.KeyFrames[_vanillaSelection.KeyFrameIndex];
        int frameNumber = selected.Frame;
        if (ImGui.InputInt("Frame number##vanilla-keyframe", ref frameNumber))
        {
            selected.Frame = Math.Clamp(frameNumber, 0, Math.Max(0, animation.QuantityFrames - 1));
            MarkVanillaDirty(entry.Document);
            RefreshVanillaPreviewAfterEdit(row);
        }

        DrawVanillaElementEditor(row, entry.Document, selected);
    }

    private void DrawVanillaElementEditor(VanillaBrowserRow row, VanillaAnimationDocument document, AnimationKeyFrame keyFrame)
    {
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);

        ImGui.SeparatorText("Element");
        string[] knownElements = GetShapeElementNames(document).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (knownElements.Length > 0)
        {
            _vanillaSelection.AddElementIndex = Math.Clamp(_vanillaSelection.AddElementIndex, 0, knownElements.Length - 1);
            ImGui.Combo("Known element##vanilla-add-element", ref _vanillaSelection.AddElementIndex, knownElements, knownElements.Length);
            if (ImGui.Button("Add selected element##vanilla-element"))
            {
                string name = knownElements[_vanillaSelection.AddElementIndex];
                keyFrame.Elements.TryAdd(name, new AnimationKeyFrameElement());
                _vanillaSelection.ElementName = name;
                MarkVanillaDirty(document);
                RefreshVanillaPreviewAfterEdit(row);
            }
        }

        string[] elementNames = keyFrame.Elements.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (elementNames.Length == 0)
        {
            ImGui.TextDisabled("No animated elements in this keyframe.");
            return;
        }

        int selectedElementIndex = Math.Max(0, Array.FindIndex(elementNames, name => string.Equals(name, _vanillaSelection.ElementName, StringComparison.OrdinalIgnoreCase)));
        if (selectedElementIndex >= elementNames.Length) selectedElementIndex = 0;
        if (ImGui.ListBox("Elements##vanilla-elements", ref selectedElementIndex, elementNames, elementNames.Length))
        {
            _vanillaSelection.ElementName = elementNames[selectedElementIndex];
        }

        if (string.IsNullOrWhiteSpace(_vanillaSelection.ElementName) || !keyFrame.Elements.TryGetValue(_vanillaSelection.ElementName, out AnimationKeyFrameElement? element))
        {
            _vanillaSelection.ElementName = elementNames[selectedElementIndex];
            element = keyFrame.Elements[_vanillaSelection.ElementName];
        }

        if (ImGui.Button("Remove element##vanilla-element"))
        {
            keyFrame.Elements.Remove(_vanillaSelection.ElementName);
            _vanillaSelection.ElementName = "";
            MarkVanillaDirty(document);
            RefreshVanillaPreviewAfterEdit(row);
            return;
        }

        DrawVanillaElementGizmoControls();

        bool changed = false;
        changed |= DrawNullableDouble("Offset X", ref element.OffsetX);
        changed |= DrawNullableDouble("Offset Y", ref element.OffsetY);
        changed |= DrawNullableDouble("Offset Z", ref element.OffsetZ);

        ImGui.SetNextItemWidth(110);
        if (ImGui.InputFloat("Rotation step degrees##vanilla-rotation-step", ref _vanillaRotationStepDegrees, 0, 0, "%.3f"))
        {
            _vanillaRotationStepDegrees = Math.Clamp(Math.Abs(_vanillaRotationStepDegrees), 0.001f, 360f);
        }

        changed |= DrawNullableRotationDouble("Rotation X", ref element.RotationX, _vanillaRotationStepDegrees);
        changed |= DrawNullableRotationDouble("Rotation Y", ref element.RotationY, _vanillaRotationStepDegrees);
        changed |= DrawNullableRotationDouble("Rotation Z", ref element.RotationZ, _vanillaRotationStepDegrees);
        changed |= DrawNullableDouble("Stretch X", ref element.StretchX);
        changed |= DrawNullableDouble("Stretch Y", ref element.StretchY);
        changed |= DrawNullableDouble("Stretch Z", ref element.StretchZ);
        changed |= DrawNullableDouble("Origin X", ref element.OriginX);
        changed |= DrawNullableDouble("Origin Y", ref element.OriginY);
        changed |= DrawNullableDouble("Origin Z", ref element.OriginZ);

        changed |= ImGui.Checkbox("Shortest rotation X##vanilla-element", ref element.RotShortestDistanceX);
        changed |= ImGui.Checkbox("Shortest rotation Y##vanilla-element", ref element.RotShortestDistanceY);
        changed |= ImGui.Checkbox("Shortest rotation Z##vanilla-element", ref element.RotShortestDistanceZ);

        if (changed)
        {
            CompleteVanillaElementTransformGroups(element);
            MarkVanillaDirty(document);
            RefreshVanillaPreviewAfterEdit(row);
        }
    }

    private void DrawVanillaMetadataInspector(VanillaBrowserRow row, VanillaAnimationMetaEntry entry)
    {
        AnimationMetaData metadata = entry.Metadata;
        bool changed = false;

        ImGui.SeparatorText("Metadata");
        if (entry.ResolveCurrentShape() == null)
        {
            ImGui.TextColored(new NVector4(1f, 0.42f, 0.32f, 1f), $"Unresolved animation reference: {metadata.Animation}");
        }

        string code = metadata.Code ?? "";
        if (ImGui.InputText("Code##vanilla-meta", ref code, 200))
        {
            metadata.Code = code;
            changed = true;
        }

        string animation = metadata.Animation ?? "";
        if (ImGui.InputText("Animation##vanilla-meta", ref animation, 200))
        {
            metadata.Animation = animation;
            entry.LinkedShape = _vanillaIndex.ResolveShapeAnimation(animation);
            changed = true;
        }

        changed |= DrawFloat("Weight##vanilla-meta", ref metadata.Weight, 0.01f);
        changed |= DrawFloat("Animation speed##vanilla-meta", ref metadata.AnimationSpeed, 0.01f);
        changed |= ImGui.Checkbox("Mul with walk speed##vanilla-meta", ref metadata.MulWithWalkSpeed);
        changed |= DrawFloat("Weight cap factor##vanilla-meta", ref metadata.WeightCapFactor, 0.01f);
        changed |= DrawFloat("Ease in speed##vanilla-meta", ref metadata.EaseInSpeed, 0.05f);
        changed |= DrawFloat("Ease out speed##vanilla-meta", ref metadata.EaseOutSpeed, 0.05f);
        changed |= DrawEnumCombo("Blend mode##vanilla-meta", ref metadata.BlendMode);
        changed |= ImGui.Checkbox("Supress default animation##vanilla-meta", ref metadata.SupressDefaultAnimation);
        changed |= DrawFloat("Hold eye pos after easein##vanilla-meta", ref metadata.HoldEyePosAfterEasein, 0.1f);
        changed |= ImGui.Checkbox("Client side##vanilla-meta", ref metadata.ClientSide);
        changed |= ImGui.Checkbox("With FP variant##vanilla-meta", ref metadata.WithFpVariant);

        bool adjustCollisionBox = metadata.AdjustCollisionBox;
        if (ImGui.Checkbox("Adjust collision box##vanilla-meta", ref adjustCollisionBox))
        {
            metadata.AdjustCollisionBox = adjustCollisionBox;
            changed = true;
        }

        changed |= DrawMetadataDictionaries(entry);
        changed |= DrawMetadataSounds(entry);

        if (changed)
        {
            MarkVanillaDirty(entry.Document);
            RefreshVanillaPreviewAfterEdit(row);
        }
    }

    private bool DrawMetadataDictionaries(VanillaAnimationMetaEntry entry)
    {
        bool changed = false;
        AnimationMetaData metadata = entry.Metadata;
        metadata.ElementWeight ??= new(StringComparer.OrdinalIgnoreCase);
        metadata.ElementBlendMode ??= new(StringComparer.OrdinalIgnoreCase);

        if (ImGui.CollapsingHeader("Element weights##vanilla-meta"))
        {
            foreach (string key in metadata.ElementWeight.Keys.ToArray())
            {
                float value = metadata.ElementWeight[key];
                ImGui.SetNextItemWidth(120);
                if (ImGui.DragFloat($"##weight-{key}", ref value, 0.01f, 0f, 10f))
                {
                    metadata.ElementWeight[key] = value;
                    changed = true;
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(key);
                ImGui.SameLine();
                if (ImGui.Button($"Remove##weight-{key}"))
                {
                    metadata.ElementWeight.Remove(key);
                    changed = true;
                }
            }

            ImGui.InputTextWithHint("##new-weight-key", "element name", ref _vanillaSelection.NewElementWeightKey, 120);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            ImGui.DragFloat("##new-weight-value", ref _vanillaSelection.NewElementWeightValue, 0.01f, 0f, 10f);
            ImGui.SameLine();
            if (ImGui.Button("Add weight##vanilla-meta") && !string.IsNullOrWhiteSpace(_vanillaSelection.NewElementWeightKey))
            {
                metadata.ElementWeight[_vanillaSelection.NewElementWeightKey.Trim()] = _vanillaSelection.NewElementWeightValue;
                _vanillaSelection.NewElementWeightKey = "";
                changed = true;
            }
        }

        if (ImGui.CollapsingHeader("Element blend modes##vanilla-meta"))
        {
            foreach (string key in metadata.ElementBlendMode.Keys.ToArray())
            {
                EnumAnimationBlendMode mode = metadata.ElementBlendMode[key];
                if (DrawEnumCombo($"##blend-{key}", ref mode))
                {
                    metadata.ElementBlendMode[key] = mode;
                    changed = true;
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(key);
                ImGui.SameLine();
                if (ImGui.Button($"Remove##blend-{key}"))
                {
                    metadata.ElementBlendMode.Remove(key);
                    changed = true;
                }
            }

            ImGui.InputTextWithHint("##new-blend-key", "element name", ref _vanillaSelection.NewElementBlendKey, 120);
            ImGui.SameLine();
            DrawEnumCombo("##new-blend-mode", ref _vanillaSelection.NewElementBlendMode);
            ImGui.SameLine();
            if (ImGui.Button("Add blend##vanilla-meta") && !string.IsNullOrWhiteSpace(_vanillaSelection.NewElementBlendKey))
            {
                metadata.ElementBlendMode[_vanillaSelection.NewElementBlendKey.Trim()] = _vanillaSelection.NewElementBlendMode;
                _vanillaSelection.NewElementBlendKey = "";
                changed = true;
            }
        }

        return changed;
    }

    private bool DrawMetadataSounds(VanillaAnimationMetaEntry entry)
    {
        bool changed = false;
        AnimationMetaData metadata = entry.Metadata;

        if (!ImGui.CollapsingHeader("Animation sounds##vanilla-meta")) return false;

        metadata.AnimationSounds ??= [];
        if (ImGui.Button("Add sound##vanilla-meta"))
        {
            metadata.AnimationSounds = metadata.AnimationSounds.Append(new AnimationSound()).ToArray();
            changed = true;
        }

        for (int index = 0; index < metadata.AnimationSounds.Length; index++)
        {
            AnimationSound sound = metadata.AnimationSounds[index];
            if (!ImGui.TreeNode($"Sound {index}##vanilla-sound-{index}")) continue;

            int frame = sound.Frame;
            if (ImGui.InputInt($"Frame##vanilla-sound-{index}", ref frame))
            {
                sound.Frame = Math.Max(0, frame);
                changed = true;
            }

            string location = sound.Attributes.Location?.ToString() ?? "";
            if (ImGui.InputText($"Location##vanilla-sound-{index}", ref location, 300))
            {
                sound.Attributes.Location = string.IsNullOrWhiteSpace(location) ? null : AssetLocation.Create(location);
                changed = true;
            }

            changed |= DrawFloat($"Chance##vanilla-sound-{index}", ref sound.Chance, 0.01f);
            changed |= ImGui.Checkbox($"Looping##vanilla-sound-{index}", ref sound.Looping);

            float range = sound.Attributes.Range;
            if (DrawFloat($"Range##vanilla-sound-{index}", ref range, 0.1f))
            {
                sound.Attributes.Range = range;
                changed = true;
            }

            if (ImGui.Button($"Remove sound##vanilla-sound-{index}"))
            {
                List<AnimationSound> sounds = metadata.AnimationSounds.ToList();
                sounds.RemoveAt(index);
                metadata.AnimationSounds = sounds.ToArray();
                changed = true;
                ImGui.TreePop();
                break;
            }

            ImGui.TreePop();
        }

        return changed;
    }

    private void DrawVanillaTimeline(VanillaBrowserRow? row)
    {
        ImGui.SeparatorText("Timeline");
        if (row == null)
        {
            ImGui.TextDisabled("Select a vanilla animation.");
            return;
        }

        VanillaAnimation? animation = GetVanillaAnimation(row);
        if (animation == null)
        {
            ImGui.TextDisabled("No linked animation.");
            return;
        }

        AnimationKeyFrame[] keyFrames = animation.KeyFrames ?? [];
        AnimationSound[] sounds = row.MetadataEntry?.Metadata.AnimationSounds ?? [];
        int quantity = Math.Max(1, animation.QuantityFrames);
        VanillaAnimationDocument historyDocument = (row.ShapeAnimation ?? row.MetadataEntry?.ResolveCurrentShape())?.Document ?? row.Document;
        VanillaAnimationDocumentSnapshot? before = _vanillaHistory.HasPendingEdit(historyDocument)
            ? null
            : _vanillaHistory.Capture(historyDocument, "Edit timeline", row);

        ImGui.TextDisabled("Click timeline to scrub. Click markers to select keyframes; drag markers to retime.");

        float width = Math.Max(420f, ImGui.GetContentRegionAvail().X);
        float height = 132f;
        ImGui.InvisibleButton($"##vanilla-timeline-{row.Key}", new NVector2(width, height));
        NVector2 min = ImGui.GetItemRectMin();
        NVector2 max = ImGui.GetItemRectMax();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0.07f, 0.065f, 0.055f, 0.86f));
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 1f));
        uint marker = ImGui.ColorConvertFloat4ToU32(new NVector4(0.95f, 0.78f, 0.28f, 1f));
        uint selected = ImGui.ColorConvertFloat4ToU32(new NVector4(0.35f, 0.70f, 1.0f, 1f));
        uint soundColor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.95f, 0.42f, 0.35f, 1f));
        uint loopColor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.35f, 0.95f, 0.55f, 1f));
        uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.86f, 0.82f, 0.72f, 1f));

        drawList.AddRectFilled(min, max, background, 4f);
        drawList.AddRect(min, max, border, 4f);

        float labelWidth = 84f;
        float trackStart = min.X + labelWidth;
        float trackEnd = max.X - 12f;
        float trackWidth = Math.Max(1, trackEnd - trackStart);
        float FrameToX(float frame) => trackStart + Math.Clamp(frame / Math.Max(1, quantity - 1), 0f, 1f) * trackWidth;
        int XToFrame(float x) => (int)Math.Round(Math.Clamp((x - trackStart) / trackWidth, 0f, 1f) * Math.Max(0, quantity - 1));

        float keyY = min.Y + 48f;
        float soundY = min.Y + 86f;
        drawList.AddText(new NVector2(min.X + 10f, keyY - 8f), text, "Keyframes");
        drawList.AddLine(new NVector2(trackStart, keyY), new NVector2(trackEnd, keyY), border, 1f);
        drawList.AddText(new NVector2(min.X + 10f, soundY - 8f), text, "Sounds");
        drawList.AddLine(new NVector2(trackStart, soundY), new NVector2(trackEnd, soundY), border, 1f);

        int maxFrame = Math.Max(0, quantity - 1);
        int loopStart = Math.Clamp(_vanillaSelection.LoopStartFrame, 0, maxFrame);
        int loopEnd = Math.Clamp(_vanillaSelection.LoopEndFrame, 0, maxFrame);
        drawList.AddRectFilled(new NVector2(FrameToX(loopStart), keyY - 16), new NVector2(FrameToX(loopEnd), soundY + 16), loopColor & 0x44FFFFFF, 2f);

        for (int index = 0; index < keyFrames.Length; index++)
        {
            float x = FrameToX(keyFrames[index].Frame);
            uint color = index == _vanillaSelection.KeyFrameIndex ? selected : marker;
            drawList.AddCircleFilled(new NVector2(x, keyY), 6f, color);
            drawList.AddText(new NVector2(x - 5f, keyY + 10f), text, index.ToString());
        }

        for (int index = 0; index < sounds.Length; index++)
        {
            float x = FrameToX(sounds[index].Frame);
            drawList.AddRectFilled(new NVector2(x - 4, soundY - 8), new NVector2(x + 4, soundY + 8), soundColor, 2f);
        }

        float currentFrame = _vanillaPreviewScene?.CurrentFrame ?? 0;
        float playX = FrameToX(currentFrame);
        drawList.AddLine(new NVector2(playX, min.Y + 12f), new NVector2(playX, max.Y - 12f), selected, 2f);
        drawList.AddText(new NVector2(min.X + 10f, min.Y + 10f), text, $"Frame {currentFrame:0.0} / {maxFrame}");

        NVector2 mouse = ImGui.GetIO().MousePos;
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            int markerIndex = FindVanillaTimelineMarker(keyFrames, mouse, keyY, FrameToX);
            if (markerIndex >= 0)
            {
                _vanillaSelection.KeyFrameIndex = markerIndex;
                _vanillaTimelineDragKeyframe = markerIndex;
                _vanillaPreviewScene?.Scrub(keyFrames[markerIndex].Frame);
            }
            else
            {
                _vanillaPreviewScene?.Scrub(XToFrame(mouse.X));
                _vanillaTimelineDragKeyframe = -1;
            }
        }

        if (_vanillaTimelineDragKeyframe >= 0 && _vanillaTimelineDragKeyframe < keyFrames.Length)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                int newFrame = XToFrame(mouse.X);
                if (keyFrames[_vanillaTimelineDragKeyframe].Frame != newFrame)
                {
                    keyFrames[_vanillaTimelineDragKeyframe].Frame = newFrame;
                    MarkVanillaDirty(historyDocument);
                    RefreshVanillaPreviewAfterEdit(row);
                    _vanillaPreviewScene?.Scrub(newFrame);
                }
            }
            else
            {
                _vanillaTimelineDragKeyframe = -1;
            }
        }

        TrackVanillaDocumentChanges(historyDocument, before, row);
    }

    private static int FindVanillaTimelineMarker(AnimationKeyFrame[] keyFrames, NVector2 mouse, float y, System.Func<float, float> frameToX)
    {
        for (int index = 0; index < keyFrames.Length; index++)
        {
            float x = frameToX(keyFrames[index].Frame);
            if (Math.Abs(mouse.X - x) <= 8f && Math.Abs(mouse.Y - y) <= 12f)
            {
                return index;
            }
        }

        return -1;
    }

    private void ExportSelectedVanillaDocument()
    {
        CommitPendingVanillaHistory();
        VanillaBrowserRow? row = FindVanillaBrowserRow(_vanillaSelection.RowKey);
        if (row == null)
        {
            _vanillaStatus = "No vanilla animation selected.";
            return;
        }

        _vanillaStatus = _vanillaExportService.Export(row.Document, _vanillaOverwriteExport);
        InvalidateVanillaBrowserFilter();
    }

    private void ExportDirtyVanillaDocuments()
    {
        CommitPendingVanillaHistory();
        List<VanillaAnimationDocument> dirty = _vanillaIndex.Documents.Where(document => document.Dirty).ToList();
        if (dirty.Count == 0)
        {
            _vanillaStatus = "No dirty vanilla documents to export.";
            return;
        }

        _vanillaStatus = string.Join(Environment.NewLine, dirty.Select(document => _vanillaExportService.Export(document, _vanillaOverwriteExport)));
        InvalidateVanillaBrowserFilter();
    }

    private void MarkVanillaDirty(VanillaAnimationDocument document)
    {
        document.MarkDirty();
        _vanillaLastEditedDocumentKey = document.HistoryKey;
        _vanillaIndex.RebuildLinks();
        InvalidateVanillaBrowserFilter();
        AutoApplyVanillaDocument(document);
    }

    private static IEnumerable<string> GetShapeElementNames(VanillaAnimationDocument document)
    {
        Shape? shape = document.Shape;
        if (shape?.Elements == null) return [];
        return shape.Elements.SelectMany(GetShapeElementNamesRecursive);
    }

    private static IEnumerable<string> GetShapeElementNamesRecursive(ShapeElement element)
    {
        if (!string.IsNullOrWhiteSpace(element.Name)) yield return element.Name;
        if (element.Children == null) yield break;
        foreach (ShapeElement child in element.Children)
        {
            foreach (string name in GetShapeElementNamesRecursive(child))
            {
                yield return name;
            }
        }
    }

    private static bool DrawFloat(string label, ref float value, float speed)
    {
        ImGui.SetNextItemWidth(160);
        return ImGui.DragFloat(label, ref value, speed);
    }

    private static bool DrawNullableDouble(string label, ref double? value)
    {
        bool enabled = value.HasValue;
        bool changed = false;

        if (enabled)
        {
            float floatValue = (float)value.GetValueOrDefault();
            ImGui.SetNextItemWidth(110);
            if (ImGui.DragFloat($"##{label}-value", ref floatValue, 0.05f))
            {
                value = floatValue;
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.Checkbox($"{label}##{label}-enabled", ref enabled))
            {
                value = enabled ? value : null;
                changed = true;
            }
        }
        else
        {
            if (ImGui.Checkbox($"{label}##{label}-enabled", ref enabled))
            {
                value = 0;
                changed = true;
            }
            ImGui.SameLine();
            ImGui.TextDisabled(label);
        }

        return changed;
    }

    private static bool DrawNullableRotationDouble(string label, ref double? value, float stepDegrees)
    {
        bool enabled = value.HasValue;
        bool changed = false;
        float floatValue = (float)value.GetValueOrDefault();

        if (!enabled) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(90);
        if (ImGui.DragFloat($"##{label}-value", ref floatValue, 0.05f))
        {
            value = floatValue;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button($"-##{label}-step-down", new NVector2(24, 0)))
        {
            value = value.GetValueOrDefault() - stepDegrees;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button($"+##{label}-step-up", new NVector2(24, 0)))
        {
            value = value.GetValueOrDefault() + stepDegrees;
            changed = true;
        }
        if (!enabled) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Checkbox($"{label}##{label}-enabled", ref enabled))
        {
            value = enabled ? 0 : null;
            changed = true;
        }

        return changed;
    }

    private static bool DrawEnumCombo<TEnum>(string label, ref TEnum value) where TEnum : struct, Enum
    {
        string[] names = Enum.GetNames<TEnum>();
        int index = Math.Max(0, Array.IndexOf(names, value.ToString()));
        ImGui.SetNextItemWidth(170);
        if (!ImGui.Combo(label, ref index, names, names.Length)) return false;
        value = Enum.Parse<TEnum>(names[index]);
        return true;
    }

    private static AnimationMetaData CloneAnimationMetaData(AnimationMetaData source)
    {
        return new()
        {
            Code = source.Code,
            Animation = source.Animation,
            Weight = source.Weight,
            ElementWeight = source.ElementWeight != null ? new(source.ElementWeight, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase),
            AnimationSpeed = source.AnimationSpeed,
            MulWithWalkSpeed = source.MulWithWalkSpeed,
            WeightCapFactor = source.WeightCapFactor,
            EaseInSpeed = source.EaseInSpeed,
            EaseOutSpeed = source.EaseOutSpeed,
            TriggeredBy = source.TriggeredBy,
            BlendMode = source.BlendMode,
            ElementBlendMode = source.ElementBlendMode != null ? new(source.ElementBlendMode, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase),
            SupressDefaultAnimation = source.SupressDefaultAnimation,
            HoldEyePosAfterEasein = source.HoldEyePosAfterEasein,
            ClientSide = source.ClientSide,
            WithFpVariant = source.WithFpVariant,
            AnimationSounds = source.AnimationSounds?.Select(sound => sound.Clone()).ToArray() ?? [],
            AdjustCollisionBox = source.AdjustCollisionBox,
            StartFrameOnce = source.StartFrameOnce
        };
    }

    private static void CopyAnimationMetaData(AnimationMetaData target, AnimationMetaData source)
    {
        AnimationMetaData clone = CloneAnimationMetaData(source);
        target.Code = clone.Code;
        target.Animation = clone.Animation;
        target.Weight = clone.Weight;
        target.ElementWeight = clone.ElementWeight;
        target.AnimationSpeed = clone.AnimationSpeed;
        target.MulWithWalkSpeed = clone.MulWithWalkSpeed;
        target.WeightCapFactor = clone.WeightCapFactor;
        target.EaseInSpeed = clone.EaseInSpeed;
        target.EaseOutSpeed = clone.EaseOutSpeed;
        target.TriggeredBy = clone.TriggeredBy;
        target.BlendMode = clone.BlendMode;
        target.ElementBlendMode = clone.ElementBlendMode;
        target.SupressDefaultAnimation = clone.SupressDefaultAnimation;
        target.HoldEyePosAfterEasein = clone.HoldEyePosAfterEasein;
        target.ClientSide = clone.ClientSide;
        target.WithFpVariant = clone.WithFpVariant;
        target.AnimationSounds = clone.AnimationSounds;
        target.AdjustCollisionBox = clone.AdjustCollisionBox;
        target.StartFrameOnce = clone.StartFrameOnce;
    }

    private static VanillaAnimation CloneVanillaAnimation(VanillaAnimation source)
    {
        return new()
        {
            Code = source.Code,
            Name = source.Name,
            QuantityFrames = source.QuantityFrames,
            Version = source.Version,
            EaseAnimationSpeed = source.EaseAnimationSpeed,
            OnActivityStopped = source.OnActivityStopped,
            OnAnimationEnd = source.OnAnimationEnd,
            KeyFrames = source.KeyFrames?.Select(CloneKeyFrame).ToArray() ?? []
        };
    }

    private static void CopyVanillaAnimation(VanillaAnimation target, VanillaAnimation source)
    {
        VanillaAnimation clone = CloneVanillaAnimation(source);
        target.Code = clone.Code;
        target.Name = clone.Name;
        target.QuantityFrames = clone.QuantityFrames;
        target.Version = clone.Version;
        target.EaseAnimationSpeed = clone.EaseAnimationSpeed;
        target.OnActivityStopped = clone.OnActivityStopped;
        target.OnAnimationEnd = clone.OnAnimationEnd;
        target.KeyFrames = clone.KeyFrames;
    }

    private static AnimationKeyFrame CloneKeyFrame(AnimationKeyFrame source)
    {
        Dictionary<string, AnimationKeyFrameElement> elements = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, AnimationKeyFrameElement element) in source.Elements ?? new())
        {
            elements[key] = CloneElement(element);
        }

        return new()
        {
            Frame = source.Frame,
            Elements = elements
        };
    }

    private static AnimationKeyFrameElement CloneElement(AnimationKeyFrameElement source)
    {
        return new()
        {
            OffsetX = source.OffsetX,
            OffsetY = source.OffsetY,
            OffsetZ = source.OffsetZ,
            StretchX = source.StretchX,
            StretchY = source.StretchY,
            StretchZ = source.StretchZ,
            RotationX = source.RotationX,
            RotationY = source.RotationY,
            RotationZ = source.RotationZ,
            OriginX = source.OriginX,
            OriginY = source.OriginY,
            OriginZ = source.OriginZ,
            RotShortestDistanceX = source.RotShortestDistanceX,
            RotShortestDistanceY = source.RotShortestDistanceY,
            RotShortestDistanceZ = source.RotShortestDistanceZ
        };
    }

    private sealed class VanillaAnimationSelection
    {
        public string RowKey = "";
        public int KeyFrameIndex;
        public string ElementName = "";
        public int AddElementIndex;
        public int LoopStartFrame;
        public int LoopEndFrame;
        public string NewElementWeightKey = "";
        public float NewElementWeightValue = 1f;
        public string NewElementBlendKey = "";
        public EnumAnimationBlendMode NewElementBlendMode = EnumAnimationBlendMode.Add;

        public void Clear()
        {
            RowKey = "";
            KeyFrameIndex = 0;
            ElementName = "";
            AddElementIndex = 0;
            LoopStartFrame = 0;
            LoopEndFrame = 0;
        }
    }

    private sealed record VanillaBrowserRow(
        string Key,
        string Label,
        VanillaAnimationDocument Document,
        VanillaShapeAnimationEntry? ShapeAnimation,
        VanillaAnimationMetaEntry? MetadataEntry,
        VanillaBrowserRowKind Kind,
        string SearchText,
        bool IsUnresolved);

    private enum VanillaBrowserRowKind
    {
        Metadata,
        Shape
    }

    private enum VanillaBrowserQuickFilter
    {
        All,
        Metadata,
        Shape,
        Dirty,
        Unresolved
    }

    private enum VanillaPreviewMode
    {
        Orbit,
        FirstPerson,
        ImmersiveFirstPerson
    }

    private sealed record VanillaEntityOption(EntityProperties EntityType, string Label, string FullLabel, string Domain);

    private sealed class VanillaAnimationIndexService
    {
        private readonly List<VanillaAnimationDocument> _documents = [];
        private readonly Dictionary<string, List<VanillaShapeAnimationEntry>> _shapeAnimationsByCode = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<VanillaEntityOption> _entityOptions = [];
        private bool _entityListReady;

        public IReadOnlyList<VanillaAnimationDocument> Documents => _documents;
        public IReadOnlyList<VanillaEntityOption> EntityOptions => _entityOptions;
        public int SelectedEntityIndex { get; private set; } = -1;
        public string? SelectedEntityLabel => SelectedEntityIndex >= 0 && SelectedEntityIndex < _entityOptions.Count ? _entityOptions[SelectedEntityIndex].Label : null;
        public bool HasSelectedEntity => SelectedEntityIndex >= 0 && SelectedEntityIndex < _entityOptions.Count;
        public string Status { get; private set; } = "Select an entity to index its vanilla animations.";

        public void EnsureEntityList(ICoreClientAPI api)
        {
            if (_entityListReady) return;

            _entityOptions.Clear();
            foreach (EntityProperties entityType in api.World.EntityTypes ?? [])
            {
                string? code = entityType.Code?.ToString();
                if (string.IsNullOrWhiteSpace(code)) continue;
                string domain = entityType.Code?.Domain ?? "game";
                _entityOptions.Add(new(entityType, ImGuiLayoutHelper.CompactAssetCode(code), code, domain));
            }

            _entityOptions.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));
            _entityListReady = true;
            Status = $"Loaded {_entityOptions.Count} entity types. Select one to index its animations.";
        }

        public void SelectEntity(ICoreClientAPI api, int index)
        {
            EnsureEntityList(api);
            if (index < 0 || index >= _entityOptions.Count)
            {
                ClearSelection();
                return;
            }

            SelectedEntityIndex = index;
            IndexSelectedEntity(api, _entityOptions[index].EntityType);
        }

        public void ReloadSelectedEntity(ICoreClientAPI api)
        {
            if (!HasSelectedEntity) return;
            IndexSelectedEntity(api, _entityOptions[SelectedEntityIndex].EntityType);
        }

        private void ClearSelection()
        {
            SelectedEntityIndex = -1;
            _documents.Clear();
            _shapeAnimationsByCode.Clear();
            Status = "Select an entity to index its vanilla animations.";
        }

        public VanillaShapeAnimationEntry? ResolveShapeAnimation(string animationCode)
        {
            if (string.IsNullOrWhiteSpace(animationCode)) return null;
            return _shapeAnimationsByCode.TryGetValue(animationCode, out List<VanillaShapeAnimationEntry>? entries)
                ? entries.FirstOrDefault()
                : null;
        }

        public void RebuildLinks()
        {
            _shapeAnimationsByCode.Clear();
            foreach (VanillaAnimationDocument document in _documents)
            {
                foreach (VanillaShapeAnimationEntry entry in document.ShapeAnimations)
                {
                    RegisterShapeAnimation(entry);
                }
            }

            foreach (VanillaAnimationDocument document in _documents)
            {
                foreach (VanillaAnimationMetaEntry entry in document.MetadataEntries)
                {
                    entry.LinkedShape = ResolveShapeAnimation(entry.Metadata.Animation);
                }
            }
        }

        private void IndexSelectedEntity(ICoreClientAPI api, EntityProperties entityType)
        {
            try
            {
                _documents.Clear();
                _shapeAnimationsByCode.Clear();

                AnimationMetaData[]? metadata = entityType.Client?.Animations;
                Shape? shape = entityType.Client?.LoadedShapeForEntity ?? entityType.Client?.LoadedShape;
                string entityCode = entityType.Code?.ToString() ?? $"entity-{entityType.Id}";

                JObject? entitySourceJson = TryLoadJson(api, GetEntityAssetLocation(entityType));
                AssetLocation? shapeAssetLocation = GetShapeAssetLocation(entityType);
                JObject? shapeSourceJson = TryLoadJson(api, shapeAssetLocation);

                VanillaAnimationDocument? shapeDocument = null;
                if (shape?.Animations != null && shape.Animations.Length > 0)
                {
                    shapeDocument = new()
                    {
                        Kind = VanillaDocumentKind.Shape,
                        Domain = shapeAssetLocation?.Domain ?? entityType.Code?.Domain ?? "game",
                        AssetPath = shapeAssetLocation != null ? EnsureJsonPath(shapeAssetLocation.Path) : $"shapes/{entityType.Code?.Path ?? entityCode}.json",
                        DisplayPath = $"{entityCode} shape",
                        EntityCode = entityCode,
                        EntityType = entityType,
                        Shape = shape,
                        SourceJson = shapeSourceJson
                    };

                    for (int index = 0; index < shape.Animations.Length; index++)
                    {
                        VanillaAnimation animation = CloneVanillaAnimation(shape.Animations[index]);
                        if (string.IsNullOrWhiteSpace(animation.Code)) animation.Code = animation.Name;
                        VanillaShapeAnimationEntry entry = new(shapeDocument, index, animation, GetSourceArrayElement(shapeSourceJson, "animations", index));
                        shapeDocument.ShapeAnimations.Add(entry);
                        RegisterShapeAnimation(entry);
                    }

                    _documents.Add(shapeDocument);
                    shapeDocument.MarkClean();
                }

                VanillaAnimationDocument metadataDocument = new()
                {
                    Kind = VanillaDocumentKind.EntityMetadata,
                    Domain = entityType.Code?.Domain ?? "game",
                    AssetPath = GetEntityAssetLocation(entityType)?.Path ?? $"entities/{entityType.Code?.Path ?? entityCode}.json",
                    DisplayPath = entityCode,
                    EntityCode = entityCode,
                    EntityType = entityType,
                    Shape = shape,
                    SourceJson = entitySourceJson
                };

                if (metadata != null)
                {
                    for (int index = 0; index < metadata.Length; index++)
                    {
                        AnimationMetaData editable = CloneAnimationMetaData(metadata[index]);
                        VanillaShapeAnimationEntry? linkedShape = ResolveShapeAnimation(editable.Animation);
                        metadataDocument.MetadataEntries.Add(new(metadataDocument, index, editable, linkedShape, GetNestedArrayElement(entitySourceJson, ["client", "animations"], index)));
                    }
                }

                _documents.Add(metadataDocument);
                metadataDocument.MarkClean();
                RebuildLinks();

                int shapeCount = shapeDocument?.ShapeAnimations.Count ?? 0;
                int metadataCount = metadataDocument.MetadataEntries.Count;
                Status = $"Indexed {entityCode}: {shapeCount} shape animations, {metadataCount} metadata entries.";
            }
            catch (Exception exception)
            {
                _documents.Clear();
                _shapeAnimationsByCode.Clear();
                Status = $"Could not index {entityType.Code}: {exception.Message}";
                LoggerUtil.Warn(api, this, $"Could not index vanilla entity animation '{entityType.Code}': {exception}");
            }
        }

        private void RegisterShapeAnimation(VanillaShapeAnimationEntry entry)
        {
            string? code = entry.Animation.Code;
            if (string.IsNullOrWhiteSpace(code)) return;

            if (!_shapeAnimationsByCode.TryGetValue(code, out List<VanillaShapeAnimationEntry>? entries))
            {
                _shapeAnimationsByCode[code] = entries = [];
            }

            entries.Add(entry);
        }

        private static AssetLocation? GetEntityAssetLocation(EntityProperties entityType)
        {
            return entityType.Code == null ? null : new AssetLocation(entityType.Code.Domain, $"entities/{entityType.Code.Path}.json");
        }

        private static AssetLocation? GetShapeAssetLocation(EntityProperties entityType)
        {
            CompositeShape? shape = entityType.Client?.ShapeForEntity ?? entityType.Client?.Shape;
            return shape?.Base?.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
        }

        private static JObject? TryLoadJson(ICoreClientAPI api, AssetLocation? location)
        {
            if (location == null) return null;
            IAsset? asset = api.Assets.TryGet(location);
            return asset == null ? null : TryParseObject(asset.ToText());
        }
    }

    private sealed class VanillaAnimationDocument
    {
        public VanillaDocumentKind Kind { get; init; }
        public string Domain { get; init; } = "game";
        public string AssetPath { get; init; } = "";
        public string DisplayPath { get; init; } = "";
        public string? EntityCode { get; init; }
        public EntityProperties? EntityType { get; init; }
        public Shape? Shape { get; init; }
        public JObject? SourceJson { get; init; }
        public List<VanillaShapeAnimationEntry> ShapeAnimations { get; } = [];
        public List<VanillaAnimationMetaEntry> MetadataEntries { get; } = [];
        public string HistoryKey => $"{Kind}:{Domain}:{AssetPath}:{EntityCode}";
        public bool Dirty { get; private set; }
        private string _cleanSerialized = "";

        public void MarkClean()
        {
            _cleanSerialized = VanillaAnimationDocumentSerializer.Serialize(this);
            Dirty = false;
        }

        public void MarkDirty()
        {
            Dirty = true;
        }

        public void UpdateDirtyState()
        {
            Dirty = !string.Equals(_cleanSerialized, VanillaAnimationDocumentSerializer.Serialize(this), StringComparison.Ordinal);
        }
    }

    private sealed class VanillaShapeAnimationEntry
    {
        public VanillaShapeAnimationEntry(VanillaAnimationDocument document, int index, VanillaAnimation animation, JToken? sourceToken)
        {
            Document = document;
            Index = index;
            Animation = animation;
            SourceToken = sourceToken;
        }

        public VanillaAnimationDocument Document { get; }
        public int Index { get; }
        public VanillaAnimation Animation { get; }
        public JToken? SourceToken { get; }
    }

    private sealed class VanillaAnimationMetaEntry
    {
        public VanillaAnimationMetaEntry(VanillaAnimationDocument document, int index, AnimationMetaData metadata, VanillaShapeAnimationEntry? linkedShape, JToken? sourceToken)
        {
            Document = document;
            Index = index;
            Metadata = metadata;
            LinkedShape = linkedShape;
            SourceToken = sourceToken;
        }

        public VanillaAnimationDocument Document { get; }
        public int Index { get; }
        public AnimationMetaData Metadata { get; }
        public VanillaShapeAnimationEntry? LinkedShape { get; set; }
        public JToken? SourceToken { get; }

        public VanillaShapeAnimationEntry? ResolveCurrentShape()
        {
            if (LinkedShape != null)
            {
                string linkedCode = LinkedShape.Animation.Code ?? LinkedShape.Animation.Name ?? "";
                if (string.Equals(linkedCode, Metadata.Animation, StringComparison.OrdinalIgnoreCase))
                {
                    return LinkedShape;
                }
            }

            if (Document.Shape?.Animations != null)
            {
                for (int index = 0; index < Document.Shape.Animations.Length; index++)
                {
                    VanillaAnimation animation = Document.Shape.Animations[index];
                    string code = animation.Code ?? animation.Name ?? "";
                    if (string.Equals(code, Metadata.Animation, StringComparison.OrdinalIgnoreCase))
                    {
                        return new VanillaShapeAnimationEntry(Document, index, animation, null);
                    }
                }
            }

            return LinkedShape;
        }
    }

    private enum VanillaDocumentKind
    {
        Shape,
        EntityMetadata
    }

    private sealed class VanillaAnimationPreviewScene : IDisposable
    {
        private readonly ICoreClientAPI _api;
        private readonly Dictionary<string, AnimationMetaData> _activeAnimationsByAnimCode = new(StringComparer.OrdinalIgnoreCase);
        private Shape _shape;
        private AnimationMetaData _metadata;
        private VanillaAnimation _animation;
        private string _activeAnimationCode;
        private ClientAnimator _animator;
        private readonly MultiTextureMeshRef _meshRef;
        private readonly MultiTextureMeshRef? _firstPersonMeshRef;
        private readonly MultiTextureMeshRef? _immersiveFirstPersonMeshRef;
        private VanillaPreviewMode _previewMode = VanillaPreviewMode.Orbit;
        private bool _disposed;

        private VanillaAnimationPreviewScene(
            ICoreClientAPI api,
            string key,
            string displayName,
            Shape shape,
            VanillaAnimation animation,
            AnimationMetaData metadata,
            ClientAnimator animator,
            VanillaPreviewMeshSet meshes,
            int textureId,
            VanillaModelBounds bounds,
            VanillaGuiTransform guiTransform,
            string status)
        {
            _api = api;
            Key = key;
            DisplayName = displayName;
            _shape = shape;
            _animation = animation;
            _metadata = metadata;
            _activeAnimationCode = GetAnimationCode(animation, metadata);
            _metadata.Animation = _activeAnimationCode;
            _animator = animator;
            _meshRef = meshes.Orbit;
            _firstPersonMeshRef = meshes.FirstPerson;
            _immersiveFirstPersonMeshRef = meshes.ImmersiveFirstPerson;
            MeshVerticesCount = meshes.VerticesCount;
            MeshIndicesCount = meshes.IndicesCount;
            TextureId = textureId;
            Status = status;
            GuiEntitySize = guiTransform.EntitySize;
            EntityEyeHeight = guiTransform.EyeHeight > 0 ? guiTransform.EyeHeight : Math.Max(0.05f, bounds.Height * guiTransform.EntitySize * 0.85f);
            GuiShapeRotateX = guiTransform.RotateX;
            GuiShapeRotateY = guiTransform.RotateY;
            GuiShapeRotateZ = guiTransform.RotateZ;
            FirstPersonFovDegrees = Math.Clamp(api.Settings.Int["fpHandsFoV"] > 0 ? api.Settings.Int["fpHandsFoV"] : 75, 25, 130);
            FirstPersonYOffset = api.Settings.Float["fpHandsYOffset"];
            QuantityFrames = Math.Max(1, animation.QuantityFrames);
            ApplyBounds(bounds);
            _activeAnimationsByAnimCode[_activeAnimationCode] = _metadata;
            ForceEvaluatePose(0);
        }

        public string Key { get; }
        public string DisplayName { get; }
        public MultiTextureMeshRef MeshRef => _meshRef;
        public int MeshVerticesCount { get; private set; }
        public int MeshIndicesCount { get; private set; }
        public int TextureId { get; }
        public string Status { get; private set; }
        public int QuantityFrames { get; private set; }
        public float CurrentFrame { get; private set; }
        public bool Playing { get; set; }
        public ClientAnimator Animator => _animator;
        public float ModelCenterX { get; private set; }
        public float ModelCenterY { get; private set; }
        public float ModelCenterZ { get; private set; }
        public float ModelWidth { get; private set; } = 1f;
        public float ModelHeight { get; private set; } = 2f;
        public float ModelDepth { get; private set; } = 1f;
        public Shape Shape => _shape;
        public float GuiEntitySize { get; private set; } = 1f;
        public float EntityEyeHeight { get; private set; } = 1.6f;
        public float GuiShapeRotateX { get; private set; }
        public float GuiShapeRotateY { get; private set; }
        public float GuiShapeRotateZ { get; private set; }
        public float FirstPersonFovDegrees { get; private set; } = 75f;
        public float FirstPersonYOffset { get; private set; }
        public bool ClassicFirstPersonAvailable => _firstPersonMeshRef is { Disposed: false, Initialized: true };
        public bool ImmersiveFirstPersonAvailable => _immersiveFirstPersonMeshRef is { Disposed: false, Initialized: true };
        public bool FirstPersonAvailable => ClassicFirstPersonAvailable || ImmersiveFirstPersonAvailable;
        public VanillaPreviewMode PreviewMode => _previewMode;

        public MultiTextureMeshRef GetMeshRef(VanillaPreviewMode mode)
        {
            return mode switch
            {
                VanillaPreviewMode.FirstPerson when ClassicFirstPersonAvailable => _firstPersonMeshRef!,
                VanillaPreviewMode.ImmersiveFirstPerson when ImmersiveFirstPersonAvailable => _immersiveFirstPersonMeshRef!,
                _ => _meshRef
            };
        }

        public static VanillaAnimationPreviewScene Create(ICoreClientAPI api, VanillaBrowserRow row)
        {
            Shape sourceShape = GetSourceShape(row) ?? throw new InvalidOperationException("Selected vanilla row has no loaded shape.");
            Shape shape = PrepareShapeForPreview(api, sourceShape, row.Key);
            ApplyEditedAnimationsToPreviewShape(row, shape);
            ResolvePreviewShapeAnimationReferences(api, shape, row.Key);
            VanillaAnimation animation = ResolvePreviewAnimation(row, shape, VanillaPreviewMode.Orbit) ?? throw new InvalidOperationException("Selected vanilla row has no matching animation in its preview shape.");
            PrepareAnimationFrames(shape, animation);
            AnimationMetaData metadata = BuildPreviewMetadata(row, animation, VanillaPreviewMode.Orbit);
            ClientAnimator animator = CreatePreviewAnimator(shape, row.Key);
            VanillaPreviewMeshSet meshes = BuildPreviewMeshes(api, row, shape, animator, out int textureId);
            VanillaModelBounds bounds = CalculateModelBounds(shape);
            VanillaGuiTransform guiTransform = GetGuiTransform(row);
            string status = $"Loaded {row.Label}. Mesh parts: {meshes.Orbit.meshrefs?.Length ?? 0}. First-person: {(meshes.FirstPerson != null ? "classic" : "not available")}, {(meshes.ImmersiveFirstPerson != null ? "immersive" : "no immersive mesh")}. Bounds: {bounds.Width:0.00} x {bounds.Height:0.00} x {bounds.Depth:0.00}.";
            return new(api, row.Key, row.Label, shape, animation, metadata, animator, meshes, textureId, bounds, guiTransform, status);
        }

        public void ReloadAnimator(VanillaBrowserRow row)
        {
            Shape sourceShape = GetSourceShape(row) ?? throw new InvalidOperationException("Selected vanilla row has no loaded shape.");
            Shape shape = PrepareShapeForPreview(_api, sourceShape, row.Key);
            ApplyEditedAnimationsToPreviewShape(row, shape);
            ResolvePreviewShapeAnimationReferences(_api, shape, row.Key);
            VanillaAnimation animation = ResolvePreviewAnimation(row, shape, _previewMode) ?? throw new InvalidOperationException("Selected vanilla row has no matching animation in its preview shape.");
            PrepareAnimationFrames(shape, animation);
            AnimationMetaData metadata = BuildPreviewMetadata(row, animation, _previewMode);

            _shape = shape;
            _animation = animation;
            _metadata = metadata;
            _activeAnimationCode = GetAnimationCode(animation, metadata);
            _metadata.Animation = _activeAnimationCode;
            _animator = CreatePreviewAnimator(shape, row.Key);
            ApplyBounds(CalculateModelBounds(shape));
            ApplyGuiTransform(GetGuiTransform(row));
            FirstPersonFovDegrees = Math.Clamp(_api.Settings.Int["fpHandsFoV"] > 0 ? _api.Settings.Int["fpHandsFoV"] : 75, 25, 130);
            FirstPersonYOffset = _api.Settings.Float["fpHandsYOffset"];
            _activeAnimationsByAnimCode.Clear();
            _activeAnimationsByAnimCode[_activeAnimationCode] = _metadata;
            QuantityFrames = Math.Max(1, animation.QuantityFrames);
            CurrentFrame = Math.Clamp(CurrentFrame, 0, Math.Max(0, QuantityFrames - 1));
            ForceEvaluatePose(CurrentFrame);
        }

        public void SetPreviewMode(VanillaBrowserRow row, VanillaPreviewMode mode)
        {
            if ((mode == VanillaPreviewMode.FirstPerson && !ClassicFirstPersonAvailable) ||
                (mode == VanillaPreviewMode.ImmersiveFirstPerson && !ImmersiveFirstPersonAvailable))
            {
                mode = VanillaPreviewMode.Orbit;
            }

            if (_previewMode == mode) return;

            VanillaAnimation animation = ResolvePreviewAnimation(row, _shape, mode) ?? _animation;
            PrepareAnimationFrames(_shape, animation);
            AnimationMetaData metadata = BuildPreviewMetadata(row, animation, mode);

            _previewMode = mode;
            _animation = animation;
            _metadata = metadata;
            _activeAnimationCode = GetAnimationCode(animation, metadata);
            _metadata.Animation = _activeAnimationCode;
            _activeAnimationsByAnimCode.Clear();
            _activeAnimationsByAnimCode[_activeAnimationCode] = _metadata;
            QuantityFrames = Math.Max(1, animation.QuantityFrames);
            CurrentFrame = Math.Clamp(CurrentFrame, 0, Math.Max(0, QuantityFrames - 1));
            ForceEvaluatePose(CurrentFrame);
        }

        public void Play()
        {
            if (_disposed) return;
            if (CurrentFrame >= QuantityFrames - 1)
            {
                Scrub(0);
            }
            else
            {
                EnsureActive();
            }

            Playing = true;
        }

        public void Tick(float deltaSeconds)
        {
            if (_disposed) return;
            EnsureActive();
            _animator.OnFrame(_activeAnimationsByAnimCode, deltaSeconds);
            RunningAnimation? state = _animator.GetAnimationState(_activeAnimationCode);
            if (state != null)
            {
                CurrentFrame = Math.Clamp(state.CurrentFrame, 0, Math.Max(0, QuantityFrames - 1));
            }
        }

        public void Scrub(float frame)
        {
            ForceEvaluatePose(frame);
        }

        private void ForceEvaluatePose(float frame)
        {
            if (_disposed) return;
            bool wasPlaying = Playing;
            CurrentFrame = Math.Clamp(frame, 0, Math.Max(0, QuantityFrames - 1));
            EnsureActive();
            _metadata.StartFrameOnce = CurrentFrame;
            _animator.OnFrame(_activeAnimationsByAnimCode, 0.001f);

            RunningAnimation? state = _animator.GetAnimationState(_activeAnimationCode);
            if (state != null)
            {
                state.meta = _metadata;
                state.EasingFactor = 1f;
                state.CurrentFrame = CurrentFrame;
                state.Iterations = CurrentFrame >= QuantityFrames - 1 ? 1 : 0;
            }

            _metadata.StartFrameOnce = CurrentFrame;
            _animator.OnFrame(_activeAnimationsByAnimCode, 0f);
            state = _animator.GetAnimationState(_activeAnimationCode);
            if (state != null)
            {
                state.meta = _metadata;
                state.EasingFactor = 1f;
                state.CurrentFrame = CurrentFrame;
                state.Iterations = CurrentFrame >= QuantityFrames - 1 ? 1 : 0;
            }
            Playing = wasPlaying;
        }

        private void EnsureActive()
        {
            _activeAnimationsByAnimCode[_activeAnimationCode] = _metadata;
        }

        private void ApplyBounds(VanillaModelBounds bounds)
        {
            ModelCenterX = bounds.CenterX;
            ModelCenterY = bounds.CenterY;
            ModelCenterZ = bounds.CenterZ;
            ModelWidth = Math.Max(0.1f, bounds.Width);
            ModelHeight = Math.Max(0.1f, bounds.Height);
            ModelDepth = Math.Max(0.1f, bounds.Depth);
        }

        private void ApplyGuiTransform(VanillaGuiTransform transform)
        {
            GuiEntitySize = transform.EntitySize;
            EntityEyeHeight = transform.EyeHeight > 0 ? transform.EyeHeight : Math.Max(0.05f, ModelHeight * GuiEntitySize * 0.85f);
            GuiShapeRotateX = transform.RotateX;
            GuiShapeRotateY = transform.RotateY;
            GuiShapeRotateZ = transform.RotateZ;
        }

        private static string GetAnimationCode(VanillaAnimation animation, AnimationMetaData metadata)
        {
            return animation.Code ?? animation.Name ?? metadata.Animation ?? metadata.Code ?? "preview";
        }

        public void Dispose()
        {
            _disposed = true;
            _meshRef.Dispose();
            _firstPersonMeshRef?.Dispose();
            _immersiveFirstPersonMeshRef?.Dispose();
        }

        private static Shape? GetSourceShape(VanillaBrowserRow row)
        {
            return row.Document.Shape ?? row.ShapeAnimation?.Document.Shape ?? row.MetadataEntry?.LinkedShape?.Document.Shape;
        }

        private static void ApplyEditedAnimationsToPreviewShape(VanillaBrowserRow row, Shape shape)
        {
            VanillaAnimationDocument? document = row.ShapeAnimation?.Document
                ?? row.MetadataEntry?.ResolveCurrentShape()?.Document
                ?? (row.Document.Kind == VanillaDocumentKind.Shape ? row.Document : null);
            if (document == null || document.ShapeAnimations.Count == 0) return;

            List<VanillaAnimation> animations = (shape.Animations ?? []).Select(CloneVanillaAnimation).ToList();
            foreach (VanillaShapeAnimationEntry entry in document.ShapeAnimations)
            {
                VanillaAnimation clone = CloneVanillaAnimation(entry.Animation);
                string code = clone.Code ?? clone.Name ?? "";
                int targetIndex = !string.IsNullOrWhiteSpace(code)
                    ? animations.FindIndex(animation => string.Equals(animation.Code ?? animation.Name, code, StringComparison.OrdinalIgnoreCase))
                    : -1;
                if (targetIndex < 0 && entry.Index >= 0 && entry.Index < animations.Count)
                {
                    targetIndex = entry.Index;
                }

                if (targetIndex >= 0)
                {
                    animations[targetIndex] = clone;
                }
                else
                {
                    animations.Add(clone);
                }
            }

            shape.Animations = animations.ToArray();
        }

        private static VanillaGuiTransform GetGuiTransform(VanillaBrowserRow row)
        {
            EntityClientProperties? client = row.Document.EntityType?.Client
                ?? row.ShapeAnimation?.Document.EntityType?.Client
                ?? row.MetadataEntry?.Document.EntityType?.Client
                ?? row.MetadataEntry?.LinkedShape?.Document.EntityType?.Client;
            CompositeShape? shape = client?.ShapeForEntity ?? client?.Shape;
            EntityProperties? entityType = row.Document.EntityType
                ?? row.ShapeAnimation?.Document.EntityType
                ?? row.MetadataEntry?.Document.EntityType
                ?? row.MetadataEntry?.LinkedShape?.Document.EntityType;
            return new(
                Math.Max(0.001f, client?.Size ?? 1f),
                Math.Max(0f, (float)(entityType?.EyeHeight ?? 0)),
                shape?.rotateX ?? 0f,
                shape?.rotateY ?? 0f,
                shape?.rotateZ ?? 0f);
        }

        private static Shape PrepareShapeForPreview(ICoreClientAPI api, Shape sourceShape, string shapeName)
        {
            Shape shape = sourceShape.Clone() ?? throw new InvalidOperationException($"Preview shape '{shapeName}' could not be cloned.");
            if (shape.Elements == null || shape.Elements.Length == 0)
            {
                throw new InvalidOperationException($"Preview shape '{shapeName}' has no elements.");
            }

            shape.Textures ??= new();
            ResolvePreviewShapeAnimationReferences(api, shape, shapeName);

            return shape;
        }

        private static void ResolvePreviewShapeAnimationReferences(ICoreClientAPI api, Shape shape, string shapeName)
        {
            shape.AnimationsByCrc32 ??= new();
            shape.AnimationsByCrc32.Clear();
            shape.JointsById ??= new();
            shape.JointsById.Clear();

            Dictionary<string, ShapeElement> elementsByName = shape.CollectAndResolveReferences(api.World.Logger, shapeName)
                ?? throw new InvalidOperationException($"Preview shape '{shapeName}' reference resolution returned no elements.");
            if (elementsByName.Count == 0)
            {
                throw new InvalidOperationException($"Preview shape '{shapeName}' has no resolved elements.");
            }

            shape.CacheInvTransforms();
            shape.ResolveAndFindJoints(api.World.Logger, shapeName, elementsByName);
        }

        private static void PrepareAnimationFrames(Shape shape, VanillaAnimation animation)
        {
            if (string.IsNullOrWhiteSpace(animation.Code)) animation.Code = animation.Name;
            CompleteVanillaAnimationTransformGroups(animation);
            if (shape.Elements == null || shape.Elements.Length == 0)
            {
                throw new InvalidOperationException($"Preview animation '{animation.Code ?? animation.Name ?? "unknown"}' has no shape elements to animate.");
            }

            animation.GenerateAllFrames(shape.Elements, shape.JointsById);
        }

        private static ClientAnimator CreatePreviewAnimator(Shape shape, string shapeName)
        {
            if (shape.Elements == null || shape.Elements.Length == 0)
            {
                throw new InvalidOperationException($"Preview shape '{shapeName}' has no elements for its animator.");
            }

            if (shape.Animations == null || shape.Animations.Length == 0)
            {
                throw new InvalidOperationException($"Preview shape '{shapeName}' has no animations for its animator.");
            }

            return new ClientAnimator(() => 1, shape.Animations, shape.Elements, shape.JointsById, null, null);
        }

        private static void CompleteVanillaAnimationTransformGroups(VanillaAnimation animation)
        {
            if (animation.KeyFrames == null) return;
            foreach (AnimationKeyFrame keyFrame in animation.KeyFrames)
            {
                if (keyFrame.Elements == null) continue;
                foreach (AnimationKeyFrameElement element in keyFrame.Elements.Values)
                {
                    CompleteVanillaElementTransformGroups(element);
                }
            }
        }

        private static VanillaModelBounds CalculateModelBounds(Shape shape)
        {
            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;
            float maxZ = float.NegativeInfinity;
            bool hasBounds = false;

            void Include(ShapeElement element)
            {
                if (element.From is { Length: >= 3 } from && element.To is { Length: >= 3 } to)
                {
                    float fromX = (float)from[0] / 16f;
                    float fromY = (float)from[1] / 16f;
                    float fromZ = (float)from[2] / 16f;
                    float toX = (float)to[0] / 16f;
                    float toY = (float)to[1] / 16f;
                    float toZ = (float)to[2] / 16f;

                    minX = Math.Min(minX, Math.Min(fromX, toX));
                    minY = Math.Min(minY, Math.Min(fromY, toY));
                    minZ = Math.Min(minZ, Math.Min(fromZ, toZ));
                    maxX = Math.Max(maxX, Math.Max(fromX, toX));
                    maxY = Math.Max(maxY, Math.Max(fromY, toY));
                    maxZ = Math.Max(maxZ, Math.Max(fromZ, toZ));
                    hasBounds = true;
                }

                if (element.Children == null) return;
                foreach (ShapeElement child in element.Children)
                {
                    Include(child);
                }
            }

            if (shape.Elements != null)
            {
                foreach (ShapeElement element in shape.Elements)
                {
                    Include(element);
                }
            }

            return hasBounds
                ? new(minX, minY, minZ, maxX, maxY, maxZ)
                : new(0f, 0f, 0f, 1f, 2f, 1f);
        }

        private static VanillaAnimation? ResolvePreviewAnimation(VanillaBrowserRow row, Shape previewShape, VanillaPreviewMode mode)
        {
            string? code = ResolvePreviewAnimationCode(row, previewShape, mode);
            if (string.IsNullOrWhiteSpace(code)) return previewShape.Animations?.FirstOrDefault();
            return previewShape.Animations?.FirstOrDefault(animation =>
                string.Equals(animation.Code ?? animation.Name, code, StringComparison.OrdinalIgnoreCase));
        }

        private static string? ResolvePreviewAnimationCode(VanillaBrowserRow row, Shape previewShape, VanillaPreviewMode mode)
        {
            if (row.MetadataEntry != null)
            {
                return ResolvePreviewMetadata(row, mode)?.Animation ?? row.MetadataEntry.Metadata.Animation;
            }

            string? code = row.ShapeAnimation?.Animation.Code ?? row.ShapeAnimation?.Animation.Name;
            if (mode == VanillaPreviewMode.Orbit || string.IsNullOrWhiteSpace(code)) return code;

            string suffix = GetPreviewModeAnimationSuffix(mode);
            if (HasFirstPersonSuffix(code)) return code;

            string variantCode = code + suffix;
            return previewShape.Animations?.Any(animation => string.Equals(animation.Code ?? animation.Name, variantCode, StringComparison.OrdinalIgnoreCase)) == true
                ? variantCode
                : code;
        }

        private static AnimationMetaData BuildPreviewMetadata(VanillaBrowserRow row, VanillaAnimation animation, VanillaPreviewMode mode)
        {
            AnimationMetaData metadata = row.MetadataEntry != null
                ? ResolvePreviewMetadata(row, mode) ?? CloneAnimationMetaData(row.MetadataEntry.Metadata)
                : new AnimationMetaData
                {
                    Code = animation.Code ?? animation.Name ?? "preview",
                    Animation = animation.Code ?? animation.Name ?? "preview",
                    AnimationSpeed = 1f,
                    Weight = 1f,
                    BlendMode = EnumAnimationBlendMode.Add,
                    EaseInSpeed = 10f,
                    EaseOutSpeed = 10f,
                    ClientSide = true
                };

            metadata.Code = string.IsNullOrWhiteSpace(metadata.Code) ? metadata.Animation : metadata.Code;
            metadata.Animation = string.IsNullOrWhiteSpace(metadata.Animation) ? animation.Code ?? animation.Name ?? metadata.Code : metadata.Animation;
            metadata.Init();
            return metadata;
        }

        private static AnimationMetaData? ResolvePreviewMetadata(VanillaBrowserRow row, VanillaPreviewMode mode)
        {
            if (row.MetadataEntry == null) return null;

            AnimationMetaData source = row.MetadataEntry.Metadata;
            if (mode == VanillaPreviewMode.Orbit || HasFirstPersonSuffix(source.Code))
            {
                return CloneAnimationMetaData(source);
            }

            if (mode == VanillaPreviewMode.FirstPerson)
            {
                AnimationMetaData sourceClone = CloneAnimationMetaData(source);
                sourceClone.Init();
                if (sourceClone.WithFpVariant && sourceClone.FpVariant != null)
                {
                    return CloneAnimationMetaData(sourceClone.FpVariant);
                }
            }

            string suffix = GetPreviewModeAnimationSuffix(mode);
            if (!string.IsNullOrWhiteSpace(source.Code) &&
                row.MetadataEntry.Document.EntityType?.Client?.AnimationsByMetaCode?.TryGetValue(source.Code + suffix, out AnimationMetaData? variant) == true)
            {
                return CloneAnimationMetaData(variant);
            }

            return CloneAnimationMetaData(source);
        }

        private static string GetPreviewModeAnimationSuffix(VanillaPreviewMode mode)
        {
            return mode == VanillaPreviewMode.ImmersiveFirstPerson ? "-ifp" : "-fp";
        }

        private static bool HasFirstPersonSuffix(string? code)
        {
            return !string.IsNullOrWhiteSpace(code) &&
                (code.EndsWith("-fp", StringComparison.OrdinalIgnoreCase) || code.EndsWith("-ifp", StringComparison.OrdinalIgnoreCase));
        }

        private static VanillaPreviewMeshSet BuildPreviewMeshes(ICoreClientAPI api, VanillaBrowserRow row, Shape shape, ClientAnimator animator, out int textureId)
        {
            if (api.Tesselator == null)
            {
                throw new InvalidOperationException("Preview tessellator is not available.");
            }

            if (api.Render == null)
            {
                throw new InvalidOperationException("Preview renderer is not available.");
            }

            if (api.EntityTextureAtlas == null)
            {
                throw new InvalidOperationException("Entity texture atlas is not available for preview tessellation.");
            }

            ITexPositionSource texSource = CreateTextureSource(api, row, shape);
            CompositeShape? compositeShape = GetCompositeShape(row);
            TesselationMetaData meta = new()
            {
                TexSource = texSource,
                WithJointIds = true,
                WithDamageEffect = true,
                TypeForLogging = row.Key,
                QuantityElements = compositeShape?.QuantityElements,
                SelectiveElements = compositeShape?.SelectiveElements,
                IgnoreElements = compositeShape?.IgnoreElements,
                Rotation = compositeShape == null
                    ? null
                    : new Vec3f(compositeShape.rotateX, compositeShape.rotateY, compositeShape.rotateZ)
            };

            api.Tesselator.TesselateShape(meta, shape, out MeshData mesh);
            if (mesh == null)
            {
                throw new InvalidOperationException($"Preview tessellation for {row.Label} returned no mesh.");
            }

            if (mesh.VerticesCount <= 0 || mesh.IndicesCount <= 0)
            {
                throw new InvalidOperationException($"Preview tessellation for {row.Label} produced an empty mesh.");
            }

            if (compositeShape != null)
            {
                mesh.Translate(compositeShape.offsetX, compositeShape.offsetY, compositeShape.offsetZ);
            }

            EnsurePreviewVertexColor(mesh);
            textureId = mesh.TextureIds is { Length: > 0 }
                ? mesh.TextureIds[0]
                : GetFallbackEntityTextureId(api);

            MultiTextureMeshRef orbit = api.Render.UploadMultiTextureMesh(mesh)
                ?? throw new InvalidOperationException($"Preview mesh upload for {row.Label} returned no mesh reference.");
            if (orbit.Disposed)
            {
                throw new InvalidOperationException($"Preview mesh upload for {row.Label} returned a disposed mesh reference.");
            }

            MultiTextureMeshRef? firstPerson = TryBuildPlayerFirstPersonMesh(api, mesh, animator, immersive: false);
            MultiTextureMeshRef? immersiveFirstPerson = TryBuildPlayerFirstPersonMesh(api, mesh, animator, immersive: true);
            return new(orbit, firstPerson, immersiveFirstPerson, mesh.VerticesCount, mesh.IndicesCount);
        }

        private static int GetFallbackEntityTextureId(ICoreClientAPI api)
        {
            if (api.EntityTextureAtlas.AtlasTextures is { Count: > 0 } atlasTextures && atlasTextures[0] != null)
            {
                return atlasTextures[0].TextureId;
            }

            TextureAtlasPosition? unknown = api.EntityTextureAtlas.UnknownTexturePosition;
            if (unknown?.atlasTextureId > 0)
            {
                return unknown.atlasTextureId;
            }

            throw new InvalidOperationException("Preview mesh has no texture ids and the entity texture atlas has no fallback texture.");
        }

        private static MultiTextureMeshRef? TryBuildPlayerFirstPersonMesh(ICoreClientAPI api, MeshData mesh, ClientAnimator animator, bool immersive)
        {
            try
            {
                return BuildPlayerFirstPersonMesh(api, mesh, animator, immersive);
            }
            catch (Exception exception)
            {
                api.Logger.VerboseDebug("[InGameDevTools] First-person vanilla preview mesh skipped: immersive={0}, reason={1}", immersive, exception.Message);
                return null;
            }
        }

        private static MultiTextureMeshRef? BuildPlayerFirstPersonMesh(ICoreClientAPI api, MeshData mesh, ClientAnimator animator, bool immersive)
        {
            if (mesh.CustomInts == null || mesh.CustomInts.Values == null || mesh.VerticesCount <= 0) return null;

            HashSet<int> jointIds = [];
            if (immersive)
            {
                LoadJointIdsRecursive(animator.GetPosebyName("Neck", StringComparison.InvariantCultureIgnoreCase), jointIds);
                if (jointIds.Count == 0) return null;
            }
            else
            {
                LoadJointIdsRecursive(animator.GetPosebyName("UpperArmR", StringComparison.InvariantCultureIgnoreCase), jointIds);
                LoadJointIdsRecursive(animator.GetPosebyName("UpperArmL", StringComparison.InvariantCultureIgnoreCase), jointIds);
                if (jointIds.Count == 0) return null;
            }

            MeshData filtered = mesh.EmptyClone() ?? throw new InvalidOperationException("Could not create first-person preview mesh clone.");
            filtered.AddMeshData(mesh, faceIndex =>
            {
                int vertexIndex = faceIndex * MeshData.StandardVerticesPerFace;
                if (vertexIndex < 0 || vertexIndex >= mesh.VerticesCount) return false;
                int jointValueIndex = vertexIndex;
                if (jointValueIndex < 0 || jointValueIndex >= mesh.CustomInts.Values.Length) return false;
                bool inSet = jointIds.Contains(mesh.CustomInts.Values[jointValueIndex]);
                return immersive ? !inSet : inSet;
            });

            return filtered.VerticesCount > 0 ? api.Render.UploadMultiTextureMesh(filtered) : null;
        }

        private static void LoadJointIdsRecursive(ElementPose? pose, HashSet<int> jointIds)
        {
            if (pose?.ForElement == null) return;

            if (pose.ForElement.JointId > 0)
            {
                jointIds.Add(pose.ForElement.JointId);
            }

            if (pose.ChildElementPoses == null) return;
            foreach (ElementPose child in pose.ChildElementPoses)
            {
                LoadJointIdsRecursive(child, jointIds);
            }
        }

        private static void EnsurePreviewVertexColor(MeshData mesh)
        {
            int requiredLength = mesh.VerticesCount * 4;
            if (requiredLength <= 0) return;

            if (mesh.Rgba == null || mesh.Rgba.Length < requiredLength)
            {
                mesh.Rgba = new byte[requiredLength];
                FillPreviewVertexColor(mesh.Rgba);
                return;
            }

            bool hasVisibleColor = false;
            for (int index = 0; index + 3 < requiredLength; index += 4)
            {
                if (mesh.Rgba[index + 3] == 0) continue;
                if ((mesh.Rgba[index + 0] | mesh.Rgba[index + 1] | mesh.Rgba[index + 2]) == 0) continue;
                hasVisibleColor = true;
                break;
            }

            if (!hasVisibleColor)
            {
                FillPreviewVertexColor(mesh.Rgba);
                return;
            }

            for (int index = 3; index < requiredLength; index += 4)
            {
                if (mesh.Rgba[index] == 0)
                {
                    mesh.Rgba[index] = 255;
                }
            }
        }

        private static void FillPreviewVertexColor(byte[] rgba)
        {
            for (int index = 0; index + 3 < rgba.Length; index += 4)
            {
                rgba[index + 0] = 255;
                rgba[index + 1] = 255;
                rgba[index + 2] = 255;
                rgba[index + 3] = 255;
            }
        }

        private static CompositeShape? GetCompositeShape(VanillaBrowserRow row)
        {
            EntityClientProperties? client = row.Document.EntityType?.Client
                ?? row.ShapeAnimation?.Document.EntityType?.Client
                ?? row.MetadataEntry?.Document.EntityType?.Client
                ?? row.MetadataEntry?.LinkedShape?.Document.EntityType?.Client;
            return client?.ShapeForEntity ?? client?.Shape;
        }

        private static ITexPositionSource CreateTextureSource(ICoreClientAPI api, VanillaBrowserRow row, Shape shape)
        {
            IDictionary<string, CompositeTexture>? textures = row.Document.EntityType?.Client?.Textures;
            if (textures != null && textures.Count > 0)
            {
                return new VanillaEntityTextureSource(api, shape, row.Key, textures);
            }

            return new ShapeTextureSource(api, shape, row.Key);
        }
    }

    private sealed class VanillaEntityTextureSource : ITexPositionSource
    {
        private readonly ICoreClientAPI _api;
        private readonly Shape _shape;
        private readonly string _filenameForLogging;
        private readonly IDictionary<string, CompositeTexture> _textures;
        private readonly HashSet<string> _missingTextures = new(StringComparer.OrdinalIgnoreCase);

        public VanillaEntityTextureSource(ICoreClientAPI api, Shape shape, string filenameForLogging, IDictionary<string, CompositeTexture> textures)
        {
            _api = api;
            _shape = shape;
            _filenameForLogging = filenameForLogging;
            _textures = textures;
        }

        public Size2i AtlasSize => _api.EntityTextureAtlas.Size;

        public TextureAtlasPosition this[string textureCode]
        {
            get
            {
                if (string.IsNullOrWhiteSpace(textureCode))
                {
                    return _api.EntityTextureAtlas.UnknownTexturePosition;
                }

                if (_textures.TryGetValue(textureCode, out CompositeTexture? texture) && texture != null)
                {
                    return GetEntityTexturePosition(textureCode, texture);
                }

                if (_shape.Textures != null && _shape.Textures.TryGetValue(textureCode, out AssetLocation? texturePath) && texturePath != null)
                {
                    if (_api.EntityTextureAtlas.GetOrInsertTexture(texturePath, out _, out TextureAtlasPosition texPos))
                    {
                        return texPos;
                    }

                    return _api.EntityTextureAtlas.UnknownTexturePosition;
                }

                if (_textures.TryGetValue("all", out CompositeTexture? fallbackTexture) && fallbackTexture != null)
                {
                    return GetEntityTexturePosition("all", fallbackTexture);
                }

                if (_missingTextures.Add(textureCode))
                {
                    _api.Logger.Warning("Shape {0} has an element using texture code {1}, but no entity texture mapping exists", _filenameForLogging, textureCode);
                }

                return _api.EntityTextureAtlas.UnknownTexturePosition;
            }
        }

        private TextureAtlasPosition GetEntityTexturePosition(string textureCode, CompositeTexture texture)
        {
            if (texture.Baked == null)
            {
                texture.Bake(_api.Assets);
            }

            BakedCompositeTexture? baked = GetDefaultBakedTexture(texture);
            TextureAtlasPosition? bakedPosition = GetEntityAtlasPosition(baked);
            if (bakedPosition != null && (baked?.TextureSubId > 0 || IsUnknownTexture(baked)))
            {
                return bakedPosition;
            }

            if (baked?.BakedName != null &&
                _api.EntityTextureAtlas.GetOrInsertTexture(baked.BakedName, out int textureSubId, out TextureAtlasPosition insertedPosition))
            {
                baked.TextureSubId = textureSubId;
                if (ReferenceEquals(baked, texture.Baked))
                {
                    texture.Baked.TextureSubId = textureSubId;
                }

                return insertedPosition;
            }

            if (_missingTextures.Add(textureCode))
            {
                _api.Logger.Warning("Could not resolve entity texture code {0} while tessellating {1}", textureCode, _filenameForLogging);
            }

            return _api.EntityTextureAtlas.UnknownTexturePosition;
        }

        private TextureAtlasPosition? GetEntityAtlasPosition(BakedCompositeTexture? baked)
        {
            if (baked == null) return null;

            int textureSubId = baked.TextureSubId;
            TextureAtlasPosition[]? positions = _api.EntityTextureAtlas.Positions;
            if (positions == null) return null;

            return textureSubId >= 0 && textureSubId < positions.Length
                ? positions[textureSubId]
                : null;
        }

        private static BakedCompositeTexture? GetDefaultBakedTexture(CompositeTexture texture)
        {
            BakedCompositeTexture? baked = texture.Baked;
            return baked?.BakedVariants is { Length: > 0 } variants
                ? variants[0] ?? baked
                : baked;
        }

        private static bool IsUnknownTexture(BakedCompositeTexture? baked)
        {
            return baked?.BakedName?.Path == "unknown";
        }
    }

    private readonly struct VanillaModelBounds
    {
        public VanillaModelBounds(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            MinX = minX;
            MinY = minY;
            MinZ = minZ;
            MaxX = maxX;
            MaxY = maxY;
            MaxZ = maxZ;
        }

        public float MinX { get; }
        public float MinY { get; }
        public float MinZ { get; }
        public float MaxX { get; }
        public float MaxY { get; }
        public float MaxZ { get; }
        public float Width => Math.Max(0.1f, MaxX - MinX);
        public float Height => Math.Max(0.1f, MaxY - MinY);
        public float Depth => Math.Max(0.1f, MaxZ - MinZ);
        public float CenterX => (MinX + MaxX) * 0.5f;
        public float CenterY => (MinY + MaxY) * 0.5f;
        public float CenterZ => (MinZ + MaxZ) * 0.5f;
    }

    private readonly record struct VanillaGizmoProjection(
        NVector2 Center,
        float Scale,
        NVector2 AxisX,
        NVector2 AxisY,
        NVector2 AxisZ,
        NVector2[] RingX,
        NVector2[] RingY,
        NVector2[] RingZ,
        NVector2[] BoundsCorners,
        bool HasVisualCenter,
        NVector2 VisualCenter);

    private sealed record VanillaPreviewMeshSet(
        MultiTextureMeshRef Orbit,
        MultiTextureMeshRef? FirstPerson,
        MultiTextureMeshRef? ImmersiveFirstPerson,
        int VerticesCount,
        int IndicesCount);

    private readonly record struct VanillaGuiTransform(float EntitySize, float EyeHeight, float RotateX, float RotateY, float RotateZ);

    private readonly record struct VanillaPreviewCameraState(
        Matrixf Projection,
        Matrixf View,
        Matrixf ProjectionView,
        Matrixf Model,
        NVector3 Eye,
        NVector3 Target,
        float Distance);

    private sealed class VanillaAnimationViewport3DRenderer : IDisposable
    {
        private readonly ICoreClientAPI _api;
        private FrameBufferRef? _frameBuffer;
        private string _lastSceneLogKey = "";
        private string _lastFrameLogKey = "";
        private string _lastSkipLogKey = "";
        private long _lastSkipLogAtMs;

        public VanillaAnimationViewport3DRenderer(ICoreClientAPI api)
        {
            _api = api;
        }

        public void SetVisible(bool visible)
        {
            if (visible) return;
            _lastFrameLogKey = "";
            _lastSkipLogKey = "";
        }

        public int RenderToTexture(
            VanillaAnimationPreviewScene scene,
            float width,
            float height,
            float yaw,
            float pitch,
            float zoom,
            float panX,
            float panY,
            VanillaPreviewMode mode,
            bool worldLighting,
            bool verboseLogs,
            out string? skipReason)
        {
            skipReason = null;
            if (width <= 32 || height <= 32) return Skip(scene, mode, width, height, "viewport too small", verboseLogs, out skipReason);

            MultiTextureMeshRef meshRef = scene.GetMeshRef(mode);
            if (meshRef.Disposed) return Skip(scene, mode, width, height, "mesh disposed", verboseLogs, out skipReason);
            if (!meshRef.Initialized) return Skip(scene, mode, width, height, "mesh not initialized", verboseLogs, out skipReason);

            int framebufferWidth = Math.Max(1, (int)Math.Ceiling(width));
            int framebufferHeight = Math.Max(1, (int)Math.Ceiling(height));
            FrameBufferRef frameBuffer = EnsureFrameBuffer(framebufferWidth, framebufferHeight);
            if (frameBuffer == null || frameBuffer.Disposed)
            {
                return Skip(scene, mode, width, height, "preview framebuffer unavailable", verboseLogs, out skipReason);
            }

            if (frameBuffer.ColorTextureIds == null || frameBuffer.ColorTextureIds.Length == 0)
            {
                return Skip(scene, mode, width, height, "preview framebuffer has no color texture", verboseLogs, out skipReason);
            }

            VanillaPreviewCameraState camera = BuildVanillaPreviewCamera(scene, framebufferWidth, framebufferHeight, yaw, pitch, zoom, panX, panY, mode);
            IRenderAPI render = _api.Render;
            if (render == null)
            {
                return Skip(scene, mode, width, height, "render API unavailable", verboseLogs, out skipReason);
            }

            FrameBufferRef? restoreFrameBuffer = render.CurrentFrameBuffer;
            IShaderProgram? previous = render.CurrentActiveShader;
            int[] restoreViewport = new int[4];
            GL.GetInteger(GetPName.Viewport, restoreViewport);
            bool restoreDepthTest = GL.IsEnabled(EnableCap.DepthTest);
            GL.GetInteger(GetPName.DepthFunc, out int restoreDepthFunc);
            GL.GetBoolean(GetPName.DepthWritemask, out bool restoreDepthMask);
            GL.GetDouble(GetPName.DepthClearValue, out double restoreDepthClearValue);
            bool restoreCullFace = GL.IsEnabled(EnableCap.CullFace);
            GL.GetInteger(GetPName.FrontFace, out int restoreFrontFace);
            GL.GetInteger(GetPName.CullFaceMode, out int restoreCullFaceMode);
            bool restoreBlend = GL.IsEnabled(EnableCap.Blend);
            float[] restoreClearColor = new float[4];
            GL.GetFloat(GetPName.ColorClearValue, restoreClearColor);
            IShaderProgram? shader = null;
            string shaderName = "";
            FramebufferErrorCode frameBufferStatus = FramebufferErrorCode.FramebufferComplete;
            ErrorCode glError = ErrorCode.NoError;

            try
            {
                render.CurrentFrameBuffer = frameBuffer;
                frameBufferStatus = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                if (frameBufferStatus != FramebufferErrorCode.FramebufferComplete)
                {
                    return Skip(scene, mode, width, height, $"framebuffer incomplete: {frameBufferStatus}", verboseLogs, out skipReason);
                }

                render.GlViewport(0, 0, framebufferWidth, framebufferHeight);
                render.GLEnableDepthTest();
                GL.DepthFunc(DepthFunction.Lequal);
                render.GLDepthMask(true);
                render.GlDisableCullFace();
                render.GlToggleBlend(true, EnumBlendMode.Standard);
                GL.ClearColor(0.055f, 0.052f, 0.045f, 1f);
                GL.ClearDepth(1.0);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

                ModSystemFpHands? fpHands = _api.ModLoader.GetModSystem<ModSystemFpHands>(true);
                bool classicFirstPerson = mode == VanillaPreviewMode.FirstPerson && fpHands?.fpModeHandShader != null;
                shader = classicFirstPerson ? fpHands!.fpModeHandShader : render.GetEngineShader(EnumShaderProgram.Entityanimated);
                shaderName = classicFirstPerson ? "fpModeHandShader" : "Entityanimated";
                if (shader == null)
                {
                    return Skip(scene, mode, width, height, $"preview shader unavailable: {shaderName}", verboseLogs, out skipReason);
                }

                previous?.Stop();
                shader.Use();

                SetUniform(shader, "extraGlow", 0);
                SetUniform(shader, "rgbaAmbientIn", worldLighting ? render.AmbientColor : new Vec3f(1f, 1f, 1f));
                SetUniform(shader, "rgbaLightIn", GetPreviewLight(render, worldLighting));
                SetUniform(shader, "rgbaFogIn", worldLighting ? render.FogColor : new Vec4f(0f, 0f, 0f, 0f));
                SetUniform(shader, "fogMinIn", worldLighting ? render.FogMin : 0f);
                SetUniform(shader, "fogDensityIn", worldLighting ? render.FogDensity : 0f);
                SetUniform(shader, "renderColor", ColorUtil.WhiteArgbVec);
                SetUniform(shader, "alphaTest", 0.01f);
                SetUniform(shader, "depthOffset", GetFirstPersonDepthOffset(mode, fpHands));
                Vec3f lightPosition = render.ShaderUniforms?.LightPosition3D ?? new Vec3f(0.7071068f, -0.7071068f, 0f);
                SetUniform(shader, "lightPosition", lightPosition);
                SetUniformMatrix(shader, "projectionMatrix", camera.Projection.Values);
                SetUniformMatrix(shader, "viewMatrix", camera.View.Values);
                SetUniformMatrix(shader, "modelMatrix", camera.Model.Values);
                SetUniform(shader, "viewDistance", 1024f);
                SetUniform(shader, "addRenderFlags", 0);
                SetUniform(shader, "windWaveIntensity", 0f);
                SetUniform(shader, "entityId", 0);
                SetUniform(shader, "glitchFlicker", 0);
                SetUniform(shader, "frostAlpha", 0f);
                SetUniform(shader, "globalWarpIntensity", 0f);
                SetUniform(shader, "glitchWaviness", 0f);
                SetUniform(shader, "waterWaveCounter", render.ShaderUniforms?.WaterWaveCounter ?? 0f);
                SetUniform(shader, "glitchEffectStrength", 0f);
                if (shader.UBOs != null && shader.UBOs.TryGetValue("Animation", out UBORef animationUbo))
                {
                    if (scene.Animator.Matrices == null)
                    {
                        return Skip(scene, mode, width, height, "preview animator has no matrices", verboseLogs, out skipReason);
                    }

                    animationUbo.Update(scene.Animator.Matrices, 0, scene.Animator.MaxJointId * 16 * 4);
                }

                LogVerboseScene(scene, mode, meshRef, verboseLogs);
                render.RenderMultiTextureMesh(meshRef, "entityTex", 0);
                glError = GL.GetError();
                shader.Stop();
                shader = null;
                previous?.Use();
                LogVerboseFrame(scene, mode, meshRef, frameBuffer, framebufferWidth, framebufferHeight, shaderName, frameBufferStatus, glError, verboseLogs);
                return frameBuffer.ColorTextureIds[0];
            }
            catch (Exception exception)
            {
                return Skip(scene, mode, width, height, exception.Message, verboseLogs, out skipReason);
            }
            finally
            {
                shader?.Stop();
                render.CurrentFrameBuffer = restoreFrameBuffer;
                render.GlViewport(restoreViewport[0], restoreViewport[1], restoreViewport[2], restoreViewport[3]);
                previous?.Use();
                GL.FrontFace((FrontFaceDirection)restoreFrontFace);
                GL.CullFace((TriangleFace)restoreCullFaceMode);
                if (restoreCullFace) render.GlEnableCullFace();
                else render.GlDisableCullFace();
                GL.ClearDepth(restoreDepthClearValue);
                GL.ClearColor(restoreClearColor[0], restoreClearColor[1], restoreClearColor[2], restoreClearColor[3]);
                GL.DepthFunc((DepthFunction)restoreDepthFunc);
                render.GLDepthMask(restoreDepthMask);
                if (restoreBlend) render.GlToggleBlend(true, EnumBlendMode.Standard);
                else render.GlToggleBlend(false);
                if (restoreDepthTest) render.GLEnableDepthTest();
                else GL.Disable(EnableCap.DepthTest);
            }
        }

        private Vec4f GetPreviewLight(IRenderAPI render, bool worldLighting)
        {
            if (!worldLighting)
            {
                return new Vec4f(1f, 1f, 1f, 1f);
            }

            BlockPos lightPos = _api.World.Player?.Entity?.Pos?.AsBlockPos ?? new BlockPos(0, 0, 0);
            return _api.World.BlockAccessor.GetLightRGBs(lightPos.X, lightPos.Y, lightPos.Z);
        }

        private float GetFirstPersonDepthOffset(VanillaPreviewMode mode, ModSystemFpHands? fpHands)
        {
            if (mode != VanillaPreviewMode.FirstPerson) return 0f;

            if (fpHands != null)
            {
                return PlayerRenderingPatches.GetOffsetAdjusted(fpHands);
            }

            int fieldOfView = Math.Max(1, _api.Settings.Int["fieldOfView"]);
            return PlayerRenderingPatches.DefaultFpHandsOffset + GameMath.Max(0f, fieldOfView / 90f - 1f) / 2f;
        }

        private FrameBufferRef EnsureFrameBuffer(int width, int height)
        {
            if (_frameBuffer != null && !_frameBuffer.Disposed && _frameBuffer.Width == width && _frameBuffer.Height == height)
            {
                return _frameBuffer;
            }

            DestroyFrameBuffer();
            FramebufferAttrs attrs = new("ingamedevtools-vanilla-preview", width, height)
            {
                Attachments =
                [
                    new FramebufferAttrsAttachment
                    {
                        AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                        Texture = new RawTexture
                        {
                            Width = width,
                            Height = height,
                            PixelFormat = EnumTexturePixelFormat.Rgba,
                            PixelInternalFormat = EnumTextureInternalFormat.Rgba8
                        }
                    },
                    new FramebufferAttrsAttachment
                    {
                        AttachmentType = EnumFramebufferAttachment.DepthAttachment,
                        Texture = new RawTexture
                        {
                            Width = width,
                            Height = height,
                            PixelFormat = EnumTexturePixelFormat.DepthComponent,
                            PixelInternalFormat = EnumTextureInternalFormat.DepthComponent32
                        }
                    }
                ]
            };
            _frameBuffer = _api.Render.CreateFrameBuffer(attrs);
            return _frameBuffer;
        }

        private void DestroyFrameBuffer()
        {
            if (_frameBuffer == null || _frameBuffer.Disposed) return;
            _api.Render.DestroyFrameBuffer(_frameBuffer);
            _frameBuffer = null;
        }

        private int Skip(VanillaAnimationPreviewScene scene, VanillaPreviewMode mode, float width, float height, string reason, bool verboseLogs, out string skipReason)
        {
            skipReason = reason;
            if (!verboseLogs) return 0;

            long now = _api.World.ElapsedMilliseconds;
            string key = $"{scene.Key}|{mode}|{(int)width}x{(int)height}|{reason}";
            if (key == _lastSkipLogKey && now - _lastSkipLogAtMs < 1000) return 0;

            _lastSkipLogKey = key;
            _lastSkipLogAtMs = now;
            _api.Logger.VerboseDebug("[InGameDevTools] Vanilla preview skipped: scene={0}, mode={1}, size={2:0}x{3:0}, reason={4}", scene.Key, mode, width, height, reason);
            return 0;
        }

        private void LogVerboseScene(VanillaAnimationPreviewScene scene, VanillaPreviewMode mode, MultiTextureMeshRef meshRef, bool verboseLogs)
        {
            if (!verboseLogs) return;

            string key = $"{scene.Key}|{mode}|scene";
            if (key == _lastSceneLogKey) return;

            _lastSceneLogKey = key;
            _api.Logger.VerboseDebug(
                "[InGameDevTools] Vanilla preview scene: scene={0}, display={1}, mode={2}, status='{3}', meshParts={4}, vertices={5}, indices={6}, textureIds=[{7}], animatorMaxJoint={8}, matrixFloats={9}",
                scene.Key,
                scene.DisplayName,
                mode,
                scene.Status,
                meshRef.meshrefs?.Length ?? 0,
                scene.MeshVerticesCount,
                scene.MeshIndicesCount,
                TextureIdsForLog(meshRef),
                scene.Animator.MaxJointId,
                scene.Animator.Matrices?.Length ?? 0);
        }

        private void LogVerboseFrame(
            VanillaAnimationPreviewScene scene,
            VanillaPreviewMode mode,
            MultiTextureMeshRef meshRef,
            FrameBufferRef frameBuffer,
            int width,
            int height,
            string shaderName,
            FramebufferErrorCode frameBufferStatus,
            ErrorCode glError,
            bool verboseLogs)
        {
            if (!verboseLogs) return;

            string key = $"{scene.Key}|{mode}|{width}x{height}|{shaderName}|frame";
            if (key == _lastFrameLogKey) return;

            _lastFrameLogKey = key;
            _api.Logger.VerboseDebug(
                "[InGameDevTools] Vanilla preview frame: scene={0}, mode={1}, size={2}x{3}, framebuffer={4}, colorTextures=[{5}], depthTexture={6}, shader={7}, meshParts={8}, textureIds=[{9}], animatorMaxJoint={10}, matrixFloats={11}, glError={12}",
                scene.Key,
                mode,
                width,
                height,
                frameBufferStatus,
                frameBuffer.ColorTextureIds == null ? "" : string.Join(",", frameBuffer.ColorTextureIds),
                frameBuffer.DepthTextureId,
                shaderName,
                meshRef.meshrefs?.Length ?? 0,
                TextureIdsForLog(meshRef),
                scene.Animator.MaxJointId,
                scene.Animator.Matrices?.Length ?? 0,
                glError);
        }

        private static string TextureIdsForLog(MultiTextureMeshRef meshRef)
        {
            int[]? textureIds = meshRef.textureids;
            if (textureIds == null || textureIds.Length == 0) return "<none>";

            const int max = 10;
            string result = string.Join(",", textureIds.Take(max));
            return textureIds.Length > max ? $"{result},+{textureIds.Length - max}" : result;
        }

        public void Dispose()
        {
            DestroyFrameBuffer();
        }

        private static void SetTexture(IShaderProgram shader, string name, int textureId, int textureNumber)
        {
            if (shader.HasUniform(name))
            {
                shader.BindTexture2D(name, textureId, textureNumber);
            }
        }

        private static void SetUniform(IShaderProgram shader, string name, int value)
        {
            if (shader.HasUniform(name))
            {
                shader.Uniform(name, value);
            }
        }

        private static void SetUniform(IShaderProgram shader, string name, float value)
        {
            if (shader.HasUniform(name))
            {
                shader.Uniform(name, value);
            }
        }

        private static void SetUniform(IShaderProgram shader, string name, Vec3f value)
        {
            if (shader.HasUniform(name))
            {
                shader.Uniform(name, value);
            }
        }

        private static void SetUniform(IShaderProgram shader, string name, Vec4f value)
        {
            if (shader.HasUniform(name))
            {
                shader.Uniform(name, value);
            }
        }

        private static void SetUniform(IShaderProgram shader, string name, float valueX, float valueY, float valueZ)
        {
            if (shader.HasUniform(name))
            {
                shader.Uniform(name, valueX, valueY, valueZ);
            }
        }

        private static void SetUniform(IShaderProgram shader, string name, float valueX, float valueY, float valueZ, float valueW)
        {
            if (shader.HasUniform(name))
            {
                shader.Uniform(name, valueX, valueY, valueZ, valueW);
            }
        }

        private static void SetUniformMatrix(IShaderProgram shader, string name, float[] matrix)
        {
            if (shader.HasUniform(name))
            {
                shader.UniformMatrix(name, matrix);
            }
        }
    }

    private static class VanillaAnimationDocumentSerializer
    {
        public static string Serialize(VanillaAnimationDocument document)
        {
            JObject token = new()
            {
                ["kind"] = document.Kind.ToString(),
                ["domain"] = document.Domain,
                ["assetPath"] = document.AssetPath
            };

            if (document.ShapeAnimations.Count > 0)
            {
                token["animations"] = new JArray(document.ShapeAnimations.Select(entry =>
                    VanillaAnimationExportService.ToVanillaAnimationToken(entry.Animation, null)));
            }

            if (document.MetadataEntries.Count > 0)
            {
                token["metadata"] = new JArray(document.MetadataEntries.Select(entry =>
                    VanillaAnimationExportService.ToAnimationMetaDataToken(entry.Metadata, null)));
            }

            return JsonConvert.SerializeObject(token, Formatting.None);
        }
    }

    private sealed class VanillaAnimationDocumentSnapshot
    {
        private readonly int[] _animationIndexes;
        private readonly List<VanillaAnimation> _animations;
        private readonly int[] _metadataIndexes;
        private readonly List<AnimationMetaData> _metadata;

        private VanillaAnimationDocumentSnapshot(
            string label,
            string serialized,
            int[] animationIndexes,
            List<VanillaAnimation> animations,
            int[] metadataIndexes,
            List<AnimationMetaData> metadata)
        {
            Label = label;
            Serialized = serialized;
            _animationIndexes = animationIndexes;
            _animations = animations;
            _metadataIndexes = metadataIndexes;
            _metadata = metadata;
        }

        public string Label { get; }
        public string Serialized { get; }

        public static VanillaAnimationDocumentSnapshot FromDocument(VanillaAnimationDocument document, string label)
        {
            return FromIndexes(
                document,
                label,
                Enumerable.Range(0, document.ShapeAnimations.Count).ToArray(),
                Enumerable.Range(0, document.MetadataEntries.Count).ToArray());
        }

        public static VanillaAnimationDocumentSnapshot FromDocument(VanillaAnimationDocument document, string label, VanillaBrowserRow row)
        {
            List<int> animationIndexes = [];
            List<int> metadataIndexes = [];

            if (row.ShapeAnimation?.Document == document)
            {
                animationIndexes.Add(row.ShapeAnimation.Index);
            }

            if (row.MetadataEntry?.Document == document)
            {
                metadataIndexes.Add(row.MetadataEntry.Index);
            }

            if (animationIndexes.Count == 0 && metadataIndexes.Count == 0)
            {
                return FromDocument(document, label);
            }

            return FromIndexes(
                document,
                label,
                animationIndexes.Distinct().OrderBy(index => index).ToArray(),
                metadataIndexes.Distinct().OrderBy(index => index).ToArray());
        }

        private static VanillaAnimationDocumentSnapshot FromIndexes(VanillaAnimationDocument document, string label, int[] animationIndexes, int[] metadataIndexes)
        {
            return new(
                label,
                Serialize(document, animationIndexes, metadataIndexes),
                animationIndexes,
                animationIndexes
                    .Where(index => index >= 0 && index < document.ShapeAnimations.Count)
                    .Select(index => CloneVanillaAnimation(document.ShapeAnimations[index].Animation))
                    .ToList(),
                metadataIndexes,
                metadataIndexes
                    .Where(index => index >= 0 && index < document.MetadataEntries.Count)
                    .Select(index => CloneAnimationMetaData(document.MetadataEntries[index].Metadata))
                    .ToList());
        }

        public bool Matches(VanillaAnimationDocument document)
        {
            return string.Equals(Serialized, Serialize(document, _animationIndexes, _metadataIndexes), StringComparison.Ordinal);
        }

        public VanillaAnimationDocumentSnapshot CaptureCurrent(VanillaAnimationDocument document, string label)
        {
            return FromIndexes(document, label, _animationIndexes, _metadataIndexes);
        }

        public void Restore(VanillaAnimationDocument document)
        {
            int animationCount = Math.Min(_animationIndexes.Length, _animations.Count);
            for (int index = 0; index < animationCount; index++)
            {
                int animationIndex = _animationIndexes[index];
                if (animationIndex < 0 || animationIndex >= document.ShapeAnimations.Count) continue;

                VanillaShapeAnimationEntry entry = document.ShapeAnimations[animationIndex];
                CopyVanillaAnimation(entry.Animation, _animations[index]);
            }

            int metadataCount = Math.Min(_metadataIndexes.Length, _metadata.Count);
            for (int index = 0; index < metadataCount; index++)
            {
                int metadataIndex = _metadataIndexes[index];
                if (metadataIndex < 0 || metadataIndex >= document.MetadataEntries.Count) continue;
                CopyAnimationMetaData(document.MetadataEntries[metadataIndex].Metadata, _metadata[index]);
            }
        }

        private static string Serialize(VanillaAnimationDocument document, int[] animationIndexes, int[] metadataIndexes)
        {
            JObject token = new()
            {
                ["kind"] = document.Kind.ToString(),
                ["domain"] = document.Domain,
                ["assetPath"] = document.AssetPath
            };

            if (animationIndexes.Length > 0)
            {
                JArray animations = [];
                foreach (int index in animationIndexes)
                {
                    if (index < 0 || index >= document.ShapeAnimations.Count) continue;
                    animations.Add(new JObject
                    {
                        ["index"] = index,
                        ["value"] = VanillaAnimationExportService.ToVanillaAnimationToken(document.ShapeAnimations[index].Animation, null)
                    });
                }

                token["animations"] = animations;
            }

            if (metadataIndexes.Length > 0)
            {
                JArray metadata = [];
                foreach (int index in metadataIndexes)
                {
                    if (index < 0 || index >= document.MetadataEntries.Count) continue;
                    metadata.Add(new JObject
                    {
                        ["index"] = index,
                        ["value"] = VanillaAnimationExportService.ToAnimationMetaDataToken(document.MetadataEntries[index].Metadata, null)
                    });
                }

                token["metadata"] = metadata;
            }

            return JsonConvert.SerializeObject(token, Formatting.None);
        }
    }

    private sealed class VanillaAnimationEditorHistory
    {
        private const int MaxEntriesPerDocument = 100;

        private readonly Dictionary<string, List<VanillaAnimationDocumentSnapshot>> _undo = new();
        private readonly Dictionary<string, List<VanillaAnimationDocumentSnapshot>> _redo = new();
        private PendingVanillaEdit? _pendingEdit;

        public VanillaAnimationDocumentSnapshot Capture(VanillaAnimationDocument document, string label) => VanillaAnimationDocumentSnapshot.FromDocument(document, label);
        public VanillaAnimationDocumentSnapshot Capture(VanillaAnimationDocument document, string label, VanillaBrowserRow row) => VanillaAnimationDocumentSnapshot.FromDocument(document, label, row);
        public int UndoCount(VanillaAnimationDocument document) => GetStack(_undo, document.HistoryKey).Count;
        public int RedoCount(VanillaAnimationDocument document) => GetStack(_redo, document.HistoryKey).Count;
        public bool HasPendingEdit(VanillaAnimationDocument document) => _pendingEdit?.HistoryKey == document.HistoryKey;

        public bool TryGetPendingDocumentKey(out string? historyKey)
        {
            historyKey = _pendingEdit?.HistoryKey;
            return historyKey != null;
        }

        public void BeginEdit(VanillaAnimationDocument document, VanillaAnimationDocumentSnapshot before)
        {
            if (_pendingEdit?.HistoryKey == document.HistoryKey) return;
            if (_pendingEdit != null) CancelPendingEdit();
            _pendingEdit = new PendingVanillaEdit(document.HistoryKey, before);
        }

        public bool CommitEdit(VanillaAnimationDocument document)
        {
            if (_pendingEdit?.HistoryKey != document.HistoryKey) return false;

            VanillaAnimationDocumentSnapshot entry = _pendingEdit.Before;
            _pendingEdit = null;

            if (entry.Matches(document)) return false;

            Push(_undo, document.HistoryKey, entry);
            GetStack(_redo, document.HistoryKey).Clear();
            return true;
        }

        public void CancelPendingEdit()
        {
            _pendingEdit = null;
        }

        public bool RecordSnapshot(VanillaAnimationDocument document, VanillaAnimationDocumentSnapshot before)
        {
            if (before.Matches(document)) return false;

            List<VanillaAnimationDocumentSnapshot> undo = GetStack(_undo, document.HistoryKey);
            if (undo.Count > 0 && undo[^1].Serialized == before.Serialized) return false;

            Push(_undo, document.HistoryKey, before);
            GetStack(_redo, document.HistoryKey).Clear();
            return true;
        }

        public bool Undo(VanillaAnimationDocument document, out string status)
        {
            status = "";
            List<VanillaAnimationDocumentSnapshot> undo = GetStack(_undo, document.HistoryKey);
            if (undo.Count == 0)
            {
                status = "Nothing to undo.";
                return false;
            }

            VanillaAnimationDocumentSnapshot target = Pop(undo);
            Push(_redo, document.HistoryKey, target.CaptureCurrent(document, "Redo"));
            target.Restore(document);
            document.UpdateDirtyState();
            status = $"Undid {target.Label}.";
            return true;
        }

        public bool Redo(VanillaAnimationDocument document, out string status)
        {
            status = "";
            List<VanillaAnimationDocumentSnapshot> redo = GetStack(_redo, document.HistoryKey);
            if (redo.Count == 0)
            {
                status = "Nothing to redo.";
                return false;
            }

            VanillaAnimationDocumentSnapshot target = Pop(redo);
            Push(_undo, document.HistoryKey, target.CaptureCurrent(document, "Undo"));
            target.Restore(document);
            document.UpdateDirtyState();
            status = $"Redid {target.Label}.";
            return true;
        }

        public void Clear(VanillaAnimationDocument document)
        {
            GetStack(_undo, document.HistoryKey).Clear();
            GetStack(_redo, document.HistoryKey).Clear();
            if (_pendingEdit?.HistoryKey == document.HistoryKey) _pendingEdit = null;
        }

        public void ClearAll()
        {
            _undo.Clear();
            _redo.Clear();
            _pendingEdit = null;
        }

        private static void Push(Dictionary<string, List<VanillaAnimationDocumentSnapshot>> stacks, string historyKey, VanillaAnimationDocumentSnapshot entry)
        {
            List<VanillaAnimationDocumentSnapshot> stack = GetStack(stacks, historyKey);
            stack.Add(entry);
            if (stack.Count > MaxEntriesPerDocument)
            {
                stack.RemoveRange(0, stack.Count - MaxEntriesPerDocument);
            }
        }

        private static VanillaAnimationDocumentSnapshot Pop(List<VanillaAnimationDocumentSnapshot> stack)
        {
            int index = stack.Count - 1;
            VanillaAnimationDocumentSnapshot entry = stack[index];
            stack.RemoveAt(index);
            return entry;
        }

        private static List<VanillaAnimationDocumentSnapshot> GetStack(Dictionary<string, List<VanillaAnimationDocumentSnapshot>> stacks, string historyKey)
        {
            if (!stacks.TryGetValue(historyKey, out List<VanillaAnimationDocumentSnapshot>? stack))
            {
                stack = [];
                stacks[historyKey] = stack;
            }

            return stack;
        }

        private sealed record PendingVanillaEdit(string HistoryKey, VanillaAnimationDocumentSnapshot Before);
    }

    private sealed class VanillaAnimationExportService
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        public string Export(VanillaAnimationDocument document, bool overwrite)
        {
            try
            {
                string sourceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ModsNeedUpdate");
                string exportRoot = Path.Combine(sourceRoot, "_ingamedevtools_exports");
                string relativePath = Path.Combine("assets", document.Domain, document.AssetPath.Replace('/', Path.DirectorySeparatorChar));
                string outputPath = Path.Combine(exportRoot, relativePath);

                if (File.Exists(outputPath) && !overwrite)
                {
                    return $"Export exists: {outputPath}. Enable overwrite exports to replace it.";
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                string json = document.Kind == VanillaDocumentKind.Shape
                    ? BuildShapeExportJson(document)
                    : BuildEntityMetadataExportJson(document);

                File.WriteAllText(outputPath, json);
                WriteManifest(outputPath, document);
                document.MarkClean();
                return $"Exported {document.DisplayPath} to {outputPath}.";
            }
            catch (Exception exception)
            {
                return $"Export failed for {document.DisplayPath}: {exception.Message}";
            }
        }

        private static string BuildShapeExportJson(VanillaAnimationDocument document)
        {
            JObject json = document.SourceJson?.DeepClone() as JObject ?? JObject.FromObject(document.Shape!, JsonSerializer.Create(JsonSettings));
            json["animations"] = new JArray(document.ShapeAnimations.Select(entry => ToVanillaAnimationToken(entry.Animation, entry.SourceToken)));
            return JsonConvert.SerializeObject(RemoveEditorPrivateProperties(json), Formatting.Indented, JsonSettings);
        }

        private static string BuildEntityMetadataExportJson(VanillaAnimationDocument document)
        {
            JObject json = document.SourceJson?.DeepClone() as JObject ?? new JObject
            {
                ["code"] = document.EntityCode ?? document.DisplayPath
            };

            JObject client = json["client"] as JObject ?? new JObject();
            json["client"] = client;
            client["animations"] = new JArray(document.MetadataEntries.Select(entry => ToAnimationMetaDataToken(entry.Metadata, entry.SourceToken)));

            return JsonConvert.SerializeObject(RemoveEditorPrivateProperties(json), Formatting.Indented, JsonSettings);
        }

        public static JToken ToVanillaAnimationToken(VanillaAnimation animation, JToken? sourceToken)
        {
            JObject token = sourceToken?.DeepClone() as JObject ?? new JObject();
            token.Remove("quantityFrames");
            token.Remove("quantityframes");
            token.Remove("keyFrames");
            token.Remove("keyframes");

            token["quantityframes"] = animation.QuantityFrames;
            if (!string.IsNullOrWhiteSpace(animation.Name)) token["name"] = animation.Name;
            token["code"] = animation.Code ?? animation.Name ?? "";
            token["version"] = animation.Version;
            token["easeAnimationSpeed"] = animation.EaseAnimationSpeed;
            token["onActivityStopped"] = animation.OnActivityStopped.ToString();
            token["onAnimationEnd"] = animation.OnAnimationEnd.ToString();
            token["keyframes"] = new JArray((animation.KeyFrames ?? []).Select(ToVanillaKeyFrameToken));
            return token;
        }

        public static JToken ToVanillaKeyFrameToken(AnimationKeyFrame keyFrame)
        {
            JObject token = new()
            {
                ["frame"] = keyFrame.Frame
            };

            JObject elements = new();
            foreach ((string name, AnimationKeyFrameElement element) in (keyFrame.Elements ?? new()).OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                elements[name] = ToVanillaElementToken(element);
            }
            token["elements"] = elements;
            return token;
        }

        public static JToken ToVanillaElementToken(AnimationKeyFrameElement element)
        {
            JObject token = new();
            AddNullable(token, "offsetX", element.OffsetX);
            AddNullable(token, "offsetY", element.OffsetY);
            AddNullable(token, "offsetZ", element.OffsetZ);
            AddNullable(token, "stretchX", element.StretchX);
            AddNullable(token, "stretchY", element.StretchY);
            AddNullable(token, "stretchZ", element.StretchZ);
            AddNullable(token, "rotationX", element.RotationX);
            AddNullable(token, "rotationY", element.RotationY);
            AddNullable(token, "rotationZ", element.RotationZ);
            AddNullable(token, "originX", element.OriginX);
            AddNullable(token, "originY", element.OriginY);
            AddNullable(token, "originZ", element.OriginZ);
            if (element.RotShortestDistanceX) token["rotShortestDistanceX"] = true;
            if (element.RotShortestDistanceY) token["rotShortestDistanceY"] = true;
            if (element.RotShortestDistanceZ) token["rotShortestDistanceZ"] = true;
            return token;
        }

        public static JToken ToAnimationMetaDataToken(AnimationMetaData metadata, JToken? sourceToken)
        {
            JObject token = sourceToken?.DeepClone() as JObject ?? new JObject();
            token["code"] = metadata.Code ?? "";
            token["animation"] = metadata.Animation ?? "";
            token["weight"] = metadata.Weight;
            token["animationSpeed"] = metadata.AnimationSpeed;
            token["mulWithWalkSpeed"] = metadata.MulWithWalkSpeed;
            token["weightCapFactor"] = metadata.WeightCapFactor;
            token["easeInSpeed"] = metadata.EaseInSpeed;
            token["easeOutSpeed"] = metadata.EaseOutSpeed;
            token["blendMode"] = metadata.BlendMode.ToString();
            token["supressDefaultAnimation"] = metadata.SupressDefaultAnimation;
            token["holdEyePosAfterEasein"] = metadata.HoldEyePosAfterEasein;
            token["clientSide"] = metadata.ClientSide;
            token["withFpVariant"] = metadata.WithFpVariant;
            token["adjustCollisionBox"] = metadata.AdjustCollisionBox;

            token["elementWeight"] = JObject.FromObject(metadata.ElementWeight ?? new Dictionary<string, float>(), JsonSerializer.Create(JsonSettings));
            JObject blendModes = new();
            foreach ((string element, EnumAnimationBlendMode mode) in metadata.ElementBlendMode ?? new Dictionary<string, EnumAnimationBlendMode>())
            {
                blendModes[element] = mode.ToString();
            }
            token["elementBlendMode"] = blendModes;

            if (metadata.AnimationSounds != null && metadata.AnimationSounds.Length > 0)
            {
                JArray? sourceSounds = sourceToken?["animationSounds"] as JArray;
                JArray sounds = new();
                for (int index = 0; index < metadata.AnimationSounds.Length; index++)
                {
                    sounds.Add(ToAnimationSoundToken(metadata.AnimationSounds[index], sourceSounds != null && index < sourceSounds.Count ? sourceSounds[index] : null));
                }

                token["animationSounds"] = sounds;
            }
            else
            {
                token.Remove("animationSounds");
            }

            return token;
        }

        public static JToken ToAnimationSoundToken(AnimationSound sound, JToken? sourceToken)
        {
            JObject token = sourceToken?.DeepClone() as JObject ?? new JObject();
            token["frame"] = sound.Frame;
            token["chance"] = sound.Chance;
            token["looping"] = sound.Looping;
            token.Remove("path");
            if (sound.Attributes.Location != null) token["location"] = sound.Attributes.Location.ToString();
            token["range"] = sound.Attributes.Range;
            return token;
        }

        private static void AddNullable(JObject token, string property, double? value)
        {
            if (value.HasValue)
            {
                token[property] = value.Value;
            }
        }

        private static JObject RemoveEditorPrivateProperties(JObject json)
        {
            json.Remove("_assetPath");
            return json;
        }

        private static void WriteManifest(string outputPath, VanillaAnimationDocument document)
        {
            JObject manifest = new()
            {
                ["exportedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["source"] = document.DisplayPath,
                ["kind"] = document.Kind.ToString(),
                ["export"] = outputPath
            };

            File.WriteAllText(outputPath + ".ingamedevtools-manifest.json", manifest.ToString(Formatting.Indented));
        }
    }

    private static JObject? TryParseObject(string text)
    {
        try
        {
            return JObject.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    private static JToken? GetSourceArrayElement(JObject? source, string property, int index)
    {
        if (source?[property] is not JArray array || index < 0 || index >= array.Count) return null;
        return array[index].DeepClone();
    }

    private static JToken? GetNestedArrayElement(JObject? source, string[] path, int index)
    {
        JToken? current = source;
        for (int i = 0; i < path.Length - 1; i++)
        {
            current = current?[path[i]];
        }

        if (current?[path[^1]] is not JArray array || index < 0 || index >= array.Count) return null;
        return array[index].DeepClone();
    }

    private static string NormalizeAssetCode(string code, string? fallbackDomain)
    {
        if (code.Contains(':')) return code;
        return $"{fallbackDomain ?? "game"}:{code}";
    }

    private static string EnsureJsonPath(string path)
    {
        path = path.Replace('\\', '/');
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : $"{path}.json";
    }
}
#endif
