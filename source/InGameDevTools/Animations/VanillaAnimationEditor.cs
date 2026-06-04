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
    private bool _vanillaPreviewMeshRebuildPending;
    private string _vanillaPreviewMeshRebuildPendingRowKey = "";
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
    private NVector3 _vanillaViewportGizmoDragModelDirection = NVector3.UnitX;
    private float _vanillaViewportGizmoDragScale = 1f;
    private VanillaGizmoTranslationBasis _vanillaViewportGizmoDragTranslationBasis = VanillaGizmoTranslationBasis.Identity;
    private NVector2 _vanillaViewportGizmoDragCenter;
    private double _vanillaViewportGizmoDragLastAngleRadians;
    private double _vanillaViewportGizmoDragAccumulatedDegrees;
    private double _vanillaViewportGizmoDragRingScreenSign = -1.0;
    private double _vanillaViewportGizmoDragStartValue;
    private double _vanillaViewportGizmoDragStartOffsetX;
    private double _vanillaViewportGizmoDragStartOffsetY;
    private double _vanillaViewportGizmoDragStartOffsetZ;
    private double _vanillaViewportGizmoDragStartRotationX;
    private double _vanillaViewportGizmoDragStartRotationY;
    private double _vanillaViewportGizmoDragStartRotationZ;
    private Vec3d _vanillaViewportGizmoDragBaseRotationDegrees = new();
    private RigIkMatrix3 _vanillaViewportGizmoDragRotationParentBasis = RigIkMatrix3.Identity;
    private TransformGizmoSpace _vanillaViewportGizmoDragSpace = TransformGizmoSpace.World;
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
    private bool _vanillaIkPreserveDraggedPartRotation = true;
    private bool _vanillaIkLockMoveToDragAxis = true;
    private VanillaIkChainMode _vanillaIkMode = VanillaIkChainMode.AutoConservative;
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

        ImGui.TextDisabled($"Showing {rows.Count} / {_vanillaBrowserAllRows.Count} indexed animations");

        if (ImGui.CollapsingHeader("Actions##vanilla-browser-actions"))
        {
            if (_vanillaIndex.HasSelectedEntity && ImGui.Button("Reload selected entity##vanilla", new NVector2(-1, 0)))
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
        VanillaAnimationDocument? document = (row.ShapeAnimation ?? row.MetadataEntry?.ResolveCurrentShape())?.Document ?? row.Document;
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
            if (!IsKnownVanillaShapeElement(document, _vanillaSelection.ElementName))
            {
                _vanillaSelection.ElementName = "";
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(_vanillaSelection.ElementName))
        {
            _vanillaSelection.ElementName = keyFrame.Elements.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).First();
            return;
        }

        if (!keyFrame.Elements.ContainsKey(_vanillaSelection.ElementName) &&
            !IsKnownVanillaShapeElement(document, _vanillaSelection.ElementName))
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
        HashSet<string> keyFrameElementNames = new(keyFrame.Elements.Keys, StringComparer.OrdinalIgnoreCase);
        HashSet<string> knownElementNames = new(knownElements, StringComparer.OrdinalIgnoreCase);
        string[] elementNames = knownElements
            .Concat(keyFrame.Elements.Keys)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (elementNames.Length == 0)
        {
            ImGui.TextDisabled("No shape elements are available for this animation.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_vanillaSelection.ElementName) ||
            (!keyFrameElementNames.Contains(_vanillaSelection.ElementName) && !knownElementNames.Contains(_vanillaSelection.ElementName)))
        {
            _vanillaSelection.ElementName = elementNames[0];
        }

        int selectedElementIndex = Math.Max(0, Array.FindIndex(elementNames, name => string.Equals(name, _vanillaSelection.ElementName, StringComparison.OrdinalIgnoreCase)));
        if (selectedElementIndex >= elementNames.Length) selectedElementIndex = 0;
        DrawVanillaElementList(elementNames, selectedElementIndex, keyFrameElementNames);

        bool selectedInKeyFrame = keyFrame.Elements.TryGetValue(_vanillaSelection.ElementName, out AnimationKeyFrameElement? element) && element != null;
        bool selectedKnownShapeElement = knownElementNames.Contains(_vanillaSelection.ElementName);

        bool canAddSelected = !selectedInKeyFrame && selectedKnownShapeElement;
        if (!canAddSelected) ImGui.BeginDisabled();
        if (ImGui.Button("Add selected channel##vanilla-element"))
        {
            element = new AnimationKeyFrameElement();
            keyFrame.Elements[_vanillaSelection.ElementName] = element;
            selectedInKeyFrame = true;
            keyFrameElementNames.Add(_vanillaSelection.ElementName);
            MarkVanillaDirty(document);
            RefreshVanillaPreviewAfterEdit(row, _vanillaSelection.ElementName);
        }
        if (!canAddSelected) ImGui.EndDisabled();

        ImGui.SameLine();
        string[] missingKnownElements = knownElements.Where(name => !keyFrameElementNames.Contains(name)).ToArray();
        if (missingKnownElements.Length == 0) ImGui.BeginDisabled();
        if (ImGui.Button("Add all shape elements##vanilla-element"))
        {
            if (AddMissingVanillaElementsToKeyFrame(keyFrame, missingKnownElements, out string[] addedNames) > 0)
            {
                MarkVanillaDirty(document);
                RefreshVanillaPreviewAfterEdit(row, addedNames);
                _vanillaStatus = $"Added {addedNames.Length} shape element channel(s) to frame {keyFrame.Frame}.";
                keyFrameElementNames.UnionWith(addedNames);
            }
        }
        if (missingKnownElements.Length == 0) ImGui.EndDisabled();

        ImGui.SameLine();
        int prunableCount = keyFrame.Elements.Count(entry => IsUnchangedVanillaElement(entry.Value));
        if (prunableCount == 0) ImGui.BeginDisabled();
        if (ImGui.Button("Prune unchanged##vanilla-element"))
        {
            int removed = PruneUnchangedVanillaElements(keyFrame, out string[] removedNames);
            if (removed > 0)
            {
                MarkVanillaDirty(document);
                RefreshVanillaPreviewAfterEdit(row);
                _vanillaStatus = $"Pruned {removed} unchanged element channel(s) from frame {keyFrame.Frame}.";
                keyFrameElementNames.ExceptWith(removedNames);
                return;
            }
        }
        if (prunableCount == 0) ImGui.EndDisabled();

        if (!selectedInKeyFrame)
        {
            if (!selectedKnownShapeElement) return;
            element = new AnimationKeyFrameElement();
            ImGui.TextDisabled($"{_vanillaSelection.ElementName} is virtual in this keyframe; editing it will add only this channel.");
        }
        if (element == null) return;

        if (!selectedInKeyFrame) ImGui.BeginDisabled();
        if (ImGui.Button("Remove element##vanilla-element"))
        {
            keyFrame.Elements.Remove(_vanillaSelection.ElementName);
            MarkVanillaDirty(document);
            RefreshVanillaPreviewAfterEdit(row);
            if (!selectedInKeyFrame) ImGui.EndDisabled();
            return;
        }
        if (!selectedInKeyFrame) ImGui.EndDisabled();

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
            element = GetOrCreateVanillaEditableKeyFrameElement(keyFrame, _vanillaSelection.ElementName, element);
            CompleteVanillaElementTransformGroups(element);
            ApplyVanillaElementEdit(row, entry, keyFrame, _vanillaSelection.ElementName);
        }
    }

    private static int AddMissingVanillaElementsToKeyFrame(AnimationKeyFrame keyFrame, IEnumerable<string> elementNames, out string[] addedNames)
    {
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        List<string> added = [];
        foreach (string elementName in elementNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (keyFrame.Elements.ContainsKey(elementName)) continue;

            keyFrame.Elements[elementName] = new AnimationKeyFrameElement();
            added.Add(elementName);
        }

        addedNames = added.ToArray();
        return addedNames.Length;
    }

    private static int PruneUnchangedVanillaElements(AnimationKeyFrame keyFrame, out string[] removedNames)
    {
        if (keyFrame.Elements == null || keyFrame.Elements.Count == 0)
        {
            removedNames = [];
            return 0;
        }

        removedNames = keyFrame.Elements
            .Where(entry => IsUnchangedVanillaElement(entry.Value))
            .Select(entry => entry.Key)
            .ToArray();

        foreach (string name in removedNames)
        {
            keyFrame.Elements.Remove(name);
        }

        return removedNames.Length;
    }

    private static bool IsUnchangedVanillaElement(AnimationKeyFrameElement element)
    {
        return element.OffsetX == null &&
            element.OffsetY == null &&
            element.OffsetZ == null &&
            element.StretchX == null &&
            element.StretchY == null &&
            element.StretchZ == null &&
            element.RotationX == null &&
            element.RotationY == null &&
            element.RotationZ == null &&
            element.OriginX == null &&
            element.OriginY == null &&
            element.OriginZ == null &&
            !element.RotShortestDistanceX &&
            !element.RotShortestDistanceY &&
            !element.RotShortestDistanceZ;
    }

    private void DrawVanillaElementList(string[] elementNames, int selectedElementIndex, IReadOnlySet<string> keyFrameElementNames)
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
            bool inKeyFrame = keyFrameElementNames.Contains(elementName);
            string prefix = inIkChain ? "[IK] " : "";
            string suffix = inKeyFrame ? "" : " (virtual)";
            string label = $"{prefix}{elementName}{suffix}##vanilla-element-{index}";

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
            else if (!inKeyFrame && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Virtual channel: this shape element is selectable now and will be added to the keyframe only when edited.");
            }
        }

        ImGui.EndListBox();
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

    private static bool IsKnownVanillaShapeElement(VanillaAnimationDocument? document, string? elementName)
    {
        if (document?.Shape == null || string.IsNullOrWhiteSpace(elementName)) return false;
        return FindShapeElement(document.Shape, elementName) != null;
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
