namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private ModelElementData? ModelBuildMeshLibPrimitive(ModelPrimitiveKind kind, out string error)
    {
        error = "";
        string texture = string.IsNullOrWhiteSpace(_modelPrimitiveTexture)
            ? _modelDoc?.Textures.FirstOrDefault()?.Code ?? "all"
            : _modelPrimitiveTexture;
        ModelGeneratedMeshBuilder builder = new(texture, (u, v, w) =>
        {
            (double x, double y, double z) = ModelPrimitiveAxisMap(u, v, w);
            return (x + _modelPrimitiveCenter.X, y + _modelPrimitiveCenter.Y, z + _modelPrimitiveCenter.Z);
        });
        try
        {
            switch (kind)
            {
                case ModelPrimitiveKind.Cylinder:
                    ModelBuildMeshCylinder(builder, _modelPrimitiveDiameter * 0.5, _modelPrimitiveHeight, _modelPrimitiveDiameter * 0.5, _modelPrimitiveHollow ? Math.Max(0d, _modelPrimitiveDiameter * 0.5 - _modelPrimitiveWall) : 0d);
                    break;
                case ModelPrimitiveKind.Cone:
                    ModelBuildMeshCone(builder, _modelPrimitiveDiameter * 0.5, _modelPrimitiveTopDiameter * 0.5, _modelPrimitiveHeight, _modelPrimitiveHollow ? _modelPrimitiveWall : 0d);
                    break;
                case ModelPrimitiveKind.Sphere:
                    ModelBuildMeshSphere(builder, capsule: false);
                    break;
                case ModelPrimitiveKind.Torus:
                    ModelBuildMeshTorus(builder);
                    break;
                case ModelPrimitiveKind.Pyramid:
                    ModelBuildMeshFrustum(builder);
                    break;
                case ModelPrimitiveKind.Wedge:
                    ModelBuildMeshWedge(builder);
                    break;
                case ModelPrimitiveKind.Capsule:
                    ModelBuildMeshSphere(builder, capsule: true);
                    break;
                case ModelPrimitiveKind.Helix:
                    ModelBuildMeshHelix(builder);
                    break;
                case ModelPrimitiveKind.BoxTube:
                    ModelBuildMeshBoxTube(builder);
                    break;
                case ModelPrimitiveKind.Star:
                    ModelBuildMeshExtrudedContour(builder, ModelStarContour(), _modelPrimitiveThickness);
                    break;
                case ModelPrimitiveKind.Cross:
                    ModelBuildMeshExtrudedContour(builder, ModelCrossContour(), _modelPrimitiveThickness);
                    break;
                case ModelPrimitiveKind.Arrow:
                    ModelBuildMeshExtrudedContour(builder, ModelArrowContour(), _modelPrimitiveThickness);
                    break;
                case ModelPrimitiveKind.Heart:
                    ModelBuildMeshExtrudedContour(builder, ModelHeartContour(), _modelPrimitiveThickness);
                    break;
                case ModelPrimitiveKind.TrianglePlate:
                    ModelBuildMeshExtrudedContour(builder, ModelTriangleContour(), _modelPrimitiveThickness);
                    break;
                case ModelPrimitiveKind.Sector:
                    ModelBuildMeshExtrudedContour(builder, ModelSectorContour(), _modelPrimitiveThickness);
                    break;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return null;
        }

        List<string> errors = ModelValidateNonCuboid(builder.Mesh);
        if (errors.Count > 0)
        {
            error = errors[0];
            return null;
        }
        ModelElementData element = new()
        {
            Name = ModelMeshPrimitiveName(kind),
            NonCuboid = builder.Mesh,
            RotationX = ModelPrimitiveRound(_modelPrimitiveRotation.X),
            RotationY = ModelPrimitiveRound(_modelPrimitiveRotation.Y),
            RotationZ = ModelPrimitiveRound(_modelPrimitiveRotation.Z),
            RotationOrigin = [_modelPrimitiveCenter.X, _modelPrimitiveCenter.Y, _modelPrimitiveCenter.Z]
        };
        if (ModelTryGetMeshBounds(element, out double[] min, out double[] max))
        {
            element.From = min;
            element.To = max;
        }
        _modelPrimitivePreviewMetrics = new ModelPrimitiveMetrics { ModeLabel = "MeshLib surface", QualityLabel = "Triangle/quad mesh", EnabledFaces = builder.Mesh.Faces.Count };
        return element;
    }

    private string ModelMeshPrimitiveName(ModelPrimitiveKind kind)
    {
        return kind switch
        {
            ModelPrimitiveKind.Cylinder => _modelPrimitiveHollow ? "Tube" : "Cylinder",
            ModelPrimitiveKind.Cone => "Cone",
            ModelPrimitiveKind.Sphere => _modelPrimitiveDome == 0 ? _modelPrimitiveHollow ? "HollowSphere" : "Sphere" : _modelPrimitiveHollow ? "Bowl" : "Dome",
            ModelPrimitiveKind.Torus => _modelPrimitiveSweep >= 359f ? "Torus" : "Arch",
            ModelPrimitiveKind.Pyramid => _modelPrimitiveTopScale > 0.001f ? "Frustum" : "Pyramid",
            ModelPrimitiveKind.Wedge => "Wedge",
            ModelPrimitiveKind.Capsule => "Capsule",
            ModelPrimitiveKind.Helix => "Helix",
            ModelPrimitiveKind.BoxTube => "BoxTube",
            ModelPrimitiveKind.Star => "Star",
            ModelPrimitiveKind.Cross => "Cross",
            ModelPrimitiveKind.Arrow => "Arrow",
            ModelPrimitiveKind.Heart => "Heart",
            ModelPrimitiveKind.TrianglePlate => "Triangle",
            _ => _modelPrimitiveSweep >= 359f ? "Disc" : "Sector"
        };
    }

    private void ModelBuildMeshCylinder(ModelGeneratedMeshBuilder b, double radius, double height, double topRadius, double innerRadius)
    {
        int sides = Math.Clamp(_modelPrimitiveSides, 3, 64);
        double v0 = -height * 0.5, v1 = height * 0.5;
        int[] bottom = ModelMeshRing(b, radius, v0, sides, 0d, _modelPrimitiveSweep);
        int[] top = ModelMeshRing(b, topRadius, v1, sides, 0d, _modelPrimitiveSweep);
        bool closedSweep = _modelPrimitiveSweep >= 359.999f;
        int segments = closedSweep ? sides : sides - 1;
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % sides;
            b.Quad(bottom[i], bottom[next], top[next], top[i], i * 16f / segments, (i + 1) * 16f / segments);
        }
        if (innerRadius > 0.001)
        {
            double innerTop = Math.Max(0.001, topRadius - (radius - innerRadius));
            int[] innerBottom = ModelMeshRing(b, innerRadius, v0, sides, 0d, _modelPrimitiveSweep);
            int[] innerTopRing = ModelMeshRing(b, innerTop, v1, sides, 0d, _modelPrimitiveSweep);
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % sides;
                b.Quad(innerBottom[next], innerBottom[i], innerTopRing[i], innerTopRing[next]);
                b.Quad(bottom[next], bottom[i], innerBottom[i], innerBottom[next]);
                b.Quad(top[i], top[next], innerTopRing[next], innerTopRing[i]);
            }
            if (!closedSweep)
            {
                b.Quad(bottom[0], top[0], innerTopRing[0], innerBottom[0]);
                b.Quad(bottom[^1], innerBottom[^1], innerTopRing[^1], top[^1]);
            }
        }
        else
        {
            int bottomCenter = b.Vertex(0d, v0, 0d);
            int topCenter = b.Vertex(0d, v1, 0d);
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % sides;
                b.Tri(bottomCenter, bottom[next], bottom[i]);
                b.Tri(topCenter, top[i], top[next]);
            }
            if (!closedSweep)
            {
                b.Quad(bottomCenter, topCenter, top[0], bottom[0]);
                b.Quad(bottom[^1], top[^1], topCenter, bottomCenter);
            }
        }
    }

    private void ModelBuildMeshCone(ModelGeneratedMeshBuilder b, double bottomRadius, double topRadius, double height, double wall)
    {
        int layers = Math.Clamp(_modelPrimitiveLayers, 1, 32);
        int sides = Math.Clamp(_modelPrimitiveSides, 3, 64);
        List<int[]> rings = [];
        for (int layer = 0; layer <= layers; layer++)
        {
            double t = layer / (double)layers;
            double radius = bottomRadius + (topRadius - bottomRadius) * t;
            if (radius <= 0.0001)
            {
                rings.Add([b.Vertex(0d, -height * 0.5 + height * t, 0d)]);
            }
            else rings.Add(ModelMeshRing(b, radius, -height * 0.5 + height * t, sides, 0d, _modelPrimitiveSweep));
        }
        ModelJoinMeshRings(b, rings, sides, _modelPrimitiveSweep >= 359.999f);
        if (wall > 0.001 && topRadius > wall && bottomRadius > wall)
        {
            int[] innerBottom = ModelMeshRing(b, bottomRadius - wall, -height * 0.5, sides, 0d, _modelPrimitiveSweep);
            int[] innerTop = ModelMeshRing(b, topRadius - wall, height * 0.5, sides, 0d, _modelPrimitiveSweep);
            int segments = _modelPrimitiveSweep >= 359.999f ? sides : sides - 1;
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % sides;
                b.Quad(innerBottom[next], innerBottom[i], innerTop[i], innerTop[next]);
                b.Quad(rings[0][next], rings[0][i], innerBottom[i], innerBottom[next]);
                b.Quad(rings[^1][i], rings[^1][next], innerTop[next], innerTop[i]);
            }
        }
        else
        {
            ModelCapMeshRing(b, rings[0], bottom: true);
            ModelCapMeshRing(b, rings[^1], bottom: false);
        }
    }

    private void ModelBuildMeshSphere(ModelGeneratedMeshBuilder b, bool capsule)
    {
        int sides = Math.Clamp(_modelPrimitiveSides, 3, 64);
        int layers = Math.Clamp(_modelPrimitiveLayers, 2, 32);
        double radius = _modelPrimitiveDiameter * 0.5;
        double polarRadius = Math.Max(0.25, _modelPrimitiveHeight * 0.5);
        double bodyHalf = capsule ? Math.Max(0d, _modelPrimitiveHeight * 0.5 - radius) : 0d;
        double start = _modelPrimitiveDome == 1 ? 0d : -Math.PI * 0.5;
        double end = _modelPrimitiveDome == 2 ? 0d : Math.PI * 0.5;
        List<int[]> rings = [];
        for (int layer = 0; layer <= layers; layer++)
        {
            double t = layer / (double)layers;
            double phi = start + (end - start) * t;
            double ringRadius = radius * Math.Cos(phi);
            double v = Math.Sin(phi) * (capsule ? radius : polarRadius);
            if (capsule) v += phi < 0 ? -bodyHalf : phi > 0 ? bodyHalf : 0d;
            rings.Add(ringRadius <= 0.0001 ? [b.Vertex(0d, v, 0d)] : ModelMeshRing(b, ringRadius, v, sides));
        }
        ModelJoinMeshRings(b, rings, sides, true);
        if (_modelPrimitiveDome != 0)
        {
            int openIndex = _modelPrimitiveDome == 1 ? 0 : rings.Count - 1;
            ModelCapMeshRing(b, rings[openIndex], bottom: _modelPrimitiveDome == 1);
        }
        if (_modelPrimitiveHollow)
        {
            // MeshLib is visual-only; an inward copy makes cut domes/bowls visibly hollow.
            List<ModelMeshFaceData> inside = b.Mesh.Faces.Select(face => face.Clone()).ToList();
            foreach (ModelMeshFaceData face in inside)
            {
                Array.Reverse(face.Vertices);
                face.Uv?.Reverse();
            }
            b.Mesh.Faces.AddRange(inside);
        }
    }

    private void ModelBuildMeshTorus(ModelGeneratedMeshBuilder b)
    {
        int majorSegments = Math.Clamp(_modelPrimitiveSegments, 3, 128);
        int minorSegments = Math.Clamp(_modelPrimitiveSides, 3, 64);
        double outer = _modelPrimitiveDiameter * 0.5;
        double minor = Math.Min(_modelPrimitiveMinor * 0.5, Math.Max(0.125, outer - 0.125));
        double major = Math.Max(0.001, outer - minor);
        bool closed = _modelPrimitiveSweep >= 359.999f;
        int ringCount = closed ? majorSegments : majorSegments + 1;
        int[][] rings = new int[ringCount][];
        for (int segment = 0; segment < ringCount; segment++)
        {
            double theta = _modelPrimitiveSweep * Math.PI / 180d * segment / majorSegments;
            rings[segment] = new int[minorSegments];
            for (int side = 0; side < minorSegments; side++)
            {
                double phi = Math.PI * 2d * side / minorSegments;
                double radial = major + minor * Math.Cos(phi);
                rings[segment][side] = b.Vertex(radial * Math.Cos(theta), minor * Math.Sin(phi), radial * Math.Sin(theta));
            }
        }
        int segmentCount = closed ? majorSegments : majorSegments;
        for (int segment = 0; segment < segmentCount; segment++)
        {
            int nextSegment = (segment + 1) % ringCount;
            for (int side = 0; side < minorSegments; side++)
            {
                int nextSide = (side + 1) % minorSegments;
                b.Quad(rings[segment][side], rings[nextSegment][side], rings[nextSegment][nextSide], rings[segment][nextSide]);
            }
        }
        if (!closed)
        {
            ModelCapMeshRing(b, rings[0], true);
            ModelCapMeshRing(b, rings[^1], false);
        }
    }

    private void ModelBuildMeshFrustum(ModelGeneratedMeshBuilder b)
    {
        double halfWidth = _modelPrimitiveDiameter * 0.5;
        double halfDepth = _modelPrimitiveDepth * 0.5;
        double topScale = Math.Clamp(_modelPrimitiveTopScale, 0f, 1f);
        double v0 = -_modelPrimitiveHeight * 0.5, v1 = _modelPrimitiveHeight * 0.5;
        int[] bottom =
        [
            b.Vertex(-halfWidth, v0, -halfDepth), b.Vertex(halfWidth, v0, -halfDepth),
            b.Vertex(halfWidth, v0, halfDepth), b.Vertex(-halfWidth, v0, halfDepth)
        ];
        if (topScale <= 0.0001)
        {
            int tip = b.Vertex(0d, v1, 0d);
            for (int i = 0; i < 4; i++) b.Tri(bottom[i], bottom[(i + 1) % 4], tip);
        }
        else
        {
            int[] top =
            [
                b.Vertex(-halfWidth * topScale, v1, -halfDepth * topScale), b.Vertex(halfWidth * topScale, v1, -halfDepth * topScale),
                b.Vertex(halfWidth * topScale, v1, halfDepth * topScale), b.Vertex(-halfWidth * topScale, v1, halfDepth * topScale)
            ];
            for (int i = 0; i < 4; i++) b.Quad(bottom[i], bottom[(i + 1) % 4], top[(i + 1) % 4], top[i]);
            b.Quad(top[0], top[1], top[2], top[3]);
        }
        b.Quad(bottom[3], bottom[2], bottom[1], bottom[0]);
    }

    private void ModelBuildMeshWedge(ModelGeneratedMeshBuilder b)
    {
        double run = _modelPrimitiveDiameter * 0.5;
        double rise = _modelPrimitiveRise * 0.5;
        double length = _modelPrimitiveHeight * 0.5;
        int[] left = [b.Vertex(-run, -length, -rise), b.Vertex(run, -length, -rise), b.Vertex(run, -length, rise)];
        int[] right = [b.Vertex(-run, length, -rise), b.Vertex(run, length, -rise), b.Vertex(run, length, rise)];
        b.Tri(left[2], left[1], left[0]);
        b.Tri(right[0], right[1], right[2]);
        for (int i = 0; i < 3; i++) b.Quad(left[i], left[(i + 1) % 3], right[(i + 1) % 3], right[i]);
    }

    private void ModelBuildMeshHelix(ModelGeneratedMeshBuilder b)
    {
        int segments = Math.Clamp(_modelPrimitiveSegments, 3, 256);
        double radius = _modelPrimitiveDiameter * 0.5;
        double tube = Math.Max(0.125, _modelPrimitiveMinor * 0.5);
        double climb = _modelPrimitiveHeight;
        int[][] rings = new int[segments + 1][];
        for (int segment = 0; segment <= segments; segment++)
        {
            double t = segment / (double)segments;
            double angle = t * _modelPrimitiveTurns * Math.PI * 2d;
            double centerU = Math.Cos(angle) * radius;
            double centerW = Math.Sin(angle) * radius;
            double centerV = -climb * 0.5 + climb * t;
            double radialU = Math.Cos(angle), radialW = Math.Sin(angle);
            rings[segment] =
            [
                b.Vertex(centerU - radialU * tube, centerV - tube, centerW - radialW * tube),
                b.Vertex(centerU + radialU * tube, centerV - tube, centerW + radialW * tube),
                b.Vertex(centerU + radialU * tube, centerV + tube, centerW + radialW * tube),
                b.Vertex(centerU - radialU * tube, centerV + tube, centerW - radialW * tube)
            ];
        }
        for (int segment = 0; segment < segments; segment++)
        {
            for (int side = 0; side < 4; side++) b.Quad(rings[segment][side], rings[segment + 1][side], rings[segment + 1][(side + 1) % 4], rings[segment][(side + 1) % 4]);
        }
        ModelCapMeshRing(b, rings[0], true);
        ModelCapMeshRing(b, rings[^1], false);
    }

    private void ModelBuildMeshBoxTube(ModelGeneratedMeshBuilder b)
    {
        double outerU = _modelPrimitiveDiameter * 0.5, outerW = _modelPrimitiveDepth * 0.5;
        double innerU = Math.Max(0.001, outerU - _modelPrimitiveWall), innerW = Math.Max(0.001, outerW - _modelPrimitiveWall);
        double v0 = -_modelPrimitiveHeight * 0.5, v1 = _modelPrimitiveHeight * 0.5;
        (double U, double W)[] outer = [(-outerU, -outerW), (outerU, -outerW), (outerU, outerW), (-outerU, outerW)];
        (double U, double W)[] inner = [(-innerU, -innerW), (-innerU, innerW), (innerU, innerW), (innerU, -innerW)];
        int[] ob = outer.Select(point => b.Vertex(point.U, v0, point.W)).ToArray();
        int[] ot = outer.Select(point => b.Vertex(point.U, v1, point.W)).ToArray();
        int[] ib = inner.Select(point => b.Vertex(point.U, v0, point.W)).ToArray();
        int[] it = inner.Select(point => b.Vertex(point.U, v1, point.W)).ToArray();
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            b.Quad(ob[i], ob[next], ot[next], ot[i]);
            b.Quad(ib[i], it[i], it[next], ib[next]);
            b.Quad(ob[next], ob[i], ib[3 - i], ib[(3 - i + 3) % 4]);
            b.Quad(ot[i], ot[next], it[(3 - i + 3) % 4], it[3 - i]);
        }
    }

    private int[] ModelMeshRing(ModelGeneratedMeshBuilder b, double radius, double v, int sides, double startDegrees = 0d, double sweepDegrees = 360d)
    {
        bool closed = sweepDegrees >= 359.999;
        int count = closed ? sides : sides + 1;
        int[] ring = new int[count];
        for (int index = 0; index < count; index++)
        {
            double t = index / (double)sides;
            double angle = (startDegrees + sweepDegrees * t) * Math.PI / 180d;
            ring[index] = b.Vertex(Math.Cos(angle) * radius, v, Math.Sin(angle) * radius);
        }
        return ring;
    }

    private static void ModelJoinMeshRings(ModelGeneratedMeshBuilder b, IReadOnlyList<int[]> rings, int sides, bool closed)
    {
        for (int ringIndex = 0; ringIndex < rings.Count - 1; ringIndex++)
        {
            int[] a = rings[ringIndex], c = rings[ringIndex + 1];
            if (a.Length == 1 && c.Length == 1) continue;
            if (a.Length == 1)
            {
                int segments = closed ? sides : c.Length - 1;
                for (int i = 0; i < segments; i++) b.Tri(a[0], c[(i + 1) % c.Length], c[i]);
            }
            else if (c.Length == 1)
            {
                int segments = closed ? sides : a.Length - 1;
                for (int i = 0; i < segments; i++) b.Tri(a[i], a[(i + 1) % a.Length], c[0]);
            }
            else
            {
                int segments = closed ? sides : Math.Min(a.Length, c.Length) - 1;
                for (int i = 0; i < segments; i++) b.Quad(a[i], a[(i + 1) % a.Length], c[(i + 1) % c.Length], c[i]);
            }
        }
    }

    private static void ModelCapMeshRing(ModelGeneratedMeshBuilder b, int[] ring, bool bottom)
    {
        if (ring.Length < 3) return;
        for (int i = 1; i < ring.Length - 1; i++)
        {
            if (bottom) b.Tri(ring[0], ring[i + 1], ring[i]);
            else b.Tri(ring[0], ring[i], ring[i + 1]);
        }
    }

    private void ModelBuildMeshExtrudedContour(ModelGeneratedMeshBuilder b, IReadOnlyList<(double U, double W)> contour, double thickness)
    {
        if (contour.Count < 3) return;
        List<(double U, double W)> points = ModelEnsureCounterClockwise(contour);
        double v0 = -thickness * 0.5, v1 = thickness * 0.5;
        int[] bottom = points.Select(point => b.Vertex(point.U, v0, point.W)).ToArray();
        int[] top = points.Select(point => b.Vertex(point.U, v1, point.W)).ToArray();
        for (int i = 0; i < points.Count; i++)
        {
            int next = (i + 1) % points.Count;
            b.Quad(bottom[i], bottom[next], top[next], top[i]);
        }
        foreach ((int a, int c, int d) in ModelTriangulateContour(points))
        {
            b.Tri(bottom[d], bottom[c], bottom[a]);
            b.Tri(top[a], top[c], top[d]);
        }
    }

    private static List<(double U, double W)> ModelEnsureCounterClockwise(IReadOnlyList<(double U, double W)> contour)
    {
        List<(double U, double W)> points = [.. contour];
        double area = 0d;
        for (int i = 0; i < points.Count; i++) area += points[i].U * points[(i + 1) % points.Count].W - points[(i + 1) % points.Count].U * points[i].W;
        if (area < 0d) points.Reverse();
        return points;
    }

    private static List<(int A, int B, int C)> ModelTriangulateContour(IReadOnlyList<(double U, double W)> points)
    {
        List<(int A, int B, int C)> triangles = [];
        List<int> remaining = Enumerable.Range(0, points.Count).ToList();
        int guard = points.Count * points.Count;
        while (remaining.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int previous = remaining[(i + remaining.Count - 1) % remaining.Count];
                int current = remaining[i];
                int next = remaining[(i + 1) % remaining.Count];
                if (ModelCross2(points[previous], points[current], points[next]) <= 0.0000001) continue;
                if (remaining.Any(candidate => candidate != previous && candidate != current && candidate != next && ModelPointInTriangle(points[candidate], points[previous], points[current], points[next]))) continue;
                triangles.Add((previous, current, next));
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) break;
        }
        if (remaining.Count == 3) triangles.Add((remaining[0], remaining[1], remaining[2]));
        if (triangles.Count == 0)
        {
            for (int i = 1; i < points.Count - 1; i++) triangles.Add((0, i, i + 1));
        }
        return triangles;
    }

    private static double ModelCross2((double U, double W) a, (double U, double W) b, (double U, double W) c)
        => (b.U - a.U) * (c.W - a.W) - (b.W - a.W) * (c.U - a.U);

    private static bool ModelPointInTriangle((double U, double W) p, (double U, double W) a, (double U, double W) b, (double U, double W) c)
    {
        double ab = ModelCross2(a, b, p), bc = ModelCross2(b, c, p), ca = ModelCross2(c, a, p);
        return ab >= -0.0000001 && bc >= -0.0000001 && ca >= -0.0000001;
    }

    private List<(double U, double W)> ModelStarContour()
    {
        int points = Math.Clamp(_modelPrimitiveStarSquares * 4, 4, 32);
        double outer = _modelPrimitiveDiameter * 0.5;
        double inner = outer * 0.45;
        return Enumerable.Range(0, points * 2).Select(index =>
        {
            double radius = (index & 1) == 0 ? outer : inner;
            double angle = -Math.PI * 0.5 + Math.PI * index / points;
            return (Math.Cos(angle) * radius, Math.Sin(angle) * radius);
        }).ToList();
    }

    private List<(double U, double W)> ModelCrossContour()
    {
        double half = _modelPrimitiveDiameter * 0.5, arm = Math.Min(half, _modelPrimitiveMinor * 0.5);
        return [(-arm, -half), (arm, -half), (arm, -arm), (half, -arm), (half, arm), (arm, arm), (arm, half), (-arm, half), (-arm, arm), (-half, arm), (-half, -arm), (-arm, -arm)];
    }

    private List<(double U, double W)> ModelArrowContour()
    {
        double length = _modelPrimitiveDiameter, halfShaft = _modelPrimitiveMinor * 0.5, halfHead = _modelPrimitiveDepth * 0.5;
        double left = -length * 0.5, neck = length * 0.1, right = length * 0.5;
        return [(left, -halfShaft), (neck, -halfShaft), (neck, -halfHead), (right, 0d), (neck, halfHead), (neck, halfShaft), (left, halfShaft)];
    }

    private List<(double U, double W)> ModelHeartContour()
    {
        double scale = _modelPrimitiveDiameter / 34d;
        int segments = Math.Clamp(_modelPrimitiveSides * 2, 12, 64);
        return Enumerable.Range(0, segments).Select(index =>
        {
            double t = Math.PI * 2d * index / segments;
            double u = 16d * Math.Pow(Math.Sin(t), 3d) * scale;
            double w = (13d * Math.Cos(t) - 5d * Math.Cos(2d * t) - 2d * Math.Cos(3d * t) - Math.Cos(4d * t)) * scale;
            return (u, w);
        }).ToList();
    }

    private List<(double U, double W)> ModelTriangleContour()
    {
        double half = _modelPrimitiveDiameter * 0.5, height = _modelPrimitiveRise;
        return [(-half, -height * 0.5), (half, -height * 0.5), (0d, height * 0.5)];
    }

    private List<(double U, double W)> ModelSectorContour()
    {
        int segments = Math.Clamp(_modelPrimitiveSegments, 3, 128);
        double radius = _modelPrimitiveDiameter * 0.5;
        double inner = _modelPrimitiveHollow ? Math.Max(0d, radius - _modelPrimitiveWall) : 0d;
        double sweep = _modelPrimitiveSweep * Math.PI / 180d;
        List<(double U, double W)> contour = [];
        if (inner <= 0.0001) contour.Add((0d, 0d));
        for (int i = 0; i <= segments; i++)
        {
            double angle = -sweep * 0.5 + sweep * i / segments;
            contour.Add((Math.Cos(angle) * radius, Math.Sin(angle) * radius));
        }
        if (inner > 0.0001)
        {
            for (int i = segments; i >= 0; i--)
            {
                double angle = -sweep * 0.5 + sweep * i / segments;
                contour.Add((Math.Cos(angle) * inner, Math.Sin(angle) * inner));
            }
        }
        return contour;
    }
}
