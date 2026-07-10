using Vintagestory.API.MathTools;
using NVector3 = System.Numerics.Vector3;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private void ModelCreateFaceFromSelectedVertices()
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        if (mesh?.Editable != true) return;
        int[] vertices = _modelMeshVertexSelectionOrder.Where(_modelMeshSelectedVertices.Contains).Distinct().ToArray();
        if (vertices.Length is not (3 or 4))
        {
            _modelStatus = "Select exactly three or four vertices in winding order.";
            return;
        }
        ModelMeshFaceData face = new()
        {
            Vertices = vertices,
            Texture = _modelSelectedTextureCode,
            Uv = vertices.Length == 3
                ? [[0f, 16f], [16f, 16f], [0f, 0f]]
                : [[0f, 16f], [16f, 16f], [16f, 0f], [0f, 0f]]
        };
        ModelBeginEdit();
        mesh.Faces.Add(face);
        ModelClearMeshComponentSelection();
        _modelMeshSelectionMode = ModelMeshSelectionMode.Face;
        _modelMeshSelectedFaces.Add(mesh.Faces.Count - 1);
        _modelMeshActiveFace = mesh.Faces.Count - 1;
        ModelMarkChanged();
        ModelEndEdit("Create mesh face");
    }

    private void ModelDeleteSelectedMeshComponents()
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        if (mesh?.Editable != true) return;
        ModelBeginEdit();
        int removed;
        if (_modelMeshSelectionMode == ModelMeshSelectionMode.Face)
        {
            removed = _modelMeshSelectedFaces.Count;
            mesh.Faces = mesh.Faces.Where((_, index) => !_modelMeshSelectedFaces.Contains(index)).ToList();
        }
        else if (_modelMeshSelectionMode == ModelMeshSelectionMode.Edge)
        {
            int before = mesh.Faces.Count;
            mesh.Faces = mesh.Faces.Where(face => !ModelFaceContainsAnyEdge(face, _modelMeshSelectedEdges)).ToList();
            removed = before - mesh.Faces.Count;
        }
        else
        {
            HashSet<int> removedVertices = [.. _modelMeshSelectedVertices];
            int beforeFaces = mesh.Faces.Count;
            mesh.Faces = mesh.Faces.Where(face => !face.Vertices.Any(removedVertices.Contains)).ToList();
            removed = removedVertices.Count + beforeFaces - mesh.Faces.Count;
            ModelCompactMeshVertices(mesh, removedVertices);
        }
        ModelClearMeshComponentSelection();
        ModelMarkChanged();
        ModelEndEdit("Delete mesh components");
        _modelStatus = $"Deleted {removed} mesh component(s).";
    }

    private static bool ModelFaceContainsAnyEdge(ModelMeshFaceData face, IReadOnlySet<ModelMeshEdge> edges)
    {
        for (int index = 0; index < face.Vertices.Length; index++)
        {
            if (edges.Contains(ModelMeshEdge.Create(face.Vertices[index], face.Vertices[(index + 1) % face.Vertices.Length]))) return true;
        }
        return false;
    }

    private void ModelRemoveUnusedMeshVertices()
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        if (mesh?.Editable != true) return;
        HashSet<int> used = mesh.Faces.SelectMany(face => face.Vertices).ToHashSet();
        HashSet<int> remove = Enumerable.Range(0, mesh.Vertices.Count).Where(index => !used.Contains(index)).ToHashSet();
        if (remove.Count == 0)
        {
            _modelStatus = "Mesh has no unused vertices.";
            return;
        }
        ModelBeginEdit();
        ModelCompactMeshVertices(mesh, remove);
        ModelClearMeshComponentSelection();
        ModelMarkChanged();
        ModelEndEdit("Remove unused mesh vertices");
        _modelStatus = $"Removed {remove.Count} unused vertex/vertices.";
    }

    private static void ModelCompactMeshVertices(ModelNonCuboidData mesh, IReadOnlySet<int> remove)
    {
        int[] map = new int[mesh.Vertices.Count];
        List<double[]> compact = [];
        for (int index = 0; index < mesh.Vertices.Count; index++)
        {
            if (remove.Contains(index))
            {
                map[index] = -1;
                continue;
            }
            map[index] = compact.Count;
            compact.Add(mesh.Vertices[index]);
        }
        foreach (ModelMeshFaceData face in mesh.Faces)
        {
            for (int index = 0; index < face.Vertices.Length; index++) face.Vertices[index] = map[face.Vertices[index]];
        }
        mesh.Vertices = compact;
    }

    private void ModelWeldSelectedMeshVertices(float tolerance)
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        if (mesh?.Editable != true || _modelMeshSelectedVertices.Count < 2) return;
        tolerance = Math.Max(0f, tolerance);
        int[] selected = _modelMeshSelectedVertices.Order().ToArray();
        int[] remap = Enumerable.Range(0, mesh.Vertices.Count).ToArray();
        int welded = 0;
        for (int left = 0; left < selected.Length; left++)
        {
            int a = selected[left];
            if (remap[a] != a) continue;
            for (int right = left + 1; right < selected.Length; right++)
            {
                int b = selected[right];
                if (remap[b] != b || ModelDistance(mesh.Vertices[a], mesh.Vertices[b]) > tolerance) continue;
                remap[b] = a;
                welded++;
            }
        }
        if (welded == 0)
        {
            _modelStatus = "No selected vertices were within the weld tolerance.";
            return;
        }

        ModelBeginEdit();
        foreach (ModelMeshFaceData face in mesh.Faces)
        {
            for (int index = 0; index < face.Vertices.Length; index++) face.Vertices[index] = remap[face.Vertices[index]];
        }
        mesh.Faces.RemoveAll(face => face.Vertices.Distinct().Count() < 3 ||
            face.Vertices.Length is not (3 or 4) ||
            ModelMeshTriangleDegenerate(mesh, face.Vertices[0], face.Vertices[1], face.Vertices[2]) ||
            (face.Vertices.Length == 4 && ModelMeshTriangleDegenerate(mesh, face.Vertices[0], face.Vertices[2], face.Vertices[3])));
        HashSet<int> remove = Enumerable.Range(0, remap.Length).Where(index => remap[index] != index).ToHashSet();
        ModelCompactMeshVertices(mesh, remove);
        ModelClearMeshComponentSelection();
        ModelMarkChanged();
        ModelEndEdit("Weld mesh vertices");
        _modelStatus = $"Welded {welded} vertex/vertices.";
    }

    private static double ModelDistance(double[] a, double[] b)
    {
        if (a.Length < 3 || b.Length < 3) return double.PositiveInfinity;
        double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private void ModelReverseSelectedMeshFaces()
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        if (mesh?.Editable != true || _modelMeshSelectedFaces.Count == 0) return;
        ModelBeginEdit();
        foreach (int index in _modelMeshSelectedFaces)
        {
            if (index < 0 || index >= mesh.Faces.Count) continue;
            Array.Reverse(mesh.Faces[index].Vertices);
            mesh.Faces[index].Uv?.Reverse();
        }
        ModelMarkChanged();
        ModelEndEdit("Reverse mesh face winding");
    }

    private void ModelMakeSelectedMeshFacesDoubleSided()
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        if (mesh?.Editable != true || _modelMeshSelectedFaces.Count == 0) return;
        ModelBeginEdit();
        List<ModelMeshFaceData> reversed = [];
        foreach (int index in _modelMeshSelectedFaces.Order())
        {
            if (index < 0 || index >= mesh.Faces.Count) continue;
            ModelMeshFaceData face = mesh.Faces[index].Clone();
            Array.Reverse(face.Vertices);
            face.Uv?.Reverse();
            reversed.Add(face);
        }
        int start = mesh.Faces.Count;
        mesh.Faces.AddRange(reversed);
        ModelClearMeshComponentSelection();
        _modelMeshSelectionMode = ModelMeshSelectionMode.Face;
        for (int index = start; index < mesh.Faces.Count; index++) _modelMeshSelectedFaces.Add(index);
        _modelMeshActiveFace = mesh.Faces.Count - 1;
        ModelMarkChanged();
        ModelEndEdit("Make mesh faces double-sided");
    }

    private void ModelDuplicateSelectedMeshFaces()
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        if (mesh?.Editable != true || _modelMeshSelectedFaces.Count == 0) return;
        ModelBeginEdit();
        List<ModelMeshFaceData> copies = _modelMeshSelectedFaces.Order().Where(index => index >= 0 && index < mesh.Faces.Count).Select(index => mesh.Faces[index].Clone()).ToList();
        int start = mesh.Faces.Count;
        mesh.Faces.AddRange(copies);
        ModelClearMeshComponentSelection();
        _modelMeshSelectionMode = ModelMeshSelectionMode.Face;
        for (int index = start; index < mesh.Faces.Count; index++) _modelMeshSelectedFaces.Add(index);
        _modelMeshActiveFace = mesh.Faces.Count - 1;
        ModelMarkChanged();
        ModelEndEdit("Duplicate mesh faces");
    }

    private void ModelSubdivideSelectedMeshComponents()
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        if (mesh?.Editable != true) return;
        if (_modelMeshSelectionMode == ModelMeshSelectionMode.Face)
        {
            ModelSubdivideMeshFaces(mesh, _modelMeshSelectedFaces);
        }
        else if (_modelMeshSelectionMode == ModelMeshSelectionMode.Edge)
        {
            ModelSubdivideMeshEdges(mesh, _modelMeshSelectedEdges);
        }
        else
        {
            _modelStatus = "Subdivide works on selected edges or faces.";
        }
    }

    private void ModelSubdivideMeshFaces(ModelNonCuboidData mesh, IReadOnlySet<int> selectedFaces)
    {
        if (selectedFaces.Count == 0) return;
        ModelBeginEdit();
        Dictionary<ModelMeshEdge, int> midpoints = [];
        List<ModelMeshFaceData> output = [];
        HashSet<int> newSelection = [];
        for (int faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
        {
            ModelMeshFaceData face = mesh.Faces[faceIndex];
            if (!selectedFaces.Contains(faceIndex) || face.Vertices.Length is not (3 or 4))
            {
                output.Add(face);
                continue;
            }
            int[] midpoint = new int[face.Vertices.Length];
            for (int edgeIndex = 0; edgeIndex < face.Vertices.Length; edgeIndex++)
            {
                int a = face.Vertices[edgeIndex], b = face.Vertices[(edgeIndex + 1) % face.Vertices.Length];
                midpoint[edgeIndex] = ModelGetOrCreateMeshMidpoint(mesh, midpoints, a, b);
            }
            if (face.Vertices.Length == 3)
            {
                int a = face.Vertices[0], b = face.Vertices[1], c = face.Vertices[2];
                int ab = midpoint[0], bc = midpoint[1], ca = midpoint[2];
                Add([a, ab, ca], ModelUvTriangle(face, 0, 0, 2, true));
                Add([ab, b, bc], ModelUvTriangle(face, 0, 1, 1, true));
                Add([ca, bc, c], ModelUvTriangle(face, 2, 1, 2, true));
                Add([ab, bc, ca], ModelUvMidTriangle(face));
            }
            else
            {
                int center = mesh.Vertices.Count;
                mesh.Vertices.Add(ModelAverageVertices(mesh, face.Vertices));
                for (int corner = 0; corner < 4; corner++)
                {
                    int previousEdge = (corner + 3) % 4;
                    int[] vertices = [face.Vertices[corner], midpoint[corner], center, midpoint[previousEdge]];
                    Add(vertices, ModelUvQuadSubdivision(face, corner));
                }
            }

            void Add(int[] vertices, List<float[]>? uv)
            {
                ModelMeshFaceData next = face.Clone();
                next.Vertices = vertices;
                next.Uv = uv;
                newSelection.Add(output.Count);
                output.Add(next);
            }
        }
        mesh.Faces = output;
        ModelClearMeshComponentSelection();
        _modelMeshSelectionMode = ModelMeshSelectionMode.Face;
        _modelMeshSelectedFaces.UnionWith(newSelection);
        _modelMeshActiveFace = newSelection.LastOrDefault(-1);
        ModelMarkChanged();
        ModelEndEdit("Subdivide mesh faces");
    }

    private void ModelSubdivideMeshEdges(ModelNonCuboidData mesh, IReadOnlySet<ModelMeshEdge> selectedEdges)
    {
        if (selectedEdges.Count == 0) return;
        ModelBeginEdit();
        Dictionary<ModelMeshEdge, int> midpoints = [];
        foreach (ModelMeshEdge edge in selectedEdges) ModelGetOrCreateMeshMidpoint(mesh, midpoints, edge.A, edge.B);
        List<ModelMeshFaceData> output = [];
        HashSet<int> newSelection = [];
        foreach (ModelMeshFaceData face in mesh.Faces)
        {
            bool affected = false;
            List<int> polygon = [];
            List<float[]>? polygonUv = face.Uv == null ? null : [];
            for (int corner = 0; corner < face.Vertices.Length; corner++)
            {
                int a = face.Vertices[corner];
                int b = face.Vertices[(corner + 1) % face.Vertices.Length];
                polygon.Add(a);
                polygonUv?.Add((float[])face.Uv![corner].Clone());
                ModelMeshEdge edge = ModelMeshEdge.Create(a, b);
                if (!selectedEdges.Contains(edge)) continue;
                affected = true;
                polygon.Add(midpoints[edge]);
                if (polygonUv != null) polygonUv.Add(ModelAverageUv(face.Uv![corner], face.Uv![(corner + 1) % face.Vertices.Length]));
            }
            if (!affected)
            {
                output.Add(face);
                continue;
            }
            int center = mesh.Vertices.Count;
            mesh.Vertices.Add(ModelAverageVertices(mesh, polygon));
            float[]? centerUv = polygonUv == null ? null : ModelAverageUv(polygonUv);
            for (int corner = 0; corner < polygon.Count; corner++)
            {
                ModelMeshFaceData triangle = face.Clone();
                triangle.Vertices = [polygon[corner], polygon[(corner + 1) % polygon.Count], center];
                triangle.Uv = polygonUv == null ? null : [(float[])polygonUv[corner].Clone(), (float[])polygonUv[(corner + 1) % polygon.Count].Clone(), (float[])centerUv!.Clone()];
                newSelection.Add(output.Count);
                output.Add(triangle);
            }
        }
        mesh.Faces = output;
        ModelClearMeshComponentSelection();
        _modelMeshSelectionMode = ModelMeshSelectionMode.Face;
        _modelMeshSelectedFaces.UnionWith(newSelection);
        _modelMeshActiveFace = newSelection.LastOrDefault(-1);
        ModelMarkChanged();
        ModelEndEdit("Subdivide mesh edges");
    }

    private static int ModelGetOrCreateMeshMidpoint(ModelNonCuboidData mesh, Dictionary<ModelMeshEdge, int> midpoints, int a, int b)
    {
        ModelMeshEdge edge = ModelMeshEdge.Create(a, b);
        if (midpoints.TryGetValue(edge, out int existing)) return existing;
        int index = mesh.Vertices.Count;
        mesh.Vertices.Add(ModelAverageVertices(mesh, [a, b]));
        midpoints[edge] = index;
        return index;
    }

    private static double[] ModelAverageVertices(ModelNonCuboidData mesh, IEnumerable<int> indices)
    {
        int[] values = indices.Where(index => index >= 0 && index < mesh.Vertices.Count && mesh.Vertices[index].Length >= 3).ToArray();
        if (values.Length == 0) return [0d, 0d, 0d];
        return [values.Average(index => mesh.Vertices[index][0]), values.Average(index => mesh.Vertices[index][1]), values.Average(index => mesh.Vertices[index][2])];
    }

    private static float[] ModelAverageUv(float[] a, float[] b)
    {
        return [(a.ElementAtOrDefault(0) + b.ElementAtOrDefault(0)) * 0.5f, (a.ElementAtOrDefault(1) + b.ElementAtOrDefault(1)) * 0.5f];
    }

    private static float[] ModelAverageUv(IEnumerable<float[]> values)
    {
        float[][] list = values.ToArray();
        return list.Length == 0 ? [0f, 0f] : [list.Average(value => value.ElementAtOrDefault(0)), list.Average(value => value.ElementAtOrDefault(1))];
    }

    private static List<float[]>? ModelUvTriangle(ModelMeshFaceData face, int a, int b, int c, bool midpoint)
    {
        if (face.Uv == null || face.Uv.Count < 3) return null;
        float[] ua = face.Uv[a], ub = face.Uv[b], uc = face.Uv[c];
        if (!midpoint) return [(float[])ua.Clone(), (float[])ub.Clone(), (float[])uc.Clone()];
        return [(float[])ua.Clone(), ModelAverageUv(ua, ub), ModelAverageUv(ua, uc)];
    }

    private static List<float[]>? ModelUvMidTriangle(ModelMeshFaceData face)
    {
        if (face.Uv == null || face.Uv.Count < 3) return null;
        return [ModelAverageUv(face.Uv[0], face.Uv[1]), ModelAverageUv(face.Uv[1], face.Uv[2]), ModelAverageUv(face.Uv[2], face.Uv[0])];
    }

    private static List<float[]>? ModelUvQuadSubdivision(ModelMeshFaceData face, int corner)
    {
        if (face.Uv == null || face.Uv.Count < 4) return null;
        int next = (corner + 1) % 4, previous = (corner + 3) % 4;
        return [(float[])face.Uv[corner].Clone(), ModelAverageUv(face.Uv[corner], face.Uv[next]), ModelAverageUv(face.Uv), ModelAverageUv(face.Uv[previous], face.Uv[corner])];
    }

    private void ModelExtrudeSelectedMeshFaces(float distance)
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        if (mesh?.Editable != true || _modelMeshSelectedFaces.Count == 0) return;
        if (!ModelTryBuildMeshFaceRegions(mesh, _modelMeshSelectedFaces, out List<HashSet<int>> regions, out string error))
        {
            _modelStatus = error;
            return;
        }
        ModelBeginEdit();
        foreach (HashSet<int> region in regions)
        {
            NVector3 normal = ModelMeshRegionNormal(mesh, region);
            if (normal.LengthSquared() < 0.0000001f) continue;
            Dictionary<int, int> duplicate = [];
            foreach (int vertexIndex in region.SelectMany(index => mesh.Faces[index].Vertices).Distinct())
            {
                double[] source = mesh.Vertices[vertexIndex];
                duplicate[vertexIndex] = mesh.Vertices.Count;
                mesh.Vertices.Add([source[0] + normal.X * distance, source[1] + normal.Y * distance, source[2] + normal.Z * distance]);
            }
            List<(int A, int B, ModelMeshFaceData Face)> boundary = ModelMeshRegionBoundary(mesh, region);
            foreach (int faceIndex in region)
            {
                ModelMeshFaceData face = mesh.Faces[faceIndex];
                face.Vertices = face.Vertices.Select(index => duplicate[index]).ToArray();
            }
            foreach ((int a, int b, ModelMeshFaceData sourceFace) in boundary)
            {
                mesh.Faces.Add(new ModelMeshFaceData
                {
                    Vertices = [a, b, duplicate[b], duplicate[a]],
                    Texture = sourceFace.Texture,
                    Glow = sourceFace.Glow,
                    Shade = sourceFace.Shade,
                    Uv = [[0f, 0f], [16f, 0f], [16f, Math.Abs(distance)], [0f, Math.Abs(distance)]]
                });
            }
        }
        ModelMarkChanged();
        ModelEndEdit("Extrude mesh face region");
        _modelStatus = $"Extruded {_modelMeshSelectedFaces.Count} face(s) by {distance:0.###} units.";
    }

    private void ModelInsetSelectedMeshFaces(float fraction)
    {
        ModelNonCuboidData? mesh = _modelSelectedElement?.NonCuboid;
        if (mesh?.Editable != true || _modelMeshSelectedFaces.Count == 0) return;
        fraction = Math.Clamp(fraction, 0.01f, 0.95f);
        if (!ModelTryBuildMeshFaceRegions(mesh, _modelMeshSelectedFaces, out List<HashSet<int>> regions, out string error))
        {
            _modelStatus = error;
            return;
        }
        foreach (HashSet<int> region in regions)
        {
            if (!ModelMeshRegionIsCoplanar(mesh, region) || !ModelMeshBoundaryIsSingleLoop(ModelMeshRegionBoundary(mesh, region)))
            {
                _modelStatus = "Inset requires each selected region to be coplanar with one simple boundary loop and no holes.";
                return;
            }
        }

        ModelBeginEdit();
        foreach (HashSet<int> region in regions)
        {
            int[] regionVertices = region.SelectMany(index => mesh.Faces[index].Vertices).Distinct().ToArray();
            double[] center = ModelAverageVertices(mesh, regionVertices);
            Dictionary<int, int> inset = [];
            foreach (int vertexIndex in regionVertices)
            {
                double[] source = mesh.Vertices[vertexIndex];
                inset[vertexIndex] = mesh.Vertices.Count;
                mesh.Vertices.Add
                ([
                    center[0] + (source[0] - center[0]) * (1d - fraction),
                    center[1] + (source[1] - center[1]) * (1d - fraction),
                    center[2] + (source[2] - center[2]) * (1d - fraction)
                ]);
            }
            List<(int A, int B, ModelMeshFaceData Face)> boundary = ModelMeshRegionBoundary(mesh, region);
            foreach (int faceIndex in region)
            {
                ModelMeshFaceData face = mesh.Faces[faceIndex];
                face.Vertices = face.Vertices.Select(index => inset[index]).ToArray();
            }
            foreach ((int a, int b, ModelMeshFaceData sourceFace) in boundary)
            {
                mesh.Faces.Add(new ModelMeshFaceData
                {
                    Vertices = [a, b, inset[b], inset[a]],
                    Texture = sourceFace.Texture,
                    Glow = sourceFace.Glow,
                    Shade = sourceFace.Shade,
                    Uv = [[0f, 0f], [16f, 0f], [16f, 16f], [0f, 16f]]
                });
            }
        }
        ModelMarkChanged();
        ModelEndEdit("Inset mesh face region");
        _modelStatus = $"Inset {_modelMeshSelectedFaces.Count} face(s) by {fraction:P0}.";
    }

    private static bool ModelTryBuildMeshFaceRegions(ModelNonCuboidData mesh, IReadOnlySet<int> selection, out List<HashSet<int>> regions, out string error)
    {
        regions = [];
        error = "";
        HashSet<int> remaining = selection.Where(index => index >= 0 && index < mesh.Faces.Count).ToHashSet();
        Dictionary<ModelMeshEdge, List<int>> byEdge = [];
        foreach (int faceIndex in remaining)
        {
            ModelMeshFaceData face = mesh.Faces[faceIndex];
            for (int edgeIndex = 0; edgeIndex < face.Vertices.Length; edgeIndex++)
            {
                ModelMeshEdge edge = ModelMeshEdge.Create(face.Vertices[edgeIndex], face.Vertices[(edgeIndex + 1) % face.Vertices.Length]);
                if (!byEdge.TryGetValue(edge, out List<int>? owners)) byEdge[edge] = owners = [];
                owners.Add(faceIndex);
                if (owners.Count > 2)
                {
                    error = "Selected region contains a non-manifold edge shared by more than two selected faces.";
                    return false;
                }
            }
        }
        while (remaining.Count > 0)
        {
            int seed = remaining.First();
            HashSet<int> region = [];
            Queue<int> queue = new();
            queue.Enqueue(seed);
            remaining.Remove(seed);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                region.Add(current);
                ModelMeshFaceData face = mesh.Faces[current];
                for (int edgeIndex = 0; edgeIndex < face.Vertices.Length; edgeIndex++)
                {
                    ModelMeshEdge edge = ModelMeshEdge.Create(face.Vertices[edgeIndex], face.Vertices[(edgeIndex + 1) % face.Vertices.Length]);
                    foreach (int neighbor in byEdge[edge]) if (remaining.Remove(neighbor)) queue.Enqueue(neighbor);
                }
            }
            regions.Add(region);
        }
        return regions.Count > 0;
    }

    private static List<(int A, int B, ModelMeshFaceData Face)> ModelMeshRegionBoundary(ModelNonCuboidData mesh, IReadOnlySet<int> region)
    {
        Dictionary<ModelMeshEdge, List<(int A, int B, ModelMeshFaceData Face)>> occurrences = [];
        foreach (int faceIndex in region)
        {
            ModelMeshFaceData face = mesh.Faces[faceIndex];
            for (int index = 0; index < face.Vertices.Length; index++)
            {
                int a = face.Vertices[index], b = face.Vertices[(index + 1) % face.Vertices.Length];
                ModelMeshEdge edge = ModelMeshEdge.Create(a, b);
                if (!occurrences.TryGetValue(edge, out List<(int, int, ModelMeshFaceData)>? list)) occurrences[edge] = list = [];
                list.Add((a, b, face));
            }
        }
        return occurrences.Values.Where(values => values.Count == 1).Select(values => values[0]).ToList();
    }

    private static bool ModelMeshBoundaryIsSingleLoop(IReadOnlyList<(int A, int B, ModelMeshFaceData Face)> boundary)
    {
        if (boundary.Count < 3) return false;
        Dictionary<int, int> degree = [];
        foreach ((int a, int b, _) in boundary)
        {
            degree[a] = degree.GetValueOrDefault(a) + 1;
            degree[b] = degree.GetValueOrDefault(b) + 1;
        }
        return degree.Values.All(value => value == 2) && degree.Count == boundary.Count;
    }

    private static NVector3 ModelMeshRegionNormal(ModelNonCuboidData mesh, IEnumerable<int> region)
    {
        NVector3 normal = NVector3.Zero;
        foreach (int faceIndex in region) normal += ModelMeshFaceNormal(mesh, mesh.Faces[faceIndex]);
        return normal.LengthSquared() < 0.0000001f ? NVector3.Zero : NVector3.Normalize(normal);
    }

    private static NVector3 ModelMeshFaceNormal(ModelNonCuboidData mesh, ModelMeshFaceData face)
    {
        if (face.Vertices.Length < 3 || face.Vertices.Any(index => index < 0 || index >= mesh.Vertices.Count)) return NVector3.Zero;
        double[] a = mesh.Vertices[face.Vertices[0]], b = mesh.Vertices[face.Vertices[1]], c = mesh.Vertices[face.Vertices[2]];
        if (a.Length < 3 || b.Length < 3 || c.Length < 3) return NVector3.Zero;
        NVector3 u = new((float)(b[0] - a[0]), (float)(b[1] - a[1]), (float)(b[2] - a[2]));
        NVector3 v = new((float)(c[0] - a[0]), (float)(c[1] - a[1]), (float)(c[2] - a[2]));
        return NVector3.Cross(u, v);
    }

    private static bool ModelMeshRegionIsCoplanar(ModelNonCuboidData mesh, IReadOnlySet<int> region)
    {
        if (region.Count == 0) return false;
        ModelMeshFaceData first = mesh.Faces[region.First()];
        NVector3 normal = ModelMeshFaceNormal(mesh, first);
        if (normal.LengthSquared() < 0.0000001f) return false;
        normal = NVector3.Normalize(normal);
        double[] origin = mesh.Vertices[first.Vertices[0]];
        foreach (int faceIndex in region)
        {
            NVector3 candidate = ModelMeshFaceNormal(mesh, mesh.Faces[faceIndex]);
            if (candidate.LengthSquared() < 0.0000001f || Math.Abs(NVector3.Dot(normal, NVector3.Normalize(candidate))) < 0.9999f) return false;
            foreach (int vertexIndex in mesh.Faces[faceIndex].Vertices)
            {
                double[] point = mesh.Vertices[vertexIndex];
                double distance = (point[0] - origin[0]) * normal.X + (point[1] - origin[1]) * normal.Y + (point[2] - origin[2]) * normal.Z;
                if (Math.Abs(distance) > 0.0001) return false;
            }
        }
        return true;
    }

    private void ModelAutoUvSelectedMeshFaces()
    {
        ModelElementData? element = _modelSelectedElement;
        ModelNonCuboidData? mesh = element?.NonCuboid;
        if (element == null || mesh?.Editable != true) return;
        IEnumerable<int> selected = _modelMeshSelectedFaces.Count > 0
            ? _modelMeshSelectedFaces
            : Enumerable.Range(0, mesh.Faces.Count);
        ModelBeginEdit();
        foreach (int faceIndex in selected)
        {
            if (faceIndex < 0 || faceIndex >= mesh.Faces.Count) continue;
            ModelMeshFaceData face = mesh.Faces[faceIndex];
            NVector3 normal = ModelMeshFaceNormal(mesh, face);
            int dropAxis = Math.Abs(normal.X) >= Math.Abs(normal.Y) && Math.Abs(normal.X) >= Math.Abs(normal.Z) ? 0 : Math.Abs(normal.Y) >= Math.Abs(normal.Z) ? 1 : 2;
            (int uAxis, int vAxis) = dropAxis switch { 0 => (2, 1), 1 => (0, 2), _ => (0, 1) };
            (int width, int height) = _modelDoc?.GetTextureSize(face.Texture) ?? (16, 16);
            double minU = face.Vertices.Min(index => mesh.Vertices[index][uAxis]);
            double maxU = face.Vertices.Max(index => mesh.Vertices[index][uAxis]);
            double minV = face.Vertices.Min(index => mesh.Vertices[index][vAxis]);
            double maxV = face.Vertices.Max(index => mesh.Vertices[index][vAxis]);
            double spanU = Math.Max(0.000001, maxU - minU), spanV = Math.Max(0.000001, maxV - minV);
            face.Uv = face.Vertices.Select(index => new[]
            {
                (float)((mesh.Vertices[index][uAxis] - minU) / spanU * width),
                (float)((maxV - mesh.Vertices[index][vAxis]) / spanV * height)
            }).ToList();
        }
        ModelMarkChanged();
        ModelEndEdit("Auto UV mesh faces");
    }
}
