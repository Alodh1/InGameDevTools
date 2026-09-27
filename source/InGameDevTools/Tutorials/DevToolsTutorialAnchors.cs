using ImGuiNET;
using NVector2 = System.Numerics.Vector2;

namespace InGameDevTools.Tutorials;

/// <summary>
/// Render-thread-only sink that lets any editor register the on-screen rectangle of a UI element
/// for the guided tutorial overlay. ImGui is immediate-mode, so a widget's screen rect is only known
/// right after it is drawn; the tutorial captures those rects during the frame and replays a spotlight
/// over the current step's target after the main window has finished drawing.
///
/// Capture is armed only while a tutorial is running, so <see cref="Mark"/> is a cheap no-op otherwise.
/// A single static instance is fine: the editor runs entirely on the client render thread and there is
/// only ever one <c>DebugWindowManager</c>. Keeping it static lets cross-class editors
/// (e.g. the particle and recipe managers) register anchors without a back-reference.
/// </summary>
internal static class DevToolsTutorialAnchors
{
    private static bool _capturing;
    private static readonly Dictionary<string, (NVector2 Min, NVector2 Max)> Rects = new(StringComparer.Ordinal);

    /// <summary>Clears last frame's rects and arms capture for this frame when a tutorial is active.</summary>
    public static void BeginFrame(bool capturing)
    {
        _capturing = capturing;
        if (capturing)
        {
            Rects.Clear();
        }
    }

    /// <summary>Records the rect of the most recently drawn ImGui item. Call right after the widget,
    /// or after a panel's <c>EndChild()</c> (the child counts as the last item, so its rect is the panel).</summary>
    public static void Mark(string id)
    {
        if (!_capturing)
        {
            return;
        }

        Rects[id] = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
    }

    /// <summary>Records an explicit rect, for panels laid out without a child wrapper where the
    /// geometry is already known (cursor pos + computed panel widths).</summary>
    public static void MarkRect(string id, NVector2 min, NVector2 max)
    {
        if (!_capturing)
        {
            return;
        }

        Rects[id] = (min, max);
    }

    public static bool TryGet(string id, out NVector2 min, out NVector2 max)
    {
        if (_capturing && Rects.TryGetValue(id, out (NVector2 Min, NVector2 Max) rect) && rect.Max.X > rect.Min.X && rect.Max.Y > rect.Min.Y)
        {
            min = rect.Min;
            max = rect.Max;
            return true;
        }

        min = default;
        max = default;
        return false;
    }
}
