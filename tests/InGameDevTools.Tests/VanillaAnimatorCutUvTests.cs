using InGameDevTools.Animations;
using System.Reflection;
using Vintagestory.API.Common;

namespace InGameDevTools.Tests;

public sealed class VanillaAnimatorCutUvTests
{
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void PreserveCutUvs_SplitsSideFaceUvsAcrossCutAxis()
    {
        ShapeElement source = CreateTexturedCube();
        ShapeElement lower = source.Clone();
        ShapeElement upper = source.Clone();
        lower.To![0] = 4;
        upper.From![0] = 4;

        MethodInfo method = typeof(DebugWindowManager).GetMethod("PreserveVanillaCutUvs", StaticFlags)
            ?? throw new MissingMethodException(nameof(DebugWindowManager), "PreserveVanillaCutUvs");
        method.Invoke(null, [source, lower, upper, 0, 4.0]);

        Assert.Equal([0f, 0f, 4f, 16f], lower.FacesResolved![0].Uv);
        Assert.Equal([4f, 0f, 16f, 16f], upper.FacesResolved![0].Uv);
        Assert.Equal([0f, 0f, 16f, 16f], lower.FacesResolved![1].Uv);
        Assert.Equal([0f, 0f, 16f, 16f], upper.FacesResolved![1].Uv);
        Assert.NotSame(source.FacesResolved![0], lower.FacesResolved![0]);
        Assert.NotSame(lower.FacesResolved![0], upper.FacesResolved![0]);
    }

    private static ShapeElement CreateTexturedCube()
    {
        ShapeElement element = new()
        {
            Name = "cube",
            From = [0, 0, 0],
            To = [16, 16, 16],
            FacesResolved = new ShapeElementFace[6]
        };

        for (int faceIndex = 0; faceIndex < 6; faceIndex++)
        {
            element.FacesResolved[faceIndex] = new ShapeElementFace
            {
                Texture = "all",
                Uv = [0f, 0f, 16f, 16f],
                Enabled = true
            };
        }

        return element;
    }
}
