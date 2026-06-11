using ImGuiNET;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Utils;

internal enum DevToolsViewportBackgroundStyle
{
    Dark,
    Grey,
    Light
}

/// <summary>
/// Shared user-selectable background tone for the editor preview viewports, so dark models stay
/// visible. Keeps the ImGui background fills and the FBO clear colors behind them in sync, and
/// provides matching text/grid colors that stay readable on every tone.
/// </summary>
internal static class DevToolsViewportBackground
{
    public const string NameDark = "Dark";
    public const string NameGrey = "Grey";
    public const string NameLight = "Light";

    public static readonly string[] StyleNames = [NameDark, NameGrey, NameLight];

    public static DevToolsViewportBackgroundStyle Style { get; set; } = DevToolsViewportBackgroundStyle.Dark;

    public static NVector4 FillColor => Style switch
    {
        DevToolsViewportBackgroundStyle.Grey => new NVector4(0.32f, 0.32f, 0.31f, 1f),
        DevToolsViewportBackgroundStyle.Light => new NVector4(0.78f, 0.77f, 0.74f, 1f),
        _ => new NVector4(0.035f, 0.036f, 0.032f, 1f)
    };

    public static NVector4 TextColor => Style switch
    {
        DevToolsViewportBackgroundStyle.Light => new NVector4(0.16f, 0.15f, 0.12f, 1f),
        _ => new NVector4(0.86f, 0.82f, 0.72f, 1f)
    };

    public static NVector4 GridMinorColor => Style switch
    {
        DevToolsViewportBackgroundStyle.Grey => new NVector4(0.55f, 0.54f, 0.50f, 0.35f),
        DevToolsViewportBackgroundStyle.Light => new NVector4(0.25f, 0.24f, 0.20f, 0.30f),
        _ => new NVector4(0.28f, 0.27f, 0.22f, 0.42f)
    };

    public static NVector4 GridMajorColor => Style switch
    {
        DevToolsViewportBackgroundStyle.Grey => new NVector4(0.72f, 0.70f, 0.64f, 0.60f),
        DevToolsViewportBackgroundStyle.Light => new NVector4(0.30f, 0.28f, 0.22f, 0.60f),
        _ => new NVector4(0.45f, 0.42f, 0.33f, 0.72f)
    };

    public static void DrawFill(ImDrawListPtr drawList, NVector2 min, NVector2 max, float rounding = 4f)
    {
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(FillColor), rounding);
    }

    public static DevToolsViewportBackgroundStyle Parse(string? name)
    {
        if (string.Equals(name, NameGrey, StringComparison.OrdinalIgnoreCase)) return DevToolsViewportBackgroundStyle.Grey;
        if (string.Equals(name, NameLight, StringComparison.OrdinalIgnoreCase)) return DevToolsViewportBackgroundStyle.Light;
        return DevToolsViewportBackgroundStyle.Dark;
    }

    public static string NormalizeName(string? name)
    {
        return Parse(name) switch
        {
            DevToolsViewportBackgroundStyle.Grey => NameGrey,
            DevToolsViewportBackgroundStyle.Light => NameLight,
            _ => NameDark
        };
    }
}
