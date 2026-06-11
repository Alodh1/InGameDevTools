using InGameDevTools.Utils;
using System.Numerics;

namespace InGameDevTools.Tests;

public sealed class DevToolsViewportBackgroundTests : IDisposable
{
    public void Dispose()
    {
        DevToolsViewportBackground.Style = DevToolsViewportBackgroundStyle.Dark;
    }

    [Theory]
    [InlineData("Dark", "Dark")]
    [InlineData("grey", "Grey")]
    [InlineData("GREY", "Grey")]
    [InlineData("light", "Light")]
    [InlineData(null, "Dark")]
    [InlineData("", "Dark")]
    [InlineData("checker", "Dark")]
    public void Parse_MapsNamesCaseInsensitivelyWithDarkFallback(string? name, string expectedName)
    {
        Assert.Equal(Enum.Parse<DevToolsViewportBackgroundStyle>(expectedName), DevToolsViewportBackground.Parse(name));
    }

    [Theory]
    [InlineData("light", "Light")]
    [InlineData("grey", "Grey")]
    [InlineData("dark", "Dark")]
    [InlineData("unknown", "Dark")]
    [InlineData(null, "Dark")]
    public void NormalizeName_ReturnsCanonicalStyleName(string? name, string expected)
    {
        Assert.Equal(expected, DevToolsViewportBackground.NormalizeName(name));
    }

    [Fact]
    public void StyleNames_RoundTripThroughParseAndNormalize()
    {
        foreach (string name in DevToolsViewportBackground.StyleNames)
        {
            Assert.Equal(name, DevToolsViewportBackground.NormalizeName(name));
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Grey")]
    [InlineData("Light")]
    public void TextColor_KeepsReadableContrastAgainstFill(string styleName)
    {
        DevToolsViewportBackground.Style = DevToolsViewportBackground.Parse(styleName);

        float fillLuminance = Luminance(DevToolsViewportBackground.FillColor);
        float textLuminance = Luminance(DevToolsViewportBackground.TextColor);

        Assert.True(Math.Abs(fillLuminance - textLuminance) > 0.35f,
            $"{styleName}: text luminance {textLuminance:0.00} too close to fill luminance {fillLuminance:0.00}");
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Grey")]
    [InlineData("Light")]
    public void FillColor_IsAlwaysOpaque(string styleName)
    {
        DevToolsViewportBackground.Style = DevToolsViewportBackground.Parse(styleName);

        Assert.Equal(1f, DevToolsViewportBackground.FillColor.W);
    }

    private static float Luminance(Vector4 color)
    {
        return 0.2126f * color.X + 0.7152f * color.Y + 0.0722f * color.Z;
    }
}
