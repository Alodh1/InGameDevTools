using InGameDevTools.Animations;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;

namespace InGameDevTools.Tests;

public sealed class ModelClothingTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // The seraph bones a wearable may step-parent onto (the exact KeyElement names the game binds clothing to).
    private static readonly string[] SeraphBones =
    [
        "LowerTorso", "UpperTorso", "UpperArmR", "LowerArmR", "UpperArmL", "LowerArmL",
        "Neck", "Head", "UpperFootL", "LowerFootL", "UpperFootR", "LowerFootR"
    ];

    [Fact]
    public void NoRegionsEnabled_FailsWithAHint()
    {
        DebugWindowManager manager = NewManager(out _);
        string? error = BuildRigError(manager);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void CoatPreset_StepParentsChestBellyAndSleevesOntoTheRealBones()
    {
        object rig = BuildRig(out _, "Coat");
        HashSet<string> targets = StepParentTargets(rig);

        Assert.Contains("UpperTorso", targets);
        Assert.Contains("LowerTorso", targets);
        Assert.Contains("UpperArmR", targets);
        Assert.Contains("UpperArmL", targets);
        // A long coat's sleeves reach the forearm and its tails hang off the pelvis.
        Assert.Contains("LowerArmR", targets);

        // Every step-parent target is a genuine seraph bone (never an invented name).
        foreach (string target in targets)
        {
            Assert.Contains(target, SeraphBones);
        }
    }

    [Fact]
    public void Anchors_AreFaceless_AndTheirGeometryIsTextured()
    {
        object rig = BuildRig(out _, "Robe");

        List<object> anchors = Anchors(rig).ToList();
        Assert.NotEmpty(anchors);
        foreach (object anchor in anchors)
        {
            // The step-parent anchor itself is face-less (matches vanilla clothing shapes)...
            Assert.Null(Faces(anchor)[0]);
            // ...and carries at least one textured, positive-size garment box.
            List<object> geometry = Descendants(anchor).Where(e => Size(e, "SizeX") > 0.0001).ToList();
            Assert.NotEmpty(geometry);
            Assert.All(geometry, box => Assert.NotNull(Faces(box)[0]));
        }
    }

    [Fact]
    public void TrimTexture_PaintsCuffsAndSolesSeparately()
    {
        object rig = BuildRig(out _, "Boots", configure: p =>
        {
            SetMember(p, "Texture", "leather");
            SetMember(p, "TrimTexture", "metal");
            SetMember(p, "Sole", true);
        });

        object sole = Descendants(rig).First(e => Name(e) == "sole");
        object boot = Descendants(rig).First(e => Name(e) == "boot");
        Assert.Equal("metal", FaceTexture(sole));     // sole rides the trim texture
        Assert.Equal("leather", FaceTexture(boot));   // boot body rides the main texture
    }

    [Fact]
    public void DigitigradeGreaves_TargetTheReverseKneeShin()
    {
        object rig = BuildRig(out _, "Greaves", baseShapeIndex: 1, configure: p => SetMember(p, "LegLength", 1f));
        HashSet<string> targets = StepParentTargets(rig);
        // Digitigrade has a 3-segment leg; the shin is MiddleFoot, not LowerFoot (which is the paw).
        Assert.Contains("UpperFootR", targets);
        Assert.Contains("MiddleFootR", targets);
    }

    [Fact]
    public void SeraphGreaves_TargetTheLowerFootShin()
    {
        object rig = BuildRig(out _, "Greaves", baseShapeIndex: 0, configure: p => SetMember(p, "LegLength", 1f));
        HashSet<string> targets = StepParentTargets(rig);
        Assert.Contains("UpperFootR", targets);
        Assert.Contains("LowerFootR", targets);
        Assert.DoesNotContain("MiddleFootR", targets);
    }

    [Fact]
    public void BootsPreset_ClothesBothFeet()
    {
        object rig = BuildRig(out _, "Boots");
        HashSet<string> targets = StepParentTargets(rig);
        Assert.Contains("LowerFootR", targets);
        Assert.Contains("LowerFootL", targets);
    }

    [Fact]
    public void HelmetJson_IsArmorWithProtectionModifiers()
    {
        (DebugWindowManager manager, _) = SetupManager("Helmet");
        JObject item = (JObject)Invoke(manager, "ClothingBuildItemJson");
        JObject attributes = (JObject)item["attributes"]!;

        Assert.Equal("armorhead", attributes["clothescategory"]!.ToString());
        JObject? protection = (JObject?)attributes["protectionModifiers"];
        Assert.NotNull(protection);
        Assert.Equal(2, (int)protection!["protectionTier"]!);
        Assert.Equal("Wearable", item["behaviors"]![0]!["name"]!.ToString());
    }

    [Fact]
    public void ClothingJson_HasWarmthNotProtection_AndTransparentBodyTexture()
    {
        (DebugWindowManager manager, _) = SetupManager("Coat", configure: p => SetMember(p, "Texture", "wool"));
        JObject item = (JObject)Invoke(manager, "ClothingBuildItemJson");
        JObject attributes = (JObject)item["attributes"]!;

        Assert.Equal("upperbodyover", attributes["clothescategory"]!.ToString());
        Assert.Null(attributes["protectionModifiers"]);
        Assert.True((double)attributes["warmth"]! > 0);
        Assert.True((bool)attributes["wearableAttachment"]!);

        // The wearer body inherits the seraph texture, mapped transparent so only the garment renders.
        JObject textures = (JObject)item["textures"]!;
        Assert.Equal("game:block/transparent", textures["seraph"]!["base"]!.ToString());
        Assert.NotNull(textures["wool"]);
    }

    [Fact]
    public void HeadSlotJson_CarriesRainProtection()
    {
        (DebugWindowManager manager, _) = SetupManager("Hood", configure: p => SetMember(p, "RainProtection", 0.5f));
        JObject item = (JObject)Invoke(manager, "ClothingBuildItemJson");
        Assert.Equal(0.5, (double)((JObject)item["attributes"]!)["rainProtectionPerc"]!, 3);
    }

    [Fact]
    public void ElementCount_StaysWithinTheGeneratorBudget()
    {
        // A maximal outfit (every region on, deep skirt/cape) must not blow the element cap.
        object rig = BuildRig(out string? error, "Robe", configure: p =>
        {
            foreach (string region in new[]
            {
                "ClotheHead", "ClotheFace", "ClotheNeck", "ClotheChest", "ClotheBelly", "ClotheShoulders",
                "ClotheArms", "ClotheHands", "ClotheWaist", "ClotheLegs", "ClotheFeet", "ClotheCape"
            })
            {
                SetMember(p, region, true);
            }
            SetMember(p, "Pouches", 4);
            SetMember(p, "CapeSegments", 8);
            SetMember(p, "SkirtSegments", 6);
        });
        Assert.True(string.IsNullOrEmpty(error), error);
        Assert.NotEmpty(Anchors(rig));
    }

    [Fact]
    public void WildwoodCloakPreset_HasTatteredBranchesAndLeaves_StepParentedToCapeAndHood()
    {
        object rig = BuildRig(out string? error, "WildwoodCloak");
        Assert.True(string.IsNullOrEmpty(error), error);
        List<string> names = Descendants(rig).Select(Name).ToList();

        Assert.Contains(names, n => n.Contains("branch"));
        Assert.Contains(names, n => n.Contains("tatter"));
        Assert.Contains(names, n => n.Contains("leaf"));

        HashSet<string> targets = StepParentTargets(rig);
        Assert.Contains("UpperTorso", targets);   // the cape + its branches/tatters
        Assert.Contains("Head", targets);          // the hood
        // Everything is genuinely step-parented onto real bones.
        foreach (string target in targets) Assert.Contains(target, SeraphBones);
    }

    [Fact]
    public void Branches_ForkAndGrowLeafClusters()
    {
        object rig = BuildRig(out _, "Cloak", configure: p =>
        {
            SetMember(p, "Branches", true);
            SetMember(p, "BranchSurface", 0 /* Cape */);
            SetMember(p, "BranchCount", 4);
            SetMember(p, "BranchForks", 2);
            SetMember(p, "BranchLeaves", true);
            SetMember(p, "Wear", 0f);
        });
        List<string> names = Descendants(rig).Select(Name).ToList();

        Assert.Contains(names, n => n.Contains("branch"));
        Assert.Contains(names, n => n.Contains("f0"));    // a fork
        Assert.Contains(names, n => n.Contains("leaf"));  // a leaf cluster
    }

    [Fact]
    public void Decoration_FallsBackToADecorAnchor_WhenTheRegionGarmentIsOff()
    {
        // Branches placed on the chest with no chest garment must still attach (a Decor anchor on UpperTorso).
        object rig = BuildRig(out _, configure: p =>
        {
            ApplyMinimalCape(p);
            SetMember(p, "Branches", true);
            SetMember(p, "BranchSurface", 2 /* Chest */);
            SetMember(p, "BranchCount", 5);
        });
        object decor = Anchors(rig).First(a => Name(a) == "DecorUpperTorso");
        Assert.Equal("UpperTorso", (string)GetMember(decor, "StepParentName"));
        Assert.Contains(Descendants(decor), e => Name(e).Contains("branch"));
    }

    [Fact]
    public void AccentTrimAndBaseTextures_RouteByElementKind()
    {
        object rig = BuildRig(out _, configure: p =>
        {
            ApplyMinimalCape(p);
            SetMember(p, "Texture", "cloth");
            SetMember(p, "TrimTexture", "metal");
            SetMember(p, "AccentTexture", "wood");
            SetMember(p, "Branches", true); SetMember(p, "BranchSurface", 0); SetMember(p, "BranchCount", 3); SetMember(p, "BranchLeaves", false);
            SetMember(p, "Studs", true); SetMember(p, "StudSurface", 0); SetMember(p, "StudCount", 3);
        });

        Assert.Equal("wood", FaceTexture(Descendants(rig).First(e => Name(e).Contains("branch"))));
        Assert.Equal("metal", FaceTexture(Descendants(rig).First(e => Name(e).Contains("stud"))));
        Assert.Equal("cloth", FaceTexture(Descendants(rig).First(e => Name(e).StartsWith("cape", StringComparison.Ordinal))));
    }

    [Fact]
    public void Plating_BuildsARowColGrid()
    {
        object rig = BuildRig(out _, configure: p =>
        {
            SetMember(p, "ClotheChest", true);
            SetMember(p, "Plating", 3 /* Plate */);
            SetMember(p, "PlatePlacement", 0 /* Chest */);
            SetMember(p, "PlateRows", 3);
            SetMember(p, "PlateCols", 3);
            SetMember(p, "Wear", 0f);
        });
        Assert.Equal(9, Descendants(rig).Count(e => Name(e).Contains("plate_")));
    }

    [Fact]
    public void Wear_RemovesSomeScatteredPieces()
    {
        int pristine = CountBranches(0f);
        int worn = CountBranches(0.8f);
        Assert.True(worn < pristine, $"worn {worn} should be fewer than pristine {pristine}");

        static int CountBranches(float wear)
        {
            object rig = BuildRig(out _, configure: p =>
            {
                ApplyMinimalCape(p);
                SetMember(p, "Branches", true); SetMember(p, "BranchSurface", 0);
                SetMember(p, "BranchCount", 20); SetMember(p, "BranchForks", 0); SetMember(p, "BranchLeaves", false);
                SetMember(p, "BranchSegments", 1); SetMember(p, "Wear", wear);
            });
            return Descendants(rig).Count(e => Name(e).Contains("branch"));
        }
    }

    private static void ApplyMinimalCape(object p)
    {
        SetMember(p, "ClotheCape", true);
        SetMember(p, "CapeSegments", 2);
    }

    // ---- harness (mirrors PlayerModelTests' reflection helpers) ----

    private static object BuildRig(out string? error, string? preset = null, int baseShapeIndex = 0, Action<object>? configure = null)
    {
        (DebugWindowManager manager, _) = SetupManager(preset, baseShapeIndex, configure);
        MethodInfo build = typeof(DebugWindowManager).GetMethod("ClothingBuildRig", InstanceFlags)!;
        object?[] args = [null];
        object? rig = build.Invoke(manager, args);
        error = args[0]?.ToString();
        return rig ?? throw new InvalidOperationException("Clothing rig was null: " + error);
    }

    private static string? BuildRigError(DebugWindowManager manager)
    {
        MethodInfo build = typeof(DebugWindowManager).GetMethod("ClothingBuildRig", InstanceFlags)!;
        object?[] args = [null];
        build.Invoke(manager, args);
        return args[0]?.ToString();
    }

    private static (DebugWindowManager Manager, object Parameters) SetupManager(string? preset = null, int baseShapeIndex = 0, Action<object>? configure = null)
    {
        DebugWindowManager manager = NewManager(out object parameters);
        SetMember(parameters, "BaseShapeIndex", baseShapeIndex);
        if (preset != null)
        {
            Type archetype = typeof(DebugWindowManager).GetNestedType("ClothingArchetype", BindingFlags.NonPublic)!;
            Invoke(manager, "ClothingApplyArchetype", Enum.Parse(archetype, preset));
        }
        configure?.Invoke(parameters);
        return (manager, parameters);
    }

    private static DebugWindowManager NewManager(out object parameters)
    {
#pragma warning disable SYSLIB0050
        DebugWindowManager manager = (DebugWindowManager)FormatterServices.GetUninitializedObject(typeof(DebugWindowManager));
#pragma warning restore SYSLIB0050
        SetField(manager, "_modelDoc", CreateModelDocument());
        parameters = CreateClothingParams();
        SetField(manager, "_clothingParams", parameters);
        return manager;
    }

    private static HashSet<string> StepParentTargets(object rig)
    {
        return Anchors(rig).Select(a => (string)GetMember(a, "StepParentName")).ToHashSet();
    }

    private static IEnumerable<object> Anchors(object rig)
    {
        return Descendants(rig).Where(e => !string.IsNullOrEmpty((string)GetMember(e, "StepParentName")));
    }

    private static string FaceTexture(object element)
    {
        object?[] faces = (object?[])GetMember(element, "Faces");
        return (string)GetMember(faces[0]!, "Texture");
    }

    private static object?[] Faces(object element) => (object?[])GetMember(element, "Faces");

    private static object Invoke(object target, string method, params object?[] args)
    {
        MethodInfo info = target.GetType().GetMethod(method, InstanceFlags)
            ?? throw new MissingMethodException(target.GetType().FullName, method);
        return info.Invoke(target, args)!;
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

    private static string Name(object element) => (string)GetMember(element, "Name");

    private static double Size(object element, string member) => Convert.ToDouble(GetMember(element, member));

    private static object CreateClothingParams()
    {
        Type type = typeof(DebugWindowManager).GetNestedType("ClothingParams", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(DebugWindowManager), "ClothingParams");
        return Activator.CreateInstance(type, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create clothing params.");
    }

    private static object CreateModelDocument()
    {
        Type managerType = typeof(DebugWindowManager);
        Type documentType = managerType.GetNestedType("ModelDocumentData", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(DebugWindowManager), "ModelDocumentData");
        Type textureType = managerType.GetNestedType("ModelTextureEntry", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(DebugWindowManager), "ModelTextureEntry");

        object document = Activator.CreateInstance(documentType, nonPublic: true)!;
        object texture = Activator.CreateInstance(textureType, nonPublic: true)!;
        SetMember(texture, "Code", "cloth");
        SetMember(texture, "Path", "block/cloth/plain");
        ((IList)GetMember(document, "Textures")).Add(texture);
        return document;
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

    private static void SetField(object target, string name, object? value)
    {
        FieldInfo field = target.GetType().GetField(name, InstanceFlags)
            ?? throw new MissingMemberException(target.GetType().FullName, name);
        field.SetValue(target, value);
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
