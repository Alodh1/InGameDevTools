#if DEBUG
using System.Text;
using CombatOverhaul.Animations.EditorUI;
using CombatOverhaul.Integration.Transpilers;

namespace CombatOverhaul.Animations;

public sealed partial class DebugWindowManager
{
    internal void UpdateProperDevTools(float deltaSeconds)
    {
        if (CurrentEditorUiMode != EditorUiMode.ProperUi || _devToolsDialog?.IsOpened() != true) return;

        string animationCode = EnsureProperAnimationSelection(_editorAppState);
        if (!TryGetProperAnimation(animationCode, out Animation? animation))
        {
            SetEditorFrameOverride(null);
            return;
        }

        EnsureEditorPlaybackState(animationCode, animation);
        if (_editorPlaybackPlaying && !_editorPlaybackPaused && _editorPlaybackAnimationCode == animationCode)
        {
            AdvanceEditorPlayback(animation, deltaSeconds);
        }

        SetEditorFrameOverride(animation.StillPlayerFrame(animation._playerFrameIndex, animation._frameProgress));
        _editorAppState.SelectedKeyframe = animation.PlayerKeyFrames.Count == 0 ? 0 : Math.Clamp(animation._playerFrameIndex, 0, animation.PlayerKeyFrames.Count - 1);
    }

    internal IReadOnlyList<string> ProperAnimationCodes => AnimationsManager._instance?.Animations?.Keys.ToArray() ?? Array.Empty<string>();

    internal string EnsureProperAnimationSelection(EditorAppState state)
    {
        string[] codes = ProperAnimationCodes.ToArray();
        if (codes.Length == 0)
        {
            state.SelectedAnimation = "";
            state.SelectedKeyframe = 0;
            return "";
        }

        if (string.IsNullOrWhiteSpace(state.SelectedAnimation) || !AnimationsManager._instance.Animations.ContainsKey(state.SelectedAnimation))
        {
            int selected = Math.Clamp(_selectedAnimationIndex, 0, codes.Length - 1);
            state.SelectedAnimation = codes[selected];
        }

        _selectedAnimationIndex = Math.Max(0, Array.IndexOf(codes, state.SelectedAnimation));
        Animation animation = AnimationsManager._instance.Animations[state.SelectedAnimation];
        state.SelectedKeyframe = animation.PlayerKeyFrames.Count == 0 ? 0 : Math.Clamp(animation._playerFrameIndex, 0, animation.PlayerKeyFrames.Count - 1);
        return state.SelectedAnimation;
    }

    internal IReadOnlyList<string> GetProperVisibleAnimationCodes(EditorAppState state, int radius = 5)
    {
        string selected = EnsureProperAnimationSelection(state);
        string[] codes = ProperAnimationCodes.ToArray();
        if (codes.Length == 0) return Array.Empty<string>();

        int selectedIndex = Math.Max(0, Array.IndexOf(codes, selected));
        int start = Math.Max(0, selectedIndex - radius);
        int end = Math.Min(codes.Length - 1, selectedIndex + radius);
        return codes.Skip(start).Take(end - start + 1).ToArray();
    }

    internal bool SelectProperAnimation(EditorAppState state, string animationCode)
    {
        if (!TryGetProperAnimation(animationCode, out Animation? animation)) return false;

        state.SelectedAnimation = animationCode;
        _selectedAnimationIndex = Math.Max(0, Array.IndexOf(ProperAnimationCodes.ToArray(), animationCode));
        EnsureEditorPlaybackState(animationCode, animation);
        SetEditorFrameOverride(animation.StillPlayerFrame(animation._playerFrameIndex, animation._frameProgress));
        state.SetStatus($"Selected animation {animationCode}.");
        return true;
    }

    internal bool SelectProperAnimationOffset(EditorAppState state, int offset)
    {
        string[] codes = ProperAnimationCodes.ToArray();
        if (codes.Length == 0) return false;

        string selected = EnsureProperAnimationSelection(state);
        int selectedIndex = Math.Max(0, Array.IndexOf(codes, selected));
        int nextIndex = Math.Clamp(selectedIndex + offset, 0, codes.Length - 1);
        return SelectProperAnimation(state, codes[nextIndex]);
    }

    internal string GetProperAnimationBrowserText(EditorAppState state)
    {
        string[] codes = ProperAnimationCodes.ToArray();
        if (codes.Length == 0) return "No animations loaded.";

        string selected = EnsureProperAnimationSelection(state);
        int selectedIndex = Math.Max(0, Array.IndexOf(codes, selected));
        return $"{codes.Length} animations loaded\nSelected {selectedIndex + 1} / {codes.Length}\n\nUse the nearby list below, or Prev/Next, while text filtering is ported.";
    }

