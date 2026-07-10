using ImGuiNET;
using InGameDevTools.Utils;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private bool _modelMeshComponentDragging;
    private int _modelMeshComponentDragAxis = -1;
    private ModelGizmoTool _modelMeshComponentDragTool;
    private NVector2 _modelMeshComponentDragStartMouse;
    private NVector2 _modelMeshComponentDragAxisScreenPerUnit;
    private NVector2 _modelMeshComponentDragCenterScreen;
    private double[] _modelMeshComponentDragCenter = [0d, 0d, 0d];
    private readonly Dictionary<int, double[]> _modelMeshComponentDragVertices = [];

    private readonly record struct ModelMeshTopologyGizmoRegion(
        HashSet<int> Faces,
        double[] CenterUnits,
        float RadiusUnits,
        Vector3 WorldDirection,
        NVector2 CenterScreen,
        NVector2 HandleScreen,
        NVector2 AxisScreenPerUnit);

    private bool _modelMeshTopologyDragging;
    private ModelGizmoTool _modelMeshTopologyDragTool;
    private ModelElementData? _modelMeshTopologyDragElement;
    private ModelMeshSelectionMode _modelMeshTopologyDragSelectionMode;
    private readonly HashSet<int> _modelMeshTopologyDragFaces = [];
    private NVector2 _modelMeshTopologyDragStartMouse;
    private NVector2 _modelMeshTopologyDragAxisScreenPerUnit;
    private float _modelMeshTopologyDragInsetRadiusUnits;
    private float _modelMeshTopologyDragValue;
    private int _modelMeshTopologyDragRegion = -1;

    private bool ModelAnyViewportGizmoDragging =>
        _modelGizmoDragging || _modelMeshComponentDragging || _modelMeshTopologyDragging;

    private void ModelSetGizmoTool(ModelGizmoTool tool)
    {
        if (_modelGizmoTool == tool) return;

        if (_modelGizmoDragging) ModelEndGizmoDrag(commit: true);
        if (_modelMeshComponentDragging) ModelEndMeshComponentDrag(commit: true);
        if (_modelMeshTopologyDragging) ModelEndMeshTopologyDrag(commit: false);
        _modelGizmoTool = tool;
    }

    private void ModelEndViewportGizmoInteractions(bool commitTransforms)
    {
        if (_modelGizmoDragging) ModelEndGizmoDrag(commitTransforms);
        if (_modelMeshComponentDragging) ModelEndMeshComponentDrag(commitTransforms);
        if (_modelMeshTopologyDragging) ModelEndMeshTopologyDrag(commit: false);
    }

    private void ModelEndMeshComponentDrag(bool commit)
    {
        if (!_modelMeshComponentDragging) return;

        _modelMeshComponentDragging = false;
        _modelMeshComponentDragAxis = -1;
        _modelMeshComponentDragTool = ModelGizmoTool.None;
        _modelMeshComponentDragVertices.Clear();
        if (commit) ModelEndEdit("Transform mesh components");
        else ModelCancelEdit();
    }

    private static Vector3 ModelMeshVertexLocalBlocks(ModelElementData element, double[] vertex)
    {
        return new Vector3(
            (float)((vertex.ElementAtOrDefault(0) - element.From[0]) / ModelUnitsPerBlock),
            (float)((vertex.ElementAtOrDefault(1) - element.From[1]) / ModelUnitsPerBlock),
            (float)((vertex.ElementAtOrDefault(2) - element.From[2]) / ModelUnitsPerBlock));
    }

    private static Vector3 ModelMeshVertexWorld(ModelElementData element, double[] vertex)
    {
        return ModelTransformPoint(ModelComputeElementMatrix(element), ModelMeshVertexLocalBlocks(element, vertex));
    }

    private static Vector3[] ModelMeshWorldVertices(ModelElementData element)
    {
        ModelNonCuboidData? mesh = element.NonCuboid;
        if (mesh?.Editable != true) return [];
        Matrixf matrix = ModelComputeElementMatrix(element);
        return mesh.Vertices
            .Select(vertex => ModelMeshTopologyPointWorld(
                matrix,
                element,
                vertex.ElementAtOrDefault(0),
                vertex.ElementAtOrDefault(1),
                vertex.ElementAtOrDefault(2)))
            .ToArray();
    }

    private static bool ModelIncludeMeshWorldBounds(ModelElementData element, ref DevToolsPreviewBounds bounds)
    {
        if (element.NonCuboid?.Editable != true) return false;
        bool any = false;
        foreach (Vector3 vertex in ModelMeshWorldVertices(element))
        {
            bounds = bounds.Include(vertex);
            any = true;
        }
        return any;
    }

    private static bool ModelIncludeMeshLocalBounds(ModelElementData element, Matrixf matrix, ref DevToolsPreviewBounds bounds)
    {
        if (element.NonCuboid?.Editable != true) return false;
        bool any = false;
        foreach (double[] vertex in element.NonCuboid.Vertices)
        {
            bounds = bounds.Include(ModelTransformPoint(matrix, ModelMeshVertexLocalBlocks(element, vertex)));
            any = true;
        }
        return any;
    }

    private static bool ModelTryGetMeshLocalBounds(ModelElementData element, out DevToolsPreviewBounds bounds)
    {
        bounds = DevToolsPreviewBounds.Empty;
        if (element.NonCuboid?.Editable != true) return false;
        foreach (double[] vertex in element.NonCuboid.Vertices) bounds = bounds.Include(ModelMeshVertexLocalBlocks(element, vertex));
        return bounds.IsValid;
    }

    private void DrawModelMeshWireOverlay(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ModelElementData element, uint color, float thickness, bool drawVertices)
    {
        ModelNonCuboidData? mesh = element.NonCuboid;
        if (mesh?.Editable != true) return;
        Vector3[] world = ModelMeshWorldVertices(element);
        foreach (ModelMeshEdge edge in ModelMeshEdges(mesh))
        {
            if (edge.A < 0 || edge.B < 0 || edge.A >= world.Length || edge.B >= world.Length) continue;
            DrawModelViewportLine(drawList, camera, world[edge.A], world[edge.B], color, thickness);
        }
        if (!drawVertices) return;
        for (int index = 0; index < world.Length; index++)
        {
            if (!camera.Project(world[index], out NVector2 screen, out _)) continue;
            drawList.AddCircleFilled(screen, _modelMeshSelectedVertices.Contains(index) ? 5f : 3f, color, 12);
        }
    }

    private void DrawModelMeshFallbackOverlays(ImDrawListPtr drawList, DevToolsPreviewCamera camera)
    {
        if (_modelDoc == null || !ModelDocumentContainsNonCuboid(_modelDoc) || ModelMeshLibIsOperational(out _)) return;
        uint color = ImGui.ColorConvertFloat4ToU32(new NVector4(0.36f, 0.92f, 0.88f, 0.72f));
        foreach (ModelElementData element in _modelDoc.EnumerateElements())
        {
            if (element.Visible && element.NonCuboid?.Editable == true) DrawModelMeshWireOverlay(drawList, camera, element, color, 1.1f, drawVertices: false);
        }
    }

    private bool DrawModelMeshSelectionOverlay(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ModelElementData element, bool active)
    {
        ModelNonCuboidData? mesh = element.NonCuboid;
        if (mesh?.Editable != true) return false;
        Vector3[] world = ModelMeshWorldVertices(element);
        uint baseColor = active
            ? ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.82f, 0.3f, 0.95f))
            : ImGui.ColorConvertFloat4ToU32(new NVector4(0.28f, 0.82f, 1f, 0.82f));
        uint selectedColor = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.64f, 1f));
        foreach (ModelMeshEdge edge in ModelMeshEdges(mesh))
        {
            if (edge.A < 0 || edge.B < 0 || edge.A >= world.Length || edge.B >= world.Length) continue;
            bool selected = active && _modelMeshSelectionMode == ModelMeshSelectionMode.Edge && _modelMeshSelectedEdges.Contains(edge);
            DrawModelViewportLine(drawList, camera, world[edge.A], world[edge.B], selected ? selectedColor : baseColor, selected ? 3f : 1.35f);
        }
        if (active && _modelMeshSelectionMode == ModelMeshSelectionMode.Face)
        {
            foreach (int faceIndex in _modelMeshSelectedFaces)
            {
                if (faceIndex < 0 || faceIndex >= mesh.Faces.Count) continue;
                ModelMeshFaceData face = mesh.Faces[faceIndex];
                NVector2[] screen = new NVector2[face.Vertices.Length];
                bool visible = true;
                for (int corner = 0; corner < face.Vertices.Length; corner++)
                {
                    int vertexIndex = face.Vertices[corner];
                    if (vertexIndex < 0 || vertexIndex >= world.Length || !camera.Project(world[vertexIndex], out screen[corner], out _))
                    {
                        visible = false;
                        break;
                    }
                }
                if (!visible) continue;
                uint fill = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.82f, 0.3f, 0.2f));
                if (screen.Length == 3) drawList.AddTriangleFilled(screen[0], screen[1], screen[2], fill);
                else if (screen.Length == 4) drawList.AddQuadFilled(screen[0], screen[1], screen[2], screen[3], fill);
            }
        }
        if (active)
        {
            for (int index = 0; index < world.Length; index++)
            {
                if (!camera.Project(world[index], out NVector2 screen, out _)) continue;
                bool selected = _modelMeshSelectionMode == ModelMeshSelectionMode.Vertex && _modelMeshSelectedVertices.Contains(index);
                drawList.AddCircleFilled(screen, selected ? 5.5f : 3f, selected ? selectedColor : baseColor, 12);
            }
        }
        return true;
    }

    private bool ModelHandleMeshViewportSelection(DevToolsPreviewCamera camera, NVector2 mouse, bool additive)
    {
        ModelElementData? element = _modelSelectedElement;
        ModelNonCuboidData? mesh = element?.NonCuboid;
        if (!ModelIsMeshLibMode || element == null || mesh?.Editable != true) return false;
        Vector3[] world = ModelMeshWorldVertices(element);
        if (_modelMeshSelectionMode == ModelMeshSelectionMode.Vertex)
        {
            int best = -1;
            float distance = 11f;
            float bestDepth = float.MaxValue;
            for (int index = 0; index < world.Length; index++)
            {
                if (!camera.Project(world[index], out NVector2 screen, out float depth)) continue;
                float candidate = NVector2.Distance(mouse, screen);
                if (candidate > distance || (Math.Abs(candidate - distance) < 0.1f && depth >= bestDepth)) continue;
                best = index;
                distance = candidate;
                bestDepth = depth;
            }
            if (best < 0) return false;
            ModelSetMeshVertexSelection(best, additive);
            return true;
        }
        if (_modelMeshSelectionMode == ModelMeshSelectionMode.Edge)
        {
            ModelMeshEdge? best = null;
            float distance = 10f;
            foreach (ModelMeshEdge edge in ModelMeshEdges(mesh))
            {
                if (edge.A < 0 || edge.B < 0 || edge.A >= world.Length || edge.B >= world.Length ||
                    !camera.Project(world[edge.A], out NVector2 a, out _) || !camera.Project(world[edge.B], out NVector2 b, out _)) continue;
                float candidate = ModelDistancePointToSegment(mouse, a, b);
                if (candidate >= distance) continue;
                distance = candidate;
                best = edge;
            }
            if (best == null) return false;
            ModelSetMeshEdgeSelection(best.Value, additive);
            return true;
        }

        Vector3 rayOrigin = camera.Position;
        NVector2 offset = mouse - camera.Center;
        Vector3 rayDirection = camera.Forward + camera.Right * (offset.X / camera.FocalLength) - camera.Up * (offset.Y / camera.FocalLength);
        if (rayDirection.LengthSquared < 0.000001f) return false;
        rayDirection = Vector3.Normalize(rayDirection);
        int bestFace = -1;
        float bestDistance = float.MaxValue;
        for (int faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
        {
            ModelMeshFaceData face = mesh.Faces[faceIndex];
            if (face.Vertices.Length is not (3 or 4) || face.Vertices.Any(index => index < 0 || index >= world.Length)) continue;
            int[][] triangles = face.Vertices.Length == 3
                ? [[face.Vertices[0], face.Vertices[1], face.Vertices[2]]]
                : [[face.Vertices[0], face.Vertices[1], face.Vertices[2]], [face.Vertices[0], face.Vertices[2], face.Vertices[3]]];
            foreach (int[] triangle in triangles)
            {
                if (ModelRayIntersectsTriangle(rayOrigin, rayDirection, world[triangle[0]], world[triangle[1]], world[triangle[2]], out float distance) && distance < bestDistance)
                {
                    bestDistance = distance;
                    bestFace = faceIndex;
                }
            }
        }
        if (bestFace < 0) return false;
        ModelSetMeshFaceSelection(bestFace, additive);
        return true;
    }

    private static bool ModelRayIntersectsMesh(ModelElementData element, Vector3 origin, Vector3 direction, out float distance)
    {
        distance = float.MaxValue;
        ModelNonCuboidData? mesh = element.NonCuboid;
        if (mesh?.Editable != true) return false;
        Vector3[] world = ModelMeshWorldVertices(element);
        bool hit = false;
        foreach (ModelMeshFaceData face in mesh.Faces)
        {
            if (face.Vertices.Length is not (3 or 4) || face.Vertices.Any(index => index < 0 || index >= world.Length)) continue;
            if (ModelRayIntersectsTriangle(origin, direction, world[face.Vertices[0]], world[face.Vertices[1]], world[face.Vertices[2]], out float first) && first < distance)
            {
                distance = first;
                hit = true;
            }
            if (face.Vertices.Length == 4 && ModelRayIntersectsTriangle(origin, direction, world[face.Vertices[0]], world[face.Vertices[2]], world[face.Vertices[3]], out float second) && second < distance)
            {
                distance = second;
                hit = true;
            }
        }
        return hit;
    }

    private bool ModelMeshComponentsActive()
    {
        return ModelIsMeshLibMode && _modelSelectedElement?.NonCuboid?.Editable == true && ModelCurrentMeshSelectedVertexSet().Count > 0;
    }

    private bool DrawModelMeshComponentGizmo(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ModelElementData element, bool hovered)
    {
        ModelNonCuboidData? mesh = element.NonCuboid;
        HashSet<int> selection = ModelCurrentMeshSelectedVertexSet();
        bool validTool = _modelGizmoTool is ModelGizmoTool.Move or ModelGizmoTool.Resize or ModelGizmoTool.Rotate;
        if (_modelMeshComponentDragging && (mesh?.Editable != true || selection.Count == 0 || !validTool || _modelMeshComponentDragTool != _modelGizmoTool))
        {
            ModelEndMeshComponentDrag(commit: true);
        }
        if (mesh?.Editable != true || selection.Count == 0 || !validTool) return false;
        double[] centerUnits = ModelMeshSelectionCenter(mesh, selection);
        Vector3 centerWorld = ModelMeshVertexWorld(element, centerUnits);
        if (!camera.Project(centerWorld, out NVector2 centerScreen, out _)) return false;
        (Vector3 axisX, Vector3 axisY, Vector3 axisZ) = ModelElementAxes(element);
        Vector3[] axes = [axisX, axisY, axisZ];
        float axisLength = Math.Clamp(_modelViewportDistance * 0.22f, 0.12f, 1.4f);
        NVector2[] ends = new NVector2[3];
        bool[] visible = new bool[3];
        for (int axis = 0; axis < 3; axis++) visible[axis] = camera.Project(centerWorld + axes[axis] * axisLength, out ends[axis], out _);
        int hoveredAxis = -1;
        if (hovered && !_modelMeshComponentDragging)
        {
            float best = ModelGizmoPickDistancePx;
            for (int axis = 0; axis < 3; axis++)
            {
                if (!visible[axis]) continue;
                float candidate = ModelDistancePointToSegment(ImGui.GetMousePos(), centerScreen, ends[axis]);
                if (candidate < best)
                {
                    best = candidate;
                    hoveredAxis = axis;
                }
            }
            if (hoveredAxis >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _modelMeshComponentDragging = true;
                _modelMeshComponentDragAxis = hoveredAxis;
                _modelMeshComponentDragTool = _modelGizmoTool;
                _modelMeshComponentDragStartMouse = ImGui.GetMousePos();
                _modelMeshComponentDragAxisScreenPerUnit = ModelProjectAxisScreenPerUnit(camera, centerWorld, axes[hoveredAxis]);
                _modelMeshComponentDragCenterScreen = centerScreen;
                _modelMeshComponentDragCenter = (double[])centerUnits.Clone();
                _modelMeshComponentDragVertices.Clear();
                foreach (int index in selection) _modelMeshComponentDragVertices[index] = (double[])mesh.Vertices[index].Clone();
                ModelBeginEdit();
            }
        }
        if (_modelMeshComponentDragging && _modelMeshComponentDragTool == _modelGizmoTool)
        {
            hoveredAxis = _modelMeshComponentDragAxis;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                ModelEndMeshComponentDrag(commit: true);
            }
            else
            {
                ModelApplyMeshComponentDrag(mesh);
                ModelMarkChanged();
            }
        }
        for (int axis = 0; axis < 3; axis++)
        {
            if (!visible[axis]) continue;
            uint color = ModelGizmoAxisColor(axis, axis == hoveredAxis);
            drawList.AddLine(centerScreen, ends[axis], color, 2.6f);
            drawList.AddCircleFilled(ends[axis], 5f, color, 12);
        }
        drawList.AddCircleFilled(centerScreen, 4.5f, ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f)), 16);
        return hoveredAxis >= 0 || _modelMeshComponentDragging;
    }

    private void ModelApplyMeshComponentDrag(ModelNonCuboidData mesh)
    {
        int axis = Math.Clamp(_modelMeshComponentDragAxis, 0, 2);
        NVector2 mouseDelta = ImGui.GetMousePos() - _modelMeshComponentDragStartMouse;
        float denominator = _modelMeshComponentDragAxisScreenPerUnit.LengthSquared();
        double units = denominator <= 0.000001f ? 0d : NVector2.Dot(mouseDelta, _modelMeshComponentDragAxisScreenPerUnit) / denominator;
        if (_modelSnapEnabled && !ImGui.IsKeyDown(ImGuiKey.LeftAlt) && !ImGui.IsKeyDown(ImGuiKey.RightAlt)) units = Math.Round(units / Math.Max(0.0001f, _modelSnapMoveUnits)) * _modelSnapMoveUnits;
        foreach ((int index, double[] source) in _modelMeshComponentDragVertices)
        {
            if (index < 0 || index >= mesh.Vertices.Count || source.Length < 3) continue;
            double[] target = mesh.Vertices[index];
            if (_modelMeshComponentDragTool == ModelGizmoTool.Move)
            {
                for (int component = 0; component < 3; component++) target[component] = source[component];
                target[axis] += units;
            }
            else if (_modelMeshComponentDragTool == ModelGizmoTool.Resize)
            {
                double extent = Math.Max(0.001, _modelMeshComponentDragVertices.Values.Max(value => Math.Abs(value[axis] - _modelMeshComponentDragCenter[axis])));
                double scale = Math.Clamp(1d + units / extent, 0.02d, 64d);
                for (int component = 0; component < 3; component++) target[component] = source[component];
                target[axis] = _modelMeshComponentDragCenter[axis] + (source[axis] - _modelMeshComponentDragCenter[axis]) * scale;
            }
            else
            {
                double start = Math.Atan2(_modelMeshComponentDragStartMouse.Y - _modelMeshComponentDragCenterScreen.Y, _modelMeshComponentDragStartMouse.X - _modelMeshComponentDragCenterScreen.X);
                double current = Math.Atan2(ImGui.GetMousePos().Y - _modelMeshComponentDragCenterScreen.Y, ImGui.GetMousePos().X - _modelMeshComponentDragCenterScreen.X);
                double angle = ModelWrapAngleRadians(current - start);
                if (_modelSnapEnabled && !ImGui.IsKeyDown(ImGuiKey.LeftAlt) && !ImGui.IsKeyDown(ImGuiKey.RightAlt))
                {
                    double step = Math.Max(1f, _modelSnapRotateDegrees) * Math.PI / 180d;
                    angle = Math.Round(angle / step) * step;
                }
                ModelRotateMeshPoint(source, target, _modelMeshComponentDragCenter, axis, angle);
            }
        }
    }

    private static void ModelRotateMeshPoint(double[] source, double[] target, double[] center, int axis, double angle)
    {
        double x = source[0] - center[0], y = source[1] - center[1], z = source[2] - center[2];
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        (double rx, double ry, double rz) = axis switch
        {
            0 => (x, y * cos - z * sin, y * sin + z * cos),
            1 => (x * cos + z * sin, y, -x * sin + z * cos),
            _ => (x * cos - y * sin, x * sin + y * cos, z)
        };
        target[0] = center[0] + rx;
        target[1] = center[1] + ry;
        target[2] = center[2] + rz;
    }

    private bool DrawModelMeshTopologyGizmo(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ModelElementData element, bool hovered)
    {
        if (_modelMeshTopologyDragging &&
            (!ReferenceEquals(_modelMeshTopologyDragElement, element) || _modelMeshTopologyDragTool != _modelGizmoTool))
        {
            ModelEndMeshTopologyDrag(commit: false);
        }

        return _modelGizmoTool switch
        {
            ModelGizmoTool.Extrude or ModelGizmoTool.Inset => DrawModelMeshFaceTopologyGizmo(drawList, camera, element, hovered),
            ModelGizmoTool.Subdivide => DrawModelMeshSubdivideGizmo(drawList, camera, element, hovered),
            _ => false
        };
    }

    private bool DrawModelMeshFaceTopologyGizmo(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ModelElementData element, bool hovered)
    {
        ModelNonCuboidData? mesh = element.NonCuboid;
        if (mesh?.Editable != true) return false;
        if (!ModelTryBuildMeshFaceTopologyGizmos(
            element,
            mesh,
            camera,
            _modelGizmoTool,
            out List<ModelMeshTopologyGizmoRegion> regions,
            out string reason))
        {
            if (_modelMeshTopologyDragging) ModelEndMeshTopologyDrag(commit: false);
            DrawModelMeshTopologyHint(drawList, camera, reason);
            return false;
        }

        int hoveredRegion = -1;
        if (hovered && !ModelAnyViewportGizmoDragging)
        {
            NVector2 mouse = ImGui.GetMousePos();
            float best = ModelGizmoPickDistancePx;
            for (int index = 0; index < regions.Count; index++)
            {
                ModelMeshTopologyGizmoRegion region = regions[index];
                float distance = _modelGizmoTool == ModelGizmoTool.Extrude
                    ? ModelDistancePointToSegment(mouse, region.CenterScreen, region.HandleScreen)
                    : NVector2.Distance(mouse, region.HandleScreen);
                if (distance >= best) continue;
                best = distance;
                hoveredRegion = index;
            }
        }

        if (hoveredRegion >= 0)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                ModelBeginMeshTopologyDrag(element, regions[hoveredRegion], hoveredRegion);
            }
        }

        if (_modelMeshTopologyDragging)
        {
            ModelUpdateMeshTopologyDrag();
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                ModelEndMeshTopologyDrag(commit: true);
                return true;
            }

            if (_modelMeshTopologyDragTool == ModelGizmoTool.Extrude)
            {
                DrawModelMeshExtrudeGhost(drawList, camera, element, mesh, regions, _modelMeshTopologyDragValue);
            }
            else
            {
                DrawModelMeshInsetGhost(drawList, camera, element, mesh, regions, _modelMeshTopologyDragValue);
            }
        }

        uint normalColor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.98f, 0.68f, 0.18f, 0.96f));
        uint activeColor = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.72f, 1f));
        for (int index = 0; index < regions.Count; index++)
        {
            ModelMeshTopologyGizmoRegion region = regions[index];
            bool active = index == hoveredRegion || (_modelMeshTopologyDragging && index == _modelMeshTopologyDragRegion);
            uint color = active ? activeColor : normalColor;
            NVector2 handle = region.HandleScreen;
            if (_modelMeshTopologyDragging)
            {
                handle = _modelMeshTopologyDragTool == ModelGizmoTool.Extrude
                    ? handle + region.AxisScreenPerUnit * _modelMeshTopologyDragValue
                    : NVector2.Lerp(handle, region.CenterScreen, _modelMeshTopologyDragValue);
            }

            if (_modelGizmoTool == ModelGizmoTool.Extrude)
            {
                DrawModelMeshExtrudeHandle(drawList, region.CenterScreen, handle, color);
            }
            else
            {
                DrawModelMeshInsetHandle(drawList, region.CenterScreen, handle, color);
            }
        }

        if (_modelMeshTopologyDragging)
        {
            string label = _modelMeshTopologyDragTool == ModelGizmoTool.Extrude
                ? $"Extrude {_modelMeshTopologyDragValue:0.###} u"
                : $"Inset {_modelMeshTopologyDragValue:P0}";
            drawList.AddText(ImGui.GetMousePos() + new NVector2(14f, 14f), activeColor, label);
        }

        return hoveredRegion >= 0 || _modelMeshTopologyDragging;
    }

    private bool ModelTryBuildMeshFaceTopologyGizmos(
        ModelElementData element,
        ModelNonCuboidData mesh,
        DevToolsPreviewCamera camera,
        ModelGizmoTool tool,
        out List<ModelMeshTopologyGizmoRegion> gizmos,
        out string reason)
    {
        gizmos = [];
        reason = "";
        if (_modelMeshSelectionMode != ModelMeshSelectionMode.Face)
        {
            reason = "Extrude and Inset require Face selection mode.";
            return false;
        }
        if (_modelMeshSelectedFaces.Count == 0)
        {
            reason = "Select one or more faces to show the topology gizmo.";
            return false;
        }
        if (!ModelMeshFacesHaveValidCoordinates(mesh, _modelMeshSelectedFaces, out _))
        {
            reason = "Fix the selected faces' validation errors before using topology gizmos.";
            return false;
        }
        if (!ModelTryBuildMeshFaceRegions(mesh, _modelMeshSelectedFaces, out List<HashSet<int>> regions, out reason))
        {
            return false;
        }

        Matrixf elementMatrix = ModelComputeElementMatrix(element);
        foreach (HashSet<int> region in regions)
        {
            List<(int A, int B, ModelMeshFaceData Face)> boundary = ModelMeshRegionBoundary(mesh, region);
            if (tool == ModelGizmoTool.Inset &&
                (!ModelMeshRegionIsCoplanar(mesh, region) || !ModelMeshBoundaryIsSingleLoop(boundary)))
            {
                gizmos.Clear();
                reason = "Inset requires one coplanar boundary loop with no holes.";
                return false;
            }

            int[] vertices = region
                .SelectMany(faceIndex => mesh.Faces[faceIndex].Vertices)
                .Where(vertexIndex => vertexIndex >= 0 && vertexIndex < mesh.Vertices.Count)
                .Distinct()
                .ToArray();
            if (vertices.Length == 0) continue;

            double[] centerUnits = ModelAverageVertices(mesh, vertices);
            Vector3 worldCenter = ModelMeshTopologyPointWorld(
                elementMatrix,
                element,
                centerUnits[0],
                centerUnits[1],
                centerUnits[2]);
            if (!camera.Project(worldCenter, out NVector2 centerScreen, out _)) continue;

            if (tool == ModelGizmoTool.Extrude)
            {
                NVector3 localNormal = ModelMeshRegionNormal(mesh, region);
                if (localNormal.LengthSquared() < 0.0000001f) continue;
                Vector3 worldNormal = ModelTransformDirection(
                    elementMatrix,
                    new Vector3(localNormal.X, localNormal.Y, localNormal.Z));
                NVector2 extrudeAxisScreenPerUnit = ModelMeshTopologyAxisScreenPerUnit(camera, worldCenter, worldNormal);
                NVector2 screenDirection = NVector2.Normalize(extrudeAxisScreenPerUnit);
                gizmos.Add(new ModelMeshTopologyGizmoRegion(
                    region,
                    centerUnits,
                    0f,
                    worldNormal,
                    centerScreen,
                    centerScreen + screenDirection * 48f,
                    extrudeAxisScreenPerUnit));
                continue;
            }

            int bestVertex = -1;
            NVector2 bestScreen = default;
            float bestScreenDistance = -1f;
            foreach (int vertexIndex in boundary.SelectMany(edge => new[] { edge.A, edge.B }).Distinct())
            {
                if (vertexIndex < 0 || vertexIndex >= mesh.Vertices.Count)
                {
                    continue;
                }
                double[] vertex = mesh.Vertices[vertexIndex];
                Vector3 vertexWorld = ModelMeshTopologyPointWorld(
                    elementMatrix,
                    element,
                    vertex.ElementAtOrDefault(0),
                    vertex.ElementAtOrDefault(1),
                    vertex.ElementAtOrDefault(2));
                if (!camera.Project(vertexWorld, out NVector2 vertexScreen, out _)) continue;
                float screenDistance = NVector2.DistanceSquared(centerScreen, vertexScreen);
                if (screenDistance <= bestScreenDistance) continue;
                bestScreenDistance = screenDistance;
                bestVertex = vertexIndex;
                bestScreen = vertexScreen;
            }
            if (bestVertex < 0) continue;

            double[] boundaryVertex = mesh.Vertices[bestVertex];
            NVector3 inward = new(
                (float)(centerUnits[0] - boundaryVertex.ElementAtOrDefault(0)),
                (float)(centerUnits[1] - boundaryVertex.ElementAtOrDefault(1)),
                (float)(centerUnits[2] - boundaryVertex.ElementAtOrDefault(2)));
            float radiusUnits = inward.Length();
            if (radiusUnits < 0.0001f) continue;
            inward /= radiusUnits;
            Vector3 worldDirection = ModelTransformDirection(
                elementMatrix,
                new Vector3(inward.X, inward.Y, inward.Z));
            NVector2 axisScreenPerUnit = (centerScreen - bestScreen) / radiusUnits;
            bool fallbackAxis = axisScreenPerUnit.LengthSquared() < 0.25f;
            if (fallbackAxis)
            {
                Vector3 boundaryWorld = ModelMeshTopologyPointWorld(
                    elementMatrix,
                    element,
                    boundaryVertex.ElementAtOrDefault(0),
                    boundaryVertex.ElementAtOrDefault(1),
                    boundaryVertex.ElementAtOrDefault(2));
                axisScreenPerUnit = ModelMeshTopologyAxisScreenPerUnit(camera, boundaryWorld, worldDirection);
            }
            if (fallbackAxis || NVector2.DistanceSquared(centerScreen, bestScreen) < 18f * 18f)
            {
                NVector2 direction = NVector2.Normalize(axisScreenPerUnit);
                bestScreen = centerScreen - direction * 48f;
                axisScreenPerUnit = (centerScreen - bestScreen) / radiusUnits;
            }

            gizmos.Add(new ModelMeshTopologyGizmoRegion(
                region,
                centerUnits,
                radiusUnits,
                worldDirection,
                centerScreen,
                bestScreen,
                axisScreenPerUnit));
        }

        if (gizmos.Count > 0) return true;
        reason = "The selected faces do not provide a usable topology gizmo.";
        return false;
    }

    private static NVector2 ModelMeshTopologyAxisScreenPerUnit(DevToolsPreviewCamera camera, Vector3 center, Vector3 worldDirection)
    {
        NVector2 projected = ModelProjectAxisScreenPerUnit(camera, center, worldDirection);
        if (projected.LengthSquared() >= 0.25f) return projected;

        float depth = Math.Max(0.05f, (center - camera.Position).Length);
        float pixelsPerUnit = Math.Clamp(camera.FocalLength / (depth * ModelUnitsPerBlock), 0.75f, 20f);
        return new NVector2(0f, -pixelsPerUnit);
    }

    private void ModelBeginMeshTopologyDrag(ModelElementData element, ModelMeshTopologyGizmoRegion region, int regionIndex)
    {
        _modelMeshTopologyDragging = true;
        _modelMeshTopologyDragTool = _modelGizmoTool;
        _modelMeshTopologyDragElement = element;
        _modelMeshTopologyDragSelectionMode = _modelMeshSelectionMode;
        _modelMeshTopologyDragFaces.Clear();
        _modelMeshTopologyDragFaces.UnionWith(_modelMeshSelectedFaces);
        _modelMeshTopologyDragStartMouse = ImGui.GetMousePos();
        _modelMeshTopologyDragAxisScreenPerUnit = region.AxisScreenPerUnit;
        _modelMeshTopologyDragInsetRadiusUnits = region.RadiusUnits;
        _modelMeshTopologyDragValue = 0f;
        _modelMeshTopologyDragRegion = regionIndex;
    }

    private void ModelUpdateMeshTopologyDrag()
    {
        NVector2 delta = ImGui.GetMousePos() - _modelMeshTopologyDragStartMouse;
        float units = ModelProjectMeshTopologyDrag(delta, _modelMeshTopologyDragAxisScreenPerUnit);
        bool bypassSnap = ImGui.IsKeyDown(ImGuiKey.LeftAlt) || ImGui.IsKeyDown(ImGuiKey.RightAlt);
        if (_modelMeshTopologyDragTool == ModelGizmoTool.Extrude)
        {
            _modelMeshTopologyDragValue = Math.Clamp(ModelSnapUnits(units, bypassSnap), -64f, 64f);
            return;
        }

        float fraction = units / Math.Max(0.0001f, _modelMeshTopologyDragInsetRadiusUnits);
        if (_modelSnapEnabled && !bypassSnap) fraction = MathF.Round(fraction / 0.05f) * 0.05f;
        fraction = Math.Clamp(fraction, 0f, 0.95f);
        _modelMeshTopologyDragValue = fraction > 0f ? Math.Max(0.01f, fraction) : 0f;
    }

    private static float ModelProjectMeshTopologyDrag(NVector2 mouseDelta, NVector2 axisScreenPerUnit)
    {
        float lengthSquared = axisScreenPerUnit.LengthSquared();
        return lengthSquared < 0.000001f ? 0f : NVector2.Dot(mouseDelta, axisScreenPerUnit) / lengthSquared;
    }

    private void ModelEndMeshTopologyDrag(bool commit)
    {
        if (!_modelMeshTopologyDragging) return;

        ModelGizmoTool tool = _modelMeshTopologyDragTool;
        float value = _modelMeshTopologyDragValue;
        bool selectionMatches = ReferenceEquals(_modelMeshTopologyDragElement, _modelSelectedElement) &&
            _modelMeshTopologyDragSelectionMode == _modelMeshSelectionMode &&
            _modelMeshTopologyDragFaces.SetEquals(_modelMeshSelectedFaces);

        _modelMeshTopologyDragging = false;
        _modelMeshTopologyDragTool = ModelGizmoTool.None;
        _modelMeshTopologyDragElement = null;
        _modelMeshTopologyDragFaces.Clear();
        _modelMeshTopologyDragAxisScreenPerUnit = NVector2.Zero;
        _modelMeshTopologyDragInsetRadiusUnits = 0f;
        _modelMeshTopologyDragValue = 0f;
        _modelMeshTopologyDragRegion = -1;

        if (!commit || !selectionMatches) return;
        if (tool == ModelGizmoTool.Extrude && Math.Abs(value) >= 0.0001f)
        {
            ModelExtrudeSelectedMeshFaces(value);
        }
        else if (tool == ModelGizmoTool.Inset && value >= 0.001f)
        {
            ModelInsetSelectedMeshFaces(value);
        }
    }

    private static void DrawModelMeshExtrudeHandle(ImDrawListPtr drawList, NVector2 center, NVector2 handle, uint color)
    {
        NVector2 direction = handle - center;
        if (direction.LengthSquared() < 0.0001f) direction = new NVector2(0f, -1f);
        else direction = NVector2.Normalize(direction);
        NVector2 perpendicular = new(-direction.Y, direction.X);
        NVector2 baseCenter = handle - direction * 10f;
        drawList.AddLine(center, baseCenter, color, 3f);
        drawList.AddTriangleFilled(handle, baseCenter + perpendicular * 5.5f, baseCenter - perpendicular * 5.5f, color);
        drawList.AddCircleFilled(center, 4f, color, 12);
    }

    private static void DrawModelMeshInsetHandle(ImDrawListPtr drawList, NVector2 center, NVector2 handle, uint color)
    {
        drawList.AddLine(center, handle, color, 2.5f);
        drawList.AddCircleFilled(center, 4f, color, 12);
        drawList.AddQuadFilled(
            handle + new NVector2(0f, -6f),
            handle + new NVector2(6f, 0f),
            handle + new NVector2(0f, 6f),
            handle + new NVector2(-6f, 0f),
            color);
    }

    private static void DrawModelMeshTopologyHint(ImDrawListPtr drawList, DevToolsPreviewCamera camera, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return;
        NVector2 textSize = ImGui.CalcTextSize(reason);
        NVector2 position = camera.Center + new NVector2(-textSize.X * 0.5f, 42f);
        NVector2 padding = new(8f, 5f);
        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0.06f, 0.055f, 0.05f, 0.84f));
        uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.95f, 0.72f, 0.42f, 1f));
        drawList.AddRectFilled(position - padding, position + textSize + padding, background, 4f);
        drawList.AddText(position, text, reason);
    }

    private static Vector3 ModelMeshTopologyPointWorld(
        Matrixf matrix,
        ModelElementData element,
        double x,
        double y,
        double z)
    {
        return ModelTransformPoint(matrix, new Vector3(
            (float)((x - element.From[0]) / ModelUnitsPerBlock),
            (float)((y - element.From[1]) / ModelUnitsPerBlock),
            (float)((z - element.From[2]) / ModelUnitsPerBlock)));
    }

    private static HashSet<ModelMeshEdge> ModelMeshTopologyRegionEdges(ModelNonCuboidData mesh, IReadOnlySet<int> faces)
    {
        HashSet<ModelMeshEdge> edges = [];
        foreach (int faceIndex in faces)
        {
            if (faceIndex < 0 || faceIndex >= mesh.Faces.Count) continue;
            int[] vertices = mesh.Faces[faceIndex].Vertices;
            for (int index = 0; index < vertices.Length; index++)
            {
                edges.Add(ModelMeshEdge.Create(vertices[index], vertices[(index + 1) % vertices.Length]));
            }
        }
        return edges;
    }

    private static void DrawModelMeshExtrudeGhost(
        ImDrawListPtr drawList,
        DevToolsPreviewCamera camera,
        ModelElementData element,
        ModelNonCuboidData mesh,
        IReadOnlyList<ModelMeshTopologyGizmoRegion> regions,
        float distanceUnits)
    {
        uint color = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.78f, 0.28f, 0.92f));
        Matrixf matrix = ModelComputeElementMatrix(element);
        foreach (ModelMeshTopologyGizmoRegion region in regions)
        {
            Vector3 offset = region.WorldDirection * (distanceUnits / ModelUnitsPerBlock);
            foreach (ModelMeshEdge edge in ModelMeshTopologyRegionEdges(mesh, region.Faces))
            {
                if (edge.A < 0 || edge.B < 0 || edge.A >= mesh.Vertices.Count || edge.B >= mesh.Vertices.Count) continue;
                double[] a = mesh.Vertices[edge.A];
                double[] b = mesh.Vertices[edge.B];
                Vector3 worldA = ModelMeshTopologyPointWorld(
                    matrix,
                    element,
                    a.ElementAtOrDefault(0),
                    a.ElementAtOrDefault(1),
                    a.ElementAtOrDefault(2)) + offset;
                Vector3 worldB = ModelMeshTopologyPointWorld(
                    matrix,
                    element,
                    b.ElementAtOrDefault(0),
                    b.ElementAtOrDefault(1),
                    b.ElementAtOrDefault(2)) + offset;
                DrawModelViewportLine(drawList, camera, worldA, worldB, color, 2.2f);
            }
            foreach (int vertexIndex in ModelMeshRegionBoundary(mesh, region.Faces)
                .SelectMany(edge => new[] { edge.A, edge.B })
                .Distinct())
            {
                if (vertexIndex < 0 || vertexIndex >= mesh.Vertices.Count) continue;
                double[] source = mesh.Vertices[vertexIndex];
                Vector3 world = ModelMeshTopologyPointWorld(
                    matrix,
                    element,
                    source.ElementAtOrDefault(0),
                    source.ElementAtOrDefault(1),
                    source.ElementAtOrDefault(2));
                DrawModelViewportLine(drawList, camera, world, world + offset, color, 1.5f);
            }
        }
    }

    private static void DrawModelMeshInsetGhost(
        ImDrawListPtr drawList,
        DevToolsPreviewCamera camera,
        ModelElementData element,
        ModelNonCuboidData mesh,
        IReadOnlyList<ModelMeshTopologyGizmoRegion> regions,
        float fraction)
    {
        uint color = ImGui.ColorConvertFloat4ToU32(new NVector4(0.42f, 0.94f, 0.86f, 0.94f));
        Matrixf matrix = ModelComputeElementMatrix(element);
        foreach (ModelMeshTopologyGizmoRegion region in regions)
        {
            Dictionary<int, Vector3> insetWorld = [];
            Vector3 Target(int vertexIndex)
            {
                if (insetWorld.TryGetValue(vertexIndex, out Vector3 existing)) return existing;
                double[] source = mesh.Vertices[vertexIndex];
                double scale = 1d - fraction;
                Vector3 target = ModelMeshTopologyPointWorld(
                    matrix,
                    element,
                    region.CenterUnits[0] + (source.ElementAtOrDefault(0) - region.CenterUnits[0]) * scale,
                    region.CenterUnits[1] + (source.ElementAtOrDefault(1) - region.CenterUnits[1]) * scale,
                    region.CenterUnits[2] + (source.ElementAtOrDefault(2) - region.CenterUnits[2]) * scale);
                insetWorld[vertexIndex] = target;
                return target;
            }

            foreach (ModelMeshEdge edge in ModelMeshTopologyRegionEdges(mesh, region.Faces))
            {
                if (edge.A < 0 || edge.B < 0 || edge.A >= mesh.Vertices.Count || edge.B >= mesh.Vertices.Count) continue;
                DrawModelViewportLine(drawList, camera, Target(edge.A), Target(edge.B), color, 2.2f);
            }
            foreach (int vertexIndex in ModelMeshRegionBoundary(mesh, region.Faces)
                .SelectMany(edge => new[] { edge.A, edge.B })
                .Distinct())
            {
                if (vertexIndex < 0 || vertexIndex >= mesh.Vertices.Count) continue;
                double[] source = mesh.Vertices[vertexIndex];
                Vector3 world = ModelMeshTopologyPointWorld(
                    matrix,
                    element,
                    source.ElementAtOrDefault(0),
                    source.ElementAtOrDefault(1),
                    source.ElementAtOrDefault(2));
                DrawModelViewportLine(drawList, camera, world, Target(vertexIndex), color, 1.5f);
            }
        }
    }

    private bool DrawModelMeshSubdivideGizmo(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ModelElementData element, bool hovered)
    {
        ModelNonCuboidData? mesh = element.NonCuboid;
        if (mesh?.Editable != true) return false;
        if (_modelMeshSelectionMode == ModelMeshSelectionMode.Vertex)
        {
            DrawModelMeshTopologyHint(drawList, camera, "Subdivide requires Face or Edge selection mode.");
            return false;
        }
        HashSet<int> selectedVertices = ModelCurrentMeshSelectedVertexSet();
        if (selectedVertices.Count == 0)
        {
            DrawModelMeshTopologyHint(drawList, camera, "Select faces or edges to show the Subdivide gizmo.");
            return false;
        }
        bool valid = _modelMeshSelectionMode == ModelMeshSelectionMode.Face
            ? ModelMeshFacesHaveValidCoordinates(mesh, _modelMeshSelectedFaces, out _)
            : ModelMeshEdgesHaveValidCoordinates(mesh, _modelMeshSelectedEdges, out _);
        if (!valid)
        {
            DrawModelMeshTopologyHint(drawList, camera, "Fix the selected components' validation errors before subdividing.");
            return false;
        }

        double[] centerUnits = ModelMeshSelectionCenter(mesh, selectedVertices);
        Vector3 centerWorld = ModelMeshVertexWorld(element, centerUnits);
        if (!camera.Project(centerWorld, out NVector2 centerScreen, out _)) return false;

        DrawModelMeshSubdivisionPreview(drawList, camera, element, mesh);
        bool handleHovered = hovered && !ModelAnyViewportGizmoDragging && NVector2.Distance(ImGui.GetMousePos(), centerScreen) <= 12f;
        uint color = ImGui.ColorConvertFloat4ToU32(handleHovered
            ? new NVector4(1f, 0.96f, 0.72f, 1f)
            : new NVector4(0.72f, 0.5f, 1f, 0.98f));
        drawList.AddQuadFilled(
            centerScreen + new NVector2(0f, -9f),
            centerScreen + new NVector2(9f, 0f),
            centerScreen + new NVector2(0f, 9f),
            centerScreen + new NVector2(-9f, 0f),
            color);
        uint mark = ImGui.ColorConvertFloat4ToU32(new NVector4(0.08f, 0.06f, 0.12f, 1f));
        drawList.AddLine(centerScreen + new NVector2(-4f, 0f), centerScreen + new NVector2(4f, 0f), mark, 2f);
        drawList.AddLine(centerScreen + new NVector2(0f, -4f), centerScreen + new NVector2(0f, 4f), mark, 2f);

        if (!handleHovered) return false;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            ModelSubdivideSelectedMeshComponents();
        }
        return true;
    }

    private void DrawModelMeshSubdivisionPreview(
        ImDrawListPtr drawList,
        DevToolsPreviewCamera camera,
        ModelElementData element,
        ModelNonCuboidData mesh)
    {
        Vector3[] world = ModelMeshWorldVertices(element);
        uint color = ImGui.ColorConvertFloat4ToU32(new NVector4(0.74f, 0.56f, 1f, 0.84f));
        if (_modelMeshSelectionMode == ModelMeshSelectionMode.Face)
        {
            foreach (int faceIndex in _modelMeshSelectedFaces)
            {
                if (faceIndex < 0 || faceIndex >= mesh.Faces.Count) continue;
                int[] vertices = mesh.Faces[faceIndex].Vertices;
                if (vertices.Length is not (3 or 4) || vertices.Any(index => index < 0 || index >= world.Length)) continue;
                Vector3[] midpoints = new Vector3[vertices.Length];
                for (int index = 0; index < vertices.Length; index++)
                {
                    midpoints[index] = (world[vertices[index]] + world[vertices[(index + 1) % vertices.Length]]) * 0.5f;
                }
                if (vertices.Length == 3)
                {
                    for (int index = 0; index < 3; index++)
                    {
                        DrawModelViewportLine(drawList, camera, midpoints[index], midpoints[(index + 1) % 3], color, 1.7f);
                    }
                }
                else
                {
                    Vector3 center = (world[vertices[0]] + world[vertices[1]] + world[vertices[2]] + world[vertices[3]]) * 0.25f;
                    foreach (Vector3 midpoint in midpoints) DrawModelViewportLine(drawList, camera, midpoint, center, color, 1.7f);
                }
            }
            return;
        }

        foreach (ModelMeshFaceData face in mesh.Faces)
        {
            if (!ModelFaceContainsAnyEdge(face, _modelMeshSelectedEdges) || face.Vertices.Any(index => index < 0 || index >= world.Length)) continue;
            List<Vector3> polygon = [];
            for (int corner = 0; corner < face.Vertices.Length; corner++)
            {
                int a = face.Vertices[corner];
                int b = face.Vertices[(corner + 1) % face.Vertices.Length];
                polygon.Add(world[a]);
                if (_modelMeshSelectedEdges.Contains(ModelMeshEdge.Create(a, b))) polygon.Add((world[a] + world[b]) * 0.5f);
            }
            if (polygon.Count == 0) continue;
            Vector3 center = polygon.Aggregate(Vector3.Zero, (sum, point) => sum + point) / polygon.Count;
            foreach (Vector3 point in polygon) DrawModelViewportLine(drawList, camera, point, center, color, 1.5f);
        }
    }
}
