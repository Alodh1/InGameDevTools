using InGameDevTools.Animations;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;

namespace InGameDevTools.Tests;

public sealed class ModelWeaponTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void Defaults_BuildAGripGuardPommelAndBlade()
    {
        object group = BuildWeapon();
        List<string> names = Descendants(group).Select(ElementName).ToList();

        Assert.Contains(names, n => n == "grip");
        Assert.Contains(names, n => n == "guard");
        Assert.Contains(names, n => n.StartsWith("pommel"));
        Assert.Contains(names, n => n.StartsWith("blade"));
        Assert.Contains(names, n => n.StartsWith("bladeTip"));

        // Every generated box is a positive-size, textured element; the group is a face-less parent.
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
    public void Katana_HasACordWrapDiscGuardAndCurvedBlade()
    {
        object group = BuildWeapon(applyArchetype: (int)6 /* Katana */);
        List<string> names = Descendants(group).Select(ElementName).ToList();

        Assert.Contains(names, n => n.StartsWith("wrap"));     // cord wrap rings
        Assert.Contains(names, n => n.StartsWith("guardDisc")); // tsuba facets
        // The curve bends each later blade segment about Z, so a mid segment carries a non-zero rotation.
        Assert.True(Math.Abs(GetDouble(First(group, "blade4"), "RotationZ")) > 1.0);
    }

    [Fact]
    public void Pickaxe_HasTwoOpposingSpikes()
    {
        object group = BuildWeapon(applyArchetype: (int)33 /* Pickaxe */);
        List<string> names = Descendants(group).Select(ElementName).ToList();
        Assert.Contains(names, n => n == "pickSpike");
        Assert.Contains(names, n => n == "pickSpikeBack");
    }

    [Fact]
    public void Morningstar_ScattersFlangesInRowsWithATopSpike()
    {
        object group = BuildWeapon(applyArchetype: (int)29 /* Morningstar */);
        // 8 flanges over 2 rows = 16, plus the crowning spike.
        Assert.Equal(16, Descendants(group).Count(e => ElementName(e).StartsWith("flange")));
        Assert.Contains(Descendants(group), e => ElementName(e).StartsWith("maceTopSpike"));
    }

    [Fact]
    public void Trident_HasThreeProngs()
    {
        object group = BuildWeapon(applyArchetype: (int)15 /* Trident */);
        List<string> names = Descendants(group).Select(ElementName).ToList();
        Assert.Contains("prong1", names);
        Assert.Contains("prong2", names);
        Assert.Contains("prong3", names);
    }

    [Fact]
    public void Halberd_AddsASideAxeAndABackHook()
    {
        object group = BuildWeapon(applyArchetype: (int)18 /* Halberd */);
        List<string> names = Descendants(group).Select(ElementName).ToList();
        Assert.Contains(names, n => n.StartsWith("spearAxe"));
        Assert.Contains(names, n => n.StartsWith("spearHook"));
        Assert.Contains(names, n => n.StartsWith("socket"));
    }

    [Fact]
    public void Naginata_BuildsACurvedPolearmBlade()
    {
        object group = BuildWeapon(applyArchetype: (int)16 /* Naginata */);
        Assert.Contains(Descendants(group), e => ElementName(e).StartsWith("polearmBlade"));
    }

    [Fact]
    public void Axe_BuildsABitWithAnEdge()
    {
        object group = BuildWeapon(applyArchetype: (int)20 /* Axe */);
        List<string> names = Descendants(group).Select(ElementName).ToList();
        Assert.Contains(names, n => n == "axeBit");
        Assert.Contains(names, n => n == "axeEdge");
    }

    [Fact]
    public void DoubleAxe_MirrorsTheBitOnTheBack()
    {
        object group = BuildWeapon(applyArchetype: (int)24 /* DoubleAxe */);
        List<string> names = Descendants(group).Select(ElementName).ToList();
        Assert.Contains(names, n => n == "axeBit");
        Assert.Contains(names, n => n == "axeBitBack");
    }

    [Fact]
    public void Warhammer_HasAHeadAndASpikeBack()
    {
        object group = BuildWeapon(applyArchetype: (int)26 /* Warhammer */);
        List<string> names = Descendants(group).Select(ElementName).ToList();
        Assert.Contains(names, n => n == "hammerHead");
        Assert.Contains(names, n => n.StartsWith("hammerBack"));
    }

    [Fact]
    public void Bow_BuildsTwoLimbsAndAString()
    {
        object group = BuildWeapon(applyArchetype: (int)42 /* Shortbow */);
        List<string> names = Descendants(group).Select(ElementName).ToList();
        Assert.Contains(names, n => n.StartsWith("bowLimbUpper"));
        Assert.Contains(names, n => n.StartsWith("bowLimbLower"));
        Assert.Contains("bowString1", names);
        Assert.Contains("bowString2", names);
    }

    [Fact]
    public void Scythe_BuildsASegmentedBladeOffATang()
    {
        object group = BuildWeapon(applyArchetype: (int)38 /* Scythe */);
        List<string> names = Descendants(group).Select(ElementName).ToList();
        Assert.Contains(names, n => n == "scytheTang");
        Assert.Contains(names, n => n.StartsWith("scytheBlade"));
    }

    [Fact]
    public void Shovel_BuildsAScoopWithAFootStep()
    {
        object group = BuildWeapon(applyArchetype: (int)36 /* Shovel */);
        List<string> names = Descendants(group).Select(ElementName).ToList();
        Assert.Contains(names, n => n == "spadeScoop");
        Assert.Contains(names, n => n.StartsWith("spadeStep"));
    }

    [Fact]
    public void AutoTextures_PutTheGripOnHandleAndTheBladeOnMetal()
    {
        object group = BuildWeaponCore(applyArchetype: 0, autoTexture: true).Group;
        Assert.Equal("handle", FaceTexture(First(group, "grip")));
        Assert.Equal("metal", FaceTexture(First(group, "blade1")));
    }

    [Fact]
    public void WithoutAutoTextures_EveryPartSharesTheMetalCode()
    {
        object group = BuildWeaponCore(applyArchetype: 0, autoTexture: false, metalTexture: "all").Group;
        Assert.Equal("all", FaceTexture(First(group, "grip")));
        Assert.Equal("all", FaceTexture(First(group, "blade1")));
    }

    [Fact]
    public void UniformScale_ScalesTheWholeWeapon()
    {
        object baseGroup = BuildWeaponCore(applyArchetype: 0).Group;
        object bigGroup = BuildWeaponCore(applyArchetype: 0, configure: p => SetMember(p, "UniformScale", 2f)).Group;

        double baseLen = GetDouble(First(baseGroup, "blade1"), "SizeX");
        double bigLen = GetDouble(First(bigGroup, "blade1"), "SizeX");
        Assert.InRange(bigLen / baseLen, 1.8, 2.2);
        Assert.Equal(CountDescendants(baseGroup), CountDescendants(bigGroup));
    }

    [Fact]
    public void EveryArchetype_BuildsWithinTheElementBudget()
    {
        int max = (int)GetStaticField("WeaponMaxElements");
        int count = WeaponArchetypeCount();
        for (int i = 0; i < count; i++)
        {
            object group = BuildWeapon(applyArchetype: i);
            int elements = CountDescendants(group);
            Assert.True(elements > 0, $"archetype {i} -> {elements} elements");
            Assert.True(elements <= max, $"archetype {i} -> {elements} > {max}");
        }
    }

    [Fact]
    public void BladeCrossSection_AddsAThickSpineRidgeAndBevelFacets()
    {
        // A bevelled blade gives each segment a thick spine ridge plus angled edge facets; the spine box is
        // thicker than the thin core the chain lays down.
        object group = BuildWeaponCore(applyArchetype: 0, configure: p =>
        {
            SetMember(p, "BladeSection", 1 /* SingleBevel */);
            SetMember(p, "BladeBevels", true);
            SetMember(p, "BladeSegments", 4);
        }).Group;

        Assert.Contains(Descendants(group), e => ElementName(e).StartsWith("bladeSpine"));
        Assert.Contains(Descendants(group), e => ElementName(e).StartsWith("bladeBevel"));
        Assert.True(GetDouble(First(group, "bladeSpine1"), "SizeZ") > GetDouble(First(group, "blade1"), "SizeZ"),
            "spine ridge must be thicker than the thin blade core");
    }

    [Fact]
    public void FlatCrossSection_LeavesASingleSlabPerSegment()
    {
        object group = BuildWeaponCore(applyArchetype: 0, configure: p => SetMember(p, "BladeSection", 0 /* Flat */)).Group;
        Assert.DoesNotContain(Descendants(group), e => ElementName(e).StartsWith("bladeSpine"));
        Assert.DoesNotContain(Descendants(group), e => ElementName(e).StartsWith("bladeBevel"));
    }

    [Fact]
    public void DoubleBevel_SharpensBothEdgesWithSwedgeFacets()
    {
        object group = BuildWeaponCore(applyArchetype: 0, configure: p =>
        {
            SetMember(p, "BladeSection", 2 /* DoubleBevel */);
            SetMember(p, "BladeBevels", true);
        }).Group;
        Assert.Contains(Descendants(group), e => ElementName(e).StartsWith("bladeBevel"));
        Assert.Contains(Descendants(group), e => ElementName(e).StartsWith("bladeSwedge"));
    }

    [Fact]
    public void DistalThickness_TapersTheSpineTowardTheTip()
    {
        object group = BuildWeaponCore(applyArchetype: 0, configure: p =>
        {
            SetMember(p, "BladeSegments", 4);
            SetMember(p, "BladeThickness", 0.8f);
            SetMember(p, "BladeTipThickness", 0.2f);
        }).Group;
        Assert.True(GetDouble(First(group, "bladeSpine1"), "SizeZ") > GetDouble(First(group, "bladeSpine4"), "SizeZ"),
            "the spine must get thinner toward the tip");
    }

    [Fact]
    public void GripSection_RibbedAddsFlutes_OctagonChamfers()
    {
        object ribbed = BuildWeaponCore(applyArchetype: 0, configure: p => SetMember(p, "GripSection", 4 /* Ribbed */)).Group;
        Assert.Equal(4, Descendants(ribbed).Count(e => ElementName(e).StartsWith("gripRib")));

        object octagon = BuildWeaponCore(applyArchetype: 0, configure: p => SetMember(p, "GripSection", 2 /* Octagon */)).Group;
        Assert.Contains(Descendants(octagon), e => ElementName(e) == "gripChamfer");
    }

    [Fact]
    public void ItemJson_CarriesToolAttackAndShapeFields()
    {
        (DebugWindowManager manager, _) = BuildWeaponCore(applyArchetype: 0);
        MethodInfo build = typeof(DebugWindowManager).GetMethod("ModelWeaponBuildItemJson", InstanceFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelWeaponBuildItemJson");
        JObject item = (JObject)build.Invoke(manager, [])!;

        Assert.Equal("sword", (string?)item["tool"]);
        Assert.NotNull(item["attackpower"]);
        Assert.NotNull(item["durability"]);
        Assert.NotNull(item["shape"]?["base"]);
    }

    // ---- Harness -----------------------------------------------------------

    private static object BuildWeapon(int? applyArchetype = null) => BuildWeaponCore(applyArchetype).Group;

    private static (DebugWindowManager Manager, object Group) BuildWeaponCore(
        int? applyArchetype = null, bool autoTexture = false, string? metalTexture = null, Action<object>? configure = null)
    {
        DebugWindowManager manager = CreateUninitializedManager();
        object document = CreateModelDocument();
        SetField(manager, "_modelDoc", document);

        object parameters = CreateWeaponParams();
        SetMember(parameters, "AutoTexture", autoTexture);
        if (metalTexture != null) SetMember(parameters, "Texture", metalTexture);
        SetField(manager, "_weaponParams", parameters);
        SetField(manager, "_weaponArchetypeIndex", applyArchetype ?? 0);

        if (applyArchetype is int archetype)
        {
            MethodInfo apply = typeof(DebugWindowManager).GetMethod("ModelApplyWeaponArchetype", InstanceFlags)
                ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelApplyWeaponArchetype");
            Type archetypeType = typeof(DebugWindowManager).GetNestedType("WeaponArchetype", BindingFlags.NonPublic)!;
            apply.Invoke(manager, [Enum.ToObject(archetypeType, archetype)]);
        }

        // Auto-texture is reset by archetype application path? No - archetype keeps it; re-apply the test's choice.
        SetMember(parameters, "AutoTexture", autoTexture);
        if (metalTexture != null) SetMember(parameters, "Texture", metalTexture);
        configure?.Invoke(parameters);

        MethodInfo buildMethod = typeof(DebugWindowManager).GetMethod("ModelBuildWeapon", InstanceFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelBuildWeapon");
        object?[] args = [null];
        object group = buildMethod.Invoke(manager, args)
            ?? throw new InvalidOperationException("Weapon generation returned null: " + args[0]);
        Assert.True(string.IsNullOrEmpty(args[0]?.ToString()), args[0]?.ToString());
        return (manager, group);
    }

    private static int WeaponArchetypeCount()
    {
        Type type = typeof(DebugWindowManager).GetNestedType("WeaponArchetype", BindingFlags.NonPublic)!;
        return Enum.GetValues(type).Length;
    }

    private static object GetStaticField(string name)
    {
        FieldInfo field = typeof(DebugWindowManager).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(DebugWindowManager), name);
        return field.GetValue(null)!;
    }

    private static object CreateWeaponParams()
    {
        Type type = typeof(DebugWindowManager).GetNestedType("WeaponParams", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(DebugWindowManager), "WeaponParams");
        return Activator.CreateInstance(type, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create weapon params.");
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

    private static int CountDescendants(object group) => Descendants(group).Count();

    private static string ElementName(object element) => (string)GetMember(element, "Name");

    private static double GetDouble(object target, string name) => Convert.ToDouble(GetMember(target, name));

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
}
