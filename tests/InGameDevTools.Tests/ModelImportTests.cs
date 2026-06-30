using InGameDevTools.Animations;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;

namespace InGameDevTools.Tests;

public sealed class ModelImportTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void ImportGroup_RenamesConflictingTextureCodeAndElementName()
    {
        object target = ParseDocument(
            """
            {
              "textureWidth": 16,
              "textureHeight": 16,
              "textures": { "all": "block/amber" },
              "elements": [
                {
                  "name": "Shell",
                  "from": [0, 0, 0],
                  "to": [16, 16, 16],
                  "faces": { "north": { "texture": "#all", "uv": [0, 0, 16, 16] } }
                }
              ]
            }
            """,
            "shapes/block/amber.json");
        object source = ParseDocument(
            """
            {
              "textureWidth": 32,
              "textureHeight": 32,
              "textures": { "all": "item/feather" },
              "elements": [
                {
                  "name": "Shell",
                  "from": [1, 2, 3],
                  "to": [4, 5, 6],
                  "faces": { "north": { "texture": "#all", "uv": [0, 0, 32, 32] } }
                }
              ]
            }
            """,
            "shapes/item/feather.json");

        object group = BuildImportGroup(target, source, "shapes/item/feather.json", out int importedElements, out int addedTextures, out int renamedTextures);

        Assert.Equal(1, importedElements);
        Assert.Equal(1, addedTextures);
        Assert.Equal(1, renamedTextures);
        Assert.Equal("feather", GetMember(group, "Name"));
        Assert.Equal(new double[] { 2.5, 3.5, 4.5 }, (double[])GetMember(group, "From"));

        IList children = (IList)GetMember(group, "Children");
        object imported = children[0]!;
        Assert.Equal("Shell2", GetMember(imported, "Name"));
        Assert.Equal("all2", FaceTexture(imported, 0));

        IDictionary textureSizes = (IDictionary)GetMember(target, "TextureSizes");
        Assert.True(textureSizes.Contains("all2"));
        Assert.Equal(new[] { 32, 32 }, (int[])textureSizes["all2"]!);
        Assert.Contains(Textures(target), texture => texture.Code == "all" && texture.Path == "block/amber");
        Assert.Contains(Textures(target), texture => texture.Code == "all2" && texture.Path == "item/feather");
    }

    [Fact]
    public void ImportGroup_ReusesCompatibleTextureCode()
    {
        object target = ParseDocument(
            """
            {
              "textureWidth": 16,
              "textureHeight": 16,
              "textures": { "all": "item/feather" },
              "elements": []
            }
            """,
            "shapes/block/amber.json");
        object source = ParseDocument(
            """
            {
              "textureWidth": 16,
              "textureHeight": 16,
              "textures": { "all": "item/feather" },
              "elements": [
                {
                  "name": "Barb",
                  "from": [0, 0, 0],
                  "to": [1, 4, 1],
                  "faces": { "north": { "texture": "#all", "uv": [0, 0, 1, 4] } }
                }
              ]
            }
            """,
            "shapes/item/feather.json");

        object group = BuildImportGroup(target, source, "shapes/item/feather.json", out int importedElements, out int addedTextures, out int renamedTextures);

        Assert.Equal(1, importedElements);
        Assert.Equal(0, addedTextures);
        Assert.Equal(0, renamedTextures);
        object imported = ((IList)GetMember(group, "Children"))[0]!;
        Assert.Equal("all", FaceTexture(imported, 0));
        Assert.Single(Textures(target));
    }

    private static object ParseDocument(string json, string assetPath)
    {
        DebugWindowManager manager = CreateUninitializedManager();
        MethodInfo parse = typeof(DebugWindowManager).GetMethod("ModelTryParseDocument", InstanceFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelTryParseDocument");
        object?[] args = [json, "game", assetPath, false, null, ""];
        bool ok = (bool)parse.Invoke(manager, args)!;
        if (!ok)
        {
            throw new InvalidOperationException((string?)args[5] ?? "Parse failed.");
        }

        return args[4] ?? throw new InvalidOperationException("Parse returned no document.");
    }

    private static object BuildImportGroup(object target, object source, string sourceAssetPath, out int importedElements, out int addedTextures, out int renamedTextures)
    {
        MethodInfo build = typeof(DebugWindowManager).GetMethod("ModelBuildImportedShapeGroup", StaticFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelBuildImportedShapeGroup");
        object?[] args = [target, source, sourceAssetPath, null, null, null];
        object group = build.Invoke(null, args)
            ?? throw new InvalidOperationException("Import group was not built.");
        importedElements = Convert.ToInt32(args[3]);
        addedTextures = Convert.ToInt32(args[4]);
        renamedTextures = Convert.ToInt32(args[5]);
        return group;
    }

    private static string FaceTexture(object element, int faceIndex)
    {
        Array faces = (Array)GetMember(element, "Faces");
        object face = faces.GetValue(faceIndex)
            ?? throw new InvalidOperationException($"Face {faceIndex} is missing.");
        return (string)GetMember(face, "Texture");
    }

    private static List<(string Code, string Path)> Textures(object document)
    {
        return ((IEnumerable)GetMember(document, "Textures"))
            .Cast<object>()
            .Select(texture => ((string)GetMember(texture, "Code"), (string)GetMember(texture, "Path")))
            .ToList();
    }

    private static DebugWindowManager CreateUninitializedManager()
    {
#pragma warning disable SYSLIB0050
        return (DebugWindowManager)FormatterServices.GetUninitializedObject(typeof(DebugWindowManager));
#pragma warning restore SYSLIB0050
    }

    private static object GetMember(object target, string name)
    {
        Type type = target.GetType();
        FieldInfo? field = type.GetField(name, InstanceFlags);
        if (field != null) return field.GetValue(target) ?? throw new MissingMemberException(type.FullName, name);
        PropertyInfo? property = type.GetProperty(name, InstanceFlags);
        if (property != null) return property.GetValue(target) ?? throw new MissingMemberException(type.FullName, name);
        throw new MissingMemberException(type.FullName, name);
    }
}
