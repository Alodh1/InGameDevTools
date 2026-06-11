namespace InGameDevTools.Utils;

internal enum DevToolsDiffLineKind
{
    Context,
    Added,
    Removed,
    Skip
}

internal readonly record struct DevToolsDiffLine(DevToolsDiffLineKind Kind, string Text);

/// <summary>
/// Line-based diff for the draft-vs-source views. Common prefix/suffix lines are trimmed first;
/// the middle is diffed with an LCS table, falling back to a whole-block remove+add when the
/// middle is too large to diff cheaply.
/// </summary>
internal static class DevToolsTextDiff
{
    public static List<DevToolsDiffLine> BuildLineDiff(string original, string current, int maxLcsLines = 1200)
    {
        string[] a = SplitLines(original);
        string[] b = SplitLines(current);

        int prefix = 0;
        int maxPrefix = Math.Min(a.Length, b.Length);
        while (prefix < maxPrefix && string.Equals(a[prefix], b[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        int suffix = 0;
        int maxSuffix = Math.Min(a.Length, b.Length) - prefix;
        while (suffix < maxSuffix && string.Equals(a[a.Length - 1 - suffix], b[b.Length - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        List<DevToolsDiffLine> result = new(a.Length + b.Length - prefix - suffix);
        for (int i = 0; i < prefix; i++)
        {
            result.Add(new(DevToolsDiffLineKind.Context, a[i]));
        }

        AppendMiddleDiff(result, a, prefix, a.Length - suffix, b, prefix, b.Length - suffix, maxLcsLines);

        for (int i = a.Length - suffix; i < a.Length; i++)
        {
            result.Add(new(DevToolsDiffLineKind.Context, a[i]));
        }

        return result;
    }

    public static (int Added, int Removed) CountChanges(IReadOnlyList<DevToolsDiffLine> diff)
    {
        int added = 0;
        int removed = 0;
        foreach (DevToolsDiffLine line in diff)
        {
            if (line.Kind == DevToolsDiffLineKind.Added) added++;
            else if (line.Kind == DevToolsDiffLineKind.Removed) removed++;
        }

        return (added, removed);
    }

    /// <summary>Collapses long unchanged runs to keep the rendered diff short.</summary>
    public static List<DevToolsDiffLine> CollapseContext(IReadOnlyList<DevToolsDiffLine> diff, int contextLines = 3)
    {
        List<DevToolsDiffLine> collapsed = [];
        int index = 0;
        while (index < diff.Count)
        {
            if (diff[index].Kind != DevToolsDiffLineKind.Context)
            {
                collapsed.Add(diff[index]);
                index++;
                continue;
            }

            int runStart = index;
            while (index < diff.Count && diff[index].Kind == DevToolsDiffLineKind.Context)
            {
                index++;
            }

            int runLength = index - runStart;
            bool atStart = runStart == 0;
            bool atEnd = index >= diff.Count;
            int keepBefore = atStart ? 0 : contextLines;
            int keepAfter = atEnd ? 0 : contextLines;
            if (runLength <= keepBefore + keepAfter + 1)
            {
                for (int i = runStart; i < index; i++)
                {
                    collapsed.Add(diff[i]);
                }

                continue;
            }

            for (int i = runStart; i < runStart + keepBefore; i++)
            {
                collapsed.Add(diff[i]);
            }

            collapsed.Add(new(DevToolsDiffLineKind.Skip, $"··· {runLength - keepBefore - keepAfter} unchanged line(s) ···"));

            for (int i = index - keepAfter; i < index; i++)
            {
                collapsed.Add(diff[i]);
            }
        }

        return collapsed;
    }

    private static void AppendMiddleDiff(
        List<DevToolsDiffLine> result,
        string[] a, int aStart, int aEnd,
        string[] b, int bStart, int bEnd,
        int maxLcsLines)
    {
        int aLength = aEnd - aStart;
        int bLength = bEnd - bStart;

        if (aLength <= 0 && bLength <= 0) return;

        if (aLength <= 0)
        {
            for (int i = bStart; i < bEnd; i++) result.Add(new(DevToolsDiffLineKind.Added, b[i]));
            return;
        }

        if (bLength <= 0)
        {
            for (int i = aStart; i < aEnd; i++) result.Add(new(DevToolsDiffLineKind.Removed, a[i]));
            return;
        }

        if ((long)aLength * bLength > (long)maxLcsLines * maxLcsLines)
        {
            // Too large for the LCS table: report the whole middle as replaced.
            for (int i = aStart; i < aEnd; i++) result.Add(new(DevToolsDiffLineKind.Removed, a[i]));
            for (int i = bStart; i < bEnd; i++) result.Add(new(DevToolsDiffLineKind.Added, b[i]));
            return;
        }

        int[,] lcs = new int[aLength + 1, bLength + 1];
        for (int i = aLength - 1; i >= 0; i--)
        {
            for (int j = bLength - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(a[aStart + i], b[bStart + j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int ai = 0;
        int bi = 0;
        while (ai < aLength && bi < bLength)
        {
            if (string.Equals(a[aStart + ai], b[bStart + bi], StringComparison.Ordinal))
            {
                result.Add(new(DevToolsDiffLineKind.Context, a[aStart + ai]));
                ai++;
                bi++;
            }
            else if (lcs[ai + 1, bi] >= lcs[ai, bi + 1])
            {
                result.Add(new(DevToolsDiffLineKind.Removed, a[aStart + ai]));
                ai++;
            }
            else
            {
                result.Add(new(DevToolsDiffLineKind.Added, b[bStart + bi]));
                bi++;
            }
        }

        while (ai < aLength)
        {
            result.Add(new(DevToolsDiffLineKind.Removed, a[aStart + ai]));
            ai++;
        }

        while (bi < bLength)
        {
            result.Add(new(DevToolsDiffLineKind.Added, b[bStart + bi]));
            bi++;
        }
    }

    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        return text.Replace("\r\n", "\n").Split('\n');
    }
}
