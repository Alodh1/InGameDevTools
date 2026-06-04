using InGameDevTools.Animations;

namespace InGameDevTools.Tests;

public sealed class EasingFunctionsTests
{
    [Theory]
    [InlineData(EasingFunctionType.Linear, 0.5f, 0.5f)]
    [InlineData(EasingFunctionType.Quadratic, 0.5f, 0.25f)]
    [InlineData(EasingFunctionType.Cubic, 0.5f, 0.125f)]
    [InlineData(EasingFunctionType.Quintic, 0.5f, 0.03125f)]
    [InlineData(EasingFunctionType.CosShifted, 0.5f, 0.5f)]
    [InlineData(EasingFunctionType.Bounce, 0.5f, 0.85f)]
    public void Get_ReturnsExpectedLegacyCurveValues(EasingFunctionType type, float progress, float expected)
    {
        float actual = EasingFunctions.Get(type)(progress);

        Assert.Equal(expected, actual, precision: 5);
    }

    [Fact]
    public void StandardEasingFunctions_StartAndEndAtExpectedEndpoints()
    {
        foreach (EasingFunctionType type in Enum.GetValues<EasingFunctionType>().Where(type => type > EasingFunctionType.Bounce))
        {
            float start = StandardEasingFunctions.Calculate(type, 0f);
            float end = StandardEasingFunctions.Calculate(type, 1f);

            Assert.True(float.IsFinite(start), $"{type} start should be finite.");
            Assert.True(float.IsFinite(end), $"{type} end should be finite.");
            Assert.Equal(0f, start, precision: 5);
            Assert.Equal(1f, end, precision: 5);
        }
    }

    [Fact]
    public void ToCrc32_IsCaseInsensitive()
    {
        Assert.Equal(EasingFunctions.ToCrc32("MyCustomEase"), EasingFunctions.ToCrc32("mycustomease"));
    }
}
