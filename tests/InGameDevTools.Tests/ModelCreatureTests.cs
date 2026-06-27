using InGameDevTools.Animations;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;

namespace InGameDevTools.Tests;

public sealed class ModelCreatureTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void QuadrupedDefaults_ProduceExpectedSkeleton()
    {
        object group = BuildCreature();

        // 4 spine + 2 neck + 1 head + (2 pairs * 3 joints * 2 sides) legs + 4 tail = 23.
        Assert.Equal(23, CountDescendants(group));

        // The group is a face-less parent; every generated box is a positive-size, textured element.
        Assert.Null(((Array)GetMember(group, "Faces")).GetValue(0));
        foreach (object element in Descendants(group))
        {
            Assert.True(GetDouble(element, "SizeX") > 0, ElementName(element));
            Assert.True(GetDouble(element, "SizeY") > 0, ElementName(element));
            Assert.True(GetDouble(element, "SizeZ") > 0, ElementName(element));
            Assert.NotNull(((Array)GetMember(element, "Faces")).GetValue(0));
        }
    }

    [Fact]
    public void Legs_HangBelowBodyAndAreSymmetricAcrossCenter()
    {
        // Splay/bend/rotation left at zero so the rest pose is a pure translation chain, letting us
        // compose world positions by summing From down the parent chain (matches the renderer at rest).
        object group = BuildCreature(legSplay: 0f, legBend: 0f);
        double centerY = 8.0;

        object rightFoot = FindDeepest(group, "legRight1");
        object leftFoot = FindDeepest(group, "legLeft1");

        double[] right = WorldCenter(rightFoot);
        double[] left = WorldCenter(leftFoot);

        // Feet sit well below the body center.
        Assert.True(right[1] < centerY - 4.0, $"right foot y={right[1]}");
        Assert.True(left[1] < centerY - 4.0, $"left foot y={left[1]}");

        // The body faces +X, so left/right is the lateral Z axis. Feet mirror across the center on Z and share
        // the same fore-aft (X) and height (Y).
        Assert.True(right[2] > 8.0 && left[2] < 8.0, $"right z={right[2]}, left z={left[2]}");
        Assert.Equal(16.0, right[2] + left[2], 3);
        Assert.Equal(right[0], left[0], 3);
        Assert.Equal(right[1], left[1], 3);
    }

    [Fact]
    public void SerpentPreset_HasNoLimbsButKeepsSpineAndTail()
    {
        object group = BuildCreature(applyArchetype: 2 /* Serpent */);

        Assert.DoesNotContain(Descendants(group), element => ElementName(element).Contains("leg"));
        Assert.Contains(Descendants(group), element => ElementName(element).StartsWith("spine"));
        Assert.Contains(Descendants(group), element => ElementName(element).StartsWith("tail"));
    }

    [Theory]
    [InlineData("Creature2", "creature2")]
    [InlineData("My Dragon", "my-dragon")]
    [InlineData("leg/R\\1", "leg-r-1")]
    [InlineData("###", "shape")]
    [InlineData("  ", "shape")]
    [InlineData(null, "shape")]
    public void AnimateAssetTarget_SanitizesRootNameIntoFilename(string? input, string expected)
    {
        // The Animate hand-off derives an authored filename from the creature's root element name when the
        // document is still the untouched template. Guard that sanitization stays filesystem/asset-safe.
        MethodInfo sanitize = typeof(DebugWindowManager).GetMethod("ModelSanitizeFileName", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelSanitizeFileName");
        string result = (string)sanitize.Invoke(null, [input])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WithoutAutoTexture_EveryFaceUsesTheBaseCode()
    {
        object group = BuildCreature();

        foreach (object element in Descendants(group))
        {
            Assert.Equal("all", FaceTexture(element));
        }
    }

    [Fact]
    public void AutoTexture_AssignsItsOwnCodePerBodyRegion()
    {
        // Quadruped defaults have a spine+neck body, a head, four legs and a tail (no eyes/arms/wings).
        object group = BuildCreatureCore(autoTexture: true).Group;

        Assert.Equal("body", FaceTexture(First(group, "spine1")));
        Assert.Equal("body", FaceTexture(First(group, "neck1")));
        Assert.Equal("head", FaceTexture(First(group, "head")));
        Assert.Equal("leg", FaceTexture(FindDeepest(group, "legRight")));
        Assert.Equal("tail", FaceTexture(First(group, "tail1")));

        // Off shares one code; on must spread across several.
        Assert.True(DistinctFaceCodes(group).Count >= 4, string.Join(",", DistinctFaceCodes(group)));
    }

    [Fact]
    public void AutoTexture_FoldsHeadDetailsIntoTheHeadCode()
    {
        // Bird preset has a beak (snout); hexapod adds horns. Both must ride the shared "head" code.
        object bird = BuildCreatureCore(autoTexture: true, applyArchetype: 4 /* Bird */).Group;
        Assert.Equal("head", FaceTexture(First(bird, "snout")));

        object hexapod = BuildCreatureCore(autoTexture: true, applyArchetype: 3 /* Hexapod */).Group;
        Assert.Equal("head", FaceTexture(FindDeepest(hexapod, "horn")));
    }

    [Fact]
    public void AutoTexture_ReusesExistingCodesByNameOrPlural()
    {
        // A shape already carrying region textures should bind to them rather than invent new codes;
        // a simple plural ("legs") still matches the singular "leg" region.
        object group = BuildCreatureCore(autoTexture: true, extraTextureCodes: ["head", "legs"]).Group;

        Assert.Equal("head", FaceTexture(First(group, "head")));
        Assert.Equal("legs", FaceTexture(FindDeepest(group, "legRight")));
        Assert.Equal("body", FaceTexture(First(group, "spine1")));
    }

    [Fact]
    public void EnsureRegionTextures_AddsMissingSlotsSeededFromTheBasePath()
    {
        (DebugWindowManager manager, object group) = BuildCreatureCore(autoTexture: true);
        object document = GetMember(manager, "_modelDoc");
        SetTexturePath(document, "all", "game:block/stone");

        MethodInfo ensure = typeof(DebugWindowManager).GetMethod("ModelCreatureEnsureRegionTextures", InstanceFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelCreatureEnsureRegionTextures");
        ensure.Invoke(manager, [group, "all"]);

        Dictionary<string, string> textures = TextureMap(document);
        Assert.Equal(["all", "body", "head", "leg", "tail"], textures.Keys.OrderBy(code => code, StringComparer.Ordinal).ToArray());
        // The base "all" code keeps its image; every created region slot inherits it so it renders at once.
        Assert.Equal("game:block/stone", textures["body"]);
        Assert.Equal("game:block/stone", textures["head"]);

        // Re-running is idempotent: codes already present are not duplicated.
        ensure.Invoke(manager, [group, "all"]);
        Assert.Equal(5, TextureMap(document).Count);
    }

    [Fact]
    public void AdvancedParametersDefaultToNoOps()
    {
        // Every advanced parameter defaults so the generated geometry is identical to basic mode: the
        // quadruped default must still be exactly 23 elements (guards against an accidental non-neutral default).
        Assert.Equal(23, CountDescendants(BuildCreatureCore().Group));
    }

    [Fact]
    public void UniformScale_ScalesTheWholeCreature()
    {
        object baseGroup = BuildCreatureCore().Group;
        object bigGroup = BuildCreatureCore(configure: p => SetMember(p, "UniformScale", 2f)).Group;

        object baseHead = First(baseGroup, "head");
        object bigHead = First(bigGroup, "head");

        // Every dimension roughly doubles (ratios are robust to unit rounding).
        Assert.InRange(GetDouble(bigHead, "SizeX") / GetDouble(baseHead, "SizeX"), 1.8, 2.2);
        Assert.InRange(GetDouble(bigHead, "SizeY") / GetDouble(baseHead, "SizeY"), 1.8, 2.2);
        Assert.InRange(GetDouble(bigHead, "SizeZ") / GetDouble(baseHead, "SizeZ"), 1.8, 2.2);

        // Scaling is geometric only: it adds no elements.
        Assert.Equal(CountDescendants(baseGroup), CountDescendants(bigGroup));
    }

    [Fact]
    public void UniformScale_ClampsGeneratedUvsToTextureTile()
    {
        object group = BuildCreatureCore(configure: p => SetMember(p, "UniformScale", 4f)).Group;

        foreach (object element in Descendants(group))
        {
            foreach (object face in Faces(element))
            {
                float[] uv = (float[])GetMember(face, "Uv");
                Assert.InRange(uv[2], 0f, 16f);
                Assert.InRange(uv[3], 0f, 16f);
            }
        }
    }

    [Fact]
    public void Feet_AppendOneFootPerLeg()
    {
        // Default quadruped has 2 leg pairs => 4 legs, so feet add 4 boxes named so the locomotion generator
        // (which keys off "foot") folds them in as toe joints.
        object withFeet = BuildCreatureCore(configure: p => SetMember(p, "Feet", true)).Group;
        Assert.Equal(4, Descendants(withFeet).Count(element => ElementName(element).StartsWith("foot")));

        object withoutFeet = BuildCreatureCore().Group;
        Assert.DoesNotContain(Descendants(withoutFeet), element => ElementName(element).StartsWith("foot"));
    }

    [Fact]
    public void DorsalSpikes_AddTheRequestedCountAlongTheBack()
    {
        object group = BuildCreatureCore(configure: p => SetMember(p, "DorsalSpikes", 6)).Group;
        Assert.Equal(6, Descendants(group).Count(element => ElementName(element).StartsWith("spike")));
    }

    [Fact]
    public void LegTaper_ShrinksTheFootEndRelativeToTheHip()
    {
        // With a taper < 1 the distal leg segment must be thinner than the hip segment.
        object group = BuildCreatureCore(legSplay: 0f, configure: p =>
        {
            SetMember(p, "LegSegments", 3);
            SetMember(p, "LegTaper", 0.5f);
        }).Group;

        object hip = First(group, "legRight11");      // first segment of the right rear leg
        object foot = FindDeepest(group, "legRight1"); // deepest = distal segment
        Assert.True(GetDouble(foot, "SizeX") < GetDouble(hip, "SizeX"), $"hip={GetDouble(hip, "SizeX")} distal={GetDouble(foot, "SizeX")}");
    }

    [Fact]
    public void Shoulders_EmbedLimbRootsThatParentTheLegs()
    {
        // Default quadruped: 2 leg pairs => 4 limbs => 4 embedded haunch volumes, each the parent of a leg chain.
        object group = BuildCreatureCore(legSplay: 0f, configure: p => SetMember(p, "Shoulders", true)).Group;

        Assert.Equal(4, Descendants(group).Count(element => ElementName(element).StartsWith("haunch")));

        object hipSegment = First(group, "legRight11");
        Assert.StartsWith("haunch", ElementName(GetMember(hipSegment, "Parent")));
    }

    [Fact]
    public void LegZigzag_AlternatesSegmentBendDirection()
    {
        // With splay/bend/lean at zero, the only Z rotation is the zigzag; consecutive joints must angle opposite
        // ways so every segment sits at a different orientation (the natural crouch).
        object group = BuildCreatureCore(legSplay: 0f, legBend: 0f, configure: p =>
        {
            SetMember(p, "LegSegments", 3);
            SetMember(p, "LegZigzag", 30f);
        }).Group;

        double first = GetDouble(First(group, "legRight11"), "RotationZ");
        double second = GetDouble(First(group, "legRight12"), "RotationZ");
        Assert.True(first * second < 0, $"seg1={first} seg2={second} should have opposite signs");
    }

    [Fact]
    public void TailBulge_WidensTheMiddleIntoAFluffyBrush()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "TailSegments", 5);
            SetMember(p, "TailBulge", 2f);
        }).Group;

        double baseWidth = GetDouble(First(group, "tail1"), "SizeZ");
        double midWidth = GetDouble(First(group, "tail3"), "SizeZ");
        Assert.True(midWidth > baseWidth, $"mid={midWidth} base={baseWidth}");
    }

    [Fact]
    public void Mouth_AddsAJawWithFangs()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "Mouth", true);
            SetMember(p, "Fangs", 2);
        }).Group;

        Assert.Contains(Descendants(group), element => ElementName(element) == "jaw");
        // 2 fangs per side, both sides, upper + lower = 8.
        Assert.Equal(8, Descendants(group).Count(element => ElementName(element).StartsWith("fang")));
    }

    [Fact]
    public void Cheeks_Nose_AndInnerEars_AddTheirDetailBoxes()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "Cheeks", true);
            SetMember(p, "Nose", true);
            SetMember(p, "Ears", 1);
            SetMember(p, "InnerEars", true);
        }).Group;

        Assert.Equal(2, Descendants(group).Count(element => ElementName(element).StartsWith("cheek")));
        Assert.Contains(Descendants(group), element => ElementName(element) == "nose");
        Assert.Equal(2, Descendants(group).Count(element => ElementName(element).EndsWith("Inner")));
    }

    [Fact]
    public void WolfArchetype_AssemblesAllTheDetailParts()
    {
        object group = BuildCreatureCore(applyArchetype: 5 /* Wolf */).Group;
        List<string> names = Descendants(group).Select(ElementName).ToList();

        Assert.Contains(names, n => n.StartsWith("haunch"));   // embedded limb roots
        Assert.Contains(names, n => n == "jaw");               // mouth
        Assert.Contains(names, n => n.StartsWith("fang"));     // teeth
        Assert.Contains(names, n => n == "nose");
        Assert.Contains(names, n => n.StartsWith("cheek"));    // fur cheeks
        Assert.Contains(names, n => n.StartsWith("foot"));     // paws
        Assert.Contains(names, n => n.StartsWith("tail"));     // fluffy tail
        Assert.Contains(names, n => n.EndsWith("Inner"));      // inner ears
    }

    [Fact]
    public void Toes_AddClawsToEveryLimb()
    {
        // Default quadruped: 2 leg pairs => 4 limbs; 3 toes each => 12 claws (toes ride the limb tip with no feet).
        object group = BuildCreatureCore(configure: p => SetMember(p, "Toes", 3)).Group;
        Assert.Equal(12, Descendants(group).Count(element => ElementName(element).Contains("Claw")));
    }

    [Fact]
    public void Pupils_AddOneBoxPerEye()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "Eyes", true);
            SetMember(p, "Pupils", true);
        }).Group;
        Assert.Equal(2, Descendants(group).Count(element => ElementName(element).EndsWith("Pupil")));
    }

    [Fact]
    public void Crest_AddsTheRequestedPlateCount()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "Crest", true);
            SetMember(p, "CrestCount", 5);
        }).Group;
        Assert.Equal(5, Descendants(group).Count(element => ElementName(element).StartsWith("crest")));
    }

    [Fact]
    public void Belly_And_TailFin_AddTheirVolumes()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "Belly", true);
            SetMember(p, "TailFin", true);
        }).Group;
        Assert.Contains(Descendants(group), element => ElementName(element) == "belly");
        Assert.Contains(Descendants(group), element => ElementName(element) == "tailFin");
    }

    [Fact]
    public void MembraneWings_BuildBodyRootedMembraneAndFingerBones()
    {
        // One wing pair, membrane style, 3 segments, 4 fingers => segmented spars and membrane panels across
        // both wings. Separate per-finger web squares made the preview look detached.
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "WingPairs", 1);
            SetMember(p, "WingStyle", 1); // Membrane
            SetMember(p, "WingSegments", 3);
            SetMember(p, "WingFingers", 4);
        }).Group;

        Assert.Equal(8, Descendants(group).Count(element => ElementName(element).Contains("Finger")));
        Assert.Equal(6, Descendants(group).Count(element => ElementName(element).Contains("Membrane")));
        Assert.Equal(6, Descendants(group).Count(element => ElementName(element).Contains("Spar")));
        Assert.DoesNotContain(Descendants(group), element => ElementName(element).Contains("Web"));
    }

    [Fact]
    public void MembraneWing_HasABodyReachingMembrane_AndNoArmNamedSpar()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "WingPairs", 1);
            SetMember(p, "WingStyle", 1); // Membrane
        }).Group;
        List<object> wings = Descendants(group).Where(e => ElementName(e).Contains("wing")).ToList();

        // The leading-edge bone is now a "Spar", not an "Arm": the locomotion generator skips anything whose
        // name contains "arm" as a limb before it checks for wings, so an "...Arm" spar would never flap.
        Assert.Contains(wings, e => ElementName(e).Contains("Spar"));
        Assert.DoesNotContain(wings, e => ElementName(e).Contains("Arm"));

        // The first membrane panel hangs off the spar root, which itself is attached to the torso. Its local
        // Z overlap starts at the root joint, so the wing starts at the body instead of at the end of the spar.
        object rightMembrane = First(group, "wingRight1Membrane1");
        object leftMembrane = First(group, "wingLeft1Membrane1");
        object rightParent = GetMember(rightMembrane, "Parent");
        object leftParent = GetMember(leftMembrane, "Parent");
        Assert.Contains("Spar", ElementName(rightParent));
        Assert.Contains("Spar", ElementName(leftParent));
        Assert.StartsWith("spine", ElementName(GetMember(rightParent, "Parent")));
        Assert.StartsWith("spine", ElementName(GetMember(leftParent, "Parent")));
        Assert.True(GetDouble(rightMembrane, "SizeZ") > GetDouble(rightMembrane, "SizeY"), "membrane should be a wide flat sheet");
        Assert.True(((double[])GetMember(rightMembrane, "From"))[2] <= 0.0);
        Assert.True(((double[])GetMember(leftMembrane, "From"))[2] <= 0.0);
    }

    [Fact]
    public void AsymmetricArms_GiveEachSideADifferentTip()
    {
        // One blade arm (right) and one normal hand (left).
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "ArmPairs", 1);
            SetMember(p, "Hands", true);
            SetMember(p, "AsymmetricArms", true);
            SetMember(p, "ArmAppendageRight", 1); // Blade
            SetMember(p, "ArmAppendageLeft", 0);  // Normal
        }).Group;
        List<string> names = Descendants(group).Select(ElementName).ToList();

        Assert.Contains(names, n => n.StartsWith("armBladeRight"));
        Assert.DoesNotContain(names, n => n.StartsWith("armBladeLeft"));
        Assert.Contains(names, n => n.StartsWith("handLeft")); // left keeps the normal hand
        Assert.DoesNotContain(names, n => n.StartsWith("handRight")); // right is the blade, no hand
    }

    [Fact]
    public void AsymmetricArms_NoneLeavesABareStump()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "ArmPairs", 1);
            SetMember(p, "Hands", true);
            SetMember(p, "AsymmetricArms", true);
            SetMember(p, "ArmAppendageRight", 5); // None
            SetMember(p, "ArmAppendageLeft", 0);  // Normal
        }).Group;
        List<string> names = Descendants(group).Select(ElementName).ToList();

        Assert.DoesNotContain(names, n => n.StartsWith("handRight")); // bare stump, no hand
        Assert.Contains(names, n => n.StartsWith("armRight"));        // the arm itself is still there
        Assert.Contains(names, n => n.StartsWith("handLeft"));
    }

    [Fact]
    public void SymmetricByDefault_ArmsAreUnchangedWhenAsymmetryIsOff()
    {
        // With asymmetry off, both arms build the normal hand (no appendage elements).
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "ArmPairs", 1);
            SetMember(p, "Hands", true);
        }).Group;
        List<string> names = Descendants(group).Select(ElementName).ToList();

        Assert.Contains(names, n => n.StartsWith("handRight"));
        Assert.Contains(names, n => n.StartsWith("handLeft"));
        Assert.DoesNotContain(names, n => n.StartsWith("armBlade") || n.StartsWith("armClaw") || n.StartsWith("armClub") || n.StartsWith("armHook"));
    }

    [Fact]
    public void DragonArchetype_AssemblesTheTopTierDetail()
    {
        object group = BuildCreatureCore(applyArchetype: 6 /* Dragon */).Group;
        List<string> names = Descendants(group).Select(ElementName).ToList();

        Assert.Contains(names, n => n.Contains("Claw"));    // articulated toes
        Assert.Contains(names, n => n.EndsWith("Pupil"));   // layered eyes
        Assert.Contains(names, n => n.StartsWith("crest")); // head crest
        Assert.Contains(names, n => n == "belly");
        Assert.Contains(names, n => n.Contains("Membrane")); // membrane wing
        Assert.Contains(names, n => n == "tailFin");
        Assert.Contains(names, n => n.StartsWith("brow"));
    }

    [Fact]
    public void Trunk_BuildsADroopingSegmentedChain()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "Trunk", true);
            SetMember(p, "TrunkSegments", 8);
        }).Group;

        Assert.Equal(8, Descendants(group).Count(element => ElementName(element).StartsWith("trunk")));
        // The droop bends later segments about Z, so a mid segment carries a non-zero rotation.
        Assert.True(Math.Abs(GetDouble(First(group, "trunk4"), "RotationZ")) > 1.0);
    }

    [Fact]
    public void Tusks_AddASymmetricPair()
    {
        object group = BuildCreatureCore(configure: p => SetMember(p, "Tusks", true)).Group;
        Assert.Contains(Descendants(group), element => ElementName(element).StartsWith("tuskRight"));
        Assert.Contains(Descendants(group), element => ElementName(element).StartsWith("tuskLeft"));
    }

    [Fact]
    public void Hump_Dewlap_TailTuft_AddTheirVolumes()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "Hump", true);
            SetMember(p, "Dewlap", true);
            SetMember(p, "TailTuft", true);
        }).Group;

        Assert.Contains(Descendants(group), element => ElementName(element) == "hump");
        Assert.Contains(Descendants(group), element => ElementName(element) == "dewlap");
        Assert.Contains(Descendants(group), element => ElementName(element) == "tailTuft");
    }

    [Fact]
    public void MammothArchetype_AssemblesTrunkTusksAndHump()
    {
        object group = BuildCreatureCore(applyArchetype: 7 /* Mammoth */).Group;
        List<string> names = Descendants(group).Select(ElementName).ToList();

        Assert.Contains(names, n => n.StartsWith("trunk"));
        Assert.Contains(names, n => n.StartsWith("tusk"));
        Assert.Contains(names, n => n == "hump");
        Assert.Contains(names, n => n == "tailTuft");
        Assert.Contains(names, n => n.Contains("Claw")); // toenails
    }

    [Fact]
    public void BovineArchetype_AssemblesHumpDewlapAndHorns()
    {
        object group = BuildCreatureCore(applyArchetype: 8 /* Bovine */).Group;
        List<string> names = Descendants(group).Select(ElementName).ToList();

        Assert.Contains(names, n => n == "hump");
        Assert.Contains(names, n => n == "dewlap");
        Assert.Contains(names, n => n.StartsWith("horn"));
        Assert.Contains(names, n => n == "tailTuft");
        Assert.Contains(names, n => n.Contains("Claw")); // cloven hooves
    }

    [Fact]
    public void Mane_AddsOnePlatePerNeckSegment()
    {
        // Default quadruped neck is 2 segments => 2 mane plates.
        object group = BuildCreatureCore(configure: p => SetMember(p, "Mane", true)).Group;
        Assert.Equal(2, Descendants(group).Count(element => ElementName(element).StartsWith("mane")));
    }

    [Fact]
    public void Fins_AddSymmetricPectoralPairs()
    {
        // Two fin pairs => four fins (left/right per pair).
        object group = BuildCreatureCore(configure: p => SetMember(p, "FinPairs", 2)).Group;
        Assert.Equal(4, Descendants(group).Count(element => ElementName(element).StartsWith("fin")));
    }

    [Fact]
    public void Antennae_TailPlume_AndShell_BuildTheirParts()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "Antennae", true);
            SetMember(p, "TailPlume", true);
            SetMember(p, "PlumeCount", 7);
            SetMember(p, "Shell", true);
        }).Group;

        Assert.Contains(Descendants(group), element => ElementName(element).StartsWith("antennaLeft"));
        Assert.Contains(Descendants(group), element => ElementName(element).StartsWith("antennaRight"));
        Assert.Equal(7, Descendants(group).Count(element => ElementName(element).StartsWith("plume")));
        Assert.Contains(Descendants(group), element => ElementName(element) == "shell");
    }

    // ---- Studied-creature detail (Wilderlands Rustbound / Darce's Drifters / Pegasus) ----

    [Fact]
    public void Antlers_BuildABranchingBeamWithTinesOnBothSides()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "AntlerPairs", 1);
            SetMember(p, "AntlerBeamSegments", 2);
            SetMember(p, "AntlerTines", 3);
        }).Group;
        List<string> names = Descendants(group).Select(ElementName).ToList();

        Assert.Contains(names, n => n.StartsWith("antlerRight1"));
        Assert.Contains(names, n => n.StartsWith("antlerLeft1"));
        // 3 tines per beam, both sides => 6.
        Assert.Equal(6, names.Count(n => n.Contains("antler") && n.Contains("Tine")));
    }

    [Fact]
    public void Mandibles_AddALateralPairWithInnerTeeth()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "Mandibles", true);
            SetMember(p, "MandibleSegments", 2);
            SetMember(p, "MandibleFangs", true);
        }).Group;
        List<string> names = Descendants(group).Select(ElementName).ToList();

        Assert.Contains(names, n => n.StartsWith("mandibleRight"));
        Assert.Contains(names, n => n.StartsWith("mandibleLeft"));
        // 2 inner teeth per side => 4.
        Assert.Equal(4, names.Count(n => n.Contains("mandible") && n.Contains("Fang")));
    }

    [Fact]
    public void ExtraEyes_ClusterAroundEachMainEye()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "Eyes", true);
            SetMember(p, "ExtraEyes", 3);
        }).Group;
        // 3 extra per side, both sides => 6.
        Assert.Equal(6, Descendants(group).Count(element => ElementName(element).Contains("Extra")));
    }

    [Fact]
    public void TailStinger_AddsASpikeClusterAtTheTip()
    {
        // Default quadruped has a 4-segment tail, so the stinger has a tip to ride.
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "TailStinger", true);
            SetMember(p, "StingerCount", 3);
        }).Group;
        Assert.Equal(3, Descendants(group).Count(element => ElementName(element).StartsWith("stinger")));
    }

    [Fact]
    public void BodySpikes_ScatterTheRequestedCount_AndAreSeedReproducible()
    {
        object a = BuildCreatureCore(configure: p => SetMember(p, "BodySpikes", 8)).Group;
        Assert.Equal(8, Descendants(a).Count(element => ElementName(element).StartsWith("quill")));

        // A given seed reproduces the same scatter.
        object b = BuildCreatureCore(configure: p => SetMember(p, "BodySpikes", 8)).Group;
        Assert.Equal(WorldFrom(First(a, "quill1")), WorldFrom(First(b, "quill1")));
    }

    [Fact]
    public void WingFeathers_LayARowAlongEachFeatheredWing()
    {
        // One wing pair, feathered style (default), 5 feathers => 10 across both wings.
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "WingPairs", 1);
            SetMember(p, "WingFeathers", 5);
        }).Group;
        Assert.Equal(10, Descendants(group).Count(element => ElementName(element).Contains("Feather")));
    }

    [Fact]
    public void StagArchetype_AssemblesAntlersOnALeggedBody()
    {
        object group = BuildCreatureCore(applyArchetype: 9 /* Stag */).Group;
        List<string> names = Descendants(group).Select(ElementName).ToList();

        Assert.Contains(names, n => n.StartsWith("antler"));
        Assert.Contains(names, n => n.Contains("Tine"));   // a real rack, not plain horns
        Assert.Contains(names, n => n == "snout");
        Assert.Contains(names, n => n.StartsWith("foot"));
    }

    [Fact]
    public void ScorpionArchetype_AssemblesMandiblesCompoundEyesStingerAndSixLegs()
    {
        object group = BuildCreatureCore(applyArchetype: 10 /* Scorpion */).Group;
        List<string> names = Descendants(group).Select(ElementName).ToList();

        Assert.Contains(names, n => n.StartsWith("mandible"));
        Assert.Contains(names, n => n.Contains("Extra"));     // compound eyes
        Assert.Contains(names, n => n.StartsWith("stinger"));
        // Three leg pairs => the third pair's first segment exists on both sides.
        Assert.Contains("legRight31", names);
        Assert.Contains("legLeft31", names);
    }

    // ---- Mesh detail (per-bone tuning, joint fillers, smoothing, wing ridges) ----

    [Fact]
    public void PerBoneTuning_ResizesEachLimbBoneIndividually()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "ArmPairs", 1);
            SetMember(p, "ArmSegments", 3);
            SetMember(p, "ArmPerBone", true);
            SetMember(p, "ArmBoneLength", new List<float> { 2f, 1f, 1f });
            SetMember(p, "ArmBoneThick", new List<float> { 1f, 1f, 0.4f });
        }).Group;

        // Bone 1 was lengthened 2x, so it is clearly longer than bone 2 along the limb (Y).
        Assert.True(GetDouble(First(group, "armRight11"), "SizeY") > GetDouble(First(group, "armRight12"), "SizeY") * 1.5);
        // Bone 3 was thinned to 0.4x, so it is thinner than bone 1 across (X).
        Assert.True(GetDouble(First(group, "armRight13"), "SizeX") < GetDouble(First(group, "armRight11"), "SizeX") * 0.6);
    }

    [Fact]
    public void FillJoints_AddsKnucklesAtBentJoints()
    {
        // A zig-zagged multi-segment leg has several bent joints; off there are none.
        object filled = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "LegSegments", 3);
            SetMember(p, "LegZigzag", 24f);
            SetMember(p, "FillJoints", true);
        }).Group;
        Assert.True(Descendants(filled).Count(e => ElementName(e).StartsWith("joint")) > 0);

        object plain = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "LegSegments", 3);
            SetMember(p, "LegZigzag", 24f);
        }).Group;
        Assert.DoesNotContain(Descendants(plain), e => ElementName(e).StartsWith("joint"));
    }

    [Fact]
    public void Smoothing_AddsFacetBoxesToChunkyVolumes()
    {
        object round = BuildCreatureCore(configure: p => SetMember(p, "Smoothing", 1)).Group;
        // The chunky spine boxes get a rounding facet child named after them.
        Assert.Contains(Descendants(round), e => ElementName(e).StartsWith("spine") && ElementName(e).Contains("Round"));
        // Off, nothing is rounded.
        object plain = BuildCreatureCore().Group;
        Assert.DoesNotContain(Descendants(plain), e => ElementName(e).Contains("Round"));
    }

    [Fact]
    public void WingRidges_DivideTheMembraneIntoSections()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            SetMember(p, "WingPairs", 1);
            SetMember(p, "WingStyle", 1); // Membrane
            SetMember(p, "WingRidges", true);
        }).Group;
        Assert.True(Descendants(group).Count(e => ElementName(e).Contains("Ridge")) >= 2);
    }

    // ---- Targeted fine-tuning (per-element tweak layer) ----

    [Fact]
    public void Tweak_ResizesOnlyTheTargetedElement()
    {
        object baseGroup = BuildCreatureCore().Group;
        double baseHead = GetDouble(First(baseGroup, "head"), "SizeX");

        object group = BuildCreatureCore(configure: p => AddTweak(p, "head", size: 2f)).Group;
        Assert.InRange(GetDouble(First(group, "head"), "SizeX") / baseHead, 1.8, 2.2);
        // A sibling is untouched - the tweak is targeted, not global.
        Assert.Equal(GetDouble(First(baseGroup, "spine1"), "SizeX"), GetDouble(First(group, "spine1"), "SizeX"), 3);
    }

    [Fact]
    public void Tweak_HideRemovesJustThatElement()
    {
        object group = BuildCreatureCore(configure: p => AddTweak(p, "head", hidden: true)).Group;
        Assert.DoesNotContain(Descendants(group), e => ElementName(e) == "head");
        Assert.Equal(CountDescendants(BuildCreatureCore().Group) - 1, CountDescendants(group)); // head is a leaf
    }

    [Fact]
    public void Tweak_OffsetMovesTheElement()
    {
        double baseY = WorldFrom(First(BuildCreatureCore().Group, "head"))[1];
        object group = BuildCreatureCore(configure: p => AddTweak(p, "head", oy: 5f)).Group;
        Assert.Equal(baseY + 5.0, WorldFrom(First(group, "head"))[1], 2);
    }

    [Fact]
    public void Tweak_MissingOrNeutralIsANoOp()
    {
        object group = BuildCreatureCore(configure: p =>
        {
            AddTweak(p, "doesnotexist", size: 3f); // names nothing -> ignored
            AddTweak(p, "head");                   // neutral -> ignored
        }).Group;
        Assert.Equal(23, CountDescendants(group));
    }

    private static void AddTweak(object parameters, string element, float size = 1f,
        float ox = 0, float oy = 0, float oz = 0, float rx = 0, float ry = 0, float rz = 0, bool hidden = false)
    {
        Type tweakType = typeof(DebugWindowManager).GetNestedType("ModelCreatureTweak", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(DebugWindowManager), "ModelCreatureTweak");
        object tweak = Activator.CreateInstance(tweakType, nonPublic: true)!;
        SetMember(tweak, "SizeMul", size);
        SetMember(tweak, "Offset", new System.Numerics.Vector3(ox, oy, oz));
        SetMember(tweak, "Rotate", new System.Numerics.Vector3(rx, ry, rz));
        SetMember(tweak, "Hidden", hidden);
        ((IDictionary)GetMember(parameters, "Tweaks"))[element] = tweak;
    }

    private static object BuildCreature(float legSplay = 4f, float legBend = 0f, int? applyArchetype = null)
    {
        return BuildCreatureCore(legSplay, legBend, applyArchetype).Group;
    }

    private static (DebugWindowManager Manager, object Group) BuildCreatureCore(
        float legSplay = 4f, float legBend = 0f, int? applyArchetype = null, bool autoTexture = false,
        string[]? extraTextureCodes = null, Action<object>? configure = null)
    {
        DebugWindowManager manager = CreateUninitializedManager();
        object document = CreateModelDocument();
        foreach (string code in extraTextureCodes ?? [])
        {
            AddTexture(document, code);
        }
        SetField(manager, "_modelDoc", document);

        object parameters = CreateCreatureParams();
        SetMember(parameters, "LegSplay", legSplay);
        SetMember(parameters, "LegBend", legBend);
        SetMember(parameters, "AutoTexture", autoTexture);
        SetField(manager, "_modelCreatureParams", parameters);
        SetField(manager, "_modelCreatureArchetypeIndex", applyArchetype ?? 0);

        if (applyArchetype is int archetype)
        {
            MethodInfo apply = typeof(DebugWindowManager).GetMethod("ModelApplyCreatureArchetype", InstanceFlags)
                ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelApplyCreatureArchetype");
            Type archetypeType = typeof(DebugWindowManager).GetNestedType("ModelCreatureArchetype", BindingFlags.NonPublic)!;
            apply.Invoke(manager, [Enum.ToObject(archetypeType, archetype)]);
        }

        // Applied last so a test's overrides win over both defaults and any archetype preset.
        configure?.Invoke(parameters);

        MethodInfo build = typeof(DebugWindowManager).GetMethod("ModelBuildCreature", InstanceFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelBuildCreature");
        object?[] args = [null];
        object group = build.Invoke(manager, args)
            ?? throw new InvalidOperationException("Creature generation returned null: " + args[0]);
        Assert.True(string.IsNullOrEmpty(args[0]?.ToString()), args[0]?.ToString());
        return (manager, group);
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

    // World-space center of the box at rest (no chain rotations): summed Froms plus half the box size.
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

    private static object First(object group, string name)
    {
        foreach (object element in Descendants(group))
        {
            if (ElementName(element) == name) return element;
        }
        throw new InvalidOperationException($"No element named {name}");
    }

    private static string FaceTexture(object element)
    {
        object face = ((Array)GetMember(element, "Faces")).GetValue(0)
            ?? throw new InvalidOperationException($"{ElementName(element)} has no first face");
        return (string)GetMember(face, "Texture");
    }

    private static IEnumerable<object> Faces(object element)
    {
        foreach (object? face in (Array)GetMember(element, "Faces"))
        {
            if (face != null) yield return face;
        }
    }

    private static HashSet<string> DistinctFaceCodes(object group)
    {
        HashSet<string> codes = new(StringComparer.Ordinal);
        foreach (object element in Descendants(group))
        {
            codes.Add(FaceTexture(element));
        }
        return codes;
    }

    private static object FindDeepest(object group, string namePrefix)
    {
        object? best = null;
        int bestDepth = -1;
        foreach (object element in Descendants(group))
        {
            if (!ElementName(element).StartsWith(namePrefix)) continue;
            int depth = Depth(element);
            if (depth > bestDepth)
            {
                bestDepth = depth;
                best = element;
            }
        }
        return best ?? throw new InvalidOperationException($"No element with prefix {namePrefix}");
    }

    private static int Depth(object element)
    {
        int depth = 0;
        for (object? current = GetMemberOrNull(element, "Parent"); current != null; current = GetMemberOrNull(current, "Parent"))
        {
            depth++;
        }
        return depth;
    }

    private static IEnumerable<object> Descendants(object group)
    {
        foreach (object child in (IEnumerable)GetMember(group, "Children"))
        {
            yield return child;
            foreach (object descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static int CountDescendants(object group)
    {
        return Descendants(group).Count();
    }

    private static string ElementName(object element)
    {
        return (string)GetMember(element, "Name");
    }

    private static object CreateCreatureParams()
    {
        Type type = typeof(DebugWindowManager).GetNestedType("ModelCreatureParams", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(DebugWindowManager), "ModelCreatureParams");
        return Activator.CreateInstance(type, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create creature params.");
    }

    private static void AddTexture(object document, string code)
    {
        Type textureType = typeof(DebugWindowManager).GetNestedType("ModelTextureEntry", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(DebugWindowManager), "ModelTextureEntry");
        object texture = Activator.CreateInstance(textureType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create texture entry.");
        SetMember(texture, "Code", code);
        ((IList)GetMember(document, "Textures")).Add(texture);
    }

    private static void SetTexturePath(object document, string code, string path)
    {
        foreach (object texture in (IEnumerable)GetMember(document, "Textures"))
        {
            if ((string)GetMember(texture, "Code") == code)
            {
                SetMember(texture, "Path", path);
                return;
            }
        }
        throw new InvalidOperationException($"No texture with code {code}");
    }

    private static Dictionary<string, string> TextureMap(object document)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (object texture in (IEnumerable)GetMember(document, "Textures"))
        {
            map[(string)GetMember(texture, "Code")] = (string)GetMember(texture, "Path");
        }
        return map;
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
        IList textures = (IList)GetMember(document, "Textures");
        textures.Add(texture);
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

    private static double GetDouble(object target, string name)
    {
        return Convert.ToDouble(GetMember(target, name));
    }
}
