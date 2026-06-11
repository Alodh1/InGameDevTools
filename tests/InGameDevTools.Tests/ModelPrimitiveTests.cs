using InGameDevTools.Animations;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;

namespace InGameDevTools.Tests;

public sealed class ModelPrimitiveTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(0, 1.0, 2.0, 3.0, 2.0, 1.0, 3.0)]
    [InlineData(1, 1.0, 2.0, 3.0, 1.0, 2.0, 3.0)]
    [InlineData(2, 1.0, 2.0, 3.0, 1.0, 3.0, 2.0)]
    public void RotationMapping_MapsLocalPrimitiveAxesToWorldAxes(int primitiveAxis, double localU, double localV, double localW, double expectedX, double expectedY, double expectedZ)
    {
        DebugWindowManager.ModelPrimitiveRotationDebug rotation = DebugWindowManager.TestMapModelPrimitiveRotation(primitiveAxis, localU, localV, localW);

        Assert.Equal(expectedX, rotation.RotationX);
        Assert.Equal(expectedY, rotation.RotationY);
        Assert.Equal(expectedZ, rotation.RotationZ);
    }

    [Theory]
    [InlineData("Sphere / Dome")]
    [InlineData("Cone")]
    [InlineData("Capsule")]
    public void SmoothProfiledPrimitives_EmitElementsWithMultipleRotationAxes(string label)
    {
        List<PrimitiveElement> elements = BuildPrimitive(label, stepped: false);

        Assert.True(NonZeroRotationAxesAcross(elements) >= 2);
    }

    [Theory]
    [InlineData("Triangle (flat)")]
    [InlineData("Disc sector (flat)")]
    public void SmoothFlatPrimitives_UseRotatedPanelsByDefault(string label)
    {
        List<PrimitiveElement> elements = BuildPrimitive(label, stepped: false, sweep: 180f);

        Assert.Contains(elements, element => NonZeroRotationAxes(element) >= 1);
    }

    [Theory]
    [InlineData("Triangle (flat)")]
    [InlineData("Disc sector (flat)")]
    public void SteppedFlatPrimitives_KeepAxisAlignedFallback(string label)
    {
        List<PrimitiveElement> elements = BuildPrimitive(label, stepped: true, sweep: 180f);

        Assert.All(elements, element => Assert.Equal(0, NonZeroRotationAxes(element)));
    }

    [Fact]
    public void GeneratedPrimitiveParent_HasCenteredRotationOrigin()
    {
        object parent = BuildPrimitiveParent("Sphere / Dome", stepped: false);

        double[] origin = (double[])GetMember(parent, "RotationOrigin");
        double[] from = (double[])GetMember(parent, "From");

        Assert.Equal(from, origin);
    }

    [Fact]
    public void GeneratedPrimitiveParent_AppliesHelperPreviewRotation()
    {
        object parent = BuildPrimitiveParent("Sphere / Dome", stepped: false, rotation: new System.Numerics.Vector3(15f, -30f, 45f));

        Assert.Equal(15, GetDouble(parent, "RotationX"));
        Assert.Equal(-30, GetDouble(parent, "RotationY"));
        Assert.Equal(45, GetDouble(parent, "RotationZ"));
    }

    private static int NonZeroRotationAxes(PrimitiveElement element)
    {
        int axes = 0;
        if (Math.Abs(element.RotationX) > 0.0001) axes++;
        if (Math.Abs(element.RotationY) > 0.0001) axes++;
        if (Math.Abs(element.RotationZ) > 0.0001) axes++;
        return axes;
    }

    private static int NonZeroRotationAxesAcross(IEnumerable<PrimitiveElement> elements)
    {
        bool rotationX = false;
        bool rotationY = false;
        bool rotationZ = false;
        foreach (PrimitiveElement element in elements)
        {
            rotationX |= Math.Abs(element.RotationX) > 0.0001;
            rotationY |= Math.Abs(element.RotationY) > 0.0001;
            rotationZ |= Math.Abs(element.RotationZ) > 0.0001;
        }

        int axes = 0;
        if (rotationX) axes++;
        if (rotationY) axes++;
        if (rotationZ) axes++;
        return axes;
    }

    private static List<PrimitiveElement> BuildPrimitive(string label, bool stepped, int axis = 1, float sweep = 360f)
    {
        object parent = BuildPrimitiveParent(label, stepped, axis, sweep);
        object children = GetMember(parent, "Children");
        List<PrimitiveElement> elements = [];
        foreach (object child in (IEnumerable)children)
        {
            CollectPrimitiveElements(child, elements);
        }

        Assert.NotEmpty(elements);
        return elements;
    }

    private static void CollectPrimitiveElements(object element, List<PrimitiveElement> elements)
    {
        elements.Add(new PrimitiveElement(
            GetDouble(element, "RotationX"),
            GetDouble(element, "RotationY"),
            GetDouble(element, "RotationZ")));

        foreach (object child in (IEnumerable)GetMember(element, "Children"))
        {
            CollectPrimitiveElements(child, elements);
        }
    }

    private static object BuildPrimitiveParent(string label, bool stepped, int axis = 1, float sweep = 360f, System.Numerics.Vector3? rotation = null)
    {
#pragma warning disable SYSLIB0050
        DebugWindowManager manager = (DebugWindowManager)FormatterServices.GetUninitializedObject(typeof(DebugWindowManager));
#pragma warning restore SYSLIB0050

        SetField(manager, "_modelDoc", CreateModelDocument());
        SetField(manager, "_modelPrimitiveKindIndex", PrimitiveKindIndex(label));
        SetField(manager, "_modelPrimitiveStepped", stepped);
        SetField(manager, "_modelPrimitiveAxis", axis);
        SetField(manager, "_modelPrimitiveCenter", new System.Numerics.Vector3(8f, 8f, 8f));
        SetField(manager, "_modelPrimitiveRotation", rotation ?? System.Numerics.Vector3.Zero);
        SetField(manager, "_modelPrimitiveTexture", "");
        SetField(manager, "_modelPrimitiveDiameter", 16f);
        SetField(manager, "_modelPrimitiveHeight", 18f);
        SetField(manager, "_modelPrimitiveTopDiameter", 4f);
        SetField(manager, "_modelPrimitiveSides", 6);
        SetField(manager, "_modelPrimitiveLayers", 8);
        SetField(manager, "_modelPrimitiveHollow", false);
        SetField(manager, "_modelPrimitiveWall", 2f);
        SetField(manager, "_modelPrimitiveMinor", 4f);
        SetField(manager, "_modelPrimitiveSegments", 16);
        SetField(manager, "_modelPrimitiveSweep", sweep);
        SetField(manager, "_modelPrimitiveStep", 1f);
        SetField(manager, "_modelPrimitiveDome", 0);
        SetField(manager, "_modelPrimitiveDepth", 16f);
        SetField(manager, "_modelPrimitiveRise", 10f);
        SetField(manager, "_modelPrimitiveTopScale", 0f);
        SetField(manager, "_modelPrimitiveTurns", 1.5f);
        SetField(manager, "_modelPrimitiveThickness", 1f);
        SetField(manager, "_modelPrimitiveStarSquares", 2);

        MethodInfo build = typeof(DebugWindowManager).GetMethod("ModelBuildPrimitive", InstanceFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelBuildPrimitive");
        object?[] args = [null];
        object parent = build.Invoke(manager, args)
            ?? throw new InvalidOperationException("Primitive generation returned null: " + args[0]);
        string error = args[0]?.ToString() ?? "";
        Assert.True(string.IsNullOrEmpty(error), error);
        return parent;
    }

    private static int PrimitiveKindIndex(string label)
    {
        string[] labels = (string[])GetStaticMember(typeof(DebugWindowManager), "ModelPrimitiveKindLabels");
        int index = Array.IndexOf(labels, label);
        Assert.True(index >= 0, $"Primitive label not found: {label}");
        return index;
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

    private static object GetStaticMember(Type type, string name)
    {
        FieldInfo? field = type.GetField(name, StaticFlags);
        if (field != null) return field.GetValue(null)!;
        PropertyInfo? property = type.GetProperty(name, StaticFlags);
        if (property != null) return property.GetValue(null)!;
        throw new MissingMemberException(type.FullName, name);
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

    private sealed record PrimitiveElement(double RotationX, double RotationY, double RotationZ);
}
