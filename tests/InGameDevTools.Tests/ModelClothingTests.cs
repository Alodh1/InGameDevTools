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

    public static IEnumerable<object[]> ClothingPresetCases =>
    [
        ["Hood"], ["Cap"], ["Helmet"], ["Mask"], ["Necklace"], ["Scarf"], ["Shirt"], ["Jacket"],
        ["Coat"], ["Robe"], ["Tabard"], ["Cuirass"], ["Pauldrons"], ["Bracers"], ["Gloves"],
        ["Gauntlets"], ["Belt"], ["Trousers"], ["Skirt"], ["Greaves"], ["Boots"], ["Cloak"],
        ["Emblem"], ["WildwoodCloak"], ["DruidRobe"], ["PlatedArmor"], ["Brigand"], ["RegalMantle"],
        ["RaggedShroud"]
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
    public void AccentTexture_HasItsOwnItemTextureMapping()
    {
        (DebugWindowManager manager, object parameters) = SetupManager("WildwoodCloak");
        SetMember(parameters, "Texture", "cloth");
        SetMember(parameters, "TrimTexture", "metal");
        SetMember(parameters, "AccentTexture", "wood");

        JObject item = (JObject)Invoke(manager, "ClothingBuildItemJson");
        JObject textures = (JObject)item["textures"]!;

        Assert.NotNull(textures["cloth"]);
        Assert.NotNull(textures["metal"]);
        Assert.NotNull(textures["wood"]);
    }

    [Fact]
    public void MeshLib_ConvertsOnlyRenderableGarmentDescendants()
    {
        object rig = BuildRig(out string? error, "Coat", meshLib: true);
        Assert.True(string.IsNullOrEmpty(error), error);

        List<object> anchors = Anchors(rig).ToList();
        Assert.NotEmpty(anchors);
        HashSet<object> garment = [];
        foreach (object anchor in anchors)
        {
            garment.Add(anchor);
            Assert.Null(GetMemberOrNull(anchor, "NonCuboid"));
            Assert.All(Faces(anchor), Assert.Null);

            foreach (object element in Descendants(anchor))
            {
                garment.Add(element);
                if (Size(element, "SizeX") <= 0.0001 || Size(element, "SizeY") <= 0.0001 || Size(element, "SizeZ") <= 0.0001)
                {
                    continue;
                }

                object? mesh = GetMemberOrNull(element, "NonCuboid");
                Assert.True(mesh != null, $"{Name(element)} did not receive MeshLib geometry");
                Assert.True((bool)GetMember(mesh!, "Editable"));
                Assert.NotEmpty((IEnumerable)GetMember(mesh!, "Vertices"));
                Assert.NotEmpty((IEnumerable)GetMember(mesh!, "Faces"));
                Assert.All(Faces(element), Assert.Null);
            }
        }

        // The grey wearer is preview-only and must remain a vanilla rig; it is never emitted by ClothingCommit.
        List<object> wearer = Descendants(rig).Where(element => !garment.Contains(element)).ToList();
        Assert.NotEmpty(wearer);
        Assert.All(wearer, element => Assert.Null(GetMemberOrNull(element, "NonCuboid")));
    }

    [Fact]
    public void MeshLib_PreservesGarmentNamesHierarchyBoundsAndStepParents()
    {
        object vanilla = BuildRig(out _, "Robe");
        object meshLib = BuildRig(out _, "Robe", meshLib: true);
        object[] vanillaGarment = Anchors(vanilla).SelectMany(Subtree).ToArray();
        object[] meshGarment = Anchors(meshLib).SelectMany(Subtree).ToArray();

        Assert.Equal(vanillaGarment.Length, meshGarment.Length);
        for (int index = 0; index < vanillaGarment.Length; index++)
        {
            object expected = vanillaGarment[index];
            object actual = meshGarment[index];
            Assert.Equal(Name(expected), Name(actual));
            Assert.Equal((double[])GetMember(expected, "From"), (double[])GetMember(actual, "From"));
            Assert.Equal((double[])GetMember(expected, "To"), (double[])GetMember(actual, "To"));
            Assert.Equal((double[]?)GetMemberOrNull(expected, "RotationOrigin"),
                (double[]?)GetMemberOrNull(actual, "RotationOrigin"));
            Assert.Equal((string)GetMember(expected, "StepParentName"), (string)GetMember(actual, "StepParentName"));
            Assert.Equal(GetMemberOrNull(expected, "Parent") is object expectedParent ? Name(expectedParent) : null,
                GetMemberOrNull(actual, "Parent") is object actualParent ? Name(actualParent) : null);
        }
    }

    [Fact]
    public void MeshLib_UsesSemanticClothOrganicArmorAndHardwareProfiles()
    {
        object wildwood = BuildRig(out _, "WildwoodCloak", meshLib: true);
        object branchMesh = Mesh(Descendants(wildwood).First(element => Name(element).Contains("branch", StringComparison.Ordinal)));
        object leafMesh = Mesh(Descendants(wildwood).First(element => Name(element).Contains("leaf", StringComparison.Ordinal)));
        object tatterMesh = Mesh(Descendants(wildwood).First(element => Name(element).Contains("tatter", StringComparison.Ordinal)));
        Assert.True(((IList)GetMember(branchMesh, "Vertices")).Count > 8); // tapered round loft
        Assert.NotEqual(6, ((IList)GetMember(leafMesh, "Faces")).Count);  // leaf contour
        Assert.NotEqual(6, ((IList)GetMember(tatterMesh, "Faces")).Count); // ribbon/membrane

        object armor = BuildRig(out _, "PlatedArmor", meshLib: true);
        object plateMesh = Mesh(Descendants(armor).First(element => Name(element).Contains("plate_", StringComparison.Ordinal)));
        object studMesh = Mesh(Descendants(armor).First(element => Name(element).Contains("stud", StringComparison.Ordinal)));
        Assert.NotEqual(8, ((IList)GetMember(plateMesh, "Vertices")).Count); // shaped plate
        Assert.True(((IList)GetMember(studMesh, "Vertices")).Count > 8);    // dome

        object belt = BuildRig(out _, "Belt", meshLib: true);
        object buckleMesh = Mesh(Descendants(belt).First(element => Name(element) == "buckle"));
        Assert.True(((IList)GetMember(buckleMesh, "Vertices")).Count > 8); // open box tube

        object mantle = BuildRig(out _, "RegalMantle", meshLib: true);
        object gemMesh = Mesh(Descendants(mantle).First(element => Name(element).Contains("gem", StringComparison.Ordinal)));
        Assert.Equal(8, ((IList)GetMember(gemMesh, "Faces")).Count);       // faceted jewel
    }

    [Fact]
    public void MeshLib_PreservesBaseTrimAndAccentTexturesOnMeshFaces()
    {
        object rig = BuildRig(out _, configure: p =>
        {
            ApplyMinimalCape(p);
            SetMember(p, "Texture", "cloth");
            SetMember(p, "TrimTexture", "metal");
            SetMember(p, "AccentTexture", "wood");
            SetMember(p, "Branches", true); SetMember(p, "BranchSurface", 0); SetMember(p, "BranchCount", 3); SetMember(p, "BranchLeaves", false);
            SetMember(p, "Studs", true); SetMember(p, "StudSurface", 0); SetMember(p, "StudCount", 3);
        }, meshLib: true);

        Assert.Equal("wood", MeshFaceTexture(Descendants(rig).First(element => Name(element).Contains("branch", StringComparison.Ordinal))));
        Assert.Equal("metal", MeshFaceTexture(Descendants(rig).First(element => Name(element).Contains("stud", StringComparison.Ordinal))));
        Assert.Equal("cloth", MeshFaceTexture(Descendants(rig).First(element => Name(element).StartsWith("cape", StringComparison.Ordinal))));
    }

    [Theory]
    [MemberData(nameof(ClothingPresetCases))]
    public void EveryPreset_PreservesVanillaStructureAndBuildsValidMeshLibGarments(string preset)
    {
        object vanilla = BuildRig(out string? vanillaError, preset);
        object meshLib = BuildRig(out string? meshError, preset, meshLib: true);
        Assert.True(string.IsNullOrEmpty(vanillaError), vanillaError);
        Assert.True(string.IsNullOrEmpty(meshError), meshError);

        object[] vanillaGarment = Anchors(vanilla).SelectMany(Subtree).ToArray();
        object[] meshGarment = Anchors(meshLib).SelectMany(Subtree).ToArray();
        Assert.Equal(vanillaGarment.Length, meshGarment.Length);
        Assert.Equal(vanillaGarment.Select(Name), meshGarment.Select(Name));

        MethodInfo validate = typeof(DebugWindowManager).GetMethod(
            "ModelValidateNonCuboid", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelValidateNonCuboid");
        for (int index = 0; index < vanillaGarment.Length; index++)
        {
            object expected = vanillaGarment[index];
            object actual = meshGarment[index];
            Assert.Equal((double[])GetMember(expected, "From"), (double[])GetMember(actual, "From"));
            Assert.Equal((double[])GetMember(expected, "To"), (double[])GetMember(actual, "To"));
            Assert.Equal((string)GetMember(expected, "StepParentName"), (string)GetMember(actual, "StepParentName"));
            Assert.Equal((double[]?)GetMemberOrNull(expected, "RotationOrigin"),
                (double[]?)GetMemberOrNull(actual, "RotationOrigin"));
            Assert.Equal(Size(expected, "RotationX"), Size(actual, "RotationX"));
            Assert.Equal(Size(expected, "RotationY"), Size(actual, "RotationY"));
            Assert.Equal(Size(expected, "RotationZ"), Size(actual, "RotationZ"));
            Assert.Equal(Name(GetMember(expected, "Parent")), Name(GetMember(actual, "Parent")));

            if (!string.IsNullOrEmpty((string)GetMember(actual, "StepParentName")) ||
                Size(actual, "SizeX") <= 0.0001 || Size(actual, "SizeY") <= 0.0001 || Size(actual, "SizeZ") <= 0.0001)
            {
                Assert.Null(GetMemberOrNull(actual, "NonCuboid"));
                continue;
            }

            Assert.Null(GetMemberOrNull(expected, "NonCuboid"));
            Assert.NotNull(Faces(expected)[0]);
            object mesh = Mesh(actual);
            IEnumerable errors = (IEnumerable)validate.Invoke(null, [mesh])!;
            Assert.Empty(errors.Cast<object>());
            Assert.All(Faces(actual), Assert.Null);
        }
    }

    [Fact]
    public void GeneratedMeshLibGarment_RoundTripsHierarchyAnchorsTransformsTexturesAndUnknownMetadata()
    {
        (DebugWindowManager manager, _) = SetupManager("WildwoodCloak", configure: parameters =>
        {
            SetMember(parameters, "Texture", "cloth");
            SetMember(parameters, "TrimTexture", "metal");
            SetMember(parameters, "AccentTexture", "wood");
        }, meshLib: true);
        MethodInfo build = typeof(DebugWindowManager).GetMethod("ClothingBuildRig", InstanceFlags)!;
        object?[] buildArgs = [null];
        object rig = build.Invoke(manager, buildArgs)
            ?? throw new InvalidOperationException("Clothing rig was null: " + buildArgs[0]);
        Assert.True(string.IsNullOrEmpty(buildArgs[0]?.ToString()), buildArgs[0]?.ToString());

        object document = GetMember(manager, "_modelDoc");
        IList roots = (IList)GetMember(document, "Roots");
        foreach (object anchor in Anchors(rig))
        {
            roots.Add(Invoke(anchor, "CloneSubtree"));
        }
        Invoke(manager, "ClothingEnsureTextures");
        SetMember(document, "Extra", new JObject { ["futureRoot"] = new JObject { ["keep"] = true } });
        object decorated = roots.Cast<object>().SelectMany(Subtree)
            .First(element => GetMemberOrNull(element, "NonCuboid") != null);
        SetMember(decorated, "Extra", new JObject { ["futureGenerated"] = 42 });

        MethodInfo serialize = typeof(DebugWindowManager).GetMethod(
            "ModelSerializeDocument", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        string json = (string)serialize.Invoke(null, [document, true, false])!;
        Assert.DoesNotContain("GeneratedMeshSpec", json, StringComparison.Ordinal);

        MethodInfo parse = typeof(DebugWindowManager).GetMethod("ModelTryParseDocument", InstanceFlags)!;
        object?[] parseArgs = [json, "test", "shapes/item/generated-garment.json", false, null, ""];
        Assert.True((bool)parse.Invoke(manager, parseArgs)!, parseArgs[5]?.ToString());
        object parsed = parseArgs[4]!;

        object[] expected = roots.Cast<object>().SelectMany(Subtree).ToArray();
        object[] actual = ((IList)GetMember(parsed, "Roots")).Cast<object>().SelectMany(Subtree).ToArray();
        Assert.Equal(expected.Select(Name), actual.Select(Name));
        int decoratedIndex = Array.IndexOf(expected, decorated);
        Assert.InRange(decoratedIndex, 0, expected.Length - 1);
        for (int index = 0; index < expected.Length; index++)
        {
            object before = expected[index];
            object after = actual[index];
            Assert.Equal((double[])GetMember(before, "From"), (double[])GetMember(after, "From"));
            Assert.Equal((double[])GetMember(before, "To"), (double[])GetMember(after, "To"));
            Assert.Equal((double[]?)GetMemberOrNull(before, "RotationOrigin"),
                (double[]?)GetMemberOrNull(after, "RotationOrigin"));
            Assert.Equal(Size(before, "RotationX"), Size(after, "RotationX"));
            Assert.Equal(Size(before, "RotationY"), Size(after, "RotationY"));
            Assert.Equal(Size(before, "RotationZ"), Size(after, "RotationZ"));
            Assert.Equal((string)GetMember(before, "StepParentName"), (string)GetMember(after, "StepParentName"));

            object? beforeMesh = GetMemberOrNull(before, "NonCuboid");
            object? afterMesh = GetMemberOrNull(after, "NonCuboid");
            if (beforeMesh == null)
            {
                Assert.Null(afterMesh);
                continue;
            }
            Assert.NotNull(afterMesh);
            IList beforeVertices = (IList)GetMember(beforeMesh, "Vertices");
            IList afterVertices = (IList)GetMember(afterMesh!, "Vertices");
            Assert.Equal(beforeVertices.Count, afterVertices.Count);
            for (int vertex = 0; vertex < beforeVertices.Count; vertex++)
            {
                Assert.Equal((double[])beforeVertices[vertex]!, (double[])afterVertices[vertex]!);
            }
            IList beforeFaces = (IList)GetMember(beforeMesh, "Faces");
            IList afterFaces = (IList)GetMember(afterMesh!, "Faces");
            Assert.Equal(beforeFaces.Count, afterFaces.Count);
            for (int face = 0; face < beforeFaces.Count; face++)
            {
                Assert.Equal((int[])GetMember(beforeFaces[face]!, "Vertices"),
                    (int[])GetMember(afterFaces[face]!, "Vertices"));
                Assert.Equal((string)GetMember(beforeFaces[face]!, "Texture"),
                    (string)GetMember(afterFaces[face]!, "Texture"));
            }
            Assert.All(Faces(after), Assert.Null);
        }

        Dictionary<string, string> expectedTextures = ((IEnumerable)GetMember(document, "Textures")).Cast<object>()
            .ToDictionary(texture => (string)GetMember(texture, "Code"), texture => (string)GetMember(texture, "Path"), StringComparer.Ordinal);
        Dictionary<string, string> actualTextures = ((IEnumerable)GetMember(parsed, "Textures")).Cast<object>()
            .ToDictionary(texture => (string)GetMember(texture, "Code"), texture => (string)GetMember(texture, "Path"), StringComparer.Ordinal);
        Assert.Equal(expectedTextures.OrderBy(pair => pair.Key), actualTextures.OrderBy(pair => pair.Key));
        Assert.True(((JObject)GetMember(parsed, "Extra"))["futureRoot"]!["keep"]!.Value<bool>());
        object parsedDecorated = actual[decoratedIndex];
        Assert.Equal(42, ((JObject)GetMember(parsedDecorated, "Extra"))["futureGenerated"]!.Value<int>());
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

    private static object BuildRig(out string? error, string? preset = null, int baseShapeIndex = 0,
        Action<object>? configure = null, bool meshLib = false)
    {
        (DebugWindowManager manager, _) = SetupManager(preset, baseShapeIndex, configure, meshLib);
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

    private static (DebugWindowManager Manager, object Parameters) SetupManager(string? preset = null,
        int baseShapeIndex = 0, Action<object>? configure = null, bool meshLib = false)
    {
        DebugWindowManager manager = NewManager(out object parameters, meshLib);
        SetMember(parameters, "BaseShapeIndex", baseShapeIndex);
        if (preset != null)
        {
            Type archetype = typeof(DebugWindowManager).GetNestedType("ClothingArchetype", BindingFlags.NonPublic)!;
            Invoke(manager, "ClothingApplyArchetype", Enum.Parse(archetype, preset));
        }
        configure?.Invoke(parameters);
        return (manager, parameters);
    }

    private static DebugWindowManager NewManager(out object parameters, bool meshLib = false)
    {
#pragma warning disable SYSLIB0050
        DebugWindowManager manager = (DebugWindowManager)FormatterServices.GetUninitializedObject(typeof(DebugWindowManager));
#pragma warning restore SYSLIB0050
        SetField(manager, "_modelDoc", CreateModelDocument());
        if (meshLib)
        {
            FieldInfo mode = typeof(DebugWindowManager).GetField("_modelEditorMode", InstanceFlags)
                ?? throw new MissingMemberException(nameof(DebugWindowManager), "_modelEditorMode");
            SetField(manager, "_modelEditorMode", Enum.Parse(mode.FieldType, "MeshLib"));
        }
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

    private static object Mesh(object element)
    {
        return GetMemberOrNull(element, "NonCuboid")
            ?? throw new InvalidOperationException($"{Name(element)} has no MeshLib geometry");
    }

    private static string MeshFaceTexture(object element)
    {
        object mesh = Mesh(element);
        object face = ((IList)GetMember(mesh, "Faces"))[0]!;
        return (string)GetMember(face, "Texture");
    }

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

    private static IEnumerable<object> Subtree(object root)
    {
        yield return root;
        foreach (object descendant in Descendants(root)) yield return descendant;
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
        if (field != null) { field.SetValue(target, value); return; }
        PropertyInfo? property = target.GetType().GetProperty(name, InstanceFlags);
        if (property != null) { property.SetValue(target, value); return; }
        throw new MissingMemberException(target.GetType().FullName, name);
    }
}
