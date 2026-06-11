using InGameDevTools.Utils;

namespace InGameDevTools.Tests;

public sealed class DevToolsTextDiffTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void BuildLineDiff_IdenticalTextsAreAllContext()
    {
        List<DevToolsDiffLine> diff = DevToolsTextDiff.BuildLineDiff(Lines("a", "b"), Lines("a", "b"));

        Assert.All(diff, line => Assert.Equal(DevToolsDiffLineKind.Context, line.Kind));
        Assert.Equal((0, 0), DevToolsTextDiff.CountChanges(diff));
    }

    [Fact]
    public void BuildLineDiff_EmptySidesProducePureAddsOrRemoves()
    {
        List<DevToolsDiffLine> added = DevToolsTextDiff.BuildLineDiff("", Lines("a", "b"));
        Assert.Equal((2, 0), DevToolsTextDiff.CountChanges(added));

        List<DevToolsDiffLine> removed = DevToolsTextDiff.BuildLineDiff(Lines("a", "b"), "");
        Assert.Equal((0, 2), DevToolsTextDiff.CountChanges(removed));
    }

    [Fact]
    public void BuildLineDiff_DetectsChangedLineAsRemovePlusAdd()
    {
        List<DevToolsDiffLine> diff = DevToolsTextDiff.BuildLineDiff(
            Lines("one", "two", "three"),
            Lines("one", "TWO", "three"));

        Assert.Equal(
            new[]
            {
                new DevToolsDiffLine(DevToolsDiffLineKind.Context, "one"),
                new DevToolsDiffLine(DevToolsDiffLineKind.Removed, "two"),
                new DevToolsDiffLine(DevToolsDiffLineKind.Added, "TWO"),
                new DevToolsDiffLine(DevToolsDiffLineKind.Context, "three")
            },
            diff);
    }

    [Fact]
    public void BuildLineDiff_HandlesInsertionInMiddle()
    {
        List<DevToolsDiffLine> diff = DevToolsTextDiff.BuildLineDiff(
            Lines("a", "c"),
            Lines("a", "b", "c"));

        Assert.Equal((1, 0), DevToolsTextDiff.CountChanges(diff));
        Assert.Contains(new DevToolsDiffLine(DevToolsDiffLineKind.Added, "b"), diff);
    }

    [Fact]
    public void BuildLineDiff_NormalizesWindowsLineEndings()
    {
        List<DevToolsDiffLine> diff = DevToolsTextDiff.BuildLineDiff("a\r\nb", "a\nb");

        Assert.Equal((0, 0), DevToolsTextDiff.CountChanges(diff));
    }

    [Fact]
    public void BuildLineDiff_OversizedMiddleFallsBackToBlockReplace()
    {
        string[] left = Enumerable.Range(0, 30).Select(i => $"left{i}").ToArray();
        string[] right = Enumerable.Range(0, 30).Select(i => $"right{i}").ToArray();

        List<DevToolsDiffLine> diff = DevToolsTextDiff.BuildLineDiff(Lines(left), Lines(right), maxLcsLines: 4);

        Assert.Equal((30, 30), DevToolsTextDiff.CountChanges(diff));
        // All removes come before all adds in the fallback.
        int lastRemoved = diff.FindLastIndex(line => line.Kind == DevToolsDiffLineKind.Removed);
        int firstAdded = diff.FindIndex(line => line.Kind == DevToolsDiffLineKind.Added);
        Assert.True(lastRemoved < firstAdded);
    }

    [Fact]
    public void CollapseContext_ShortensLongUnchangedRuns()
    {
        string[] lines = Enumerable.Range(0, 40).Select(i => $"line{i}").ToArray();
        string original = Lines(lines);
        string changed = Lines(lines.Select(line => line == "line20" ? "CHANGED" : line).ToArray());

        List<DevToolsDiffLine> collapsed = DevToolsTextDiff.CollapseContext(
            DevToolsTextDiff.BuildLineDiff(original, changed), contextLines: 3);

        Assert.Contains(collapsed, line => line.Kind == DevToolsDiffLineKind.Skip);
        Assert.True(collapsed.Count < 20, $"collapsed diff should be short, was {collapsed.Count}");
        Assert.Contains(new DevToolsDiffLine(DevToolsDiffLineKind.Removed, "line20"), collapsed);
        Assert.Contains(new DevToolsDiffLine(DevToolsDiffLineKind.Added, "CHANGED"), collapsed);
        // Three context lines survive on each side of the change.
        Assert.Contains(new DevToolsDiffLine(DevToolsDiffLineKind.Context, "line19"), collapsed);
        Assert.Contains(new DevToolsDiffLine(DevToolsDiffLineKind.Context, "line21"), collapsed);
    }

    [Fact]
    public void CollapseContext_KeepsShortRunsIntact()
    {
        List<DevToolsDiffLine> diff = DevToolsTextDiff.BuildLineDiff(
            Lines("a", "b", "c"),
            Lines("a", "B", "c"));

        List<DevToolsDiffLine> collapsed = DevToolsTextDiff.CollapseContext(diff, contextLines: 3);

        Assert.Equal(diff, collapsed);
    }
}
