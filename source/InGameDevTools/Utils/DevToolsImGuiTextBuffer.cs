namespace InGameDevTools.Utils;

/// <summary>
/// Sizes ImGui text buffers close to their current content instead of reserving multi-megabyte
/// native buffers on every draw. The headroom lets typing continue; capacity grows again on the
/// next frame when the text approaches the current limit.
/// </summary>
internal static class DevToolsImGuiTextBuffer
{
    public static uint Capacity(string? text, int minimum = 16 * 1024, int headroom = 64 * 1024, int growthLimit = 4 * 1024 * 1024)
    {
        if (minimum < 1) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (headroom < 1) throw new ArgumentOutOfRangeException(nameof(headroom));
        if (growthLimit < minimum) throw new ArgumentOutOfRangeException(nameof(growthLimit));

        long currentLength = text?.Length ?? 0;
        long requiredWithHeadroom = currentLength + headroom + 1;
        long preferredCapacity = Math.Min(Math.Max(minimum, requiredWithHeadroom), growthLimit);
        // Existing text may exceed the preferred idle limit. Always retain headroom in that case
        // so typing never stalls at currentLength + 1.
        return checked((uint)Math.Max(requiredWithHeadroom, preferredCapacity));
    }
}
