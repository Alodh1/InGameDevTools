using InGameDevTools.Animations;
using System.Reflection;

namespace InGameDevTools.Tests;

public sealed class WorldgenAssetVisibilityTests
{
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type ManagerType = typeof(DebugWindowManager);
    private static readonly Type KindType = ManagerType.GetNestedType("WorldgenAssetKind", BindingFlags.NonPublic)!;

    [Theory]
    [InlineData("Other", false, "Deposits", true)]
    [InlineData("Other", false, "RockStrata", true)]
    [InlineData("Other", true, "Deposits", false)]
    [InlineData("Other", true, "Other", true)]
    [InlineData("Deposits", false, "Deposits", true)]
    [InlineData("Deposits", false, "Landforms", false)]
    public void KindFilter_KeepsOnlyPendingOtherEntriesDiscoverable(
        string entryKind,
        bool isContentClassified,
        string filterKind,
        bool expected)
    {
        MethodInfo method = ManagerType.GetMethod("MatchesWorldgenKindFilter", StaticFlags)!;
        object? result = method.Invoke(null,
        [
            Enum.Parse(KindType, entryKind),
            isContentClassified,
            Enum.Parse(KindType, filterKind)
        ]);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void OrePreview_KeepsOtherEligibleOnlyWhileClassificationIsPending(
        bool isContentClassified,
        bool expected)
    {
        MethodInfo method = ManagerType.GetMethod("IsWorldgenEntryCompatibleWithPreviewMode", StaticFlags)!;
        object?[] arguments =
        [
            Enum.Parse(KindType, "Other"),
            isContentClassified,
            7,
            null
        ];

        object? result = method.Invoke(null, arguments);

        Assert.Equal(expected, result);
        Assert.False(string.IsNullOrWhiteSpace(arguments[3]?.ToString()));
    }
}
