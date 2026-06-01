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
    private const double VanillaIkSolveTolerance = 0.01;
    private const double VanillaIkSolveImprovementEpsilon = 0.001;
    private const int VanillaIkAutoMaxChainLength = 4;
    private const int VanillaIkAutoAbsoluteMaxChainLength = 8;
    private const int VanillaIkAutoMaxAdjustmentBones = 4;
    private const int VanillaIkAutoHubChildThreshold = 3;

    private static readonly string[] VanillaIkTrunkNameTokens =
    [
        "root",
        "body",
        "torso",
        "trunk",
        "lowertorso",
        "uppertorso",
        "pelvis",
        "hip",
        "hips",
        "spine",
        "neck",
        "chest",
        "abdomen",
        "waist"
    ];

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
    private VanillaEntitySelectorMode _vanillaEntitySelectorMode = VanillaEntitySelectorMode.Grouped;
    private bool _vanillaShowHiddenEntities;
    private bool _vanillaSingleVariantEdit;
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
    private double _vanillaViewportGizmoDragRingScreenSign = -1.0;
    private double _vanillaViewportGizmoDragStartValue;
    private string _vanillaViewportGizmoDragRowKey = "";
    private int _vanillaViewportGizmoDragKeyFrameIndex = -1;
    private string _vanillaViewportGizmoDragElementName = "";
    private float _vanillaRotationStepDegrees = 1f;
    private string _vanillaNewAnimationCode = "new-animation";
    private string _vanillaNewAnimationName = "";
    private int _vanillaNewAnimationFrames = 30;
    private bool _vanillaNewAnimationMetadata = true;
    private readonly Dictionary<string, string> _vanillaSymmetryPairOverrides = new(StringComparer.OrdinalIgnoreCase);
    private bool _vanillaLiveSymmetryEnabled;
    private VanillaLiveSymmetryMode _vanillaLiveSymmetryMode = VanillaLiveSymmetryMode.HalfCycle;
    private VanillaLiveSymmetryDriver _vanillaLiveSymmetryDriver = VanillaLiveSymmetryDriver.SelectedElement;
    private int _vanillaLiveSymmetryPhaseFrames = -1;
    private bool _vanillaLiveSymmetryPropagating;
    private bool _vanillaShowLiveSymmetryGhost = true;
    private float _vanillaLiveSymmetryGhostOpacity = 0.35f;
    private bool _vanillaOnionSkinEnabled;
    private bool _vanillaOnionSkinPrevious = true;
    private bool _vanillaOnionSkinNext = true;
    private float _vanillaOnionSkinOpacity = 0.22f;
    private bool _vanillaIkFollowMove;
    private VanillaIkChainMode _vanillaIkMode = VanillaIkChainMode.AutoLimb;
    private readonly List<string> _vanillaIkChainElementNames = [];
    private bool _vanillaIkHasTarget;
    private float _vanillaIkTargetX;
    private float _vanillaIkTargetY;
    private float _vanillaIkTargetZ;
    private int _vanillaIkAutoRootExtraBones;
    private int _vanillaIkAutoEndExtraBones;
    private string _vanillaIkAutoAdjustmentElementName = "";
    private bool _vanillaIkDragActive;
    private string _vanillaIkDragRowKey = "";
    private int _vanillaIkDragKeyFrameIndex = -1;
    private string _vanillaIkDragElementName = "";
    private VanillaIkCcdCache? _vanillaIkDragCache;

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

        if (_vanillaIndex.HasSelectedEntity && ImGui.Button("Reload selected entity##vanilla", new NVector2(-1, 0)))
        {
            CommitPendingVanillaHistory();
            _vanillaIndex.ReloadSelectedEntity(_api, ShouldVanillaUseGroupEdit(_vanillaIndex.SelectedEntityOption));
            ResetVanillaEntitySelectionState();
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
        _vanillaLastEditedDocumentKey = "";
        _vanillaSelection.Clear();
        DisposeVanillaPreviewScene();
        _vanillaStatus = "Preview not loaded. Select an animation and press Load preview when ready.";
    }

    private IEnumerable<string> GetVanillaDomains()
    {
        return _vanillaIndex.AllEntityDomains
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

        bool onionSkins = _vanillaOnionSkinEnabled;
        ImGui.SameLine();
        if (ImGui.Checkbox("Onion skins##vanilla-preview-onion", ref onionSkins))
        {
            _vanillaOnionSkinEnabled = onionSkins;
            _vanillaStatus = _vanillaOnionSkinEnabled
                ? "Viewport onion skins enabled."
                : "Viewport onion skins disabled.";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Shows neighboring vanilla shape keyframes as translucent ghosts in the preview viewport.");
        }

        if (_vanillaOnionSkinEnabled)
        {
            ImGui.SameLine();
            ImGui.Checkbox("Prev##vanilla-preview-onion-prev", ref _vanillaOnionSkinPrevious);
            ImGui.SameLine();
            ImGui.Checkbox("Next##vanilla-preview-onion-next", ref _vanillaOnionSkinNext);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(92);
            if (ImGui.SliderFloat("Opacity##vanilla-preview-onion-opacity", ref _vanillaOnionSkinOpacity, 0.05f, 0.6f, "%.2f"))
            {
                _vanillaOnionSkinOpacity = Math.Clamp(_vanillaOnionSkinOpacity, 0.05f, 0.6f);
            }
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
        uint grid = ImGui.ColorConvertFloat4ToU32(new NVector4(0.28f, 0.27f, 0.22f, 0.42f));
        uint gridMajor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.45f, 0.42f, 0.33f, 0.72f));
        uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.86f, 0.82f, 0.72f, 1f));
        drawList.AddRectFilled(min, max, background, 4f);

        VanillaPreviewMode effectiveMode = GetVanillaEffectivePreviewMode(scene);
        float viewportWidth = Math.Max(1f, max.X - min.X);
        float viewportHeight = Math.Max(1f, max.Y - min.Y);
        VanillaPreviewGhost[] ghosts = BuildVanillaViewportGhosts(row, scene, effectiveMode, out string ghostOverlayStatus);

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
            ghosts,
            _vanillaVerbosePreviewLogs,
            out string? previewSkipReason);
        if (textureId > 0)
        {
            drawList.AddImage(new IntPtr(textureId), min, max, new NVector2(0f, 1f), new NVector2(1f, 0f));
        }
        else if (!string.IsNullOrWhiteSpace(previewSkipReason))
        {
            uint warning = ImGui.ColorConvertFloat4ToU32(new NVector4(0.95f, 0.72f, 0.43f, 1f));
            float skipY = string.IsNullOrWhiteSpace(ghostOverlayStatus) ? 54f : 70f;
            drawList.AddText(new NVector2(min.X + 12f, min.Y + skipY), warning, $"Preview skipped: {previewSkipReason}");
        }

        if (effectiveMode == VanillaPreviewMode.Orbit)
        {
            VanillaPreviewCameraState camera = BuildVanillaPreviewCamera(scene, viewportWidth, viewportHeight, _vanillaViewportYaw, _vanillaViewportPitch, _vanillaViewportZoom, _vanillaViewportPanX, _vanillaViewportPanY, effectiveMode);
            drawList.PushClipRect(min, max, true);
            DrawVanillaViewportGrid(drawList, camera, scene, min, viewportWidth, viewportHeight, grid, gridMajor);
            drawList.PopClipRect();
        }

        drawList.AddRect(min, max, border, 4f);
        drawList.AddText(new NVector2(min.X + 12f, min.Y + 10f), text, $"Preview: {scene.DisplayName}");
        drawList.AddText(new NVector2(min.X + 12f, min.Y + 30f), text, GetVanillaViewportHelpText(effectiveMode, scene));
        if (!string.IsNullOrWhiteSpace(ghostOverlayStatus))
        {
            uint ghostText = ImGui.ColorConvertFloat4ToU32(ghosts.Length > 0
                ? new NVector4(0.54f, 0.86f, 1f, 1f)
                : new NVector4(0.95f, 0.72f, 0.43f, 1f));
            drawList.AddText(new NVector2(min.X + 12f, min.Y + 50f), ghostText, ghostOverlayStatus);
        }

        if (effectiveMode == VanillaPreviewMode.Orbit)
        {
            bool suppressBodyPick = DrawVanillaViewportGizmo(row, scene, drawList, min, max, hovered);
            DrawVanillaViewportElementPicker(row, scene, drawList, min, max, hovered, suppressBodyPick);
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

    private static void DrawVanillaViewportGrid(ImDrawListPtr drawList, VanillaPreviewCameraState camera, VanillaAnimationPreviewScene scene, NVector2 min, float width, float height, uint color, uint majorColor)
    {
        float modelExtent = Math.Max(Math.Max(scene.ModelWidth, scene.ModelHeight), scene.ModelDepth);
        float centerExtent = Math.Max(Math.Max(Math.Abs(scene.ModelCenterX), Math.Abs(scene.ModelCenterY)), Math.Abs(scene.ModelCenterZ));
        int extent = Math.Clamp((int)Math.Ceiling(Math.Max(modelExtent * 1.5f, centerExtent + 2f)), 4, 16);

        DrawVanillaViewportGridPlane(drawList, camera, min, width, height, NVector3.UnitX, NVector3.UnitZ, extent, color, majorColor);
        DrawVanillaViewportGridPlane(drawList, camera, min, width, height, NVector3.UnitX, NVector3.UnitY, extent, color, majorColor);
        DrawVanillaViewportGridPlane(drawList, camera, min, width, height, NVector3.UnitZ, NVector3.UnitY, extent, color, majorColor);
    }

    private static void DrawVanillaViewportGridPlane(ImDrawListPtr drawList, VanillaPreviewCameraState camera, NVector2 min, float width, float height, NVector3 axisA, NVector3 axisB, int extent, uint color, uint majorColor)
    {
        for (int i = -extent; i <= extent; i++)
        {
            uint lineColor = i == 0 ? majorColor : color;
            float thickness = i == 0 ? 1.8f : 1f;
            DrawVanillaViewportGridLine(drawList, camera, min, width, height, axisA * -extent + axisB * i, axisA * extent + axisB * i, lineColor, thickness);
            DrawVanillaViewportGridLine(drawList, camera, min, width, height, axisA * i + axisB * -extent, axisA * i + axisB * extent, lineColor, thickness);
        }
    }

    private static void DrawVanillaViewportGridLine(ImDrawListPtr drawList, VanillaPreviewCameraState camera, NVector2 min, float width, float height, NVector3 start, NVector3 end, uint color, float thickness)
    {
        int segments = Math.Max(1, (int)Math.Ceiling((end - start).Length()));
        NVector3 step = (end - start) / segments;
        NVector3 previousPoint = start;
        bool previousVisible = ProjectVanillaPreviewPoint(camera.Model, camera, previousPoint, min, width, height, out NVector2 previousScreen);

        for (int segment = 1; segment <= segments; segment++)
        {
            NVector3 point = start + step * segment;
            bool visible = ProjectVanillaPreviewPoint(camera.Model, camera, point, min, width, height, out NVector2 screen);
            if (previousVisible && visible)
            {
                DrawVanillaViewportLine(drawList, previousScreen, screen, color, thickness);
            }

            previousPoint = point;
            previousScreen = screen;
            previousVisible = visible;
        }
    }

    private VanillaPreviewGhost[] BuildVanillaViewportGhosts(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, VanillaPreviewMode effectiveMode, out string overlayStatus)
    {
        overlayStatus = "";
        if (effectiveMode != VanillaPreviewMode.Orbit)
        {
            if (_vanillaOnionSkinEnabled || (_vanillaLiveSymmetryEnabled && _vanillaShowLiveSymmetryGhost))
            {
                overlayStatus = "Ghost overlays hidden: switch to Orbit mode.";
            }

            return [];
        }

        List<VanillaPreviewGhost> ghosts = [];
        List<string> hiddenReasons = [];
        AddVanillaOnionSkinGhosts(row, scene, ghosts, out string onionSkinStatus);
        if (!string.IsNullOrWhiteSpace(onionSkinStatus))
        {
            hiddenReasons.Add(onionSkinStatus);
        }

        VanillaPreviewGhost symmetry = BuildVanillaLiveSymmetryGhost(row, scene, effectiveMode, out string symmetryStatus);
        if (symmetry.Enabled) ghosts.Add(symmetry);
        else if (!string.IsNullOrWhiteSpace(symmetryStatus))
        {
            hiddenReasons.Add(symmetryStatus);
        }

        overlayStatus = ghosts.Count > 0
            ? GetVanillaViewportGhostStatus(ghosts)
            : string.Join(" ", hiddenReasons);
        return ghosts.ToArray();
    }

    private void AddVanillaOnionSkinGhosts(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, List<VanillaPreviewGhost> ghosts, out string hiddenReason)
    {
        hiddenReason = "";
        if (!_vanillaOnionSkinEnabled)
        {
            return;
        }

        if (row.ShapeAnimation == null)
        {
            hiddenReason = "Onion skins hidden: select a shape animation.";
            return;
        }

        if (scene.Playing)
        {
            hiddenReason = "Onion skins hidden while playback is running.";
            return;
        }

        VanillaAnimation animation = row.ShapeAnimation.Animation;
        if (animation.KeyFrames == null || animation.KeyFrames.Length <= 1)
        {
            hiddenReason = "Onion skins hidden: this animation has no neighboring keyframes.";
            return;
        }

        if (!_vanillaOnionSkinPrevious && !_vanillaOnionSkinNext)
        {
            hiddenReason = "Onion skins hidden: previous and next are disabled.";
            return;
        }

        int keyFrameIndex = Math.Clamp(_vanillaSelection.KeyFrameIndex, 0, animation.KeyFrames.Length - 1);
        float opacity = Math.Clamp(_vanillaOnionSkinOpacity, 0.05f, 0.6f);
        int initialCount = ghosts.Count;
        if (_vanillaOnionSkinPrevious && keyFrameIndex > 0)
        {
            float frame = animation.KeyFrames[keyFrameIndex - 1].Frame;
            if (!IsSamePreviewFrame(frame, scene.CurrentFrame))
            {
                ghosts.Add(new VanillaPreviewGhost(true, frame, opacity, 1.0f, 0.62f, 0.28f, $"prev {frame:0}"));
            }
        }

        if (_vanillaOnionSkinNext && keyFrameIndex < animation.KeyFrames.Length - 1)
        {
            float frame = animation.KeyFrames[keyFrameIndex + 1].Frame;
            if (!IsSamePreviewFrame(frame, scene.CurrentFrame))
            {
                ghosts.Add(new VanillaPreviewGhost(true, frame, opacity, 0.35f, 1.0f, 0.55f, $"next {frame:0}"));
            }
        }

        if (ghosts.Count == initialCount)
        {
            hiddenReason = "Onion skins hidden: no enabled neighboring keyframe differs from the current frame.";
        }
    }

    private VanillaPreviewGhost BuildVanillaLiveSymmetryGhost(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, VanillaPreviewMode effectiveMode, out string hiddenReason)
    {
        hiddenReason = "";
        if (!_vanillaLiveSymmetryEnabled)
        {
            return VanillaPreviewGhost.Disabled;
        }

        if (!_vanillaShowLiveSymmetryGhost)
        {
            hiddenReason = "Symmetry ghost hidden: ghost display is disabled.";
            return VanillaPreviewGhost.Disabled;
        }

        if (scene.Playing)
        {
            hiddenReason = "Symmetry ghost hidden while playback is running.";
            return VanillaPreviewGhost.Disabled;
        }

        if (effectiveMode != VanillaPreviewMode.Orbit)
        {
            hiddenReason = "Symmetry ghost hidden: switch to Orbit mode.";
            return VanillaPreviewGhost.Disabled;
        }

        if (row.ShapeAnimation == null)
        {
            hiddenReason = "Symmetry ghost hidden: select a shape animation.";
            return VanillaPreviewGhost.Disabled;
        }

        VanillaAnimation animation = row.ShapeAnimation.Animation;
        if (_vanillaLiveSymmetryMode == VanillaLiveSymmetryMode.InPlace)
        {
            hiddenReason = "Symmetry ghost hidden: in-place mode mirrors on the current frame.";
            return VanillaPreviewGhost.Disabled;
        }

        if (animation.QuantityFrames <= 1 ||
            animation.KeyFrames == null ||
            animation.KeyFrames.Length == 0)
        {
            hiddenReason = "Symmetry ghost hidden: half-cycle mode needs multiple frames.";
            return VanillaPreviewGhost.Disabled;
        }

        int phaseFrames = GetVanillaLiveSymmetryPhaseFrames(animation);
        if (phaseFrames <= 0)
        {
            hiddenReason = "Symmetry ghost hidden: phase is zero.";
            return VanillaPreviewGhost.Disabled;
        }

        int sourceFrame = (int)Math.Round(scene.CurrentFrame, MidpointRounding.AwayFromZero);
        int ghostFrame = GetVanillaPhaseTargetFrame(animation, sourceFrame, phaseFrames);
        if (ghostFrame == sourceFrame)
        {
            hiddenReason = "Symmetry ghost hidden: phase resolves to the current frame.";
            return VanillaPreviewGhost.Disabled;
        }

        return new VanillaPreviewGhost(true, ghostFrame, Math.Clamp(_vanillaLiveSymmetryGhostOpacity, 0.05f, 0.8f), 0.42f, 0.82f, 1f, $"sym {ghostFrame:0}");
    }

    private static bool IsSamePreviewFrame(float left, float right)
    {
        return Math.Abs(left - right) < 0.001f;
    }

    private static string GetVanillaViewportGhostStatus(IReadOnlyList<VanillaPreviewGhost> ghosts)
    {
        return ghosts.Count == 1
            ? $"Ghost: {ghosts[0].Label}"
            : $"Ghosts: {string.Join(", ", ghosts.Select(ghost => ghost.Label))}";
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
            ? "LMB picks body parts. RMB orbits. MMB or Shift+RMB pans. Mouse wheel zooms."
            : "First person: RMB adjusts preview yaw/pitch. MMB or Shift+RMB offsets. Mouse wheel changes hand FOV.";
    }

    private bool DrawVanillaViewportGizmo(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, ImDrawListPtr drawList, NVector2 min, NVector2 max, bool hovered)
    {
        if (GizmoMode == TransformGizmoMode.None) return false;
        if (!TryGetVanillaViewportGizmoTarget(row, out VanillaShapeAnimationEntry? entry, out VanillaAnimation? animation, out AnimationKeyFrame? keyFrame, out AnimationKeyFrameElement? element)) return false;
        if (!TryGetVanillaGizmoProjection(scene, element, _vanillaSelection.ElementName, min, max, out VanillaGizmoProjection projection)) return false;

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
            _vanillaViewportGizmoDragRingScreenSign = GizmoMode == TransformGizmoMode.Rotate
                ? GetVanillaViewportGizmoRingScreenSign(projection, hoveredAxis)
                : -1.0;
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
                ApplyVanillaViewportGizmoDrag(row, entry, keyFrame, element, _vanillaViewportGizmoDragMode, _vanillaViewportGizmoDragAxis, _vanillaViewportGizmoDragVector, projection.Scale);
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
        return hoveredAxis != TransformGizmoAxis.None || _vanillaViewportGizmoDragAxis != TransformGizmoAxis.None;
    }

    private void DrawVanillaViewportElementPicker(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, ImDrawListPtr drawList, NVector2 min, NVector2 max, bool hovered, bool suppressClick)
    {
        if (!hovered || _vanillaViewportGizmoDragAxis != TransformGizmoAxis.None) return;
        if (!TryPickVanillaViewportElement(scene, min, max, ImGui.GetMousePos(), out VanillaViewportElementHit hit)) return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        drawList.PushClipRect(min, max, true);
        bool manualChainHit = _vanillaIkMode == VanillaIkChainMode.ManualOverride && ContainsVanillaIkChainElement(hit.ElementName);
        uint boundsColor = manualChainHit
            ? ImGui.ColorConvertFloat4ToU32(new NVector4(0.42f, 0.86f, 1f, 0.95f))
            : ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.86f, 0.36f, 0.92f));
        uint labelColor = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f));
        DrawVanillaViewportBoxBounds(drawList, hit.BoundsCorners, boundsColor, 2.2f);
        string action = _vanillaIkMode == VanillaIkChainMode.ManualOverride ? "manual IK" : "select";
        drawList.AddText(hit.Center + new NVector2(8f, -18f), labelColor, $"{hit.ElementName} ({action})");
        drawList.PopClipRect();

        if (suppressClick || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;

        _vanillaSelection.ElementName = hit.ElementName;
        if (_vanillaIkMode == VanillaIkChainMode.ManualOverride)
        {
            ToggleVanillaIkChainElement(hit.ElementName);
        }
        else
        {
            ClearVanillaViewportGizmoDrag();
            _vanillaStatus = $"Selected {hit.ElementName}.";
        }
    }

    private bool TryPickVanillaViewportElement(VanillaAnimationPreviewScene scene, NVector2 min, NVector2 max, NVector2 mouse, out VanillaViewportElementHit hit)
    {
        hit = default;
        float width = Math.Max(1f, max.X - min.X);
        float height = Math.Max(1f, max.Y - min.Y);
        VanillaPreviewCameraState camera = BuildVanillaPreviewCamera(scene, width, height, _vanillaViewportYaw, _vanillaViewportPitch, _vanillaViewportZoom, _vanillaViewportPanX, _vanillaViewportPanY, VanillaPreviewMode.Orbit);
        bool found = false;

        foreach (ElementPose root in scene.Animator.RootPoses ?? [])
        {
            CollectVanillaViewportElementHits(root, camera, min, width, height, mouse, depth: 0, ref found, ref hit);
        }

        return found;
    }

    private static void CollectVanillaViewportElementHits(ElementPose pose, VanillaPreviewCameraState camera, NVector2 min, float width, float height, NVector2 mouse, int depth, ref bool found, ref VanillaViewportElementHit best)
    {
        if (TryBuildVanillaViewportElementHit(pose, camera, min, width, height, mouse, depth, out VanillaViewportElementHit candidate) &&
            (!found || IsBetterVanillaViewportElementHit(candidate, best)))
        {
            best = candidate;
            found = true;
        }

        if (pose.ChildElementPoses == null) return;
        foreach (ElementPose child in pose.ChildElementPoses)
        {
            CollectVanillaViewportElementHits(child, camera, min, width, height, mouse, depth + 1, ref found, ref best);
        }
    }

    private static bool IsBetterVanillaViewportElementHit(VanillaViewportElementHit candidate, VanillaViewportElementHit current)
    {
        if (candidate.Distance < current.Distance - 0.01) return true;
        if (candidate.Distance > current.Distance + 0.01) return false;
        if (candidate.HierarchyDepth != current.HierarchyDepth) return candidate.HierarchyDepth > current.HierarchyDepth;
        return candidate.ScreenArea < current.ScreenArea;
    }

    private static bool TryBuildVanillaViewportElementHit(ElementPose pose, VanillaPreviewCameraState camera, NVector2 min, float width, float height, NVector2 mouse, int depth, out VanillaViewportElementHit hit)
    {
        hit = default;
        if (pose.ForElement == null || string.IsNullOrWhiteSpace(pose.ForElement.Name)) return false;

        Matrixf elementModel = BuildVanillaElementModelMatrix(camera.Model, pose);
        if (!TryIntersectVanillaViewportElementBox(camera, elementModel, pose.ForElement, min, width, height, mouse, out double distance)) return false;

        NVector2[] bounds = BuildVanillaElementBounds3D(camera, elementModel, pose.ForElement, min, width, height, out bool hasVisualCenter, out NVector2 visualCenter);
        if (bounds.Length < 8 || !hasVisualCenter) return false;

        hit = new(
            pose.ForElement.Name,
            bounds,
            visualCenter,
            distance,
            GetProjectedBoundsArea(bounds),
            depth);
        return true;
    }

    private static bool TryIntersectVanillaViewportElementBox(VanillaPreviewCameraState camera, Matrixf elementModel, ShapeElement element, NVector2 min, float width, float height, NVector2 mouse, out double distance)
    {
        distance = 0;
        Matrixf clipFromLocal = new();
        clipFromLocal.Set(elementModel.Values);
        clipFromLocal.ReverseMul(camera.ProjectionView.Values);

        double[] inverseClipFromLocal = Mat4d.Create();
        if (Mat4d.Invert(inverseClipFromLocal, ToDoubleMatrix(clipFromLocal.Values)) == null) return false;
        if (!UnprojectVanillaViewportPoint(inverseClipFromLocal, min, width, height, mouse, -1.0, out Vec3d near)) return false;
        if (!UnprojectVanillaViewportPoint(inverseClipFromLocal, min, width, height, mouse, 1.0, out Vec3d far)) return false;

        Vec3d direction = Sub(far, near);
        if (direction.LengthSq() < 0.000001) return false;
        direction.Normalize();

        Vec3f[] corners = GetElementLocalBoxCorners(element);
        return TryIntersectLocalAabb(near, direction, corners, out distance);
    }

    private static double[] ToDoubleMatrix(float[] values)
    {
        double[] result = new double[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            result[index] = values[index];
        }

        return result;
    }

    private static bool UnprojectVanillaViewportPoint(double[] inverseClipFromLocal, NVector2 min, float width, float height, NVector2 mouse, double clipZ, out Vec3d local)
    {
        local = new Vec3d();
        double ndcX = 2.0 * (mouse.X - min.X) / Math.Max(1f, width) - 1.0;
        double ndcY = 1.0 - 2.0 * (mouse.Y - min.Y) / Math.Max(1f, height);
        double[] result = Mat4d.MulWithVec4(inverseClipFromLocal, [ndcX, ndcY, clipZ, 1.0]);
        if (Math.Abs(result[3]) < 0.000001) return false;

        local.X = result[0] / result[3];
        local.Y = result[1] / result[3];
        local.Z = result[2] / result[3];
        return IsFinite((float)local.X) && IsFinite((float)local.Y) && IsFinite((float)local.Z);
    }

    private static bool TryIntersectLocalAabb(Vec3d origin, Vec3d direction, Vec3f[] corners, out double distance)
    {
        distance = 0;
        if (corners.Length == 0) return false;

        double minX = corners.Min(corner => corner.X);
        double minY = corners.Min(corner => corner.Y);
        double minZ = corners.Min(corner => corner.Z);
        double maxX = corners.Max(corner => corner.X);
        double maxY = corners.Max(corner => corner.Y);
        double maxZ = corners.Max(corner => corner.Z);

        double tMin = 0;
        double tMax = double.MaxValue;
        if (!UpdateRaySlab(origin.X, direction.X, minX, maxX, ref tMin, ref tMax)) return false;
        if (!UpdateRaySlab(origin.Y, direction.Y, minY, maxY, ref tMin, ref tMax)) return false;
        if (!UpdateRaySlab(origin.Z, direction.Z, minZ, maxZ, ref tMin, ref tMax)) return false;

        distance = tMin >= 0 ? tMin : tMax;
        return distance >= 0 && distance < double.MaxValue;
    }

    private static bool UpdateRaySlab(double origin, double direction, double min, double max, ref double tMin, ref double tMax)
    {
        const double epsilon = 0.000001;
        if (Math.Abs(direction) < epsilon)
        {
            return origin >= min && origin <= max;
        }

        double t1 = (min - origin) / direction;
        double t2 = (max - origin) / direction;
        if (t1 > t2) (t1, t2) = (t2, t1);
        tMin = Math.Max(tMin, t1);
        tMax = Math.Min(tMax, t2);
        return tMin <= tMax;
    }

    private static float GetProjectedBoundsArea(NVector2[] bounds)
    {
        if (bounds.Length == 0) return float.MaxValue;

        float minX = bounds.Min(point => point.X);
        float minY = bounds.Min(point => point.Y);
        float maxX = bounds.Max(point => point.X);
        float maxY = bounds.Max(point => point.Y);
        return Math.Max(0.001f, (maxX - minX) * (maxY - minY));
    }

    private static void DrawVanillaViewportGizmoBounds(ImDrawListPtr drawList, VanillaGizmoProjection projection, uint boundsColor, uint helperColor)
    {
        if (projection.BoundsCorners.Length >= 8)
        {
            DrawVanillaViewportBoxBounds(drawList, projection.BoundsCorners, boundsColor, 2f);
        }

        if (projection.HasVisualCenter && (projection.VisualCenter - projection.Center).Length() > 12f)
        {
            DrawVanillaViewportLine(drawList, projection.Center, projection.VisualCenter, helperColor, 2f);
            drawList.AddCircleFilled(projection.VisualCenter, 4f, helperColor, 16);
        }
    }

    private static void DrawVanillaViewportBoxBounds(ImDrawListPtr drawList, NVector2[] points, uint color, float thickness)
    {
        if (points.Length < 8) return;

        DrawVanillaViewportLine(drawList, points[0], points[1], color, thickness);
        DrawVanillaViewportLine(drawList, points[1], points[2], color, thickness);
        DrawVanillaViewportLine(drawList, points[2], points[3], color, thickness);
        DrawVanillaViewportLine(drawList, points[3], points[0], color, thickness);
        DrawVanillaViewportLine(drawList, points[4], points[5], color, thickness);
        DrawVanillaViewportLine(drawList, points[5], points[6], color, thickness);
        DrawVanillaViewportLine(drawList, points[6], points[7], color, thickness);
        DrawVanillaViewportLine(drawList, points[7], points[4], color, thickness);
        DrawVanillaViewportLine(drawList, points[0], points[4], color, thickness);
        DrawVanillaViewportLine(drawList, points[1], points[5], color, thickness);
        DrawVanillaViewportLine(drawList, points[2], points[6], color, thickness);
        DrawVanillaViewportLine(drawList, points[3], points[7], color, thickness);
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

    private static double GetVanillaViewportGizmoRingScreenSign(VanillaGizmoProjection projection, TransformGizmoAxis axis)
    {
        NVector2[] points = axis switch
        {
            TransformGizmoAxis.X => projection.RingX,
            TransformGizmoAxis.Y => projection.RingY,
            TransformGizmoAxis.Z => projection.RingZ,
            _ => []
        };

        for (int index = 1; index < points.Length; index++)
        {
            NVector2 from = points[index - 1] - projection.Center;
            NVector2 to = points[index] - projection.Center;
            float cross = from.X * to.Y - from.Y * to.X;
            if (Math.Abs(cross) > 0.001f) return Math.Sign(cross);
        }

        return -1.0;
    }

    private bool ApplyVanillaViewportGizmoDrag(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, AnimationKeyFrameElement element, TransformGizmoMode mode, TransformGizmoAxis axis, NVector2 axisVector, float scale)
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
        if (_vanillaIkFollowMove && mode == TransformGizmoMode.Move && TryApplyVanillaViewportIkMove(row, entry, element, axis, value))
        {
            return true;
        }

        SetVanillaGizmoAxisValue(element, mode, axis, value);
        ApplyVanillaElementEdit(row, entry, keyFrame, _vanillaSelection.ElementName);
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
        double sign = Math.Abs(_vanillaViewportGizmoDragRingScreenSign) < 0.001 ? -1.0 : _vanillaViewportGizmoDragRingScreenSign;
        _vanillaViewportGizmoDragAccumulatedDegrees += delta * 180.0 / Math.PI / sign;
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

        if (GizmoSpace == TransformGizmoSpace.Parent) GizmoSpace = TransformGizmoSpace.World;
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
        float modelRingRadius = Math.Clamp(modelAxisLength * 0.95f, 0.10f, 0.80f);
        NVector2 axisX;
        NVector2 axisY;
        NVector2 axisZ;
        NVector2[] ringX;
        NVector2[] ringY;
        NVector2[] ringZ;
        if (GizmoSpace == TransformGizmoSpace.World)
        {
            NVector3 centerWorld = TransformVanillaPreviewPoint(elementModel, elementPoint);
            NVector3 worldX = TransformVanillaPreviewDirection(camera.Model, new NVector3(modelAxisLength, 0f, 0f));
            NVector3 worldY = TransformVanillaPreviewDirection(camera.Model, new NVector3(0f, modelAxisLength, 0f));
            NVector3 worldZ = TransformVanillaPreviewDirection(camera.Model, new NVector3(0f, 0f, modelAxisLength));
            if (!ProjectVanillaPreviewWorldPoint(camera, centerWorld + worldX, min, width, height, out NVector2 axisXEnd)) return false;
            if (!ProjectVanillaPreviewWorldPoint(camera, centerWorld + worldY, min, width, height, out NVector2 axisYEnd)) return false;
            if (!ProjectVanillaPreviewWorldPoint(camera, centerWorld + worldZ, min, width, height, out NVector2 axisZEnd)) return false;
            axisX = axisXEnd - center;
            axisY = axisYEnd - center;
            axisZ = axisZEnd - center;

            float ringScale = modelRingRadius / Math.Max(0.0001f, modelAxisLength);
            ringX = BuildVanillaViewportGizmoRingWorld(camera, centerWorld, worldY * ringScale, worldZ * ringScale, min, width, height);
            ringY = BuildVanillaViewportGizmoRingWorld(camera, centerWorld, worldX * ringScale, worldZ * ringScale, min, width, height);
            ringZ = BuildVanillaViewportGizmoRingWorld(camera, centerWorld, worldX * ringScale, worldY * ringScale, min, width, height);
        }
        else
        {
            if (!ProjectVanillaPreviewPoint(elementModel, camera, elementPoint + new NVector3(modelAxisLength, 0f, 0f), min, width, height, out NVector2 axisXEnd)) return false;
            if (!ProjectVanillaPreviewPoint(elementModel, camera, elementPoint + new NVector3(0f, modelAxisLength, 0f), min, width, height, out NVector2 axisYEnd)) return false;
            if (!ProjectVanillaPreviewPoint(elementModel, camera, elementPoint + new NVector3(0f, 0f, modelAxisLength), min, width, height, out NVector2 axisZEnd)) return false;
            axisX = axisXEnd - center;
            axisY = axisYEnd - center;
            axisZ = axisZEnd - center;
            ringX = BuildVanillaViewportGizmoRing(camera, elementModel, elementPoint, modelRingRadius, min, width, height, TransformGizmoAxis.X);
            ringY = BuildVanillaViewportGizmoRing(camera, elementModel, elementPoint, modelRingRadius, min, width, height, TransformGizmoAxis.Y);
            ringZ = BuildVanillaViewportGizmoRing(camera, elementModel, elementPoint, modelRingRadius, min, width, height, TransformGizmoAxis.Z);
        }

        float pixelScale = Math.Max(1f, (axisX.Length() + axisY.Length() + axisZ.Length()) / Math.Max(0.001f, modelAxisLength * 3f));
        NVector2[] bounds = BuildVanillaElementBounds3D(camera, elementModel, pose.ForElement, min, width, height, out bool hasVisualCenter, out NVector2 visualCenter);
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

    private static NVector2[] BuildVanillaViewportGizmoRingWorld(VanillaPreviewCameraState camera, NVector3 centerWorld, NVector3 axisAWorld, NVector3 axisBWorld, NVector2 min, float width, float height)
    {
        const int segments = 72;
        NVector2[] points = new NVector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float angle = (float)(i / (double)segments * Math.PI * 2.0);
            NVector3 world = centerWorld + axisAWorld * (float)Math.Cos(angle) + axisBWorld * (float)Math.Sin(angle);
            if (!ProjectVanillaPreviewWorldPoint(camera, world, min, width, height, out points[i]))
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
        return ProjectVanillaPreviewWorldPoint(camera, TransformVanillaPreviewPoint(localToWorld, point), min, width, height, out screen);
    }

    private static bool ProjectVanillaPreviewWorldPoint(VanillaPreviewCameraState camera, NVector3 worldPoint, NVector2 min, float width, float height, out NVector2 screen)
    {
        Vec4f clip = camera.ProjectionView.TransformVector(new Vec4f(worldPoint.X, worldPoint.Y, worldPoint.Z, 1f));
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

    private static NVector3 TransformVanillaPreviewPoint(Matrixf matrix, NVector3 point)
    {
        Vec4f transformed = matrix.TransformVector(new Vec4f(point.X, point.Y, point.Z, 1f));
        return new NVector3(transformed.X, transformed.Y, transformed.Z);
    }

    private static NVector3 TransformVanillaPreviewDirection(Matrixf matrix, NVector3 direction)
    {
        Vec4f transformed = matrix.TransformVector(new Vec4f(direction.X, direction.Y, direction.Z, 0f));
        return new NVector3(transformed.X, transformed.Y, transformed.Z);
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
        _vanillaViewportGizmoDragRingScreenSign = -1.0;
        _vanillaViewportGizmoDragRowKey = "";
        _vanillaViewportGizmoDragKeyFrameIndex = -1;
        _vanillaViewportGizmoDragElementName = "";
        _vanillaIkDragActive = false;
        _vanillaIkDragRowKey = "";
        _vanillaIkDragKeyFrameIndex = -1;
        _vanillaIkDragElementName = "";
        _vanillaIkDragCache = null;
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

    private void PauseVanillaLiveSymmetryPreview(VanillaBrowserRow row, VanillaAnimation animation)
    {
        if (!_vanillaLiveSymmetryEnabled || _vanillaLiveSymmetryMode != VanillaLiveSymmetryMode.HalfCycle) return;
        if (_vanillaPreviewScene?.Key != row.Key) return;

        int maxFrame = Math.Max(0, Math.Max(1, animation.QuantityFrames) - 1);
        if (_vanillaSelection.LoopEndFrame <= _vanillaSelection.LoopStartFrame || _vanillaSelection.LoopStartFrame < 0 || _vanillaSelection.LoopEndFrame > maxFrame)
        {
            _vanillaSelection.LoopStartFrame = 0;
            _vanillaSelection.LoopEndFrame = maxFrame;
        }

        _vanillaPreviewScene.Playing = false;
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
            else
            {
                AutoApplyVanillaDocument(document);
            }
            return;
        }

        if (before == null || before.Matches(document)) return;

        if (anyItemActive)
        {
            _vanillaHistory.BeginEdit(document, before);
            AutoApplyVanillaDocument(document);
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

        DrawVanillaElementEditor(row, entry, selected);
    }

    private void DrawVanillaElementEditor(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame)
    {
        VanillaAnimationDocument document = entry.Document;
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
        DrawVanillaElementList(elementNames, selectedElementIndex);

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

        DrawVanillaSymmetryControls(row, entry, keyFrame, _vanillaSelection.ElementName, element);
        DrawVanillaIkControls(row, entry, keyFrame, _vanillaSelection.ElementName);
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
            ApplyVanillaElementEdit(row, entry, keyFrame, _vanillaSelection.ElementName);
        }
    }

    private void DrawVanillaElementList(string[] elementNames, int selectedElementIndex)
    {
        if (string.IsNullOrWhiteSpace(_vanillaSelection.ElementName) && elementNames.Length > 0)
        {
            _vanillaSelection.ElementName = elementNames[Math.Clamp(selectedElementIndex, 0, elementNames.Length - 1)];
        }

        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        float listHeight = Math.Clamp(elementNames.Length * lineHeight + 8f, 96f * _devToolsUiScale, 220f * _devToolsUiScale);
        if (!ImGui.BeginListBox("Elements##vanilla-elements", new NVector2(-float.Epsilon, listHeight))) return;

        ImGuiIOPtr io = ImGui.GetIO();
        for (int index = 0; index < elementNames.Length; index++)
        {
            string elementName = elementNames[index];
            bool selected = string.Equals(elementName, _vanillaSelection.ElementName, StringComparison.OrdinalIgnoreCase);
            bool inIkChain = _vanillaIkMode == VanillaIkChainMode.ManualOverride && ContainsVanillaIkChainElement(elementName);
            string label = inIkChain ? $"[IK] {elementName}##vanilla-element-{index}" : $"{elementName}##vanilla-element-{index}";

            if (ImGui.Selectable(label, selected))
            {
                _vanillaSelection.ElementName = elementName;
                if (io.KeyCtrl && _vanillaIkMode == VanillaIkChainMode.ManualOverride)
                {
                    ToggleVanillaIkChainElement(elementName);
                }
                else if (io.KeyCtrl)
                {
                    _vanillaStatus = "Manual IK chain editing is available in Manual override mode.";
                }
            }

            if (inIkChain && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Selected for the manual IK chain. Switch to Manual override to edit this chain.");
            }
        }

        ImGui.EndListBox();
    }

    private bool ContainsVanillaIkChainElement(string elementName)
    {
        return _vanillaIkChainElementNames.Any(name => string.Equals(name, elementName, StringComparison.OrdinalIgnoreCase));
    }

    private void ToggleVanillaIkChainElement(string elementName)
    {
        if (string.IsNullOrWhiteSpace(elementName)) return;

        int existingIndex = _vanillaIkChainElementNames.FindIndex(name => string.Equals(name, elementName, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            _vanillaIkChainElementNames.RemoveAt(existingIndex);
            _vanillaStatus = $"Removed {elementName} from the IK chain.";
        }
        else
        {
            _vanillaIkChainElementNames.Add(elementName);
            _vanillaStatus = $"Added {elementName} to the IK chain.";
        }

        _vanillaIkHasTarget = false;
        ClearVanillaViewportGizmoDrag();
    }

    private void DrawVanillaSymmetryControls(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, string selectedElementName, AnimationKeyFrameElement selectedElement)
    {
        VanillaAnimationDocument document = entry.Document;
        VanillaAnimation animation = entry.Animation;
        string[] allElements = BuildVanillaSymmetryElementUniverse(document, animation, keyFrame);
        if (allElements.Length <= 1)
        {
            ImGui.SeparatorText("Symmetry");
            ImGui.TextDisabled("No other elements available for symmetry.");
            return;
        }

        ImGui.SeparatorText("Symmetry");
        DrawVanillaLiveSymmetryControls(row, animation);
        DrawVanillaSymmetryPairSelector(document, selectedElementName, allElements);

        bool hasPair = TryResolveVanillaSymmetryPair(document, selectedElementName, allElements, out string pairElementName, out VanillaSymmetrySide sourceSide, out bool manualPair);
        if (hasPair)
        {
            ImGui.TextDisabled(manualPair
                ? $"Pair: {pairElementName} (manual)"
                : $"Pair: {pairElementName} (auto)");
        }
        else
        {
            ImGui.TextDisabled("Pair: none");
        }

        if (sourceSide == VanillaSymmetrySide.Unknown)
        {
            ImGui.TextDisabled("Source side: unknown; all-pair actions need a left/right-style element name.");
        }
        else
        {
            ImGui.TextDisabled($"Source side: {sourceSide}");
        }

        bool hasAnimationLength = animation.QuantityFrames > 0;
        bool pairInCurrentKeyframe = hasPair && keyFrame.Elements != null && keyFrame.Elements.ContainsKey(pairElementName);

        bool canMirrorSelected = hasPair;
        if (!canMirrorSelected) ImGui.BeginDisabled();
        if (ImGui.Button("Mirror selected -> pair##vanilla-symmetry-selected-to-pair"))
        {
            ApplyVanillaSymmetryAction(row, document, () => MirrorVanillaElementInKeyFrame(keyFrame, selectedElementName, pairElementName, selectedElement));
        }
        if (!canMirrorSelected) ImGui.EndDisabled();

        ImGui.SameLine();
        bool canMirrorPair = hasPair && pairInCurrentKeyframe;
        if (!canMirrorPair) ImGui.BeginDisabled();
        if (ImGui.Button("Mirror pair -> selected##vanilla-symmetry-pair-to-selected"))
        {
            ApplyVanillaSymmetryAction(row, document, () => MirrorVanillaPairToSelected(keyFrame, selectedElementName, pairElementName));
        }
        if (!canMirrorPair) ImGui.EndDisabled();

        bool canMirrorAll = sourceSide != VanillaSymmetrySide.Unknown;
        if (!canMirrorAll) ImGui.BeginDisabled();
        if (ImGui.Button("Mirror all source-side pairs in keyframe##vanilla-symmetry-all-keyframe"))
        {
            ApplyVanillaSymmetryAction(row, document, () => MirrorVanillaSourceSidePairsInKeyFrame(document, animation, keyFrame, sourceSide));
        }
        if (!canMirrorAll) ImGui.EndDisabled();

        bool canBakeSelected = hasPair && hasAnimationLength;
        if (!canBakeSelected) ImGui.BeginDisabled();
        if (ImGui.Button("Bake half-cycle selected -> pair##vanilla-symmetry-bake-selected"))
        {
            ApplyVanillaSymmetryAction(row, document, () => BakeVanillaHalfCycleSymmetry(document, animation, selectedElementName, pairElementName));
        }
        if (!canBakeSelected) ImGui.EndDisabled();

        ImGui.SameLine();
        bool canBakeAll = sourceSide != VanillaSymmetrySide.Unknown && hasAnimationLength;
        if (!canBakeAll) ImGui.BeginDisabled();
        if (ImGui.Button("Bake half-cycle all source-side pairs##vanilla-symmetry-bake-all"))
        {
            ApplyVanillaSymmetryAction(row, document, () => BakeVanillaHalfCycleSymmetryForSide(document, animation, sourceSide));
        }
        if (!canBakeAll) ImGui.EndDisabled();

        if (!hasPair)
        {
            ImGui.TextDisabled("Select or auto-detect an opposite element before mirroring.");
        }
        else if (!pairInCurrentKeyframe)
        {
            ImGui.TextDisabled($"{pairElementName} is not present in this keyframe; selected-to-pair can create it.");
        }

        if (!hasAnimationLength)
        {
            ImGui.TextDisabled("Half-cycle bake needs a positive animation frame count.");
        }
    }

    private void DrawVanillaLiveSymmetryControls(VanillaBrowserRow row, VanillaAnimation animation)
    {
        bool enabled = _vanillaLiveSymmetryEnabled;
        if (ImGui.Checkbox("Live symmetry##vanilla-live-symmetry", ref enabled))
        {
            _vanillaLiveSymmetryEnabled = enabled;
            if (_vanillaLiveSymmetryEnabled)
            {
                PauseVanillaLiveSymmetryPreview(row, animation);
            }

            _vanillaStatus = _vanillaLiveSymmetryEnabled
                ? "Live symmetry enabled. Edits to driver elements mirror to their pair."
                : "Live symmetry disabled.";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Writes mirrored pair keyframes as you edit the selected element.");
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("In-place##vanilla-live-symmetry-mode", _vanillaLiveSymmetryMode == VanillaLiveSymmetryMode.InPlace))
        {
            _vanillaLiveSymmetryMode = VanillaLiveSymmetryMode.InPlace;
            _vanillaStatus = "Live symmetry mode: in-place mirror.";
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Half-cycle gait##vanilla-live-symmetry-mode", _vanillaLiveSymmetryMode == VanillaLiveSymmetryMode.HalfCycle))
        {
            _vanillaLiveSymmetryMode = VanillaLiveSymmetryMode.HalfCycle;
            PauseVanillaLiveSymmetryPreview(row, animation);
            _vanillaStatus = "Live symmetry mode: half-cycle gait.";
        }

        string[] driverOptions = ["Selected drives pair", "Left drives right", "Right drives left"];
        int driverIndex = (int)_vanillaLiveSymmetryDriver;
        ImGui.SetNextItemWidth(190);
        if (ImGui.Combo("Driver##vanilla-live-symmetry-driver", ref driverIndex, driverOptions, driverOptions.Length))
        {
            _vanillaLiveSymmetryDriver = (VanillaLiveSymmetryDriver)Math.Clamp(driverIndex, 0, driverOptions.Length - 1);
            _vanillaStatus = $"Live symmetry driver: {driverOptions[(int)_vanillaLiveSymmetryDriver]}.";
        }

        if (_vanillaLiveSymmetryMode == VanillaLiveSymmetryMode.HalfCycle)
        {
            int maxPhase = Math.Max(0, Math.Max(1, animation.QuantityFrames) - 1);
            int phase = _vanillaLiveSymmetryPhaseFrames >= 0
                ? Math.Clamp(_vanillaLiveSymmetryPhaseFrames, 0, maxPhase)
                : Math.Clamp(GetVanillaHalfCycleFrames(animation), 0, maxPhase);

            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("Phase frames##vanilla-live-symmetry-phase", ref phase))
            {
                _vanillaLiveSymmetryPhaseFrames = Math.Clamp(phase, 0, maxPhase);
                _vanillaStatus = $"Live symmetry phase: {_vanillaLiveSymmetryPhaseFrames} frame(s).";
            }

            ImGui.SameLine();
            if (ImGui.Button("Half cycle##vanilla-live-symmetry-phase-reset"))
            {
                _vanillaLiveSymmetryPhaseFrames = -1;
                _vanillaStatus = $"Live symmetry phase: half cycle ({GetVanillaLiveSymmetryPhaseFrames(animation)} frame(s)).";
            }

            int activePhase = GetVanillaLiveSymmetryPhaseFrames(animation);
            ImGui.TextDisabled(_vanillaLiveSymmetryPhaseFrames < 0
                ? $"Using half-cycle phase: {activePhase} frame(s)."
                : $"Using custom phase: {activePhase} frame(s).");
            if (_vanillaLiveSymmetryEnabled)
            {
                ImGui.TextDisabled("Half-cycle mode writes the pair at the shifted frame; the ghost shows that pose while paused.");
            }
        }

        if (_vanillaLiveSymmetryEnabled)
        {
            bool showGhost = _vanillaShowLiveSymmetryGhost;
            if (ImGui.Checkbox("Show symmetry ghost##vanilla-live-symmetry-ghost", ref showGhost))
            {
                _vanillaShowLiveSymmetryGhost = showGhost;
                _vanillaStatus = _vanillaShowLiveSymmetryGhost
                    ? "Live symmetry ghost enabled."
                    : "Live symmetry ghost hidden.";
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Shows a translucent phase-shifted preview of the mirrored pose without playing the animation.");
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderFloat("Ghost opacity##vanilla-live-symmetry-ghost-opacity", ref _vanillaLiveSymmetryGhostOpacity, 0.05f, 0.8f, "%.2f"))
            {
                _vanillaLiveSymmetryGhostOpacity = Math.Clamp(_vanillaLiveSymmetryGhostOpacity, 0.05f, 0.8f);
            }
        }
    }

    private void DrawVanillaIkControls(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, string selectedElementName)
    {
        VanillaAnimationDocument document = entry.Document;
        string[] allElements = BuildVanillaSymmetryElementUniverse(document, entry.Animation, keyFrame);

        ImGui.SeparatorText("IK");
        if (allElements.Length < 1)
        {
            ImGui.TextDisabled("IK needs at least one shape element.");
            return;
        }

        PruneVanillaIkChainElements(allElements);

        if (ImGui.RadioButton("Auto limb##vanilla-ik-mode", _vanillaIkMode == VanillaIkChainMode.AutoLimb))
        {
            _vanillaIkMode = VanillaIkChainMode.AutoLimb;
            _vanillaIkHasTarget = false;
            ClearVanillaViewportGizmoDrag();
            _vanillaStatus = "IK mode: auto limb. Select any element; the chain is detected from the shape hierarchy.";
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Manual override##vanilla-ik-mode", _vanillaIkMode == VanillaIkChainMode.ManualOverride))
        {
            _vanillaIkMode = VanillaIkChainMode.ManualOverride;
            _vanillaIkHasTarget = false;
            ClearVanillaViewportGizmoDrag();
            _vanillaStatus = "IK mode: manual override. Click body parts or Ctrl+Click elements to edit the chain.";
        }

        bool hasChain = TryGetActiveVanillaIkChain(document, entry.Animation, keyFrame, selectedElementName, out VanillaIkManualChain chain, out string chainError, out string chainWarning);

        if (ImGui.Checkbox("IK on Move##vanilla-ik-follow-move", ref _vanillaIkFollowMove))
        {
            _vanillaStatus = _vanillaIkFollowMove
                ? "IK Move enabled. Drag the Move gizmo on the active IK chain end."
                : "IK Move disabled.";
        }

        if (_vanillaIkMode == VanillaIkChainMode.ManualOverride && ImGui.Button("Clear IK chain##vanilla-ik-clear"))
        {
            _vanillaIkChainElementNames.Clear();
            _vanillaIkHasTarget = false;
            ClearVanillaViewportGizmoDrag();
            _vanillaStatus = "IK chain cleared.";
        }

        if (hasChain)
        {
            ImGui.TextDisabled(_vanillaIkMode == VanillaIkChainMode.AutoLimb
                ? $"Auto chain: {chain.DisplayName} -> distal end of {chain.EndElementName}"
                : $"Manual chain: {chain.DisplayName} -> distal end of {chain.EndElementName}");
            if (!string.IsNullOrWhiteSpace(chainWarning))
            {
                ImGui.TextColored(new NVector4(1f, 0.72f, 0.32f, 1f), chainWarning);
            }

            if (!string.Equals(selectedElementName, chain.EndElementName, StringComparison.OrdinalIgnoreCase))
            {
                ImGui.TextDisabled($"IK Move handle: {chain.EndElementName}.");
            }
            ImGui.TextDisabled($"End effector: distal end of {chain.EndElementName}.");
            if (_vanillaIkMode == VanillaIkChainMode.AutoLimb)
            {
                DrawVanillaAutoIkChainAdjusters(selectedElementName);
            }
        }
        else
        {
            ImGui.TextDisabled(chainError);
        }

        if (!hasChain) ImGui.BeginDisabled();
        if (ImGui.Button("Target = current end##vanilla-ik-current-target"))
        {
            SetVanillaIkTargetFromCurrentEnd(row, entry, keyFrame, chain);
        }
        if (!hasChain) ImGui.EndDisabled();

        NVector3 target = new(_vanillaIkTargetX, _vanillaIkTargetY, _vanillaIkTargetZ);
        ImGui.SetNextItemWidth(220);
        if (ImGui.DragFloat3("Target##vanilla-ik-target", ref target, 0.01f))
        {
            _vanillaIkTargetX = target.X;
            _vanillaIkTargetY = target.Y;
            _vanillaIkTargetZ = target.Z;
            _vanillaIkHasTarget = true;
        }

        bool canSolve = hasChain && _vanillaIkHasTarget;
        if (!canSolve) ImGui.BeginDisabled();
        if (ImGui.Button("Solve IK to target##vanilla-ik-solve"))
        {
            ApplyVanillaIkTarget(row, entry, keyFrame, chain);
        }
        if (!canSolve) ImGui.EndDisabled();

        if (!_vanillaIkHasTarget)
        {
            ImGui.TextDisabled("Set a target from the current end or edit target coordinates.");
        }

        ImGui.TextDisabled(_vanillaIkMode == VanillaIkChainMode.AutoLimb
            ? "Orbit viewport: click body parts to select them. IK detects limb chains structurally and stops before body hubs."
            : "Manual override: click body parts or Ctrl+Click elements to add/remove IK chain bones.");
    }

    private void DrawVanillaAutoIkChainAdjusters(string selectedElementName)
    {
        NormalizeVanillaIkAutoAdjustmentSelection(selectedElementName);

        ImGui.TextDisabled("Auto chain length:");
        ImGui.SameLine();
        if (ImGui.SmallButton("- root##vanilla-ik-auto-root-minus"))
        {
            AdjustVanillaAutoIkChainLength(ref _vanillaIkAutoRootExtraBones, -1, "root");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ root##vanilla-ik-auto-root-plus"))
        {
            AdjustVanillaAutoIkChainLength(ref _vanillaIkAutoRootExtraBones, 1, "root");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("- end##vanilla-ik-auto-end-minus"))
        {
            AdjustVanillaAutoIkChainLength(ref _vanillaIkAutoEndExtraBones, -1, "end");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ end##vanilla-ik-auto-end-plus"))
        {
            AdjustVanillaAutoIkChainLength(ref _vanillaIkAutoEndExtraBones, 1, "end");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Extends the auto end effector farther down the selected element's child path.");
        }
    }

    private void AdjustVanillaAutoIkChainLength(ref int value, int delta, string side)
    {
        int next = Math.Clamp(value + delta, 0, VanillaIkAutoMaxAdjustmentBones);
        if (next == value) return;

        value = next;
        _vanillaIkHasTarget = false;
        ClearVanillaViewportGizmoDrag();
        _vanillaStatus = side == "root"
            ? $"Auto IK root expansion: {value} extra bone(s)."
            : $"Auto IK end extension: {value} extra bone(s).";
    }

    private void PruneVanillaIkChainElements(string[] allElements)
    {
        for (int index = _vanillaIkChainElementNames.Count - 1; index >= 0; index--)
        {
            if (ContainsElementName(allElements, _vanillaIkChainElementNames[index])) continue;

            _vanillaIkChainElementNames.RemoveAt(index);
            _vanillaIkHasTarget = false;
            ClearVanillaViewportGizmoDrag();
        }
    }

    private bool TryGetActiveVanillaIkChain(
        VanillaAnimationDocument document,
        VanillaAnimation animation,
        AnimationKeyFrame keyFrame,
        string selectedElementName,
        out VanillaIkManualChain chain,
        out string error,
        out string warning)
    {
        return _vanillaIkMode == VanillaIkChainMode.ManualOverride
            ? TryGetManualVanillaIkChain(document.Shape, out chain, out error, out warning)
            : TryGetAutoVanillaIkLimbChain(document, animation, keyFrame, selectedElementName, out chain, out error, out warning);
    }

    private bool TryGetAutoVanillaIkLimbChain(
        VanillaAnimationDocument document,
        VanillaAnimation animation,
        AnimationKeyFrame keyFrame,
        string selectedElementName,
        out VanillaIkManualChain chain,
        out string error,
        out string warning)
    {
        chain = default;
        error = "";
        warning = "";

        if (string.IsNullOrWhiteSpace(selectedElementName))
        {
            error = "Select an element for auto IK.";
            return false;
        }

        NormalizeVanillaIkAutoAdjustmentSelection(selectedElementName);

        Shape? shape = document.Shape;
        if (shape?.Elements == null || shape.Elements.Length == 0)
        {
            error = "IK needs a loaded shape hierarchy.";
            return false;
        }

        if (!TryFindShapeElementPath(shape, selectedElementName, out List<ShapeElement> path) || path.Count == 0)
        {
            error = $"IK element {selectedElementName} was not found in the shape hierarchy.";
            return false;
        }

        ShapeElement selected = path[^1];
        string resolvedSelectedName = string.IsNullOrWhiteSpace(selected.Name) ? selectedElementName.Trim() : selected.Name!;
        int selectedIndex = path.Count - 1;
        if (IsVanillaIkStructuralHub(selected, selectedIndex == 0, out string selectedHubReason))
        {
            if (TryGetVanillaIkLongestChildPath(selected, GetVanillaIkAutoMaxChainLength(), out List<ShapeElement> childPath, out string childNote) &&
                TryBuildVanillaIkChainNames(childPath, out string[] childNames))
            {
                warning = $"Selected {resolvedSelectedName} is {selectedHubReason}; auto uses child limb {childNames[0]}.";
                if (!string.IsNullOrWhiteSpace(childNote)) warning += $" {childNote}";
                chain = new VanillaIkManualChain(childNames, childNames[^1], string.Join(" -> ", childNames));
                return true;
            }

            warning = $"Selected {resolvedSelectedName} is {selectedHubReason}; auto uses a one-bone chain.";
            chain = new VanillaIkManualChain([resolvedSelectedName], resolvedSelectedName, resolvedSelectedName);
            return true;
        }

        int detectedStartIndex = FindVanillaIkStructuralChainStart(path, selectedIndex, out string stopReason);
        int startIndex = Math.Max(0, detectedStartIndex - _vanillaIkAutoRootExtraBones);
        int maxChainLength = GetVanillaIkAutoMaxChainLength();
        if (selectedIndex - startIndex + 1 > maxChainLength)
        {
            startIndex = Math.Max(0, selectedIndex - maxChainLength + 1);
        }

        var chainElements = path.Skip(startIndex).Take(selectedIndex - startIndex + 1).ToList();
        int remaining = Math.Max(0, maxChainLength - chainElements.Count);
        AppendVanillaIkLongestDescendantPath(chainElements, selected, remaining, out string extensionNote);

        if (!TryBuildVanillaIkChainNames(chainElements, out string[] orderedNames))
        {
            orderedNames = BuildVanillaIkFallbackChainNames(path, selectedElementName);
            warning = "Auto IK could not build a named structural path; using a fallback chain.";
        }

        if (orderedNames.Length == 0)
        {
            error = $"IK element {selectedElementName} has no usable named chain.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(warning))
        {
            warning = string.IsNullOrWhiteSpace(stopReason)
                ? "Auto chain used the nearest structural path."
                : $"Auto chain stopped before {stopReason}.";
            if (_vanillaIkAutoRootExtraBones > 0) warning += $" Root expanded by {_vanillaIkAutoRootExtraBones}.";
            if (!string.IsNullOrWhiteSpace(extensionNote)) warning += $" {extensionNote}";
        }
        if (orderedNames.Length == 1 && !warning.Contains("one-bone", StringComparison.OrdinalIgnoreCase))
        {
            warning += " Using a one-bone chain.";
        }

        chain = new VanillaIkManualChain(orderedNames, orderedNames[^1], string.Join(" -> ", orderedNames));
        return true;
    }

    private void NormalizeVanillaIkAutoAdjustmentSelection(string selectedElementName)
    {
        string normalized = selectedElementName?.Trim() ?? "";
        if (string.Equals(_vanillaIkAutoAdjustmentElementName, normalized, StringComparison.OrdinalIgnoreCase)) return;

        _vanillaIkAutoAdjustmentElementName = normalized;
        _vanillaIkAutoRootExtraBones = 0;
        _vanillaIkAutoEndExtraBones = 0;
    }

    private int GetVanillaIkAutoMaxChainLength()
    {
        return Math.Clamp(
            VanillaIkAutoMaxChainLength + _vanillaIkAutoRootExtraBones + _vanillaIkAutoEndExtraBones,
            1,
            VanillaIkAutoAbsoluteMaxChainLength);
    }

    private static int FindVanillaIkStructuralChainStart(IReadOnlyList<ShapeElement> path, int selectedIndex, out string stopReason)
    {
        stopReason = "";
        for (int index = selectedIndex - 1; index >= 0; index--)
        {
            if (!IsVanillaIkStructuralHub(path[index], index == 0, out string reason)) continue;

            string name = string.IsNullOrWhiteSpace(path[index].Name) ? "unnamed hub" : path[index].Name!;
            stopReason = $"{name} ({reason})";
            return Math.Min(selectedIndex, index + 1);
        }

        return Math.Max(0, selectedIndex - 1);
    }

    private static bool IsVanillaIkStructuralHub(ShapeElement element, bool isRoot, out string reason)
    {
        if (isRoot)
        {
            reason = "shape root";
            return true;
        }

        int childCount = element.Children?.Length ?? 0;
        if (childCount >= VanillaIkAutoHubChildThreshold)
        {
            reason = $"hub with {childCount} children";
            return true;
        }

        string normalized = NormalizeVanillaIkStructureName(element.Name);
        if (IsVanillaIkTrunkName(normalized))
        {
            reason = $"trunk name '{element.Name}'";
            return true;
        }

        reason = "";
        return false;
    }

    private static bool IsVanillaIkTrunkName(string normalizedName)
    {
        if (string.IsNullOrWhiteSpace(normalizedName)) return false;
        if (VanillaIkTrunkNameTokens.Contains(normalizedName, StringComparer.OrdinalIgnoreCase)) return true;
        return normalizedName.Contains("torso", StringComparison.OrdinalIgnoreCase) ||
               normalizedName.Contains("spine", StringComparison.OrdinalIgnoreCase) ||
               normalizedName.Contains("pelvis", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVanillaIkStructureName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        StringBuilder builder = new(name.Length);
        foreach (char character in name)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static bool TryGetVanillaIkLongestChildPath(ShapeElement element, int maxLength, out List<ShapeElement> path, out string note)
    {
        path = [];
        note = "";
        if (maxLength <= 0) return false;

        ShapeElement? current = SelectVanillaIkLongestNamedChild(element, out int childCount);
        if (current == null) return false;
        if (childCount > 1)
        {
            note = $"Chose the longest child path from {element.Name ?? "unnamed"} ({childCount} children).";
        }

        path.Add(current);
        AppendVanillaIkLongestDescendantPath(path, current, maxLength - 1, out string extensionNote);
        if (!string.IsNullOrWhiteSpace(extensionNote))
        {
            note = string.IsNullOrWhiteSpace(note) ? extensionNote : $"{note} {extensionNote}";
        }

        return path.Count > 0;
    }

    private static void AppendVanillaIkLongestDescendantPath(List<ShapeElement> chainElements, ShapeElement current, int maxAdditional, out string note)
    {
        note = "";
        for (int added = 0; added < maxAdditional; added++)
        {
            ShapeElement? next = SelectVanillaIkLongestNamedChild(current, out int childCount);
            if (next == null) return;

            if (childCount > 1 && string.IsNullOrWhiteSpace(note))
            {
                note = $"Extended through the longest child path from {current.Name ?? "unnamed"} ({childCount} children).";
            }

            chainElements.Add(next);
            current = next;
        }
    }

    private static ShapeElement? SelectVanillaIkLongestNamedChild(ShapeElement element, out int childCount)
    {
        childCount = element.Children?.Length ?? 0;
        if (element.Children == null || element.Children.Length == 0) return null;

        return element.Children
            .Where(child => !string.IsNullOrWhiteSpace(child.Name))
            .OrderByDescending(GetVanillaIkNamedSubtreeDepth)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int GetVanillaIkNamedSubtreeDepth(ShapeElement element)
    {
        if (element.Children == null || element.Children.Length == 0) return 1;

        int best = 0;
        foreach (ShapeElement child in element.Children)
        {
            if (string.IsNullOrWhiteSpace(child.Name)) continue;
            best = Math.Max(best, GetVanillaIkNamedSubtreeDepth(child));
        }

        return 1 + best;
    }

    private static bool TryBuildVanillaIkChainNames(IEnumerable<ShapeElement> elements, out string[] names)
    {
        names = elements
            .Select(element => element.Name ?? "")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length > 0;
    }

    private static string[] BuildVanillaIkFallbackChainNames(IReadOnlyList<ShapeElement> path, string selectedElementName)
    {
        if (path.Count == 0) return string.IsNullOrWhiteSpace(selectedElementName) ? [] : [selectedElementName.Trim()];

        int selectedIndex = path.Count - 1;
        int startIndex = Math.Max(0, selectedIndex - 1);
        string[] names = path
            .Skip(startIndex)
            .Select(element => element.Name ?? "")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return names.Length > 0 || string.IsNullOrWhiteSpace(selectedElementName) ? names : [selectedElementName.Trim()];
    }

    private bool TryGetManualVanillaIkChain(Shape? shape, out VanillaIkManualChain chain, out string error, out string warning)
    {
        chain = default;
        error = "";
        warning = "";

        if (_vanillaIkChainElementNames.Count == 0)
        {
            error = "Switch to Manual override, then click body parts or Ctrl+Click elements to build a manual IK chain.";
            return false;
        }

        if (shape?.Elements == null || shape.Elements.Length == 0)
        {
            error = "IK needs a loaded shape hierarchy.";
            return false;
        }

        var nodes = new List<VanillaIkChainNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string selectedName in _vanillaIkChainElementNames)
        {
            string name = selectedName.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!seen.Add(name))
            {
                error = $"IK chain contains {name} more than once.";
                return false;
            }

            if (!TryFindShapeElementPath(shape, name, out List<ShapeElement> path) || path.Count == 0)
            {
                error = $"IK chain element {name} was not found in the shape hierarchy.";
                return false;
            }

            ShapeElement element = path[^1];
            string elementName = string.IsNullOrWhiteSpace(element.Name) ? name : element.Name!;
            string parentName = path.Count > 1 && !string.IsNullOrWhiteSpace(path[^2].Name) ? path[^2].Name! : "";
            nodes.Add(new VanillaIkChainNode(elementName, parentName, path.Count - 1));
        }

        if (nodes.Count == 0)
        {
            error = "Switch to Manual override, then click body parts or Ctrl+Click elements to build a manual IK chain.";
            return false;
        }

        nodes.Sort((left, right) =>
        {
            int depth = left.Depth.CompareTo(right.Depth);
            return depth != 0 ? depth : string.Compare(left.ElementName, right.ElementName, StringComparison.OrdinalIgnoreCase);
        });

        for (int index = 1; index < nodes.Count; index++)
        {
            VanillaIkChainNode previous = nodes[index - 1];
            VanillaIkChainNode current = nodes[index];
            if (!string.Equals(current.ParentElementName, previous.ElementName, StringComparison.OrdinalIgnoreCase))
            {
                error = $"IK chain must be one contiguous parent-to-child path. {current.ElementName} is not a direct child of {previous.ElementName}.";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(nodes[0].ParentElementName))
        {
            warning = "Top selected element is a root element; IK can rotate the whole model.";
        }

        string[] orderedNames = nodes.Select(node => node.ElementName).ToArray();
        chain = new VanillaIkManualChain(orderedNames, orderedNames[^1], string.Join(" -> ", orderedNames));
        return true;
    }

    private static bool ContainsElementName(string[] elementNames, string value)
    {
        return !string.IsNullOrWhiteSpace(value) && elementNames.Any(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase));
    }

    private void SetVanillaIkTargetFromCurrentEnd(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, VanillaIkManualChain chain)
    {
        if (!TryCreateVanillaIkCcdCache(row, entry, keyFrame, chain, out VanillaIkCcdCache? cache, out string error) || cache == null)
        {
            _vanillaStatus = error;
            return;
        }

        _vanillaIkTargetX = (float)cache.EndOrigin.X;
        _vanillaIkTargetY = (float)cache.EndOrigin.Y;
        _vanillaIkTargetZ = (float)cache.EndOrigin.Z;
        _vanillaIkHasTarget = true;
        _vanillaStatus = $"IK target set from distal end of {chain.EndElementName}.";
    }

    private void ApplyVanillaIkTarget(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, VanillaIkManualChain chain)
    {
        if (!_vanillaIkHasTarget)
        {
            _vanillaStatus = "IK solve needs a target.";
            return;
        }

        if (!TryCreateVanillaIkCcdCache(row, entry, keyFrame, chain, out VanillaIkCcdCache? cache, out string error) || cache == null)
        {
            _vanillaStatus = error;
            return;
        }

        Vec3d target = new(_vanillaIkTargetX, _vanillaIkTargetY, _vanillaIkTargetZ);
        if (!TrySolveVanillaIkCcdToTarget(cache, target, out AnimationKeyFrameElement[] solvedElements, out double finalDistance, out string solveError))
        {
            _vanillaStatus = solveError;
            return;
        }

        ApplyVanillaIkSolvedElements(keyFrame, chain, solvedElements);
        ApplyVanillaElementEdit(row, entry, keyFrame, chain.ElementNames.ToArray());
        _vanillaStatus = finalDistance <= VanillaIkSolveTolerance
            ? $"Solved IK for {chain.ElementNames.Count} element(s) at frame {keyFrame.Frame}."
            : $"Solved IK best effort for {chain.ElementNames.Count} element(s); remaining distance {finalDistance:0.###}.";
    }

    private void DrawVanillaSymmetryPairSelector(VanillaAnimationDocument document, string selectedElementName, string[] allElements)
    {
        string[] pairOptions = BuildVanillaSymmetryPairOptions(selectedElementName, allElements);
        string manualPair = GetVanillaSymmetryPairOverride(document, selectedElementName);
        int selectedPairIndex = string.IsNullOrWhiteSpace(manualPair)
            ? 0
            : Math.Max(0, Array.FindIndex(pairOptions, option => string.Equals(option, manualPair, StringComparison.OrdinalIgnoreCase)));

        if (selectedPairIndex < 0) selectedPairIndex = 0;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Manual pair##vanilla-symmetry-manual-pair", ref selectedPairIndex, pairOptions, pairOptions.Length))
        {
            if (selectedPairIndex <= 0)
            {
                ClearVanillaSymmetryPairOverride(document, selectedElementName);
                _vanillaStatus = $"Cleared manual symmetry pair for {selectedElementName}.";
            }
            else
            {
                SetVanillaSymmetryPairOverride(document, selectedElementName, pairOptions[selectedPairIndex]);
                _vanillaStatus = $"Manual symmetry pair set: {selectedElementName} <-> {pairOptions[selectedPairIndex]}.";
            }
        }
    }

    private void ApplyVanillaSymmetryAction(VanillaBrowserRow row, VanillaAnimationDocument document, Func<VanillaSymmetryResult> action)
    {
        VanillaSymmetryResult result = action();
        _vanillaStatus = result.Message;
        if (!result.Applied) return;

        MarkVanillaDirty(document);
        RefreshVanillaPreviewAfterEdit(row);
    }

    private VanillaSymmetryResult ApplyVanillaElementEdit(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame sourceKeyFrame, params string[] changedElementNames)
    {
        VanillaSymmetryResult symmetry = PropagateVanillaLiveSymmetry(entry.Document, entry.Animation, sourceKeyFrame, changedElementNames);
        PreserveVanillaSelectedKeyFrame(entry.Animation, sourceKeyFrame);
        MarkVanillaDirty(entry.Document);
        RefreshVanillaPreviewAfterEdit(row);

        if (symmetry.Applied)
        {
            _vanillaStatus = symmetry.Message;
        }

        return symmetry;
    }

    private void PreserveVanillaSelectedKeyFrame(VanillaAnimation animation, AnimationKeyFrame sourceKeyFrame)
    {
        if (animation.KeyFrames == null || animation.KeyFrames.Length == 0) return;

        int previousIndex = _vanillaSelection.KeyFrameIndex;
        int currentIndex = Array.FindIndex(animation.KeyFrames, keyFrame => ReferenceEquals(keyFrame, sourceKeyFrame));
        if (currentIndex < 0) return;

        _vanillaSelection.KeyFrameIndex = currentIndex;
        if (_vanillaViewportGizmoDragAxis != TransformGizmoAxis.None && _vanillaViewportGizmoDragKeyFrameIndex == previousIndex)
        {
            _vanillaViewportGizmoDragKeyFrameIndex = currentIndex;
        }

        if (_vanillaIkDragActive && _vanillaIkDragKeyFrameIndex == previousIndex)
        {
            _vanillaIkDragKeyFrameIndex = currentIndex;
        }
    }

    private VanillaSymmetryResult PropagateVanillaLiveSymmetry(VanillaAnimationDocument document, VanillaAnimation animation, AnimationKeyFrame sourceKeyFrame, IEnumerable<string> changedElementNames)
    {
        if (!_vanillaLiveSymmetryEnabled || _vanillaLiveSymmetryPropagating)
        {
            return new(false, 0, 0, 0, "");
        }

        if (sourceKeyFrame.Elements == null || sourceKeyFrame.Elements.Count == 0)
        {
            return new(false, 0, 0, 0, "");
        }

        string[] allElements = BuildVanillaSymmetryElementUniverse(document, animation, sourceKeyFrame);
        if (allElements.Length <= 1)
        {
            return new(false, 0, 0, 0, "");
        }

        var sourceSnapshots = new List<(string Name, AnimationKeyFrameElement Element)>();
        foreach (string sourceName in changedElementNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (sourceKeyFrame.Elements.TryGetValue(sourceName, out AnimationKeyFrameElement? sourceElement) && sourceElement != null)
            {
                sourceSnapshots.Add((sourceName, CloneElement(sourceElement)));
            }
        }

        if (sourceSnapshots.Count == 0)
        {
            return new(false, 0, 0, 0, "");
        }

        int phaseFrames = GetVanillaLiveSymmetryPhaseFrames(animation);
        int targetFrame = _vanillaLiveSymmetryMode == VanillaLiveSymmetryMode.InPlace
            ? sourceKeyFrame.Frame
            : GetVanillaPhaseTargetFrame(animation, sourceKeyFrame.Frame, phaseFrames);
        int written = 0;
        int created = 0;
        int overwritten = 0;
        int skipped = 0;

        _vanillaLiveSymmetryPropagating = true;
        try
        {
            foreach ((string sourceName, AnimationKeyFrameElement sourceElement) in sourceSnapshots)
            {
                if (!TryResolveVanillaSymmetryPair(document, sourceName, allElements, out string pairName, out VanillaSymmetrySide sourceSide, out _) ||
                    string.Equals(sourceName, pairName, StringComparison.OrdinalIgnoreCase) ||
                    !ShouldVanillaLiveSymmetryPropagateFrom(sourceSide))
                {
                    skipped++;
                    continue;
                }

                AnimationKeyFrame targetKeyFrame = GetOrCreateVanillaTargetKeyFrame(animation, targetFrame, out bool createdKeyFrame);
                if (createdKeyFrame) created++;
                targetKeyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
                if (targetKeyFrame.Elements.ContainsKey(pairName)) overwritten++;
                targetKeyFrame.Elements[pairName] = MirrorVanillaElement(sourceElement);
                written++;
            }
        }
        finally
        {
            _vanillaLiveSymmetryPropagating = false;
        }

        if (written > 0 && animation.KeyFrames != null)
        {
            animation.KeyFrames = animation.KeyFrames.OrderBy(keyFrame => keyFrame.Frame).ToArray();
        }

        return written == 0
            ? new(false, 0, 0, 0, skipped > 0 ? "Live symmetry skipped all changed elements." : "")
            : new(true, written, created, overwritten, $"Live symmetry wrote {written} mirrored pair(s) at frame {targetFrame}; created {created} keyframe(s), overwrote {overwritten} element(s).");
    }

    private bool ShouldVanillaLiveSymmetryPropagateFrom(VanillaSymmetrySide sourceSide)
    {
        return _vanillaLiveSymmetryDriver switch
        {
            VanillaLiveSymmetryDriver.LeftDrivesRight => sourceSide == VanillaSymmetrySide.Left,
            VanillaLiveSymmetryDriver.RightDrivesLeft => sourceSide == VanillaSymmetrySide.Right,
            _ => true
        };
    }

    private static VanillaSymmetryResult MirrorVanillaElementInKeyFrame(AnimationKeyFrame keyFrame, string sourceElementName, string targetElementName, AnimationKeyFrameElement sourceElement)
    {
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        bool overwritten = keyFrame.Elements.ContainsKey(targetElementName);
        keyFrame.Elements[targetElementName] = MirrorVanillaElement(sourceElement);
        return new(true, 1, 0, overwritten ? 1 : 0, $"Mirrored {sourceElementName} to {targetElementName} in frame {keyFrame.Frame}.");
    }

    private static VanillaSymmetryResult MirrorVanillaPairToSelected(AnimationKeyFrame keyFrame, string selectedElementName, string pairElementName)
    {
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        if (!keyFrame.Elements.TryGetValue(pairElementName, out AnimationKeyFrameElement? pairElement) || pairElement == null)
        {
            return new(false, 0, 0, 0, $"{pairElementName} is not present in frame {keyFrame.Frame}.");
        }

        bool overwritten = keyFrame.Elements.ContainsKey(selectedElementName);
        keyFrame.Elements[selectedElementName] = MirrorVanillaElement(pairElement);
        return new(true, 1, 0, overwritten ? 1 : 0, $"Mirrored {pairElementName} to {selectedElementName} in frame {keyFrame.Frame}.");
    }

    private VanillaSymmetryResult MirrorVanillaSourceSidePairsInKeyFrame(VanillaAnimationDocument document, VanillaAnimation animation, AnimationKeyFrame keyFrame, VanillaSymmetrySide sourceSide)
    {
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        string[] allElements = BuildVanillaSymmetryElementUniverse(document, animation, keyFrame);
        int written = 0;
        int overwritten = 0;

        foreach ((string sourceName, AnimationKeyFrameElement sourceElement) in keyFrame.Elements.ToArray().OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryResolveVanillaSymmetryPair(document, sourceName, allElements, out string pairName, out VanillaSymmetrySide elementSide, out _) ||
                elementSide != sourceSide ||
                string.Equals(sourceName, pairName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (keyFrame.Elements.ContainsKey(pairName)) overwritten++;
            keyFrame.Elements[pairName] = MirrorVanillaElement(sourceElement);
            written++;
        }

        return written == 0
            ? new(false, 0, 0, 0, $"No {sourceSide.ToString().ToLowerInvariant()}-side elements with pairs were found in frame {keyFrame.Frame}.")
            : new(true, written, 0, overwritten, $"Mirrored {written} {sourceSide.ToString().ToLowerInvariant()}-side pair(s) in frame {keyFrame.Frame}; overwrote {overwritten}.");
    }

    private VanillaSymmetryResult BakeVanillaHalfCycleSymmetry(VanillaAnimationDocument document, VanillaAnimation animation, string sourceElementName, string targetElementName)
    {
        if (animation.QuantityFrames <= 0)
        {
            return new(false, 0, 0, 0, "Half-cycle bake needs a positive animation frame count.");
        }

        animation.KeyFrames ??= [];
        int halfCycleFrames = GetVanillaHalfCycleFrames(animation);
        int written = 0;
        int created = 0;
        int overwritten = 0;

        foreach (AnimationKeyFrame sourceKeyFrame in animation.KeyFrames.ToArray().OrderBy(keyFrame => keyFrame.Frame))
        {
            if (sourceKeyFrame.Elements == null ||
                !sourceKeyFrame.Elements.TryGetValue(sourceElementName, out AnimationKeyFrameElement? sourceElement) ||
                sourceElement == null)
            {
                continue;
            }

            AnimationKeyFrame targetKeyFrame = GetOrCreateVanillaTargetKeyFrame(animation, GetVanillaHalfCycleTargetFrame(animation, sourceKeyFrame.Frame, halfCycleFrames), out bool createdKeyFrame);
            if (createdKeyFrame) created++;
            targetKeyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
            if (targetKeyFrame.Elements.ContainsKey(targetElementName)) overwritten++;
            targetKeyFrame.Elements[targetElementName] = MirrorVanillaElement(sourceElement);
            written++;
        }

        if (written > 0)
        {
            animation.KeyFrames = animation.KeyFrames.OrderBy(keyFrame => keyFrame.Frame).ToArray();
        }

        return written == 0
            ? new(false, 0, 0, 0, $"No source keyframes contain {sourceElementName}.")
            : new(true, written, created, overwritten, $"Baked half-cycle symmetry {sourceElementName} -> {targetElementName}: wrote {written}, created {created} keyframe(s), overwrote {overwritten} element(s).");
    }

    private VanillaSymmetryResult BakeVanillaHalfCycleSymmetryForSide(VanillaAnimationDocument document, VanillaAnimation animation, VanillaSymmetrySide sourceSide)
    {
        if (animation.QuantityFrames <= 0)
        {
            return new(false, 0, 0, 0, "Half-cycle bake needs a positive animation frame count.");
        }

        animation.KeyFrames ??= [];
        int halfCycleFrames = GetVanillaHalfCycleFrames(animation);
        int written = 0;
        int created = 0;
        int overwritten = 0;

        foreach (AnimationKeyFrame sourceKeyFrame in animation.KeyFrames.ToArray().OrderBy(keyFrame => keyFrame.Frame))
        {
            if (sourceKeyFrame.Elements == null || sourceKeyFrame.Elements.Count == 0) continue;

            string[] allElements = BuildVanillaSymmetryElementUniverse(document, animation, sourceKeyFrame);
            foreach ((string sourceName, AnimationKeyFrameElement sourceElement) in sourceKeyFrame.Elements.ToArray().OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryResolveVanillaSymmetryPair(document, sourceName, allElements, out string pairName, out VanillaSymmetrySide elementSide, out _) ||
                    elementSide != sourceSide ||
                    string.Equals(sourceName, pairName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AnimationKeyFrame targetKeyFrame = GetOrCreateVanillaTargetKeyFrame(animation, GetVanillaHalfCycleTargetFrame(animation, sourceKeyFrame.Frame, halfCycleFrames), out bool createdKeyFrame);
                if (createdKeyFrame) created++;
                targetKeyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
                if (targetKeyFrame.Elements.ContainsKey(pairName)) overwritten++;
                targetKeyFrame.Elements[pairName] = MirrorVanillaElement(sourceElement);
                written++;
            }
        }

        if (written > 0)
        {
            animation.KeyFrames = animation.KeyFrames.OrderBy(keyFrame => keyFrame.Frame).ToArray();
        }

        return written == 0
            ? new(false, 0, 0, 0, $"No {sourceSide.ToString().ToLowerInvariant()}-side source elements with pairs were found.")
            : new(true, written, created, overwritten, $"Baked half-cycle symmetry for {sourceSide.ToString().ToLowerInvariant()} side: wrote {written}, created {created} keyframe(s), overwrote {overwritten} element(s).");
    }

    private bool TryApplyVanillaViewportIkMove(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrameElement selectedElement, TransformGizmoAxis axis, double value)
    {
        if (entry.Animation.KeyFrames == null || entry.Animation.KeyFrames.Length == 0) return false;
        int keyFrameIndex = Math.Clamp(_vanillaSelection.KeyFrameIndex, 0, entry.Animation.KeyFrames.Length - 1);
        AnimationKeyFrame keyFrame = entry.Animation.KeyFrames[keyFrameIndex];

        if (!TryGetActiveVanillaIkChain(entry.Document, entry.Animation, keyFrame, _vanillaSelection.ElementName, out VanillaIkManualChain chain, out string chainError, out _))
        {
            _vanillaStatus = chainError;
            return false;
        }

        string selectedElementName = _vanillaSelection.ElementName;
        if (!string.Equals(selectedElementName, chain.EndElementName, StringComparison.OrdinalIgnoreCase))
        {
            _vanillaStatus = $"IK Move handle is {chain.EndElementName}; select that element before dragging.";
            return false;
        }

        if (!_vanillaIkDragActive ||
            _vanillaIkDragCache == null ||
            _vanillaIkDragRowKey != row.Key ||
            _vanillaIkDragKeyFrameIndex != keyFrameIndex ||
            !string.Equals(_vanillaIkDragElementName, selectedElementName, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryCreateVanillaIkCcdCache(row, entry, keyFrame, chain, out VanillaIkCcdCache? cache, out string error) || cache == null)
            {
                _vanillaStatus = error;
                return false;
            }

            _vanillaIkDragActive = true;
            _vanillaIkDragRowKey = row.Key;
            _vanillaIkDragKeyFrameIndex = keyFrameIndex;
            _vanillaIkDragElementName = selectedElementName;
            _vanillaIkDragCache = cache;
        }

        AnimationKeyFrameElement desiredElement = CloneElement(_vanillaIkDragCache.SelectedStartElement);
        SetVanillaGizmoAxisValue(desiredElement, TransformGizmoMode.Move, axis, value);
        Vec3d target = GetVanillaIkDesiredEndTarget(_vanillaIkDragCache, desiredElement);
        if (!TrySolveVanillaIkCcdToTarget(_vanillaIkDragCache, target, out AnimationKeyFrameElement[] solvedElements, out double finalDistance, out string solveError))
        {
            _vanillaStatus = solveError;
            return false;
        }

        ApplyVanillaIkSolvedElements(keyFrame, chain, solvedElements);
        ApplyVanillaElementEdit(row, entry, keyFrame, chain.ElementNames.ToArray());
        _vanillaStatus = finalDistance <= VanillaIkSolveTolerance
            ? $"IK Move solved {chain.ElementNames.Count} element(s)."
            : $"IK Move solved best effort; remaining distance {finalDistance:0.###}.";
        return true;
    }

    private bool TryCreateVanillaIkCcdCache(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, VanillaIkManualChain chain, out VanillaIkCcdCache? cache, out string error)
    {
        cache = null;
        error = "";

        VanillaAnimationPreviewScene? scene = EnsureVanillaPreviewScene(row);
        if (scene == null)
        {
            error = "IK needs a loaded vanilla preview scene.";
            return false;
        }

        scene.Scrub(Math.Clamp(keyFrame.Frame, 0, Math.Max(0, scene.QuantityFrames - 1)));

        var infos = new List<VanillaIkPoseInfo>();
        foreach (string elementName in chain.ElementNames)
        {
            if (!TryGetVanillaIkPoseInfo(scene, elementName, out VanillaIkPoseInfo info, out error)) return false;
            infos.Add(info);
        }

        if (infos.Count == 0)
        {
            error = "IK needs at least one chain element.";
            return false;
        }

        VanillaIkPoseInfo endInfo = infos[^1];
        if (!TryGetVanillaDistalEndpointModel(endInfo.Pose, endInfo.Origin, out Vec3d endOrigin))
        {
            error = $"Could not find a distal endpoint for {chain.EndElementName}.";
            return false;
        }

        Vec3d[] jointPositions = new Vec3d[infos.Count + 1];
        for (int index = 0; index < infos.Count; index++)
        {
            jointPositions[index] = infos[index].Origin;
        }
        jointPositions[^1] = endOrigin;

        for (int index = 0; index < jointPositions.Length - 1; index++)
        {
            if (Distance(jointPositions[index], jointPositions[index + 1]) > 0.0001) continue;

            error = $"IK chain segment at {chain.ElementNames[index]} has no usable length.";
            return false;
        }

        TransformGizmoAxes selectedAxes = new(
            SafeNormalize(endInfo.WorldRotation.TransformDirection(new Vec3d(1, 0, 0)), new Vec3d(1, 0, 0)),
            SafeNormalize(endInfo.WorldRotation.TransformDirection(new Vec3d(0, 1, 0)), new Vec3d(0, 1, 0)),
            SafeNormalize(endInfo.WorldRotation.TransformDirection(new Vec3d(0, 0, 1)), new Vec3d(0, 0, 1)));

        AnimationKeyFrameElement[] startElements = chain.ElementNames
            .Select(name => CloneElement(GetVanillaIkElementOrDefault(keyFrame, name)))
            .ToArray();
        AnimationKeyFrameElement selectedStart = CloneElement(GetVanillaIkElementOrDefault(keyFrame, chain.EndElementName));

        cache = new VanillaIkCcdCache(chain, infos.ToArray(), jointPositions, endOrigin, selectedAxes, selectedStart, startElements);
        return true;
    }

    private static AnimationKeyFrameElement GetVanillaIkElementOrDefault(AnimationKeyFrame keyFrame, string elementName)
    {
        if (keyFrame.Elements != null &&
            keyFrame.Elements.TryGetValue(elementName, out AnimationKeyFrameElement? element) &&
            element != null)
        {
            return element;
        }

        return new AnimationKeyFrameElement();
    }

    private static Vec3d GetVanillaIkDesiredEndTarget(VanillaIkCcdCache cache, AnimationKeyFrameElement desiredElement)
    {
        double dx = ((desiredElement.OffsetX ?? 0) - (cache.SelectedStartElement.OffsetX ?? 0)) / 16.0;
        double dy = ((desiredElement.OffsetY ?? 0) - (cache.SelectedStartElement.OffsetY ?? 0)) / 16.0;
        double dz = ((desiredElement.OffsetZ ?? 0) - (cache.SelectedStartElement.OffsetZ ?? 0)) / 16.0;

        return Add(cache.EndOrigin, Add(Add(Scale(cache.SelectedAxes.X, dx), Scale(cache.SelectedAxes.Y, dy)), Scale(cache.SelectedAxes.Z, dz)));
    }

    private static bool TrySolveVanillaIkCcdToTarget(VanillaIkCcdCache cache, Vec3d requestedTarget, out AnimationKeyFrameElement[] solvedElements, out double finalDistance, out string error)
    {
        solvedElements = cache.StartElements.Select(CloneElement).ToArray();
        finalDistance = Distance(cache.EndOrigin, requestedTarget);
        error = "";

        int count = cache.BoneInfos.Count;
        if (count == 0)
        {
            error = "IK needs at least one chain element.";
            return false;
        }

        Vec3d[] joints = cache.JointPositions.Select(point => new Vec3d(point.X, point.Y, point.Z)).ToArray();
        RigIkMatrix3[] rotations = cache.BoneInfos.Select(info => info.WorldRotation).ToArray();
        double initialDistance = Distance(joints[^1], requestedTarget);

        const int maxIterations = 24;
        const double vectorEpsilon = 0.000001;
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            for (int boneIndex = count - 1; boneIndex >= 0; boneIndex--)
            {
                Vec3d origin = joints[boneIndex];
                Vec3d currentVector = Sub(joints[^1], origin);
                Vec3d targetVector = Sub(requestedTarget, origin);
                if (currentVector.LengthSq() < vectorEpsilon || targetVector.LengthSq() < vectorEpsilon) continue;

                RigIkMatrix3 delta = RigIkMatrix3.FromTo(currentVector, targetVector).Orthonormalized();
                for (int jointIndex = boneIndex + 1; jointIndex < joints.Length; jointIndex++)
                {
                    joints[jointIndex] = Add(origin, delta.TransformDirection(Sub(joints[jointIndex], origin)));
                }

                for (int rotationIndex = boneIndex; rotationIndex < rotations.Length; rotationIndex++)
                {
                    rotations[rotationIndex] = delta.Mul(rotations[rotationIndex]).Orthonormalized();
                }

                if (Distance(joints[^1], requestedTarget) <= VanillaIkSolveTolerance) break;
            }

            if (Distance(joints[^1], requestedTarget) <= VanillaIkSolveTolerance) break;
        }

        finalDistance = Distance(joints[^1], requestedTarget);
        if (finalDistance > VanillaIkSolveTolerance && finalDistance >= initialDistance - VanillaIkSolveImprovementEpsilon)
        {
            error = $"IK target did not improve. Remaining distance {finalDistance:0.###}.";
            return false;
        }

        for (int index = 0; index < count; index++)
        {
            RigIkMatrix3 parentWorld = index > 0 ? rotations[index - 1] : cache.BoneInfos[index].ParentWorldRotation;
            RigIkMatrix3 local = parentWorld.Inverted().Mul(rotations[index]).Orthonormalized();
            Vec3d euler = Sub(local.ToEulerDegrees(), cache.BoneInfos[index].BaseRotationDegrees);
            solvedElements[index] = WithVanillaIkRotation(cache.StartElements[index], euler);
        }

        return true;
    }

    private static AnimationKeyFrameElement WithVanillaIkRotation(AnimationKeyFrameElement source, Vec3d rotation)
    {
        AnimationKeyFrameElement result = CloneElement(source);
        result.RotationX = NormalizeVanillaDegrees(rotation.X);
        result.RotationY = NormalizeVanillaDegrees(rotation.Y);
        result.RotationZ = NormalizeVanillaDegrees(rotation.Z);
        CompleteVanillaRotationGroup(result);
        return result;
    }

    private static void ApplyVanillaIkSolvedElements(AnimationKeyFrame keyFrame, VanillaIkManualChain chain, IReadOnlyList<AnimationKeyFrameElement> solvedElements)
    {
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < chain.ElementNames.Count && index < solvedElements.Count; index++)
        {
            keyFrame.Elements[chain.ElementNames[index]] = solvedElements[index];
        }
    }

    private static bool TryGetVanillaIkPoseInfo(VanillaAnimationPreviewScene scene, string elementName, out VanillaIkPoseInfo info, out string error)
    {
        info = default;
        error = "";

        if (!TryFindVanillaPose(scene.Animator.RootPoses, elementName, out ElementPose? pose, out ElementPose? parentPose) || pose?.ForElement == null)
        {
            error = $"Preview pose for {elementName} was not found.";
            return false;
        }

        if (!TryGetVanillaPoseModelOrigin(pose, out Vec3d origin))
        {
            error = $"Could not resolve the model-space origin for {elementName}.";
            return false;
        }

        if (!TryBuildVanillaPoseModelMatrix(pose, out Matrixf worldMatrix))
        {
            error = $"Could not resolve the model matrix for {elementName}.";
            return false;
        }

        RigIkMatrix3 parentWorldRotation = RigIkMatrix3.Identity;
        if (parentPose != null && TryBuildVanillaPoseModelMatrix(parentPose, out Matrixf parentMatrix))
        {
            parentWorldRotation = RigIkMatrix3.FromMatrixf(parentMatrix).Orthonormalized();
        }

        info = new(
            pose,
            origin,
            RigIkMatrix3.FromMatrixf(worldMatrix).Orthonormalized(),
            parentWorldRotation,
            new Vec3d(pose.ForElement.RotationX, pose.ForElement.RotationY, pose.ForElement.RotationZ));
        return true;
    }

    private static bool TryFindVanillaPose(IEnumerable<ElementPose>? poses, string elementName, out ElementPose? result, out ElementPose? parentResult)
    {
        return TryFindVanillaPose(poses, elementName, default!, hasParent: false, out result, out parentResult);
    }

    private static bool TryFindVanillaPose(IEnumerable<ElementPose>? poses, string elementName, ElementPose parent, bool hasParent, out ElementPose? result, out ElementPose? parentResult)
    {
        result = default!;
        parentResult = default!;
        if (poses == null || string.IsNullOrWhiteSpace(elementName)) return false;

        foreach (ElementPose pose in poses)
        {
            if (string.Equals(pose.ForElement?.Name, elementName, StringComparison.OrdinalIgnoreCase))
            {
                result = pose;
                parentResult = hasParent ? parent : default!;
                return true;
            }

            if (TryFindVanillaPose(pose.ChildElementPoses, elementName, pose, hasParent: true, out result, out parentResult)) return true;
        }

        return false;
    }

    private static bool TryGetVanillaPoseModelOrigin(ElementPose pose, out Vec3d origin)
    {
        origin = new Vec3d();
        if (!TryBuildVanillaPoseModelMatrix(pose, out Matrixf matrix)) return false;

        Vec3f localOrigin = GetElementLocalRotationOrigin(pose);
        Vec4f transformed = matrix.TransformVector(new Vec4f(localOrigin.X, localOrigin.Y, localOrigin.Z, 1f));
        origin = new Vec3d(transformed.X, transformed.Y, transformed.Z);
        return true;
    }

    private static bool TryBuildVanillaPoseModelMatrix(ElementPose pose, out Matrixf matrix)
    {
        matrix = new Matrixf();
        if (pose.AnimModelMatrix == null || pose.AnimModelMatrix.Length < 16) return false;

        matrix.Identity();
        matrix.Mul(pose.AnimModelMatrix);
        return true;
    }

    private static bool TryGetVanillaDistalEndpointModel(ElementPose lowerPose, Vec3d jointOrigin, out Vec3d endpoint)
    {
        endpoint = jointOrigin;
        if (lowerPose.ForElement == null) return false;
        if (!TryBuildVanillaPoseModelMatrix(lowerPose, out Matrixf matrix)) return false;

        Vec3f[] localCorners = GetElementLocalBoxCorners(lowerPose.ForElement);
        double best = -1;
        foreach (Vec3f local in localCorners)
        {
            Vec4f transformed = matrix.TransformVector(new Vec4f(local.X, local.Y, local.Z, 1f));
            Vec3d model = new(transformed.X, transformed.Y, transformed.Z);
            double distance = Sub(model, jointOrigin).LengthSq();
            if (distance <= best) continue;

            best = distance;
            endpoint = model;
        }

        return best > 0.000001;
    }

    private static bool TryFindShapeElementPath(Shape? shape, string elementName, out List<ShapeElement> path)
    {
        path = [];
        if (shape?.Elements == null || string.IsNullOrWhiteSpace(elementName)) return false;

        foreach (ShapeElement root in shape.Elements)
        {
            if (TryFindShapeElementPathRecursive(root, elementName, path)) return true;
        }

        path.Clear();
        return false;
    }

    private static bool TryFindShapeElementPathRecursive(ShapeElement current, string elementName, List<ShapeElement> path)
    {
        path.Add(current);
        if (string.Equals(current.Name, elementName, StringComparison.OrdinalIgnoreCase)) return true;

        if (current.Children != null)
        {
            foreach (ShapeElement child in current.Children)
            {
                if (TryFindShapeElementPathRecursive(child, elementName, path)) return true;
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
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

    private static string[] BuildVanillaSymmetryElementUniverse(VanillaAnimationDocument document, VanillaAnimation animation, AnimationKeyFrame keyFrame)
    {
        IEnumerable<string> shapeElements = GetShapeElementNames(document);
        IEnumerable<string> animationElements = (animation.KeyFrames ?? [])
            .Where(frame => frame.Elements != null)
            .SelectMany(frame => frame.Elements!.Keys);
        IEnumerable<string> keyFrameElements = keyFrame.Elements == null ? [] : keyFrame.Elements.Keys;

        return shapeElements
            .Concat(animationElements)
            .Concat(keyFrameElements)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] BuildVanillaSymmetryPairOptions(string selectedElementName, string[] allElements)
    {
        return new[] { "Auto detect" }
            .Concat(allElements.Where(name => !string.Equals(name, selectedElementName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private string GetVanillaSymmetryPairOverride(VanillaAnimationDocument document, string elementName)
    {
        return _vanillaSymmetryPairOverrides.TryGetValue(GetVanillaSymmetryPairOverrideKey(document, elementName), out string? pair)
            ? pair
            : "";
    }

    private void SetVanillaSymmetryPairOverride(VanillaAnimationDocument document, string elementName, string pairElementName)
    {
        ClearVanillaSymmetryPairOverride(document, elementName);
        ClearVanillaSymmetryPairOverride(document, pairElementName);
        _vanillaSymmetryPairOverrides[GetVanillaSymmetryPairOverrideKey(document, elementName)] = pairElementName;
        _vanillaSymmetryPairOverrides[GetVanillaSymmetryPairOverrideKey(document, pairElementName)] = elementName;
    }

    private void ClearVanillaSymmetryPairOverride(VanillaAnimationDocument document, string elementName)
    {
        string key = GetVanillaSymmetryPairOverrideKey(document, elementName);
        if (_vanillaSymmetryPairOverrides.TryGetValue(key, out string? pairElementName))
        {
            string pairKey = GetVanillaSymmetryPairOverrideKey(document, pairElementName);
            if (_vanillaSymmetryPairOverrides.TryGetValue(pairKey, out string? reversePair) &&
                string.Equals(reversePair, elementName, StringComparison.OrdinalIgnoreCase))
            {
                _vanillaSymmetryPairOverrides.Remove(pairKey);
            }
        }

        _vanillaSymmetryPairOverrides.Remove(key);
    }

    private static string GetVanillaSymmetryPairOverrideKey(VanillaAnimationDocument document, string elementName)
    {
        return $"{document.HistoryKey}\u001f{elementName}";
    }

    private bool TryResolveVanillaSymmetryPair(
        VanillaAnimationDocument document,
        string elementName,
        string[] allElements,
        out string pairElementName,
        out VanillaSymmetrySide sourceSide,
        out bool manualPair)
    {
        pairElementName = "";
        sourceSide = InferVanillaSymmetrySide(elementName);
        manualPair = false;

        Dictionary<string, string> elementLookup = BuildVanillaElementLookup(allElements);
        string overridePair = GetVanillaSymmetryPairOverride(document, elementName);
        if (!string.IsNullOrWhiteSpace(overridePair) &&
            elementLookup.TryGetValue(overridePair, out string? resolvedOverridePair) &&
            !string.Equals(resolvedOverridePair, elementName, StringComparison.OrdinalIgnoreCase))
        {
            pairElementName = resolvedOverridePair;
            manualPair = true;
            return true;
        }

        if (TryDetectVanillaSymmetryPairByName(elementName, elementLookup, out string detectedPair, out VanillaSymmetrySide detectedSide))
        {
            pairElementName = detectedPair;
            sourceSide = detectedSide;
            return true;
        }

        return false;
    }

    private static Dictionary<string, string> BuildVanillaElementLookup(IEnumerable<string> allElements)
    {
        Dictionary<string, string> lookup = new(StringComparer.OrdinalIgnoreCase);
        foreach (string element in allElements)
        {
            lookup.TryAdd(element, element);
        }

        return lookup;
    }

    private static bool TryDetectVanillaSymmetryPairByName(string elementName, Dictionary<string, string> elementLookup, out string pairElementName, out VanillaSymmetrySide sourceSide)
    {
        foreach (VanillaSymmetryPairCandidate candidate in EnumerateVanillaSymmetryPairCandidates(elementName))
        {
            if (elementLookup.TryGetValue(candidate.ElementName, out string? resolved) &&
                !string.Equals(resolved, elementName, StringComparison.OrdinalIgnoreCase))
            {
                pairElementName = resolved;
                sourceSide = candidate.SourceSide;
                return true;
            }
        }

        pairElementName = "";
        sourceSide = VanillaSymmetrySide.Unknown;
        return false;
    }

    private static VanillaSymmetrySide InferVanillaSymmetrySide(string elementName)
    {
        return EnumerateVanillaSymmetryPairCandidates(elementName).FirstOrDefault().SourceSide;
    }

    private static IEnumerable<VanillaSymmetryPairCandidate> EnumerateVanillaSymmetryPairCandidates(string elementName)
    {
        foreach (VanillaSymmetryPairCandidate candidate in EnumerateVanillaSymmetryTextPair(elementName, "Left", "Right", VanillaSymmetrySide.Left)) yield return candidate;
        foreach (VanillaSymmetryPairCandidate candidate in EnumerateVanillaSymmetryTextPair(elementName, "Right", "Left", VanillaSymmetrySide.Right)) yield return candidate;
        foreach (VanillaSymmetryPairCandidate candidate in EnumerateVanillaSymmetryTextPair(elementName, "left", "right", VanillaSymmetrySide.Left)) yield return candidate;
        foreach (VanillaSymmetryPairCandidate candidate in EnumerateVanillaSymmetryTextPair(elementName, "right", "left", VanillaSymmetrySide.Right)) yield return candidate;
        foreach (VanillaSymmetryPairCandidate candidate in EnumerateVanillaSymmetryTextPair(elementName, "LEFT", "RIGHT", VanillaSymmetrySide.Left)) yield return candidate;
        foreach (VanillaSymmetryPairCandidate candidate in EnumerateVanillaSymmetryTextPair(elementName, "RIGHT", "LEFT", VanillaSymmetrySide.Right)) yield return candidate;

        foreach ((string leftToken, string rightToken) in new[] { ("_l", "_r"), ("-l", "-r"), (".l", ".r") })
        {
            if (elementName.EndsWith(leftToken, StringComparison.Ordinal))
            {
                yield return new(elementName[..^leftToken.Length] + rightToken, VanillaSymmetrySide.Left);
            }
            if (elementName.EndsWith(rightToken, StringComparison.Ordinal))
            {
                yield return new(elementName[..^rightToken.Length] + leftToken, VanillaSymmetrySide.Right);
            }
        }

        if (elementName.Length > 1 && elementName.EndsWith("L", StringComparison.Ordinal))
        {
            yield return new(elementName[..^1] + "R", VanillaSymmetrySide.Left);
        }
        if (elementName.Length > 1 && elementName.EndsWith("R", StringComparison.Ordinal))
        {
            yield return new(elementName[..^1] + "L", VanillaSymmetrySide.Right);
        }
    }

    private static IEnumerable<VanillaSymmetryPairCandidate> EnumerateVanillaSymmetryTextPair(string elementName, string sourceToken, string targetToken, VanillaSymmetrySide sourceSide)
    {
        int index = elementName.IndexOf(sourceToken, StringComparison.Ordinal);
        while (index >= 0)
        {
            yield return new(elementName[..index] + targetToken + elementName[(index + sourceToken.Length)..], sourceSide);
            index = elementName.IndexOf(sourceToken, index + sourceToken.Length, StringComparison.Ordinal);
        }
    }

    private static AnimationKeyFrameElement MirrorVanillaElement(AnimationKeyFrameElement source)
    {
        return new()
        {
            OffsetX = Negate(source.OffsetX),
            OffsetY = source.OffsetY,
            OffsetZ = source.OffsetZ,
            StretchX = source.StretchX,
            StretchY = source.StretchY,
            StretchZ = source.StretchZ,
            RotationX = source.RotationX,
            RotationY = Negate(source.RotationY),
            RotationZ = Negate(source.RotationZ),
            OriginX = Negate(source.OriginX),
            OriginY = source.OriginY,
            OriginZ = source.OriginZ,
            RotShortestDistanceX = source.RotShortestDistanceX,
            RotShortestDistanceY = source.RotShortestDistanceY,
            RotShortestDistanceZ = source.RotShortestDistanceZ
        };
    }

    private static double? Negate(double? value) => value.HasValue ? -value.Value : null;

    private static int GetVanillaHalfCycleFrames(VanillaAnimation animation)
    {
        return Math.Max(1, (int)Math.Round(animation.QuantityFrames / 2.0, MidpointRounding.AwayFromZero));
    }

    private int GetVanillaLiveSymmetryPhaseFrames(VanillaAnimation animation)
    {
        if (_vanillaLiveSymmetryMode == VanillaLiveSymmetryMode.InPlace)
        {
            return 0;
        }

        int maxPhase = Math.Max(0, Math.Max(1, animation.QuantityFrames) - 1);
        return _vanillaLiveSymmetryPhaseFrames >= 0
            ? Math.Clamp(_vanillaLiveSymmetryPhaseFrames, 0, maxPhase)
            : Math.Clamp(GetVanillaHalfCycleFrames(animation), 0, maxPhase);
    }

    private static int GetVanillaHalfCycleTargetFrame(VanillaAnimation animation, int sourceFrame, int halfCycleFrames)
    {
        return GetVanillaPhaseTargetFrame(animation, sourceFrame, halfCycleFrames);
    }

    private static int GetVanillaPhaseTargetFrame(VanillaAnimation animation, int sourceFrame, int phaseFrames)
    {
        int frameCount = Math.Max(1, animation.QuantityFrames);
        int normalizedSourceFrame = ((sourceFrame % frameCount) + frameCount) % frameCount;
        int normalizedPhaseFrames = ((phaseFrames % frameCount) + frameCount) % frameCount;
        return (normalizedSourceFrame + normalizedPhaseFrames) % frameCount;
    }

    private static AnimationKeyFrame GetOrCreateVanillaTargetKeyFrame(VanillaAnimation animation, int frameNumber, out bool created)
    {
        animation.KeyFrames ??= [];
        AnimationKeyFrame? target = animation.KeyFrames.FirstOrDefault(keyFrame => keyFrame.Frame == frameNumber);
        if (target != null)
        {
            created = false;
            target.Elements ??= new(StringComparer.OrdinalIgnoreCase);
            return target;
        }

        target = new AnimationKeyFrame
        {
            Frame = frameNumber,
            Elements = new(StringComparer.OrdinalIgnoreCase)
        };
        animation.KeyFrames = animation.KeyFrames.Append(target).ToArray();
        created = true;
        return target;
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

    private enum VanillaSymmetrySide
    {
        Unknown,
        Left,
        Right
    }

    private enum VanillaLiveSymmetryMode
    {
        InPlace,
        HalfCycle
    }

    private enum VanillaLiveSymmetryDriver
    {
        SelectedElement,
        LeftDrivesRight,
        RightDrivesLeft
    }

    private enum VanillaIkChainMode
    {
        AutoLimb,
        ManualOverride
    }

    private readonly record struct VanillaSymmetryPairCandidate(string ElementName, VanillaSymmetrySide SourceSide);
    private readonly record struct VanillaSymmetryResult(bool Applied, int Written, int CreatedKeyFrames, int OverwrittenElements, string Message);
    private readonly record struct VanillaIkManualChain(IReadOnlyList<string> ElementNames, string EndElementName, string DisplayName);
    private readonly record struct VanillaIkChainNode(string ElementName, string ParentElementName, int Depth);
    private readonly record struct VanillaIkPoseInfo(ElementPose Pose, Vec3d Origin, RigIkMatrix3 WorldRotation, RigIkMatrix3 ParentWorldRotation, Vec3d BaseRotationDegrees);

    private sealed record VanillaIkCcdCache(
        VanillaIkManualChain Chain,
        IReadOnlyList<VanillaIkPoseInfo> BoneInfos,
        Vec3d[] JointPositions,
        Vec3d EndOrigin,
        TransformGizmoAxes SelectedAxes,
        AnimationKeyFrameElement SelectedStartElement,
        IReadOnlyList<AnimationKeyFrameElement> StartElements);

    private enum VanillaEntitySelectorMode
    {
        Grouped,
        Exact
    }

    private sealed record VanillaEntityOption(
        IReadOnlyList<VanillaEntityMember> Members,
        string Label,
        string Tooltip,
        string Domain,
        string SearchText,
        string GroupKey,
        string GroupKind,
        int HiddenCount)
    {
        public VanillaEntityMember Representative => Members.Count > 0 ? Members[0] : throw new InvalidOperationException("Entity option has no members.");
    }

    private sealed record VanillaEntityMember(
        EntityProperties EntityType,
        string Label,
        string FullLabel,
        string Domain,
        VanillaEntitySourceInfo? Source,
        string MetadataSignature,
        string ShapeSignature,
        bool Hidden,
        string HiddenReason)
    {
        public string Code => EntityType.Code?.ToString() ?? FullLabel;
    }

    private sealed record VanillaEntitySourceInfo(
        AssetLocation Location,
        string AssetPath,
        string SourceCode,
        JObject? SourceJson,
        bool HasVariantGroups,
        bool Hidden,
        string HiddenReason)
    {
        public string Key => $"{Location.Domain}:{AssetPath}";
    }

    private sealed record VanillaGroupTargets(IReadOnlyList<EntityProperties> Targets, int Skipped);

    private sealed class VanillaAnimationIndexService
    {
        private readonly List<VanillaAnimationDocument> _documents = [];
        private readonly Dictionary<string, List<VanillaShapeAnimationEntry>> _shapeAnimationsByCode = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<VanillaEntityOption> _groupedEntityOptions = [];
        private readonly List<VanillaEntityOption> _groupedVisibleEntityOptions = [];
        private readonly List<VanillaEntityOption> _exactEntityOptions = [];
        private readonly List<VanillaEntityOption> _exactVisibleEntityOptions = [];
        private readonly List<string> _allEntityDomains = [];
        private bool _entityListReady;

        public IReadOnlyList<VanillaAnimationDocument> Documents => _documents;
        public IEnumerable<string> AllEntityDomains => _allEntityDomains;
        public VanillaEntityOption? SelectedEntityOption { get; private set; }
        public int SelectedMemberIndex { get; private set; } = -1;
        public string? SelectedEntityLabel => SelectedEntityOption?.Label;
        public bool HasSelectedEntity => SelectedEntityOption != null && SelectedMemberIndex >= 0;
        public string Status { get; private set; } = "Select an entity to index its vanilla animations.";

        public IReadOnlyList<VanillaEntityOption> GetEntityOptions(VanillaEntitySelectorMode mode, bool showHidden)
        {
            return mode switch
            {
                VanillaEntitySelectorMode.Exact => showHidden ? _exactEntityOptions : _exactVisibleEntityOptions,
                _ => showHidden ? _groupedEntityOptions : _groupedVisibleEntityOptions
            };
        }

        public bool IsSelectedEntityOption(VanillaEntityOption option)
        {
            return ReferenceEquals(option, SelectedEntityOption);
        }

        public void EnsureEntityList(ICoreClientAPI api)
        {
            if (_entityListReady) return;

            _groupedEntityOptions.Clear();
            _groupedVisibleEntityOptions.Clear();
            _exactEntityOptions.Clear();
            _exactVisibleEntityOptions.Clear();
            _allEntityDomains.Clear();

            VanillaEntitySourceIndex sourceIndex = VanillaEntitySourceIndex.Build(api);
            List<VanillaEntityMember> members = [];
            foreach (EntityProperties entityType in api.World.EntityTypes ?? [])
            {
                string? code = entityType.Code?.ToString();
                if (string.IsNullOrWhiteSpace(code)) continue;
                string domain = entityType.Code?.Domain ?? "game";
                VanillaEntitySourceInfo? source = sourceIndex.Resolve(entityType);
                bool hidden = source?.Hidden == true;
                string hiddenReason = source?.HiddenReason ?? "";
                members.Add(new(
                    entityType,
                    ImGuiLayoutHelper.CompactAssetCode(code),
                    code,
                    domain,
                    source,
                    BuildMetadataCompatibilitySignature(entityType),
                    BuildShapeCompatibilitySignature(entityType),
                    hidden,
                    hiddenReason));
            }

            _allEntityDomains.AddRange(members.Select(member => member.Domain).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase));
            _exactEntityOptions.AddRange(members.Select(member => BuildEntityOption([member], "exact", "Exact runtime entity")));
            _groupedEntityOptions.AddRange(BuildGroupedEntityOptions(members));
            _exactEntityOptions.Sort(CompareEntityOptions);
            _groupedEntityOptions.Sort(CompareEntityOptions);
            _exactVisibleEntityOptions.AddRange(_exactEntityOptions.Where(option => option.Members.Any(member => !member.Hidden)));
            _groupedVisibleEntityOptions.AddRange(BuildVisibleEntityOptions(_groupedEntityOptions));
            _entityListReady = true;
            Status = $"Loaded {members.Count} entity types into {_groupedEntityOptions.Count} group(s). Select one to index its animations.";
        }

        public void SelectEntity(ICoreClientAPI api, IReadOnlyList<VanillaEntityOption> options, int index, int memberIndex, bool groupEdit)
        {
            EnsureEntityList(api);
            if (index < 0 || index >= options.Count)
            {
                ClearSelection();
                return;
            }

            SelectEntity(api, options[index], memberIndex, groupEdit);
        }

        public void SelectEntity(ICoreClientAPI api, VanillaEntityOption option, int memberIndex, bool groupEdit)
        {
            EnsureEntityList(api);
            if (option.Members.Count == 0)
            {
                ClearSelection();
                return;
            }

            SelectedEntityOption = option;
            SelectedMemberIndex = Math.Clamp(memberIndex, 0, option.Members.Count - 1);
            IndexSelectedEntity(api, option, SelectedMemberIndex, groupEdit);
        }

        public void ReloadSelectedEntity(ICoreClientAPI api, bool groupEdit)
        {
            if (!HasSelectedEntity) return;
            IndexSelectedEntity(api, SelectedEntityOption!, SelectedMemberIndex, groupEdit);
        }

        public void ClearSelection()
        {
            SelectedEntityOption = null;
            SelectedMemberIndex = -1;
            _documents.Clear();
            _shapeAnimationsByCode.Clear();
            Status = "Select an entity to index its vanilla animations.";
        }

        private static IEnumerable<VanillaEntityOption> BuildGroupedEntityOptions(IReadOnlyList<VanillaEntityMember> members)
        {
            List<VanillaEntityOption> options = [];
            HashSet<EntityProperties> grouped = [];

            foreach (IGrouping<string, VanillaEntityMember> sourceGroup in members
                .Where(member => member.Source != null)
                .GroupBy(member => member.Source!.Key, StringComparer.OrdinalIgnoreCase))
            {
                List<VanillaEntityMember> groupMembers = sourceGroup.OrderBy(member => member.Label, StringComparer.OrdinalIgnoreCase).ToList();
                if (groupMembers.Count > 1 || groupMembers[0].Source?.HasVariantGroups == true)
                {
                    options.Add(BuildEntityOption(groupMembers, "source", "Source family"));
                    foreach (VanillaEntityMember member in groupMembers) grouped.Add(member.EntityType);
                }
            }

            List<VanillaEntityMember> remaining = members.Where(member => !grouped.Contains(member.EntityType)).ToList();
            foreach (IGrouping<string, VanillaEntityMember> signatureGroup in remaining
                .Where(member => !string.IsNullOrWhiteSpace(BuildCompatibleEntityGroupKey(member)))
                .GroupBy(BuildCompatibleEntityGroupKey, StringComparer.Ordinal))
            {
                List<VanillaEntityMember> groupMembers = signatureGroup.OrderBy(member => member.Label, StringComparer.OrdinalIgnoreCase).ToList();
                if (groupMembers.Count <= 1) continue;
                options.Add(BuildEntityOption(groupMembers, "compatible", "Compatible animation signature"));
                foreach (VanillaEntityMember member in groupMembers) grouped.Add(member.EntityType);
            }

            foreach (VanillaEntityMember member in members.Where(member => !grouped.Contains(member.EntityType)))
            {
                options.Add(BuildEntityOption([member], "single", "Single runtime entity"));
            }

            return options;
        }

        private static IEnumerable<VanillaEntityOption> BuildVisibleEntityOptions(IEnumerable<VanillaEntityOption> options)
        {
            foreach (VanillaEntityOption option in options)
            {
                List<VanillaEntityMember> visibleMembers = option.Members.Where(member => !member.Hidden).ToList();
                if (visibleMembers.Count == 0) continue;
                yield return visibleMembers.Count == option.Members.Count
                    ? option
                    : BuildEntityOption(visibleMembers, option.GroupKind, option.GroupKind, option.HiddenCount + option.Members.Count - visibleMembers.Count);
            }
        }

        private static VanillaEntityOption BuildEntityOption(IReadOnlyList<VanillaEntityMember> members, string groupKeyPrefix, string groupKind, int extraHiddenCount = 0)
        {
            List<VanillaEntityMember> sorted = members.OrderBy(member => member.Label, StringComparer.OrdinalIgnoreCase).ToList();
            int hiddenCount = sorted.Count(member => member.Hidden) + extraHiddenCount;
            string domain = BuildGroupDomain(sorted);
            string label = sorted.Count == 1
                ? sorted[0].Label
                : $"{BuildGroupBaseLabel(sorted)} ({sorted.Count})";
            string groupKey = $"{groupKeyPrefix}:{string.Join("|", sorted.Select(member => member.FullLabel))}";
            string tooltip = BuildEntityOptionTooltip(sorted, groupKind, hiddenCount);
            string search = $"{label} {tooltip} {string.Join(' ', sorted.Select(member => $"{member.Label} {member.FullLabel} {member.Source?.AssetPath} {member.Source?.SourceCode}"))}";
            return new(sorted, label, tooltip, domain, search, groupKey, groupKind, hiddenCount);
        }

        private static string BuildEntityOptionTooltip(IReadOnlyList<VanillaEntityMember> members, string groupKind, int hiddenCount)
        {
            StringBuilder builder = new();
            builder.Append(groupKind).AppendLine();
            builder.Append("Members: ").Append(members.Count);
            if (hiddenCount > 0) builder.Append(" (hidden/helper: ").Append(hiddenCount).Append(')');
            builder.AppendLine();

            string[] sourceAssets = members
                .Select(member => member.Source?.Key)
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray()!;
            if (sourceAssets.Length > 0)
            {
                builder.Append("Source: ").Append(string.Join(", ", sourceAssets)).AppendLine();
            }

            foreach (VanillaEntityMember member in members.Take(24))
            {
                builder.Append("- ").Append(member.FullLabel);
                if (member.Hidden && !string.IsNullOrWhiteSpace(member.HiddenReason))
                {
                    builder.Append(" (").Append(member.HiddenReason).Append(')');
                }
                builder.AppendLine();
            }

            if (members.Count > 24)
            {
                builder.Append("... ").Append(members.Count - 24).Append(" more");
            }

            return builder.ToString().TrimEnd();
        }

        private static string BuildGroupDomain(IReadOnlyList<VanillaEntityMember> members)
        {
            string[] domains = members.Select(member => member.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return domains.Length == 1 ? domains[0] : "";
        }

        private static string BuildGroupBaseLabel(IReadOnlyList<VanillaEntityMember> members)
        {
            VanillaEntitySourceInfo? commonSource = members[0].Source;
            if (commonSource != null && members.All(member => member.Source?.Key == commonSource.Key))
            {
                string sourceCode = commonSource.SourceCode.Contains(':', StringComparison.Ordinal)
                    ? commonSource.SourceCode
                    : $"{commonSource.Location.Domain}:{commonSource.SourceCode}";
                return ImGuiLayoutHelper.CompactAssetCode(sourceCode);
            }

            string prefix = GetCommonPrefix(members.Select(member => member.Label)).TrimEnd('-', '_', '/', ' ');
            return prefix.Length >= 3 ? prefix : members[0].Label;
        }

        private static string GetCommonPrefix(IEnumerable<string> values)
        {
            using IEnumerator<string> enumerator = values.GetEnumerator();
            if (!enumerator.MoveNext()) return "";
            string prefix = enumerator.Current;
            while (enumerator.MoveNext() && prefix.Length > 0)
            {
                string value = enumerator.Current;
                int length = Math.Min(prefix.Length, value.Length);
                int index = 0;
                while (index < length && char.ToUpperInvariant(prefix[index]) == char.ToUpperInvariant(value[index])) index++;
                prefix = prefix[..index];
            }

            return prefix;
        }

        private static string BuildCompatibleEntityGroupKey(VanillaEntityMember member)
        {
            return string.IsNullOrWhiteSpace(member.MetadataSignature) || string.IsNullOrWhiteSpace(member.ShapeSignature)
                ? ""
                : $"{member.MetadataSignature}\n--shape--\n{member.ShapeSignature}";
        }

        private static int CompareEntityOptions(VanillaEntityOption left, VanillaEntityOption right)
        {
            return string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildMetadataCompatibilitySignature(EntityProperties entityType)
        {
            AnimationMetaData[] animations = entityType.Client?.Animations ?? [];
            if (animations.Length == 0) return "";
            StringBuilder builder = new();
            for (int index = 0; index < animations.Length; index++)
            {
                AnimationMetaData animation = animations[index];
                builder.Append(index)
                    .Append(':')
                    .Append(animation.Code ?? "")
                    .Append("->")
                    .Append(animation.Animation ?? "")
                    .Append('|');
            }

            return builder.ToString();
        }

        private static string BuildShapeCompatibilitySignature(EntityProperties entityType)
        {
            Shape? shape = entityType.Client?.LoadedShapeForEntity ?? entityType.Client?.LoadedShape;
            VanillaAnimation[] animations = shape?.Animations ?? [];
            if (animations.Length == 0) return "";

            StringBuilder builder = new();
            for (int index = 0; index < animations.Length; index++)
            {
                VanillaAnimation animation = animations[index];
                builder.Append(index)
                    .Append(':')
                    .Append(animation.Code ?? animation.Name ?? "")
                    .Append(':')
                    .Append(animation.QuantityFrames)
                    .Append(':');

                foreach (AnimationKeyFrame keyFrame in animation.KeyFrames ?? [])
                {
                    builder.Append(keyFrame.Frame).Append('[');
                    if (keyFrame.Elements != null)
                    {
                        foreach (string elementName in keyFrame.Elements.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                        {
                            builder.Append(elementName).Append(',');
                        }
                    }
                    builder.Append(']');
                }

                builder.Append('|');
            }

            return builder.ToString();
        }

        private sealed class VanillaEntitySourceIndex
        {
            private static readonly string[] TechnicalMetadataMarkers =
            [
                "bot",
                "debug",
                "dev",
                "helper",
                "hidden",
                "internal",
                "technical",
                "test"
            ];

            private readonly Dictionary<string, VanillaEntitySourceInfo> _sourcesByCode = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<VanillaEntitySourceInfo> _sources = [];

            public static VanillaEntitySourceIndex Build(ICoreClientAPI api)
            {
                VanillaEntitySourceIndex index = new();
                foreach (IAsset asset in api.Assets.AllAssets.Values)
                {
                    if (asset?.Location == null) continue;
                    string assetPath = asset.Location.Path.Replace('\\', '/');
                    if (!assetPath.StartsWith("entities/", StringComparison.OrdinalIgnoreCase) ||
                        !assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    JObject? json = TryParseJsonObject(ReadAssetText(asset));
                    string? sourceCode = json?["code"]?.ToString();
                    if (json == null || string.IsNullOrWhiteSpace(sourceCode)) continue;

                    bool hidden = TryGetHiddenReason(json, out string hiddenReason);
                    VanillaEntitySourceInfo source = new(
                        new AssetLocation(asset.Location.Domain, assetPath),
                        assetPath,
                        StripCodeDomain(sourceCode),
                        json,
                        json["variantgroups"] is JArray { Count: > 0 },
                        hidden,
                        hiddenReason);
                    index._sources.Add(source);

                    index.Register(source, source.SourceCode);
                    foreach (string entityCode in ExpandEntityCodes(api, source.Location.Domain, json, source.SourceCode))
                    {
                        index.Register(source, entityCode);
                    }
                }

                index._sources.Sort((left, right) => right.SourceCode.Length.CompareTo(left.SourceCode.Length));
                return index;
            }

            public VanillaEntitySourceInfo? Resolve(EntityProperties entityType)
            {
                if (entityType.Code == null) return null;
                string fullCode = NormalizeEntityCode(entityType.Code.Domain, entityType.Code.Path);
                if (_sourcesByCode.TryGetValue(fullCode, out VanillaEntitySourceInfo? exact))
                {
                    return exact;
                }

                string path = entityType.Code.Path;
                foreach (VanillaEntitySourceInfo source in _sources)
                {
                    if (string.Equals(path, source.SourceCode, StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith(source.SourceCode + "-", StringComparison.OrdinalIgnoreCase))
                    {
                        return source;
                    }
                }

                return null;
            }

            private void Register(VanillaEntitySourceInfo source, string code)
            {
                if (string.IsNullOrWhiteSpace(code)) return;
                _sourcesByCode[NormalizeEntityCode(source.Location.Domain, code)] = source;
            }

            private static IEnumerable<string> ExpandEntityCodes(ICoreClientAPI api, string domain, JObject sourceJson, string sourceCode)
            {
                if (sourceJson["variantgroups"] is not JArray groups || groups.Count == 0)
                {
                    yield return sourceCode;
                    yield break;
                }

                List<VanillaVariantGroup> variantGroups = [];
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
                string path = EnsureJsonPath($"worldproperties/{loadFromProperties.Trim().TrimStart('/')}");
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

            private static IEnumerable<Dictionary<string, string>> BuildVariantCombinations(IReadOnlyList<VanillaVariantGroup> groups)
            {
                List<Dictionary<string, string>> combinations = [new(StringComparer.OrdinalIgnoreCase)];
                foreach (VanillaVariantGroup group in groups)
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

            private static string BuildVariantCode(string sourceCode, IReadOnlyList<VanillaVariantGroup> groups, IReadOnlyDictionary<string, string> states)
            {
                string code = sourceCode;
                List<string> suffixes = [];
                foreach (VanillaVariantGroup group in groups)
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

            private static bool TryGetHiddenReason(JObject source, out string reason)
            {
                List<string> evidence = [];
                if (source["tags"] is JArray tags)
                {
                    foreach (string tag in tags.Select(token => token.ToString()))
                    {
                        if (HasTechnicalMarker(tag)) evidence.Add($"tag:{tag}");
                    }
                }

                string? className = source["class"]?.ToString();
                if (!string.IsNullOrWhiteSpace(className) && HasTechnicalMarker(className))
                {
                    evidence.Add($"class:{className}");
                }

                if (source["attributes"] is JObject attributes)
                {
                    foreach (JProperty property in attributes.Properties())
                    {
                        if (HasTechnicalMarker(property.Name))
                        {
                            evidence.Add($"attribute:{property.Name}");
                        }
                    }
                }

                reason = string.Join(", ", evidence.Take(3));
                return evidence.Count > 0;
            }

            private static bool HasTechnicalMarker(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                return EnumerateMetadataTokens(value).Any(token => TechnicalMetadataMarkers.Contains(token, StringComparer.Ordinal));
            }

            private static IEnumerable<string> EnumerateMetadataTokens(string value)
            {
                StringBuilder token = new();
                char previous = '\0';
                foreach (char ch in value)
                {
                    if (!char.IsLetterOrDigit(ch))
                    {
                        if (token.Length > 0)
                        {
                            yield return token.ToString();
                            token.Clear();
                        }
                        previous = '\0';
                        continue;
                    }

                    if (char.IsUpper(ch) && token.Length > 0 && char.IsLower(previous))
                    {
                        yield return token.ToString();
                        token.Clear();
                    }

                    token.Append(char.ToLowerInvariant(ch));
                    previous = ch;
                }

                if (token.Length > 0)
                {
                    yield return token.ToString();
                }
            }

            private static string NormalizeEntityCode(string defaultDomain, string code)
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

            private sealed record VanillaVariantGroup(string Code, IReadOnlyList<string> States);
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

        private void IndexSelectedEntity(ICoreClientAPI api, VanillaEntityOption option, int memberIndex, bool groupEdit)
        {
            VanillaEntityMember selectedMember = option.Members[Math.Clamp(memberIndex, 0, option.Members.Count - 1)];
            EntityProperties entityType = selectedMember.EntityType;
            try
            {
                _documents.Clear();
                _shapeAnimationsByCode.Clear();

                AnimationMetaData[]? metadata = entityType.Client?.Animations;
                Shape? shape = entityType.Client?.LoadedShapeForEntity ?? entityType.Client?.LoadedShape;
                string entityCode = entityType.Code?.ToString() ?? $"entity-{entityType.Id}";
                string groupLabel = groupEdit && option.Members.Count > 1 ? option.Label : ImGuiLayoutHelper.CompactAssetCode(entityCode);

                JObject? entitySourceJson = selectedMember.Source?.SourceJson ?? TryLoadJson(api, GetEntityAssetLocation(entityType));
                AssetLocation? entityAssetLocation = selectedMember.Source?.Location ?? GetEntityAssetLocation(entityType);
                AssetLocation? shapeAssetLocation = GetShapeAssetLocation(entityType);
                JObject? shapeSourceJson = TryLoadJson(api, shapeAssetLocation);
                IReadOnlyList<VanillaEntityMember> editMembers = groupEdit ? option.Members : [selectedMember];
                VanillaGroupTargets shapeTargets = BuildGroupTargets(editMembers, selectedMember, VanillaDocumentKind.Shape);
                VanillaGroupTargets metadataTargets = BuildGroupTargets(editMembers, selectedMember, VanillaDocumentKind.EntityMetadata);

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
                        SourceJson = shapeSourceJson,
                        GroupLabel = groupLabel,
                        RuntimeTargetEntities = shapeTargets.Targets,
                        RuntimeSkippedMembers = shapeTargets.Skipped,
                        RuntimeGroupKind = option.GroupKind
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
                    Domain = entityAssetLocation?.Domain ?? entityType.Code?.Domain ?? "game",
                    AssetPath = entityAssetLocation?.Path ?? $"entities/{entityType.Code?.Path ?? entityCode}.json",
                    DisplayPath = entityCode,
                    EntityCode = entityCode,
                    EntityType = entityType,
                    Shape = shape,
                    SourceJson = entitySourceJson,
                    GroupLabel = groupLabel,
                    RuntimeTargetEntities = metadataTargets.Targets,
                    RuntimeSkippedMembers = metadataTargets.Skipped,
                    RuntimeGroupKind = option.GroupKind
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
                string targetStatus = groupEdit && option.Members.Count > 1
                    ? $" Group edit targets: metadata {metadataTargets.Targets.Count}/{editMembers.Count}, shape {shapeTargets.Targets.Count}/{editMembers.Count}."
                    : "";
                Status = $"Indexed {entityCode}: {shapeCount} shape animations, {metadataCount} metadata entries.{targetStatus}";
            }
            catch (Exception exception)
            {
                _documents.Clear();
                _shapeAnimationsByCode.Clear();
                Status = $"Could not index {entityType.Code}: {exception.Message}";
                LoggerUtil.Warn(api, this, $"Could not index vanilla entity animation '{entityType.Code}': {exception}");
            }
        }

        private static VanillaGroupTargets BuildGroupTargets(IReadOnlyList<VanillaEntityMember> members, VanillaEntityMember selected, VanillaDocumentKind kind)
        {
            string selectedSignature = kind == VanillaDocumentKind.Shape ? selected.ShapeSignature : selected.MetadataSignature;
            List<EntityProperties> targets = [];
            int skipped = 0;
            foreach (VanillaEntityMember member in members)
            {
                string signature = kind == VanillaDocumentKind.Shape ? member.ShapeSignature : member.MetadataSignature;
                if (!string.IsNullOrWhiteSpace(selectedSignature) &&
                    string.Equals(signature, selectedSignature, StringComparison.Ordinal))
                {
                    targets.Add(member.EntityType);
                }
                else
                {
                    skipped++;
                }
            }

            if (targets.Count == 0)
            {
                targets.Add(selected.EntityType);
                skipped = Math.Max(0, members.Count - 1);
            }

            return new(targets, skipped);
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
            IAsset? asset = api.Assets.TryGet(location, true);
            return TryParseJsonObject(ReadAssetText(asset));
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
        public string GroupLabel { get; init; } = "";
        public IReadOnlyList<EntityProperties> RuntimeTargetEntities { get; init; } = [];
        public int RuntimeSkippedMembers { get; init; }
        public string RuntimeGroupKind { get; init; } = "";
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
        private readonly Dictionary<string, AnimationMetaData> _ghostAnimationsByAnimCode = new(StringComparer.OrdinalIgnoreCase);
        private Shape _shape;
        private AnimationMetaData _metadata;
        private AnimationMetaData _ghostMetadata;
        private VanillaAnimation _animation;
        private string _activeAnimationCode;
        private ClientAnimator _animator;
        private ClientAnimator _ghostAnimator;
        private readonly MeshData _previewMeshData;
        private readonly MultiTextureMeshRef _meshRef;
        private MultiTextureMeshRef? _firstPersonMeshRef;
        private MultiTextureMeshRef? _immersiveFirstPersonMeshRef;
        private bool _classicFirstPersonBuildAttempted;
        private bool _immersiveFirstPersonBuildAttempted;
        private readonly bool _classicFirstPersonSupported;
        private readonly bool _immersiveFirstPersonSupported;
        private VanillaPreviewMode _previewMode = VanillaPreviewMode.Orbit;
        private long _renderRevision;
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
            _ghostMetadata = CloneAnimationMetaData(metadata);
            _ghostMetadata.Animation = _activeAnimationCode;
            _ghostAnimator = CreatePreviewAnimator(shape, animation, key);
            _previewMeshData = meshes.PreviewMeshData;
            _meshRef = meshes.Orbit;
            _firstPersonMeshRef = meshes.FirstPerson;
            _immersiveFirstPersonMeshRef = meshes.ImmersiveFirstPerson;
            _classicFirstPersonSupported = meshes.ClassicFirstPersonSupported;
            _immersiveFirstPersonSupported = meshes.ImmersiveFirstPersonSupported;
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
            _ghostAnimationsByAnimCode[_activeAnimationCode] = _ghostMetadata;
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
        public ClientAnimator GhostAnimator => _ghostAnimator;
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
        public bool ClassicFirstPersonAvailable => IsUsableMesh(_firstPersonMeshRef) || (_classicFirstPersonSupported && !_classicFirstPersonBuildAttempted);
        public bool ImmersiveFirstPersonAvailable => IsUsableMesh(_immersiveFirstPersonMeshRef) || (_immersiveFirstPersonSupported && !_immersiveFirstPersonBuildAttempted);
        public bool FirstPersonAvailable => ClassicFirstPersonAvailable || ImmersiveFirstPersonAvailable;
        public VanillaPreviewMode PreviewMode => _previewMode;
        public long RenderRevision => _renderRevision;

        public MultiTextureMeshRef GetMeshRef(VanillaPreviewMode mode)
        {
            return mode switch
            {
                VanillaPreviewMode.FirstPerson when IsUsableMesh(_firstPersonMeshRef) => _firstPersonMeshRef!,
                VanillaPreviewMode.ImmersiveFirstPerson when IsUsableMesh(_immersiveFirstPersonMeshRef) => _immersiveFirstPersonMeshRef!,
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
            ClientAnimator animator = CreatePreviewAnimator(shape, animation, row.Key);
            VanillaPreviewMeshSet meshes = BuildPreviewMeshes(api, row, shape, animator, out int textureId);
            VanillaModelBounds bounds = CalculateModelBounds(shape);
            VanillaGuiTransform guiTransform = GetGuiTransform(row);
            string status = $"Loaded {row.Label}. Mesh parts: {meshes.Orbit.meshrefs?.Length ?? 0}. First-person: {(meshes.ClassicFirstPersonSupported ? "classic lazy" : "not available")}, {(meshes.ImmersiveFirstPersonSupported ? "immersive lazy" : "no immersive mesh")}. Bounds: {bounds.Width:0.00} x {bounds.Height:0.00} x {bounds.Depth:0.00}.";
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
            _ghostMetadata = CloneAnimationMetaData(metadata);
            _activeAnimationCode = GetAnimationCode(animation, metadata);
            _metadata.Animation = _activeAnimationCode;
            _ghostMetadata.Animation = _activeAnimationCode;
            _animator = CreatePreviewAnimator(shape, animation, row.Key);
            _ghostAnimator = CreatePreviewAnimator(shape, animation, row.Key);
            ApplyBounds(CalculateModelBounds(shape));
            ApplyGuiTransform(GetGuiTransform(row));
            FirstPersonFovDegrees = Math.Clamp(_api.Settings.Int["fpHandsFoV"] > 0 ? _api.Settings.Int["fpHandsFoV"] : 75, 25, 130);
            FirstPersonYOffset = _api.Settings.Float["fpHandsYOffset"];
            _activeAnimationsByAnimCode.Clear();
            _activeAnimationsByAnimCode[_activeAnimationCode] = _metadata;
            _ghostAnimationsByAnimCode.Clear();
            _ghostAnimationsByAnimCode[_activeAnimationCode] = _ghostMetadata;
            QuantityFrames = Math.Max(1, animation.QuantityFrames);
            CurrentFrame = Math.Clamp(CurrentFrame, 0, Math.Max(0, QuantityFrames - 1));
            ForceEvaluatePose(CurrentFrame);
        }

        public void SetPreviewMode(VanillaBrowserRow row, VanillaPreviewMode mode)
        {
            if ((mode == VanillaPreviewMode.FirstPerson && !EnsureFirstPersonMesh(immersive: false)) ||
                (mode == VanillaPreviewMode.ImmersiveFirstPerson && !EnsureFirstPersonMesh(immersive: true)))
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
            _ghostMetadata = CloneAnimationMetaData(metadata);
            _activeAnimationCode = GetAnimationCode(animation, metadata);
            _metadata.Animation = _activeAnimationCode;
            _ghostMetadata.Animation = _activeAnimationCode;
            _animator = CreatePreviewAnimator(_shape, animation, row.Key);
            _ghostAnimator = CreatePreviewAnimator(_shape, animation, row.Key);
            _activeAnimationsByAnimCode.Clear();
            _activeAnimationsByAnimCode[_activeAnimationCode] = _metadata;
            _ghostAnimationsByAnimCode.Clear();
            _ghostAnimationsByAnimCode[_activeAnimationCode] = _ghostMetadata;
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

            MarkRenderDirty();
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
            MarkRenderDirty();
        }

        public bool TryEvaluateGhostPose(float frame)
        {
            if (_disposed) return false;

            float ghostFrame = Math.Clamp(frame, 0, Math.Max(0, QuantityFrames - 1));
            _ghostAnimationsByAnimCode[_activeAnimationCode] = _ghostMetadata;
            _ghostMetadata.StartFrameOnce = ghostFrame;
            _ghostAnimator.OnFrame(_ghostAnimationsByAnimCode, 0.001f);

            RunningAnimation? state = _ghostAnimator.GetAnimationState(_activeAnimationCode);
            if (state != null)
            {
                state.meta = _ghostMetadata;
                state.EasingFactor = 1f;
                state.CurrentFrame = ghostFrame;
                state.Iterations = ghostFrame >= QuantityFrames - 1 ? 1 : 0;
            }

            _ghostMetadata.StartFrameOnce = ghostFrame;
            _ghostAnimator.OnFrame(_ghostAnimationsByAnimCode, 0f);
            state = _ghostAnimator.GetAnimationState(_activeAnimationCode);
            if (state != null)
            {
                state.meta = _ghostMetadata;
                state.EasingFactor = 1f;
                state.CurrentFrame = ghostFrame;
                state.Iterations = ghostFrame >= QuantityFrames - 1 ? 1 : 0;
            }

            return _ghostAnimator.Matrices != null;
        }

        private void MarkRenderDirty()
        {
            unchecked
            {
                _renderRevision++;
            }
        }

        private bool EnsureFirstPersonMesh(bool immersive)
        {
            if (immersive)
            {
                if (IsUsableMesh(_immersiveFirstPersonMeshRef)) return true;
                if (!_immersiveFirstPersonSupported || _immersiveFirstPersonBuildAttempted) return false;

                _immersiveFirstPersonBuildAttempted = true;
                _immersiveFirstPersonMeshRef = TryBuildPlayerFirstPersonMesh(_api, _previewMeshData, _animator, immersive: true);
                if (IsUsableMesh(_immersiveFirstPersonMeshRef))
                {
                    MarkRenderDirty();
                    return true;
                }

                Status = $"{Status} Immersive first-person mesh could not be built.";
                return false;
            }

            if (IsUsableMesh(_firstPersonMeshRef)) return true;
            if (!_classicFirstPersonSupported || _classicFirstPersonBuildAttempted) return false;

            _classicFirstPersonBuildAttempted = true;
            _firstPersonMeshRef = TryBuildPlayerFirstPersonMesh(_api, _previewMeshData, _animator, immersive: false);
            if (IsUsableMesh(_firstPersonMeshRef))
            {
                MarkRenderDirty();
                return true;
            }

            Status = $"{Status} Classic first-person mesh could not be built.";
            return false;
        }

        private static bool IsUsableMesh(MultiTextureMeshRef? meshRef)
        {
            return meshRef is { Disposed: false, Initialized: true };
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

        private static ClientAnimator CreatePreviewAnimator(Shape shape, VanillaAnimation animation, string shapeName)
        {
            if (shape.Elements == null || shape.Elements.Length == 0)
            {
                throw new InvalidOperationException($"Preview shape '{shapeName}' has no elements for its animator.");
            }

            if (animation == null)
            {
                throw new InvalidOperationException($"Preview shape '{shapeName}' has no selected animation for its animator.");
            }

            return new ClientAnimator(() => 1, [animation], shape.Elements, shape.JointsById, null, null);
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

            return new(
                orbit,
                null,
                null,
                mesh,
                HasPlayerFirstPersonMeshJoints(animator, immersive: false),
                HasPlayerFirstPersonMeshJoints(animator, immersive: true),
                mesh.VerticesCount,
                mesh.IndicesCount);
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

        private static bool HasPlayerFirstPersonMeshJoints(ClientAnimator animator, bool immersive)
        {
            HashSet<int> jointIds = [];
            if (immersive)
            {
                LoadJointIdsRecursive(animator.GetPosebyName("Neck", StringComparison.InvariantCultureIgnoreCase), jointIds);
                return jointIds.Count > 0;
            }

            LoadJointIdsRecursive(animator.GetPosebyName("UpperArmR", StringComparison.InvariantCultureIgnoreCase), jointIds);
            LoadJointIdsRecursive(animator.GetPosebyName("UpperArmL", StringComparison.InvariantCultureIgnoreCase), jointIds);
            return jointIds.Count > 0;
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

    private readonly record struct VanillaViewportElementHit(
        string ElementName,
        NVector2[] BoundsCorners,
        NVector2 Center,
        double Distance,
        float ScreenArea,
        int HierarchyDepth);

    private sealed record VanillaPreviewMeshSet(
        MultiTextureMeshRef Orbit,
        MultiTextureMeshRef? FirstPerson,
        MultiTextureMeshRef? ImmersiveFirstPerson,
        MeshData PreviewMeshData,
        bool ClassicFirstPersonSupported,
        bool ImmersiveFirstPersonSupported,
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

    private readonly record struct VanillaPreviewGhost(bool Enabled, float Frame, float Opacity, float Red, float Green, float Blue, string Label)
    {
        public static VanillaPreviewGhost Disabled { get; } = new(false, 0f, 0f, 0f, 0f, 0f, "");
    }

    private readonly record struct VanillaPreviewRenderKey(
        string SceneKey,
        long RenderRevision,
        int Width,
        int Height,
        float Yaw,
        float Pitch,
        float Zoom,
        float PanX,
        float PanY,
        VanillaPreviewMode Mode,
        bool WorldLighting,
        string GhostKey);

    private sealed class VanillaAnimationViewport3DRenderer : IDisposable
    {
        private readonly ICoreClientAPI _api;
        private FrameBufferRef? _frameBuffer;
        private VanillaPreviewRenderKey? _lastRenderKey;
        private int _lastTextureId;
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
            ClearRenderCache();
            _lastFrameLogKey = "";
            _lastSkipLogKey = "";
        }

        private static string BuildGhostRenderKey(IReadOnlyList<VanillaPreviewGhost> ghosts)
        {
            if (ghosts.Count == 0) return "";
            return string.Join(
                "|",
                ghosts
                    .Where(ghost => ghost.Enabled)
                    .Select(ghost => $"{ghost.Frame:0.###}:{ghost.Opacity:0.###}:{ghost.Red:0.###}:{ghost.Green:0.###}:{ghost.Blue:0.###}:{ghost.Label}"));
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
            IReadOnlyList<VanillaPreviewGhost> ghosts,
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
            VanillaPreviewRenderKey renderKey = new(
                scene.Key,
                scene.RenderRevision,
                framebufferWidth,
                framebufferHeight,
                yaw,
                pitch,
                zoom,
                panX,
                panY,
                mode,
                worldLighting,
                BuildGhostRenderKey(ghosts));
            if (_lastTextureId > 0 &&
                _lastRenderKey == renderKey &&
                _frameBuffer is { Disposed: false, ColorTextureIds.Length: > 0 })
            {
                return _lastTextureId;
            }

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
                foreach (VanillaPreviewGhost ghost in ghosts)
                {
                    if (!ghost.Enabled || !scene.TryEvaluateGhostPose(ghost.Frame)) continue;

                    render.GLDepthMask(false);
                    SetUniform(shader, "renderColor", new Vec4f(ghost.Red, ghost.Green, ghost.Blue, Math.Clamp(ghost.Opacity, 0.05f, 0.8f)));
                    if (shader.UBOs != null && shader.UBOs.TryGetValue("Animation", out UBORef ghostAnimationUbo) && scene.GhostAnimator.Matrices != null)
                    {
                        ghostAnimationUbo.Update(scene.GhostAnimator.Matrices, 0, scene.GhostAnimator.MaxJointId * 16 * 4);
                    }

                    render.RenderMultiTextureMesh(meshRef, "entityTex", 0);
                    render.GLDepthMask(true);
                    SetUniform(shader, "renderColor", ColorUtil.WhiteArgbVec);
                }
                glError = GL.GetError();
                shader.Stop();
                shader = null;
                previous?.Use();
                LogVerboseFrame(scene, mode, meshRef, frameBuffer, framebufferWidth, framebufferHeight, shaderName, frameBufferStatus, glError, verboseLogs);
                _lastRenderKey = renderKey;
                _lastTextureId = frameBuffer.ColorTextureIds[0];
                return _lastTextureId;
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
            if (_frameBuffer != null && !_frameBuffer.Disposed)
            {
                _api.Render.DestroyFrameBuffer(_frameBuffer);
            }

            _frameBuffer = null;
            ClearRenderCache();
        }

        private void ClearRenderCache()
        {
            _lastRenderKey = null;
            _lastTextureId = 0;
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
                string relativePath = Path.Combine("vanilla", "assets", document.Domain, document.AssetPath.Replace('/', Path.DirectorySeparatorChar));
                string outputPath = GetToolAuthoredAssetPath("animations", relativePath);

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
