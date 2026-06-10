using ImGuiNET;
using InGameDevTools.Utils;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
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

    private DevToolsPreview3DRenderer? _modelPreviewRenderer;
    private DevToolsPreviewMesh? _modelPreviewMesh;
    private string? _modelPreviewSkipReason;
    private float _modelViewportYaw = 0.7f;
    private float _modelViewportPitch = -0.45f;
    private float _modelViewportDistance = 2.4f;
    private Vector3 _modelViewportTarget = new(0.5f, 0.5f, 0.5f);
    private bool _modelCameraFitPending;
    private bool _modelViewportScreenshotQueued;

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
        if (ImGui.SmallButton("Screenshot##model-vp-screenshot"))
        {
            _modelViewportScreenshotQueued = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("RMB orbit, MMB/Shift+RMB pan, wheel zoom, LMB select/drag gizmo, Home focus");

        ModelRebuildPreviewMeshIfNeeded();
        DrawModelViewportSurface();
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
                _modelViewportDistance = Math.Clamp(_modelViewportDistance * MathF.Pow(0.88f, wheel), 0.2f, 80f);
            }
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0.035f, 0.036f, 0.032f, 1f));
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 1f));
        uint gridMinor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.28f, 0.27f, 0.22f, 0.4f));
        uint gridMajor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.45f, 0.42f, 0.33f, 0.8f));
        uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.86f, 0.82f, 0.72f, 1f));
        drawList.AddRectFilled(min, max, background, 4f);

        DevToolsPreviewCamera camera = BuildModelViewportCamera(min, max);
        List<DevToolsPreviewMeshInstance> instances = [];
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

            if (hovered && !gizmoConsumedMouse && !_modelGizmoDragging && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                ModelElementData? picked = ModelPickElement(camera, ImGui.GetMousePos());
                bool additive = ImGui.GetIO().KeyCtrl;
                if (picked != null || !additive)
                {
                    ModelSelectElement(picked, additive: additive);
                }
            }

            drawList.AddText(min + new NVector2(12f, 10f), text, _modelDoc.DisplayPath);
            if (selected != null)
            {
                drawList.AddText(min + new NVector2(12f, 28f), text,
                    $"{selected.Name}  from [{selected.From[0]:0.##}, {selected.From[1]:0.##}, {selected.From[2]:0.##}]  size [{selected.SizeX:0.##}, {selected.SizeY:0.##}, {selected.SizeZ:0.##}]  selected {selectedElements.Count}");
            }
        }
        finally
        {
            drawList.PopClipRect();
        }
        drawList.AddRect(min, max, border, 4f);
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
        _modelPreviewRenderer?.Dispose();
        _modelPreviewRenderer = null;
    }

    private void ModelRebuildPreviewMeshIfNeeded()
    {
        if (!_modelPreviewDirty) return;

        _modelPreviewDirty = false;
        _modelPreviewMesh?.Dispose();
        _modelPreviewMesh = null;
        _modelPreviewSkipReason = null;
        if (_modelDoc == null) return;

        try
        {
            string json = ModelSerializeDocument(_modelDoc, includeInvisible: false, indented: false);
            Shape? shape = JsonUtil.ToObject<Shape>(json, _modelDoc.Domain);
            if (shape?.Elements == null || shape.Elements.Length == 0)
            {
                _modelPreviewSkipReason = "No visible elements.";
                return;
            }

            try
            {
                shape.ResolveReferences(_api.Logger, "ingamedevtools-model-editor");
            }
            catch (Exception exception)
            {
                LoggerUtil.Verbose(_api, this, $"Model preview reference resolve failed: {exception.Message}");
            }

            ShapeTextureSource textureSource = new(_api, shape, "ingamedevtools-model-editor");
            TesselationMetaData meta = new()
            {
                TexSource = textureSource,
                WithJointIds = false,
                WithDamageEffect = false,
                TypeForLogging = "ingamedevtools-model-editor"
            };
            _api.Tesselator.TesselateShape(meta, shape, out MeshData mesh);
            if (mesh == null || mesh.VerticesCount <= 0)
            {
                _modelPreviewSkipReason = "Tesselation produced no geometry.";
                return;
            }

            _modelPreviewMesh = DevToolsPreviewMeshFactory.FromMesh(_api, _modelDoc.DisplayPath, mesh);
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
        if (!bounds.IsValid) return;

        _modelViewportTarget = bounds.Center;
        _modelViewportDistance = Math.Clamp(bounds.Radius * 2.6f, 0.4f, 70f);
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

    private void DrawModelViewportGrid(ImDrawListPtr drawList, DevToolsPreviewCamera camera, uint minorColor, uint majorColor)
    {
        const int subdivisions = 16;
        (int minX, int maxX, int minY, int maxY, int minZ, int maxZ) = ModelReferenceBlockRange(_modelPreviewMesh?.Bounds ?? DevToolsPreviewBounds.Empty);

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
        Matrixf matrix = ModelComputeElementMatrix(element);
        Vector3[] corners = ModelTransformBoxCorners(matrix, element);
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

            Matrixf matrix = ModelComputeElementMatrix(element);
            Vector3[] corners = ModelTransformBoxCorners(matrix, element);
            if (ModelRayIntersectsBox(rayOrigin, rayDirection, corners, out float distance))
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

        return _modelGizmoTool switch
        {
            ModelGizmoTool.Move => DrawModelMoveGizmo(drawList, camera, element, hovered),
            ModelGizmoTool.Resize => DrawModelResizeGizmo(drawList, camera, element, hovered),
            ModelGizmoTool.Rotate => DrawModelRotateGizmo(drawList, camera, element, hovered),
            _ => false
        };
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
            Matrixf matrix = ModelComputeElementMatrix(element);
            Vector3[] corners = ModelTransformBoxCorners(matrix, element);
            foreach (Vector3 corner in corners)
            {
                bounds = bounds.Include(corner);
            }
        }

        return bounds;
    }

    private static Vector3 ModelBoundsSize(DevToolsPreviewBounds bounds)
    {
        return bounds.IsValid ? bounds.Max - bounds.Min : Vector3.One;
    }

    private (Vector3 AxisX, Vector3 AxisY, Vector3 AxisZ) ModelGizmoAxes(ModelElementData element)
    {
        Matrixf parentMatrix = ModelComputeParentChainMatrix(element);
        return (
            ModelTransformDirection(parentMatrix, Vector3.UnitX),
            ModelTransformDirection(parentMatrix, Vector3.UnitY),
            ModelTransformDirection(parentMatrix, Vector3.UnitZ));
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

    private void ModelBeginGizmoDrag(ModelElementData element, int axis, int face, NVector2 axisScreenPerUnit, NVector2 centerScreen, double rotationSign)
    {
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
        _modelGizmoDragElements.Clear();
        foreach (ModelElementData target in targets)
        {
            _modelGizmoDragElements.Add(new ModelGizmoDragElementState(
                target,
                (double[])target.From.Clone(),
                (double[])target.To.Clone(),
                (double[]?)target.RotationOrigin?.Clone(),
                target.RotationX,
                target.RotationY,
                target.RotationZ));
        }
        ModelBeginEdit();
    }

    private void ModelEndGizmoDrag(bool commit)
    {
        if (!_modelGizmoDragging) return;

        _modelGizmoDragging = false;
        _modelGizmoDragAxis = -1;
        _modelGizmoDragFace = -1;
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
        DevToolsPreviewBounds groupBounds = ModelElementsWorldBounds(targets);
        Matrixf matrix = ModelComputeElementMatrix(element);
        float halfX = (float)Math.Max(0.0, element.SizeX) / (ModelUnitsPerBlock * 2f);
        float halfY = (float)Math.Max(0.0, element.SizeY) / (ModelUnitsPerBlock * 2f);
        float halfZ = (float)Math.Max(0.0, element.SizeZ) / (ModelUnitsPerBlock * 2f);
        Vector3 centerLocal = new(halfX, halfY, halfZ);

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
        if (targets.Count > 1)
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
                ? center + (handleLocals[handle] - centerLocal)
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
                ModelBeginGizmoDrag(element, axis, hoveredHandle, perUnit, handleScreens[hoveredHandle], 1.0);
            }
        }

        if (_modelGizmoDragging && _modelGizmoDragFace >= 0 && _modelGizmoTool == ModelGizmoTool.Resize)
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
                bool positiveFace = (_modelGizmoDragFace & 1) == 1;
                foreach (ModelGizmoDragElementState state in _modelGizmoDragElements)
                {
                    if (positiveFace)
                    {
                        state.Element.To[axis] = Math.Max(state.From[axis], state.To[axis] + units);
                    }
                    else
                    {
                        state.Element.From[axis] = Math.Min(state.To[axis], state.From[axis] + units);
                    }
                }
                ModelMarkChanged();
                hoveredHandle = _modelGizmoDragFace;
            }
        }

        for (int handle = 0; handle < 6; handle++)
        {
            if (!handleVisible[handle]) continue;
            uint color = ModelGizmoAxisColor(handle / 2, handle == hoveredHandle);
            NVector2 position = handleScreens[handle];
            drawList.AddRectFilled(position - new NVector2(4.5f, 4.5f), position + new NVector2(4.5f, 4.5f), color);
        }

        return hoveredHandle >= 0 || (_modelGizmoDragging && _modelGizmoTool == ModelGizmoTool.Resize);
    }

    private bool DrawModelRotateGizmo(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ModelElementData element, bool hovered)
    {
        List<ModelElementData> targets = ModelGizmoTargets(element);
        DevToolsPreviewBounds groupBounds = ModelElementsWorldBounds(targets);
        Matrixf parentMatrix = ModelComputeParentChainMatrix(element);
        Vector3 originLocal = element.RotationOrigin != null
            ? new Vector3(
                (float)element.RotationOrigin[0] / ModelUnitsPerBlock,
                (float)element.RotationOrigin[1] / ModelUnitsPerBlock,
                (float)element.RotationOrigin[2] / ModelUnitsPerBlock)
            : Vector3.Zero;
        Vector3 center = targets.Count > 1 && groupBounds.IsValid ? groupBounds.Center : ModelTransformPoint(parentMatrix, originLocal);
        if (!camera.Project(center, out NVector2 centerScreen, out _)) return false;

        (Vector3 axisX, Vector3 axisY, Vector3 axisZ) = ModelGizmoAxes(element);
        Vector3[] axes = [axisX, axisY, axisZ];
        float maxSize = targets.Count > 1 && groupBounds.IsValid
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
                foreach (ModelGizmoDragElementState state in _modelGizmoDragElements)
                {
                    switch (_modelGizmoDragAxis)
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
                }
                ModelMarkChanged();
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
