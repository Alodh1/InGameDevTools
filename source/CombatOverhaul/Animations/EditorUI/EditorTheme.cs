#if DEBUG
using Vintagestory.API.Client;

namespace CombatOverhaul.Animations.EditorUI;

internal static class EditorTheme
{
    public const double WindowWidth = 1180;
    public const double WindowHeight = 720;
    public const double Padding = 14;
    public const double ToolbarHeight = 38;
    public const double FooterHeight = 28;
    public const double LeftPanelWidth = 240;
    public const double RightPanelWidth = 280;
    public const double BottomPanelHeight = 155;
    public const double Gap = 8;

    public static CairoFont TitleFont => CairoFont.WhiteSmallText().WithFontSize(18);
    public static CairoFont HeaderFont => CairoFont.WhiteSmallText().WithFontSize(15);
    public static CairoFont BodyFont => CairoFont.WhiteSmallText().WithFontSize(13);
    public static CairoFont MutedFont => CairoFont.WhiteSmallText().WithFontSize(12).WithColor(new double[] { 0.70, 0.68, 0.62, 1.0 });
    public static CairoFont ButtonFont => CairoFont.WhiteSmallText().WithFontSize(13).WithOrientation(EnumTextOrientation.Center);
}
#endif
