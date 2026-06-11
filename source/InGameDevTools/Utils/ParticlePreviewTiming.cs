namespace InGameDevTools.Utils;

internal static class ParticlePreviewTiming
{
    public static float ScaleDelta(float deltaSeconds, float timeScale, bool paused)
    {
        if (paused || !float.IsFinite(deltaSeconds) || deltaSeconds <= 0f)
        {
            return 0f;
        }

        float scale = !float.IsFinite(timeScale) ? 1f : Math.Clamp(timeScale, 0f, 16f);
        return Math.Clamp(deltaSeconds, 0f, 0.1f) * scale;
    }

    public static int TakeRuntimeTicks(float deltaSeconds, ref float accumulator, float intervalSeconds, int maxTicks)
    {
        if (deltaSeconds <= 0f || intervalSeconds <= 0f || maxTicks <= 0)
        {
            return 0;
        }

        accumulator += deltaSeconds;
        int ticks = Math.Min((int)(accumulator / intervalSeconds), maxTicks);
        accumulator -= ticks * intervalSeconds;
        if (Math.Abs(accumulator) <= 0.00001f)
        {
            accumulator = 0f;
        }

        return ticks;
    }

    public static int TakeEmitterBursts(float deltaSeconds, float ratePerSecond, ref float accumulator, int maxBursts = 64)
    {
        if (deltaSeconds <= 0f || ratePerSecond <= 0f || maxBursts <= 0)
        {
            return 0;
        }

        accumulator += deltaSeconds * ratePerSecond;
        int bursts = Math.Min((int)accumulator, maxBursts);
        accumulator -= bursts;
        if (Math.Abs(accumulator) <= 0.00001f)
        {
            accumulator = 0f;
        }

        return bursts;
    }
}
