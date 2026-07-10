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
        return mesh.Vertices.Select(vertex => ModelMeshVertexWorld(element, vertex)).ToArray();
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
        if (mesh?.Editable != true || selection.Count == 0 || _modelGizmoTool is not (ModelGizmoTool.Move or ModelGizmoTool.Resize or ModelGizmoTool.Rotate)) return false;
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
                _modelMeshComponentDragging = false;
                _modelMeshComponentDragAxis = -1;
                _modelMeshComponentDragVertices.Clear();
                ModelEndEdit("Transform mesh components");
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
}