    internal string GetProperAnimationPropertiesText(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return "No animation selected.";

        double currentMs = GetEditorFrameTimeMs(animation);
        double totalMs = GetEditorAnimationDurationMs(animation);
        int errors = 0;
        int warnings = 0;
        foreach (AnimationValidationMessage message in BuildAnimationValidationMessages(animation))
        {
            if (message.Severity == AnimationValidationSeverity.Error) errors++;
            else warnings++;
        }

        return $"Animation: {selected}\n{GetAnimationSourceText(selected)}\nPlayer keyframes: {animation.PlayerKeyFrames.Count}\nItem keyframes: {animation.ItemKeyFrames.Count}\nSounds: {animation.SoundFrames.Count}\nParticles: {animation.ParticlesFrames.Count}\nCallbacks: {animation.CallbackFrames.Count}\n\nCurrent keyframe: {animation._playerFrameIndex}\nFrame progress: {animation._frameProgress:0.000}\nTime: {currentMs:0} / {totalMs:0} ms\nSpeed: {_animationSpeed:0.00}x\n\nValidation: {errors} errors, {warnings} warnings\nUndo: {_animationHistory.UndoCount(selected)}  Redo: {_animationHistory.RedoCount(selected)}";
    }

    internal string GetProperViewportText(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return "No animation selected.";

        string playback = _editorPlaybackPlaying ? (_editorPlaybackPaused ? "paused" : "playing") : "stopped";
        return $"Previewing {selected}\nPlayback: {playback}\nCamera: {state.CameraMode}\n\nThe world viewport still renders behind this panel. Gizmo-driven rig editing remains available in ImGui fallback until the proper rig controls are ported.";
    }

    internal string GetProperTimelineText(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return "No animation selected.";
        if (animation.PlayerKeyFrames.Count == 0) return "No player keyframes.";

        StringBuilder builder = new();
        builder.AppendLine($"Time: {GetEditorFrameTimeMs(animation):0} ms / {GetEditorAnimationDurationMs(animation):0} ms");
        builder.AppendLine($"Loop: {_editorPlaybackLoopStartKeyframe} -> {_editorPlaybackLoopEndKeyframe}");
        builder.AppendLine();

        int start = Math.Max(0, animation._playerFrameIndex - 4);
        int end = Math.Min(animation.PlayerKeyFrames.Count - 1, animation._playerFrameIndex + 4);
        for (int index = start; index <= end; index++)
        {
            string marker = index == animation._playerFrameIndex ? ">" : " ";
            builder.AppendLine($"{marker} {index,3}: {animation.PlayerKeyFrames[index].Time.TotalMilliseconds,6:0} ms");
        }

        return builder.ToString().TrimEnd();
    }

    internal string GetProperValidationText(EditorAppState state, int maxMessages = 6)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return "No animation selected.";

        List<AnimationValidationMessage> messages = BuildAnimationValidationMessages(animation);
        if (messages.Count == 0) return "Validation OK.";

