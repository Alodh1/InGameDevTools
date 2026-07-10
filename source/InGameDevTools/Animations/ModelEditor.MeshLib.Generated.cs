#nullable enable

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private enum ModelGeneratedMeshKind
    {
        Box,
        ChamferedBox,
        Tube,
        Ellipsoid,
        Cone,
        ExtrudedContour,
        Wedge,
        Leaf,
        Membrane,
        Dome,
        Ring,
        BoxTube,
        Jewel
    }

    /// <summary>
    /// Transient description of a procedural surface. Generator previews keep their original cuboid bounds
    /// through all sizing/tweak passes, then consume this record into an editable MeshLib payload.
    /// </summary>
    private sealed class ModelGeneratedMeshSpec
    {
        public ModelGeneratedMeshKind Kind = ModelGeneratedMeshKind.ChamferedBox;
        public int Axis;
        public int Sign = 1;
        public int Sides = 8;
        public int Layers = 6;
        public double StartScale = 1d;
        public double EndScale = 1d;
    }

    private sealed class ModelGeneratedMeshBuilder
    {
        private readonly string texture;
        private readonly Func<double, double, double, (double X, double Y, double Z)>? coordinateMap;
        private readonly Dictionary<(long X, long Y, long Z), int> shared = [];

        public ModelGeneratedMeshBuilder(
            string texture,
            Func<double, double, double, (double X, double Y, double Z)>? coordinateMap = null)
        {
            this.texture = string.IsNullOrWhiteSpace(texture) ? "all" : texture.TrimStart('#');
            this.coordinateMap = coordinateMap;
        }

        public ModelNonCuboidData Mesh { get; } = new();

        public int Vertex(double x, double y, double z, bool share = true)
        {
            if (coordinateMap != null) (x, y, z) = coordinateMap(x, y, z);
            x = Round(x);
            y = Round(y);
            z = Round(z);
            (long X, long Y, long Z) key = (
                (long)Math.Round(x * 1_000_000d),
                (long)Math.Round(y * 1_000_000d),
                (long)Math.Round(z * 1_000_000d));
            if (share && shared.TryGetValue(key, out int existing)) return existing;
            int index = Mesh.Vertices.Count;
            Mesh.Vertices.Add([x, y, z]);
            if (share) shared[key] = index;
            return index;
        }

        public void Face(int[] indices, params float[][] uv)
        {
            Mesh.Faces.Add(new ModelMeshFaceData
            {
                Vertices = (int[])indices.Clone(),
                Texture = texture,
                Uv = uv.Length == indices.Length
                    ? uv.Select(value => (float[])value.Clone()).ToList()
                    : DefaultUv(indices.Length)
            });
        }

        public void Quad(int a, int b, int c, int d, float u0 = 0f, float u1 = 16f, float v0 = 0f, float v1 = 16f)
        {
            Face([a, b, c, d], [u0, v1], [u1, v1], [u1, v0], [u0, v0]);
        }

        public void Tri(int a, int b, int c)
        {
            Face([a, b, c], [0f, 16f], [16f, 16f], [8f, 0f]);
        }

        public void FaceOutward(int[] indices, double[] reference, params float[][] uv)
        {
            int[] vertices = (int[])indices.Clone();
            List<float[]> mappedUv = uv.Length == vertices.Length
                ? uv.Select(value => (float[])value.Clone()).ToList()
                : DefaultUv(vertices.Length);
            if (vertices.Length >= 3 && PointsInward(vertices, reference))
            {
                Array.Reverse(vertices);
                mappedUv.Reverse();
            }
            Mesh.Faces.Add(new ModelMeshFaceData
            {
                Vertices = vertices,
                Texture = texture,
                Uv = mappedUv
            });
        }

        private bool PointsInward(int[] indices, double[] reference)
        {
            double[] a = Mesh.Vertices[indices[0]], b = Mesh.Vertices[indices[1]], c = Mesh.Vertices[indices[2]];
            double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
            double vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
            double nx = uy * vz - uz * vy;
            double ny = uz * vx - ux * vz;
            double nz = ux * vy - uy * vx;
            double cx = 0d, cy = 0d, cz = 0d;
            foreach (int index in indices)
            {
                double[] point = Mesh.Vertices[index];
                cx += point[0]; cy += point[1]; cz += point[2];
            }
            double inverse = 1d / indices.Length;
            cx = cx * inverse - reference[0];
            cy = cy * inverse - reference[1];
            cz = cz * inverse - reference[2];
            return nx * cx + ny * cy + nz * cz < 0d;
        }

        private static List<float[]> DefaultUv(int count)
        {
            return count == 3
                ? [[0f, 16f], [16f, 16f], [8f, 0f]]
                : [[0f, 16f], [16f, 16f], [16f, 0f], [0f, 0f]];
        }

        private static double Round(double value)
        {
            return Math.Abs(value) < 0.0000005d ? 0d : Math.Round(value, 6);
        }
    }

    private static ModelGeneratedMeshSpec ModelGeneratedSpec(
        ModelGeneratedMeshKind kind,
        int axis = 0,
        int sign = 1,
        int sides = 8,
        int layers = 6,
        double startScale = 1d,
        double endScale = 1d)
    {
        return new ModelGeneratedMeshSpec
        {
            Kind = kind,
            Axis = Math.Clamp(axis, 0, 2),
            Sign = sign < 0 ? -1 : 1,
            Sides = Math.Clamp(sides, 4, 32),
            Layers = Math.Clamp(layers, 2, 16),
            StartScale = Math.Clamp(startScale, 0d, 1d),
            EndScale = Math.Clamp(endScale, 0d, 1d)
        };
    }

    private static void ModelAssignGeneratedMeshSpecs(
        ModelElementData root,
        Func<ModelElementData, ModelGeneratedMeshSpec?> resolver)
    {
        foreach (ModelElementData element in root.EnumerateSubtree())
        {
            if (ReferenceEquals(element, root)) continue;
            element.GeneratedMeshSpec = resolver(element);
        }
    }

    private bool ModelMaterializeGeneratedMeshes(
        ModelElementData root,
        Func<ModelElementData, bool>? include,
        out string error)
    {
        error = "";
        foreach (ModelElementData element in root.EnumerateSubtree())
        {
            if (ReferenceEquals(element, root) || include?.Invoke(element) == false)
            {
                element.GeneratedMeshSpec = null;
                continue;
            }
            if (element.SizeX <= 0.0001d || element.SizeY <= 0.0001d || element.SizeZ <= 0.0001d)
            {
                element.GeneratedMeshSpec = null;
                continue;
            }

            ModelGeneratedMeshSpec spec = element.GeneratedMeshSpec ?? ModelGeneratedSpec(ModelGeneratedMeshKind.ChamferedBox);
            string texture = ModelBestElementTexture(element);
            if (string.IsNullOrWhiteSpace(texture)) texture = _modelDoc?.Textures.FirstOrDefault()?.Code ?? "all";
            ModelNonCuboidData mesh;
            try
            {
                mesh = ModelBuildGeneratedMesh(element, spec, texture);
            }
            catch (Exception exception)
            {
                error = $"Mesh generation failed for {element.Name} ({spec.Kind}): {exception.Message}";
                return false;
            }

            List<string> errors = ModelValidateNonCuboid(mesh);
            if (errors.Count > 0)
            {
                error = $"Mesh generation failed for {element.Name} ({spec.Kind}): {errors[0]}";
                return false;
            }
            if (!ModelGeneratedMeshMatchesBounds(element, mesh))
            {
                error = $"Mesh generation failed for {element.Name} ({spec.Kind}): generated bounds do not match the element From/To bounds.";
                return false;
            }

            int glow = element.Faces.Where(face => face != null).Select(face => face!.Glow).DefaultIfEmpty().Max();
            foreach (ModelMeshFaceData face in mesh.Faces)
            {
                face.Texture = texture.TrimStart('#');
                face.Glow = glow;
                face.Shade = element.Shade;
            }
            element.NonCuboid = mesh;
            Array.Clear(element.Faces);
            element.GeneratedMeshSpec = null;
        }
        return true;
    }

    private static (int Vertices, int Faces) ModelGeneratedMeshCounts(ModelElementData? root)
    {
        if (root == null) return (0, 0);
        int vertices = 0, faces = 0;
        foreach (ModelElementData element in root.EnumerateSubtree())
        {
            if (element.NonCuboid?.Editable != true) continue;
            vertices += element.NonCuboid.Vertices.Count;
            faces += element.NonCuboid.Faces.Count;
        }
        return (vertices, faces);
    }

    private static IEnumerable<string> ModelGeneratedTextureCodes(ModelElementData root)
    {
        return root.EnumerateSubtree()
            .SelectMany(element => element.NonCuboid?.Editable == true
                ? element.NonCuboid.Faces.Select(face => face.Texture)
                : element.Faces.Where(face => face != null).Select(face => face!.Texture))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal);
    }

    private static ModelNonCuboidData ModelBuildGeneratedMesh(
        ModelElementData element,
        ModelGeneratedMeshSpec spec,
        string texture)
    {
        if (spec.Kind == ModelGeneratedMeshKind.Box)
        {
            return ModelCreateBoxMesh(element.From, element.To, texture);
        }

        ModelGeneratedMeshBuilder builder = new(texture);
        switch (spec.Kind)
        {
            case ModelGeneratedMeshKind.ChamferedBox:
                ModelBuildGeneratedChamferedBox(builder, element);
                break;
            case ModelGeneratedMeshKind.Tube:
                ModelBuildGeneratedTube(builder, element, spec, pointed: false);
                break;
            case ModelGeneratedMeshKind.Cone:
                ModelBuildGeneratedTube(builder, element, spec, pointed: true);
                break;
            case ModelGeneratedMeshKind.ExtrudedContour:
                ModelBuildGeneratedExtrudedContour(builder, element, spec);
                break;
            case ModelGeneratedMeshKind.Ellipsoid:
                ModelBuildGeneratedEllipsoid(builder, element, spec, dome: false);
                break;
            case ModelGeneratedMeshKind.Dome:
                ModelBuildGeneratedEllipsoid(builder, element, spec, dome: true);
                break;
            case ModelGeneratedMeshKind.Wedge:
                ModelBuildGeneratedLeaf(builder, element, spec, membrane: false, wedge: true);
                break;
            case ModelGeneratedMeshKind.Leaf:
                ModelBuildGeneratedLeaf(builder, element, spec, membrane: false, wedge: false);
                break;
            case ModelGeneratedMeshKind.Membrane:
                ModelBuildGeneratedLeaf(builder, element, spec, membrane: true, wedge: false);
                break;
            case ModelGeneratedMeshKind.Ring:
                ModelBuildGeneratedRing(builder, element, spec);
                break;
            case ModelGeneratedMeshKind.BoxTube:
                ModelBuildGeneratedBoxTube(builder, element, spec);
                break;
            case ModelGeneratedMeshKind.Jewel:
                ModelBuildGeneratedJewel(builder, element);
                break;
            default:
                throw new InvalidOperationException($"Unsupported generated mesh profile {spec.Kind}.");
        }
        ModelApplyGeneratedProfileUvs(builder.Mesh, element, spec);
        return builder.Mesh;
    }

    private static void ModelApplyGeneratedProfileUvs(
        ModelNonCuboidData mesh,
        ModelElementData element,
        ModelGeneratedMeshSpec spec)
    {
        if (spec.Kind == ModelGeneratedMeshKind.Ring)
        {
            ModelApplyGeneratedRingUvs(mesh, ModelGeneratedRoundSides(Math.Max(8, spec.Sides)));
            return;
        }

        bool cylindrical = spec.Kind is ModelGeneratedMeshKind.Tube or ModelGeneratedMeshKind.Cone or
            ModelGeneratedMeshKind.Ellipsoid or ModelGeneratedMeshKind.Dome;
        int axis = Math.Clamp(spec.Axis, 0, 2);
        int[] cross = ModelGeneratedCrossAxes(axis);
        double[] center = ModelGeneratedCenter(element);
        double halfA = Math.Max(0.000001d, ModelGeneratedSize(element, cross[0]) * 0.5d);
        double halfB = Math.Max(0.000001d, ModelGeneratedSize(element, cross[1]) * 0.5d);
        double axisSize = Math.Max(0.000001d, ModelGeneratedSize(element, axis));

        foreach (ModelMeshFaceData face in mesh.Faces)
        {
            if (!cylindrical || face.Vertices.Max(index => mesh.Vertices[index][axis]) -
                face.Vertices.Min(index => mesh.Vertices[index][axis]) <= 0.000001d)
            {
                face.Uv = ModelGeneratedPlanarFaceUvs(mesh, element, face);
                continue;
            }

            double[] u = new double[face.Vertices.Length];
            bool[] atCenter = new bool[face.Vertices.Length];
            for (int index = 0; index < face.Vertices.Length; index++)
            {
                double[] vertex = mesh.Vertices[face.Vertices[index]];
                double a = (vertex[cross[0]] - center[cross[0]]) / halfA;
                double b = (vertex[cross[1]] - center[cross[1]]) / halfB;
                atCenter[index] = Math.Abs(a) + Math.Abs(b) <= 0.000001d;
                double angle = Math.Atan2(b, a) / (Math.PI * 2d);
                if (angle < 0d) angle += 1d;
                u[index] = angle;
            }
            int previousRim = -1;
            for (int index = 0; index < u.Length; index++)
            {
                if (atCenter[index]) continue;
                if (previousRim < 0)
                {
                    previousRim = index;
                    continue;
                }
                double previous = u[previousRim];
                while (u[index] - previous > 0.5d) u[index] -= 1d;
                while (previous - u[index] > 0.5d) u[index] += 1d;
                previousRim = index;
            }
            double averageU = u.Where((_, index) => !atCenter[index]).DefaultIfEmpty(0d).Average();
            for (int index = 0; index < u.Length; index++) if (atCenter[index]) u[index] = averageU;
            double shift = -Math.Floor(u.Min());

            face.Uv = new List<float[]>(face.Vertices.Length);
            for (int index = 0; index < face.Vertices.Length; index++)
            {
                double[] vertex = mesh.Vertices[face.Vertices[index]];
                double lengthFraction = spec.Sign > 0
                    ? (vertex[axis] - element.From[axis]) / axisSize
                    : (element.To[axis] - vertex[axis]) / axisSize;
                double v = spec.Kind switch
                {
                    ModelGeneratedMeshKind.Ellipsoid => (Math.Asin(Math.Clamp(lengthFraction * 2d - 1d, -1d, 1d)) + Math.PI * 0.5d) / Math.PI,
                    ModelGeneratedMeshKind.Dome => Math.Asin(Math.Clamp(lengthFraction, 0d, 1d)) / (Math.PI * 0.5d),
                    _ => lengthFraction
                };
                face.Uv.Add([(float)Math.Clamp((u[index] + shift) * 16d, 0d, 16d), (float)Math.Clamp(v * 16d, 0d, 16d)]);
            }
        }
    }

    private static void ModelApplyGeneratedRingUvs(ModelNonCuboidData mesh, int majorSegments)
    {
        const int minorSegments = 4;
        foreach (ModelMeshFaceData face in mesh.Faces)
        {
            int[] major = face.Vertices.Select(index => index / minorSegments).ToArray();
            int[] minor = face.Vertices.Select(index => index % minorSegments).ToArray();
            if (major.Max() - major.Min() > majorSegments / 2)
            {
                for (int index = 0; index < major.Length; index++) if (major[index] == 0) major[index] = majorSegments;
            }
            if (minor.Max() - minor.Min() > minorSegments / 2)
            {
                for (int index = 0; index < minor.Length; index++) if (minor[index] == 0) minor[index] = minorSegments;
            }
            face.Uv = Enumerable.Range(0, face.Vertices.Length)
                .Select(index => new[]
                {
                    major[index] * 16f / majorSegments,
                    minor[index] * 16f / minorSegments
                })
                .ToList();
        }
    }

    private static List<float[]> ModelGeneratedPlanarFaceUvs(
        ModelNonCuboidData mesh,
        ModelElementData element,
        ModelMeshFaceData face)
    {
        double[] a = mesh.Vertices[face.Vertices[0]];
        double[] b = mesh.Vertices[face.Vertices[1]];
        double[] c = mesh.Vertices[face.Vertices[2]];
        double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
        double vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
        double[] normal =
        [
            Math.Abs(uy * vz - uz * vy),
            Math.Abs(uz * vx - ux * vz),
            Math.Abs(ux * vy - uy * vx)
        ];
        int dominant = normal[0] >= normal[1] && normal[0] >= normal[2] ? 0 : normal[1] >= normal[2] ? 1 : 2;
        int[] axes = ModelGeneratedCrossAxes(dominant);
        double uSize = Math.Max(0.000001d, ModelGeneratedSize(element, axes[0]));
        double vSize = Math.Max(0.000001d, ModelGeneratedSize(element, axes[1]));
        return face.Vertices.Select(index =>
        {
            double[] vertex = mesh.Vertices[index];
            return new[]
            {
                (float)Math.Clamp((vertex[axes[0]] - element.From[axes[0]]) / uSize * 16d, 0d, 16d),
                (float)Math.Clamp((vertex[axes[1]] - element.From[axes[1]]) / vSize * 16d, 0d, 16d)
            };
        }).ToList();
    }

    private static void ModelBuildGeneratedChamferedBox(ModelGeneratedMeshBuilder builder, ModelElementData element)
    {
        int axis = ModelGeneratedLongestAxis(element);
        int[] cross = ModelGeneratedCrossAxes(axis);
        double[] center = ModelGeneratedCenter(element);
        double halfA = ModelGeneratedSize(element, cross[0]) * 0.5d;
        double halfB = ModelGeneratedSize(element, cross[1]) * 0.5d;
        double cutA = Math.Min(halfA * 0.35d, halfB * 0.35d);
        double cutB = Math.Min(halfB * 0.35d, halfA * 0.35d);
        (double A, double B)[] section =
        [
            (-halfA + cutA, -halfB), (halfA - cutA, -halfB),
            (halfA, -halfB + cutB), (halfA, halfB - cutB),
            (halfA - cutA, halfB), (-halfA + cutA, halfB),
            (-halfA, halfB - cutB), (-halfA, -halfB + cutB)
        ];
        double start = element.From[axis], end = element.To[axis];
        int[] a = ModelGeneratedSection(builder, center, axis, cross, start, section);
        int[] b = ModelGeneratedSection(builder, center, axis, cross, end, section);
        ModelGeneratedJoinSections(builder, a, b, center);
        ModelGeneratedCap(builder, a, center);
        ModelGeneratedCap(builder, b, center);
    }

    private static void ModelBuildGeneratedTube(
        ModelGeneratedMeshBuilder builder,
        ModelElementData element,
        ModelGeneratedMeshSpec spec,
        bool pointed)
    {
        int axis = Math.Clamp(spec.Axis, 0, 2);
        int[] cross = ModelGeneratedCrossAxes(axis);
        double[] center = ModelGeneratedCenter(element);
        int sides = ModelGeneratedRoundSides(spec.Sides);
        double startCoord = spec.Sign > 0 ? element.From[axis] : element.To[axis];
        double endCoord = spec.Sign > 0 ? element.To[axis] : element.From[axis];
        double startScale = pointed ? 1d : spec.StartScale;
        double endScale = pointed ? 0d : spec.EndScale;
        int[] start = ModelGeneratedEllipseSection(builder, element, center, axis, cross, startCoord, startScale, sides);
        int[] end = ModelGeneratedEllipseSection(builder, element, center, axis, cross, endCoord, endScale, sides);
        if (!pointed && startScale < 0.999999d && endScale < 0.999999d)
        {
            double middleCoord = (startCoord + endCoord) * 0.5d;
            int[] middle = ModelGeneratedEllipseSection(builder, element, center, axis, cross, middleCoord, 1d, sides);
            ModelGeneratedJoinSections(builder, start, middle, center);
            ModelGeneratedJoinSections(builder, middle, end, center);
        }
        else
        {
            ModelGeneratedJoinSections(builder, start, end, center);
        }
        ModelGeneratedCap(builder, start, center);
        ModelGeneratedCap(builder, end, center);
    }

    private static void ModelBuildGeneratedEllipsoid(
        ModelGeneratedMeshBuilder builder,
        ModelElementData element,
        ModelGeneratedMeshSpec spec,
        bool dome)
    {
        int axis = Math.Clamp(spec.Axis, 0, 2);
        int[] cross = ModelGeneratedCrossAxes(axis);
        int sides = ModelGeneratedRoundSides(Math.Max(8, spec.Sides));
        int layers = Math.Max(2, spec.Layers + (spec.Layers & 1));
        double[] center = ModelGeneratedCenter(element);
        double halfAxis = ModelGeneratedSize(element, axis) * 0.5d;
        double halfA = ModelGeneratedSize(element, cross[0]) * 0.5d;
        double halfB = ModelGeneratedSize(element, cross[1]) * 0.5d;
        List<int[]> rings = [];
        if (dome)
        {
            double baseCoord = spec.Sign > 0 ? element.From[axis] : element.To[axis];
            for (int layer = 0; layer <= layers; layer++)
            {
                double t = layer / (double)layers;
                double angle = t * Math.PI * 0.5d;
                double coord = baseCoord + spec.Sign * ModelGeneratedSize(element, axis) * Math.Sin(angle);
                double scale = Math.Cos(angle);
                rings.Add(ModelGeneratedEllipseSection(builder, element, center, axis, cross, coord, scale, sides));
            }
            for (int i = 0; i < rings.Count - 1; i++) ModelGeneratedJoinSections(builder, rings[i], rings[i + 1], center);
            ModelGeneratedCap(builder, rings[0], center);
            return;
        }

        for (int layer = 0; layer <= layers; layer++)
        {
            double phi = -Math.PI * 0.5d + Math.PI * layer / layers;
            double coord = center[axis] + Math.Sin(phi) * halfAxis;
            double scale = Math.Cos(phi);
            if (scale <= 0.000001d)
            {
                rings.Add([builder.Vertex(
                    axis == 0 ? coord : center[0],
                    axis == 1 ? coord : center[1],
                    axis == 2 ? coord : center[2])]);
                continue;
            }
            (double A, double B)[] section = new (double A, double B)[sides];
            for (int side = 0; side < sides; side++)
            {
                double angle = Math.PI * 2d * side / sides;
                section[side] = (Math.Cos(angle) * halfA * scale, Math.Sin(angle) * halfB * scale);
            }
            rings.Add(ModelGeneratedSection(builder, center, axis, cross, coord, section));
        }
        for (int i = 0; i < rings.Count - 1; i++) ModelGeneratedJoinSections(builder, rings[i], rings[i + 1], center);
    }

    private static void ModelBuildGeneratedLeaf(
        ModelGeneratedMeshBuilder builder,
        ModelElementData element,
        ModelGeneratedMeshSpec spec,
        bool membrane,
        bool wedge)
    {
        int axis = Math.Clamp(spec.Axis, 0, 2);
        int[] cross = ModelGeneratedCrossAxes(axis);
        double[] center = ModelGeneratedCenter(element);
        double start = spec.Sign > 0 ? element.From[axis] : element.To[axis];
        double end = spec.Sign > 0 ? element.To[axis] : element.From[axis];
        double mid = (start + end) * 0.5d;
        double halfWidth = ModelGeneratedSize(element, cross[0]) * 0.5d;
        double thickness = ModelGeneratedSize(element, cross[1]) * 0.5d;
        List<(double Axis, double Width)> contour = wedge
            ? [(start, -halfWidth), (start, halfWidth), (end, 0d)]
            : membrane
                ? [(start, -halfWidth), (end, -halfWidth * 0.65d), (end, halfWidth * 0.35d), (start, halfWidth)]
                : [(start, -halfWidth * 0.35d), (mid, -halfWidth), (end, 0d), (mid, halfWidth), (start, halfWidth * 0.35d)];
        int[] low = new int[contour.Count];
        int[] high = new int[contour.Count];
        for (int i = 0; i < contour.Count; i++)
        {
            low[i] = ModelGeneratedVertex(builder, center, axis, cross, contour[i].Axis, contour[i].Width, -thickness);
            high[i] = ModelGeneratedVertex(builder, center, axis, cross, contour[i].Axis, contour[i].Width, thickness);
        }
        for (int i = 0; i < contour.Count; i++)
        {
            int next = (i + 1) % contour.Count;
            builder.FaceOutward([low[i], low[next], high[next], high[i]], center);
        }
        for (int i = 1; i < contour.Count - 1; i++)
        {
            builder.FaceOutward([low[0], low[i], low[i + 1]], center);
            builder.FaceOutward([high[0], high[i + 1], high[i]], center);
        }
    }

    private static void ModelBuildGeneratedExtrudedContour(
        ModelGeneratedMeshBuilder builder,
        ModelElementData element,
        ModelGeneratedMeshSpec spec)
    {
        int axis = Math.Clamp(spec.Axis, 0, 2);
        int[] cross = ModelGeneratedCrossAxes(axis);
        double[] center = ModelGeneratedCenter(element);
        double start = spec.Sign > 0 ? element.From[axis] : element.To[axis];
        double end = spec.Sign > 0 ? element.To[axis] : element.From[axis];
        double mid = (start + end) * 0.5d;
        double halfWidth = ModelGeneratedSize(element, cross[0]) * 0.5d;
        double thickness = ModelGeneratedSize(element, cross[1]) * 0.5d;
        double startWidth = halfWidth * Math.Clamp(spec.StartScale, 0.05d, 0.82d);
        double endWidth = halfWidth * Math.Clamp(spec.EndScale, 0.05d, 0.82d);
        (double Axis, double Width)[] contour =
        [
            (start, -startWidth), (mid, -halfWidth), (end, -endWidth),
            (end, endWidth), (mid, halfWidth), (start, startWidth)
        ];
        int[] low = new int[contour.Length];
        int[] high = new int[contour.Length];
        for (int index = 0; index < contour.Length; index++)
        {
            low[index] = ModelGeneratedVertex(builder, center, axis, cross, contour[index].Axis, contour[index].Width, -thickness);
            high[index] = ModelGeneratedVertex(builder, center, axis, cross, contour[index].Axis, contour[index].Width, thickness);
        }
        for (int index = 0; index < contour.Length; index++)
        {
            int next = (index + 1) % contour.Length;
            builder.FaceOutward([low[index], low[next], high[next], high[index]], center);
        }
        for (int index = 1; index < contour.Length - 1; index++)
        {
            builder.FaceOutward([low[0], low[index], low[index + 1]], center);
            builder.FaceOutward([high[0], high[index + 1], high[index]], center);
        }
    }

    private static void ModelBuildGeneratedRing(
        ModelGeneratedMeshBuilder builder,
        ModelElementData element,
        ModelGeneratedMeshSpec spec)
    {
        int axis = Math.Clamp(spec.Axis, 0, 2);
        int[] cross = ModelGeneratedCrossAxes(axis);
        int majorSegments = ModelGeneratedRoundSides(Math.Max(8, spec.Sides));
        const int minorSegments = 4;
        double[] center = ModelGeneratedCenter(element);
        double halfAxis = ModelGeneratedSize(element, axis) * 0.5d;
        double halfA = ModelGeneratedSize(element, cross[0]) * 0.5d;
        double halfB = ModelGeneratedSize(element, cross[1]) * 0.5d;
        double tubeA = Math.Max(0.0001d, halfA * 0.28d);
        double tubeB = Math.Max(0.0001d, halfB * 0.28d);
        double majorA = Math.Max(0.0001d, halfA - tubeA);
        double majorB = Math.Max(0.0001d, halfB - tubeB);
        int[][] rings = new int[majorSegments][];
        double[][] references = new double[majorSegments][];
        for (int segment = 0; segment < majorSegments; segment++)
        {
            double theta = Math.PI * 2d * segment / majorSegments;
            double ca = Math.Cos(theta), cb = Math.Sin(theta);
            double centerA = ca * majorA, centerB = cb * majorB;
            references[segment] = ModelGeneratedPoint(center, axis, cross, center[axis], centerA, centerB);
            rings[segment] = new int[minorSegments];
            for (int side = 0; side < minorSegments; side++)
            {
                double phi = Math.PI * 2d * side / minorSegments;
                double axisValue = center[axis] + Math.Sin(phi) * halfAxis;
                double a = centerA + ca * Math.Cos(phi) * tubeA;
                double b = centerB + cb * Math.Cos(phi) * tubeB;
                rings[segment][side] = ModelGeneratedVertex(builder, center, axis, cross, axisValue, a, b);
            }
        }
        for (int segment = 0; segment < majorSegments; segment++)
        {
            int next = (segment + 1) % majorSegments;
            double[] reference =
            [
                (references[segment][0] + references[next][0]) * 0.5d,
                (references[segment][1] + references[next][1]) * 0.5d,
                (references[segment][2] + references[next][2]) * 0.5d
            ];
            for (int side = 0; side < minorSegments; side++)
            {
                int nextSide = (side + 1) % minorSegments;
                builder.FaceOutward([rings[segment][side], rings[next][side], rings[next][nextSide], rings[segment][nextSide]], reference);
            }
        }
    }

    private static void ModelBuildGeneratedBoxTube(
        ModelGeneratedMeshBuilder builder,
        ModelElementData element,
        ModelGeneratedMeshSpec spec)
    {
        int axis = Math.Clamp(spec.Axis, 0, 2);
        int[] cross = ModelGeneratedCrossAxes(axis);
        double[] center = ModelGeneratedCenter(element);
        double start = element.From[axis], end = element.To[axis];
        double halfA = ModelGeneratedSize(element, cross[0]) * 0.5d;
        double halfB = ModelGeneratedSize(element, cross[1]) * 0.5d;
        double innerA = halfA * 0.48d, innerB = halfB * 0.48d;
        (double A, double B)[] outer = [(-halfA, -halfB), (halfA, -halfB), (halfA, halfB), (-halfA, halfB)];
        (double A, double B)[] inner = [(-innerA, -innerB), (innerA, -innerB), (innerA, innerB), (-innerA, innerB)];
        int[] os = ModelGeneratedSection(builder, center, axis, cross, start, outer);
        int[] oe = ModelGeneratedSection(builder, center, axis, cross, end, outer);
        int[] ins = ModelGeneratedSection(builder, center, axis, cross, start, inner);
        int[] ine = ModelGeneratedSection(builder, center, axis, cross, end, inner);
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            builder.FaceOutward([os[i], os[next], oe[next], oe[i]], center);
            double middleA = (inner[i].A + inner[next].A) * 0.5d;
            double middleB = (inner[i].B + inner[next].B) * 0.5d;
            double[] innerReference = ModelGeneratedPoint(center, axis, cross, center[axis], middleA * 2d, middleB * 2d);
            builder.FaceOutward([ins[i], ine[i], ine[next], ins[next]], innerReference);
            builder.FaceOutward([os[i], ins[i], ins[next], os[next]], center);
            builder.FaceOutward([oe[i], oe[next], ine[next], ine[i]], center);
        }
    }

    private static void ModelBuildGeneratedJewel(ModelGeneratedMeshBuilder builder, ModelElementData element)
    {
        double[] center = ModelGeneratedCenter(element);
        int[] vertices =
        [
            builder.Vertex(element.From[0], center[1], center[2]),
            builder.Vertex(element.To[0], center[1], center[2]),
            builder.Vertex(center[0], element.From[1], center[2]),
            builder.Vertex(center[0], element.To[1], center[2]),
            builder.Vertex(center[0], center[1], element.From[2]),
            builder.Vertex(center[0], center[1], element.To[2])
        ];
        int[][] faces =
        [
            [0, 2, 4], [0, 5, 2], [0, 4, 3], [0, 3, 5],
            [1, 4, 2], [1, 2, 5], [1, 3, 4], [1, 5, 3]
        ];
        foreach (int[] face in faces) builder.FaceOutward(face, center);
    }

    private static int[] ModelGeneratedEllipseSection(
        ModelGeneratedMeshBuilder builder,
        ModelElementData element,
        double[] center,
        int axis,
        int[] cross,
        double coordinate,
        double scale,
        int sides)
    {
        if (scale <= 0.000001d)
        {
            return [ModelGeneratedVertex(builder, center, axis, cross, coordinate, 0d, 0d)];
        }
        double halfA = ModelGeneratedSize(element, cross[0]) * 0.5d * scale;
        double halfB = ModelGeneratedSize(element, cross[1]) * 0.5d * scale;
        (double A, double B)[] section = new (double A, double B)[sides];
        if (sides == 4)
        {
            section = [(-halfA, -halfB), (halfA, -halfB), (halfA, halfB), (-halfA, halfB)];
        }
        else
        {
            for (int side = 0; side < sides; side++)
            {
                double angle = Math.PI * 2d * side / sides;
                section[side] = (Math.Cos(angle) * halfA, Math.Sin(angle) * halfB);
            }
        }
        return ModelGeneratedSection(builder, center, axis, cross, coordinate, section);
    }

    private static int[] ModelGeneratedSection(
        ModelGeneratedMeshBuilder builder,
        double[] center,
        int axis,
        int[] cross,
        double coordinate,
        IReadOnlyList<(double A, double B)> section)
    {
        int[] ring = new int[section.Count];
        for (int index = 0; index < section.Count; index++)
        {
            ring[index] = ModelGeneratedVertex(builder, center, axis, cross, coordinate, section[index].A, section[index].B);
        }
        return ring;
    }

    private static int ModelGeneratedVertex(
        ModelGeneratedMeshBuilder builder,
        double[] center,
        int axis,
        int[] cross,
        double coordinate,
        double a,
        double b)
    {
        double[] point = ModelGeneratedPoint(center, axis, cross, coordinate, a, b);
        return builder.Vertex(point[0], point[1], point[2]);
    }

    private static double[] ModelGeneratedPoint(double[] center, int axis, int[] cross, double coordinate, double a, double b)
    {
        double[] point = (double[])center.Clone();
        point[axis] = coordinate;
        point[cross[0]] += a;
        point[cross[1]] += b;
        return point;
    }

    private static void ModelGeneratedJoinSections(
        ModelGeneratedMeshBuilder builder,
        int[] a,
        int[] b,
        double[] center)
    {
        if (a.Length == 1 && b.Length == 1) return;
        if (a.Length == 1)
        {
            for (int i = 0; i < b.Length; i++) builder.FaceOutward([a[0], b[i], b[(i + 1) % b.Length]], center);
            return;
        }
        if (b.Length == 1)
        {
            for (int i = 0; i < a.Length; i++) builder.FaceOutward([a[i], b[0], a[(i + 1) % a.Length]], center);
            return;
        }
        int count = Math.Min(a.Length, b.Length);
        for (int i = 0; i < count; i++) builder.FaceOutward([a[i], b[i], b[(i + 1) % count], a[(i + 1) % count]], center);
    }

    private static void ModelGeneratedCap(ModelGeneratedMeshBuilder builder, int[] ring, double[] center)
    {
        if (ring.Length < 3) return;
        for (int i = 1; i < ring.Length - 1; i++) builder.FaceOutward([ring[0], ring[i], ring[i + 1]], center);
    }

    private static bool ModelGeneratedMeshMatchesBounds(ModelElementData element, ModelNonCuboidData mesh)
    {
        if (mesh.Vertices.Count == 0) return false;
        for (int axis = 0; axis < 3; axis++)
        {
            double min = mesh.Vertices.Min(vertex => vertex[axis]);
            double max = mesh.Vertices.Max(vertex => vertex[axis]);
            if (Math.Abs(min - element.From[axis]) > 0.00001d || Math.Abs(max - element.To[axis]) > 0.00001d) return false;
        }
        return true;
    }

    private static int ModelGeneratedLongestAxis(ModelElementData element)
    {
        double[] size = [element.SizeX, element.SizeY, element.SizeZ];
        return size[0] >= size[1] && size[0] >= size[2] ? 0 : size[1] >= size[2] ? 1 : 2;
    }

    private static int[] ModelGeneratedCrossAxes(int axis)
    {
        return axis switch
        {
            0 => [1, 2],
            1 => [0, 2],
            _ => [0, 1]
        };
    }

    private static int ModelGeneratedRoundSides(int sides)
    {
        sides = Math.Clamp(sides, 4, 32);
        return sides == 4 ? 4 : sides + (4 - sides % 4) % 4;
    }

    private static double[] ModelGeneratedCenter(ModelElementData element)
    {
        return
        [
            (element.From[0] + element.To[0]) * 0.5d,
            (element.From[1] + element.To[1]) * 0.5d,
            (element.From[2] + element.To[2]) * 0.5d
        ];
    }

    private static double ModelGeneratedSize(ModelElementData element, int axis)
    {
        return element.To[axis] - element.From[axis];
    }
}
