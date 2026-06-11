using InGameDevTools.Utils;

namespace InGameDevTools.Tests;

public sealed class DevToolsTextHistoryTests
{
    [Fact]
    public void Reset_SeedsHistoryWithSingleState()
    {
        DevToolsTextHistory history = new();
        history.Reset("seed");

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal("seed", history.Current);
    }

    [Fact]
    public void Record_IdenticalTextIsNoOp()
    {
        DevToolsTextHistory history = new();
        history.Reset("a");
        history.Record("a", now: 10.0);

        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Record_PushesNewStateAndUndoRestoresPrevious()
    {
        DevToolsTextHistory history = new();
        history.Reset("a");
        history.Record("ab", now: 10.0);

        Assert.True(history.CanUndo);
        Assert.True(history.TryUndo(out string text));
        Assert.Equal("a", text);
        Assert.True(history.CanRedo);
        Assert.True(history.TryRedo(out text));
        Assert.Equal("ab", text);
    }

    [Fact]
    public void Record_CoalescesRapidEditsIntoOneStep()
    {
        DevToolsTextHistory history = new(coalesceSeconds: 0.75);
        history.Reset("a");
        history.Record("ab", now: 10.0);
        history.Record("abc", now: 10.2);
        history.Record("abcd", now: 10.4);

        Assert.True(history.TryUndo(out string text));
        Assert.Equal("a", text);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Record_SeparateBurstsAreSeparateSteps()
    {
        DevToolsTextHistory history = new(coalesceSeconds: 0.75);
        history.Reset("a");
        history.Record("ab", now: 10.0);
        history.Record("abc", now: 20.0);

        Assert.True(history.TryUndo(out string text));
        Assert.Equal("ab", text);
        Assert.True(history.TryUndo(out text));
        Assert.Equal("a", text);
    }

    [Fact]
    public void Record_AfterUndoTruncatesRedoBranch()
    {
        DevToolsTextHistory history = new();
        history.Reset("a");
        history.Record("ab", now: 10.0);
        history.Record("abc", now: 20.0);
        history.TryUndo(out _);

        history.Record("abX", now: 30.0);

        Assert.False(history.CanRedo);
        Assert.True(history.TryUndo(out string text));
        Assert.Equal("ab", text);
    }

    [Fact]
    public void Record_AfterUndoDoesNotCoalesceIntoRestoredState()
    {
        DevToolsTextHistory history = new(coalesceSeconds: 0.75);
        history.Reset("a");
        history.Record("ab", now: 10.0);
        history.Record("abc", now: 20.0);
        history.TryUndo(out _); // back to "ab"

        // Within the coalescing window of the 20.0 record, but the undo must force a new step
        // instead of overwriting the restored "ab" state.
        history.Record("abQ", now: 20.1);

        Assert.True(history.TryUndo(out string text));
        Assert.Equal("ab", text);
    }

    [Fact]
    public void Record_CapacityDropsOldestStates()
    {
        DevToolsTextHistory history = new(capacity: 3);
        history.Reset("s0");
        history.Record("s1", now: 10.0);
        history.Record("s2", now: 20.0);
        history.Record("s3", now: 30.0);

        Assert.Equal("s3", history.Current);
        Assert.True(history.TryUndo(out string text));
        Assert.Equal("s2", text);
        Assert.True(history.TryUndo(out text));
        Assert.Equal("s1", text);
        Assert.False(history.CanUndo); // s0 was dropped
    }

    [Fact]
    public void TryUndoRedo_AtBoundsReturnFalseAndKeepCurrent()
    {
        DevToolsTextHistory history = new();
        history.Reset("a");

        Assert.False(history.TryUndo(out string text));
        Assert.Equal("a", text);
        Assert.False(history.TryRedo(out text));
        Assert.Equal("a", text);
    }
}

public sealed class DevToolsJsonTextToolsTests
{
    [Fact]
    public void TryFormat_PrettyPrintsValidJson()
    {
        Assert.True(DevToolsJsonTextTools.TryFormat("{\"a\":1,\"b\":[1,2]}", out string formatted, out _));
        Assert.Contains("\"a\": 1", formatted);
        Assert.Contains(Environment.NewLine, formatted);
    }

    [Fact]
    public void TryFormat_PreservesPropertyOrder()
    {
        Assert.True(DevToolsJsonTextTools.TryFormat("{\"zeta\":1,\"alpha\":2}", out string formatted, out _));
        Assert.True(formatted.IndexOf("zeta", StringComparison.Ordinal) < formatted.IndexOf("alpha", StringComparison.Ordinal));
    }

    [Fact]
    public void TryFormat_FailsOnInvalidJsonAndKeepsText()
    {
        string input = "{ not json";
        Assert.False(DevToolsJsonTextTools.TryFormat(input, out string formatted, out string error));
        Assert.Equal(input, formatted);
        Assert.NotEqual("", error);
    }

    [Fact]
    public void TryFormat_AcceptsVintageStoryRelaxedJson()
    {
        // Unquoted property names parse through the Vintage Story fallback parser.
        Assert.True(DevToolsJsonTextTools.TryFormat("{ code: \"thing\" }", out string formatted, out _));
        Assert.Contains("\"code\": \"thing\"", formatted);
    }
}
