namespace InGameDevTools.Utils;

/// <summary>
/// Scoring matcher for the command palette and browser filters. Every whitespace-separated query
/// token must match (substring preferred, in-order subsequence as fallback for tokens of 3+
/// characters); the total score ranks substring/prefix/word-boundary hits above loose matches.
/// </summary>
internal static class DevToolsFuzzyMatch
{
    private const int SubstringBaseScore = 100;
    private const int PrefixBonus = 40;
    private const int WordBoundaryBonus = 25;
    private const int SubsequenceScore = 10;

    /// <summary>Returns a negative value when <paramref name="query"/> does not match; higher is better.</summary>
    public static int Score(string? text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        if (string.IsNullOrEmpty(text)) return -1;

        int total = 0;
        foreach (string token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int tokenScore = ScoreToken(text, token);
            if (tokenScore < 0) return -1;
            total += tokenScore;
        }

        return total;
    }

    public static bool Matches(string? text, string? query)
    {
        return Score(text, query) >= 0;
    }

    private static int ScoreToken(string text, string token)
    {
        int index = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            int score = SubstringBaseScore;
            if (index == 0)
            {
                score += PrefixBonus;
            }
            else if (IsWordBoundary(text[index - 1]))
            {
                score += WordBoundaryBonus;
            }

            // Earlier hits in shorter labels rank above hits buried deep in long search text.
            score += Math.Max(0, 30 - index / 4);
            score -= Math.Min(20, Math.Max(0, text.Length - token.Length) / 16);
            return score;
        }

        // Short tokens as subsequences match almost anything; require 3+ characters.
        return token.Length >= 3 && IsSubsequence(text, token) ? SubsequenceScore : -1;
    }

    private static bool IsWordBoundary(char previous)
    {
        return previous is ' ' or ':' or '/' or '-' or '_' or '.' or '(' or '[';
    }

    private static bool IsSubsequence(string text, string token)
    {
        int tokenIndex = 0;
        for (int textIndex = 0; textIndex < text.Length && tokenIndex < token.Length; textIndex++)
        {
            if (char.ToLowerInvariant(text[textIndex]) == char.ToLowerInvariant(token[tokenIndex]))
            {
                tokenIndex++;
            }
        }

        return tokenIndex >= token.Length;
    }
}
