using ImGuiNET;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Utils;

/// <summary>
/// Cached draft-vs-source diff panel for the JSON text editors. Both sides are normalized through
/// the JSON formatter when they parse, so formatting noise does not drown out real edits.
/// </summary>
internal static class DevToolsTextDiffView
{
    private sealed class CacheEntry
    {
        public string Original = "";
        public string Current = "";
        public List<DevToolsDiffLine> Display = [];
        public int Added;
        public int Removed;
        public bool CurrentParsed = true;
    }

    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);

    public static void Draw(string id, string originalText, string currentText, float height)
    {
        CacheEntry entry = GetOrUpdate(id, originalText, currentText);

        string summary = entry.Added == 0 && entry.Removed == 0
            ? "Draft matches the source."
            : $"+{entry.Added} added / -{entry.Removed} removed line(s) vs source.";
        if (!entry.CurrentParsed)
        {
            summary += " Draft does not parse; showing raw text diff.";
        }

        ImGui.TextDisabled(summary);

        if (entry.Display.Count == 0) return;

        uint addedColor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.45f, 0.85f, 0.45f, 1f));
        uint removedColor = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.45f, 0.38f, 1f));
        uint contextColor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.62f, 0.60f, 0.54f, 1f));
        uint skipColor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.48f, 0.46f, 0.42f, 1f));

        if (ImGui.BeginChild($"##{id}-diff-lines", new NVector2(-float.Epsilon, height), true, ImGuiWindowFlags.HorizontalScrollbar))
        {
            foreach (DevToolsDiffLine line in entry.Display)
            {
                (uint color, string prefix) = line.Kind switch
                {
                    DevToolsDiffLineKind.Added => (addedColor, "+ "),
                    DevToolsDiffLineKind.Removed => (removedColor, "- "),
                    DevToolsDiffLineKind.Skip => (skipColor, "  "),
                    _ => (contextColor, "  ")
                };

                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.TextUnformatted(prefix + line.Text);
                ImGui.PopStyleColor();
            }
        }
        ImGui.EndChild();
    }

    public static void Invalidate(string id)
    {
        Cache.Remove(id);
    }

    private static CacheEntry GetOrUpdate(string id, string originalText, string currentText)
    {
        if (!Cache.TryGetValue(id, out CacheEntry? entry))
        {
            entry = new CacheEntry { Original = "\0unset" };
            Cache[id] = entry;
        }

        if (string.Equals(entry.Original, originalText, StringComparison.Ordinal) &&
            string.Equals(entry.Current, currentText, StringComparison.Ordinal))
        {
            return entry;
        }

        entry.Original = originalText;
        entry.Current = currentText;

        string normalizedOriginal = DevToolsJsonTextTools.TryFormat(originalText, out string formattedOriginal, out _)
            ? formattedOriginal
            : originalText;
        entry.CurrentParsed = DevToolsJsonTextTools.TryFormat(currentText, out string formattedCurrent, out _);
        string normalizedCurrent = entry.CurrentParsed ? formattedCurrent : currentText;

        List<DevToolsDiffLine> diff = DevToolsTextDiff.BuildLineDiff(normalizedOriginal, normalizedCurrent);
        (entry.Added, entry.Removed) = DevToolsTextDiff.CountChanges(diff);
        entry.Display = DevToolsTextDiff.CollapseContext(diff);
        return entry;
    }
}
