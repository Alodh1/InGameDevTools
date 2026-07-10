using InGameDevTools.Animations;
using System.Collections;
using System.Reflection;

namespace InGameDevTools.Tests;

public sealed class ModelGeneratedMeshTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly double[] From = [-3.5d, 1.25d, 4.75d];
    private static readonly double[] To = [8.5d, 14.25d, 19.75d];

    [Theory]
    [InlineData("Box")]
    [InlineData("ChamferedBox")]
    [InlineData("Tube")]
    [InlineData("Ellipsoid")]
    [InlineData("Cone")]
    [InlineData("ExtrudedContour")]
    [InlineData("Wedge")]
    [InlineData("Leaf")]
    [InlineData("Membrane")]
    [InlineData("Dome")]
    [InlineData("Ring")]
    [InlineData("BoxTube")]
    [InlineData("Jewel")]
    public void EveryGeneratedMeshKind_IsValidBoundedAndUvComplete(string kind)
    {
        foreach (int axis in Enumerable.Range(0, 3))
        {
            foreach (int sign in new[] { -1, 1 })
            {
                object mesh = BuildMesh(kind, axis, sign, startScale: 1d, endScale: 0.55d);
                IList vertices = (IList)GetMember(mesh, "Vertices");
                IList faces = (IList)GetMember(mesh, "Faces");
                string context = $"{kind}, axis {axis}, sign {sign}";

                Assert.True(vertices.Count >= 3, context);
                Assert.NotEmpty(faces);
                Assert.Empty(Validate(mesh));
                AssertExactBounds(vertices, context);
                AssertClosedOutwardWinding(mesh, context);

                foreach (object face in faces)
                {
                    int[] indices = (int[])GetMember(face, "Vertices");
                    Assert.True(indices.Length is 3 or 4, $"{context} emitted a {indices.Length}-corner face.");
                    Assert.All(indices, index => Assert.InRange(index, 0, vertices.Count - 1));

                    IList uv = (IList)GetMember(face, "Uv");
                    Assert.Equal(indices.Length, uv.Count);
                    foreach (float[] coordinate in uv.Cast<float[]>())
                    {
                        Assert.Equal(2, coordinate.Length);
                        Assert.All(coordinate, value => Assert.InRange(value, 0f, 16f));
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("Box")]
    [InlineData("ChamferedBox")]
    [InlineData("Tube")]
    [InlineData("Ellipsoid")]
    [InlineData("Cone")]
    [InlineData("ExtrudedContour")]
    [InlineData("Wedge")]
    [InlineData("Leaf")]
    [InlineData("Membrane")]
    [InlineData("Dome")]
    [InlineData("Ring")]
    [InlineData("BoxTube")]
    [InlineData("Jewel")]
    public void EveryGeneratedMeshKind_HasDeterministicSemanticTopology(string kind)
    {
        object first = BuildMesh(kind, axis: 2, sign: -1, startScale: 1d, endScale: 0.4d);
        object second = BuildMesh(kind, axis: 2, sign: -1, startScale: 1d, endScale: 0.4d);

        AssertMeshesEqual(first, second);

        if (!string.Equals(kind, "Box", StringComparison.Ordinal))
        {
            int vertexCount = ((IList)GetMember(first, "Vertices")).Count;
            int faceCount = ((IList)GetMember(first, "Faces")).Count;
            Assert.False(vertexCount == 8 && faceCount == 6, $"{kind} unexpectedly used box topology.");
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    [InlineData(1, 1)]
    [InlineData(1, -1)]
    [InlineData(2, 1)]
    [InlineData(2, -1)]
    public void TubeProfile_AppliesTaperAtTheDirectedEnd(int axis, int sign)
    {
        const double endScale = 0.35d;
        object mesh = BuildMesh("Tube", axis, sign, startScale: 1d, endScale: endScale);
        IList vertices = (IList)GetMember(mesh, "Vertices");
        double startCoordinate = sign > 0 ? From[axis] : To[axis];
        double endCoordinate = sign > 0 ? To[axis] : From[axis];
        int[] crossAxes = Enumerable.Range(0, 3).Where(candidate => candidate != axis).ToArray();
        double[] center =
        [
            (From[0] + To[0]) * 0.5d,
            (From[1] + To[1]) * 0.5d,
            (From[2] + To[2]) * 0.5d
        ];

        double startRadius = vertices.Cast<double[]>()
            .Where(vertex => vertex[axis] == startCoordinate)
            .Max(vertex => Math.Abs(vertex[crossAxes[0]] - center[crossAxes[0]]));
        double endRadius = vertices.Cast<double[]>()
            .Where(vertex => vertex[axis] == endCoordinate)
            .Max(vertex => Math.Abs(vertex[crossAxes[0]] - center[crossAxes[0]]));

        Assert.Equal(endScale, endRadius / startRadius, 5);
        Assert.Empty(Validate(mesh));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    [InlineData(1, 1)]
    [InlineData(1, -1)]
    [InlineData(2, 1)]
    [InlineData(2, -1)]
    public void TubeProfile_TaperedAtBothEndsRetainsExactCompatibilityBounds(int axis, int sign)
    {
        object mesh = BuildMesh("Tube", axis, sign, startScale: 0.94d, endScale: 0.86d);
        IList vertices = (IList)GetMember(mesh, "Vertices");

        AssertExactBounds(vertices, $"Tube, axis {axis}, sign {sign}, both ends tapered");
        Assert.Empty(Validate(mesh));
        AssertClosedOutwardWinding(mesh, $"Tube, axis {axis}, sign {sign}, both ends tapered");
    }

    [Fact]
    public void ConeProfile_UvSeamStaysContinuousAcrossTipTriangles()
    {
        object mesh = BuildMesh("Cone", axis: 2, sign: 1, startScale: 1d, endScale: 0d);
        IList vertices = (IList)GetMember(mesh, "Vertices");
        IList faces = (IList)GetMember(mesh, "Faces");

        foreach (object face in faces)
        {
            int[] indices = (int[])GetMember(face, "Vertices");
            double span = indices.Max(index => ((double[])vertices[index]!)[2]) -
                indices.Min(index => ((double[])vertices[index]!)[2]);
            if (span <= 0.000001d) continue;
            IList uv = (IList)GetMember(face, "Uv");
            float[] u = uv.Cast<float[]>().Select(coordinate => coordinate[0]).ToArray();
            Assert.True(u.Max() - u.Min() <= 2.0001f, $"Cone seam face spans U {u.Min()}..{u.Max()}.");
        }
    }

    [Fact]
    public void RingProfile_UsesContinuousMajorAndMinorPerimeterUvs()
    {
        object mesh = BuildMesh("Ring", axis: 0, sign: 1, startScale: 1d, endScale: 1d);
        foreach (object face in (IList)GetMember(mesh, "Faces"))
        {
            IList uv = (IList)GetMember(face, "Uv");
            float[] u = uv.Cast<float[]>().Select(coordinate => coordinate[0]).ToArray();
            float[] v = uv.Cast<float[]>().Select(coordinate => coordinate[1]).ToArray();
            Assert.True(u.Max() - u.Min() <= 2.0001f, $"Ring major seam spans U {u.Min()}..{u.Max()}.");
            Assert.True(v.Max() - v.Min() <= 4.0001f, $"Ring minor seam spans V {v.Min()}..{v.Max()}.");
        }
    }

    private static object BuildMesh(
        string kind,
        int axis,
        int sign,
        double startScale,
        double endScale)
    {
        Type managerType = typeof(DebugWindowManager);
        Type kindType = managerType.GetNestedType("ModelGeneratedMeshKind", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(DebugWindowManager), "ModelGeneratedMeshKind");
        object kindValue = Enum.Parse(kindType, kind);
        MethodInfo createSpec = managerType.GetMethod("ModelGeneratedSpec", StaticFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelGeneratedSpec");
        object spec = createSpec.Invoke(null, [kindValue, axis, sign, 8, 4, startScale, endScale])
            ?? throw new InvalidOperationException("Generated mesh spec creation returned null.");
        object element = CreateElement();
        MethodInfo build = managerType.GetMethod("ModelBuildGeneratedMesh", StaticFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelBuildGeneratedMesh");
        return build.Invoke(null, [element, spec, "test"])
            ?? throw new InvalidOperationException($"{kind} mesh generation returned null.");
    }

    private static object CreateElement()
    {
        Type elementType = typeof(DebugWindowManager).GetNestedType("ModelElementData", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(DebugWindowManager), "ModelElementData");
        object element = Activator.CreateInstance(elementType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create a model element.");
        SetMember(element, "Name", "generated-test");
        SetMember(element, "From", (double[])From.Clone());
        SetMember(element, "To", (double[])To.Clone());
        return element;
    }

    private static IEnumerable<object> Validate(object mesh)
    {
        MethodInfo validate = typeof(DebugWindowManager).GetMethod("ModelValidateNonCuboid", StaticFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelValidateNonCuboid");
        return ((IEnumerable)validate.Invoke(null, [mesh])!).Cast<object>();
    }

    private static void AssertExactBounds(IList vertices, string context)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            double minimum = vertices.Cast<double[]>().Min(vertex => vertex[axis]);
            double maximum = vertices.Cast<double[]>().Max(vertex => vertex[axis]);
            Assert.True(minimum == From[axis], $"{context}: axis {axis} minimum was {minimum}, expected {From[axis]}.");
            Assert.True(maximum == To[axis], $"{context}: axis {axis} maximum was {maximum}, expected {To[axis]}.");
        }
    }

    private static void AssertClosedOutwardWinding(object mesh, string context)
    {
        IList vertices = (IList)GetMember(mesh, "Vertices");
        IList faces = (IList)GetMember(mesh, "Faces");
        Dictionary<(int A, int B), (int Count, int Balance)> edges = [];
        double signedVolume = 0d;

        foreach (object face in faces)
        {
            int[] indices = (int[])GetMember(face, "Vertices");
            for (int index = 0; index < indices.Length; index++)
            {
                int from = indices[index];
                int to = indices[(index + 1) % indices.Length];
                (int A, int B) edge = from < to ? (from, to) : (to, from);
                (int Count, int Balance) current = edges.GetValueOrDefault(edge);
                edges[edge] = (current.Count + 1, current.Balance + (from < to ? 1 : -1));
            }

            signedVolume += SignedTetrahedronVolume(
                (double[])vertices[indices[0]]!,
                (double[])vertices[indices[1]]!,
                (double[])vertices[indices[2]]!);
            if (indices.Length == 4)
            {
                signedVolume += SignedTetrahedronVolume(
                    (double[])vertices[indices[0]]!,
                    (double[])vertices[indices[2]]!,
                    (double[])vertices[indices[3]]!);
            }
        }

        Assert.All(edges, pair =>
        {
            Assert.True(pair.Value.Count == 2, $"{context}: edge {pair.Key} is used {pair.Value.Count} times.");
            Assert.True(pair.Value.Balance == 0, $"{context}: edge {pair.Key} has inconsistent face winding.");
        });
        Assert.True(signedVolume > 0.000001d, $"{context}: signed volume {signedVolume} is not outward.");
    }

    private static double SignedTetrahedronVolume(double[] a, double[] b, double[] c)
    {
        return (a[0] * (b[1] * c[2] - b[2] * c[1])
            + a[1] * (b[2] * c[0] - b[0] * c[2])
            + a[2] * (b[0] * c[1] - b[1] * c[0])) / 6d;
    }

    private static void AssertMeshesEqual(object expected, object actual)
    {
        IList expectedVertices = (IList)GetMember(expected, "Vertices");
        IList actualVertices = (IList)GetMember(actual, "Vertices");
        Assert.Equal(expectedVertices.Count, actualVertices.Count);
        for (int index = 0; index < expectedVertices.Count; index++)
        {
            Assert.Equal((double[])expectedVertices[index]!, (double[])actualVertices[index]!);
        }

        IList expectedFaces = (IList)GetMember(expected, "Faces");
        IList actualFaces = (IList)GetMember(actual, "Faces");
        Assert.Equal(expectedFaces.Count, actualFaces.Count);
        for (int index = 0; index < expectedFaces.Count; index++)
        {
            object expectedFace = expectedFaces[index]!;
            object actualFace = actualFaces[index]!;
            Assert.Equal((int[])GetMember(expectedFace, "Vertices"), (int[])GetMember(actualFace, "Vertices"));
            Assert.Equal(GetMember(expectedFace, "Texture"), GetMember(actualFace, "Texture"));

            IList expectedUv = (IList)GetMember(expectedFace, "Uv");
            IList actualUv = (IList)GetMember(actualFace, "Uv");
            Assert.Equal(expectedUv.Count, actualUv.Count);
            for (int uvIndex = 0; uvIndex < expectedUv.Count; uvIndex++)
            {
                Assert.Equal((float[])expectedUv[uvIndex]!, (float[])actualUv[uvIndex]!);
            }
        }
    }

    private static object GetMember(object target, string name)
    {
        Type type = target.GetType();
        FieldInfo? field = type.GetField(name, InstanceFlags);
        if (field != null) return field.GetValue(target)!;
        PropertyInfo? property = type.GetProperty(name, InstanceFlags);
        if (property != null) return property.GetValue(target)!;
        throw new MissingMemberException(type.FullName, name);
    }

    private static void SetMember(object target, string name, object? value)
    {
        Type type = target.GetType();
        FieldInfo? field = type.GetField(name, InstanceFlags);
        if (field != null)
        {
            field.SetValue(target, value);
            return;
        }

        PropertyInfo? property = type.GetProperty(name, InstanceFlags);
        if (property != null)
        {
            property.SetValue(target, value);
            return;
        }

        throw new MissingMemberException(type.FullName, name);
    }
}
