using InGameDevTools.Utils;

namespace InGameDevTools.Tests;

public sealed class DevToolsFuzzyMatchTests
{
    [Fact]
    public void Score_EmptyQueryMatchesEverythingNeutrally()
    {
        Assert.Equal(0, DevToolsFuzzyMatch.Score("anything", ""));
        Assert.Equal(0, DevToolsFuzzyMatch.Score("anything", null));
        Assert.Equal(0, DevToolsFuzzyMatch.Score("anything", "   "));
    }

    [Fact]
    public void Score_EmptyTextNeverMatchesNonEmptyQuery()
    {
        Assert.True(DevToolsFuzzyMatch.Score("", "query") < 0);
        Assert.True(DevToolsFuzzyMatch.Score(null, "query") < 0);
    }

    [Fact]
    public void Score_AllTokensMustMatch()
    {
        Assert.True(DevToolsFuzzyMatch.Score("transform block game:oakplanks", "block oak") >= 0);
        Assert.True(DevToolsFuzzyMatch.Score("transform block game:oakplanks", "block iron") < 0);
    }

    [Fact]
    public void Score_IsCaseInsensitive()
    {
        Assert.True(DevToolsFuzzyMatch.Score("Transform Block Game:OakPlanks", "BLOCK oak") >= 0);
    }

    [Fact]
    public void Score_PrefixBeatsWordBoundaryBeatsBuriedSubstring()
    {
        int prefix = DevToolsFuzzyMatch.Score("oakplanks block", "oak");
        int boundary = DevToolsFuzzyMatch.Score("block game:oakplanks", "oak");
        int buried = DevToolsFuzzyMatch.Score("block game:redoakplanks", "oak");

        Assert.True(prefix > boundary, $"prefix {prefix} should beat word boundary {boundary}");
        Assert.True(boundary > buried, $"word boundary {boundary} should beat buried {buried}");
    }

    [Fact]
    public void Score_SubstringBeatsSubsequence()
    {
        int substring = DevToolsFuzzyMatch.Score("worldgen deposits game:ores", "ores");
        int subsequence = DevToolsFuzzyMatch.Score("open recipe editor sources", "ores");

        Assert.True(substring > 0 && subsequence > 0);
        Assert.True(substring > subsequence, $"substring {substring} should beat subsequence {subsequence}");
    }

    [Fact]
    public void Score_ShortTokensDoNotFallBackToSubsequence()
    {
        // "tk" appears as a subsequence of "transform block" but is too short to count.
        Assert.True(DevToolsFuzzyMatch.Score("transform block", "tk") < 0);
        // Three characters and up may match as a subsequence.
        Assert.True(DevToolsFuzzyMatch.Score("transform block", "tfb") >= 0);
    }

    [Fact]
    public void Score_SubsequenceRespectsCharacterOrder()
    {
        Assert.True(DevToolsFuzzyMatch.Score("abcdef", "ace") >= 0);
        Assert.True(DevToolsFuzzyMatch.Score("abcdef", "eca") < 0);
    }

    [Fact]
    public void Score_TighterLabelsRankAboveLongOnes()
    {
        int tight = DevToolsFuzzyMatch.Score("wolf", "wolf");
        int loose = DevToolsFuzzyMatch.Score("wolf" + new string('x', 400), "wolf");

        Assert.True(tight > loose, $"tight {tight} should beat loose {loose}");
    }

    [Fact]
    public void Matches_MirrorsScoreSign()
    {
        Assert.True(DevToolsFuzzyMatch.Matches("transform block", "block"));
        Assert.False(DevToolsFuzzyMatch.Matches("transform block", "zzz"));
    }
}
