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
    public void SmoothFlatPrimitives_UseRotatedPanelsWhenSelected(string label)
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

    [Theory]
    [InlineData("Cylinder / Prism")]
    [InlineData("Cone")]
    [InlineData("Sphere / Dome")]
    [InlineData("Torus / Arch")]
    [InlineData("Capsule")]
    [InlineData("Disc sector (flat)")]
    [InlineData("Triangle (flat)")]
    [InlineData("Star (flat)")]
    [InlineData("Cross / Plus (flat)")]
    [InlineData("Arrow (flat)")]
    [InlineData("Heart (flat)")]
    public void ExactPrimitives_HaveNoInvertedCuboidsOrAxisAlignedOverlaps(string label)
    {
        List<PrimitiveBox> boxes = BuildPrimitiveBoxes(label, stepped: true, sweep: 180f, step: 2f);

        Assert.All(boxes, box =>
        {
            Assert.True(box.SizeX > 0, box.ToString());
            Assert.True(box.SizeY > 0, box.ToString());
            Assert.True(box.SizeZ > 0, box.ToString());
        });

        for (int left = 0; left < boxes.Count; left++)
        {
            for (int right = left + 1; right < boxes.Count; right++)
            {
                Assert.False(BoxesOverlap(boxes[left], boxes[right]), $"{boxes[left]} overlaps {boxes[right]}");
            }
        }
    }

    [Fact]
    public void ExactCylinder_CoversEveryInteriorGridCellWithoutTinyGaps()
    {
        float step = 2f;
        List<PrimitiveBox> boxes = BuildPrimitiveBoxes("Cylinder / Prism", stepped: true, step: step);
        double radius = 8.0;
        double extent = Math.Ceiling(radius * 2.0 / step) * step * 0.5;

        for (double u = -extent + step * 0.5; u < extent; u += step)
        {
            for (double w = -extent + step * 0.5; w < extent; w += step)
            {
                if (u * u + w * w > radius * radius) continue;
                Assert.Contains(boxes, box => box.Contains(u, 0, w));
            }
        }
    }

    [Fact]
    public void ExactCylinder_UsesMergedCuboidsInsteadOfRawCells()
    {
        float step = 2f;
        List<PrimitiveBox> boxes = BuildPrimitiveBoxes("Cylinder / Prism", stepped: true, step: step);
        double radius = 8.0;
        double extent = Math.Ceiling(radius * 2.0 / step) * step * 0.5;
        int occupiedCells = 0;
        for (double u = -extent + step * 0.5; u < extent; u += step)
        {
            for (double w = -extent + step * 0.5; w < extent; w += step)
            {
                if (u * u + w * w <= radius * radius) occupiedCells += (int)Math.Ceiling(18.0 / step);
            }
        }

        Assert.True(boxes.Count < occupiedCells / 2, $"Expected merged output; got {boxes.Count} cuboids for {occupiedCells} raw cells.");
    }

    [Fact]
    public void InternalFaceCleanup_CullsExactlySharedFaces()
    {
        DebugWindowManager manager = CreateUninitializedManager();
        object parent = CreateElement("parent", [0, 0, 0], [0, 0, 0], faces: false);
        object left = CreateElement("left", [0, 0, 0], [1, 1, 1], faces: true);
        object right = CreateElement("right", [1, 0, 0], [2, 1, 1], faces: true);
        AddChild(parent, left);
        AddChild(parent, right);

        MethodInfo analyze = typeof(DebugWindowManager).GetMethod("ModelAnalyzePrimitive", InstanceFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelAnalyzePrimitive");
        object metrics = analyze.Invoke(manager, [parent, true, true, "Exact merged"])!;

        Assert.Equal(2, GetInt(metrics, "CulledInternalFaces"));
        Assert.Equal(10, GetInt(metrics, "EnabledFaces"));
        Assert.Empty((IList)GetMember(metrics, "Errors"));
    }

    [Fact]
    public void ExactAnalyzer_RejectsEnabledCoincidentFacesWhenNotCulled()
    {
        DebugWindowManager manager = CreateUninitializedManager();
        object parent = CreateElement("parent", [0, 0, 0], [0, 0, 0], faces: false);
        object left = CreateElement("left", [0, 0, 0], [1, 1, 1], faces: true);
        object right = CreateElement("right", [1, 0, 0], [2, 1, 1], faces: true);
        AddChild(parent, left);
        AddChild(parent, right);

        MethodInfo analyze = typeof(DebugWindowManager).GetMethod("ModelAnalyzePrimitive", InstanceFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelAnalyzePrimitive");
        object metrics = analyze.Invoke(manager, [parent, true, false, "Exact merged"])!;

        Assert.NotEmpty((IList)GetMember(metrics, "Errors"));
        Assert.Equal("blocked", (string)GetMember(metrics, "QualityLabel"));
    }

    [Fact]
    public void SmoothLegacyMode_RemainsAvailableButWarnsAboutOverlapRisk()
    {
        (object _, object manager) = BuildPrimitiveContext("Sphere / Dome", stepped: false);
        object metrics = GetMember(manager, "_modelPrimitivePreviewMetrics");

        Assert.NotEmpty((IList)GetMember(metrics, "Warnings"));
    }

    [Fact]
    public void ElementCut_BuildsGaplessPiecesAndPreservesTransform()
    {
        object source = CreateElement("Box", [0, 0, 0], [9, 6, 3], faces: true);
        SetMember(source, "RotationOrigin", new double[] { 4.5, 3, 1.5 });
        SetMember(source, "RotationX", 15.0);
        SetMember(source, "RotationY", -25.0);
        SetMember(source, "RotationZ", 35.0);

        MethodInfo buildCutPieces = typeof(DebugWindowManager).GetMethod("ModelBuildCutPieces", StaticFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelBuildCutPieces");
        IList pieces = (IList)buildCutPieces.Invoke(null, [source, 3, 2, 1, (Func<string, string>)(name => name)])!;

        Assert.Equal(6, pieces.Count);
        List<PrimitiveBox> boxes = pieces.Cast<object>()
            .Select(piece => new PrimitiveBox((double[])GetMember(piece, "From"), (double[])GetMember(piece, "To")))
            .ToList();
        Assert.All(boxes, box =>
        {
            Assert.True(box.SizeX > 0, box.ToString());
            Assert.True(box.SizeY > 0, box.ToString());
            Assert.True(box.SizeZ > 0, box.ToString());
        });

        foreach (object piece in pieces)
        {
            Assert.Equal(15.0, GetDouble(piece, "RotationX"));
            Assert.Equal(-25.0, GetDouble(piece, "RotationY"));
            Assert.Equal(35.0, GetDouble(piece, "RotationZ"));
            Assert.Equal(new double[] { 4.5, 3, 1.5 }, (double[])GetMember(piece, "RotationOrigin"));
            Assert.Empty((IList)GetMember(piece, "Children"));
            Assert.NotNull(((Array)GetMember(piece, "Faces")).GetValue(0));
        }

        for (int left = 0; left < boxes.Count; left++)
        {
            for (int right = left + 1; right < boxes.Count; right++)
            {
                Assert.False(BoxesOverlap(boxes[left], boxes[right]), $"{boxes[left]} overlaps {boxes[right]}");
            }
        }

        foreach (double x in new[] { 1.5, 4.5, 7.5 })
        {
            foreach (double y in new[] { 1.5, 4.5 })
            {
                Assert.Equal(1, boxes.Count(box => box.Contains(x, y, 1.5)));
            }
        }
    }

    [Fact]
    public void ElementCutAtCoordinate_SplitsExactlyOnRequestedLine()
    {
        object source = CreateElement("Box", [0, 0, 0], [9, 6, 3], faces: true);
        SetMember(source, "RotationX", 12.0);

        MethodInfo buildCutPieces = typeof(DebugWindowManager).GetMethod("ModelBuildCutPiecesAtCoordinate", StaticFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "ModelBuildCutPiecesAtCoordinate");
        IList pieces = (IList)buildCutPieces.Invoke(null, [source, 0, 4.25, (Func<string, string>)(name => name)])!;

        Assert.Equal(2, pieces.Count);
        object left = pieces[0]!;
        object right = pieces[1]!;
        Assert.Equal(new double[] { 0, 0, 0 }, (double[])GetMember(left, "From"));
        Assert.Equal(new double[] { 4.25, 6, 3 }, (double[])GetMember(left, "To"));
        Assert.Equal(new double[] { 4.25, 0, 0 }, (double[])GetMember(right, "From"));
        Assert.Equal(new double[] { 9, 6, 3 }, (double[])GetMember(right, "To"));
        Assert.Equal(12.0, GetDouble(left, "RotationX"));
        Assert.Equal(12.0, GetDouble(right, "RotationX"));
        Assert.False(BoxesOverlap(new PrimitiveBox((double[])GetMember(left, "From"), (double[])GetMember(left, "To")),
            new PrimitiveBox((double[])GetMember(right, "From"), (double[])GetMember(right, "To"))));
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

    private static List<PrimitiveBox> BuildPrimitiveBoxes(string label, bool stepped, int axis = 1, float sweep = 360f, float step = 1f)
    {
        object parent = BuildPrimitiveParent(label, stepped, axis, sweep, step: step);
        object children = GetMember(parent, "Children");
        List<PrimitiveBox> boxes = [];
        foreach (object child in (IEnumerable)children)
        {
            CollectPrimitiveBoxes(child, boxes);
        }

        Assert.NotEmpty(boxes);
        return boxes;
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

    private static void CollectPrimitiveBoxes(object element, List<PrimitiveBox> boxes)
    {
        object children = GetMember(element, "Children");
        bool hasChildren = false;
        foreach (object child in (IEnumerable)children)
        {
            hasChildren = true;
            CollectPrimitiveBoxes(child, boxes);
        }

        if (hasChildren) return;

        double[] from = (double[])GetMember(element, "From");
        double[] to = (double[])GetMember(element, "To");
        boxes.Add(new PrimitiveBox(from, to));
    }

    private static object BuildPrimitiveParent(string label, bool stepped, int axis = 1, float sweep = 360f, System.Numerics.Vector3? rotation = null, float step = 1f)
    {
        return BuildPrimitiveContext(label, stepped, axis, sweep, rotation, step).Parent;
    }

    private static (object Parent, object Manager) BuildPrimitiveContext(string label, bool stepped, int axis = 1, float sweep = 360f, System.Numerics.Vector3? rotation = null, float step = 1f)
    {
        DebugWindowManager manager = CreateUninitializedManager();

        SetField(manager, "_modelDoc", CreateModelDocument());
        SetField(manager, "_modelPrimitiveKindIndex", PrimitiveKindIndex(label));
        SetField(manager, "_modelPrimitiveStepped", stepped);
        SetField(manager, "_modelPrimitiveCullInternalFaces", true);
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
        SetField(manager, "_modelPrimitiveStep", step);
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
        return (parent, manager);
    }

    private static DebugWindowManager CreateUninitializedManager()
    {
#pragma warning disable SYSLIB0050
        return (DebugWindowManager)FormatterServices.GetUninitializedObject(typeof(DebugWindowManager));
#pragma warning restore SYSLIB0050
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

    private static object CreateElement(string name, double[] from, double[] to, bool faces)
    {
        Type elementType = typeof(DebugWindowManager).GetNestedType("ModelElementData", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(DebugWindowManager), "ModelElementData");
        object element = Activator.CreateInstance(elementType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create model element.");
        SetMember(element, "Name", name);
        SetMember(element, "From", from);
        SetMember(element, "To", to);

        if (faces)
        {
            Type faceType = typeof(DebugWindowManager).GetNestedType("ModelFaceData", BindingFlags.NonPublic)
                ?? throw new MissingMemberException(nameof(DebugWindowManager), "ModelFaceData");
            Array faceArray = (Array)GetMember(element, "Faces");
            for (int index = 0; index < faceArray.Length; index++)
            {
                object face = Activator.CreateInstance(faceType, nonPublic: true)
                    ?? throw new InvalidOperationException("Could not create model face.");
                SetMember(face, "Enabled", true);
                faceArray.SetValue(face, index);
            }
        }

        return element;
    }

    private static void AddChild(object parent, object child)
    {
        IList children = (IList)GetMember(parent, "Children");
        children.Add(child);
        SetMember(child, "Parent", parent);
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

    private static int GetInt(object target, string name)
    {
        return Convert.ToInt32(GetMember(target, name));
    }

    private static bool BoxesOverlap(PrimitiveBox left, PrimitiveBox right)
    {
        return Overlap(left.From[0], left.To[0], right.From[0], right.To[0]) > 0.0001 &&
            Overlap(left.From[1], left.To[1], right.From[1], right.To[1]) > 0.0001 &&
            Overlap(left.From[2], left.To[2], right.From[2], right.To[2]) > 0.0001;
    }

    private static double Overlap(double a0, double a1, double b0, double b1)
    {
        return Math.Min(a1, b1) - Math.Max(a0, b0);
    }

    private sealed record PrimitiveElement(double RotationX, double RotationY, double RotationZ);

    private sealed record PrimitiveBox(double[] From, double[] To)
    {
        public double SizeX => To[0] - From[0];
        public double SizeY => To[1] - From[1];
        public double SizeZ => To[2] - From[2];

        public bool Contains(double x, double y, double z)
        {
            return x >= From[0] - 0.0001 && x <= To[0] + 0.0001 &&
                y >= From[1] - 0.0001 && y <= To[1] + 0.0001 &&
                z >= From[2] - 0.0001 && z <= To[2] + 0.0001;
        }

        public override string ToString()
        {
            return $"[{From[0]}, {From[1]}, {From[2]}] to [{To[0]}, {To[1]}, {To[2]}]";
        }
    }
}
