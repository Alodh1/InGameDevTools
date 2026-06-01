using ImGuiNET;
using System.Numerics;

namespace InGameDevTools.Utils;

internal enum DevToolsDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

internal sealed class DevToolsEditorDiagnostics(string editorName)
{
    private string _summary = "";
    private string _details = "";
    private DateTime _updatedAt;
    private DevToolsDiagnosticSeverity _severity = DevToolsDiagnosticSeverity.Info;

    public bool HasMessage => !string.IsNullOrWhiteSpace(_summary);

    public void Info(string summary, string details = "")
    {
        Set(DevToolsDiagnosticSeverity.Info, summary, details);
    }

    public void Warning(string summary, string details = "")
    {
        Set(DevToolsDiagnosticSeverity.Warning, summary, details);
    }

    public void Error(string summary, string details = "")
    {
        Set(DevToolsDiagnosticSeverity.Error, summary, details);
    }

    public void Exception(string summary, Exception exception)
    {
        Set(DevToolsDiagnosticSeverity.Error, $"{summary}: {exception.Message}", exception.ToString());
    }

    public void Clear()
    {
        _summary = "";
        _details = "";
        _severity = DevToolsDiagnosticSeverity.Info;
        _updatedAt = default;
    }

    public void Draw(string id, bool showDetails)
    {
        if (!HasMessage) return;

        ImGui.SeparatorText("Diagnostics");
        ImGui.TextColored(GetColor(), $"{editorName}: {_summary}");
        ImGui.TextDisabled(_updatedAt == default
            ? "No timestamp"
            : $"Last update: {_updatedAt:HH:mm:ss}");

        if (!string.IsNullOrWhiteSpace(_details))
        {
            if (showDetails)
            {
                ImGui.TextWrapped(_details);
            }
            else
            {
                ImGui.TextDisabled("Enable Diagnostics in the top toolbar for details.");
            }
        }

        if (ImGui.SmallButton($"Clear diagnostics##{id}"))
        {
            Clear();
        }
    }

    private void Set(DevToolsDiagnosticSeverity severity, string summary, string details)
    {
        _severity = severity;
        _summary = summary;
        _details = details;
        _updatedAt = DateTime.Now;
    }

    private Vector4 GetColor()
    {
        return _severity switch
        {
            DevToolsDiagnosticSeverity.Error => new Vector4(1f, 0.42f, 0.34f, 1f),
            DevToolsDiagnosticSeverity.Warning => new Vector4(1f, 0.76f, 0.32f, 1f),
            _ => new Vector4(0.58f, 0.82f, 1f, 1f)
        };
    }
}
