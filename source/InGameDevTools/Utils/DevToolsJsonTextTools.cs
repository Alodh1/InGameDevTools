using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InGameDevTools.Utils;

/// <summary>
/// Shared toolbar for the raw JSON text editors: undo/redo (when a history is supplied),
/// pretty-print formatting, and copy to clipboard.
/// </summary>
internal static class DevToolsJsonTextTools
{
    /// <summary>Pretty-prints JSON with the standard indented style. Comments are normalized away.</summary>
    public static bool TryFormat(string text, out string formatted, out string error)
    {
        formatted = text;
        if (!DevToolsJson.TryParseToken(text, out JToken? token, out error) || token == null)
        {
            return false;
        }

        // JsonConvert.SerializeObject(object, Formatting) binds identically in the game's
        // Newtonsoft build and the standard assembly the test host loads; JToken writer overloads
        // do not, so avoid them here.
        formatted = JsonConvert.SerializeObject(token, Formatting.Indented);
        return true;
    }

    /// <summary>
    /// Draws the toolbar row. Returns true when <paramref name="text"/> was replaced (undo, redo,
    /// or format) so the caller can revalidate and persist its draft. <paramref name="status"/> is
    /// non-empty when there is something worth surfacing in the editor status line.
    /// </summary>
    public static bool DrawEditToolbar(string id, ref string text, DevToolsTextHistory? history, out string status)
    {
        status = "";
        bool changed = false;

        if (history != null)
        {
            bool undoDisabled = !history.CanUndo;
            if (undoDisabled) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"Undo##{id}-undo") && history.TryUndo(out string undone))
            {
                text = undone;
                changed = true;
                status = "Undid last text edit.";
            }
            if (undoDisabled) ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Undo the last text edit step.");

            ImGui.SameLine();
            bool redoDisabled = !history.CanRedo;
            if (redoDisabled) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"Redo##{id}-redo") && history.TryRedo(out string redone))
            {
                text = redone;
                changed = true;
                status = "Redid text edit.";
            }
            if (redoDisabled) ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Redo the next text edit step.");

            ImGui.SameLine();
        }

        if (ImGui.SmallButton($"Format##{id}-format"))
        {
            if (TryFormat(text, out string formatted, out string error))
            {
                if (!string.Equals(formatted, text, StringComparison.Ordinal))
                {
                    text = formatted;
                    changed = true;
                    status = "Formatted JSON.";
                }
                else
                {
                    status = "JSON already formatted.";
                }
            }
            else
            {
                status = $"Cannot format: {error}";
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pretty-print the JSON (normalizes whitespace; strips comments).");

        ImGui.SameLine();
        if (ImGui.SmallButton($"Copy##{id}-copy"))
        {
            ImGui.SetClipboardText(text);
            status = $"Copied {text.Length:N0} character(s) to clipboard.";
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy the whole JSON text to the clipboard.");

        return changed;
    }
}
