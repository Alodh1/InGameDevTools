using InGameDevTools.Animations;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;

namespace InGameDevTools.Tests;

public sealed class PlayerModelTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // The exact KeyElements PlayerModelLib's two base shapes require by name (config/baseshapes/*.json).
    private static readonly string[] SeraphKeyElements =
    [
        "LowerTorso", "UpperTorso", "UpperArmR", "LowerArmR", "UpperArmL", "LowerArmL",
        "Neck", "Head", "UpperFootL", "LowerFootL", "UpperFootR", "LowerFootR"
    ];

    private static readonly string[] DigitigradeKeyElements =
    [
        "LowerTorso", "UpperTorso", "Tail", "UpperArmR", "LowerArmR", "UpperArmL", "LowerArmL",
        "Neck", "Head", "Snout", "UpperFootL", "MiddleFootL", "LowerFootL",
        "UpperFootR", "MiddleFootR", "LowerFootR", "HeadWithSnout", "AboveSnout", "SnoutHeadWide"
    ];

    [Fact]
    public void SeraphSkeleton_ContainsEveryKeyElementByExactName()
    {
        object root = BuildShape(baseShapeIndex: 0);
        List<string> names = SubtreeNames(root);
        foreach (string key in SeraphKeyElements)
        {
            Assert.Contains(key, names);
        }
        // The seraph rig is plantigrade: it must NOT carry the digitigrade-only middle foot joint.
        Assert.DoesNotContain("MiddleFootL", names);
    }

    [Fact]
    public void DigitigradeSkeleton_ContainsEveryKeyElementByExactName()
    {
        object root = BuildShape(baseShapeIndex: 1);
        List<string> names = SubtreeNames(root);
        foreach (string key in DigitigradeKeyElements)
        {
            Assert.Contains(key, names);
        }
    }

    [Fact]
    public void FacelessAnchorsStayFaceless_AndEverythingElseIsTextured()
    {
        object root = BuildShape(baseShapeIndex: 1);
        string[] faceless = ["HeadWithSnout", "AboveSnout", "SnoutHeadWide"];

        foreach (object element in Descendants(root))
        {
            string name = ElementName(element);
            object?[] faces = (object?[])GetMember(element, "Faces");
            if (faceless.Contains(name))
            {
                Assert.Null(faces[0]);
            }
            else
            {
                Assert.NotNull(faces[0]);
            }
        }
    }

    [Fact]
    public void Head_CarriesTheEyesAttachmentPoint()
    {
        object root = BuildShape(baseShapeIndex: 0);
        object head = First(root, "Head");
        object? extra = GetMemberOrNull(head, "Extra");
        Assert.NotNull(extra);

        JObject attachments = (JObject)extra!;
        JToken? points = attachments["attachmentpoints"];
        Assert.NotNull(points);
        Assert.Contains(points!, token => token["code"]?.ToString() == "Eyes");
    }

    [Fact]
    public void UpperTorso_CarriesTheBackAttachmentPoint()
    {
        object root = BuildShape(baseShapeIndex: 0);
        object torso = First(root, "UpperTorso");
        JObject extra = (JObject)GetMember(torso, "Extra");
        Assert.Contains(extra["attachmentpoints"]!, token => token["code"]?.ToString() == "Back");
    }

    [Fact]
    public void Root_SitsAtOriginAndFeetHangBelowTheTorso()
    {
        object root = BuildShape(baseShapeIndex: 0);
        double[] rootFrom = (double[])GetMember(root, "From");
        Assert.Equal(0.0, rootFrom[0], 3);
        Assert.Equal(0.0, rootFrom[1], 3);
        Assert.Equal(0.0, rootFrom[2], 3);

        // The lower foot's world Y is well below the lower torso's (15) - the legs hang down.
        double footY = WorldFrom(First(root, "LowerFootR"))[1];
        Assert.True(footY < 8.0, $"foot world Y = {footY}");
    }

    [Fact]
    public void Arms_MirrorAcrossTheLateralAxis()
    {
        object root = BuildShape(baseShapeIndex: 0);
        double[] right = WorldCenter(First(root, "UpperArmR"));
        double[] left = WorldCenter(First(root, "UpperArmL"));

        // Lateral is Z, centred on the body (~8). Right and left mirror; fore-aft (X) and height (Y) match.
        Assert.True(right[2] < 8.0 && left[2] > 8.0, $"right z={right[2]}, left z={left[2]}");
        Assert.Equal(right[0], left[0], 1);
        Assert.Equal(right[1], left[1], 1);
    }

    [Fact]
    public void HeadScale_GrowsTheHeadWithoutAddingElements()
    {
        object baseRoot = BuildShape(baseShapeIndex: 0);
        object bigRoot = BuildShape(baseShapeIndex: 0, configure: p => SetMember(p, "HeadScale", 2f));

        double baseSize = GetDouble(First(baseRoot, "Head"), "SizeY");
        double bigSize = GetDouble(First(bigRoot, "Head"), "SizeY");
        Assert.InRange(bigSize / baseSize, 1.8, 2.2);
        Assert.Equal(CountDescendants(baseRoot), CountDescendants(bigRoot));
    }

    [Fact]
    public void HeadFeatures_AddTheirNamedBoxes()
    {
        object root = BuildShape(baseShapeIndex: 0, configure: p =>
        {
            SetMember(p, "Muzzle", true);
            SetMember(p, "Nose", true);
            SetMember(p, "Ears", 1);
            SetMember(p, "HornPairs", 1);
            SetMember(p, "Eyes", true);
            SetMember(p, "Pupils", true);
            SetMember(p, "Jaw", true);
            SetMember(p, "Fangs", 2);
            SetMember(p, "Crest", true);
            SetMember(p, "CrestCount", 4);
        });
        List<string> names = SubtreeNames(root);

        Assert.Contains("Muzzle", names);
        Assert.Contains("Nose", names);
        Assert.Equal(2, names.Count(n => n is "EarR" or "EarL"));
        // One horn pair, 2 segments each => two segments on each side.
        Assert.Equal(2, names.Count(n => n.StartsWith("HornR")));
        Assert.Equal(2, names.Count(n => n.StartsWith("HornL")));
        Assert.Equal(2, names.Count(n => n is "EyeR" or "EyeL"));
        Assert.Equal(2, names.Count(n => n.EndsWith("Pupil")));
        Assert.Contains("Jaw", names);
        Assert.Equal(8, names.Count(n => n.StartsWith("Fang")));   // 2 per side, upper + lower
        Assert.Equal(4, names.Count(n => n.StartsWith("Crest")));
    }

    [Fact]
    public void Claws_FanAcrossBothFeet_AndOptionallyHands()
    {
        object feetOnly = BuildShape(baseShapeIndex: 0, configure: p => SetMember(p, "Claws", 3));
        Assert.Equal(6, SubtreeNames(feetOnly).Count(n => n.Contains("ClawFoot")));

        object withHands = BuildShape(baseShapeIndex: 0, configure: p =>
        {
            SetMember(p, "Claws", 3);
            SetMember(p, "HandClaws", true);
        });
        Assert.Equal(6, SubtreeNames(withHands).Count(n => n.Contains("ClawHand")));
    }

    [Fact]
    public void Tail_ExtendsDigitigradeAndAddsOneToSeraph()
    {
        object digi = BuildShape(baseShapeIndex: 1, configure: p =>
        {
            SetMember(p, "Tail", true);
            SetMember(p, "TailSegments", 4);
        });
        // Digitigrade keeps its base "Tail" and appends a "TailSeg" chain.
        Assert.Contains("Tail", SubtreeNames(digi));
        Assert.Equal(4, SubtreeNames(digi).Count(n => n.StartsWith("TailSeg")));

        object seraph = BuildShape(baseShapeIndex: 0, configure: p =>
        {
            SetMember(p, "Tail", true);
            SetMember(p, "TailSegments", 4);
        });
        // Seraph has no base tail, so the chain itself is named "Tail".
        Assert.Equal(4, SubtreeNames(seraph).Count(n => n.StartsWith("Tail")));
    }

    [Fact]
    public void Config_SerializesWithTheChosenBaseShapeCode()
    {
        (DebugWindowManager manager, _) = BuildShapeCore(baseShapeIndex: 1, configure: p =>
        {
            SetMember(p, "Code", "My Beast");
            SetMember(p, "Group", "beasts");
        });

        JObject config = (JObject)Invoke(manager, "PlayerModelBuildConfig");
        // The config is keyed by the sanitized code.
        JObject body = (JObject)config["my-beast"]!;
        Assert.Equal("playermodellib:digitigrade", body["BaseShapeCode"]!.ToString());
        Assert.True((bool)body["CreateCuboidsForAttachmentPoints"]!);
        Assert.Equal(2, ((JArray)body["CollisionBox"]!).Count);
        Assert.NotNull(body["SkinnableParts"]);
    }

    [Fact]
    public void Config_SeraphBaseUsesSeraphCode()
    {
        (DebugWindowManager manager, _) = BuildShapeCore(baseShapeIndex: 0, configure: p => SetMember(p, "Code", "raccoon"));
        JObject config = (JObject)Invoke(manager, "PlayerModelBuildConfig");
        Assert.Equal("playermodellib:seraph", config["raccoon"]!["BaseShapeCode"]!.ToString());
    }

    // ---- helpers (mirror ModelCreatureTests' reflection harness) ----

    private static object BuildShape(int baseShapeIndex, Action<object>? configure = null)
    {
        return BuildShapeCore(baseShapeIndex, configure).Root;
    }

    private static (DebugWindowManager Manager, object Root) BuildShapeCore(int baseShapeIndex, Action<object>? configure = null)
    {
        DebugWindowManager manager = CreateUninitializedManager();
        object document = CreateModelDocument();
        SetField(manager, "_modelDoc", document);

        object parameters = CreatePlayerModelParams();
        SetMember(parameters, "BaseShapeIndex", baseShapeIndex);
        SetMember(parameters, "Texture", "all");
        configure?.Invoke(parameters);
        SetField(manager, "_playerModelParams", parameters);

        MethodInfo build = typeof(DebugWindowManager).GetMethod("PlayerModelBuildShape", InstanceFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "PlayerModelBuildShape");
        object?[] args = [null];
        object root = build.Invoke(manager, args)
            ?? throw new InvalidOperationException("Player model generation returned null: " + args[0]);
        Assert.True(string.IsNullOrEmpty(args[0]?.ToString()), args[0]?.ToString());
        return (manager, root);
    }

    private static object Invoke(object target, string method, params object?[] args)
    {
        MethodInfo info = target.GetType().GetMethod(method, InstanceFlags)
            ?? throw new MissingMethodException(target.GetType().FullName, method);
        return info.Invoke(target, args)!;
    }

    private static List<string> SubtreeNames(object root)
    {
        return Descendants(root).Select(ElementName).ToList();
    }

    private static double[] WorldFrom(object element)
    {
        double[] sum = [0, 0, 0];
        for (object? current = element; current != null; current = GetMemberOrNull(current, "Parent"))
        {
            double[] from = (double[])GetMember(current, "From");
            sum[0] += from[0];
            sum[1] += from[1];
            sum[2] += from[2];
        }
        return sum;
    }

    private static double[] WorldCenter(object element)
    {
        double[] from = WorldFrom(element);
        return
        [
            from[0] + GetDouble(element, "SizeX") * 0.5,
            from[1] + GetDouble(element, "SizeY") * 0.5,
            from[2] + GetDouble(element, "SizeZ") * 0.5
        ];
    }

    private static object First(object root, string name)
    {
        foreach (object element in Descendants(root))
        {
            if (ElementName(element) == name) return element;
        }
        throw new InvalidOperationException($"No element named {name}");
    }

    private static IEnumerable<object> Descendants(object root)
    {
        foreach (object child in (IEnumerable)GetMember(root, "Children"))
        {
            yield return child;
            foreach (object descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static int CountDescendants(object root) => Descendants(root).Count();

    private static string ElementName(object element) => (string)GetMember(element, "Name");

    private static object CreatePlayerModelParams()
    {
        Type type = typeof(DebugWindowManager).GetNestedType("PlayerModelParams", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(DebugWindowManager), "PlayerModelParams");
        return Activator.CreateInstance(type, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create player model params.");
    }

    private static object CreateModelDocument()
    {
        Type managerType = typeof(DebugWindowManager);
        Type documentType = managerType.GetNestedType("ModelDocumentData", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(DebugWindowManager), "ModelDocumentData");
        Type textureType = managerType.GetNestedType("ModelTextureEntry", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(DebugWindowManager), "ModelTextureEntry");

        object document = Activator.CreateInstance(documentType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create model document.");
        object texture = Activator.CreateInstance(textureType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create texture entry.");
        SetMember(texture, "Code", "all");
        ((IList)GetMember(document, "Textures")).Add(texture);
        return document;
    }

    private static DebugWindowManager CreateUninitializedManager()
    {
#pragma warning disable SYSLIB0050
        return (DebugWindowManager)FormatterServices.GetUninitializedObject(typeof(DebugWindowManager));
#pragma warning restore SYSLIB0050
    }

    private static object GetMember(object target, string name)
    {
        return GetMemberOrNull(target, name) ?? throw new MissingMemberException(target.GetType().FullName, name);
    }

    private static object? GetMemberOrNull(object target, string name)
    {
        Type type = target.GetType();
        FieldInfo? field = type.GetField(name, InstanceFlags);
        if (field != null) return field.GetValue(target);
        PropertyInfo? property = type.GetProperty(name, InstanceFlags);
        if (property != null) return property.GetValue(target);
        throw new MissingMemberException(type.FullName, name);
    }

    private static void SetField(object target, string name, object? value)
    {
        FieldInfo field = target.GetType().GetField(name, InstanceFlags)
            ?? throw new MissingMemberException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    private static void SetMember(object target, string name, object? value)
    {
        FieldInfo? field = target.GetType().GetField(name, InstanceFlags);
        if (field != null)
        {
            field.SetValue(target, value);
            return;
        }
        PropertyInfo? property = target.GetType().GetProperty(name, InstanceFlags);
        if (property != null)
        {
            property.SetValue(target, value);
            return;
        }
        throw new MissingMemberException(target.GetType().FullName, name);
    }

    private static double GetDouble(object target, string name) => Convert.ToDouble(GetMember(target, name));
}
