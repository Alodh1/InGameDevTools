using InGameDevTools.Utils;

namespace InGameDevTools.Tests;

public sealed class ParticlePreviewTimingTests
{
    [Fact]
    public void ScaleDelta_ReturnsZeroWhenPaused()
    {
        Assert.Equal(0f, ParticlePreviewTiming.ScaleDelta(0.016f, 4f, paused: true));
    }

    [Fact]
    public void ScaleDelta_ClampsFrameDeltaAndAppliesSpeed()
    {
        Assert.Equal(0.2f, ParticlePreviewTiming.ScaleDelta(0.5f, 2f, paused: false), precision: 5);
    }

    [Fact]
    public void TakeRuntimeTicks_AccumulatesScaledTimeAndKeepsRemainder()
    {
        float accumulator = 0f;

        int first = ParticlePreviewTiming.TakeRuntimeTicks(0.016f, ref accumulator, 0.033f, maxTicks: 8);
        int second = ParticlePreviewTiming.TakeRuntimeTicks(0.050f, ref accumulator, 0.033f, maxTicks: 8);

        Assert.Equal(0, first);
        Assert.Equal(2, second);
        Assert.Equal(0f, accumulator, precision: 5);
    }

    [Fact]
    public void TakeRuntimeTicks_RespectsPerFrameCap()
    {
        float accumulator = 0f;

        int ticks = ParticlePreviewTiming.TakeRuntimeTicks(1f, ref accumulator, 0.033f, maxTicks: 8);

        Assert.Equal(8, ticks);
        Assert.True(accumulator > 0.7f);
    }

    [Fact]
    public void TakeEmitterBursts_UsesRateAndKeepsFractionalRemainder()
    {
        float accumulator = 0f;

        int first = ParticlePreviewTiming.TakeEmitterBursts(0.25f, 3f, ref accumulator);
        int second = ParticlePreviewTiming.TakeEmitterBursts(0.25f, 3f, ref accumulator);

        Assert.Equal(0, first);
        Assert.Equal(1, second);
        Assert.Equal(0.5f, accumulator, precision: 5);
    }
}
