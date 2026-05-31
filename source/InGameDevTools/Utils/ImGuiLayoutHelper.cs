#if DEBUG
using ImGuiNET;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Utils;

internal sealed class ImGuiThreePanelLayoutState
{
    private readonly float _defaultLeftFraction;
    private readonly float _defaultRightFraction;

    public ImGuiThreePanelLayoutState(float leftFraction, float rightFraction)
    {
        _defaultLeftFraction = leftFraction;
        _defaultRightFraction = rightFraction;
        LeftFraction = leftFraction;
        RightFraction = rightFraction;
    }

    public float LeftFraction;
    public float RightFraction;

    public void Reset()
    {
        LeftFraction = _defaultLeftFraction;
        RightFraction = _defaultRightFraction;
    }
}

internal static class ImGuiLayoutHelper
{
    public static void CalculateThreePanelWidths(
        float totalWidth,
        float splitterThickness,
        ImGuiThreePanelLayoutState state,
        float minLeftWidth,
        float maxLeftWidth,
        float minCenterWidth,
        float minRightWidth,
        float maxRightWidth,
        out float panelAvailableWidth,
        out float leftWidth,
        out float centerWidth,
        out float rightWidth)
    {
        panelAvailableWidth = Math.Max(1f, totalWidth - splitterThickness * 2f);
        leftWidth = Math.Clamp(panelAvailableWidth * state.LeftFraction, minLeftWidth, maxLeftWidth);
        rightWidth = Math.Clamp(panelAvailableWidth * state.RightFraction, minRightWidth, maxRightWidth);

        float availableForSidePanels = Math.Max(0f, panelAvailableWidth - minCenterWidth);
        float sideWidth = leftWidth + rightWidth;
        if (sideWidth > availableForSidePanels && sideWidth > 0f)
        {
            float scale = availableForSidePanels / sideWidth;
            leftWidth *= scale;
            rightWidth *= scale;
        }

        centerWidth = Math.Max(minCenterWidth, panelAvailableWidth - leftWidth - rightWidth);
        state.LeftFraction = Math.Clamp(leftWidth / panelAvailableWidth, 0.05f, 0.8f);
        state.RightFraction = Math.Clamp(rightWidth / panelAvailableWidth, 0.05f, 0.8f);
    }

    public static void DrawVerticalSplitter(
        string id,
        float height,
        float thickness,
        float availableWidth,
        ref float fraction,
        float minWidth,
        float maxWidth,
        bool invertDrag = false)
    {
        ImGui.InvisibleButton(id, new NVector2(thickness, height));
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        if (hovered || active)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        }

        if (active && availableWidth > 1f)
        {
            float delta = ImGui.GetIO().MouseDelta.X * (invertDrag ? -1f : 1f);
            float width = Math.Clamp(fraction * availableWidth + delta, minWidth, maxWidth);
            fraction = Math.Clamp(width / availableWidth, 0.05f, 0.8f);
        }

        NVector2 min = ImGui.GetItemRectMin();
        NVector2 max = ImGui.GetItemRectMax();
        float x = (min.X + max.X) * 0.5f;
        ImGui.GetWindowDrawList().AddLine(
            new NVector2(x, min.Y + 3f),
            new NVector2(x, max.Y - 3f),
            SplitterColor(active, hovered),
            Math.Max(2f, thickness * 0.35f));
    }

    public static void DrawHorizontalSplitter(
        string id,
        float width,
        float thickness,
        float availableHeight,
        ref float bottomFraction,
        float minBottomHeight,
        float maxBottomHeight)
    {
        ImGui.InvisibleButton(id, new NVector2(width, thickness));
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        if (hovered || active)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
        }

        if (active && availableHeight > 1f)
        {
            float bottomHeight = Math.Clamp(bottomFraction * availableHeight - ImGui.GetIO().MouseDelta.Y, minBottomHeight, maxBottomHeight);
            bottomFraction = Math.Clamp(bottomHeight / availableHeight, 0.05f, 0.9f);
        }

        NVector2 min = ImGui.GetItemRectMin();
        NVector2 max = ImGui.GetItemRectMax();
        float y = (min.Y + max.Y) * 0.5f;
        ImGui.GetWindowDrawList().AddLine(
            new NVector2(min.X + 3f, y),
            new NVector2(max.X - 3f, y),
            SplitterColor(active, hovered),
            Math.Max(2f, thickness * 0.35f));
    }

    public static bool DrawDomainCombo(string id, ref string selectedDomain, IEnumerable<string> domains)
    {
        string[] options = domains
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain.Equals("game", StringComparison.OrdinalIgnoreCase) ? "" : domain, StringComparer.OrdinalIgnoreCase)
            .Prepend("All domains")
            .ToArray();

        string currentDomain = selectedDomain;
        int current = string.IsNullOrWhiteSpace(currentDomain)
            ? 0
            : Math.Max(0, Array.FindIndex(options, option => option.Equals(currentDomain, StringComparison.OrdinalIgnoreCase)));

        bool changed = ImGui.Combo(id, ref current, options, options.Length);
        if (changed)
        {
            selectedDomain = current <= 0 ? "" : options[current];
        }

        return changed;
    }

    public static bool MatchesDomain(string selectedDomain, string domain)
    {
        return string.IsNullOrWhiteSpace(selectedDomain) || selectedDomain.Equals(domain, StringComparison.OrdinalIgnoreCase);
    }

    public static string CompactAssetCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        string result = value;
        result = ReplaceOrdinalIgnoreCase(result, "block:game:", "block:");
        result = ReplaceOrdinalIgnoreCase(result, "item:game:", "item:");
        result = ReplaceOrdinalIgnoreCase(result, " | game:", " | ");
        if (result.StartsWith("game:", StringComparison.OrdinalIgnoreCase))
        {
            result = result[5..];
        }

        return result;
    }

    public static string GetDomainFromAssetCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "game";

        string code = value;
        if (code.StartsWith("block:", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("item:", StringComparison.OrdinalIgnoreCase))
        {
            int firstColon = code.IndexOf(':');
            code = firstColon >= 0 && firstColon + 1 < code.Length ? code[(firstColon + 1)..] : code;
        }

        int hash = code.IndexOf('#');
        if (hash >= 0) code = code[..hash];

        int colon = code.IndexOf(':');
        return colon > 0 ? code[..colon] : "game";
    }

    private static uint SplitterColor(bool active, bool hovered)
    {
        return ImGui.ColorConvertFloat4ToU32(active
            ? new NVector4(0.8f, 0.55f, 0.24f, 1f)
            : hovered
                ? new NVector4(0.7f, 0.5f, 0.25f, 0.9f)
                : new NVector4(0.42f, 0.38f, 0.32f, 0.75f));
    }

    private static string ReplaceOrdinalIgnoreCase(string value, string oldValue, string newValue)
    {
        int index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            value = value[..index] + newValue + value[(index + oldValue.Length)..];
            index = value.IndexOf(oldValue, index + newValue.Length, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }
}
#endif