        return string.Join(Environment.NewLine, messages.Take(maxMessages).Select(message => $"{message.Severity}: {message.Text}"))
            + (messages.Count > maxMessages ? $"\n... {messages.Count - maxMessages} more" : "");
    }

    internal bool ProperUndo(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (string.IsNullOrWhiteSpace(selected)) return false;

        CommitPendingAnimationEdit(selected);
        _animationHistory.Undo(selected, AnimationsManager._instance.Animations, out string status);
        state.SetStatus(status);
        return true;
    }

    internal bool ProperRedo(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (string.IsNullOrWhiteSpace(selected)) return false;

        CommitPendingAnimationEdit(selected);
        _animationHistory.Redo(selected, AnimationsManager._instance.Animations, out string status);
        state.SetStatus(status);
        return true;
    }

    internal bool ProperClearHistory(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (string.IsNullOrWhiteSpace(selected)) return false;

        _animationHistory.Clear(selected);
        state.SetStatus("Animation edit history cleared.");
        return true;
    }

    internal bool ProperSaveAnimationToSource(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return false;

        QueueSourceSave(TrySaveAnimationToSource(selected, animation), status =>
        {
            _transformSaveStatus = status;
            state.SetStatus(status);
        });

        if (_pendingSourceSaveRequest != null)
        {
            state.SetStatus("Save preview opened in ImGui fallback.");
            SwitchToImGuiDevTools();
        }

        return true;
    }

    internal bool ProperExportAnimationToClipboard(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return false;

        _api.Forms.SetClipboardText(AnimationJson.FromAnimation(animation).ToString());
        state.SetStatus($"Exported {selected} to clipboard.");
        return true;
    }

    internal bool ProperSaveAnimationBuffer(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return false;

        _animationBuffer = AnimationJson.FromAnimation(animation);
        state.SetStatus($"Saved {selected} to animation buffer.");
        return true;
    }

    internal bool ProperLoadAnimationBuffer(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (string.IsNullOrWhiteSpace(selected) || _animationBuffer == null) return false;

        Animation currentAnimation = AnimationsManager._instance.Animations[selected];
        _animationHistory.BeginEdit(selected, currentAnimation, "Load from buffer");
        AnimationsManager._instance.Animations[selected] = _animationBuffer.ToAnimation();
        _animationHistory.CommitEdit(selected, AnimationsManager._instance.Animations[selected]);
        state.SetStatus($"Loaded animation buffer into {selected}.");
        return true;
    }

    internal bool ProperPlay(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return false;

        StartEditorPlayback(selected, animation);
        state.SetStatus($"Playing {selected}.");
        return true;
    }

    internal bool ProperTogglePause(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return false;

        EnsureEditorPlaybackState(selected, animation);
        if (!_editorPlaybackPlaying)
        {
            StartEditorPlayback(selected, animation);
            _editorPlaybackPaused = true;
        }
        else
        {
            _editorPlaybackPaused = !_editorPlaybackPaused;
        }

        state.SetStatus(_editorPlaybackPaused ? "Playback paused." : "Playback resumed.");
        return true;
    }

    internal bool ProperStepKeyframe(EditorAppState state, int direction)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return false;

        StepEditorKeyframe(animation, direction);
        state.SelectedKeyframe = animation._playerFrameIndex;
        state.SetStatus($"Selected keyframe {animation._playerFrameIndex}.");
        return true;
    }

    internal bool ProperStepFrame(EditorAppState state, int direction)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return false;

        StepEditorFrame(animation, direction);
        state.SelectedKeyframe = animation._playerFrameIndex;
        state.SetStatus($"Scrubbed to {GetEditorFrameTimeMs(animation):0} ms.");
        return true;
    }

    internal bool ProperScrubFraction(EditorAppState state, double fraction)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return false;

        double duration = GetEditorAnimationDurationMs(animation);
        ScrubEditorTimeline(animation, duration * Math.Clamp(fraction, 0, 1));
        state.SelectedKeyframe = animation._playerFrameIndex;
        state.SetStatus($"Scrubbed to {GetEditorFrameTimeMs(animation):0} ms.");
        return true;
    }

    internal bool ProperSetLoopStart(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return false;

        _editorPlaybackLoopStartKeyframe = animation._playerFrameIndex;
        ClampEditorPlaybackRange(animation);
        state.SetStatus($"Loop start set to keyframe {_editorPlaybackLoopStartKeyframe}.");
        return true;
    }

    internal bool ProperSetLoopEnd(EditorAppState state)
    {
        string selected = EnsureProperAnimationSelection(state);
        if (!TryGetProperAnimation(selected, out Animation? animation)) return false;

        _editorPlaybackLoopEndKeyframe = animation._playerFrameIndex;
        ClampEditorPlaybackRange(animation);
        state.SetStatus($"Loop end set to keyframe {_editorPlaybackLoopEndKeyframe}.");
        return true;
    }

    internal bool ProperAdjustPlaybackSpeed(EditorAppState state, float delta)
    {
        _animationSpeed = Math.Clamp(_animationSpeed + delta, 0.1f, 2f);
        state.SetStatus($"Playback speed set to {_animationSpeed:0.00}x.");
        return true;
    }

    internal bool ProperToggleThirdPersonAnimations(EditorAppState state)
    {
        PlayAnimationsInThirdPerson = !PlayAnimationsInThirdPerson;
        state.SetStatus($"Third person animation preview {(PlayAnimationsInThirdPerson ? "enabled" : "disabled")}.");
        return true;
    }

    internal bool ProperToggleRenderingOffset(EditorAppState state)
    {
        PlayerRenderingPatches.FpHandsOffset = PlayerRenderingPatches.FpHandsOffset != PlayerRenderingPatches.DefaultFpHandsOffset
            ? PlayerRenderingPatches.DefaultFpHandsOffset
            : 0;
        state.SetStatus("Toggled first-person rendering offset.");
        return true;
    }

    internal bool ProperSetCameraMode(EditorAppState state, EditorCameraMode mode)
    {
        state.CameraMode = mode;
        if (_detachedEditorCamera != null)
        {
            RigEditorCameraMode cameraMode = mode switch
            {
                EditorCameraMode.Detached => RigEditorCameraMode.Detached,
                EditorCameraMode.Orbit => RigEditorCameraMode.Orbiting,
                _ => RigEditorCameraMode.FirstPerson
            };
            _detachedEditorCamera.SetEditorMode(cameraMode);
        }
        state.SetStatus($"Camera mode set to {mode}.");
        return true;
    }

    private bool TryGetProperAnimation(string animationCode, out Animation? animation)
    {
        animation = null;
        if (string.IsNullOrWhiteSpace(animationCode)) return false;
        return AnimationsManager._instance?.Animations?.TryGetValue(animationCode, out animation) == true && animation != null;
    }
}
#endif
