#if DEBUG
namespace CombatOverhaul.Animations.EditorUI;

internal enum EditorUiMode
{
    ProperUi,
    ImGui
}

internal enum DevToolsTab
{
    Animations,
    Transforms,
    Particles,
    Colliders,
    Debug,
    GenericDisplay
}

internal enum EditorCameraMode
{
    FirstPerson,
    Orbit,
    Detached
}

internal sealed class EditorAppState
{
    public DevToolsTab SelectedTab { get; set; } = DevToolsTab.Animations;
    public string SelectedAnimation { get; set; } = "";
    public int SelectedKeyframe { get; set; }
    public string SelectedRigPart { get; set; } = "";
    public string SelectedTool { get; set; } = "Move";
    public EditorCameraMode CameraMode { get; set; } = EditorCameraMode.Orbit;
    public string StatusText { get; private set; } = "Proper UI shell active. Use ImGui fallback for full tools while panels are ported.";

    public void SetStatus(string message)
    {
        StatusText = string.IsNullOrWhiteSpace(message) ? "Ready." : message;
    }
}
#endif
