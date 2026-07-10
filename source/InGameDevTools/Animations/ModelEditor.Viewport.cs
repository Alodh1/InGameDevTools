using ImGuiNET;
using InGameDevTools.Utils;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private const float ModelGizmoPickDistancePx = 12f;
    private const float ModelUnitsPerBlock = 16f;
    private const int ModelReferenceBlockPadding = 2;
    private const int ModelReferenceMaxHorizontalBlocks = 9;
    private const int ModelReferenceMaxVerticalBlocks = 12;
    private const int ModelResizeCornerHandleBase = 6;

    private DevToolsPreview3DRenderer? _modelPreviewRenderer;
    private DevToolsPreviewMesh? _modelPreviewMesh;
    private DevToolsPreviewMesh? _modelReferenceMesh;
    private ModelShapeAssetEntry? _modelReferenceEntry;
    private EntityProperties? _modelReferenceEntityType;
    private string? _modelReferenceEntityDisplay;
    private string? _modelPreviewSkipReason;
    private string? _modelReferenceSkipReason;
    private float _modelViewportYaw = 0.7f;
    private float _modelViewportPitch = -0.45f;
    private float _modelViewportDistance = 2.4f;
    private Vector3 _modelViewportTarget = new(0.5f, 0.5f, 0.5f);
    private bool _modelCameraFitPending;
    private bool _modelViewportScreenshotQueued;
    private bool _modelReferenceVisible = true;
    private bool _modelReferenceDirty;
    private float _modelReferenceOpacity = 0.42f;
    private float _modelReferenceScale = 1f;
    private float _modelReferenceOffsetX;
    private float _modelReferenceOffsetY;
    private float _modelReferenceOffsetZ;

    private bool _modelGizmoDragging;
    private int _modelGizmoDragAxis = -1;
    private int _modelGizmoDragFace = -1;
    private NVector2 _modelGizmoDragStartMouse;
    private NVector2 _modelGizmoDragAxisScreenPerUnit;
    private NVector2 _modelGizmoDragCenterScreen;
    private double _modelGizmoDragRotationSign = 1.0;
    private double[] _modelGizmoDragStartFrom = new double[3];
    private double[] _modelGizmoDragStartTo = new double[3];
    private double[]? _modelGizmoDragStartOrigin;
    private double _modelGizmoDragStartRotX;
    private double _modelGizmoDragStartRotY;
    private double _modelGizmoDragStartRotZ;
    private bool _modelGizmoDragUniformScale;
    private float _modelGizmoDragStartHandleDistanceUnits;
    private int _modelGizmoDragCorner = -1;
    private DevToolsPreviewBounds _modelGizmoDragLocalBounds;
    private ModelResizeBoundsUnits _modelGizmoDragSelectionBoundsUnits;
    private bool _modelGizmoDragSelectionResize;
    private Vector3 _modelGizmoDragAnchorUnits;
    private ModelElementData? _modelGizmoDragGroupRotationElement;
    private ModelElementData? _modelGizmoDragGroupRotationLayer;
    private readonly NVector2[] _modelGizmoDragLocalAxisScreenPerUnit = [NVector2.Zero, NVector2.Zero, NVector2.Zero];
    private readonly List<ModelGizmoDragElementState> _modelGizmoDragElements = [];

    private static readonly (int A, int B)[] ModelBoxEdges =
    [
        (0, 1), (1, 2), (2, 3), (3, 0),
        (4, 5), (5, 6), (6, 7), (7, 4),
        (0, 4), (1, 5), (2, 6), (3, 7)
    ];

    private static readonly (int A, int B, int C)[] ModelBoxTriangles =
    [
        (0, 1, 2), (0, 2, 3),
        (4, 6, 5), (4, 7, 6),
        (0, 4, 5), (0, 5, 1),
        (1, 5, 6), (1, 6, 2),
        (2, 6, 7), (2, 7, 3),
        (3, 7, 4), (3, 4, 0)
    ];

    private readonly record struct ModelCutPreview(
        ModelElementData Element,
        int FaceAxis,
        bool FacePositive,
        int CutAxis,
        double CutCoordinate,
        Vector3[] PlaneCorners,
        Vector3 LineStart,
        Vector3 LineEnd);

    private readonly record struct ModelChiselPreview(
        ModelElementData Element,
        int FaceAxis,
        bool FacePositive,
        double[] RemoveFrom,
        double[] RemoveTo,
        double[] AddFrom,
        double[] AddTo,
        Vector3[] RemoveCorners,
        Vector3[] AddCorners);

    private readonly record struct ModelResizeBoundsUnits(
        double MinX,
        double MinY,
        double MinZ,
        double MaxX,
        double MaxY,
        double MaxZ)
    {
        public double Min(int axis) => axis switch
        {
            0 => MinX,
            1 => MinY,
            _ => MinZ
        };

        public double Max(int axis) => axis switch
        {
            0 => MaxX,
            1 => MaxY,
            _ => MaxZ
        };

        public double Size(int axis) => Max(axis) - Min(axis);

        public DevToolsPreviewBounds ToBlockBounds()
        {
            return new DevToolsPreviewBounds(
                new Vector3((float)(MinX / ModelUnitsPerBlock), (float)(MinY / ModelUnitsPerBlock), (float)(MinZ / ModelUnitsPerBlock)),
                new Vector3((float)(MaxX / ModelUnitsPerBlock), (float)(MaxY / ModelUnitsPerBlock), (float)(MaxZ / ModelUnitsPerBlock)));
        }

        public Vector3 CornerUnits(int corner)
        {
            return new Vector3(
                (corner == 1 || corner == 2 || corner == 5 || corner == 6) ? (float)MaxX : (float)MinX,
                (corner == 2 || corner == 3 || corner == 6 || corner == 7) ? (float)MaxY : (float)MinY,
                corner >= 4 ? (float)MaxZ : (float)MinZ);
        }
    }

    // Local box corner index layout: bit0 = +X, bit1 = +Y, bit2 = +Z is NOT used here;
    // corners follow the same winding as AnimationElementPicking boxes.
    private static Vector3[] ModelLocalBoxCorners(ModelElementData element)
    {
        float sizeX = (float)Math.Max(0.0, element.SizeX) / ModelUnitsPerBlock;
        float sizeY = (float)Math.Max(0.0, element.SizeY) / ModelUnitsPerBlock;
        float sizeZ = (float)Math.Max(0.0, element.SizeZ) / ModelUnitsPerBlock;
        return
        [
            new Vector3(0f, 0f, 0f),
            new Vector3(sizeX, 0f, 0f),
            new Vector3(sizeX, sizeY, 0f),
            new Vector3(0f, sizeY, 0f),
            new Vector3(0f, 0f, sizeZ),
            new Vector3(sizeX, 0f, sizeZ),
            new Vector3(sizeX, sizeY, sizeZ),
            new Vector3(0f, sizeY, sizeZ)
        ];
    }

    private void DrawModelViewportPanel()
    {
        if (_modelDoc == null)
        {
            ImGui.TextDisabled("Open a shape or create a new one.");
            return;
        }

        DrawModelReferenceControls();
        ModelRebuildPreviewMeshIfNeeded();
        ModelRebuildReferenceMeshIfNeeded();

        if (ImGui.SmallButton("Focus selection##model-vp-focus"))
        {
            ModelFocusCameraOnSelection();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Center the camera on the selected element (F).");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Fit shape##model-vp-fit"))
        {
            ModelFitCameraToMesh();
        }
        ImGui.SameLine();
        bool hasReferenceMesh = _modelReferenceVisible && _modelReferenceMesh != null;
        if (!hasReferenceMesh) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Fit reference##model-vp-fit-reference"))
        {
            ModelFitCameraToReference();
        }
        if (!hasReferenceMesh) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Center the camera on the reference model.");
        }
        ImGui.SameLine();
        bool hasSceneBounds = ModelViewportSceneBounds().IsValid;
        if (!hasSceneBounds) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Fit all##model-vp-fit-all"))
        {
            ModelFitCameraToScene();
        }
        if (!hasSceneBounds) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.SmallButton("Screenshot##model-vp-screenshot"))
        {
            _modelViewportScreenshotQueued = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("RMB orbit, MMB/Shift+RMB pan, wheel zoom, Ctrl+wheel nudge axis, arrows nudge plane");

        DrawModelViewportSurface();
    }

    private void DrawModelReferenceControls()
    {
        EnsureModelShapeIndex();

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Reference");
        ImGui.SameLine();

        List<ModelShapeAssetEntry> index = _modelShapeIndex ?? [];
        List<string> options = index.Select(ModelReferenceLabel).ToList();
        string current = ModelReferenceDisplay() ?? "(none)";
        float comboWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.46f, 220f, 520f);
        ImGui.SetNextItemWidth(comboWidth);
        if (ModelFilteredCombo("##model-reference-shape", current, options, out string selectedReference, allowCustom: false, filterHint: "filter reference shapes"))
        {
            ModelShapeAssetEntry? entry = index.FirstOrDefault(candidate =>
                string.Equals(ModelReferenceLabel(candidate), selectedReference, StringComparison.Ordinal));
            ModelSetReferenceEntry(entry);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Load another shape as a non-editable reference in the viewport. It is not saved into this model file.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Player##model-reference-player"))
        {
            ModelSetPlayerReference();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Use the current player entity shape as the reference model.");
        }

        ImGui.SameLine();
        bool hasReference = _modelReferenceEntry != null || _modelReferenceEntityType != null;
        if (!hasReference) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Clear##model-reference-clear"))
        {
            ModelSetReferenceEntry(null);
        }
        if (!hasReference) ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.Checkbox("Show##model-reference-visible", ref _modelReferenceVisible);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Toggle the reference model without clearing it.");
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(116f);
        if (ImGui.SliderFloat("Opacity##model-reference-opacity", ref _modelReferenceOpacity, 0.12f, 1f, "%.2f"))
        {
            _modelReferenceOpacity = Math.Clamp(_modelReferenceOpacity, 0.12f, 1f);
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Transform");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(92f);
        if (ImGui.DragFloat("Scale##model-reference-scale", ref _modelReferenceScale, 0.01f, 0.01f, 20f, "%.2f"))
        {
            _modelReferenceScale = Math.Clamp(_modelReferenceScale, 0.01f, 20f);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Reference scale in rendered model space.");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Offset");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(74f);
        ImGui.DragFloat("X##model-reference-offset-x", ref _modelReferenceOffsetX, 0.01f, -64f, 64f, "%.2f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(74f);
        ImGui.DragFloat("Y##model-reference-offset-y", ref _modelReferenceOffsetY, 0.01f, -64f, 64f, "%.2f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(74f);
        ImGui.DragFloat("Z##model-reference-offset-z", ref _modelReferenceOffsetZ, 0.01f, -64f, 64f, "%.2f");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Reference offset in rendered block units.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Reset##model-reference-transform-reset"))
        {
            _modelReferenceScale = 1f;
            _modelReferenceOffsetX = 0f;
            _modelReferenceOffsetY = 0f;
            _modelReferenceOffsetZ = 0f;
        }

        if (!string.IsNullOrWhiteSpace(_modelReferenceSkipReason))
        {
            ImGui.TextColored(new NVector4(0.95f, 0.72f, 0.42f, 1f), _modelReferenceSkipReason);
        }
    }

    private void DrawModelViewportSurface()
    {
        if (_modelDoc == null) return;

        NVector2 available = ImGui.GetContentRegionAvail();
        NVector2 size = new(Math.Max(320f, available.X), Math.Max(260f, available.Y));
        ImGui.InvisibleButton("##model-viewport-surface", size);
        NVector2 min = ImGui.GetItemRectMin();
        NVector2 max = ImGui.GetItemRectMax();
        bool hovered = ImGui.IsItemHovered();
        NVector2 afterViewportCursor = ImGui.GetCursorScreenPos();
        bool toolOverlayActive = HandleModelViewportToolOverlayInput(min, max);
        if (toolOverlayActive) hovered = false;

        if (hovered && !_modelGizmoDragging)
        {
            NVector2 delta = ImGui.GetIO().MouseDelta;
            bool pan = ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
                (ImGui.IsMouseDragging(ImGuiMouseButton.Right) && (ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift)));
            if (pan)
            {
                DevToolsPreviewCamera panCamera = BuildModelViewportCamera(min, max);
                float panScale = _modelViewportDistance / Math.Max(120f, size.Y);
                _modelViewportTarget -= panCamera.Right * delta.X * panScale;
                _modelViewportTarget += panCamera.Up * delta.Y * panScale;
            }
            else if (ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                _modelViewportYaw += delta.X * 0.01f;
                _modelViewportPitch = Math.Clamp(_modelViewportPitch + delta.Y * 0.01f, -1.45f, 1.45f);
            }

            float wheel = ImGui.GetIO().MouseWheel;
            if (Math.Abs(wheel) > 0.001f)
            {
                if (IsDevToolsCtrlDown() && (ModelMeshComponentsActive()
                    ? ModelNudgeSelectedMeshComponents(
                        _modelWheelNudgeAxis == 0 ? wheel * ModelNudgeStep() : 0d,
                        _modelWheelNudgeAxis == 1 ? wheel * ModelNudgeStep() : 0d,
                        _modelWheelNudgeAxis == 2 ? wheel * ModelNudgeStep() : 0d)
                    : ModelNudgeSelectedElements(_modelWheelNudgeAxis, wheel * ModelNudgeStep())))
                {
                    // Ctrl+wheel is reserved for selection nudging; plain wheel stays camera zoom.
                }
                else
                {
                    _modelViewportDistance = Math.Clamp(_modelViewportDistance * MathF.Pow(0.88f, wheel), 0.2f, 80f);
                }
            }
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint background = ImGui.ColorConvertFloat4ToU32(DevToolsViewportBackground.FillColor);
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 1f));
        uint gridMinor = ImGui.ColorConvertFloat4ToU32(DevToolsViewportBackground.GridMinorColor);
        uint gridMajor = ImGui.ColorConvertFloat4ToU32(DevToolsViewportBackground.GridMajorColor);
        uint text = ImGui.ColorConvertFloat4ToU32(DevToolsViewportBackground.TextColor);
        drawList.AddRectFilled(min, max, background, 4f);

        DevToolsPreviewCamera camera = BuildModelViewportCamera(min, max);
        List<DevToolsPreviewMeshInstance> instances = [];
        if (_modelReferenceVisible && _modelReferenceMesh != null)
        {
            instances.Add(new(
                _modelReferenceMesh,
                ModelReferenceMatrix(),
                new Vector4(0.72f, 0.86f, 1f, _modelReferenceOpacity)));
        }
        if (_modelPreviewMesh != null)
        {
            instances.Add(new(_modelPreviewMesh, CreateIdentityMatrix()));
        }

        int textureId = EnsureModelPreviewRenderer().RenderToTexture(max.X - min.X, max.Y - min.Y, camera, instances, out string? skipReason);
        if (textureId > 0)
        {
            drawList.AddImage(new IntPtr(textureId), min, max, new NVector2(0f, 1f), new NVector2(1f, 0f));
            if (_modelViewportScreenshotQueued)
            {
                _modelViewportScreenshotQueued = false;
                DevToolsTextureCapture.SaveTexture2D(textureId, (int)MathF.Ceiling(max.X - min.X), (int)MathF.Ceiling(max.Y - min.Y), "model-editor", out _modelStatus);
            }
        }
        else
        {
            _modelViewportScreenshotQueued = false;
        }

        drawList.PushClipRect(min, max, true);
        try
        {
            DrawModelViewportGrid(drawList, camera, gridMinor, gridMajor);
            DrawModelMeshFallbackOverlays(drawList, camera);

            string? renderNote = _modelPreviewSkipReason ?? skipReason;
            if (textureId <= 0 && !string.IsNullOrWhiteSpace(renderNote))
            {
                drawList.AddText(min + new NVector2(12f, 48f), text, $"Preview skipped: {renderNote}");
            }
            else if (!string.IsNullOrWhiteSpace(_modelPreviewSkipReason))
            {
                drawList.AddText(min + new NVector2(12f, 48f), text, _modelPreviewSkipReason);
            }

            ModelElementData? selected = _modelSelectedElement;
            bool gizmoConsumedMouse = false;
            List<ModelElementData> selectedElements = ModelSelectedElementsInDocument();
            foreach (ModelElementData selectedElement in selectedElements)
            {
                if (ReferenceEquals(selectedElement, selected)) continue;
                DrawModelSelectionOverlay(drawList, camera, selectedElement, active: false);
            }

            if (selected != null && _modelDoc.EnumerateElements().Contains(selected))
            {
                DrawModelSelectionOverlay(drawList, camera, selected, active: true);
            }

            if (_modelGizmoTool == ModelGizmoTool.Cut)
            {
                gizmoConsumedMouse = DrawModelCutTool(drawList, camera, hovered);
            }
            else if (_modelGizmoTool == ModelGizmoTool.Chisel)
            {
                gizmoConsumedMouse = DrawModelChiselTool(drawList, camera, hovered);
            }
            else if (_modelGizmoTool is ModelGizmoTool.Extrude or ModelGizmoTool.Inset or ModelGizmoTool.Subdivide)
            {
                gizmoConsumedMouse = false;
            }
            else if (selected != null && _modelDoc.EnumerateElements().Contains(selected))
            {
                gizmoConsumedMouse = DrawModelGizmo(drawList, camera, selected, hovered);
            }
            else if (_modelGizmoDragging)
            {
                ModelEndGizmoDrag(commit: false);
            }

            // Safety net: if the tool changed or the element was hidden mid-drag the
            // per-tool drag handler no longer runs, so end the gesture here.
            if (_modelGizmoDragging && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                ModelEndGizmoDrag(commit: true);
            }

            DrawModelPrimitiveGhost(drawList, camera);
            DrawModelCreatureGhost(drawList, camera);
            DrawPlayerModelGhost(drawList, camera);
            DrawModelClothingGhost(drawList, camera);
            DrawWeaponGhost(drawList, camera);

            if (hovered && !gizmoConsumedMouse && !_modelGizmoDragging && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                bool additive = IsDevToolsCtrlDown();
                if (!ModelHandleMeshViewportSelection(camera, ImGui.GetMousePos(), additive))
                {
                    ModelElementData? picked = ModelPickElement(camera, ImGui.GetMousePos());
                    if (picked != null || !additive)
                    {
                        ModelSelectElement(picked, additive: additive);
                    }
                }
            }

            drawList.AddText(min + new NVector2(12f, 10f), text, _modelDoc.DisplayPath);
            float nextTextY = 28f;
            if (selected != null)
            {
                drawList.AddText(min + new NVector2(12f, 28f), text,
                    $"{selected.Name}  from [{selected.From[0]:0.##}, {selected.From[1]:0.##}, {selected.From[2]:0.##}]  size [{selected.SizeX:0.##}, {selected.SizeY:0.##}, {selected.SizeZ:0.##}]  selected {selectedElements.Count}");
                nextTextY = 46f;
            }
            string? referenceDisplay = ModelReferenceDisplay();
            if (_modelReferenceVisible && referenceDisplay != null)
            {
                drawList.AddText(min + new NVector2(12f, nextTextY), text, $"Ref: {referenceDisplay}");
            }
        }
        finally
        {
            drawList.PopClipRect();
        }
        drawList.AddRect(min, max, border, 4f);
        DrawModelViewportToolOverlay(min, max);
        ImGui.SetCursorScreenPos(afterViewportCursor);
    }

    private NVector2 ModelViewportToolOverlayPosition(NVector2 viewportMin, NVector2 viewportMax)
    {
        return new NVector2(viewportMax.X - ModelViewportToolOverlaySize().X - 12f, viewportMin.Y + 12f);
    }

    private NVector2 ModelViewportToolOverlaySize()
    {
        float rowHeight = Math.Max(20f, ImGui.GetFrameHeight());
        float spacingY = ImGui.GetStyle().ItemSpacing.Y;
        int rows = _modelGizmoTool == ModelGizmoTool.Cut ? 11 : ModelIsMeshLibMode ? 7 : 6;
        return new NVector2(112f, rowHeight * rows + spacingY * (rows - 1) + 10f);
    }

    private bool HandleModelViewportToolOverlayInput(NVector2 viewportMin, NVector2 viewportMax)
    {
        NVector2 point = ImGui.GetMousePos();
        NVector2 position = ModelViewportToolOverlayPosition(viewportMin, viewportMax);
        NVector2 size = ModelViewportToolOverlaySize();
        bool inside = point.X >= position.X && point.X <= position.X + size.X &&
            point.Y >= position.Y && point.Y <= position.Y + size.Y;
        if (!inside) return false;

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            float localY = point.Y - position.Y - 5f;
            float rowStride = Math.Max(20f, ImGui.GetFrameHeight()) + ImGui.GetStyle().ItemSpacing.Y;
            int row = (int)MathF.Floor(localY / Math.Max(1f, rowStride));
            int primaryRows = ModelIsMeshLibMode ? 7 : 6;
            if (row >= 0 && row < primaryRows)
            {
                if (_modelGizmoDragging) ModelEndGizmoDrag(commit: true);
                _modelGizmoTool = row switch
                {
                    0 => ModelGizmoTool.None,
                    1 => ModelGizmoTool.Move,
                    2 => ModelGizmoTool.Resize,
                    3 => ModelGizmoTool.Rotate,
                    4 => ModelIsMeshLibMode ? ModelGizmoTool.Extrude : ModelGizmoTool.Cut,
                    5 => ModelIsMeshLibMode ? ModelGizmoTool.Inset : ModelGizmoTool.Chisel,
                    _ => ModelGizmoTool.Subdivide
                };
            }
            else if (_modelGizmoTool == ModelGizmoTool.Cut && row >= 7 && row < 11)
            {
                _modelCutOrientation = row switch
                {
                    8 => ModelCutOrientation.X,
                    9 => ModelCutOrientation.Y,
                    10 => ModelCutOrientation.Z,
                    _ => ModelCutOrientation.Auto
                };
            }
        }

        return true;
    }

    private bool DrawModelViewportToolOverlay(NVector2 viewportMin, NVector2 viewportMax)
    {
        NVector2 position = ModelViewportToolOverlayPosition(viewportMin, viewportMax);
        NVector2 size = ModelViewportToolOverlaySize();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint fill = ImGui.ColorConvertFloat4ToU32(new NVector4(0.06f, 0.055f, 0.05f, 0.84f));
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 0.92f));
        drawList.AddRectFilled(position - new NVector2(6f, 5f), position + size, fill, 4f);
        drawList.AddRect(position - new NVector2(6f, 5f), position + size, border, 4f);

        ImGui.SetCursorScreenPos(position);
        ImGui.PushID("model-viewport-tools");
        bool hoveredOrActive = false;
        try
        {
            float rowStride = Math.Max(20f, ImGui.GetFrameHeight()) + ImGui.GetStyle().ItemSpacing.Y;
            DrawModelViewportToolRadio(position, rowStride, 0, "Select", ModelGizmoTool.None, "Select elements in the viewport (Ctrl+Shift+1).", ref hoveredOrActive);
            DrawModelViewportToolRadio(position, rowStride, 1, "Move", ModelGizmoTool.Move, "Drag the axis arrows to translate the selection (Ctrl+Shift+2).", ref hoveredOrActive);
            DrawModelViewportToolRadio(position, rowStride, 2, "Resize", ModelGizmoTool.Resize, "Drag face or corner handles to resize/deform the selection (Ctrl+Shift+3).", ref hoveredOrActive);
            DrawModelViewportToolRadio(position, rowStride, 3, "Rotate", ModelGizmoTool.Rotate, "Drag the rings to rotate around the rotation origin (Ctrl+Shift+4).", ref hoveredOrActive);
            if (ModelIsMeshLibMode)
            {
                DrawModelViewportToolRadio(position, rowStride, 4, "Extrude", ModelGizmoTool.Extrude, "Select connected faces, set the distance in the toolbar, then Apply.", ref hoveredOrActive);
                DrawModelViewportToolRadio(position, rowStride, 5, "Inset", ModelGizmoTool.Inset, "Select a coplanar face region, set its inset fraction, then Apply.", ref hoveredOrActive);
                DrawModelViewportToolRadio(position, rowStride, 6, "Subdivide", ModelGizmoTool.Subdivide, "Select faces or edges and use Subdivide in the toolbar or inspector.", ref hoveredOrActive);
            }
            else
            {
                DrawModelViewportToolRadio(position, rowStride, 4, "Cut", ModelGizmoTool.Cut, "Hover a cuboid to preview a cut line, then click to split it (Ctrl+Shift+5).", ref hoveredOrActive);
                DrawModelViewportToolRadio(position, rowStride, 5, "Chisel", ModelGizmoTool.Chisel, "Add or remove one microblock on the hovered face (Ctrl+Shift+6).", ref hoveredOrActive);
            }
            if (_modelGizmoTool == ModelGizmoTool.Cut)
            {
                ImGui.SetCursorScreenPos(position + new NVector2(0f, 6f * rowStride));
                ImGui.TextDisabled("Cut axis");
                hoveredOrActive |= ImGui.IsItemHovered() || ImGui.IsItemActive();
                DrawModelCutOrientationRadio(position, rowStride, 7, "Auto", ModelCutOrientation.Auto, "Pick the best cut axis from the hovered face.", ref hoveredOrActive);
                DrawModelCutOrientationRadio(position, rowStride, 8, "X", ModelCutOrientation.X, "Cut along the element's local X axis.", ref hoveredOrActive);
                DrawModelCutOrientationRadio(position, rowStride, 9, "Y", ModelCutOrientation.Y, "Cut along the element's local Y axis.", ref hoveredOrActive);
                DrawModelCutOrientationRadio(position, rowStride, 10, "Z", ModelCutOrientation.Z, "Cut along the element's local Z axis.", ref hoveredOrActive);
            }
        }
        finally
        {
            ImGui.PopID();
        }

        return hoveredOrActive;
    }

    private void DrawModelViewportToolRadio(NVector2 position, float rowStride, int row, string label, ModelGizmoTool tool, string tooltip, ref bool hoveredOrActive)
    {
        ImGui.SetCursorScreenPos(position + new NVector2(0f, row * rowStride));
        if (ImGui.RadioButton($"{label}##{label}", _modelGizmoTool == tool))
        {
            if (_modelGizmoDragging) ModelEndGizmoDrag(commit: true);
            _modelGizmoTool = tool;
        }

        hoveredOrActive |= ImGui.IsItemHovered() || ImGui.IsItemActive();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    private void DrawModelCutOrientationRadio(NVector2 position, float rowStride, int row, string label, ModelCutOrientation orientation, string tooltip, ref bool hoveredOrActive)
    {
        ImGui.SetCursorScreenPos(position + new NVector2(0f, row * rowStride));
        if (ImGui.RadioButton($"{label}##cut-orientation-{label}", _modelCutOrientation == orientation))
        {
            _modelCutOrientation = orientation;
        }

        hoveredOrActive |= ImGui.IsItemHovered() || ImGui.IsItemActive();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    private DevToolsPreviewCamera BuildModelViewportCamera(NVector2 min, NVector2 max)
    {
        return DevToolsPreviewCamera.Orbit(min, max, _modelViewportTarget, _modelViewportYaw, _modelViewportPitch, _modelViewportDistance);
    }

    private DevToolsPreview3DRenderer EnsureModelPreviewRenderer()
    {
        return _modelPreviewRenderer ??= new DevToolsPreview3DRenderer(_api);
    }

    private void ModelDisposePreviewResources()
    {
        _modelPreviewMesh?.Dispose();
        _modelPreviewMesh = null;
        _modelReferenceMesh?.Dispose();
        _modelReferenceMesh = null;
        _modelPreviewRenderer?.Dispose();
        _modelPreviewRenderer = null;
    }

    private void ModelRebuildPreviewMeshIfNeeded()
    {
        if (!_modelPreviewDirty) return;

        // Gizmo input mutates the document after this draw path has rendered the current mesh. Rebuilding
        // here on every drag frame therefore stays one input frame behind while repeatedly serializing,
        // parsing, tesselating, uploading and disposing the complete model. Keep the last solid mesh during
        // the gesture; the live selection wireframe still reflects the edited bounds, and the dirty flag
        // causes one coalesced rebuild on the first frame after the drag ends.
        if (_modelGizmoDragging && _modelPreviewMesh != null) return;

        _modelPreviewDirty = false;
        _modelPreviewMesh?.Dispose();
        _modelPreviewMesh = null;
        _modelPreviewSkipReason = null;
        if (_modelDoc == null) return;

        try
        {
            string json = ModelSerializeDocument(_modelDoc, includeInvisible: false, indented: false);
            _modelPreviewMesh = ModelBuildShapePreviewMesh(json, _modelDoc.Domain, _modelDoc.DisplayPath, out _modelPreviewSkipReason);
            if (_modelCameraFitPending)
            {
                _modelCameraFitPending = false;
                ModelFitCameraToMesh();
            }
        }
        catch (Exception exception)
        {
            _modelPreviewSkipReason = $"Preview failed: {exception.Message}";
            _modelDiagnostics.Exception("Model preview tesselation failed", exception);
        }
    }

    private void ModelRebuildReferenceMeshIfNeeded()
    {
        if (!_modelReferenceDirty) return;

        _modelReferenceDirty = false;
        _modelReferenceMesh?.Dispose();
        _modelReferenceMesh = null;
        _modelReferenceSkipReason = null;

        if (_modelReferenceEntityType != null)
        {
            try
            {
                string label = _modelReferenceEntityDisplay ?? $"entity:{_modelReferenceEntityType.Code}";
                _modelReferenceMesh = ModelBuildEntityReferencePreviewMesh(_modelReferenceEntityType, label, out _modelReferenceSkipReason);
                if (_modelReferenceMesh == null && string.IsNullOrWhiteSpace(_modelReferenceSkipReason))
                {
                    _modelReferenceSkipReason = $"Reference preview failed for {label}.";
                }
            }
            catch (Exception exception)
            {
                _modelReferenceSkipReason = $"Reference failed: {exception.Message}";
                _modelDiagnostics.Exception("Model entity reference tesselation failed", exception);
            }

            return;
        }

        if (_modelReferenceEntry == null) return;

        try
        {
            string json = _modelReferenceEntry.Asset.ToText();
            _modelReferenceMesh = ModelBuildShapePreviewMesh(json, _modelReferenceEntry.Domain, $"ref:{_modelReferenceEntry.Display}", out _modelReferenceSkipReason);
            if (_modelReferenceMesh == null && string.IsNullOrWhiteSpace(_modelReferenceSkipReason))
            {
                _modelReferenceSkipReason = $"Reference preview failed for {_modelReferenceEntry.Display}.";
            }
        }
        catch (Exception exception)
        {
            _modelReferenceSkipReason = $"Reference failed: {exception.Message}";
            _modelDiagnostics.Exception("Model reference tesselation failed", exception);
        }
    }

    private DevToolsPreviewMesh? ModelBuildShapePreviewMesh(string json, string domain, string label, out string? skipReason)
    {
        skipReason = null;
        Shape? shape = JsonUtil.ToObject<Shape>(json, domain);
        string attachmentStatus = "";
        bool attached = shape != null && ModelTryAttachMeshLibShape(shape, json, label, out attachmentStatus);
        DevToolsPreviewMesh? preview = ModelBuildShapePreviewMesh(
            shape,
            label,
            _ => null,
            resolvedShape => new ShapeTextureSource(_api, resolvedShape, label),
            out skipReason);
        if (!attached && json.Contains("\"noncuboid\"", StringComparison.OrdinalIgnoreCase))
        {
            skipReason = string.IsNullOrWhiteSpace(attachmentStatus)
                ? skipReason
                : preview == null ? attachmentStatus : attachmentStatus + " Cuboid elements still preview normally.";
        }
        return preview;
    }

    private DevToolsPreviewMesh? ModelBuildEntityReferencePreviewMesh(EntityProperties entityType, string label, out string? skipReason)
    {
        skipReason = null;
        EntityClientProperties? client = entityType.Client;
        Shape? sourceShape = client?.LoadedShapeForEntity ?? client?.LoadedShape;
        Shape? shape = sourceShape?.Clone();
        if (shape == null)
        {
            skipReason = "Player reference has no loaded shape.";
            return null;
        }

        CompositeShape? compositeShape = client?.ShapeForEntity ?? client?.Shape;
        IDictionary<string, CompositeTexture> textures = client?.Textures ?? new Dictionary<string, CompositeTexture>();
        return ModelBuildShapePreviewMesh(
            shape,
            label,
            _ => compositeShape,
            resolvedShape => new VanillaEntityTextureSource(_api, resolvedShape, label, textures),
            out skipReason);
    }

    private DevToolsPreviewMesh? ModelBuildShapePreviewMesh(
        Shape? shape,
        string label,
        System.Func<Shape, CompositeShape?> compositeShapeSelector,
        System.Func<Shape, ITexPositionSource> textureSourceFactory,
        out string? skipReason)
    {
        skipReason = null;
        if (shape?.Elements == null || shape.Elements.Length == 0)
        {
            skipReason = "No visible elements.";
            return null;
        }

        try
        {
            shape.ResolveReferences(_api.Logger, label);
        }
        catch (Exception exception)
        {
            LoggerUtil.Verbose(_api, this, $"Model preview reference resolve failed for {label}: {exception.Message}");
        }

        CompositeShape? compositeShape = compositeShapeSelector(shape);
        ITexPositionSource textureSource = textureSourceFactory(shape);
        TesselationMetaData meta = new()
        {
            TexSource = textureSource,
            WithJointIds = compositeShape != null,
            WithDamageEffect = compositeShape != null,
            TypeForLogging = label,
            QuantityElements = compositeShape?.QuantityElements,
            SelectiveElements = compositeShape?.SelectiveElements,
            IgnoreElements = compositeShape?.IgnoreElements,
            Rotation = compositeShape == null
                ? null
                : new Vec3f(compositeShape.rotateX, compositeShape.rotateY, compositeShape.rotateZ)
        };
        _api.Tesselator.TesselateShape(meta, shape, out MeshData mesh);
        if (mesh == null || mesh.VerticesCount <= 0)
        {
            skipReason = "Tesselation produced no geometry.";
            return null;
        }

        if (compositeShape != null)
        {
            mesh.Translate(compositeShape.offsetX, compositeShape.offsetY, compositeShape.offsetZ);
        }

        return DevToolsPreviewMeshFactory.FromMesh(_api, label, mesh);
    }

    private void ModelSetReferenceEntry(ModelShapeAssetEntry? entry)
    {
        if (_modelReferenceEntry != null &&
            entry != null &&
            string.Equals(_modelReferenceEntry.Display, entry.Display, StringComparison.OrdinalIgnoreCase) &&
            _modelReferenceEntry.Authored == entry.Authored &&
            _modelReferenceEntityType == null)
        {
            return;
        }

        _modelReferenceEntry = entry;
        _modelReferenceEntityType = null;
        _modelReferenceEntityDisplay = null;
        _modelReferenceDirty = true;
        _modelReferenceSkipReason = null;
        if (entry == null)
        {
            _modelReferenceMesh?.Dispose();
            _modelReferenceMesh = null;
        }
    }

    private void ModelSetReferenceEntity(EntityProperties entityType, string display)
    {
        if (ReferenceEquals(_modelReferenceEntityType, entityType) &&
            string.Equals(_modelReferenceEntityDisplay, display, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _modelReferenceEntry = null;
        _modelReferenceEntityType = entityType;
        _modelReferenceEntityDisplay = display;
        _modelReferenceDirty = true;
        _modelReferenceSkipReason = null;
    }

    private void ModelSetPlayerReference()
    {
        EntityProperties? playerEntityType = _api.World?.Player?.Entity?.Properties;
        if (ModelCanUseEntityReference(playerEntityType))
        {
            ModelSetReferenceEntity(playerEntityType!, "current player");
            _modelStatus = "Reference set to current player.";
            return;
        }

        playerEntityType = ModelFindPlayerEntityReference();
        if (ModelCanUseEntityReference(playerEntityType))
        {
            string code = playerEntityType!.Code?.ToString() ?? "player";
            ModelSetReferenceEntity(playerEntityType, $"entity:{code}");
            _modelStatus = $"Reference set to entity:{code}.";
            return;
        }

        ModelShapeAssetEntry? player = ModelFindPlayerReferenceEntry();
        if (player != null)
        {
            ModelSetReferenceEntry(player);
            _modelStatus = $"Reference set to {player.Display}.";
            return;
        }

        _modelStatus = "No player-like shape found in loaded assets.";
    }

    private EntityProperties? ModelFindPlayerEntityReference()
    {
        try
        {
            IEnumerable<EntityProperties>? entityTypes = _api.World?.EntityTypes;
            if (entityTypes == null) return null;

            return entityTypes
                .Where(ModelCanUseEntityReference)
                .OrderBy(ModelPlayerEntityReferenceScore)
                .ThenBy(entityType => entityType.Code?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(entityType => ModelPlayerEntityReferenceScore(entityType) < int.MaxValue);
        }
        catch (Exception exception)
        {
            _modelDiagnostics.Exception("Player entity reference lookup failed", exception);
            return null;
        }
    }

    private static bool ModelCanUseEntityReference(EntityProperties? entityType)
    {
        Shape? shape = entityType?.Client?.LoadedShapeForEntity ?? entityType?.Client?.LoadedShape;
        return shape?.Elements != null && shape.Elements.Length > 0;
    }

    private static int ModelPlayerEntityReferenceScore(EntityProperties entityType)
    {
        string code = entityType.Code?.ToString()?.ToLowerInvariant() ?? "";
        if (code == "game:player") return 0;
        if (code.EndsWith(":player", StringComparison.Ordinal)) return 1;
        if (code.Contains("humanoid/player", StringComparison.Ordinal)) return 5;
        if (code.Contains("player", StringComparison.Ordinal)) return 10;
        if (code.Contains("seraph", StringComparison.Ordinal)) return 20;
        if (code.Contains("humanoid", StringComparison.Ordinal)) return 30;
        return int.MaxValue;
    }

    private string? ModelReferenceDisplay()
    {
        if (_modelReferenceEntityType != null) return _modelReferenceEntityDisplay ?? $"entity:{_modelReferenceEntityType.Code}";
        return _modelReferenceEntry == null ? null : ModelReferenceLabel(_modelReferenceEntry);
    }

    private ModelShapeAssetEntry? ModelFindPlayerReferenceEntry()
    {
        EnsureModelShapeIndex();
        List<ModelShapeAssetEntry> index = _modelShapeIndex ?? [];
        if (index.Count == 0) return null;

        return index
            .Select(entry => (Entry: entry, Score: ModelPlayerReferenceScore(entry)))
            .Where(candidate => candidate.Score < int.MaxValue)
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Entry.AssetPath.Length)
            .ThenBy(candidate => candidate.Entry.AssetPath, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Entry)
            .FirstOrDefault();
    }

    private static int ModelPlayerReferenceScore(ModelShapeAssetEntry entry)
    {
        string display = entry.Display.ToLowerInvariant();
        string path = entry.AssetPath.ToLowerInvariant();
        int domainBonus = entry.Domain.Equals("game", StringComparison.OrdinalIgnoreCase) ? 0 : 1000;

        if (display == "game:shapes/entity/humanoid/seraph-faceless.json") return domainBonus;
        if (display == "game:shapes/entity/humanoid/player.json") return domainBonus + 1;
        if (display == "game:shapes/entity/player.json") return domainBonus + 2;
        if (display == "game:shapes/entity/humanoid/seraph-hairless.json") return domainBonus + 3;
        if (display == "game:shapes/entity/humanoid/seraph.json") return domainBonus + 4;
        if (path.Contains("shapes/entity/humanoid", StringComparison.Ordinal) && path.Contains("player", StringComparison.Ordinal)) return domainBonus + 10;
        if (path.Contains("shapes/entity/humanoid", StringComparison.Ordinal) && path.Contains("seraph-faceless", StringComparison.Ordinal)) return domainBonus + 11;
        if (path.Contains("shapes/entity/humanoid", StringComparison.Ordinal) && path.Contains("seraph", StringComparison.Ordinal)) return domainBonus + 12;
        if (path.Contains("shapes/entity/player", StringComparison.Ordinal)) return domainBonus + 20;
        if (path.Contains("shapes/entity/humanoid", StringComparison.Ordinal)) return domainBonus + 30;
        if (path.Contains("player", StringComparison.Ordinal)) return domainBonus + 60;
        if (path.Contains("seraph", StringComparison.Ordinal)) return domainBonus + 70;
        if (path.Contains("human", StringComparison.Ordinal)) return domainBonus + 90;
        return int.MaxValue;
    }

    private static string ModelReferenceLabel(ModelShapeAssetEntry entry)
    {
        return entry.Display + (entry.Authored ? " [authored]" : "");
    }

    private void ModelResetCameraToFit()
    {
        _modelViewportYaw = 0.7f;
        _modelViewportPitch = -0.45f;
        _modelViewportDistance = 2.4f;
        _modelViewportTarget = new Vector3(0.5f, 0.5f, 0.5f);
        _modelCameraFitPending = true;
    }

    private void ModelFitCameraToMesh()
    {
        DevToolsPreviewBounds bounds = _modelPreviewMesh?.Bounds ?? DevToolsPreviewBounds.Empty;
        if (!bounds.IsValid && _modelDoc != null) bounds = ModelElementsWorldBounds(_modelDoc.Roots);
        ModelFitCameraToBounds(bounds, 2.6f);
    }

    private void ModelFitCameraToReference()
    {
        ModelFitCameraToBounds(ModelReferenceTransformedBounds(), 2.8f);
    }

    private void ModelFitCameraToScene()
    {
        ModelFitCameraToBounds(ModelViewportSceneBounds(), 2.8f);
    }

    private void ModelFitCameraToBounds(DevToolsPreviewBounds bounds, float distanceScale)
    {
        if (!bounds.IsValid) return;
        _modelViewportTarget = bounds.Center;
        _modelViewportDistance = Math.Clamp(bounds.Radius * distanceScale, 0.4f, 70f);
    }

    private void ModelFocusCameraOnSelection()
    {
        if (_modelDoc == null || _modelSelectedElement == null)
        {
            ModelFitCameraToMesh();
            return;
        }

        DevToolsPreviewBounds bounds = ModelElementsWorldBounds(ModelSelectedElementsInDocument());
        if (!bounds.IsValid) return;

        _modelViewportTarget = bounds.Center;
        _modelViewportDistance = Math.Clamp(bounds.Radius * 3.2f, 0.3f, 70f);
    }

    private static Matrixf ModelLocalElementMatrix(ModelElementData element)
    {
        float originX = 0f;
        float originY = 0f;
        float originZ = 0f;
        if (element.RotationOrigin != null)
        {
            originX = (float)element.RotationOrigin[0] / ModelUnitsPerBlock;
            originY = (float)element.RotationOrigin[1] / ModelUnitsPerBlock;
            originZ = (float)element.RotationOrigin[2] / ModelUnitsPerBlock;
        }

        Matrixf matrix = new();
        matrix.Identity();
        matrix.Translate(originX, originY, originZ);
        matrix.Rotate(
            (float)(element.RotationX * GameMath.DEG2RAD),
            (float)(element.RotationY * GameMath.DEG2RAD),
            (float)(element.RotationZ * GameMath.DEG2RAD));
        matrix.Translate(
            (float)element.From[0] / ModelUnitsPerBlock - originX,
            (float)element.From[1] / ModelUnitsPerBlock - originY,
            (float)element.From[2] / ModelUnitsPerBlock - originZ);
        return matrix;
    }

    private static Matrixf ModelComputeElementMatrix(ModelElementData element)
    {
        List<ModelElementData> chain = [];
        for (ModelElementData? current = element; current != null; current = current.Parent)
        {
            chain.Add(current);
        }
        chain.Reverse();

        Matrixf matrix = new();
        matrix.Identity();
        foreach (ModelElementData node in chain)
        {
            matrix.Mul(ModelLocalElementMatrix(node).Values);
        }
        return matrix;
    }

    private static Matrixf ModelComputeParentChainMatrix(ModelElementData element)
    {
        Matrixf matrix = new();
        matrix.Identity();
        if (element.Parent == null) return matrix;
        return ModelComputeElementMatrix(element.Parent);
    }

    private static Vector3 ModelTransformPoint(Matrixf matrix, Vector3 point)
    {
        Vec4f transformed = matrix.TransformVector(new Vec4f(point.X, point.Y, point.Z, 1f));
        return new Vector3(transformed.X, transformed.Y, transformed.Z);
    }

    private static Vector3 ModelTransformDirection(Matrixf matrix, Vector3 direction)
    {
        Vec4f transformed = matrix.TransformVector(new Vec4f(direction.X, direction.Y, direction.Z, 0f));
        Vector3 result = new(transformed.X, transformed.Y, transformed.Z);
        return result.LengthSquared < 0.000001f ? direction : Vector3.Normalize(result);
    }

    private static Vector3[] ModelTransformBoxCorners(Matrixf matrix, ModelElementData element)
    {
        Vector3[] corners = ModelLocalBoxCorners(element);
        for (int index = 0; index < corners.Length; index++)
        {
            corners[index] = ModelTransformPoint(matrix, corners[index]);
        }
        return corners;
    }

    private Matrixf ModelReferenceMatrix()
    {
        Matrixf matrix = new();
        matrix.Identity();
        matrix.Translate(_modelReferenceOffsetX, _modelReferenceOffsetY, _modelReferenceOffsetZ);
        matrix.Scale(_modelReferenceScale, _modelReferenceScale, _modelReferenceScale);
        return matrix;
    }

    private DevToolsPreviewBounds ModelReferenceTransformedBounds()
    {
        if (!_modelReferenceVisible || _modelReferenceMesh == null) return DevToolsPreviewBounds.Empty;

        DevToolsPreviewBounds bounds = DevToolsPreviewBounds.Empty;
        foreach (Vector3 corner in ModelTransformBoundsCorners(ModelReferenceMatrix(), _modelReferenceMesh.Bounds))
        {
            bounds = bounds.Include(corner);
        }

        return bounds;
    }

    private DevToolsPreviewBounds ModelViewportSceneBounds()
    {
        DevToolsPreviewBounds bounds = DevToolsPreviewBounds.Empty;
        if (_modelPreviewMesh != null) bounds = bounds.Include(_modelPreviewMesh.Bounds);
        if (_modelDoc != null) bounds = bounds.Include(ModelElementsWorldBounds(_modelDoc.Roots));
        bounds = bounds.Include(ModelReferenceTransformedBounds());
        return bounds;
    }

    private void DrawModelViewportGrid(ImDrawListPtr drawList, DevToolsPreviewCamera camera, uint minorColor, uint majorColor)
    {
        const int subdivisions = 16;
        (int minX, int maxX, int minY, int maxY, int minZ, int maxZ) = ModelReferenceBlockRange(ModelViewportSceneBounds());

        uint referenceColor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.36f, 0.35f, 0.28f, 0.42f));
        uint referenceGround = ImGui.ColorConvertFloat4ToU32(new NVector4(0.48f, 0.45f, 0.34f, 0.55f));

        for (int y = minY; y <= maxY; y++)
        {
            uint color = y == 0 ? referenceGround : referenceColor;
            for (int x = minX; x <= maxX; x++)
            {
                DrawModelViewportLine(drawList, camera, new Vector3(x, y, minZ), new Vector3(x, y, maxZ), color, 1f);
            }
            for (int z = minZ; z <= maxZ; z++)
            {
                DrawModelViewportLine(drawList, camera, new Vector3(minX, y, z), new Vector3(maxX, y, z), color, 1f);
            }
        }

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                DrawModelViewportLine(drawList, camera, new Vector3(x, minY, z), new Vector3(x, maxY, z), referenceColor, 1f);
            }
        }

        for (int line = 0; line <= subdivisions; line++)
        {
            float offset = line / (float)subdivisions;
            uint color = line == 0 || line == subdivisions ? majorColor : minorColor;
            DrawModelViewportLine(drawList, camera, new Vector3(offset, 0f, 0f), new Vector3(offset, 0f, 1f), color, 1f);
            DrawModelViewportLine(drawList, camera, new Vector3(0f, 0f, offset), new Vector3(1f, 0f, offset), color, 1f);
        }

        // Emphasize the edited block's 0..1 bounds, then draw colored origin axes.
        DrawModelReferenceBlockWireframe(drawList, camera, Vector3.Zero, Vector3.One, majorColor, 1.5f);
        uint axisX = ImGui.ColorConvertFloat4ToU32(new NVector4(0.85f, 0.25f, 0.16f, 0.9f));
        uint axisY = ImGui.ColorConvertFloat4ToU32(new NVector4(0.32f, 0.9f, 0.34f, 0.9f));
        uint axisZ = ImGui.ColorConvertFloat4ToU32(new NVector4(0.25f, 0.42f, 0.95f, 0.9f));
        DrawModelViewportLine(drawList, camera, Vector3.Zero, new Vector3(0.35f, 0f, 0f), axisX, 2.4f);
        DrawModelViewportLine(drawList, camera, Vector3.Zero, new Vector3(0f, 0.35f, 0f), axisY, 2.4f);
        DrawModelViewportLine(drawList, camera, Vector3.Zero, new Vector3(0f, 0f, 0.35f), axisZ, 2.4f);
    }

    private static (int MinX, int MaxX, int MinY, int MaxY, int MinZ, int MaxZ) ModelReferenceBlockRange(DevToolsPreviewBounds bounds)
    {
        float boundMinX = 0f;
        float boundMaxX = 1f;
        float boundMinY = 0f;
        float boundMaxY = 1f;
        float boundMinZ = 0f;
        float boundMaxZ = 1f;
        if (bounds.IsValid)
        {
            boundMinX = Math.Min(boundMinX, bounds.Min.X);
            boundMaxX = Math.Max(boundMaxX, bounds.Max.X);
            boundMinY = Math.Min(boundMinY, bounds.Min.Y);
            boundMaxY = Math.Max(boundMaxY, bounds.Max.Y);
            boundMinZ = Math.Min(boundMinZ, bounds.Min.Z);
            boundMaxZ = Math.Max(boundMaxZ, bounds.Max.Z);
        }

        int minX = (int)MathF.Floor(boundMinX) - ModelReferenceBlockPadding;
        int maxX = (int)MathF.Ceiling(boundMaxX) + ModelReferenceBlockPadding;
        int minY = (int)MathF.Floor(boundMinY) - 1;
        int maxY = (int)MathF.Ceiling(boundMaxY) + ModelReferenceBlockPadding;
        int minZ = (int)MathF.Floor(boundMinZ) - ModelReferenceBlockPadding;
        int maxZ = (int)MathF.Ceiling(boundMaxZ) + ModelReferenceBlockPadding;

        ClampModelReferenceSpan(ref minX, ref maxX, ModelReferenceMaxHorizontalBlocks, (boundMinX + boundMaxX) * 0.5f);
        ClampModelReferenceSpan(ref minY, ref maxY, ModelReferenceMaxVerticalBlocks, (boundMinY + boundMaxY) * 0.5f);
        ClampModelReferenceSpan(ref minZ, ref maxZ, ModelReferenceMaxHorizontalBlocks, (boundMinZ + boundMaxZ) * 0.5f);

        minX = Math.Min(minX, 0);
        maxX = Math.Max(maxX, 1);
        minY = Math.Min(minY, 0);
        maxY = Math.Max(maxY, 1);
        minZ = Math.Min(minZ, 0);
        maxZ = Math.Max(maxZ, 1);
        return (minX, maxX, minY, maxY, minZ, maxZ);
    }

    private static void ClampModelReferenceSpan(ref int min, ref int max, int maxBlocks, float center)
    {
        if (max - min <= maxBlocks) return;

        int centeredMin = (int)MathF.Floor(center) - maxBlocks / 2;
        int centeredMax = centeredMin + maxBlocks;
        if (centeredMin > 0)
        {
            centeredMax -= centeredMin;
            centeredMin = 0;
        }
        if (centeredMax < 1)
        {
            centeredMin += 1 - centeredMax;
            centeredMax = 1;
        }

        min = centeredMin;
        max = centeredMax;
    }

    private static void DrawModelReferenceBlockWireframe(ImDrawListPtr drawList, DevToolsPreviewCamera camera, Vector3 min, Vector3 max, uint color, float thickness)
    {
        Vector3[] corners =
        [
            new(min.X, min.Y, min.Z),
            new(max.X, min.Y, min.Z),
            new(max.X, max.Y, min.Z),
            new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z),
            new(max.X, min.Y, max.Z),
            new(max.X, max.Y, max.Z),
            new(min.X, max.Y, max.Z)
        ];

        foreach ((int a, int b) in ModelBoxEdges)
        {
            DrawModelViewportLine(drawList, camera, corners[a], corners[b], color, thickness);
        }
    }

    private static void DrawModelViewportLine(ImDrawListPtr drawList, DevToolsPreviewCamera camera, Vector3 from, Vector3 to, uint color, float thickness)
    {
        if (camera.Project(from, out NVector2 start, out _) && camera.Project(to, out NVector2 end, out _))
        {
            drawList.AddLine(start, end, color, thickness);
        }
    }

    private void DrawModelSelectionOverlay(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ModelElementData element, bool active)
    {
        if (DrawModelMeshSelectionOverlay(drawList, camera, element, active)) return;
        Matrixf matrix = ModelComputeElementMatrix(element);
        Vector3[] corners = ModelTryGetGroupLocalBounds(element, out DevToolsPreviewBounds localBounds)
            ? ModelTransformBoundsCorners(matrix, localBounds)
            : ModelTransformBoxCorners(matrix, element);
        uint wire = active
            ? ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.82f, 0.3f, 0.95f))
            : ImGui.ColorConvertFloat4ToU32(new NVector4(0.28f, 0.82f, 1f, 0.82f));
        foreach ((int a, int b) in ModelBoxEdges)
        {
            DrawModelViewportLine(drawList, camera, corners[a], corners[b], wire, active ? 1.6f : 1.2f);
        }

        if (active && _modelSelectedFace >= 0 && _modelSelectedFace < 6 && element.Faces[_modelSelectedFace] != null)
        {
            int[] faceCorners = ModelFaceCornerIndices(_modelSelectedFace);
            NVector2[] screen = new NVector2[4];
            bool visible = true;
            for (int index = 0; index < 4; index++)
            {
                if (!camera.Project(corners[faceCorners[index]], out screen[index], out _))
                {
                    visible = false;
                    break;
                }
            }
            if (visible)
            {
                uint fill = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.82f, 0.3f, 0.18f));
                drawList.AddQuadFilled(screen[0], screen[1], screen[2], screen[3], fill);
            }
        }
    }

    private static int[] ModelFaceCornerIndices(int faceIndex)
    {
        // Corner order matches ModelLocalBoxCorners; faces in N, E, S, W, U, D order.
        return faceIndex switch
        {
            0 => [0, 1, 2, 3],
            1 => [1, 5, 6, 2],
            2 => [5, 4, 7, 6],
            3 => [4, 0, 3, 7],
            4 => [3, 2, 6, 7],
            _ => [0, 4, 5, 1]
        };
    }

    private ModelElementData? ModelPickElement(DevToolsPreviewCamera camera, NVector2 mouse)
    {
        if (_modelDoc == null) return null;

        Vector3 rayOrigin = camera.Position;
        NVector2 offset = mouse - camera.Center;
        Vector3 rayDirection = camera.Forward
            + camera.Right * (offset.X / camera.FocalLength)
            - camera.Up * (offset.Y / camera.FocalLength);
        if (rayDirection.LengthSquared < 0.000001f) return null;
        rayDirection = Vector3.Normalize(rayDirection);

        ModelElementData? best = null;
        float bestDistance = float.MaxValue;
        int bestDepth = -1;
        void Visit(ModelElementData element, int depth)
        {
            if (!element.Visible) return;

            bool meshHit = ModelRayIntersectsMesh(element, rayOrigin, rayDirection, out float distance);
            Matrixf matrix = ModelComputeElementMatrix(element);
            Vector3[] corners = ModelTransformBoxCorners(matrix, element);
            if (meshHit || ModelRayIntersectsBox(rayOrigin, rayDirection, corners, out distance))
            {
                bool better = distance < bestDistance - 0.001f ||
                    (Math.Abs(distance - bestDistance) <= 0.001f && depth > bestDepth);
                if (better)
                {
                    best = element;
                    bestDistance = distance;
                    bestDepth = depth;
                }
            }

            foreach (ModelElementData child in element.Children)
            {
                Visit(child, depth + 1);
            }
        }

        foreach (ModelElementData root in _modelDoc.Roots)
        {
            Visit(root, 0);
        }

        return best;
    }

    private static bool ModelRayIntersectsBox(Vector3 origin, Vector3 direction, Vector3[] corners, out float distance)
    {
        distance = float.MaxValue;
        bool hit = false;
        foreach ((int a, int b, int c) in ModelBoxTriangles)
        {
            if (ModelRayIntersectsTriangle(origin, direction, corners[a], corners[b], corners[c], out float triangleDistance) &&
                triangleDistance < distance)
            {
                distance = triangleDistance;
                hit = true;
            }
        }
        return hit;
    }

    private static bool ModelRayIntersectsTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0f;
        const float epsilon = 0.0000001f;
        Vector3 edge1 = b - a;
        Vector3 edge2 = c - a;
        Vector3 h = Vector3.Cross(direction, edge2);
        float det = Vector3.Dot(edge1, h);
        if (det > -epsilon && det < epsilon) return false;

        float invDet = 1f / det;
        Vector3 s = origin - a;
        float u = invDet * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return false;

        Vector3 q = Vector3.Cross(s, edge1);
        float v = invDet * Vector3.Dot(direction, q);
        if (v < 0f || u + v > 1f) return false;

        distance = invDet * Vector3.Dot(edge2, q);
        return distance >= 0f;
    }

    private bool DrawModelGizmo(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ModelElementData element, bool hovered)
    {
        if (_modelGizmoTool == ModelGizmoTool.None || !element.Visible) return false;

        if (ModelMeshComponentsActive())
        {
            return DrawModelMeshComponentGizmo(drawList, camera, element, hovered);
        }

        return _modelGizmoTool switch
        {
            ModelGizmoTool.Move => DrawModelMoveGizmo(drawList, camera, element, hovered),
            ModelGizmoTool.Resize => DrawModelResizeGizmo(drawList, camera, element, hovered),
            ModelGizmoTool.Rotate => DrawModelRotateGizmo(drawList, camera, element, hovered),
            _ => false
        };
    }

    private bool DrawModelCutTool(ImDrawListPtr drawList, DevToolsPreviewCamera camera, bool hovered)
    {
        if (!hovered || _modelDoc == null) return false;
        if (!ModelTryPickCutPreview(camera, ImGui.GetMousePos(), out ModelCutPreview preview)) return false;

        uint plane = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.62f, 0.18f, 0.82f));
        uint line = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f));
        for (int index = 0; index < 4; index++)
        {
            DrawModelViewportLine(drawList, camera, preview.PlaneCorners[index], preview.PlaneCorners[(index + 1) & 3], plane, 1.8f);
        }
        DrawModelViewportLine(drawList, camera, preview.LineStart, preview.LineEnd, line, 3.1f);

        if (camera.Project((preview.LineStart + preview.LineEnd) * 0.5f, out NVector2 labelPosition, out _))
        {
            drawList.AddText(labelPosition + new NVector2(8f, -18f), line, $"Cut {ModelAxisName(preview.CutAxis)} {preview.CutCoordinate:0.###}");
        }

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            ModelCutElementAtCoordinate(preview.Element, preview.CutAxis, preview.CutCoordinate);
        }

        return true;
    }

    private bool DrawModelChiselTool(ImDrawListPtr drawList, DevToolsPreviewCamera camera, bool hovered)
    {
        if (!hovered || _modelDoc == null) return false;
        if (!ModelTryPickChiselPreview(camera, ImGui.GetMousePos(), out ModelChiselPreview preview)) return false;

        bool addBlocked = ModelChiselWouldOverlap(preview.Element, preview.AddFrom, preview.AddTo);
        uint addColor = ImGui.ColorConvertFloat4ToU32(addBlocked
            ? new NVector4(1f, 0.28f, 0.22f, 0.88f)
            : new NVector4(0.36f, 0.95f, 0.46f, 0.9f));
        uint removeColor = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.72f, 0.22f, 0.94f));
        DrawModelChiselBoxOutline(drawList, camera, preview.AddCorners, addColor, 2.4f);
        DrawModelChiselBoxOutline(drawList, camera, preview.RemoveCorners, removeColor, 1.7f);

        if (camera.Project(ModelChiselBoxCenter(preview.AddCorners), out NVector2 labelPosition, out _))
        {
            string label = addBlocked ? "occupied" : ModelResolveChiselTexture(preview.Element);
            drawList.AddText(labelPosition + new NVector2(8f, -18f), addColor, string.IsNullOrWhiteSpace(label) ? "chisel" : label);
        }

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            ModelAddChiselMicroblock(preview.Element, preview.AddFrom, preview.AddTo);
        }
        else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            ModelRemoveChiselMicroblock(preview.Element, preview.RemoveFrom, preview.RemoveTo);
        }

        return true;
    }

    private bool ModelTryPickCutPreview(DevToolsPreviewCamera camera, NVector2 mouse, out ModelCutPreview preview)
    {
        preview = default;
        if (_modelDoc == null) return false;

        Vector3 rayOrigin = camera.Position;
        if (!ModelViewportMouseRay(camera, mouse, out Vector3 rayDirection)) return false;

        ModelElementData? bestElement = null;
        double[] bestLocalUnits = [0, 0, 0];
        int bestFaceAxis = -1;
        bool bestFacePositive = false;
        float bestDistance = float.MaxValue;
        int bestDepth = -1;

        void Visit(ModelElementData element, int depth)
        {
            if (!element.Visible) return;

            if (ModelElementHasRenderableBox(element) &&
                ModelTryRayElementLocalHit(element, rayOrigin, rayDirection, out float distance, out double[] localUnits, out int faceAxis, out bool facePositive))
            {
                bool better = distance < bestDistance - 0.001f ||
                    (Math.Abs(distance - bestDistance) <= 0.001f && depth > bestDepth);
                if (better)
                {
                    bestElement = element;
                    bestLocalUnits = localUnits;
                    bestFaceAxis = faceAxis;
                    bestFacePositive = facePositive;
                    bestDistance = distance;
                    bestDepth = depth;
                }
            }

            foreach (ModelElementData child in element.Children)
            {
                Visit(child, depth + 1);
            }
        }

        foreach (ModelElementData root in _modelDoc.Roots)
        {
            Visit(root, 0);
        }

        return bestElement != null &&
            ModelTryBuildCutPreview(camera, bestElement, bestLocalUnits, bestFaceAxis, bestFacePositive, out preview);
    }

    private bool ModelTryPickChiselPreview(DevToolsPreviewCamera camera, NVector2 mouse, out ModelChiselPreview preview)
    {
        preview = default;
        if (_modelDoc == null) return false;

        Vector3 rayOrigin = camera.Position;
        if (!ModelViewportMouseRay(camera, mouse, out Vector3 rayDirection)) return false;

        ModelElementData? bestElement = null;
        double[] bestLocalUnits = [0, 0, 0];
        int bestFaceAxis = -1;
        bool bestFacePositive = false;
        float bestDistance = float.MaxValue;
        int bestDepth = -1;

        void Visit(ModelElementData element, int depth)
        {
            if (!element.Visible) return;

            if (ModelElementHasRenderableBox(element) &&
                ModelTryRayElementLocalHit(element, rayOrigin, rayDirection, out float distance, out double[] localUnits, out int faceAxis, out bool facePositive))
            {
                bool better = distance < bestDistance - 0.001f ||
                    (Math.Abs(distance - bestDistance) <= 0.001f && depth > bestDepth);
                if (better)
                {
                    bestElement = element;
                    bestLocalUnits = localUnits;
                    bestFaceAxis = faceAxis;
                    bestFacePositive = facePositive;
                    bestDistance = distance;
                    bestDepth = depth;
                }
            }

            foreach (ModelElementData child in element.Children)
            {
                Visit(child, depth + 1);
            }
        }

        foreach (ModelElementData root in _modelDoc.Roots)
        {
            Visit(root, 0);
        }

        return bestElement != null &&
            ModelTryBuildChiselPreview(bestElement, bestLocalUnits, bestFaceAxis, bestFacePositive, ModelCurrentChiselSize(), out preview);
    }

    private bool ModelTryBuildCutPreview(
        DevToolsPreviewCamera camera,
        ModelElementData element,
        double[] localUnits,
        int faceAxis,
        bool facePositive,
        out ModelCutPreview preview)
    {
        preview = default;
        if (faceAxis < 0 || faceAxis > 2) return false;

        double[] size =
        [
            Math.Max(0.0, element.SizeX),
            Math.Max(0.0, element.SizeY),
            Math.Max(0.0, element.SizeZ)
        ];
        int[] candidates = ModelCutCandidateAxes(faceAxis);
        if (candidates.Length == 0) return false;

        Matrixf matrix = ModelComputeElementMatrix(element);
        bool alternate = _modelCutOrientation == ModelCutOrientation.Auto && IsDevToolsShiftDown();
        bool found = false;
        float bestScore = float.MinValue;
        ModelCutPreview best = default;

        foreach (int cutAxis in candidates)
        {
            double cutLocal = Math.Clamp(localUnits[cutAxis], 0.0, size[cutAxis]);
            double cutCoordinate = Math.Round(element.From[cutAxis] + cutLocal, 6);
            if (!ModelIsCutCoordinateInside(element, cutAxis, cutCoordinate)) continue;

            int lineAxis = 3 - faceAxis - cutAxis;
            Vector3[] planeCorners = ModelCutPlaneWorldCorners(matrix, size, cutAxis, cutLocal);
            double[] lineStartUnits = [0, 0, 0];
            double[] lineEndUnits = [0, 0, 0];
            lineStartUnits[faceAxis] = facePositive ? size[faceAxis] : 0.0;
            lineEndUnits[faceAxis] = lineStartUnits[faceAxis];
            lineStartUnits[cutAxis] = cutLocal;
            lineEndUnits[cutAxis] = cutLocal;
            lineStartUnits[lineAxis] = 0.0;
            lineEndUnits[lineAxis] = size[lineAxis];
            Vector3 lineStart = ModelLocalUnitsPoint(matrix, lineStartUnits);
            Vector3 lineEnd = ModelLocalUnitsPoint(matrix, lineEndUnits);

            float score = (lineEnd - lineStart).LengthSquared;
            if (camera.Project(lineStart, out NVector2 screenA, out _) && camera.Project(lineEnd, out NVector2 screenB, out _))
            {
                score = (screenB - screenA).LengthSquared();
            }
            if (alternate) score = -score;

            if (!found || score > bestScore)
            {
                bestScore = score;
                best = new ModelCutPreview(element, faceAxis, facePositive, cutAxis, cutCoordinate, planeCorners, lineStart, lineEnd);
                found = true;
            }
        }

        preview = best;
        return found;
    }

    private static bool ModelTryBuildChiselPreview(
        ModelElementData element,
        double[] localUnits,
        int faceAxis,
        bool facePositive,
        double cellSize,
        out ModelChiselPreview preview)
    {
        preview = default;
        if (faceAxis < 0 || faceAxis > 2) return false;
        cellSize = Math.Clamp(cellSize, 0.0625, 8.0);

        double[] removeFrom = new double[3];
        double[] removeTo = new double[3];
        double[] addFrom = new double[3];
        double[] addTo = new double[3];
        for (int axis = 0; axis < 3; axis++)
        {
            double min = Math.Min(element.From[axis], element.To[axis]);
            double max = Math.Max(element.From[axis], element.To[axis]);
            double size = max - min;
            if (size <= 0.000001) return false;

            if (axis == faceAxis)
            {
                if (size <= cellSize)
                {
                    removeFrom[axis] = min;
                    removeTo[axis] = max;
                }
                else if (facePositive)
                {
                    removeFrom[axis] = max - cellSize;
                    removeTo[axis] = max;
                }
                else
                {
                    removeFrom[axis] = min;
                    removeTo[axis] = min + cellSize;
                }

                addFrom[axis] = facePositive ? max : min - cellSize;
                addTo[axis] = facePositive ? max + cellSize : min;
                continue;
            }

            double coordinate = element.From[axis] + Math.Clamp(localUnits[axis], 0.0, size);
            if (size <= cellSize)
            {
                removeFrom[axis] = min;
                removeTo[axis] = max;
            }
            else
            {
                double start = Math.Floor(coordinate / cellSize) * cellSize;
                if (coordinate >= max - 0.000001) start = max - cellSize;
                start = Math.Clamp(start, min, max - cellSize);
                removeFrom[axis] = start;
                removeTo[axis] = start + cellSize;
            }

            addFrom[axis] = removeFrom[axis];
            addTo[axis] = removeTo[axis];
        }

        removeFrom = ModelRoundVector(removeFrom);
        removeTo = ModelRoundVector(removeTo);
        addFrom = ModelRoundVector(addFrom);
        addTo = ModelRoundVector(addTo);
        preview = new ModelChiselPreview(
            element,
            faceAxis,
            facePositive,
            removeFrom,
            removeTo,
            addFrom,
            addTo,
            ModelChiselBoxWorldCorners(element, removeFrom, removeTo),
            ModelChiselBoxWorldCorners(element, addFrom, addTo));
        return true;
    }

    private double ModelCurrentChiselSize()
    {
        _modelChiselSize = Math.Clamp(_modelChiselSize, 0.0625f, 8f);
        return _modelChiselSize;
    }

    private int[] ModelCutCandidateAxes(int faceAxis)
    {
        if (_modelCutOrientation == ModelCutOrientation.Auto)
        {
            return faceAxis switch
            {
                0 => [1, 2],
                1 => [0, 2],
                2 => [0, 1],
                _ => []
            };
        }

        int axis = ModelCutOrientationAxis(_modelCutOrientation);
        return axis >= 0 && axis != faceAxis ? [axis] : [];
    }

    private static int ModelCutOrientationAxis(ModelCutOrientation orientation)
    {
        return orientation switch
        {
            ModelCutOrientation.X => 0,
            ModelCutOrientation.Y => 1,
            ModelCutOrientation.Z => 2,
            _ => -1
        };
    }

    private static bool ModelViewportMouseRay(DevToolsPreviewCamera camera, NVector2 mouse, out Vector3 rayDirection)
    {
        NVector2 offset = mouse - camera.Center;
        rayDirection = camera.Forward
            + camera.Right * (offset.X / camera.FocalLength)
            - camera.Up * (offset.Y / camera.FocalLength);
        if (rayDirection.LengthSquared < 0.000001f) return false;

        rayDirection = Vector3.Normalize(rayDirection);
        return true;
    }

    private static bool ModelTryRayElementLocalHit(
        ModelElementData element,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out float distance,
        out double[] localUnits,
        out int faceAxis,
        out bool facePositive)
    {
        distance = float.MaxValue;
        localUnits = [0, 0, 0];
        faceAxis = -1;
        facePositive = false;

        Matrixf matrix = ModelComputeElementMatrix(element);
        Vector3[] corners = ModelTransformBoxCorners(matrix, element);
        if (!ModelRayIntersectsBox(rayOrigin, rayDirection, corners, out distance)) return false;

        Vector3 hitWorld = rayOrigin + rayDirection * distance;
        try
        {
            Matrixd inverse = ModelMatrixd(matrix).Clone().Invert();
            Vec4d local = inverse.TransformVector(new Vec4d(hitWorld.X, hitWorld.Y, hitWorld.Z, 1.0));
            localUnits = [local.X * ModelUnitsPerBlock, local.Y * ModelUnitsPerBlock, local.Z * ModelUnitsPerBlock];
        }
        catch
        {
            return false;
        }

        double[] size = [element.SizeX, element.SizeY, element.SizeZ];
        double best = double.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            double toMin = Math.Abs(localUnits[axis]);
            if (toMin < best)
            {
                best = toMin;
                faceAxis = axis;
                facePositive = false;
            }

            double toMax = Math.Abs(size[axis] - localUnits[axis]);
            if (toMax < best)
            {
                best = toMax;
                faceAxis = axis;
                facePositive = true;
            }

            localUnits[axis] = Math.Clamp(localUnits[axis], 0.0, Math.Max(0.0, size[axis]));
        }

        return faceAxis >= 0;
    }

    private static Vector3[] ModelCutPlaneWorldCorners(Matrixf matrix, double[] size, int cutAxis, double cutLocal)
    {
        int axisA = cutAxis == 0 ? 1 : 0;
        int axisB = cutAxis == 2 ? 1 : 2;
        if (axisA == cutAxis) axisA = 2;
        if (axisB == cutAxis || axisB == axisA) axisB = Enumerable.Range(0, 3).First(axis => axis != cutAxis && axis != axisA);

        double[][] points =
        [
            [0, 0, 0],
            [0, 0, 0],
            [0, 0, 0],
            [0, 0, 0]
        ];
        foreach (double[] point in points)
        {
            point[cutAxis] = cutLocal;
        }

        points[0][axisA] = 0.0;
        points[0][axisB] = 0.0;
        points[1][axisA] = size[axisA];
        points[1][axisB] = 0.0;
        points[2][axisA] = size[axisA];
        points[2][axisB] = size[axisB];
        points[3][axisA] = 0.0;
        points[3][axisB] = size[axisB];

        return points.Select(point => ModelLocalUnitsPoint(matrix, point)).ToArray();
    }

    private static Vector3[] ModelChiselBoxWorldCorners(ModelElementData element, double[] from, double[] to)
    {
        Matrixf matrix = ModelComputeElementMatrix(element);
        double[] localFrom =
        [
            from[0] - element.From[0],
            from[1] - element.From[1],
            from[2] - element.From[2]
        ];
        double[] localTo =
        [
            to[0] - element.From[0],
            to[1] - element.From[1],
            to[2] - element.From[2]
        ];
        double x0 = localFrom[0];
        double y0 = localFrom[1];
        double z0 = localFrom[2];
        double x1 = localTo[0];
        double y1 = localTo[1];
        double z1 = localTo[2];
        double[][] points =
        [
            [x0, y0, z0],
            [x1, y0, z0],
            [x1, y1, z0],
            [x0, y1, z0],
            [x0, y0, z1],
            [x1, y0, z1],
            [x1, y1, z1],
            [x0, y1, z1]
        ];
        return points.Select(point => ModelLocalUnitsPoint(matrix, point)).ToArray();
    }

    private static void DrawModelChiselBoxOutline(ImDrawListPtr drawList, DevToolsPreviewCamera camera, Vector3[] corners, uint color, float thickness)
    {
        foreach ((int a, int b) in ModelBoxEdges)
        {
            DrawModelViewportLine(drawList, camera, corners[a], corners[b], color, thickness);
        }
    }

    private static Vector3 ModelChiselBoxCenter(Vector3[] corners)
    {
        Vector3 center = Vector3.Zero;
        foreach (Vector3 corner in corners)
        {
            center += corner;
        }
        return center / Math.Max(1, corners.Length);
    }

    private static Vector3 ModelLocalUnitsPoint(Matrixf matrix, double[] localUnits)
    {
        return ModelTransformPoint(matrix, new Vector3(
            (float)(localUnits[0] / ModelUnitsPerBlock),
            (float)(localUnits[1] / ModelUnitsPerBlock),
            (float)(localUnits[2] / ModelUnitsPerBlock)));
    }

    private List<ModelElementData> ModelGizmoTargets(ModelElementData fallback)
    {
        List<ModelElementData> roots = ModelEffectiveSelectedRoots()
            .Where(element => element.Visible)
            .ToList();
        if (roots.Count == 0)
        {
            roots.Add(fallback);
        }
        return roots;
    }

    private DevToolsPreviewBounds ModelElementsWorldBounds(IEnumerable<ModelElementData> elements)
    {
        DevToolsPreviewBounds bounds = DevToolsPreviewBounds.Empty;
        foreach (ModelElementData element in elements)
        {
            ModelIncludeElementWorldBounds(element, ref bounds);
        }

        return bounds;
    }

    private static void ModelIncludeElementWorldBounds(ModelElementData element, ref DevToolsPreviewBounds bounds)
    {
        Matrixf matrix = ModelComputeElementMatrix(element);
        if (!ModelIncludeMeshWorldBounds(element, ref bounds) && ModelElementHasRenderableBox(element))
        {
            foreach (Vector3 corner in ModelTransformBoxCorners(matrix, element))
            {
                bounds = bounds.Include(corner);
            }
        }

        foreach (ModelElementData child in element.Children)
        {
            ModelIncludeElementWorldBounds(child, ref bounds);
        }
    }

    private static bool ModelTryGetGroupLocalBounds(ModelElementData element, out DevToolsPreviewBounds bounds)
    {
        bounds = DevToolsPreviewBounds.Empty;
        if (element.Children.Count == 0 || ModelElementHasRenderableGeometry(element)) return false;

        Matrixf identity = new();
        identity.Identity();
        foreach (ModelElementData child in element.Children)
        {
            ModelIncludeElementLocalBounds(child, identity, ref bounds);
        }

        if (!bounds.IsValid) return false;
        Vector3 size = bounds.Max - bounds.Min;
        return size.LengthSquared > 0.000001f;
    }

    private static void ModelIncludeElementLocalBounds(ModelElementData element, Matrixf parentMatrix, ref DevToolsPreviewBounds bounds)
    {
        Matrixf matrix = new();
        matrix.Identity();
        matrix.Mul(parentMatrix.Values);
        matrix.Mul(ModelLocalElementMatrix(element).Values);

        if (!ModelIncludeMeshLocalBounds(element, matrix, ref bounds) && ModelElementHasRenderableBox(element))
        {
            foreach (Vector3 corner in ModelTransformBoxCorners(matrix, element))
            {
                bounds = bounds.Include(corner);
            }
        }

        foreach (ModelElementData child in element.Children)
        {
            ModelIncludeElementLocalBounds(child, matrix, ref bounds);
        }
    }

    private static bool ModelElementHasRenderableBox(ModelElementData element)
    {
        return element.SizeX > 0.0001 &&
            element.SizeY > 0.0001 &&
            element.SizeZ > 0.0001 &&
            element.Faces.Any(face => face != null);
    }

    private static bool ModelElementHasRenderableGeometry(ModelElementData element)
    {
        return element.NonCuboid?.Editable == true && element.NonCuboid.Faces.Count > 0 || ModelElementHasRenderableBox(element);
    }

    private static Vector3 ModelBoundsSize(DevToolsPreviewBounds bounds)
    {
        return bounds.IsValid ? bounds.Max - bounds.Min : Vector3.One;
    }

    private static Vector3[] ModelBoundsCorners(DevToolsPreviewBounds bounds)
    {
        Vector3 min = bounds.Min;
        Vector3 max = bounds.Max;
        return
        [
            new(min.X, min.Y, min.Z),
            new(max.X, min.Y, min.Z),
            new(max.X, max.Y, min.Z),
            new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z),
            new(max.X, min.Y, max.Z),
            new(max.X, max.Y, max.Z),
            new(min.X, max.Y, max.Z)
        ];
    }

    private static Vector3[] ModelTransformBoundsCorners(Matrixf matrix, DevToolsPreviewBounds bounds)
    {
        Vector3[] corners = ModelBoundsCorners(bounds);
        for (int index = 0; index < corners.Length; index++)
        {
            corners[index] = ModelTransformPoint(matrix, corners[index]);
        }

        return corners;
    }

    private (Vector3 AxisX, Vector3 AxisY, Vector3 AxisZ) ModelGizmoAxes(ModelElementData element)
    {
        Matrixf parentMatrix = ModelComputeParentChainMatrix(element);
        return (
            ModelTransformDirection(parentMatrix, Vector3.UnitX),
            ModelTransformDirection(parentMatrix, Vector3.UnitY),
            ModelTransformDirection(parentMatrix, Vector3.UnitZ));
    }

    private (Vector3 AxisX, Vector3 AxisY, Vector3 AxisZ) ModelElementAxes(ModelElementData element)
    {
        Matrixf matrix = ModelComputeElementMatrix(element);
        return (
            ModelTransformDirection(matrix, Vector3.UnitX),
            ModelTransformDirection(matrix, Vector3.UnitY),
            ModelTransformDirection(matrix, Vector3.UnitZ));
    }

    private static double[] ModelDefaultRotationOrigin(ModelElementData element)
    {
        if (ModelTryGetMeshBounds(element, out double[] min, out double[] max))
        {
            return [(min[0] + max[0]) * 0.5, (min[1] + max[1]) * 0.5, (min[2] + max[2]) * 0.5];
        }
        return
        [
            element.From[0] + element.SizeX * 0.5,
            element.From[1] + element.SizeY * 0.5,
            element.From[2] + element.SizeZ * 0.5
        ];
    }

    private static double[] ModelEffectiveRotationOrigin(ModelElementData element)
    {
        return element.RotationOrigin ?? ModelDefaultRotationOrigin(element);
    }

    private static void ModelEnsureRotationOrigin(ModelElementData element)
    {
        element.RotationOrigin ??= ModelDefaultRotationOrigin(element);
    }

    private static uint ModelGizmoAxisColor(int axis, bool highlighted)
    {
        if (highlighted) return ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f));
        return axis switch
        {
            0 => ImGui.ColorConvertFloat4ToU32(new NVector4(0.85f, 0.25f, 0.16f, 0.95f)),
            1 => ImGui.ColorConvertFloat4ToU32(new NVector4(0.32f, 0.9f, 0.34f, 0.95f)),
            _ => ImGui.ColorConvertFloat4ToU32(new NVector4(0.25f, 0.42f, 0.95f, 0.95f))
        };
    }

    private static uint ModelGizmoCornerColor(bool highlighted)
    {
        return highlighted
            ? ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f))
            : ImGui.ColorConvertFloat4ToU32(new NVector4(0.95f, 0.72f, 0.2f, 0.95f));
    }

    private float ModelSnapUnits(float value, bool bypass)
    {
        if (!_modelSnapEnabled || bypass || _modelSnapMoveUnits <= 0f) return value;
        return MathF.Round(value / _modelSnapMoveUnits) * _modelSnapMoveUnits;
    }

    private double ModelSnapDegrees(double value, bool bypass)
    {
        if (!_modelSnapEnabled || bypass || _modelSnapRotateDegrees <= 0f) return value;
        return Math.Round(value / _modelSnapRotateDegrees) * _modelSnapRotateDegrees;
    }

    private void ModelBeginGizmoDrag(
        ModelElementData element,
        int axis,
        int face,
        NVector2 axisScreenPerUnit,
        NVector2 centerScreen,
        double rotationSign,
        IEnumerable<ModelElementData>? dragElements = null,
        float startHandleDistanceUnits = 0f,
        int corner = -1,
        DevToolsPreviewBounds localBounds = default,
        Vector3 anchorUnits = default,
        NVector2[]? localAxisScreenPerUnit = null,
        bool selectionResize = false)
    {
        ModelBeginEdit();
        List<ModelElementData> targets = ModelGizmoTargets(element);
        _modelGizmoDragging = true;
        _modelGizmoDragAxis = axis;
        _modelGizmoDragFace = face;
        _modelGizmoDragStartMouse = ImGui.GetMousePos();
        _modelGizmoDragAxisScreenPerUnit = axisScreenPerUnit;
        _modelGizmoDragCenterScreen = centerScreen;
        _modelGizmoDragRotationSign = rotationSign;
        _modelGizmoDragStartFrom = (double[])element.From.Clone();
        _modelGizmoDragStartTo = (double[])element.To.Clone();
        _modelGizmoDragStartOrigin = (double[]?)element.RotationOrigin?.Clone();
        _modelGizmoDragStartRotX = element.RotationX;
        _modelGizmoDragStartRotY = element.RotationY;
        _modelGizmoDragStartRotZ = element.RotationZ;
        _modelGizmoDragUniformScale = startHandleDistanceUnits > 0f && face >= ModelResizeCornerHandleBase;
        _modelGizmoDragStartHandleDistanceUnits = startHandleDistanceUnits;
        _modelGizmoDragCorner = corner;
        _modelGizmoDragLocalBounds = localBounds;
        _modelGizmoDragAnchorUnits = anchorUnits;
        _modelGizmoDragSelectionResize = selectionResize;
        _modelGizmoDragGroupRotationElement = _modelGizmoTool == ModelGizmoTool.Rotate &&
            face < 0 &&
            axis >= 0 &&
            targets.Count == 1 &&
            ReferenceEquals(targets[0], element) &&
            ModelIsStableRotationGroup(element)
                ? element
                : null;
        _modelGizmoDragGroupRotationLayer = null;
        for (int axisIndex = 0; axisIndex < 3; axisIndex++)
        {
            _modelGizmoDragLocalAxisScreenPerUnit[axisIndex] = localAxisScreenPerUnit != null && axisIndex < localAxisScreenPerUnit.Length
                ? localAxisScreenPerUnit[axisIndex]
                : NVector2.Zero;
        }
        _modelGizmoDragElements.Clear();
        foreach (ModelElementData target in dragElements ?? targets)
        {
            _modelGizmoDragElements.Add(ModelCaptureGizmoDragState(target));
        }
        if (!ModelTryGetResizeBoundsUnits(_modelGizmoDragElements, out _modelGizmoDragSelectionBoundsUnits))
        {
            _modelGizmoDragSelectionBoundsUnits = default;
        }
    }

    private void ModelEndGizmoDrag(bool commit)
    {
        if (!_modelGizmoDragging) return;

        _modelGizmoDragging = false;
        _modelGizmoDragAxis = -1;
        _modelGizmoDragFace = -1;
        _modelGizmoDragUniformScale = false;
        _modelGizmoDragStartHandleDistanceUnits = 0f;
        _modelGizmoDragCorner = -1;
        _modelGizmoDragLocalBounds = default;
        _modelGizmoDragSelectionBoundsUnits = default;
        _modelGizmoDragSelectionResize = false;
        _modelGizmoDragAnchorUnits = default;
        _modelGizmoDragGroupRotationElement = null;
        _modelGizmoDragGroupRotationLayer = null;
        for (int axis = 0; axis < 3; axis++)
        {
            _modelGizmoDragLocalAxisScreenPerUnit[axis] = NVector2.Zero;
        }
        _modelGizmoDragElements.Clear();
        if (commit)
        {
            ModelEndEdit("Gizmo edit");
        }
        else
        {
            ModelCancelEdit();
        }
    }

    private float ModelGizmoDragUnits(bool bypassSnap)
    {
        NVector2 axisScreen = _modelGizmoDragAxisScreenPerUnit;
        float lengthSquared = axisScreen.X * axisScreen.X + axisScreen.Y * axisScreen.Y;
        if (lengthSquared < 0.000001f) return 0f;

        NVector2 mouseDelta = ImGui.GetMousePos() - _modelGizmoDragStartMouse;
        float units = (mouseDelta.X * axisScreen.X + mouseDelta.Y * axisScreen.Y) / lengthSquared;
        return ModelSnapUnits(units, bypassSnap);
    }

    private static ModelGizmoDragElementState ModelCaptureGizmoDragState(ModelElementData target)
    {
        return new ModelGizmoDragElementState(
            target,
            (double[])target.From.Clone(),
            (double[])target.To.Clone(),
            (double[]?)target.RotationOrigin?.Clone(),
            target.RotationX,
            target.RotationY,
            target.RotationZ,
            target.NonCuboid?.Editable == true ? target.NonCuboid.Vertices.Select(vertex => (double[])vertex.Clone()).ToArray() : null);
    }

    private static bool ModelIsStableRotationGroup(ModelElementData element)
    {
        return element.Children.Count > 0 && !ModelElementHasRenderableGeometry(element);
    }

    private static ModelElementData ModelGetOrCreateGroupRotationLayer(ModelElementData group, int axis)
    {
        if (group.Children.Count == 1 && ModelIsRotationLayerForAxis(group.Children[0], axis))
        {
            return group.Children[0];
        }

        ModelElementData layer = new()
        {
            Name = $"Rotate{ModelAxisName(axis)}",
            From = [0.0, 0.0, 0.0],
            To = [0.0, 0.0, 0.0],
            RotationOrigin = [0.0, 0.0, 0.0]
        };

        List<ModelElementData> oldChildren = [.. group.Children];
        group.Children.Clear();
        foreach (ModelElementData child in oldChildren)
        {
            child.Parent = layer;
            layer.Children.Add(child);
        }

        layer.Parent = group;
        group.Children.Add(layer);
        return layer;
    }

    private static bool ModelIsRotationLayerForAxis(ModelElementData element, int axis)
    {
        if (element.Children.Count == 0 || ModelElementHasRenderableGeometry(element)) return false;
        if (Math.Abs(element.SizeX) > 0.0001 || Math.Abs(element.SizeY) > 0.0001 || Math.Abs(element.SizeZ) > 0.0001) return false;
        if (!string.Equals(element.Name, $"Rotate{ModelAxisName(axis)}", StringComparison.OrdinalIgnoreCase)) return false;

        return axis switch
        {
            0 => Math.Abs(element.RotationY) < 0.0001 && Math.Abs(element.RotationZ) < 0.0001,
            1 => Math.Abs(element.RotationX) < 0.0001 && Math.Abs(element.RotationZ) < 0.0001,
            _ => Math.Abs(element.RotationX) < 0.0001 && Math.Abs(element.RotationY) < 0.0001
        };
    }

    private static IEnumerable<ModelElementData> ModelGroupResizeElements(ModelElementData element)
    {
        if (element.NonCuboid?.Editable == true) yield return element;
        foreach (ModelElementData child in element.Children)
        {
            foreach (ModelElementData descendant in child.EnumerateSubtree())
            {
                yield return descendant;
            }
        }
    }

    private static List<ModelElementData> ModelSelectionResizeElements(IEnumerable<ModelElementData> targets)
    {
        List<ModelElementData> elements = [];
        foreach (ModelElementData target in targets)
        {
            if (target.Children.Count > 0 && !ModelElementHasRenderableGeometry(target))
            {
                foreach (ModelElementData child in target.Children)
                {
                    elements.AddRange(child.EnumerateSubtree());
                }
            }
            else
            {
                elements.Add(target);
            }
        }

        return elements;
    }

    private static bool ModelTryGetResizeBoundsUnits(IEnumerable<ModelElementData> elements, out ModelResizeBoundsUnits bounds)
    {
        List<ModelElementData> candidates = elements.ToList();
        List<(double[] From, double[] To)> meshBounds = [];
        foreach (ModelElementData candidate in candidates)
        {
            if (ModelTryGetMeshBounds(candidate, out double[] min, out double[] max)) meshBounds.Add((min, max));
        }
        if (ModelTryGetResizeBoundsUnitsCore(meshBounds, out bounds)) return true;
        if (ModelTryGetResizeBoundsUnitsCore(candidates.Where(ModelElementHasRenderableBox).Select(element => (element.From, element.To)), out bounds))
        {
            return true;
        }

        return ModelTryGetResizeBoundsUnitsCore(candidates.Select(element => (element.From, element.To)), out bounds);
    }

    private static bool ModelTryGetResizeBoundsUnits(IEnumerable<ModelGizmoDragElementState> states, out ModelResizeBoundsUnits bounds)
    {
        List<ModelGizmoDragElementState> candidates = states.ToList();
        List<(double[] From, double[] To)> meshBounds = [];
        foreach (ModelGizmoDragElementState state in candidates)
        {
            if (state.MeshVertices is not { Length: > 0 }) continue;
            double[] min = [state.MeshVertices.Min(vertex => vertex[0]), state.MeshVertices.Min(vertex => vertex[1]), state.MeshVertices.Min(vertex => vertex[2])];
            double[] max = [state.MeshVertices.Max(vertex => vertex[0]), state.MeshVertices.Max(vertex => vertex[1]), state.MeshVertices.Max(vertex => vertex[2])];
            meshBounds.Add((min, max));
        }
        if (ModelTryGetResizeBoundsUnitsCore(meshBounds, out bounds)) return true;
        if (ModelTryGetResizeBoundsUnitsCore(candidates.Where(ModelStateHasRenderableBox).Select(state => (state.From, state.To)), out bounds))
        {
            return true;
        }

        return ModelTryGetResizeBoundsUnitsCore(candidates.Select(state => (state.From, state.To)), out bounds);
    }

    private static bool ModelStateHasRenderableBox(ModelGizmoDragElementState state)
    {
        return state.To[0] - state.From[0] > 0.0001 &&
            state.To[1] - state.From[1] > 0.0001 &&
            state.To[2] - state.From[2] > 0.0001 &&
            state.Element.Faces.Any(face => face != null);
    }

    private static bool ModelTryGetResizeBoundsUnitsCore(IEnumerable<(double[] From, double[] To)> boxes, out ModelResizeBoundsUnits bounds)
    {
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        double maxZ = double.NegativeInfinity;
        bool any = false;

        foreach ((double[] from, double[] to) in boxes)
        {
            minX = Math.Min(minX, Math.Min(from[0], to[0]));
            minY = Math.Min(minY, Math.Min(from[1], to[1]));
            minZ = Math.Min(minZ, Math.Min(from[2], to[2]));
            maxX = Math.Max(maxX, Math.Max(from[0], to[0]));
            maxY = Math.Max(maxY, Math.Max(from[1], to[1]));
            maxZ = Math.Max(maxZ, Math.Max(from[2], to[2]));
            any = true;
        }

        bounds = any
            ? new ModelResizeBoundsUnits(minX, minY, minZ, maxX, maxY, maxZ)
            : default;
        return any;
    }

    private static bool ModelApplySelectionAxisScale(
        IEnumerable<ModelGizmoDragElementState> states,
        ModelResizeBoundsUnits bounds,
        int axis,
        bool positiveFace,
        double units)
    {
        if (!ModelTryGetSelectionAxisScale(bounds, axis, positiveFace, units, out Vector3 anchorUnits, out Vector3 scale))
        {
            return false;
        }

        foreach (ModelGizmoDragElementState state in states)
        {
            ModelApplyAnchoredScale(state, anchorUnits, scale);
        }

        return true;
    }

    private static void ModelApplyIndependentFaceDelta(
        IEnumerable<ModelGizmoDragElementState> states,
        int axis,
        bool positiveFace,
        double units)
    {
        axis = Math.Clamp(axis, 0, 2);
        foreach (ModelGizmoDragElementState state in states)
        {
            if (state.MeshVertices is { Length: > 0 })
            {
                double min = state.MeshVertices.Min(vertex => vertex[axis]);
                double max = state.MeshVertices.Max(vertex => vertex[axis]);
                double anchor = positiveFace ? min : max;
                double dragged = positiveFace ? max : min;
                double span = dragged - anchor;
                if (Math.Abs(span) > 0.000001)
                {
                    Vector3 anchorVector = Vector3.Zero;
                    Vector3 scale = Vector3.One;
                    double factor = Math.Clamp((dragged + units - anchor) / span, 0.02, 64.0);
                    if (axis == 0) { anchorVector.X = (float)anchor; scale.X = (float)factor; }
                    else if (axis == 1) { anchorVector.Y = (float)anchor; scale.Y = (float)factor; }
                    else { anchorVector.Z = (float)anchor; scale.Z = (float)factor; }
                    ModelApplyAnchoredScale(state, anchorVector, scale);
                }
                continue;
            }
            if (positiveFace)
            {
                state.Element.To[axis] = Math.Max(state.From[axis], state.To[axis] + units);
            }
            else
            {
                state.Element.From[axis] = Math.Min(state.To[axis], state.From[axis] + units);
            }
        }
    }

    private static bool ModelTryGetSelectionAxisScale(
        ModelResizeBoundsUnits bounds,
        int axis,
        bool positiveFace,
        double units,
        out Vector3 anchorUnits,
        out Vector3 scale)
    {
        axis = Math.Clamp(axis, 0, 2);
        double anchor = positiveFace ? bounds.Min(axis) : bounds.Max(axis);
        double draggedStart = positiveFace ? bounds.Max(axis) : bounds.Min(axis);
        double oldDistance = draggedStart - anchor;
        if (Math.Abs(oldDistance) < 0.000001)
        {
            anchorUnits = Vector3.Zero;
            scale = Vector3.One;
            return false;
        }

        double factor = Math.Clamp((draggedStart + units - anchor) / oldDistance, 0.02, 64.0);
        anchorUnits = new Vector3((float)bounds.MinX, (float)bounds.MinY, (float)bounds.MinZ);
        scale = Vector3.One;
        switch (axis)
        {
            case 0:
                anchorUnits.X = (float)anchor;
                scale.X = (float)factor;
                break;
            case 1:
                anchorUnits.Y = (float)anchor;
                scale.Y = (float)factor;
                break;
            default:
                anchorUnits.Z = (float)anchor;
                scale.Z = (float)factor;
                break;
        }

        return true;
    }

    private static void ModelApplyUniformScale(ModelGizmoDragElementState state, double scale)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            state.Element.From[axis] = state.From[axis] * scale;
            state.Element.To[axis] = state.To[axis] * scale;
            if (state.Element.RotationOrigin != null && state.RotationOrigin != null)
            {
                state.Element.RotationOrigin[axis] = state.RotationOrigin[axis] * scale;
            }
        }
        if (state.MeshVertices != null && state.Element.NonCuboid?.Editable == true)
        {
            for (int index = 0; index < Math.Min(state.MeshVertices.Length, state.Element.NonCuboid.Vertices.Count); index++)
            {
                for (int axis = 0; axis < 3; axis++) state.Element.NonCuboid.Vertices[index][axis] = state.MeshVertices[index][axis] * scale;
            }
        }
    }

    private static Vector3 ModelCornerUnits(Vector3 cornerBlocks)
    {
        return cornerBlocks * ModelUnitsPerBlock;
    }

    private bool ModelTryGetCornerResizeScale(NVector2 mouseDelta, bool bypassSnap, out Vector3 scale)
    {
        scale = Vector3.One;
        if (_modelGizmoDragCorner < 0) return false;

        Vector3[] corners = ModelBoundsCorners(_modelGizmoDragLocalBounds);
        Vector3 cornerUnits = ModelCornerUnits(corners[_modelGizmoDragCorner]);
        if (_modelGizmoDragElements.Count == 1 && _modelGizmoDragElements[0].MeshVertices != null)
        {
            ModelElementData meshElement = _modelGizmoDragElements[0].Element;
            cornerUnits += new Vector3((float)meshElement.From[0], (float)meshElement.From[1], (float)meshElement.From[2]);
        }
        Vector3 deltaUnits = ModelSolveScreenDeltaToLocalUnits(mouseDelta, _modelGizmoDragLocalAxisScreenPerUnit);
        if (!bypassSnap && _modelSnapEnabled && _modelSnapMoveUnits > 0f)
        {
            deltaUnits.X = ModelSnapUnits(deltaUnits.X, bypass: false);
            deltaUnits.Y = ModelSnapUnits(deltaUnits.Y, bypass: false);
            deltaUnits.Z = ModelSnapUnits(deltaUnits.Z, bypass: false);
        }

        Vector3 newCornerUnits = cornerUnits + deltaUnits;
        Vector3 extents = ModelBoundsSize(_modelGizmoDragLocalBounds) * ModelUnitsPerBlock;
        bool[] activeAxes = ModelResizableCornerAxes(extents);
        float[] anchor = [_modelGizmoDragAnchorUnits.X, _modelGizmoDragAnchorUnits.Y, _modelGizmoDragAnchorUnits.Z];
        float[] oldCorner = [cornerUnits.X, cornerUnits.Y, cornerUnits.Z];
        float[] newCorner = [newCornerUnits.X, newCornerUnits.Y, newCornerUnits.Z];
        float[] scales = [1f, 1f, 1f];
        for (int axis = 0; axis < 3; axis++)
        {
            if (!activeAxes[axis]) continue;

            float oldDistance = oldCorner[axis] - anchor[axis];
            if (Math.Abs(oldDistance) < 0.001f) continue;

            float newDistance = newCorner[axis] - anchor[axis];
            scales[axis] = Math.Clamp(newDistance / oldDistance, 0.02f, 64f);
        }

        scale = new Vector3(scales[0], scales[1], scales[2]);
        return true;
    }

    private static bool[] ModelResizableCornerAxes(Vector3 extentsUnits)
    {
        float maxExtent = Math.Max(extentsUnits.X, Math.Max(extentsUnits.Y, extentsUnits.Z));
        float threshold = Math.Max(0.25f, maxExtent * 0.08f);
        return
        [
            extentsUnits.X >= threshold,
            extentsUnits.Y >= threshold,
            extentsUnits.Z >= threshold
        ];
    }

    private static Vector3 ModelSolveScreenDeltaToLocalUnits(NVector2 mouseDelta, IReadOnlyList<NVector2> axisScreenPerUnit)
    {
        double[,] normal = new double[3, 3];
        double[] rhs = new double[3];
        for (int axis = 0; axis < 3; axis++)
        {
            NVector2 a = axis < axisScreenPerUnit.Count ? axisScreenPerUnit[axis] : NVector2.Zero;
            rhs[axis] = a.X * mouseDelta.X + a.Y * mouseDelta.Y;
            for (int other = 0; other < 3; other++)
            {
                NVector2 b = other < axisScreenPerUnit.Count ? axisScreenPerUnit[other] : NVector2.Zero;
                normal[axis, other] = a.X * b.X + a.Y * b.Y;
            }
        }

        double trace = normal[0, 0] + normal[1, 1] + normal[2, 2];
        double damping = Math.Max(0.0001, trace * 0.001);
        for (int axis = 0; axis < 3; axis++)
        {
            normal[axis, axis] += damping;
        }

        return ModelSolveSymmetric3(normal, rhs, out Vector3 solved) ? solved : Vector3.Zero;
    }

    private static bool ModelSolveSymmetric3(double[,] matrix, double[] rhs, out Vector3 result)
    {
        double[,] a =
        {
            { matrix[0, 0], matrix[0, 1], matrix[0, 2], rhs[0] },
            { matrix[1, 0], matrix[1, 1], matrix[1, 2], rhs[1] },
            { matrix[2, 0], matrix[2, 1], matrix[2, 2], rhs[2] }
        };

        for (int pivot = 0; pivot < 3; pivot++)
        {
            int best = pivot;
            double bestAbs = Math.Abs(a[pivot, pivot]);
            for (int row = pivot + 1; row < 3; row++)
            {
                double candidate = Math.Abs(a[row, pivot]);
                if (candidate <= bestAbs) continue;
                best = row;
                bestAbs = candidate;
            }

            if (bestAbs < 0.000001)
            {
                result = Vector3.Zero;
                return false;
            }

            if (best != pivot)
            {
                for (int col = pivot; col < 4; col++)
                {
                    (a[pivot, col], a[best, col]) = (a[best, col], a[pivot, col]);
                }
            }

            double divisor = a[pivot, pivot];
            for (int col = pivot; col < 4; col++)
            {
                a[pivot, col] /= divisor;
            }

            for (int row = 0; row < 3; row++)
            {
                if (row == pivot) continue;
                double factor = a[row, pivot];
                for (int col = pivot; col < 4; col++)
                {
                    a[row, col] -= factor * a[pivot, col];
                }
            }
        }

        result = new Vector3((float)a[0, 3], (float)a[1, 3], (float)a[2, 3]);
        return true;
    }

    private static void ModelApplyAnchoredScale(ModelGizmoDragElementState state, Vector3 anchorUnits, Vector3 scale)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            double anchor = axis switch
            {
                0 => anchorUnits.X,
                1 => anchorUnits.Y,
                _ => anchorUnits.Z
            };
            double factor = axis switch
            {
                0 => scale.X,
                1 => scale.Y,
                _ => scale.Z
            };

            state.Element.From[axis] = anchor + (state.From[axis] - anchor) * factor;
            state.Element.To[axis] = anchor + (state.To[axis] - anchor) * factor;
            if (state.Element.From[axis] > state.Element.To[axis])
            {
                (state.Element.From[axis], state.Element.To[axis]) = (state.Element.To[axis], state.Element.From[axis]);
            }

            if (state.Element.RotationOrigin != null && state.RotationOrigin != null)
            {
                state.Element.RotationOrigin[axis] = anchor + (state.RotationOrigin[axis] - anchor) * factor;
            }
            if (state.MeshVertices != null && state.Element.NonCuboid?.Editable == true)
            {
                for (int index = 0; index < Math.Min(state.MeshVertices.Length, state.Element.NonCuboid.Vertices.Count); index++)
                {
                    state.Element.NonCuboid.Vertices[index][axis] = anchor + (state.MeshVertices[index][axis] - anchor) * factor;
                }
            }
        }
    }

    private bool DrawModelMoveGizmo(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ModelElementData element, bool hovered)
    {
        List<ModelElementData> targets = ModelGizmoTargets(element);
        DevToolsPreviewBounds groupBounds = ModelElementsWorldBounds(targets);
        Matrixf matrix = ModelComputeElementMatrix(element);
        Vector3 centerLocal = new(
            (float)Math.Max(0.0, element.SizeX) / (ModelUnitsPerBlock * 2f),
            (float)Math.Max(0.0, element.SizeY) / (ModelUnitsPerBlock * 2f),
            (float)Math.Max(0.0, element.SizeZ) / (ModelUnitsPerBlock * 2f));
        Vector3 center = groupBounds.IsValid ? groupBounds.Center : ModelTransformPoint(matrix, centerLocal);
        if (!camera.Project(center, out NVector2 centerScreen, out _)) return false;

        (Vector3 axisX, Vector3 axisY, Vector3 axisZ) = ModelGizmoAxes(element);
        Vector3[] axes = [axisX, axisY, axisZ];
        float axisLength = Math.Clamp(_modelViewportDistance * 0.22f, 0.12f, 1.4f);

        int hoveredAxis = -1;
        NVector2 mouse = ImGui.GetMousePos();
        NVector2[] axisEnds = new NVector2[3];
        bool[] axisVisible = new bool[3];
        for (int axis = 0; axis < 3; axis++)
        {
            axisVisible[axis] = camera.Project(center + axes[axis] * axisLength, out axisEnds[axis], out _);
        }

        if (hovered && !_modelGizmoDragging)
        {
            float best = ModelGizmoPickDistancePx;
            for (int axis = 0; axis < 3; axis++)
            {
                if (!axisVisible[axis]) continue;
                float distance = ModelDistancePointToSegment(mouse, centerScreen, axisEnds[axis]);
                if (distance < best)
                {
                    best = distance;
                    hoveredAxis = axis;
                }
            }
        }

        if (hoveredAxis >= 0)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                NVector2 perUnit = ModelProjectAxisScreenPerUnit(camera, center, axes[hoveredAxis]);
                ModelBeginGizmoDrag(element, hoveredAxis, -1, perUnit, centerScreen, 1.0);
            }
        }

        if (_modelGizmoDragging && _modelGizmoDragFace < 0 && _modelGizmoDragAxis >= 0 && _modelGizmoTool == ModelGizmoTool.Move)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                ModelEndGizmoDrag(commit: true);
            }
            else
            {
                bool bypassSnap = ImGui.IsKeyDown(ImGuiKey.LeftAlt) || ImGui.IsKeyDown(ImGuiKey.RightAlt);
                float units = ModelGizmoDragUnits(bypassSnap);
                int axis = _modelGizmoDragAxis;
                foreach (ModelGizmoDragElementState state in _modelGizmoDragElements)
                {
                    double size = state.To[axis] - state.From[axis];
                    state.Element.From[axis] = state.From[axis] + units;
                    state.Element.To[axis] = state.Element.From[axis] + size;
                    if (state.Element.RotationOrigin != null && state.RotationOrigin != null)
                    {
                        state.Element.RotationOrigin[axis] = state.RotationOrigin[axis] + units;
                    }
                    if (state.MeshVertices != null && state.Element.NonCuboid?.Editable == true)
                    {
                        for (int index = 0; index < Math.Min(state.MeshVertices.Length, state.Element.NonCuboid.Vertices.Count); index++)
                        {
                            state.Element.NonCuboid.Vertices[index][axis] = state.MeshVertices[index][axis] + units;
                        }
                    }
                }
                ModelMarkChanged();
                hoveredAxis = _modelGizmoDragAxis;
            }
        }

        for (int axis = 0; axis < 3; axis++)
        {
            if (!axisVisible[axis]) continue;
            uint color = ModelGizmoAxisColor(axis, axis == hoveredAxis);
            drawList.AddLine(centerScreen, axisEnds[axis], color, 2.6f);
            drawList.AddCircleFilled(axisEnds[axis], 5f, color, 12);
        }
        drawList.AddCircleFilled(centerScreen, 4.5f, ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f)), 16);

        return hoveredAxis >= 0 || (_modelGizmoDragging && _modelGizmoTool == ModelGizmoTool.Move);
    }

    private bool DrawModelResizeGizmo(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ModelElementData element, bool hovered)
    {
        List<ModelElementData> targets = ModelGizmoTargets(element);
        if (targets.Count == 1 && ReferenceEquals(targets[0], element) && ModelTryGetMeshLocalBounds(element, out DevToolsPreviewBounds meshLocalBounds))
        {
            return DrawModelGroupCornerResizeGizmo(drawList, camera, element, meshLocalBounds, hovered);
        }
        if (targets.Count == 1 && ReferenceEquals(targets[0], element) && ModelTryGetGroupLocalBounds(element, out DevToolsPreviewBounds localBounds))
        {
            return DrawModelGroupCornerResizeGizmo(drawList, camera, element, localBounds, hovered);
        }

        bool selectionResize = targets.Count > 1;
        List<ModelElementData> selectionResizeElements = selectionResize ? ModelSelectionResizeElements(targets) : [];
        ModelResizeBoundsUnits selectionBoundsUnits = default;
        bool hasSelectionResizeBounds = selectionResize && ModelTryGetResizeBoundsUnits(selectionResizeElements, out selectionBoundsUnits);
        DevToolsPreviewBounds selectionLocalBounds = hasSelectionResizeBounds ? selectionBoundsUnits.ToBlockBounds() : default;
        Matrixf matrix = ModelComputeElementMatrix(element);
        Matrixf parentMatrix = ModelComputeParentChainMatrix(element);
        float halfX = (float)Math.Max(0.0, element.SizeX) / (ModelUnitsPerBlock * 2f);
        float halfY = (float)Math.Max(0.0, element.SizeY) / (ModelUnitsPerBlock * 2f);
        float halfZ = (float)Math.Max(0.0, element.SizeZ) / (ModelUnitsPerBlock * 2f);
        Vector3 centerLocal = new(halfX, halfY, halfZ);
        DevToolsPreviewBounds groupBounds = hasSelectionResizeBounds
            ? DevToolsPreviewBounds.Empty
            : ModelElementsWorldBounds(targets);
        Vector3[]? selectionCornerWorlds = null;
        if (hasSelectionResizeBounds)
        {
            selectionCornerWorlds = ModelTransformBoundsCorners(parentMatrix, selectionLocalBounds);
            foreach (Vector3 corner in selectionCornerWorlds)
            {
                groupBounds = groupBounds.Include(corner);
            }
        }

        // Handle order: -X, +X, -Y, +Y, -Z, +Z.
        Vector3[] handleLocals =
        [
            new Vector3(0f, halfY, halfZ),
            new Vector3(halfX * 2f, halfY, halfZ),
            new Vector3(halfX, 0f, halfZ),
            new Vector3(halfX, halfY * 2f, halfZ),
            new Vector3(halfX, halfY, 0f),
            new Vector3(halfX, halfY, halfZ * 2f)
        ];

        (Vector3 axisX, Vector3 axisY, Vector3 axisZ) = ModelGizmoAxes(element);
        Vector3[] axes = [axisX, axisY, axisZ];
        Vector3 center = groupBounds.IsValid ? groupBounds.Center : ModelTransformPoint(matrix, centerLocal);
        Vector3 groupSize = ModelBoundsSize(groupBounds);
        float groupHalfX = Math.Max(groupSize.X * 0.5f, halfX);
        float groupHalfY = Math.Max(groupSize.Y * 0.5f, halfY);
        float groupHalfZ = Math.Max(groupSize.Z * 0.5f, halfZ);
        if (hasSelectionResizeBounds)
        {
            Vector3 min = selectionLocalBounds.Min;
            Vector3 max = selectionLocalBounds.Max;
            centerLocal = selectionLocalBounds.Center;
            center = ModelTransformPoint(parentMatrix, centerLocal);
            handleLocals =
            [
                new Vector3(min.X, centerLocal.Y, centerLocal.Z),
                new Vector3(max.X, centerLocal.Y, centerLocal.Z),
                new Vector3(centerLocal.X, min.Y, centerLocal.Z),
                new Vector3(centerLocal.X, max.Y, centerLocal.Z),
                new Vector3(centerLocal.X, centerLocal.Y, min.Z),
                new Vector3(centerLocal.X, centerLocal.Y, max.Z)
            ];
        }
        else if (targets.Count > 1)
        {
            handleLocals =
            [
                centerLocal + new Vector3(-groupHalfX, 0f, 0f),
                centerLocal + new Vector3(groupHalfX, 0f, 0f),
                centerLocal + new Vector3(0f, -groupHalfY, 0f),
                centerLocal + new Vector3(0f, groupHalfY, 0f),
                centerLocal + new Vector3(0f, 0f, -groupHalfZ),
                centerLocal + new Vector3(0f, 0f, groupHalfZ)
            ];
        }

        NVector2 mouse = ImGui.GetMousePos();
        int hoveredHandle = -1;
        NVector2[] handleScreens = new NVector2[6];
        bool[] handleVisible = new bool[6];
        for (int handle = 0; handle < 6; handle++)
        {
            Vector3 handleWorld = targets.Count > 1
                ? hasSelectionResizeBounds
                    ? ModelTransformPoint(parentMatrix, handleLocals[handle])
                    : center + (handleLocals[handle] - centerLocal)
                : ModelTransformPoint(matrix, handleLocals[handle]);
            handleVisible[handle] = camera.Project(handleWorld, out handleScreens[handle], out _);
        }

        if (hovered && !_modelGizmoDragging)
        {
            float best = ModelGizmoPickDistancePx;
            for (int handle = 0; handle < 6; handle++)
            {
                if (!handleVisible[handle]) continue;
                float distance = NVector2.Distance(mouse, handleScreens[handle]);
                if (distance < best)
                {
                    best = distance;
                    hoveredHandle = handle;
                }
            }
        }

        if (hoveredHandle >= 0)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                int axis = hoveredHandle / 2;
                NVector2 perUnit = ModelProjectAxisScreenPerUnit(camera, center, axes[axis]);
                ModelBeginGizmoDrag(
                    element,
                    axis,
                    hoveredHandle,
                    perUnit,
                    handleScreens[hoveredHandle],
                    1.0,
                    hasSelectionResizeBounds ? selectionResizeElements : null,
                    selectionResize: hasSelectionResizeBounds);
            }
        }

        if (_modelGizmoDragging &&
            _modelGizmoDragFace >= 0 &&
            _modelGizmoDragFace < ModelResizeCornerHandleBase &&
            _modelGizmoTool == ModelGizmoTool.Resize)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                ModelEndGizmoDrag(commit: true);
            }
            else
            {
                bool bypassSnap = ImGui.IsKeyDown(ImGuiKey.LeftAlt) || ImGui.IsKeyDown(ImGuiKey.RightAlt);
                float units = ModelGizmoDragUnits(bypassSnap);
                if (_modelGizmoDragUniformScale)
                {
                    double scale = Math.Max(0.02, (_modelGizmoDragStartHandleDistanceUnits + units) / Math.Max(0.001f, _modelGizmoDragStartHandleDistanceUnits));
                    foreach (ModelGizmoDragElementState state in _modelGizmoDragElements)
                    {
                        if (state.MeshVertices != null) ModelApplyAnchoredScale(state, _modelGizmoDragAnchorUnits, new Vector3((float)scale, (float)scale, (float)scale));
                        else ModelApplyUniformScale(state, scale);
                    }
                }
                else
                {
                    int axis = _modelGizmoDragAxis;
                    bool positiveFace = (_modelGizmoDragFace & 1) == 1;
                    if (_modelGizmoDragSelectionResize)
                    {
                        ModelApplySelectionAxisScale(_modelGizmoDragElements, _modelGizmoDragSelectionBoundsUnits, axis, positiveFace, units);
                    }
                    else
                    {
                        ModelApplyIndependentFaceDelta(_modelGizmoDragElements, axis, positiveFace, units);
                    }
                }
                ModelMarkChanged();
                hoveredHandle = _modelGizmoDragFace;
            }
        }

        int hoveredCorner = -1;
        NVector2[] cornerScreens = new NVector2[8];
        bool[] cornerVisible = new bool[8];
        if (hasSelectionResizeBounds && selectionCornerWorlds != null)
        {
            for (int corner = 0; corner < selectionCornerWorlds.Length; corner++)
            {
                cornerVisible[corner] = camera.Project(selectionCornerWorlds[corner], out cornerScreens[corner], out _);
            }

            if (hovered && !_modelGizmoDragging)
            {
                float best = ModelGizmoPickDistancePx;
                for (int corner = 0; corner < cornerScreens.Length; corner++)
                {
                    if (!cornerVisible[corner]) continue;
                    float distance = NVector2.Distance(mouse, cornerScreens[corner]);
                    if (distance < best)
                    {
                        best = distance;
                        hoveredCorner = corner;
                    }
                }
            }

            if (hoveredCorner >= 0)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && camera.Project(center, out NVector2 centerScreen, out _))
                {
                    Vector3[] localCorners = ModelBoundsCorners(selectionLocalBounds);
                    Vector3 diagonalLocal = localCorners[hoveredCorner] - selectionLocalBounds.Center;
                    if (diagonalLocal.LengthSquared > 0.000001f)
                    {
                        Vector3 axisWorld = ModelTransformDirection(parentMatrix, diagonalLocal);
                        float startDistanceUnits = diagonalLocal.Length * ModelUnitsPerBlock;
                        Vector3 anchorUnits = selectionBoundsUnits.CornerUnits(hoveredCorner ^ 6);
                        NVector2[] localAxisScreenPerUnit =
                        [
                            ModelProjectAxisScreenPerUnit(camera, center, ModelTransformDirection(parentMatrix, Vector3.UnitX)),
                            ModelProjectAxisScreenPerUnit(camera, center, ModelTransformDirection(parentMatrix, Vector3.UnitY)),
                            ModelProjectAxisScreenPerUnit(camera, center, ModelTransformDirection(parentMatrix, Vector3.UnitZ))
                        ];
                        NVector2 perUnit = ModelProjectAxisScreenPerUnit(camera, center, axisWorld);
                        ModelBeginGizmoDrag(
                            element,
                            -1,
                            ModelResizeCornerHandleBase + hoveredCorner,
                            perUnit,
                            centerScreen,
                            1.0,
                            selectionResizeElements,
                            startDistanceUnits,
                            hoveredCorner,
                            selectionLocalBounds,
                            anchorUnits,
                            localAxisScreenPerUnit,
                            selectionResize: true);
                    }
                }
            }
        }

        if (_modelGizmoDragging &&
            _modelGizmoDragSelectionResize &&
            _modelGizmoDragFace >= ModelResizeCornerHandleBase &&
            _modelGizmoTool == ModelGizmoTool.Resize)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                ModelEndGizmoDrag(commit: true);
            }
            else
            {
                bool bypassSnap = ImGui.IsKeyDown(ImGuiKey.LeftAlt) || ImGui.IsKeyDown(ImGuiKey.RightAlt);
                bool uniform = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
                if (uniform)
                {
                    float units = ModelGizmoDragUnits(bypassSnap);
                    double scaleValue = Math.Max(0.02, (_modelGizmoDragStartHandleDistanceUnits + units) / Math.Max(0.001f, _modelGizmoDragStartHandleDistanceUnits));
                    Vector3 scale = new((float)scaleValue, (float)scaleValue, (float)scaleValue);
                    foreach (ModelGizmoDragElementState state in _modelGizmoDragElements)
                    {
                        ModelApplyAnchoredScale(state, _modelGizmoDragAnchorUnits, scale);
                    }
                }
                else if (ModelTryGetCornerResizeScale(ImGui.GetMousePos() - _modelGizmoDragStartMouse, bypassSnap, out Vector3 scale))
                {
                    foreach (ModelGizmoDragElementState state in _modelGizmoDragElements)
                    {
                        ModelApplyAnchoredScale(state, _modelGizmoDragAnchorUnits, scale);
                    }
                }
                ModelMarkChanged();
                hoveredCorner = _modelGizmoDragFace - ModelResizeCornerHandleBase;
            }
        }

        for (int handle = 0; handle < 6; handle++)
        {
            if (!handleVisible[handle]) continue;
            uint color = ModelGizmoAxisColor(handle / 2, handle == hoveredHandle);
            NVector2 position = handleScreens[handle];
            drawList.AddRectFilled(position - new NVector2(4.5f, 4.5f), position + new NVector2(4.5f, 4.5f), color);
        }

        if (hasSelectionResizeBounds && selectionCornerWorlds != null)
        {
            uint wire = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.82f, 0.3f, 0.55f));
            for (int edge = 0; edge < ModelBoxEdges.Length; edge++)
            {
                (int a, int b) = ModelBoxEdges[edge];
                if (cornerVisible[a] && cornerVisible[b])
                {
                    drawList.AddLine(cornerScreens[a], cornerScreens[b], wire, 1.1f);
                }
            }

            for (int corner = 0; corner < cornerScreens.Length; corner++)
            {
                if (!cornerVisible[corner]) continue;
                uint color = ModelGizmoCornerColor(corner == hoveredCorner);
                NVector2 position = cornerScreens[corner];
                drawList.AddRectFilled(position - new NVector2(4.5f, 4.5f), position + new NVector2(4.5f, 4.5f), color);
            }
        }

        return hoveredHandle >= 0 || hoveredCorner >= 0 || (_modelGizmoDragging && _modelGizmoTool == ModelGizmoTool.Resize);
    }

    private bool DrawModelGroupCornerResizeGizmo(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ModelElementData element, DevToolsPreviewBounds localBounds, bool hovered)
    {
        Matrixf matrix = ModelComputeElementMatrix(element);
        Vector3 centerLocal = localBounds.Center;
        Vector3 center = ModelTransformPoint(matrix, centerLocal);
        if (!camera.Project(center, out NVector2 centerScreen, out _)) return false;

        Vector3[] corners = ModelBoundsCorners(localBounds);
        NVector2[] cornerScreens = new NVector2[corners.Length];
        bool[] cornerVisible = new bool[corners.Length];
        for (int corner = 0; corner < corners.Length; corner++)
        {
            Vector3 cornerWorld = ModelTransformPoint(matrix, corners[corner]);
            cornerVisible[corner] = camera.Project(cornerWorld, out cornerScreens[corner], out _);
        }

        int hoveredCorner = -1;
        NVector2 mouse = ImGui.GetMousePos();
        if (hovered && !_modelGizmoDragging)
        {
            float best = ModelGizmoPickDistancePx;
            for (int corner = 0; corner < corners.Length; corner++)
            {
                if (!cornerVisible[corner]) continue;
                float distance = NVector2.Distance(mouse, cornerScreens[corner]);
                if (distance < best)
                {
                    best = distance;
                    hoveredCorner = corner;
                }
            }
        }

        if (hoveredCorner >= 0)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                Vector3 diagonalLocal = corners[hoveredCorner] - centerLocal;
                if (diagonalLocal.LengthSquared > 0.000001f)
                {
                    Vector3 axisWorld = ModelTransformDirection(matrix, diagonalLocal);
                    float startDistanceUnits = diagonalLocal.Length * ModelUnitsPerBlock;
                    Vector3 anchorUnits = ModelCornerUnits(corners[hoveredCorner ^ 6]);
                    if (element.NonCuboid?.Editable == true)
                    {
                        anchorUnits += new Vector3((float)element.From[0], (float)element.From[1], (float)element.From[2]);
                    }
                    NVector2[] localAxisScreenPerUnit =
                    [
                        ModelProjectAxisScreenPerUnit(camera, center, ModelTransformDirection(matrix, Vector3.UnitX)),
                        ModelProjectAxisScreenPerUnit(camera, center, ModelTransformDirection(matrix, Vector3.UnitY)),
                        ModelProjectAxisScreenPerUnit(camera, center, ModelTransformDirection(matrix, Vector3.UnitZ))
                    ];
                    NVector2 perUnit = ModelProjectAxisScreenPerUnit(camera, center, axisWorld);
                    ModelBeginGizmoDrag(
                        element,
                        -1,
                        ModelResizeCornerHandleBase + hoveredCorner,
                        perUnit,
                        centerScreen,
                        1.0,
                        ModelGroupResizeElements(element),
                        startDistanceUnits,
                        hoveredCorner,
                        localBounds,
                        anchorUnits,
                        localAxisScreenPerUnit);
                }
            }
        }

        if (_modelGizmoDragging && _modelGizmoDragUniformScale && _modelGizmoTool == ModelGizmoTool.Resize)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                ModelEndGizmoDrag(commit: true);
            }
            else
            {
                bool bypassSnap = ImGui.IsKeyDown(ImGuiKey.LeftAlt) || ImGui.IsKeyDown(ImGuiKey.RightAlt);
                bool uniform = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
                if (uniform)
                {
                    float units = ModelGizmoDragUnits(bypassSnap);
                    double scale = Math.Max(0.02, (_modelGizmoDragStartHandleDistanceUnits + units) / Math.Max(0.001f, _modelGizmoDragStartHandleDistanceUnits));
                    foreach (ModelGizmoDragElementState state in _modelGizmoDragElements)
                    {
                        if (state.MeshVertices != null) ModelApplyAnchoredScale(state, _modelGizmoDragAnchorUnits, new Vector3((float)scale, (float)scale, (float)scale));
                        else ModelApplyUniformScale(state, scale);
                    }
                }
                else if (ModelTryGetCornerResizeScale(ImGui.GetMousePos() - _modelGizmoDragStartMouse, bypassSnap, out Vector3 scale))
                {
                    foreach (ModelGizmoDragElementState state in _modelGizmoDragElements)
                    {
                        ModelApplyAnchoredScale(state, _modelGizmoDragAnchorUnits, scale);
                    }
                }
                ModelMarkChanged();
                hoveredCorner = _modelGizmoDragFace - ModelResizeCornerHandleBase;
            }
        }

        uint wire = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.82f, 0.3f, 0.72f));
        for (int edge = 0; edge < ModelBoxEdges.Length; edge++)
        {
            (int a, int b) = ModelBoxEdges[edge];
            if (cornerVisible[a] && cornerVisible[b])
            {
                drawList.AddLine(cornerScreens[a], cornerScreens[b], wire, 1.3f);
            }
        }

        for (int corner = 0; corner < corners.Length; corner++)
        {
            if (!cornerVisible[corner]) continue;
            uint color = ModelGizmoCornerColor(corner == hoveredCorner);
            NVector2 position = cornerScreens[corner];
            drawList.AddRectFilled(position - new NVector2(5f, 5f), position + new NVector2(5f, 5f), color);
        }

        return hoveredCorner >= 0 || (_modelGizmoDragging && _modelGizmoDragUniformScale && _modelGizmoTool == ModelGizmoTool.Resize);
    }

    private bool DrawModelRotateGizmo(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ModelElementData element, bool hovered)
    {
        List<ModelElementData> targets = ModelGizmoTargets(element);
        DevToolsPreviewBounds groupBounds = ModelElementsWorldBounds(targets);
        Matrixf parentMatrix = ModelComputeParentChainMatrix(element);
        double[] effectiveOrigin = ModelEffectiveRotationOrigin(element);
        Vector3 originLocal = new(
            (float)effectiveOrigin[0] / ModelUnitsPerBlock,
            (float)effectiveOrigin[1] / ModelUnitsPerBlock,
            (float)effectiveOrigin[2] / ModelUnitsPerBlock);
        Vector3 center = targets.Count > 1 && groupBounds.IsValid ? groupBounds.Center : ModelTransformPoint(parentMatrix, originLocal);
        if (!camera.Project(center, out NVector2 centerScreen, out _)) return false;

        (Vector3 axisX, Vector3 axisY, Vector3 axisZ) = ModelElementAxes(element);
        Vector3[] axes = [axisX, axisY, axisZ];
        float maxSize = groupBounds.IsValid
            ? Math.Max(Math.Max(groupBounds.Max.X - groupBounds.Min.X, groupBounds.Max.Y - groupBounds.Min.Y), groupBounds.Max.Z - groupBounds.Min.Z) * ModelUnitsPerBlock
            : (float)Math.Max(Math.Max(element.SizeX, element.SizeY), Math.Max(element.SizeZ, 4.0));
        float radius = Math.Clamp(maxSize / (ModelUnitsPerBlock * 2f) + 0.08f, 0.14f, 2.4f);

        NVector2 mouse = ImGui.GetMousePos();
        int hoveredAxis = -1;
        if (hovered && !_modelGizmoDragging)
        {
            float best = ModelGizmoPickDistancePx + 2f;
            for (int axis = 0; axis < 3; axis++)
            {
                float distance = ModelDistanceToRing(camera, center, axes[axis], radius, mouse);
                if (distance < best)
                {
                    best = distance;
                    hoveredAxis = axis;
                }
            }
        }

        if (hoveredAxis >= 0)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                double sign = ModelComputeRingScreenSign(camera, center, axes[hoveredAxis], radius);
                ModelBeginGizmoDrag(element, hoveredAxis, -1, NVector2.Zero, centerScreen, sign);
            }
        }

        if (_modelGizmoDragging && _modelGizmoTool == ModelGizmoTool.Rotate && _modelGizmoDragAxis >= 0)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                ModelEndGizmoDrag(commit: true);
            }
            else
            {
                bool bypassSnap = ImGui.IsKeyDown(ImGuiKey.LeftAlt) || ImGui.IsKeyDown(ImGuiKey.RightAlt);
                double startAngle = Math.Atan2(_modelGizmoDragStartMouse.Y - _modelGizmoDragCenterScreen.Y, _modelGizmoDragStartMouse.X - _modelGizmoDragCenterScreen.X);
                double currentAngle = Math.Atan2(mouse.Y - _modelGizmoDragCenterScreen.Y, mouse.X - _modelGizmoDragCenterScreen.X);
                double deltaDegrees = GameMath.RAD2DEG * ModelWrapAngleRadians(currentAngle - startAngle) * _modelGizmoDragRotationSign;
                deltaDegrees = ModelSnapDegrees(deltaDegrees, bypassSnap);
                if (Math.Abs(deltaDegrees) > 0.0001)
                {
                    if (_modelGizmoDragGroupRotationElement != null)
                    {
                        ModelElementData layer = ModelGetOrCreateGroupRotationLayer(_modelGizmoDragGroupRotationElement, _modelGizmoDragAxis);
                        if (!ReferenceEquals(_modelGizmoDragGroupRotationLayer, layer))
                        {
                            _modelGizmoDragGroupRotationLayer = layer;
                            _modelGizmoDragElements.Clear();
                            _modelGizmoDragElements.Add(ModelCaptureGizmoDragState(layer));
                        }
                    }

                    foreach (ModelGizmoDragElementState state in _modelGizmoDragElements)
                    {
                        ModelEnsureRotationOrigin(state.Element);
                        ModelApplyEulerFieldRotationDelta(state, _modelGizmoDragAxis, deltaDegrees);
                    }
                    ModelMarkChanged();
                }
                hoveredAxis = _modelGizmoDragAxis;
            }
        }

        for (int axis = 0; axis < 3; axis++)
        {
            DrawModelRing(drawList, camera, center, axes[axis], radius, ModelGizmoAxisColor(axis, axis == hoveredAxis));
        }
        drawList.AddCircleFilled(centerScreen, 4f, ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f)), 16);

        return hoveredAxis >= 0 || (_modelGizmoDragging && _modelGizmoTool == ModelGizmoTool.Rotate);
    }

    private static void ModelRingBasis(Vector3 axis, out Vector3 basisA, out Vector3 basisB)
    {
        Vector3 reference = Math.Abs(axis.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        basisA = Vector3.Cross(axis, reference);
        if (basisA.LengthSquared < 0.000001f) basisA = Vector3.UnitX;
        basisA = Vector3.Normalize(basisA);
        basisB = Vector3.Normalize(Vector3.Cross(axis, basisA));
    }

    private static void ModelApplyEulerFieldRotationDelta(ModelGizmoDragElementState state, int axis, double deltaDegrees)
    {
        // When only the dragged axis carries rotation, plain accumulation keeps the value
        // continuous past ±90° and avoids canonicalizing e.g. 135° into a three-axis triple.
        bool otherAxesZero = axis switch
        {
            0 => Math.Abs(state.RotationY) < 0.0001 && Math.Abs(state.RotationZ) < 0.0001,
            1 => Math.Abs(state.RotationX) < 0.0001 && Math.Abs(state.RotationZ) < 0.0001,
            _ => Math.Abs(state.RotationX) < 0.0001 && Math.Abs(state.RotationY) < 0.0001
        };
        if (otherAxesZero)
        {
            switch (axis)
            {
                case 0:
                    state.Element.RotationX = ModelWrapDegrees(state.RotationX + deltaDegrees);
                    break;
                case 1:
                    state.Element.RotationY = ModelWrapDegrees(state.RotationY + deltaDegrees);
                    break;
                default:
                    state.Element.RotationZ = ModelWrapDegrees(state.RotationZ + deltaDegrees);
                    break;
            }

            return;
        }

        // Rings are drawn on the element's rotated local axes, so rotate about that drawn axis:
        // with VS's R = Rx·Ry·Rz composition this is a right-multiplication, then a decompose
        // back into Euler angles. Editing one Euler field directly only matches the drawn axis
        // for Z; for X/Y it rotates about parent-space axes and collapses near Y = ±90°.
        (double x, double y, double z) = DevToolsRotationMath.RotateXyzEulerAboutLocalAxis(
            state.RotationX,
            state.RotationY,
            state.RotationZ,
            axis,
            deltaDegrees);
        state.Element.RotationX = ModelWrapDegrees(x);
        state.Element.RotationY = ModelWrapDegrees(y);
        state.Element.RotationZ = ModelWrapDegrees(z);
    }

    private static void DrawModelRing(ImDrawListPtr drawList, DevToolsPreviewCamera camera, Vector3 center, Vector3 axis, float radius, uint color)
    {
        const int segments = 56;
        ModelRingBasis(axis, out Vector3 basisA, out Vector3 basisB);
        bool hasPrevious = camera.Project(center + basisA * radius, out NVector2 previous, out _);
        for (int segment = 1; segment <= segments; segment++)
        {
            float angle = segment / (float)segments * MathF.PI * 2f;
            Vector3 point = center + basisA * (MathF.Cos(angle) * radius) + basisB * (MathF.Sin(angle) * radius);
            bool hasCurrent = camera.Project(point, out NVector2 current, out _);
            if (hasPrevious && hasCurrent)
            {
                drawList.AddLine(previous, current, color, 2.2f);
            }
            previous = current;
            hasPrevious = hasCurrent;
        }
    }

    private static float ModelDistanceToRing(DevToolsPreviewCamera camera, Vector3 center, Vector3 axis, float radius, NVector2 mouse)
    {
        const int segments = 56;
        ModelRingBasis(axis, out Vector3 basisA, out Vector3 basisB);
        if (!camera.Project(center + basisA * radius, out NVector2 previous, out _)) return float.MaxValue;

        float best = float.MaxValue;
        for (int segment = 1; segment <= segments; segment++)
        {
            float angle = segment / (float)segments * MathF.PI * 2f;
            Vector3 point = center + basisA * (MathF.Cos(angle) * radius) + basisB * (MathF.Sin(angle) * radius);
            if (!camera.Project(point, out NVector2 current, out _)) continue;
            best = Math.Min(best, ModelDistancePointToSegment(mouse, previous, current));
            previous = current;
        }
        return best;
    }

    private static double ModelComputeRingScreenSign(DevToolsPreviewCamera camera, Vector3 center, Vector3 axis, float radius)
    {
        ModelRingBasis(axis, out Vector3 basisA, out Vector3 basisB);
        const float epsilon = 0.12f;
        Vector3 start = center + basisA * radius;
        Vector3 rotated = center + (basisA * MathF.Cos(epsilon) + basisB * MathF.Sin(epsilon)) * radius;
        if (!camera.Project(center, out NVector2 centerScreen, out _) ||
            !camera.Project(start, out NVector2 startScreen, out _) ||
            !camera.Project(rotated, out NVector2 rotatedScreen, out _))
        {
            return 1.0;
        }

        double startAngle = Math.Atan2(startScreen.Y - centerScreen.Y, startScreen.X - centerScreen.X);
        double rotatedAngle = Math.Atan2(rotatedScreen.Y - centerScreen.Y, rotatedScreen.X - centerScreen.X);
        double screenDelta = ModelWrapAngleRadians(rotatedAngle - startAngle);

        // Positive rotation around (axis, basisA, basisB) moved the point by +epsilon in world space.
        // If the projection turned that into a negative screen-space angle, invert the mapping.
        return screenDelta >= 0 ? 1.0 : -1.0;
    }

    private static double ModelWrapAngleRadians(double radians)
    {
        while (radians > Math.PI) radians -= Math.PI * 2.0;
        while (radians < -Math.PI) radians += Math.PI * 2.0;
        return radians;
    }

    private static double ModelWrapDegrees(double degrees)
    {
        while (degrees > 360.0) degrees -= 360.0;
        while (degrees < -360.0) degrees += 360.0;
        return Math.Abs(degrees) < 0.0001 ? 0.0 : degrees;
    }

    private static NVector2 ModelProjectAxisScreenPerUnit(DevToolsPreviewCamera camera, Vector3 center, Vector3 axisWorld)
    {
        Vector3 unitOffset = axisWorld * (1f / ModelUnitsPerBlock);
        if (camera.Project(center, out NVector2 start, out _) &&
            camera.Project(center + unitOffset, out NVector2 end, out _))
        {
            return end - start;
        }

        return new NVector2(1f, 0f);
    }

    private static float ModelDistancePointToSegment(NVector2 point, NVector2 segmentStart, NVector2 segmentEnd)
    {
        NVector2 segment = segmentEnd - segmentStart;
        float lengthSquared = segment.X * segment.X + segment.Y * segment.Y;
        if (lengthSquared < 0.000001f) return NVector2.Distance(point, segmentStart);

        float t = Math.Clamp(((point.X - segmentStart.X) * segment.X + (point.Y - segmentStart.Y) * segment.Y) / lengthSquared, 0f, 1f);
        NVector2 projected = segmentStart + segment * t;
        return NVector2.Distance(point, projected);
    }
}
