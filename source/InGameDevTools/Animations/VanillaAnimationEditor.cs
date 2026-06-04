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
            _animationDiagnostics.Warning($"Preview skipped: {previewSkipReason}", $"Scene: {scene.Key}\nMode: {effectiveMode}\nSize: {viewportWidth:0}x{viewportHeight:0}");
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
        if (!string.IsNullOrWhiteSpace(ghostOverlayStatus))
        {
            uint ghostText = ImGui.ColorConvertFloat4ToU32(ghosts.Length > 0
                ? new NVector4(0.54f, 0.86f, 1f, 1f)
                : new NVector4(0.95f, 0.72f, 0.43f, 1f));
            drawList.AddText(new NVector2(min.X + 12f, min.Y + 30f), ghostText, ghostOverlayStatus);
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
            _vanillaViewportGizmoDragModelDirection = GetVanillaViewportMoveModelDirection(projection, hoveredAxis);
            _vanillaViewportGizmoDragScale = Math.Max(1f, projection.Scale);
            _vanillaViewportGizmoDragTranslationBasis = projection.TranslationBasis;
            _vanillaViewportGizmoDragCenter = projection.Center;
            _vanillaViewportGizmoDragLastAngleRadians = GetVanillaViewportGizmoMouseAngle(projection.Center, _vanillaViewportGizmoDragMouseStart);
            _vanillaViewportGizmoDragAccumulatedDegrees = 0;
            _vanillaViewportGizmoDragRingScreenSign = GizmoMode == TransformGizmoMode.Rotate
                ? GetVanillaViewportGizmoRingScreenSign(projection, hoveredAxis)
                : -1.0;
            _vanillaViewportGizmoDragStartValue = GetVanillaGizmoAxisValue(element, GizmoMode, hoveredAxis);
            _vanillaViewportGizmoDragStartOffsetX = element.OffsetX ?? 0;
            _vanillaViewportGizmoDragStartOffsetY = element.OffsetY ?? 0;
            _vanillaViewportGizmoDragStartOffsetZ = element.OffsetZ ?? 0;
            _vanillaViewportGizmoDragStartRotationX = element.RotationX ?? 0;
            _vanillaViewportGizmoDragStartRotationY = element.RotationY ?? 0;
            _vanillaViewportGizmoDragStartRotationZ = element.RotationZ ?? 0;
            _vanillaViewportGizmoDragBaseRotationDegrees = projection.BaseRotationDegrees;
            _vanillaViewportGizmoDragRotationParentBasis = projection.RotationParentBasis;
            _vanillaViewportGizmoDragSpace = GizmoSpace;
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
                FlushPendingVanillaPreviewMeshRebuild(row);
                ClearVanillaViewportGizmoDrag();
            }
            else
            {
                ApplyVanillaViewportGizmoDrag(row, entry, keyFrame, element, _vanillaViewportGizmoDragMode, _vanillaViewportGizmoDragAxis, _vanillaViewportGizmoDragVector, projection);
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

        Matrixf elementModel = AnimationElementPicking.BuildPoseModelMatrix(camera.Model, pose);
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
        return AnimationElementPicking.TryIntersectScreenLocalBox(camera.ProjectionView, elementModel, element, min, width, height, mouse, out distance);
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
            if (Math.Abs(cross) > 0.001f)
            {
                double sign = Math.Sign(cross);
                return axis == TransformGizmoAxis.Y ? -sign : sign;
            }
        }

        return -1.0;
    }

    private bool ApplyVanillaViewportGizmoDrag(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, AnimationKeyFrameElement element, TransformGizmoMode mode, TransformGizmoAxis axis, NVector2 axisVector, VanillaGizmoProjection projection)
    {
        NVector2 direction = NormalizeOrDefault(axisVector, new NVector2(1f, 0f));
        NVector2 mouseDelta = ImGui.GetMousePos() - _vanillaViewportGizmoDragMouseStart;
        double projected = NVector2.Dot(mouseDelta, direction);

        switch (mode)
        {
            case TransformGizmoMode.Move:
                return ApplyVanillaViewportMoveGizmoDrag(row, entry, keyFrame, element, axis, projected, projection);
            case TransformGizmoMode.Scale:
            {
                element = GetOrCreateVanillaEditableKeyFrameElement(keyFrame, _vanillaSelection.ElementName, element);
                double value = _vanillaViewportGizmoDragStartValue;
                value += projected / Math.Max(1f, projection.Scale) * 16.0;
                value = SnapVanillaGizmoValue(value, Math.Max(0.001, TransformGizmoIncrement * 16.0));
                if (Math.Abs(value - GetVanillaGizmoAxisValue(element, mode, axis)) < 0.0001) return false;
                SetVanillaGizmoAxisValue(element, mode, axis, value);
                break;
            }
            case TransformGizmoMode.Rotate:
            {
                element = GetOrCreateVanillaEditableKeyFrameElement(keyFrame, _vanillaSelection.ElementName, element);
                double deltaDegrees = SnapVanillaGizmoValue(UpdateVanillaViewportGizmoRingDrag(), Math.Max(0.001, TransformGizmoIncrement));
                if (!ApplyVanillaViewportRotationGizmoDrag(element, axis, deltaDegrees)) return false;
                break;
            }
            default:
                return false;
        }

        ApplyVanillaElementEdit(row, entry, keyFrame, _vanillaSelection.ElementName);
        return true;
    }

    private bool ApplyVanillaViewportMoveGizmoDrag(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, AnimationKeyFrameElement element, TransformGizmoAxis axis, double projected, VanillaGizmoProjection projection)
    {
        double modelDelta = projected / Math.Max(1f, _vanillaViewportGizmoDragScale);
        modelDelta = SnapVanillaGizmoValue(modelDelta, Math.Max(0.001, TransformGizmoIncrement));
        NVector3 modelDeltaVector = GetVanillaViewportMoveModelDelta(projection, axis, modelDelta);
        NVector3 offsetDelta = _vanillaViewportGizmoDragTranslationBasis.ModelToOffsetDelta(modelDeltaVector) * 16f;
        double offsetX = _vanillaViewportGizmoDragStartOffsetX + offsetDelta.X;
        double offsetY = _vanillaViewportGizmoDragStartOffsetY + offsetDelta.Y;
        double offsetZ = _vanillaViewportGizmoDragStartOffsetZ + offsetDelta.Z;

        if (Math.Abs(offsetX - (element.OffsetX ?? 0)) < 0.0001 &&
            Math.Abs(offsetY - (element.OffsetY ?? 0)) < 0.0001 &&
            Math.Abs(offsetZ - (element.OffsetZ ?? 0)) < 0.0001)
        {
            return false;
        }

        AnimationKeyFrameElement desiredElement = CloneElement(element);
        SetVanillaGizmoMoveOffsetValues(desiredElement, offsetX, offsetY, offsetZ);
        if (_vanillaIkFollowMove)
        {
            return TryApplyVanillaViewportIkMove(row, entry, desiredElement, modelDeltaVector);
        }

        element = GetOrCreateVanillaEditableKeyFrameElement(keyFrame, _vanillaSelection.ElementName, element);
        SetVanillaGizmoMoveOffsetValues(element, offsetX, offsetY, offsetZ);
        ApplyVanillaElementEdit(row, entry, keyFrame, _vanillaSelection.ElementName);
        return true;
    }

    private bool ApplyVanillaViewportRotationGizmoDrag(AnimationKeyFrameElement element, TransformGizmoAxis axis, double deltaDegrees)
    {
        Vec3d baseRotation = _vanillaViewportGizmoDragBaseRotationDegrees;
        RigIkMatrix3 startLocalRotation = RigIkMatrix3.FromEulerDegrees(
            baseRotation.X + _vanillaViewportGizmoDragStartRotationX,
            baseRotation.Y + _vanillaViewportGizmoDragStartRotationY,
            baseRotation.Z + _vanillaViewportGizmoDragStartRotationZ);

        RigIkMatrix3 axisRotation = RigIkMatrix3.FromAxisAngle(GetVanillaCanonicalGizmoAxis(axis), deltaDegrees * GameMath.DEG2RAD);
        RigIkMatrix3 newLocalRotation;
        if (_vanillaViewportGizmoDragSpace == TransformGizmoSpace.World)
        {
            RigIkMatrix3 parent = _vanillaViewportGizmoDragRotationParentBasis.Orthonormalized();
            RigIkMatrix3 newWorldRotation = axisRotation.Mul(parent.Mul(startLocalRotation));
            newLocalRotation = parent.Inverted().Mul(newWorldRotation).Orthonormalized();
        }
        else
        {
            newLocalRotation = startLocalRotation.Mul(axisRotation).Orthonormalized();
        }

        Vec3d euler = newLocalRotation.ToEulerDegrees();
        double rotationX = NormalizeVanillaDegrees(euler.X - baseRotation.X);
        double rotationY = NormalizeVanillaDegrees(euler.Y - baseRotation.Y);
        double rotationZ = NormalizeVanillaDegrees(euler.Z - baseRotation.Z);

        if (Math.Abs(rotationX - (element.RotationX ?? 0)) < 0.0001 &&
            Math.Abs(rotationY - (element.RotationY ?? 0)) < 0.0001 &&
            Math.Abs(rotationZ - (element.RotationZ ?? 0)) < 0.0001)
        {
            return false;
        }

        element.RotationX = rotationX;
        element.RotationY = rotationY;
        element.RotationZ = rotationZ;
        CompleteVanillaRotationGroup(element);
        return true;
    }

    private static AnimationKeyFrameElement GetOrCreateVanillaEditableKeyFrameElement(AnimationKeyFrame keyFrame, string elementName, AnimationKeyFrameElement fallback)
    {
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(elementName)) return fallback;
        if (keyFrame.Elements.TryGetValue(elementName, out AnimationKeyFrameElement? existing) && existing != null) return existing;

        keyFrame.Elements[elementName] = fallback;
        return fallback;
    }

    private NVector3 GetVanillaViewportMoveModelDelta(VanillaGizmoProjection projection, TransformGizmoAxis axis, double modelDelta)
    {
        NVector3 direction = _vanillaViewportGizmoDragAxis == axis && _vanillaViewportGizmoDragMode == TransformGizmoMode.Move
            ? _vanillaViewportGizmoDragModelDirection
            : GetVanillaViewportMoveModelDirection(projection, axis);

        return direction * (float)modelDelta;
    }

    private NVector3 GetVanillaViewportMoveModelDirection(VanillaGizmoProjection projection, TransformGizmoAxis axis)
    {
        NVector3 direction = GizmoSpace == TransformGizmoSpace.World
            ? axis switch
            {
                TransformGizmoAxis.X => NVector3.UnitX,
                TransformGizmoAxis.Y => NVector3.UnitY,
                TransformGizmoAxis.Z => NVector3.UnitZ,
                _ => NVector3.UnitX
            }
            : axis switch
            {
                TransformGizmoAxis.X => projection.AxisXModel,
                TransformGizmoAxis.Y => projection.AxisYModel,
                TransformGizmoAxis.Z => projection.AxisZModel,
                _ => projection.AxisXModel
            };

        return NormalizeOrDefault(direction, NVector3.UnitX);
    }

    private static Vec3d GetVanillaCanonicalGizmoAxis(TransformGizmoAxis axis)
    {
        return axis switch
        {
            TransformGizmoAxis.X => new Vec3d(1, 0, 0),
            TransformGizmoAxis.Y => new Vec3d(0, 1, 0),
            TransformGizmoAxis.Z => new Vec3d(0, 0, 1),
            _ => new Vec3d(1, 0, 0)
        };
    }

    private static void SetVanillaGizmoMoveOffsetValues(AnimationKeyFrameElement element, double offsetX, double offsetY, double offsetZ)
    {
        element.OffsetX = offsetX;
        element.OffsetY = offsetY;
        element.OffsetZ = offsetZ;
        CompleteVanillaPositionGroup(element);
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
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(_vanillaSelection.ElementName))
        {
            if (keyFrame.Elements.Count == 0) return false;
            _vanillaSelection.ElementName = keyFrame.Elements.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).First();
        }

        if (keyFrame.Elements.TryGetValue(_vanillaSelection.ElementName, out AnimationKeyFrameElement? found) && found != null)
        {
            element = found;
            return true;
        }

        if (IsKnownVanillaShapeElement(selectedEntry.Document, _vanillaSelection.ElementName))
        {
            element = new AnimationKeyFrameElement();
            return true;
        }

        if (keyFrame.Elements.Count == 0) return false;
        _vanillaSelection.ElementName = keyFrame.Elements.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).First();
        if (!keyFrame.Elements.TryGetValue(_vanillaSelection.ElementName, out found) || found == null) return false;
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

        VanillaGizmoTranslationBasis translationBasis = BuildVanillaGizmoTranslationBasis(pose);
        RigIkMatrix3 rotationParentBasis = RigIkMatrix3.Identity;
        Vec3d baseRotationDegrees = new(pose.ForElement.RotationX, pose.ForElement.RotationY, pose.ForElement.RotationZ);
        if (TryGetVanillaIkPoseInfo(scene, elementName, out VanillaIkPoseInfo poseInfo, out _))
        {
            rotationParentBasis = poseInfo.ParentWorldRotation;
            baseRotationDegrees = poseInfo.BaseRotationDegrees;
        }

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
            visualCenter,
            translationBasis,
            translationBasis.AxisX,
            translationBasis.AxisY,
            translationBasis.AxisZ,
            rotationParentBasis,
            baseRotationDegrees);
        return true;
    }

    private static VanillaGizmoTranslationBasis BuildVanillaGizmoTranslationBasis(ElementPose pose)
    {
        if (!TryBuildVanillaPoseModelMatrix(pose, out Matrixf poseMatrix))
        {
            return VanillaGizmoTranslationBasis.Identity;
        }

        NVector3 axisX = NormalizeOrDefault(TransformVanillaPreviewDirection(poseMatrix, NVector3.UnitX), NVector3.UnitX);
        NVector3 axisY = NormalizeOrDefault(TransformVanillaPreviewDirection(poseMatrix, NVector3.UnitY), NVector3.UnitY);
        NVector3 axisZ = NormalizeOrDefault(TransformVanillaPreviewDirection(poseMatrix, NVector3.UnitZ), NVector3.UnitZ);
        return new VanillaGizmoTranslationBasis(axisX, axisY, axisZ);
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
        _vanillaViewportGizmoDragModelDirection = NVector3.UnitX;
        _vanillaViewportGizmoDragScale = 1f;
        _vanillaViewportGizmoDragTranslationBasis = VanillaGizmoTranslationBasis.Identity;
        _vanillaViewportGizmoDragCenter = NVector2.Zero;
        _vanillaViewportGizmoDragLastAngleRadians = 0;
        _vanillaViewportGizmoDragAccumulatedDegrees = 0;
        _vanillaViewportGizmoDragRingScreenSign = -1.0;
        _vanillaViewportGizmoDragStartValue = 0;
        _vanillaViewportGizmoDragStartOffsetX = 0;
        _vanillaViewportGizmoDragStartOffsetY = 0;
        _vanillaViewportGizmoDragStartOffsetZ = 0;
        _vanillaViewportGizmoDragStartRotationX = 0;
        _vanillaViewportGizmoDragStartRotationY = 0;
        _vanillaViewportGizmoDragStartRotationZ = 0;
        _vanillaViewportGizmoDragBaseRotationDegrees = new Vec3d();
        _vanillaViewportGizmoDragRotationParentBasis = RigIkMatrix3.Identity;
        _vanillaViewportGizmoDragSpace = TransformGizmoSpace.World;
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
            bool sameScene = _vanillaPreviewScene?.Key == row.Key;
            if (!sameScene)
            {
                ClearPendingVanillaPreviewMeshRebuild();
            }

            float requestedFrame = sameScene ? _vanillaPreviewScene!.CurrentFrame : 0f;
            if (_vanillaPreviewScene == null || !sameScene || rebuildMesh)
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
                _vanillaPreviewScene.Scrub(Math.Clamp(requestedFrame, 0, Math.Max(0, _vanillaPreviewScene.QuantityFrames - 1)));
                _vanillaStatus = _vanillaPreviewScene.Status;
            }
        }
        catch (Exception exception)
        {
            DisposeVanillaPreviewScene();
            _vanillaStatus = $"Preview failed for {row.Label}: {exception.Message}";
            _animationDiagnostics.Exception($"Preview failed for {row.Label}", exception);
            LoggerUtil.Warn(_api, this, $"Vanilla preview failed for '{row.Label}' ({row.Key}): {exception}");
        }
    }

    private void RefreshVanillaPreviewAfterEdit(VanillaBrowserRow row, params string[] changedElementNames)
    {
        if (_vanillaPreviewScene?.Key != row.Key) return;
        bool rebuildMesh = ShouldRebuildVanillaPreviewMeshAfterEdit(changedElementNames);
        if (rebuildMesh && IsVanillaViewportDraggingRow(row))
        {
            _vanillaPreviewMeshRebuildPending = true;
            _vanillaPreviewMeshRebuildPendingRowKey = row.Key;
            BuildVanillaPreviewScene(row, rebuildMesh: false);
            return;
        }

        BuildVanillaPreviewScene(row, rebuildMesh);
    }

    private bool IsVanillaViewportDraggingRow(VanillaBrowserRow row)
    {
        return _vanillaViewportGizmoDragAxis != TransformGizmoAxis.None &&
            string.Equals(_vanillaViewportGizmoDragRowKey, row.Key, StringComparison.Ordinal);
    }

    private void FlushPendingVanillaPreviewMeshRebuild(VanillaBrowserRow row)
    {
        if (!_vanillaPreviewMeshRebuildPending ||
            !string.Equals(_vanillaPreviewMeshRebuildPendingRowKey, row.Key, StringComparison.Ordinal))
        {
            return;
        }

        ClearPendingVanillaPreviewMeshRebuild();
        BuildVanillaPreviewScene(row, rebuildMesh: true);
    }

    private void ClearPendingVanillaPreviewMeshRebuild()
    {
        _vanillaPreviewMeshRebuildPending = false;
        _vanillaPreviewMeshRebuildPendingRowKey = "";
    }

    private bool ShouldRebuildVanillaPreviewMeshAfterEdit(IEnumerable<string>? changedElementNames)
    {
        if (_vanillaPreviewScene == null || changedElementNames == null) return false;

        foreach (string elementName in changedElementNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryFindVanillaPose(_vanillaPreviewScene.Animator.RootPoses, elementName, out ElementPose? pose, out _) ||
                pose?.ForElement == null ||
                pose.ForElement.JointId <= 0)
            {
                return true;
            }
        }

        return false;
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
        ClearPendingVanillaPreviewMeshRebuild();
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

        if (ImGui.RadioButton("Auto chain##vanilla-ik-mode", _vanillaIkMode != VanillaIkChainMode.ManualOverride))
        {
            _vanillaIkMode = _vanillaIkMode == VanillaIkChainMode.AutoExtended
                ? VanillaIkChainMode.AutoExtended
                : VanillaIkChainMode.AutoConservative;
            _vanillaIkHasTarget = false;
            ClearVanillaViewportGizmoDrag();
            SaveVanillaIkSettings("IK mode: auto chain. Select an element; the chain is detected from the shape hierarchy and anchors.");
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Manual override##vanilla-ik-mode", _vanillaIkMode == VanillaIkChainMode.ManualOverride))
        {
            _vanillaIkMode = VanillaIkChainMode.ManualOverride;
            _vanillaIkHasTarget = false;
            ClearVanillaViewportGizmoDrag();
            SaveVanillaIkSettings("IK mode: manual override. Click body parts or Ctrl+Click elements to edit the chain.");
        }

        if (_vanillaIkMode != VanillaIkChainMode.ManualOverride)
        {
            int profile = _vanillaIkMode == VanillaIkChainMode.AutoExtended ? 1 : 0;
            string[] profiles = ["Conservative", "Extended"];
            ImGui.SetNextItemWidth(180f);
            if (ImGui.Combo("Auto profile##vanilla-ik-profile", ref profile, profiles, profiles.Length))
            {
                _vanillaIkMode = profile == 1 ? VanillaIkChainMode.AutoExtended : VanillaIkChainMode.AutoConservative;
                _vanillaIkHasTarget = false;
                ClearVanillaViewportGizmoDrag();
                SaveVanillaIkSettings(_vanillaIkMode == VanillaIkChainMode.AutoExtended
                    ? "Auto IK profile: extended topology."
                    : "Auto IK profile: conservative topology.");
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Conservative favors short chains that stop at anchors and body hubs. Extended allows longer linear appendages such as tails, necks, wings, and tentacles.");
            }
        }

        bool hasChain = TryGetActiveVanillaIkChain(document, entry.Animation, keyFrame, selectedElementName, out VanillaIkManualChain chain, out string chainError, out string chainWarning);

        if (ImGui.Checkbox("IK on Move##vanilla-ik-follow-move", ref _vanillaIkFollowMove))
        {
            _vanillaStatus = _vanillaIkFollowMove
                ? "IK Move enabled. Drag the Move gizmo on the active IK chain end."
                : "IK Move disabled.";
        }

        if (_vanillaIkFollowMove)
        {
            bool lockToDragAxis = _vanillaIkLockMoveToDragAxis;
            if (ImGui.Checkbox("Lock IK to drag axis##vanilla-ik-lock-drag-axis", ref lockToDragAxis))
            {
                _vanillaIkLockMoveToDragAxis = lockToDragAxis;
                SaveVanillaIkSettings(_vanillaIkLockMoveToDragAxis
                    ? "IK Move target locked to the active Move gizmo axis."
                    : "IK Move target is free.");
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("When enabled, dragging a Move gizmo axis constrains the IK target to that same world/local axis instead of rebuilding the target from all offset channels.");
            }
        }

        bool preserveHandle = _vanillaIkPreserveDraggedPartRotation;
        if (ImGui.Checkbox("Preserve dragged part rotation##vanilla-ik-preserve-handle", ref preserveHandle))
        {
            _vanillaIkPreserveDraggedPartRotation = preserveHandle;
            _vanillaIkHasTarget = false;
            ClearVanillaViewportGizmoDrag();
            SaveVanillaIkSettings(_vanillaIkPreserveDraggedPartRotation
                ? "IK handle rotation lock enabled."
                : "IK handle rotation lock disabled.");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("On keeps the dragged body part's world orientation locked and solves the parent chain around it. Off allows the handle itself to rotate.");
        }

        if (_vanillaIkMode == VanillaIkChainMode.ManualOverride && ImGui.Button("Clear IK chain##vanilla-ik-clear"))
        {
            _vanillaIkChainElementNames.Clear();
            _vanillaIkHasTarget = false;
            ClearVanillaViewportGizmoDrag();
            _vanillaStatus = "IK chain cleared.";
        }

        DrawVanillaIkAnchorControls(document, selectedElementName, allElements);

        if (hasChain)
        {
            ImGui.TextDisabled(_vanillaIkMode != VanillaIkChainMode.ManualOverride
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
            if (_vanillaIkMode != VanillaIkChainMode.ManualOverride)
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

        ImGui.TextDisabled(_vanillaIkMode != VanillaIkChainMode.ManualOverride
            ? "Auto IK uses topology, anchors, and body hubs to choose the driver chain."
            : "Manual override edits the exact chain from viewport clicks or Ctrl+Click rows.");
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

    private void DrawVanillaIkAnchorControls(VanillaAnimationDocument document, string selectedElementName, string[] allElements)
    {
        PruneVanillaIkAnchors(document, allElements);

        bool hasSelection = !string.IsNullOrWhiteSpace(selectedElementName);
        bool selectedPinned = hasSelection && ContainsVanillaIkAnchor(document, selectedElementName);
        if (!hasSelection) ImGui.BeginDisabled();
        if (ImGui.SmallButton(selectedPinned ? "Unpin IK anchor##vanilla-ik-anchor-toggle" : "Pin IK anchor##vanilla-ik-anchor-toggle"))
        {
            ToggleVanillaIkAnchor(document, selectedElementName);
        }
        if (!hasSelection) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Pinned body parts stop automatic IK chains and are never rotated by auto IK. Manual override can still include them explicitly.");
        }

        string[] anchors = GetVanillaIkAnchors(document);
        if (anchors.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear anchors##vanilla-ik-anchor-clear"))
            {
                ClearVanillaIkAnchors(document);
            }

            ImGui.TextDisabled($"Anchors: {string.Join(", ", anchors)}");
        }
    }

    private void SaveVanillaIkSettings(string status)
    {
        _devToolsConfig.AnimationIkMode = FormatVanillaIkChainMode(_vanillaIkMode);
        _devToolsConfig.AnimationIkPreserveDraggedPartRotation = _vanillaIkPreserveDraggedPartRotation;
        _devToolsConfig.AnimationIkLockMoveToDragAxis = _vanillaIkLockMoveToDragAxis;
        _vanillaStatus = status;
        QueueDevToolsConfigSave(status);
    }

    private static VanillaIkChainMode ParseVanillaIkChainMode(string? value)
    {
        if (string.Equals(value, "AutoExtended", StringComparison.OrdinalIgnoreCase)) return VanillaIkChainMode.AutoExtended;
        if (string.Equals(value, "ManualOverride", StringComparison.OrdinalIgnoreCase)) return VanillaIkChainMode.ManualOverride;
        return VanillaIkChainMode.AutoConservative;
    }

    private static string FormatVanillaIkChainMode(VanillaIkChainMode mode)
    {
        return mode switch
        {
            VanillaIkChainMode.AutoExtended => "AutoExtended",
            VanillaIkChainMode.ManualOverride => "ManualOverride",
            _ => "AutoConservative"
        };
    }

    private static string GetVanillaIkAnchorKey(VanillaAnimationDocument document)
    {
        return document.HistoryKey;
    }

    private string[] GetVanillaIkAnchors(VanillaAnimationDocument document)
    {
        string key = GetVanillaIkAnchorKey(document);
        if (!_devToolsConfig.AnimationIkAnchors.TryGetValue(key, out string[]? anchors) || anchors == null) return [];

        return anchors
            .Where(anchor => !string.IsNullOrWhiteSpace(anchor))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(anchor => anchor, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool ContainsVanillaIkAnchor(VanillaAnimationDocument document, string elementName)
    {
        return GetVanillaIkAnchors(document).Any(anchor => string.Equals(anchor, elementName, StringComparison.OrdinalIgnoreCase));
    }

    private void ToggleVanillaIkAnchor(VanillaAnimationDocument document, string elementName)
    {
        if (string.IsNullOrWhiteSpace(elementName)) return;

        string key = GetVanillaIkAnchorKey(document);
        List<string> anchors = GetVanillaIkAnchors(document).ToList();
        int index = anchors.FindIndex(anchor => string.Equals(anchor, elementName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            anchors.RemoveAt(index);
            _vanillaStatus = $"IK anchor removed: {elementName}.";
        }
        else
        {
            anchors.Add(elementName.Trim());
            _vanillaStatus = $"IK anchor pinned: {elementName}.";
        }

        if (anchors.Count == 0)
        {
            _devToolsConfig.AnimationIkAnchors.Remove(key);
        }
        else
        {
            _devToolsConfig.AnimationIkAnchors[key] = anchors
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(anchor => anchor, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        _vanillaIkHasTarget = false;
        ClearVanillaViewportGizmoDrag();
        SaveVanillaIkSettings(_vanillaStatus);
    }

    private void ClearVanillaIkAnchors(VanillaAnimationDocument document)
    {
        _devToolsConfig.AnimationIkAnchors.Remove(GetVanillaIkAnchorKey(document));
        _vanillaIkHasTarget = false;
        ClearVanillaViewportGizmoDrag();
        SaveVanillaIkSettings("IK anchors cleared.");
    }

    private void PruneVanillaIkAnchors(VanillaAnimationDocument document, string[] allElements)
    {
        string key = GetVanillaIkAnchorKey(document);
        if (!_devToolsConfig.AnimationIkAnchors.TryGetValue(key, out string[]? anchors) || anchors == null) return;

        string[] pruned = anchors
            .Where(anchor => ContainsElementName(allElements, anchor))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(anchor => anchor, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pruned.Length == anchors.Length && pruned.SequenceEqual(anchors, StringComparer.OrdinalIgnoreCase)) return;

        if (pruned.Length == 0) _devToolsConfig.AnimationIkAnchors.Remove(key);
        else _devToolsConfig.AnimationIkAnchors[key] = pruned;
        QueueDevToolsConfigSave("IK anchors pruned.");
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
            : TryGetAutoVanillaIkChain(document, animation, keyFrame, selectedElementName, out chain, out error, out warning);
    }

    private bool TryGetAutoVanillaIkChain(
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

        if (ContainsVanillaIkAnchor(document, resolvedSelectedName))
        {
            error = $"{resolvedSelectedName} is pinned as an IK anchor. Select a downstream handle, unpin it, or use Manual override.";
            return false;
        }

        bool selectedIsStructuralHub = IsVanillaIkStructuralHub(selected, selectedIndex == 0, out string selectedHubReason);
        bool selectedIsTrunkOrRoot = selectedIndex == 0 || IsVanillaIkTrunkName(NormalizeVanillaIkStructureName(selected.Name));
        if (selectedIsStructuralHub && selectedIsTrunkOrRoot)
        {
            if (_vanillaIkMode != VanillaIkChainMode.AutoExtended)
            {
                error = $"{resolvedSelectedName} is {selectedHubReason}. Select an appendage, switch to Extended, or use Manual override.";
                return false;
            }

            if (TryGetVanillaIkLongestChildPath(selected, GetVanillaIkAutoMaxChainLength(), out List<ShapeElement> childPath, out string childNote) &&
                TryBuildVanillaIkChainNames(childPath, out string[] childNames))
            {
                warning = $"Selected {resolvedSelectedName} is {selectedHubReason}; Extended auto uses child chain {childNames[0]}.";
                if (!string.IsNullOrWhiteSpace(childNote)) warning += $" {childNote}";
                chain = new VanillaIkManualChain(childNames, childNames[^1], string.Join(" -> ", childNames));
                return true;
            }

            warning = $"Selected {resolvedSelectedName} is {selectedHubReason}; auto uses a one-bone chain.";
            chain = new VanillaIkManualChain([resolvedSelectedName], resolvedSelectedName, resolvedSelectedName);
            return true;
        }

        int detectedStartIndex = FindVanillaIkAutoChainStart(document, path, selectedIndex, out string stopReason);
        int hardAnchorStartIndex = FindVanillaIkHardAnchorStart(document, path, selectedIndex);
        int startIndex = Math.Max(hardAnchorStartIndex, detectedStartIndex - _vanillaIkAutoRootExtraBones);
        int maxChainLength = GetVanillaIkAutoMaxChainLength();
        if (selectedIndex - startIndex + 1 > maxChainLength)
        {
            startIndex = Math.Max(hardAnchorStartIndex, selectedIndex - maxChainLength + 1);
        }

        var chainElements = path.Skip(startIndex).Take(selectedIndex - startIndex + 1).ToList();
        int remaining = Math.Min(_vanillaIkAutoEndExtraBones, Math.Max(0, maxChainLength - chainElements.Count));
        AppendVanillaIkLongestDescendantPath(chainElements, selected, remaining, out string extensionNote);
        TrimVanillaIkChainAtAnchors(document, chainElements);

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

        if (_vanillaIkMode == VanillaIkChainMode.AutoConservative &&
            !IsVanillaIkConservativeChainAllowed(document, animation, keyFrame, path, startIndex, selectedIndex, orderedNames))
        {
            error = "Conservative auto IK could not prove a paired or clear linear appendage. Switch to Extended, pin anchors, or use Manual override.";
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
        int baseLength = _vanillaIkMode == VanillaIkChainMode.AutoExtended
            ? VanillaIkAutoAbsoluteMaxChainLength
            : VanillaIkAutoMaxChainLength;
        return Math.Clamp(
            baseLength + _vanillaIkAutoRootExtraBones + _vanillaIkAutoEndExtraBones,
            1,
            VanillaIkAutoAbsoluteMaxChainLength);
    }

    private int FindVanillaIkAutoChainStart(VanillaAnimationDocument document, IReadOnlyList<ShapeElement> path, int selectedIndex, out string stopReason)
    {
        stopReason = "";
        for (int index = selectedIndex - 1; index >= 0; index--)
        {
            string elementName = path[index].Name ?? "";
            if (!string.IsNullOrWhiteSpace(elementName) && ContainsVanillaIkAnchor(document, elementName))
            {
                stopReason = $"{elementName} (pinned anchor)";
                return Math.Min(selectedIndex, index + 1);
            }

            if (!IsVanillaIkStructuralHub(path[index], index == 0, out string reason)) continue;

            string name = string.IsNullOrWhiteSpace(path[index].Name) ? "unnamed hub" : path[index].Name!;
            stopReason = $"{name} ({reason})";
            return Math.Min(selectedIndex, index + 1);
        }

        return Math.Max(0, selectedIndex - 1);
    }

    private int FindVanillaIkHardAnchorStart(VanillaAnimationDocument document, IReadOnlyList<ShapeElement> path, int selectedIndex)
    {
        for (int index = selectedIndex - 1; index >= 0; index--)
        {
            string elementName = path[index].Name ?? "";
            if (!string.IsNullOrWhiteSpace(elementName) && ContainsVanillaIkAnchor(document, elementName))
            {
                return Math.Min(selectedIndex, index + 1);
            }
        }

        return 0;
    }

    private bool IsVanillaIkConservativeChainAllowed(
        VanillaAnimationDocument document,
        VanillaAnimation animation,
        AnimationKeyFrame keyFrame,
        IReadOnlyList<ShapeElement> path,
        int startIndex,
        int selectedIndex,
        IReadOnlyList<string> orderedNames)
    {
        if (orderedNames.Count <= 2) return true;
        if (IsVanillaIkClearLinearPath(path, startIndex, selectedIndex)) return true;

        string[] allElements = BuildVanillaSymmetryElementUniverse(document, animation, keyFrame);
        foreach (string elementName in orderedNames)
        {
            if (TryResolveVanillaSymmetryPair(document, elementName, allElements, out _, out VanillaSymmetrySide sourceSide, out _) &&
                sourceSide != VanillaSymmetrySide.Unknown)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVanillaIkClearLinearPath(IReadOnlyList<ShapeElement> path, int startIndex, int selectedIndex)
    {
        for (int index = Math.Max(0, startIndex); index < selectedIndex; index++)
        {
            ShapeElement current = path[index];
            ShapeElement next = path[index + 1];
            ShapeElement[] children = current.Children ?? [];
            ShapeElement[] namedChildren = children
                .Where(child => !string.IsNullOrWhiteSpace(child.Name))
                .ToArray();
            if (namedChildren.Length <= 1) continue;

            int nextMatches = namedChildren.Count(child => string.Equals(child.Name, next.Name, StringComparison.OrdinalIgnoreCase));
            if (nextMatches != 1) return false;
        }

        return true;
    }

    private void TrimVanillaIkChainAtAnchors(VanillaAnimationDocument document, List<ShapeElement> chainElements)
    {
        for (int index = 0; index < chainElements.Count; index++)
        {
            string elementName = chainElements[index].Name ?? "";
            if (string.IsNullOrWhiteSpace(elementName) || !ContainsVanillaIkAnchor(document, elementName)) continue;

            chainElements.RemoveRange(index, chainElements.Count - index);
            return;
        }
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
        if (!TryCreateVanillaIkCcdCache(row, entry, keyFrame, chain, VanillaIkEffectorMode.DistalEndpoint, out VanillaIkCcdCache? cache, out string error) || cache == null)
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

        if (!TryCreateVanillaIkCcdCache(row, entry, keyFrame, chain, VanillaIkEffectorMode.DistalEndpoint, out VanillaIkCcdCache? cache, out string error) || cache == null)
        {
            _vanillaStatus = error;
            return;
        }

        Vec3d target = new(_vanillaIkTargetX, _vanillaIkTargetY, _vanillaIkTargetZ);
        if (!TrySolveVanillaIkCcdToTarget(cache, target, _vanillaIkPreserveDraggedPartRotation, keepHandleLocalTransform: false, out AnimationKeyFrameElement[] solvedElements, out double finalDistance, out string solveError))
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
        RefreshVanillaPreviewAfterEdit(row, changedElementNames);

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

    private bool TryApplyVanillaViewportIkMove(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrameElement desiredElement, NVector3 dragModelDelta)
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
        if (!_vanillaIkDragActive ||
            _vanillaIkDragCache == null ||
            _vanillaIkDragRowKey != row.Key ||
            _vanillaIkDragKeyFrameIndex != keyFrameIndex ||
            !string.Equals(_vanillaIkDragElementName, selectedElementName, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryCreateVanillaIkCcdCache(row, entry, keyFrame, chain, VanillaIkEffectorMode.GizmoHandle, out VanillaIkCcdCache? cache, out string error) || cache == null)
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

        Vec3d target = _vanillaIkLockMoveToDragAxis && dragModelDelta.LengthSquared() > 0.000001f
            ? Add(_vanillaIkDragCache.EndOrigin, new Vec3d(dragModelDelta.X, dragModelDelta.Y, dragModelDelta.Z))
            : GetVanillaIkDesiredEndTarget(_vanillaIkDragCache, desiredElement);
        if (!TrySolveVanillaIkCcdToTarget(_vanillaIkDragCache, target, _vanillaIkPreserveDraggedPartRotation, keepHandleLocalTransform: true, out AnimationKeyFrameElement[] solvedElements, out double finalDistance, out string solveError))
        {
            _vanillaStatus = solveError;
            return false;
        }

        ApplyVanillaIkSolvedElements(keyFrame, chain, solvedElements);
        ApplyVanillaElementEdit(row, entry, keyFrame, chain.ElementNames.ToArray());
        string targetMode = _vanillaIkLockMoveToDragAxis ? " on drag axis" : "";
        string handleMode = _vanillaIkPreserveDraggedPartRotation ? " with connected handle" : "";
        _vanillaStatus = finalDistance <= VanillaIkSolveTolerance
            ? $"IK Move solved {chain.ElementNames.Count} element(s){targetMode}{handleMode}."
            : $"IK Move solved best effort{targetMode}{handleMode}; remaining distance {finalDistance:0.###}.";
        return true;
    }

    private bool TryCreateVanillaIkCcdCache(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, VanillaIkManualChain chain, VanillaIkEffectorMode effectorMode, out VanillaIkCcdCache? cache, out string error)
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
        bool allowZeroEndSegment = effectorMode == VanillaIkEffectorMode.GizmoHandle;
        if (!TryGetVanillaIkEffectorModel(endInfo, effectorMode, out Vec3d endOrigin))
        {
            error = $"Could not find an IK effector point for {chain.EndElementName}.";
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
            if (allowZeroEndSegment && index == jointPositions.Length - 2 && jointPositions.Length > 2) continue;

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

    private static bool TryGetVanillaIkEffectorModel(VanillaIkPoseInfo endInfo, VanillaIkEffectorMode mode, out Vec3d origin)
    {
        if (mode == VanillaIkEffectorMode.GizmoHandle && TryGetVanillaGizmoHandlePointModel(endInfo.Pose, out origin))
        {
            return true;
        }

        return TryGetVanillaDistalEndpointModel(endInfo.Pose, endInfo.Origin, out origin);
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

    private static bool TrySolveVanillaIkCcdToTarget(VanillaIkCcdCache cache, Vec3d requestedTarget, bool preserveHandleRotation, bool keepHandleLocalTransform, out AnimationKeyFrameElement[] solvedElements, out double finalDistance, out string error)
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

        bool solveWithUpstreamDriversOnly = preserveHandleRotation || keepHandleLocalTransform;
        int driverCount = solveWithUpstreamDriversOnly ? count - 1 : count;
        if (driverCount == 0)
        {
            error = keepHandleLocalTransform || preserveHandleRotation
                ? "Locked IK handle needs a parent chain; disable handle rotation lock or add driver bones."
                : "IK needs at least one driver bone.";
            return false;
        }

        Vec3d[] joints = cache.JointPositions.Select(point => new Vec3d(point.X, point.Y, point.Z)).ToArray();
        RigIkMatrix3[] rotations = cache.BoneInfos.Select(info => info.WorldRotation).ToArray();
        Vec3d lockedHandleEndpointOffset = preserveHandleRotation
            ? Sub(cache.EndOrigin, cache.JointPositions[count - 1])
            : new Vec3d();
        double initialDistance = Distance(joints[^1], requestedTarget);

        const int maxIterations = 24;
        const double vectorEpsilon = 0.000001;
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            for (int boneIndex = driverCount - 1; boneIndex >= 0; boneIndex--)
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

                for (int rotationIndex = boneIndex; rotationIndex < (solveWithUpstreamDriversOnly ? driverCount : rotations.Length); rotationIndex++)
                {
                    rotations[rotationIndex] = delta.Mul(rotations[rotationIndex]).Orthonormalized();
                }

                if (preserveHandleRotation && !keepHandleLocalTransform)
                {
                    joints[^1] = Add(joints[count - 1], lockedHandleEndpointOffset);
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
            if (keepHandleLocalTransform && index == count - 1)
            {
                solvedElements[index] = CloneElement(cache.StartElements[index]);
                continue;
            }

            RigIkMatrix3 world = preserveHandleRotation && index == count - 1
                ? cache.BoneInfos[index].WorldRotation
                : rotations[index];
            RigIkMatrix3 local = parentWorld.Inverted().Mul(world).Orthonormalized();
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

    private static bool TryGetVanillaGizmoHandlePointModel(ElementPose pose, out Vec3d origin)
    {
        origin = new Vec3d();
        if (pose.ForElement == null) return false;
        if (!TryBuildVanillaPoseModelMatrix(pose, out Matrixf matrix)) return false;

        NVector3 local = GetVanillaGizmoLocalPoint(pose);
        Vec4f transformed = matrix.TransformVector(new Vec4f(local.X, local.Y, local.Z, 1f));
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
