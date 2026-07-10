using InGameDevTools.Animations;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;

namespace InGameDevTools.Tests;

public sealed class ModelMeshLibTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void NonCuboidRoundTrip_PreservesTypedAndUnknownFields()
    {
        const string json = """
            {
              "textureWidth": 32,
              "textureHeight": 16,
              "textures": { "main": "game:block/metal/plate/copper" },
              "elements": [
                {
                  "name": "triangle",
                  "from": [3, 2, 8],
                  "to": [13, 14, 8],
                  "noncuboid": {
                    "futureMeshFlag": { "keep": true },
                    "vertices": [[3, 2, 8], [13, 2, 8], [8, 14, 8]],
                    "faces": [
                      {
                        "v": [0, 1, 2],
                        "texture": "#main",
                        "uv": [[0, 16], [32, 16], [16, 0]],
                        "glow": 17,
                        "shade": false,
                        "futureFaceFlag": "keep"
                      }
                    ]
                  }
                }
              ]
            }
            """;

        object document = ParseDocument(json);
        string serialized = SerializeDocument(document);
        JObject root = JObject.Parse(serialized);
        JObject noncuboid = (JObject)root["elements"]![0]!["noncuboid"]!;
        JObject face = (JObject)noncuboid["faces"]![0]!;

        Assert.Equal(3, ((JArray)noncuboid["vertices"]!).Count);
        Assert.True(noncuboid["futureMeshFlag"]!["keep"]!.Value<bool>());
        Assert.Equal("#main", face["texture"]!.Value<string>());
        Assert.Equal(17, face["glow"]!.Value<int>());
        Assert.False(face["shade"]!.Value<bool>());
        Assert.Equal("keep", face["futureFaceFlag"]!.Value<string>());
    }

    [Fact]
    public void MalformedNonCuboid_RoundTripsVerbatimInsteadOfDroppingData()
    {
        const string json = """
            {
              "textures": { "main": "" },
              "elements": [
                {
                  "name": "future",
                  "from": [0, 0, 0],
                  "to": [1, 1, 1],
                  "noncuboid": { "vertices": "future-format", "faces": [], "keep": 42 }
                }
              ]
            }
            """;

        JObject output = JObject.Parse(SerializeDocument(ParseDocument(json)));
        JObject noncuboid = (JObject)output["elements"]![0]!["noncuboid"]!;

        Assert.Equal("future-format", noncuboid["vertices"]!.Value<string>());
        Assert.Equal(42, noncuboid["keep"]!.Value<int>());
    }

    [Fact]
    public void Validation_MatchesMeshLibDegeneracyUvAndGlowRules()
    {
        object document = ParseDocument(
            """
            {
              "textures": { "main": "" },
              "elements": [
                {
                  "name": "invalid",
                  "from": [0, 0, 0],
                  "to": [1, 1, 1],
                  "noncuboid": {
                    "vertices": [[0, 0, 0], [1, 0, 0], [2, 0, 0]],
                    "faces": [{ "v": [0, 1, 2], "texture": "##main", "uv": [[0, 0]], "glow": 300 }]
                  }
                }
              ]
            }
            """);
        object element = ((IList)GetMember(document, "Roots"))[0]!;
        object mesh = GetMember(element, "NonCuboid");
        MethodInfo validate = typeof(DebugWindowManager).GetMethod("ModelValidateNonCuboid", StaticFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelValidateNonCuboid");
        IEnumerable<string> errors = ((IEnumerable)validate.Invoke(null, [mesh])!).Cast<string>();
        string joined = string.Join("\n", errors);

        Assert.Contains("degenerate", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("texture", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uv count", joined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0..255", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Cylinder / Prism")]
    [InlineData("Cone")]
    [InlineData("Sphere / Dome")]
    [InlineData("Torus / Arch")]
    [InlineData("Pyramid / Frustum")]
    [InlineData("Wedge / Stairs")]
    [InlineData("Capsule")]
    [InlineData("Helix / Spiral")]
    [InlineData("Box tube")]
    [InlineData("Star (flat)")]
    [InlineData("Cross / Plus (flat)")]
    [InlineData("Arrow (flat)")]
    [InlineData("Heart (flat)")]
    [InlineData("Triangle (flat)")]
    [InlineData("Disc sector (flat)")]
    public void EveryPrimitiveKind_ProducesOneValidMeshLibElement(string label)
    {
        object element = BuildMeshPrimitive(label);
        object mesh = GetMember(element, "NonCuboid");
        IList vertices = (IList)GetMember(mesh, "Vertices");
        IList faces = (IList)GetMember(mesh, "Faces");
        MethodInfo validate = typeof(DebugWindowManager).GetMethod("ModelValidateNonCuboid", StaticFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelValidateNonCuboid");
        IList errors = (IList)validate.Invoke(null, [mesh])!;

        Assert.True(vertices.Count >= 3, label);
        Assert.NotEmpty(faces);
        Assert.Empty(errors);
        Assert.Empty((IList)GetMember(element, "Children"));
        foreach (object face in faces)
        {
            int[] indices = (int[])GetMember(face, "Vertices");
            Assert.True(indices.Length is 3 or 4, $"{label} emitted a {indices.Length}-corner face.");
            Assert.All(indices, index => Assert.InRange(index, 0, vertices.Count - 1));
            object? uvValue = GetNullableMember(face, "Uv");
            if (uvValue is IList uv) Assert.Equal(indices.Length, uv.Count);
        }
    }

    [Fact]
    public void ConnectedFaceRegion_HasOneSimpleSixEdgeBoundary()
    {
        object mesh = ParseMesh(
            """
            {
              "vertices": [[0,0,0],[1,0,0],[2,0,0],[0,1,0],[1,1,0],[2,1,0]],
              "faces": [
                {"v":[0,1,4,3],"texture":"#main"},
                {"v":[1,2,5,4],"texture":"#main"}
              ]
            }
            """);
        MethodInfo buildRegions = typeof(DebugWindowManager).GetMethod("ModelTryBuildMeshFaceRegions", StaticFlags)!;
        object?[] args = [mesh, new HashSet<int> { 0, 1 }, null, ""];

        Assert.True((bool)buildRegions.Invoke(null, args)!);
        IList regions = (IList)args[2]!;
        Assert.Single(regions);

        MethodInfo boundaryMethod = typeof(DebugWindowManager).GetMethod("ModelMeshRegionBoundary", StaticFlags)!;
        object boundary = boundaryMethod.Invoke(null, [mesh, regions[0]])!;
        Assert.Equal(6, ((ICollection)boundary).Count);
        MethodInfo singleLoop = typeof(DebugWindowManager).GetMethod("ModelMeshBoundaryIsSingleLoop", StaticFlags)!;
        Assert.True((bool)singleLoop.Invoke(null, [boundary])!);
        MethodInfo coplanar = typeof(DebugWindowManager).GetMethod("ModelMeshRegionIsCoplanar", StaticFlags)!;
        Assert.True((bool)coplanar.Invoke(null, [mesh, regions[0]])!);
    }

    [Fact]
    public void SharedMidpoint_IsReusedForBothEdgeDirections()
    {
        object mesh = ParseMesh(
            """
            {"vertices":[[0,0,0],[2,0,0],[0,2,0]],"faces":[{"v":[0,1,2],"texture":"#main"}]}
            """);
        Type edgeType = typeof(DebugWindowManager).GetNestedType("ModelMeshEdge", BindingFlags.NonPublic)!;
        Type dictionaryType = typeof(Dictionary<,>).MakeGenericType(edgeType, typeof(int));
        object dictionary = Activator.CreateInstance(dictionaryType)!;
        MethodInfo midpoint = typeof(DebugWindowManager).GetMethod("ModelGetOrCreateMeshMidpoint", StaticFlags)!;

        int forward = (int)midpoint.Invoke(null, [mesh, dictionary, 0, 1])!;
        int reverse = (int)midpoint.Invoke(null, [mesh, dictionary, 1, 0])!;
        IList vertices = (IList)GetMember(mesh, "Vertices");

        Assert.Equal(forward, reverse);
        Assert.Equal(4, vertices.Count);
        Assert.Equal(new[] { 1d, 0d, 0d }, (double[])vertices[forward]!);
    }

    [Fact]
    public void Mirror_ReversesMeshWindingUvAndCoordinates()
    {
        object document = ParseDocument(
            """
            {
              "textures":{"main":""},
              "elements":[{
                "name":"tri","from":[0,0,0],"to":[2,2,0],
                "noncuboid":{"vertices":[[0,0,0],[2,0,0],[0,2,0]],"faces":[{"v":[0,1,2],"texture":"#main","uv":[[0,0],[2,0],[0,2]]}]}
              }]
            }
            """);
        object element = ((IList)GetMember(document, "Roots"))[0]!;
        object mesh = GetMember(element, "NonCuboid");
        object face = ((IList)GetMember(mesh, "Faces"))[0]!;
        DebugWindowManager manager = CreateUninitializedManager();
        MethodInfo mirror = typeof(DebugWindowManager).GetMethod("ModelMirrorElementSubtree", InstanceFlags)!;

        mirror.Invoke(manager, [element, 0]);

        IList vertices = (IList)GetMember(mesh, "Vertices");
        Assert.Equal(-2d, ((double[])vertices[1]!)[0]);
        Assert.Equal(new[] { 2, 1, 0 }, (int[])GetMember(face, "Vertices"));
        IList uv = (IList)GetMember(face, "Uv");
        Assert.Equal(new[] { 0f, 2f }, (float[])uv[0]!);
    }

    private static object BuildMeshPrimitive(string label)
    {
        DebugWindowManager manager = CreateUninitializedManager();
        SetField(manager, "_modelDoc", CreateModelDocument());
        SetEnumField(manager, "_modelEditorMode", "MeshLib");
        SetField(manager, "_modelPrimitiveKindIndex", PrimitiveKindIndex(label));
        SetField(manager, "_modelPrimitiveStepped", false);
        SetField(manager, "_modelPrimitiveCullInternalFaces", true);
        SetField(manager, "_modelPrimitiveAxis", 1);
        SetField(manager, "_modelPrimitiveCenter", new System.Numerics.Vector3(8f, 8f, 8f));
        SetField(manager, "_modelPrimitiveRotation", System.Numerics.Vector3.Zero);
        SetField(manager, "_modelPrimitiveTexture", "main");
        SetField(manager, "_modelPrimitiveDiameter", 16f);
        SetField(manager, "_modelPrimitiveHeight", 18f);
        SetField(manager, "_modelPrimitiveTopDiameter", 4f);
        SetField(manager, "_modelPrimitiveSides", 8);
        SetField(manager, "_modelPrimitiveLayers", 8);
        SetField(manager, "_modelPrimitiveHollow", false);
        SetField(manager, "_modelPrimitiveWall", 2f);
        SetField(manager, "_modelPrimitiveMinor", 4f);
        SetField(manager, "_modelPrimitiveSegments", 16);
        SetField(manager, "_modelPrimitiveSweep", 270f);
        SetField(manager, "_modelPrimitiveStep", 1f);
        SetField(manager, "_modelPrimitiveDome", 0);
        SetField(manager, "_modelPrimitiveDepth", 16f);
        SetField(manager, "_modelPrimitiveRise", 10f);
        SetField(manager, "_modelPrimitiveTopScale", 0.35f);
        SetField(manager, "_modelPrimitiveTurns", 1.5f);
        SetField(manager, "_modelPrimitiveThickness", 1f);
        SetField(manager, "_modelPrimitiveStarSquares", 2);

        MethodInfo build = typeof(DebugWindowManager).GetMethod("ModelBuildPrimitive", InstanceFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelBuildPrimitive");
        object?[] args = [null];
        object? result = build.Invoke(manager, args);
        Assert.True(result != null, $"{label}: {args[0]}");
        return result!;
    }

    private static object ParseDocument(string json)
    {
        DebugWindowManager manager = CreateUninitializedManager();
        MethodInfo parse = typeof(DebugWindowManager).GetMethod("ModelTryParseDocument", InstanceFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelTryParseDocument");
        object?[] args = [json, "test", "shapes/item/mesh.json", false, null, ""];
        bool ok = (bool)parse.Invoke(manager, args)!;
        Assert.True(ok, args[5]?.ToString());
        return args[4]!;
    }

    private static object ParseMesh(string nonCuboidJson)
    {
        object document = ParseDocument($$"""
            {
              "textures":{"main":""},
              "elements":[{"name":"mesh","from":[0,0,0],"to":[2,2,2],"noncuboid":{{nonCuboidJson}}}]
            }
            """);
        object element = ((IList)GetMember(document, "Roots"))[0]!;
        return GetMember(element, "NonCuboid");
    }

    private static string SerializeDocument(object document)
    {
        MethodInfo serialize = typeof(DebugWindowManager).GetMethod("ModelSerializeDocument", StaticFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelSerializeDocument");
        return (string)serialize.Invoke(null, [document, true, false])!;
    }

    private static object CreateModelDocument()
    {
        Type documentType = typeof(DebugWindowManager).GetNestedType("ModelDocumentData", BindingFlags.NonPublic)!;
        Type textureType = typeof(DebugWindowManager).GetNestedType("ModelTextureEntry", BindingFlags.NonPublic)!;
        object document = Activator.CreateInstance(documentType, nonPublic: true)!;
        object texture = Activator.CreateInstance(textureType, nonPublic: true)!;
        SetMember(texture, "Code", "main");
        ((IList)GetMember(document, "Textures")).Add(texture);
        return document;
    }

    private static int PrimitiveKindIndex(string label)
    {
        string[] labels = (string[])GetStaticMember(typeof(DebugWindowManager), "ModelPrimitiveKindLabels");
        int index = Array.IndexOf(labels, label);
        Assert.True(index >= 0, label);
        return index;
    }

    private static DebugWindowManager CreateUninitializedManager()
    {
#pragma warning disable SYSLIB0050
        return (DebugWindowManager)FormatterServices.GetUninitializedObject(typeof(DebugWindowManager));
#pragma warning restore SYSLIB0050
    }

    private static object GetStaticMember(Type type, string name)
    {
        return type.GetField(name, StaticFlags)?.GetValue(null)
            ?? type.GetProperty(name, StaticFlags)?.GetValue(null)
            ?? throw new MissingMemberException(type.FullName, name);
    }

    private static object GetMember(object target, string name)
    {
        return GetNullableMember(target, name) ?? throw new MissingMemberException(target.GetType().FullName, name);
    }

    private static object? GetNullableMember(object target, string name)
    {
        return target.GetType().GetField(name, InstanceFlags)?.GetValue(target)
            ?? target.GetType().GetProperty(name, InstanceFlags)?.GetValue(target);
    }

    private static void SetField(object target, string name, object? value)
    {
        FieldInfo field = target.GetType().GetField(name, InstanceFlags)
            ?? throw new MissingMemberException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    private static void SetEnumField(object target, string name, string value)
    {
        FieldInfo field = target.GetType().GetField(name, InstanceFlags)
            ?? throw new MissingMemberException(target.GetType().FullName, name);
        field.SetValue(target, Enum.Parse(field.FieldType, value));
    }

    private static void SetMember(object target, string name, object? value)
    {
        FieldInfo? field = target.GetType().GetField(name, InstanceFlags);
        if (field != null) { field.SetValue(target, value); return; }
        PropertyInfo? property = target.GetType().GetProperty(name, InstanceFlags);
        if (property != null) { property.SetValue(target, value); return; }
        throw new MissingMemberException(target.GetType().FullName, name);
    }
}
