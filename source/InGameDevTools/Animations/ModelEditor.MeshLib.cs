using ImGuiNET;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private enum ModelEditorMode
    {
        Vanilla,
        MeshLib
    }

    private enum ModelMeshSelectionMode
    {
        Vertex,
        Edge,
        Face
    }

    private readonly record struct ModelMeshEdge(int A, int B)
    {
        public static ModelMeshEdge Create(int a, int b) => a <= b ? new ModelMeshEdge(a, b) : new ModelMeshEdge(b, a);
    }

    private sealed class ModelMeshFaceData
    {
        public int[] Vertices = [];
        public string Texture = "";
        public List<float[]>? Uv;
        public int Glow;
        public bool Shade = true;
        public JObject? Extra;

        public ModelMeshFaceData Clone()
        {
            return new ModelMeshFaceData
            {
                Vertices = (int[])Vertices.Clone(),
                Texture = Texture,
                Uv = Uv?.Select(value => (float[])value.Clone()).ToList(),
                Glow = Glow,
                Shade = Shade,
                Extra = (JObject?)Extra?.DeepClone()
            };
        }
    }

    private sealed class ModelNonCuboidData
    {
        public List<double[]> Vertices = [];
        public List<ModelMeshFaceData> Faces = [];
        public JObject? Extra;
        public JToken? Raw;

        public bool Editable => Raw == null;

        public ModelNonCuboidData Clone()
        {
            return new ModelNonCuboidData
            {
                Vertices = Vertices.Select(value => (double[])value.Clone()).ToList(),
                Faces = Faces.Select(face => face.Clone()).ToList(),
                Extra = (JObject?)Extra?.DeepClone(),
                Raw = Raw?.DeepClone()
            };
        }
    }

    private static readonly HashSet<string> ModelKnownNonCuboidKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "vertices", "faces"
    };

    private static readonly HashSet<string> ModelKnownMeshFaceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "v", "texture", "uv", "glow", "shade"
    };

    private ModelEditorMode _modelEditorMode;
    private ModelMeshSelectionMode _modelMeshSelectionMode = ModelMeshSelectionMode.Face;
    private readonly HashSet<int> _modelMeshSelectedVertices = [];
    private readonly HashSet<ModelMeshEdge> _modelMeshSelectedEdges = [];
    private readonly HashSet<int> _modelMeshSelectedFaces = [];
    private readonly List<int> _modelMeshVertexSelectionOrder = [];
    private int _modelMeshActiveVertex = -1;
    private ModelMeshEdge? _modelMeshActiveEdge;
    private int _modelMeshActiveFace = -1;
    private float _modelMeshExtrudeDistance = 1f;
    private float _modelMeshInsetFraction = 0.2f;
    private float _modelMeshWeldTolerance = 0.0001f;
    private string _modelMeshValidationStatus = "";

    private bool ModelIsMeshLibMode => _modelEditorMode == ModelEditorMode.MeshLib;

    private static bool ModelDocumentContainsNonCuboid(ModelDocumentData? doc)
    {
        return doc?.EnumerateElements().Any(element => element.NonCuboid != null) == true;
    }

    private static bool ModelAssetLooksMeshLib(IAsset asset)
    {
        try
        {
            byte[]? data = asset.Data;
            if (data == null || data.Length < 9) return false;
            return Encoding.UTF8.GetString(data).Contains("\"noncuboid\"", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void DrawModelModePicker()
    {
        ImGui.SameLine();
        ImGui.TextDisabled("Mode");
        ImGui.SameLine();
        int mode = (int)_modelEditorMode;
        ImGui.SetNextItemWidth(92f);
        if (ImGui.Combo("##model-editor-mode", ref mode, ["Vanilla", "MeshLib"], 2))
        {
            _modelEditorMode = (ModelEditorMode)Math.Clamp(mode, 0, 1);
            if (!ModelIsMeshLibMode)
            {
                ModelClearMeshComponentSelection();
                if (_modelGizmoTool is ModelGizmoTool.Extrude or ModelGizmoTool.Inset or ModelGizmoTool.Subdivide)
                {
                    _modelGizmoTool = ModelGizmoTool.None;
                }
            }
            _modelPreviewDirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("MeshLib mode adds triangle/quad mesh elements. Switching modes never removes noncuboid JSON.");
        }

        if (ModelIsMeshLibMode)
        {
            ImGui.SameLine();
            bool operational = ModelMeshLibIsOperational(out string status);
            ImGui.TextColored(
                operational ? new NVector4(0.45f, 0.85f, 0.5f, 1f) : new NVector4(1f, 0.65f, 0.28f, 1f),
                operational ? "MeshLib ready" : "MeshLib offline");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(status);
        }
    }

    private void DrawModelMeshToolbar()
    {
        if (!ModelIsMeshLibMode || _modelSelectedElement?.NonCuboid?.Editable != true) return;

        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Mesh components");
        ImGui.SameLine();
        int selectionMode = (int)_modelMeshSelectionMode;
        ImGui.SetNextItemWidth(92f);
        if (ImGui.Combo("##model-mesh-selection-mode", ref selectionMode, ["Vertex", "Edge", "Face"], 3))
        {
            _modelMeshSelectionMode = (ModelMeshSelectionMode)Math.Clamp(selectionMode, 0, 2);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("All##model-mesh-select-all")) ModelSelectAllMeshComponents();
        ImGui.SameLine();
        if (ImGui.SmallButton("None##model-mesh-select-none")) ModelClearMeshComponentSelection();
        ImGui.SameLine();
        if (ImGui.SmallButton("Invert##model-mesh-select-invert")) ModelInvertMeshComponentSelection();
        ImGui.SameLine();
        if (ImGui.SmallButton("Connected##model-mesh-select-connected")) ModelSelectConnectedMeshComponents();

        if (_modelMeshSelectionMode == ModelMeshSelectionMode.Face)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(72f);
            ImGui.DragFloat("Extrude##model-mesh-extrude-distance", ref _modelMeshExtrudeDistance, 0.05f, -64f, 64f, "%.3f");
            ImGui.SameLine();
            if (ImGui.SmallButton("Apply##model-mesh-extrude")) ModelExtrudeSelectedMeshFaces(_modelMeshExtrudeDistance);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(68f);
            ImGui.SliderFloat("Inset##model-mesh-inset-fraction", ref _modelMeshInsetFraction, 0.01f, 0.95f, "%.2f");
            ImGui.SameLine();
            if (ImGui.SmallButton("Apply##model-mesh-inset")) ModelInsetSelectedMeshFaces(_modelMeshInsetFraction);
        }

        if (_modelMeshSelectionMode is ModelMeshSelectionMode.Edge or ModelMeshSelectionMode.Face)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Subdivide##model-mesh-subdivide")) ModelSubdivideSelectedMeshComponents();
        }
    }

    private static bool ModelTryParseNonCuboid(JToken token, out ModelNonCuboidData data)
    {
        data = new ModelNonCuboidData();
        if (token is not JObject obj)
        {
            data.Raw = token.DeepClone();
            return false;
        }

        try
        {
            if (ModelFindProperty(obj, "vertices")?.Value is not JArray vertices ||
                ModelFindProperty(obj, "faces")?.Value is not JArray faces)
            {
                data.Raw = token.DeepClone();
                return false;
            }

            foreach (JToken vertexToken in vertices)
            {
                if (vertexToken is not JArray vertex)
                {
                    data.Raw = token.DeepClone();
                    return false;
                }
                data.Vertices.Add(vertex.Select(value => value.ToObject<double>()).ToArray());
            }

            foreach (JToken faceToken in faces)
            {
                if (faceToken is not JObject faceObject || ModelFindProperty(faceObject, "v")?.Value is not JArray indices)
                {
                    data.Raw = token.DeepClone();
                    data.Vertices.Clear();
                    data.Faces.Clear();
                    return false;
                }

                ModelMeshFaceData face = new()
                {
                    Vertices = indices.Select(value => value.ToObject<int>()).ToArray(),
                    Texture = ModelFindProperty(faceObject, "texture")?.Value.ToString() ?? "",
                    Glow = ModelReadInt(faceObject, "glow", 0),
                    Shade = ModelReadBool(faceObject, "shade", true)
                };
                if (face.Texture.StartsWith('#')) face.Texture = face.Texture[1..];
                if (ModelFindProperty(faceObject, "uv")?.Value is JArray uv)
                {
                    face.Uv = [];
                    foreach (JToken uvToken in uv)
                    {
                        if (uvToken is not JArray values)
                        {
                            data.Raw = token.DeepClone();
                            data.Vertices.Clear();
                            data.Faces.Clear();
                            return false;
                        }
                        face.Uv.Add(values.Select(value => value.ToObject<float>()).ToArray());
                    }
                }

                JObject faceExtra = new();
                foreach (JProperty property in faceObject.Properties())
                {
                    if (!ModelKnownMeshFaceKeys.Contains(property.Name)) faceExtra[property.Name] = property.Value.DeepClone();
                }
                face.Extra = faceExtra.Count > 0 ? faceExtra : null;
                data.Faces.Add(face);
            }

            JObject extra = new();
            foreach (JProperty property in obj.Properties())
            {
                if (!ModelKnownNonCuboidKeys.Contains(property.Name)) extra[property.Name] = property.Value.DeepClone();
            }
            data.Extra = extra.Count > 0 ? extra : null;
            return true;
        }
        catch
        {
            data = new ModelNonCuboidData { Raw = token.DeepClone() };
            return false;
        }
    }

    private static JToken ModelSerializeNonCuboid(ModelNonCuboidData data)
    {
        if (data.Raw != null) return data.Raw.DeepClone();

        JObject obj = new();
        JArray vertices = [];
        foreach (double[] vertex in data.Vertices) vertices.Add(new JArray(vertex.Cast<object?>()));
        obj["vertices"] = vertices;

        JArray faces = [];
        foreach (ModelMeshFaceData face in data.Faces)
        {
            JObject faceObject = new()
            {
                ["v"] = new JArray(face.Vertices.Cast<object?>()),
                ["texture"] = "#" + face.Texture
            };
            if (face.Uv != null)
            {
                faceObject["uv"] = new JArray(face.Uv.Select(value => new JArray(value.Cast<object?>())));
            }
            if (face.Glow != 0) faceObject["glow"] = face.Glow;
            if (!face.Shade) faceObject["shade"] = false;
            if (face.Extra != null)
            {
                foreach (JProperty property in face.Extra.Properties()) faceObject[property.Name] = property.Value.DeepClone();
            }
            faces.Add(faceObject);
        }
        obj["faces"] = faces;
        if (data.Extra != null)
        {
            foreach (JProperty property in data.Extra.Properties()) obj[property.Name] = property.Value.DeepClone();
        }
        return obj;
    }

    private static List<string> ModelValidateNonCuboid(ModelNonCuboidData? data)
    {
        List<string> errors = [];
        if (data == null) return errors;
        if (data.Raw != null)
        {
            errors.Add("noncuboid payload is not structurally editable; repair it in JSON or replace it with a new mesh");
            return errors;
        }
        if (data.Vertices.Count < 3) errors.Add("needs at least 3 vertices");
        for (int index = 0; index < data.Vertices.Count; index++)
        {
            double[] vertex = data.Vertices[index];
            if (vertex.Length != 3) errors.Add($"vertex {index} must have exactly 3 coordinates");
            else if (vertex.Any(value => !double.IsFinite(value))) errors.Add($"vertex {index} coordinates must all be finite");
        }
        if (data.Faces.Count == 0) errors.Add("needs at least 1 face");
        for (int faceIndex = 0; faceIndex < data.Faces.Count; faceIndex++)
        {
            ModelMeshFaceData face = data.Faces[faceIndex];
            if (face.Vertices.Length is not (3 or 4))
            {
                errors.Add($"face {faceIndex} must reference exactly 3 or 4 vertices");
                continue;
            }
            if (face.Vertices.Any(index => index < 0 || index >= data.Vertices.Count))
            {
                errors.Add($"face {faceIndex} contains an out-of-range vertex index");
                continue;
            }
            if (face.Vertices.Length == 3 && face.Vertices.Distinct().Count() != 3)
            {
                errors.Add($"face {faceIndex} triangle must reference 3 distinct vertices");
            }
            if (ModelMeshTriangleDegenerate(data, face.Vertices[0], face.Vertices[1], face.Vertices[2]))
            {
                errors.Add($"face {faceIndex} triangle (0,1,2) is degenerate");
            }
            if (face.Vertices.Length == 4 && ModelMeshTriangleDegenerate(data, face.Vertices[0], face.Vertices[2], face.Vertices[3]))
            {
                errors.Add($"face {faceIndex} triangle (0,2,3) is degenerate");
            }
            string texture = face.Texture.Trim();
            if (texture.Length == 0 || texture.StartsWith('#')) errors.Add($"face {faceIndex} has an invalid texture code");
            if (face.Uv != null)
            {
                if (face.Uv.Count != face.Vertices.Length) errors.Add($"face {faceIndex} uv count must match its vertex count");
                for (int uvIndex = 0; uvIndex < face.Uv.Count; uvIndex++)
                {
                    float[] uv = face.Uv[uvIndex];
                    if (uv.Length != 2 || uv.Any(value => !float.IsFinite(value))) errors.Add($"face {faceIndex} uv {uvIndex} must contain two finite values");
                }
            }
            if (face.Glow is < 0 or > 255) errors.Add($"face {faceIndex} glow must be in the inclusive range 0..255");
        }
        return errors;
    }

    private static bool ModelMeshTriangleDegenerate(ModelNonCuboidData data, int ia, int ib, int ic)
    {
        if (ia < 0 || ib < 0 || ic < 0 || ia >= data.Vertices.Count || ib >= data.Vertices.Count || ic >= data.Vertices.Count) return false;
        double[] a = data.Vertices[ia];
        double[] b = data.Vertices[ib];
        double[] c = data.Vertices[ic];
        if (a.Length != 3 || b.Length != 3 || c.Length != 3) return false;
        double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
        double vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
        double nx = uy * vz - uz * vy;
        double ny = uz * vx - ux * vz;
        double nz = ux * vy - uy * vx;
        return Math.Sqrt(nx * nx + ny * ny + nz * nz) <= 1e-6;
    }

    private static bool ModelTryGetMeshBounds(ModelElementData element, out double[] min, out double[] max)
    {
        min = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        max = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        ModelNonCuboidData? mesh = element.NonCuboid;
        if (mesh?.Editable != true || mesh.Vertices.Count == 0) return false;
        bool any = false;
        foreach (double[] vertex in mesh.Vertices)
        {
            if (vertex.Length != 3 || vertex.Any(value => !double.IsFinite(value))) continue;
            for (int axis = 0; axis < 3; axis++)
            {
                min[axis] = Math.Min(min[axis], vertex[axis]);
                max[axis] = Math.Max(max[axis], vertex[axis]);
            }
            any = true;
        }
        return any;
    }

    private static IEnumerable<ModelMeshEdge> ModelMeshEdges(ModelNonCuboidData mesh)
    {
        HashSet<ModelMeshEdge> seen = [];
        foreach (ModelMeshFaceData face in mesh.Faces)
        {
            for (int index = 0; index < face.Vertices.Length; index++)
            {
                ModelMeshEdge edge = ModelMeshEdge.Create(face.Vertices[index], face.Vertices[(index + 1) % face.Vertices.Length]);
                if (seen.Add(edge)) yield return edge;
            }
        }
    }

    private static HashSet<int> ModelMeshSelectedVertexSet(
        ModelNonCuboidData mesh,
        ModelMeshSelectionMode mode,
        IReadOnlyCollection<int> selectedVertices,
        IReadOnlyCollection<ModelMeshEdge> selectedEdges,
        IReadOnlyCollection<int> selectedFaces)
    {
        HashSet<int> result = [];
        if (mode == ModelMeshSelectionMode.Vertex) result.UnionWith(selectedVertices);
        else if (mode == ModelMeshSelectionMode.Edge)
        {
            foreach (ModelMeshEdge edge in selectedEdges)
            {
                result.Add(edge.A);
                result.Add(edge.B);
            }
        }
        else
        {
            foreach (int faceIndex in selectedFaces)
            {
                if (faceIndex >= 0 && faceIndex < mesh.Faces.Count) result.UnionWith(mesh.Faces[faceIndex].Vertices);
            }
        }
        result.RemoveWhere(index => index < 0 || index >= mesh.Vertices.Count);
        return result;
    }

    private HashSet<int> ModelCurrentMeshSelectedVertexSet()
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        return mesh?.Editable == true
            ? ModelMeshSelectedVertexSet(mesh, _modelMeshSelectionMode, _modelMeshSelectedVertices, _modelMeshSelectedEdges, _modelMeshSelectedFaces)
            : [];
    }

    private bool ModelNudgeSelectedMeshComponents(double dx, double dy, double dz)
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        HashSet<int> selection = ModelCurrentMeshSelectedVertexSet();
        if (mesh?.Editable != true || selection.Count == 0) return false;
        ModelBeginEdit();
        foreach (int index in selection)
        {
            if (index < 0 || index >= mesh.Vertices.Count || mesh.Vertices[index].Length < 3) continue;
            mesh.Vertices[index][0] += dx;
            mesh.Vertices[index][1] += dy;
            mesh.Vertices[index][2] += dz;
        }
        ModelMarkChanged();
        ModelEndEdit("Nudge mesh components");
        _modelStatus = $"Nudged {selection.Count} mesh vertex/vertices {ModelFormatNudgeDelta(dx, dy, dz)}.";
        return true;
    }

    private void ModelClearMeshComponentSelection()
    {
        _modelMeshSelectedVertices.Clear();
        _modelMeshSelectedEdges.Clear();
        _modelMeshSelectedFaces.Clear();
        _modelMeshVertexSelectionOrder.Clear();
        _modelMeshActiveVertex = -1;
        _modelMeshActiveEdge = null;
        _modelMeshActiveFace = -1;
    }

    private void ModelSelectAllMeshComponents()
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        if (mesh?.Editable != true) return;
        ModelClearMeshComponentSelection();
        if (_modelMeshSelectionMode == ModelMeshSelectionMode.Vertex)
        {
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                _modelMeshSelectedVertices.Add(i);
                _modelMeshVertexSelectionOrder.Add(i);
            }
            _modelMeshActiveVertex = mesh.Vertices.Count - 1;
        }
        else if (_modelMeshSelectionMode == ModelMeshSelectionMode.Edge)
        {
            _modelMeshSelectedEdges.UnionWith(ModelMeshEdges(mesh));
            _modelMeshActiveEdge = _modelMeshSelectedEdges.LastOrDefault();
        }
        else
        {
            for (int i = 0; i < mesh.Faces.Count; i++) _modelMeshSelectedFaces.Add(i);
            _modelMeshActiveFace = mesh.Faces.Count - 1;
        }
    }

    private void ModelInvertMeshComponentSelection()
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        if (mesh?.Editable != true) return;
        if (_modelMeshSelectionMode == ModelMeshSelectionMode.Vertex)
        {
            HashSet<int> old = [.. _modelMeshSelectedVertices];
            _modelMeshSelectedVertices.Clear();
            _modelMeshVertexSelectionOrder.Clear();
            for (int i = 0; i < mesh.Vertices.Count; i++) if (!old.Contains(i))
            {
                _modelMeshSelectedVertices.Add(i);
                _modelMeshVertexSelectionOrder.Add(i);
            }
        }
        else if (_modelMeshSelectionMode == ModelMeshSelectionMode.Edge)
        {
            HashSet<ModelMeshEdge> old = [.. _modelMeshSelectedEdges];
            _modelMeshSelectedEdges.Clear();
            foreach (ModelMeshEdge edge in ModelMeshEdges(mesh)) if (!old.Contains(edge)) _modelMeshSelectedEdges.Add(edge);
        }
        else
        {
            HashSet<int> old = [.. _modelMeshSelectedFaces];
            _modelMeshSelectedFaces.Clear();
            for (int i = 0; i < mesh.Faces.Count; i++) if (!old.Contains(i)) _modelMeshSelectedFaces.Add(i);
        }
    }

    private void ModelSelectConnectedMeshComponents()
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        if (mesh?.Editable != true) return;
        HashSet<int> seedVertices = ModelCurrentMeshSelectedVertexSet();
        if (seedVertices.Count == 0) return;
        HashSet<int> connectedVertices = [.. seedVertices];
        bool changed;
        do
        {
            changed = false;
            foreach (ModelMeshFaceData face in mesh.Faces)
            {
                if (!face.Vertices.Any(connectedVertices.Contains)) continue;
                foreach (int vertex in face.Vertices) changed |= connectedVertices.Add(vertex);
            }
        } while (changed);

        if (_modelMeshSelectionMode == ModelMeshSelectionMode.Vertex)
        {
            _modelMeshSelectedVertices.UnionWith(connectedVertices);
            foreach (int vertex in connectedVertices) if (!_modelMeshVertexSelectionOrder.Contains(vertex)) _modelMeshVertexSelectionOrder.Add(vertex);
        }
        else if (_modelMeshSelectionMode == ModelMeshSelectionMode.Edge)
        {
            foreach (ModelMeshEdge edge in ModelMeshEdges(mesh)) if (connectedVertices.Contains(edge.A) && connectedVertices.Contains(edge.B)) _modelMeshSelectedEdges.Add(edge);
        }
        else
        {
            for (int i = 0; i < mesh.Faces.Count; i++) if (mesh.Faces[i].Vertices.Any(connectedVertices.Contains)) _modelMeshSelectedFaces.Add(i);
        }
    }

    private void DrawModelMeshElementBoundsControls(ModelElementData element)
    {
        ImGui.SeparatorText("Mesh geometry");
        if (!ModelTryGetMeshBounds(element, out double[] min, out double[] max))
        {
            ImGui.TextDisabled("No finite mesh bounds.");
            return;
        }
        ImGui.TextDisabled($"Bounds [{min[0]:0.###}, {min[1]:0.###}, {min[2]:0.###}] to [{max[0]:0.###}, {max[1]:0.###}, {max[2]:0.###}]");
        ImGui.TextDisabled($"Size [{max[0] - min[0]:0.###}, {max[1] - min[1]:0.###}, {max[2] - min[2]:0.###}]");
        if (ImGui.SmallButton("Fit from/to to mesh##model-mesh-fit-anchor"))
        {
            ModelBeginEdit();
            element.From = (double[])min.Clone();
            element.To = (double[])max.Clone();
            ModelMarkChanged();
            ModelEndEdit("Fit mesh anchor bounds");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Sets the raw MeshLib from/to compatibility fields to the current vertex AABB without moving vertices.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Convert to cuboid##model-mesh-to-cuboid")) ModelConvertSelectedMeshToCuboid();
    }

    private void DrawModelMeshInspector(ModelDocumentData doc)
    {
        ModelElementData? element = _modelSelectedElement;
        ModelNonCuboidData? mesh = element?.NonCuboid;
        if (element == null || mesh == null) return;

        ImGui.SeparatorText("MeshLib geometry");
        if (!mesh.Editable)
        {
            ImGui.TextColored(new NVector4(1f, 0.45f, 0.35f, 1f), "The noncuboid payload is malformed or uses an unsupported structure.");
            ImGui.TextWrapped("It is preserved verbatim. Repair it in the JSON tab or replace it with a generated mesh.");
            if (ImGui.Button("Replace with mesh box##model-mesh-replace-raw"))
            {
                ModelBeginEdit();
                element.NonCuboid = ModelCreateBoxMesh(element.From, element.To, _modelSelectedTextureCode);
                Array.Clear(element.Faces);
                ModelClearMeshComponentSelection();
                ModelMarkChanged();
                ModelEndEdit("Replace malformed mesh");
            }
            return;
        }

        List<string> errors = ModelValidateNonCuboid(mesh);
        _modelMeshValidationStatus = errors.Count == 0 ? "MeshLib validation passed." : errors[0];
        ImGui.TextColored(errors.Count == 0 ? new NVector4(0.48f, 0.82f, 0.5f, 1f) : new NVector4(1f, 0.45f, 0.35f, 1f),
            errors.Count == 0 ? $"{mesh.Vertices.Count} vertices, {mesh.Faces.Count} faces — valid" : $"{errors.Count} validation error(s): {errors[0]}");
        if (errors.Count > 1 && ImGui.TreeNode("All validation errors##model-mesh-errors"))
        {
            foreach (string error in errors) ImGui.BulletText(error);
            ImGui.TreePop();
        }

        int selectionMode = (int)_modelMeshSelectionMode;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.Combo("Selection##model-mesh-inspector-selection", ref selectionMode, ["Vertex", "Edge", "Face"], 3))
        {
            _modelMeshSelectionMode = (ModelMeshSelectionMode)Math.Clamp(selectionMode, 0, 2);
        }

        if (_modelMeshSelectionMode == ModelMeshSelectionMode.Vertex) DrawModelMeshVertexInspector(mesh);
        else if (_modelMeshSelectionMode == ModelMeshSelectionMode.Edge) DrawModelMeshEdgeInspector(mesh);
        else DrawModelMeshFaceInspector(doc, mesh);

        DrawModelMeshTopologyButtons(mesh);
    }

    private void DrawModelMeshVertexInspector(ModelNonCuboidData mesh)
    {
        if (ImGui.SmallButton("Add vertex##model-mesh-add-vertex"))
        {
            ModelBeginEdit();
            double[] vertex = ModelMeshSelectionCenter(mesh, ModelCurrentMeshSelectedVertexSet());
            mesh.Vertices.Add(vertex);
            ModelClearMeshComponentSelection();
            _modelMeshSelectedVertices.Add(mesh.Vertices.Count - 1);
            _modelMeshVertexSelectionOrder.Add(mesh.Vertices.Count - 1);
            _modelMeshActiveVertex = mesh.Vertices.Count - 1;
            ModelMarkChanged();
            ModelEndEdit("Add mesh vertex");
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{_modelMeshSelectedVertices.Count} selected");

        ImGui.BeginChild("##model-mesh-vertices", new NVector2(0f, Math.Min(230f, Math.Max(100f, mesh.Vertices.Count * 25f + 8f))), true);
        try
        {
            for (int index = 0; index < mesh.Vertices.Count; index++)
            {
                double[] value = mesh.Vertices[index];
                bool selected = _modelMeshSelectedVertices.Contains(index);
                if (ImGui.Selectable($"{index}##model-mesh-vertex-select-{index}", selected, ImGuiSelectableFlags.AllowOverlap, new NVector2(34f, 0f)))
                {
                    ModelSetMeshVertexSelection(index, IsDevToolsCtrlDown());
                }
                ImGui.SameLine();
                NVector3 vector = value.Length >= 3 ? new((float)value[0], (float)value[1], (float)value[2]) : NVector3.Zero;
                ImGui.SetNextItemWidth(-1f);
                bool changed = ImGui.DragFloat3($"##model-mesh-vertex-{index}", ref vector, 0.05f);
                if (ImGui.IsItemActivated()) ModelBeginEdit();
                if (changed)
                {
                    mesh.Vertices[index] = [vector.X, vector.Y, vector.Z];
                    ModelMarkChanged();
                }
                if (ImGui.IsItemDeactivatedAfterEdit()) ModelEndEdit("Edit mesh vertex");
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawModelMeshEdgeInspector(ModelNonCuboidData mesh)
    {
        List<ModelMeshEdge> edges = ModelMeshEdges(mesh).ToList();
        ImGui.TextDisabled($"{_modelMeshSelectedEdges.Count} / {edges.Count} implicit edges selected");
        ImGui.BeginChild("##model-mesh-edges", new NVector2(0f, Math.Min(220f, Math.Max(80f, edges.Count * 22f + 8f))), true);
        try
        {
            foreach (ModelMeshEdge edge in edges)
            {
                bool selected = _modelMeshSelectedEdges.Contains(edge);
                if (ImGui.Selectable($"{edge.A} — {edge.B}##model-mesh-edge-{edge.A}-{edge.B}", selected))
                {
                    ModelSetMeshEdgeSelection(edge, IsDevToolsCtrlDown());
                }
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawModelMeshFaceInspector(ModelDocumentData doc, ModelNonCuboidData mesh)
    {
        ImGui.TextDisabled($"{_modelMeshSelectedFaces.Count} / {mesh.Faces.Count} faces selected");
        ImGui.BeginChild("##model-mesh-faces", new NVector2(0f, Math.Min(260f, Math.Max(100f, mesh.Faces.Count * 28f + 8f))), true);
        try
        {
            for (int index = 0; index < mesh.Faces.Count; index++)
            {
                ModelMeshFaceData face = mesh.Faces[index];
                bool selected = _modelMeshSelectedFaces.Contains(index);
                if (ImGui.Selectable($"{index}: [{string.Join(", ", face.Vertices)}]  #{face.Texture}##model-mesh-face-{index}", selected))
                {
                    ModelSetMeshFaceSelection(index, IsDevToolsCtrlDown());
                }
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        int active = _modelMeshActiveFace >= 0 ? _modelMeshActiveFace : _modelMeshSelectedFaces.FirstOrDefault(-1);
        if (active < 0 || active >= mesh.Faces.Count) return;
        ModelMeshFaceData activeFace = mesh.Faces[active];
        string indices = string.Join(",", activeFace.Vertices);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("Indices##model-mesh-face-indices", ref indices, 96) && ModelTryParseMeshIndices(indices, out int[] parsed))
        {
            activeFace.Vertices = parsed;
            ModelMarkChanged();
        }
        List<string> textureCodes = doc.Textures.Select(texture => texture.Code).ToList();
        ImGui.SetNextItemWidth(-1f);
        if (ModelFilteredCombo("Texture##model-mesh-face-texture", activeFace.Texture, textureCodes, out string pickedTexture, true, "filter texture codes"))
        {
            ModelBeginEdit();
            foreach (int faceIndex in ModelSelectedMeshFacesOrActive(active)) mesh.Faces[faceIndex].Texture = pickedTexture.TrimStart('#');
            ModelMarkChanged();
            ModelEndEdit("Edit mesh face texture");
        }
        int glow = activeFace.Glow;
        bool shade = activeFace.Shade;
        if (ImGui.SliderInt("Glow##model-mesh-face-glow", ref glow, 0, 255))
        {
            foreach (int faceIndex in ModelSelectedMeshFacesOrActive(active)) mesh.Faces[faceIndex].Glow = glow;
            ModelMarkChanged();
        }
        if (ImGui.Checkbox("Shade##model-mesh-face-shade", ref shade))
        {
            foreach (int faceIndex in ModelSelectedMeshFacesOrActive(active)) mesh.Faces[faceIndex].Shade = shade;
            ModelMarkChanged();
        }
        if (ImGui.SmallButton("Auto UV##model-mesh-face-auto-uv")) ModelAutoUvSelectedMeshFaces();
    }

    private void DrawModelMeshTopologyButtons(ModelNonCuboidData mesh)
    {
        ImGui.SeparatorText("Topology");
        if (_modelMeshSelectionMode == ModelMeshSelectionMode.Vertex)
        {
            bool canFace = _modelMeshVertexSelectionOrder.Distinct().Count() is 3 or 4;
            if (!canFace) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Create face##model-mesh-create-face")) ModelCreateFaceFromSelectedVertices();
            if (!canFace) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton("Weld##model-mesh-weld")) ModelWeldSelectedMeshVertices(_modelMeshWeldTolerance);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(74f);
            ImGui.DragFloat("##model-mesh-weld-tolerance", ref _modelMeshWeldTolerance, 0.0001f, 0f, 1f, "%.5f");
        }
        else if (_modelMeshSelectionMode == ModelMeshSelectionMode.Face)
        {
            if (ImGui.SmallButton("Reverse##model-mesh-reverse")) ModelReverseSelectedMeshFaces();
            ImGui.SameLine();
            if (ImGui.SmallButton("Double-sided##model-mesh-double-sided")) ModelMakeSelectedMeshFacesDoubleSided();
            ImGui.SameLine();
            if (ImGui.SmallButton("Duplicate##model-mesh-duplicate-faces")) ModelDuplicateSelectedMeshFaces();
        }
        if (ImGui.SmallButton("Delete selected##model-mesh-delete")) ModelDeleteSelectedMeshComponents();
        ImGui.SameLine();
        if (ImGui.SmallButton("Remove unused##model-mesh-remove-unused")) ModelRemoveUnusedMeshVertices();
        ImGui.SameLine();
        if (ImGui.SmallButton("Subdivide##model-mesh-subdivide-inspector")) ModelSubdivideSelectedMeshComponents();

        _ = mesh;
    }

    private static bool ModelTryParseMeshIndices(string value, out int[] indices)
    {
        indices = [];
        try
        {
            indices = value.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            return indices.Length is 3 or 4;
        }
        catch
        {
            indices = [];
            return false;
        }
    }

    private IEnumerable<int> ModelSelectedMeshFacesOrActive(int active)
    {
        return _modelMeshSelectedFaces.Count > 0 ? _modelMeshSelectedFaces.Order() : [active];
    }

    private static double[] ModelMeshSelectionCenter(ModelNonCuboidData mesh, IEnumerable<int> selection)
    {
        int[] valid = selection.Where(index => index >= 0 && index < mesh.Vertices.Count && mesh.Vertices[index].Length >= 3).Distinct().ToArray();
        if (valid.Length == 0) return [0d, 0d, 0d];
        return
        [
            valid.Average(index => mesh.Vertices[index][0]),
            valid.Average(index => mesh.Vertices[index][1]),
            valid.Average(index => mesh.Vertices[index][2])
        ];
    }

    private void ModelSetMeshVertexSelection(int index, bool additive)
    {
        if (!additive) ModelClearMeshComponentSelection();
        if (additive && _modelMeshSelectedVertices.Remove(index)) _modelMeshVertexSelectionOrder.Remove(index);
        else
        {
            _modelMeshSelectedVertices.Add(index);
            _modelMeshVertexSelectionOrder.Remove(index);
            _modelMeshVertexSelectionOrder.Add(index);
            _modelMeshActiveVertex = index;
        }
    }

    private void ModelSetMeshEdgeSelection(ModelMeshEdge edge, bool additive)
    {
        if (!additive) ModelClearMeshComponentSelection();
        if (additive && _modelMeshSelectedEdges.Remove(edge)) _modelMeshActiveEdge = null;
        else
        {
            _modelMeshSelectedEdges.Add(edge);
            _modelMeshActiveEdge = edge;
        }
    }

    private void ModelSetMeshFaceSelection(int index, bool additive)
    {
        if (!additive) ModelClearMeshComponentSelection();
        if (additive && _modelMeshSelectedFaces.Remove(index)) _modelMeshActiveFace = -1;
        else
        {
            _modelMeshSelectedFaces.Add(index);
            _modelMeshActiveFace = index;
            _modelSelectedFace = index;
        }
    }

    private static ModelNonCuboidData ModelCreateBoxMesh(double[] from, double[] to, string texture)
    {
        double x0 = Math.Min(from[0], to[0]), y0 = Math.Min(from[1], to[1]), z0 = Math.Min(from[2], to[2]);
        double x1 = Math.Max(from[0], to[0]), y1 = Math.Max(from[1], to[1]), z1 = Math.Max(from[2], to[2]);
        ModelNonCuboidData mesh = new()
        {
            Vertices =
            [
                [x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0],
                [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1]
            ]
        };
        int[][] faces =
        [
            [3, 2, 1, 0], [1, 2, 6, 5], [5, 6, 7, 4],
            [4, 7, 3, 0], [7, 6, 2, 3], [0, 1, 5, 4]
        ];
        foreach (int[] indices in faces)
        {
            mesh.Faces.Add(new ModelMeshFaceData
            {
                Vertices = indices,
                Texture = string.IsNullOrWhiteSpace(texture) ? "all" : texture.TrimStart('#'),
                Uv = [[0f, 0f], [16f, 0f], [16f, 16f], [0f, 16f]]
            });
        }
        return mesh;
    }

    private static ModelNonCuboidData ModelCreatePlaneMesh(bool triangle, string texture)
    {
        ModelNonCuboidData mesh = new();
        mesh.Vertices.AddRange(triangle
            ? [[0d, 0d, 0d], [16d, 0d, 0d], [0d, 16d, 0d]]
            : [[0d, 0d, 0d], [16d, 0d, 0d], [16d, 16d, 0d], [0d, 16d, 0d]]);
        mesh.Faces.Add(new ModelMeshFaceData
        {
            Vertices = triangle ? [0, 1, 2] : [0, 1, 2, 3],
            Texture = string.IsNullOrWhiteSpace(texture) ? "all" : texture.TrimStart('#'),
            Uv = triangle ? [[0f, 16f], [16f, 16f], [0f, 0f]] : [[0f, 16f], [16f, 16f], [16f, 0f], [0f, 0f]]
        });
        return mesh;
    }

    private void ModelAddMeshElement(ModelElementData? parent, string kind)
    {
        if (_modelDoc == null) return;
        string texture = _modelDoc.Textures.FirstOrDefault()?.Code ?? "all";
        ModelElementData element = new()
        {
            Name = ModelGenerateElementName(kind),
            From = [0d, 0d, 0d],
            To = [16d, 16d, 16d],
            RotationOrigin = [8d, 8d, 8d],
            Parent = parent,
            NonCuboid = kind switch
            {
                "Triangle" => ModelCreatePlaneMesh(true, texture),
                "Quad" => ModelCreatePlaneMesh(false, texture),
                _ => ModelCreateBoxMesh([0d, 0d, 0d], [16d, 16d, 16d], texture)
            }
        };
        if (kind is "Triangle" or "Quad")
        {
            element.To = [16d, 16d, 0d];
            element.RotationOrigin = [8d, 8d, 0d];
        }
        ModelBeginEdit();
        (parent?.Children ?? _modelDoc.Roots).Add(element);
        ModelSelectElement(element);
        ModelClearMeshComponentSelection();
        ModelMarkChanged();
        ModelEndEdit("Add MeshLib element");
        _modelStatus = $"Added MeshLib {kind.ToLowerInvariant()} {element.Name}.";
    }

    private void ModelConvertSelectedCuboidToMesh()
    {
        ModelElementData? element = _modelSelectedElement;
        if (element == null || element.NonCuboid != null || !ModelElementHasRenderableBox(element)) return;
        string texture = ModelBestElementTexture(element);
        ModelNonCuboidData mesh = ModelCreateBoxMesh(element.From, element.To, texture);
        for (int faceIndex = 0; faceIndex < Math.Min(6, mesh.Faces.Count); faceIndex++)
        {
            ModelFaceData? source = element.Faces[faceIndex];
            if (source == null) continue;
            mesh.Faces[faceIndex].Texture = source.Texture;
            mesh.Faces[faceIndex].Glow = source.Glow;
            mesh.Faces[faceIndex].Uv =
            [
                [source.Uv[0], source.Uv[1]], [source.Uv[2], source.Uv[1]],
                [source.Uv[2], source.Uv[3]], [source.Uv[0], source.Uv[3]]
            ];
        }
        ModelBeginEdit();
        element.NonCuboid = mesh;
        Array.Clear(element.Faces);
        ModelClearMeshComponentSelection();
        ModelMarkChanged();
        ModelEndEdit("Convert cuboid to MeshLib mesh");
        _modelStatus = $"Converted {element.Name} to a MeshLib mesh.";
    }

    private void ModelConvertSelectedMeshToCuboid()
    {
        ModelElementData? element = _modelSelectedElement;
        if (element?.NonCuboid?.Editable != true || !ModelTryGetMeshBounds(element, out double[] min, out double[] max)) return;
        ModelNonCuboidData mesh = element.NonCuboid;
        if (mesh.Vertices.Count != 8 || mesh.Faces.Count != 6 || mesh.Faces.Any(face => face.Vertices.Length != 4) ||
            mesh.Vertices.Any(vertex => vertex.Length != 3 ||
                (Math.Abs(vertex[0] - min[0]) > 0.000001 && Math.Abs(vertex[0] - max[0]) > 0.000001) ||
                (Math.Abs(vertex[1] - min[1]) > 0.000001 && Math.Abs(vertex[1] - max[1]) > 0.000001) ||
                (Math.Abs(vertex[2] - min[2]) > 0.000001 && Math.Abs(vertex[2] - max[2]) > 0.000001)))
        {
            _modelStatus = "Mesh can convert to a cuboid only when it is an exact axis-aligned eight-vertex box.";
            return;
        }

        ModelBeginEdit();
        element.From = min;
        element.To = max;
        for (int i = 0; i < 6; i++)
        {
            ModelMeshFaceData source = mesh.Faces[i];
            float[] uv = source.Uv is { Count: >= 3 }
                ? [source.Uv.Min(value => value[0]), source.Uv.Min(value => value[1]), source.Uv.Max(value => value[0]), source.Uv.Max(value => value[1])]
                : [0f, 0f, 16f, 16f];
            element.Faces[i] = new ModelFaceData { Texture = source.Texture, Glow = source.Glow, Uv = uv };
        }
        element.NonCuboid = null;
        ModelClearMeshComponentSelection();
        ModelMarkChanged();
        ModelEndEdit("Convert MeshLib mesh to cuboid");
        _modelStatus = $"Converted {element.Name} to a cuboid.";
    }

    private bool ModelMeshLibIsOperational(out string status)
    {
        status = "MeshLib is not installed.";
        try
        {
            ModSystem? system = _api.ModLoader.GetModSystem("MeshLib.MeshLibModSystem");
            if (system == null) return false;
            object? meshApi = system.GetType().GetProperty("Api", BindingFlags.Instance | BindingFlags.Public)?.GetValue(system);
            if (meshApi == null)
            {
                status = "MeshLib is installed, but its public API is unavailable.";
                return false;
            }
            bool operational = meshApi.GetType().GetProperty("IsOperational", BindingFlags.Instance | BindingFlags.Public)?.GetValue(meshApi) as bool? == true;
            status = operational ? "MeshLib runtime patches are operational." : "MeshLib is installed, but its runtime patches are not operational.";
            return operational;
        }
        catch (Exception exception)
        {
            status = $"MeshLib API lookup failed: {exception.Message}";
            return false;
        }
    }

    private bool ModelTryAttachMeshLibShape(Shape shape, string json, string label, out string status)
    {
        status = "";
        if (!json.Contains("\"noncuboid\"", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            ModSystem? system = _api.ModLoader.GetModSystem("MeshLib.MeshLibModSystem");
            if (system == null)
            {
                status = "MeshLib is not installed; custom geometry is shown as an editor wireframe only.";
                return false;
            }
            object? meshApi = system.GetType().GetProperty("Api", BindingFlags.Instance | BindingFlags.Public)?.GetValue(system);
            if (meshApi == null)
            {
                status = "MeshLib public API is unavailable.";
                return false;
            }
            if (meshApi.GetType().GetProperty("IsOperational", BindingFlags.Instance | BindingFlags.Public)?.GetValue(meshApi) as bool? != true)
            {
                status = "MeshLib is installed but not operational.";
                return false;
            }
            MethodInfo? attach = meshApi.GetType().GetMethod("AttachShape", BindingFlags.Instance | BindingFlags.Public, [typeof(Shape), typeof(IAsset)]);
            if (attach == null)
            {
                status = "MeshLib AttachShape API was not found.";
                return false;
            }
            Asset asset = new(Encoding.UTF8.GetBytes(json), new AssetLocation("ingamedevtools", $"shapes/preview/{ModelSanitizeFileName(label)}.json"), null!);
            int attached = Convert.ToInt32(attach.Invoke(meshApi, [shape, asset]));
            status = attached > 0 ? $"MeshLib attached {attached} custom element(s)." : "MeshLib found no valid custom elements to attach.";
            return true;
        }
        catch (Exception exception)
        {
            status = $"MeshLib preview attachment failed: {exception.GetBaseException().Message}";
            return false;
        }
    }
}
