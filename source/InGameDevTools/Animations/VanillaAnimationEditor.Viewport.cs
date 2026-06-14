using System.Diagnostics;
using System.Text;
using InGameDevTools.Utils;
using InGameDevTools.Integration.Transpilers;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VanillaAnimation = Vintagestory.API.Common.Animation;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private readonly record struct VanillaCutPreview(
        string ElementName,
        int FaceAxis,
        bool FacePositive,
        int CutAxis,
        double CutCoordinate,
        NVector2[] PlaneCorners,
        NVector2 LineStart,
        NVector2 LineEnd);

    private static readonly JsonSerializerSettings VanillaShapeElementJsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore
    };

    private void DrawVanillaCenterPanel(VanillaBrowserRow? row, float deltaSeconds)
    {
        if (row == null)
        {
            ImGui.TextDisabled(_vanillaIndex.HasSelectedEntity ? "Select a vanilla animation." : "Select an entity first.");
            return;
        }

        VanillaAnimation? animation = GetVanillaAnimation(row);
        if (animation == null)
        {
            ImGui.TextWrapped("No linked shape animation is available for this metadata entry.");
            return;
        }

        VanillaAnimationPreviewScene? scene = _vanillaPreviewScene?.Key == row.Key ? _vanillaPreviewScene : null;
        if (scene == null)
        {
            ImGui.TextWrapped(row.Label);
            ImGui.TextWrapped("Preview is not loaded. Loading a preview prepares the selected shape and uploads its mesh, so it runs only when requested.");
            if (ImGui.Button("Load preview##vanilla-preview", new NVector2(-1, 0)))
            {
                BuildVanillaPreviewScene(row, rebuildMesh: true);
            }

            if (!string.IsNullOrWhiteSpace(_vanillaStatus))
            {
                ImGui.TextWrapped(_vanillaStatus);
            }
            return;
        }

        ImGui.TextWrapped(row.Label);
        if (!string.IsNullOrWhiteSpace(scene.Status))
        {
            ImGui.TextWrapped(scene.Status);
        }

        DrawVanillaPlaybackControls(row, scene, deltaSeconds);

        NVector2 centerAvailable = ImGui.GetContentRegionAvail();
        if (_vanillaViewportPoppedOut)
        {
            ImGui.Separator();
            ImGui.TextWrapped("Viewport is popped out into a separate resizable window.");
            if (ImGui.Button("Return viewport here##vanilla-preview-pop-in", new NVector2(-1, 0)))
            {
                _vanillaViewportPoppedOut = false;
            }
        }
        else
        {
            DrawVanillaViewport(row, scene, new NVector2(centerAvailable.X, Math.Max(_vanillaViewportMinHeight, centerAvailable.Y)));
        }
    }

    private void DrawVanillaPlaybackControls(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, float deltaSeconds)
    {
        scene.SetPreviewMode(row, GetVanillaEffectivePreviewMode(scene));

        if (scene.Playing)
        {
            scene.Tick(deltaSeconds * Math.Clamp(_vanillaPlaybackSpeed, 0.05f, 4f));
            ApplyVanillaLoop(scene);
        }

        if (ImGui.Button("Play##vanilla-playback"))
        {
            scene.Play();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        if (ImGui.SliderFloat("Speed##vanilla-playback-speed", ref _vanillaPlaybackSpeed, 0.1f, 4f, "%.2fx"))
        {
            _vanillaPlaybackSpeed = Math.Clamp(_vanillaPlaybackSpeed, 0.05f, 4f);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Preview playback speed multiplier. Does not change the animation data.");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("1x##vanilla-playback-speed-reset"))
        {
            _vanillaPlaybackSpeed = 1f;
        }

        ImGui.SameLine();
        if (ImGui.Button(scene.Playing ? "Pause##vanilla-playback" : "Resume##vanilla-playback"))
        {
            if (scene.Playing)
            {
                scene.Playing = false;
            }
            else
            {
                scene.Play();
            }
        }

        if (ImGui.Button("Key <##vanilla-playback"))
        {
            StepVanillaKeyframe(row, -1);
        }

        ImGui.SameLine();
        if (ImGui.Button("Key >##vanilla-playback"))
        {
            StepVanillaKeyframe(row, 1);
        }

        ImGui.SameLine();
        if (ImGui.Button("Frame <##vanilla-playback"))
        {
            ScrubVanillaPreview(scene, Math.Max(0, scene.CurrentFrame - 1));
        }

        ImGui.SameLine();
        if (ImGui.Button("Frame >##vanilla-playback"))
        {
            ScrubVanillaPreview(scene, Math.Min(Math.Max(0, scene.QuantityFrames - 1), scene.CurrentFrame + 1));
        }

        int maxFrame = Math.Max(0, scene.QuantityFrames - 1);
        int loopStart = Math.Clamp(_vanillaSelection.LoopStartFrame, 0, maxFrame);
        int loopEnd = Math.Clamp(_vanillaSelection.LoopEndFrame, 0, maxFrame);
        if (loopEnd < loopStart) loopEnd = loopStart;

        ImGui.SetNextItemWidth(Math.Min(180f, Math.Max(120f, ImGui.GetContentRegionAvail().X)));
        if (ImGui.SliderInt("Loop start frame##vanilla-playback", ref loopStart, 0, maxFrame))
        {
            _vanillaSelection.LoopStartFrame = Math.Min(loopStart, _vanillaSelection.LoopEndFrame);
        }

        ImGui.SetNextItemWidth(Math.Min(180f, Math.Max(120f, ImGui.GetContentRegionAvail().X)));
        if (ImGui.SliderInt("Loop end frame##vanilla-playback", ref loopEnd, 0, maxFrame))
        {
            _vanillaSelection.LoopEndFrame = Math.Max(loopEnd, _vanillaSelection.LoopStartFrame);
        }

        int frame = (int)Math.Clamp(scene.CurrentFrame, 0, maxFrame);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("Frame##vanilla-playback", ref frame, 0, maxFrame))
        {
            ScrubVanillaPreview(scene, frame);
        }

        bool worldLighting = _vanillaViewportWorldLighting;
        if (ImGui.Checkbox("World lighting##vanilla-preview-lighting", ref worldLighting))
        {
            _vanillaViewportWorldLighting = worldLighting;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Use current world light and fog instead of stable editor lighting.");
        }

        bool onionSkins = _vanillaOnionSkinEnabled;
        ImGui.SameLine();
        if (ImGui.Checkbox("Onion skins##vanilla-preview-onion", ref onionSkins))
        {
            _vanillaOnionSkinEnabled = onionSkins;
            _vanillaStatus = _vanillaOnionSkinEnabled
                ? "Viewport onion skins enabled."
                : "Viewport onion skins disabled.";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Shows neighboring vanilla shape keyframes as translucent ghosts in the preview viewport.");
        }

        if (_vanillaOnionSkinEnabled)
        {
            ImGui.SameLine();
            ImGui.Checkbox("Prev##vanilla-preview-onion-prev", ref _vanillaOnionSkinPrevious);
            ImGui.SameLine();
            ImGui.Checkbox("Next##vanilla-preview-onion-next", ref _vanillaOnionSkinNext);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(92);
            if (ImGui.SliderFloat("Opacity##vanilla-preview-onion-opacity", ref _vanillaOnionSkinOpacity, 0.05f, 0.6f, "%.2f"))
            {
                _vanillaOnionSkinOpacity = Math.Clamp(_vanillaOnionSkinOpacity, 0.05f, 0.6f);
            }
        }

        ImGui.SameLine();
        bool motionTrail = _vanillaMotionTrailEnabled;
        if (ImGui.Checkbox("Motion trail##vanilla-preview-trail", ref motionTrail))
        {
            _vanillaMotionTrailEnabled = motionTrail;
            _vanillaMotionTrailCacheKey = "";
            _vanillaStatus = _vanillaMotionTrailEnabled
                ? "Motion trail enabled for the selected element."
                : "Motion trail disabled.";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Draws the selected element's pivot path across the whole animation in the Orbit viewport: keyframes are marked yellow, the playhead green.");
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("Mode:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Orbit##vanilla-preview-mode", _vanillaViewportMode == VanillaPreviewMode.Orbit))
        {
            SetVanillaViewportMode(VanillaPreviewMode.Orbit);
        }

        ImGui.SameLine();
        bool firstPersonAvailable = scene.ClassicFirstPersonAvailable;
        if (!firstPersonAvailable) ImGui.BeginDisabled();
        if (ImGui.RadioButton("First person##vanilla-preview-mode", _vanillaViewportMode == VanillaPreviewMode.FirstPerson))
        {
            SetVanillaViewportMode(VanillaPreviewMode.FirstPerson);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(firstPersonAvailable
                ? "Classic Vintage Story first person: arms/hands mesh, first-person FOV, and -fp animation variants."
                : "First-person preview is only available for player-style meshes with arm joints.");
        }
        if (!firstPersonAvailable) ImGui.EndDisabled();

        ImGui.SameLine();
        bool immersiveFirstPersonAvailable = scene.ImmersiveFirstPersonAvailable;
        if (!immersiveFirstPersonAvailable) ImGui.BeginDisabled();
        if (ImGui.RadioButton("Immersive FP##vanilla-preview-mode", _vanillaViewportMode == VanillaPreviewMode.ImmersiveFirstPerson))
        {
            SetVanillaViewportMode(VanillaPreviewMode.ImmersiveFirstPerson);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(immersiveFirstPersonAvailable
                ? "Opt-in immersive first person: body mesh with the neck/head subtree hidden and -ifp animation variants."
                : "Immersive first-person preview is only available for player-style meshes.");
        }
        if (!immersiveFirstPersonAvailable) ImGui.EndDisabled();

        if (_vanillaViewportMode == VanillaPreviewMode.FirstPerson || _vanillaViewportMode == VanillaPreviewMode.ImmersiveFirstPerson)
        {
            DrawVanillaFirstPersonPreviewControls(scene);
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset view##vanilla-preview-camera-reset"))
        {
            _vanillaViewportYaw = 0;
            _vanillaViewportPitch = 0;
            _vanillaViewportZoom = 1f;
            _vanillaViewportPanX = 0;
            _vanillaViewportPanY = 0;
            _vanillaFirstPersonLookPitchDegrees = 0;
        }

        ImGui.SameLine();
        if (ImGui.Button("Screenshot##vanilla-preview-screenshot"))
        {
            _vanillaViewportScreenshotRequested = true;
            _vanillaStatus = "Viewport screenshot queued.";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Save the current animation viewport texture as a PNG.");
        }

        ImGui.SameLine();
        bool verbosePreviewLogs = _vanillaVerbosePreviewLogs;
        if (ImGui.Checkbox("Verbose preview logs##vanilla-preview-verbose", ref verbosePreviewLogs))
        {
            _vanillaVerbosePreviewLogs = verbosePreviewLogs;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Write vanilla preview framebuffer, shader, mesh, texture, and skip diagnostics to verbose debug logs.");
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("Pop out viewport##vanilla-preview-popout", ref _vanillaViewportPoppedOut) && !_vanillaViewportPoppedOut)
        {
            ClearVanillaViewportGizmoDrag();
        }
    }

    private void DrawVanillaFirstPersonPreviewControls(VanillaAnimationPreviewScene scene)
    {
        ImGui.NewLine();
        ImGui.SetNextItemWidth(132);
        if (ImGui.SliderFloat("Look pitch##vanilla-fp-look-pitch", ref _vanillaFirstPersonLookPitchDegrees, -89f, 89f, "%.0f deg"))
        {
            _vanillaFirstPersonLookPitchDegrees = Math.Clamp(_vanillaFirstPersonLookPitchDegrees, -89f, 89f);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Game-accurate first-person pitch follow around the local eye position.");
        }

        ImGui.SameLine();
        ImGui.Checkbox("Inspect camera##vanilla-fp-inspect-camera", ref _vanillaFirstPersonInspectCamera);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Temporarily use orbit-style camera controls to inspect the first-person mesh. Off is the game-accurate view.");
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("Right hand##vanilla-fp-right-item", "item/block code", ref _vanillaFirstPersonRightHandItemCode, 256);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Reference item or block rendered through the engine hand transform path.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Use held##vanilla-fp-use-held-right"))
        {
            SetVanillaFirstPersonItemFromHeldHand(rightHand: true);
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear##vanilla-fp-clear-right"))
        {
            _vanillaFirstPersonRightHandItemCode = "";
        }

        ImGui.SameLine();
        ImGui.Checkbox("Left##vanilla-fp-left-enabled", ref _vanillaFirstPersonRenderLeftHandItem);
        if (_vanillaFirstPersonRenderLeftHandItem)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(190);
            ImGui.InputTextWithHint("Left hand##vanilla-fp-left-item", "item/block code", ref _vanillaFirstPersonLeftHandItemCode, 256);
            ImGui.SameLine();
            if (ImGui.Button("Use held##vanilla-fp-use-held-left"))
            {
                SetVanillaFirstPersonItemFromHeldHand(rightHand: false);
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear##vanilla-fp-clear-left"))
            {
                _vanillaFirstPersonLeftHandItemCode = "";
            }
        }

        if (!string.IsNullOrWhiteSpace(_vanillaFirstPersonRightHandItemCode) && !scene.HasAttachmentPoint("RightHand"))
        {
            ImGui.TextDisabled("RightHand attachment point not found on this preview shape.");
        }
        if (_vanillaFirstPersonRenderLeftHandItem && !string.IsNullOrWhiteSpace(_vanillaFirstPersonLeftHandItemCode) && !scene.HasAttachmentPoint("LeftHand"))
        {
            ImGui.TextDisabled("LeftHand attachment point not found on this preview shape.");
        }
    }

    private void SetVanillaFirstPersonItemFromHeldHand(bool rightHand)
    {
        ItemStack? stack = rightHand
            ? _api.World.Player?.Entity?.RightHandItemSlot?.Itemstack
            : _api.World.Player?.Entity?.LeftHandItemSlot?.Itemstack;
        string code = stack?.Collectible?.Code?.ToString() ?? "";
        if (rightHand)
        {
            _vanillaFirstPersonRightHandItemCode = code;
        }
        else
        {
            _vanillaFirstPersonLeftHandItemCode = code;
            _vanillaFirstPersonRenderLeftHandItem = !string.IsNullOrWhiteSpace(code);
        }

        _vanillaStatus = string.IsNullOrWhiteSpace(code)
            ? "No held item found for first-person reference."
            : $"First-person reference set to {code}.";
    }

    private void DrawVanillaPoppedOutViewport()
    {
        DrawVanillaPoppedOutViewport(FindVanillaBrowserRow(_vanillaSelection.RowKey));
    }

    private void DrawVanillaPoppedOutViewport(VanillaBrowserRow? row)
    {
        if (!_vanillaViewportPoppedOut) return;

        bool open = true;
        NVector2 displaySize = GetVanillaImGuiDisplaySize();
        _vanillaPoppedViewportWidth = Math.Clamp(_vanillaPoppedViewportWidth, 420f, Math.Max(420f, displaySize.X - 24f));
        _vanillaPoppedViewportHeight = Math.Clamp(_vanillaPoppedViewportHeight, 300f, Math.Max(300f, displaySize.Y - 36f));
        ImGui.SetNextWindowSize(new NVector2(_vanillaPoppedViewportWidth, _vanillaPoppedViewportHeight), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new NVector2(420f, 300f), new NVector2(Math.Max(420f, displaySize.X), Math.Max(300f, displaySize.Y)));
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoSavedSettings;
        if (ImGui.Begin("Animation viewport##vanilla-popped-viewport", ref open, flags))
        {
            ImGui.SetWindowFontScale(_devToolsUiScale);
            DrawVanillaPoppedViewportControls(displaySize);

            if (row == null)
            {
                ImGui.TextDisabled(_vanillaIndex.HasSelectedEntity ? "Select a vanilla animation." : "Select an entity first.");
            }
            else if (_vanillaPreviewScene?.Key != row.Key)
            {
                ImGui.TextWrapped(row.Label);
                ImGui.TextWrapped("Preview is not loaded.");
                if (ImGui.Button("Load preview##vanilla-popped-load-preview", new NVector2(-1, 0)))
                {
                    BuildVanillaPreviewScene(row, rebuildMesh: true);
                }
            }
            else
            {
                VanillaAnimationPreviewScene scene = _vanillaPreviewScene;
                NVector2 available = ImGui.GetContentRegionAvail();
                DrawVanillaViewport(row, scene, new NVector2(available.X, Math.Max(_vanillaViewportMinHeight, available.Y)));
            }

            if (!ImGui.IsAnyItemActive())
            {
                NVector2 windowSize = ImGui.GetWindowSize();
                _vanillaPoppedViewportWidth = windowSize.X;
                _vanillaPoppedViewportHeight = windowSize.Y;
            }

            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();

        if (!open)
        {
            _vanillaViewportPoppedOut = false;
            ClearVanillaViewportGizmoDrag();
        }
    }

    private NVector2 GetVanillaImGuiDisplaySize()
    {
        NVector2 displaySize = ImGui.GetIO().DisplaySize;
        if (displaySize.X <= 0 || displaySize.Y <= 0)
        {
            displaySize = new NVector2(_api.Render.FrameWidth, _api.Render.FrameHeight);
        }

        return new NVector2(Math.Max(640f, displaySize.X), Math.Max(480f, displaySize.Y));
    }

    private void DrawVanillaPoppedViewportControls(NVector2 displaySize)
    {
        const float margin = 10f;
        float toolbarHeight = 34f * _devToolsUiScale;
        NVector2 topLeft = new(margin, margin + toolbarHeight);
        NVector2 usable = new(Math.Max(420f, displaySize.X - margin * 2f), Math.Max(300f, displaySize.Y - margin * 2f - toolbarHeight));
        float halfWidth = Math.Max(420f, usable.X * 0.5f - margin * 0.5f);
        float halfHeight = Math.Max(300f, usable.Y * 0.5f - margin * 0.5f);

        if (ImGui.Button("Left half##vanilla-popout-place-left"))
        {
            SetVanillaPoppedViewportRect(topLeft.X, topLeft.Y, halfWidth, usable.Y);
        }

        ImGui.SameLine();
        if (ImGui.Button("Right half##vanilla-popout-place-right"))
        {
            SetVanillaPoppedViewportRect(topLeft.X + usable.X - halfWidth, topLeft.Y, halfWidth, usable.Y);
        }

        ImGui.SameLine();
        if (ImGui.Button("Top half##vanilla-popout-place-top"))
        {
            SetVanillaPoppedViewportRect(topLeft.X, topLeft.Y, usable.X, halfHeight);
        }

        ImGui.SameLine();
        if (ImGui.Button("Bottom half##vanilla-popout-place-bottom"))
        {
            SetVanillaPoppedViewportRect(topLeft.X, topLeft.Y + usable.Y - halfHeight, usable.X, halfHeight);
        }

        ImGui.SameLine();
        if (ImGui.Button("Fill##vanilla-popout-place-fill"))
        {
            SetVanillaPoppedViewportRect(topLeft.X, topLeft.Y, usable.X, usable.Y);
        }

        ImGui.SameLine();
        if (ImGui.Button("Center##vanilla-popout-place-center"))
        {
            float width = Math.Min(_vanillaPoppedViewportWidth, usable.X);
            float height = Math.Min(_vanillaPoppedViewportHeight, usable.Y);
            SetVanillaPoppedViewportRect(topLeft.X + (usable.X - width) * 0.5f, topLeft.Y + (usable.Y - height) * 0.5f, width, height);
        }

        ImGui.SameLine();
        if (ImGui.Button("Dock back##vanilla-popout-dock-back"))
        {
            _vanillaViewportPoppedOut = false;
            ClearVanillaViewportGizmoDrag();
        }

        float requestedWidth = _vanillaPoppedViewportWidth;
        float requestedHeight = _vanillaPoppedViewportHeight;
        ImGui.SetNextItemWidth(110);
        bool resize = ImGui.InputFloat("Width##vanilla-popout-width", ref requestedWidth, 0, 0, "%.0f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        resize |= ImGui.InputFloat("Height##vanilla-popout-height", ref requestedHeight, 0, 0, "%.0f");
        if (resize)
        {
            _vanillaPoppedViewportWidth = Math.Clamp(requestedWidth, 420f, Math.Max(420f, displaySize.X));
            _vanillaPoppedViewportHeight = Math.Clamp(requestedHeight, 300f, Math.Max(300f, displaySize.Y));
            ImGui.SetWindowSize(new NVector2(_vanillaPoppedViewportWidth, _vanillaPoppedViewportHeight), ImGuiCond.Always);
        }
    }

    private void SetVanillaPoppedViewportRect(float x, float y, float width, float height)
    {
        _vanillaPoppedViewportWidth = Math.Max(420f, width);
        _vanillaPoppedViewportHeight = Math.Max(300f, height);
        ImGui.SetWindowPos(new NVector2(Math.Max(0f, x), Math.Max(0f, y)), ImGuiCond.Always);
        ImGui.SetWindowSize(new NVector2(_vanillaPoppedViewportWidth, _vanillaPoppedViewportHeight), ImGuiCond.Always);
        ClearVanillaViewportGizmoDrag();
    }

    private void SetVanillaViewportMode(VanillaPreviewMode mode)
    {
        if (_vanillaViewportMode == mode) return;
        _vanillaViewportMode = mode;
        _vanillaViewportPanX = 0;
        _vanillaViewportPanY = 0;
        _vanillaViewportZoom = 1f;
    }

    private void ApplyVanillaLoop(VanillaAnimationPreviewScene scene)
    {
        int maxFrame = Math.Max(0, scene.QuantityFrames - 1);
        int loopStart = Math.Clamp(_vanillaSelection.LoopStartFrame, 0, maxFrame);
        int loopEnd = Math.Clamp(_vanillaSelection.LoopEndFrame, 0, maxFrame);
        if (loopEnd <= loopStart) return;
        if (scene.CurrentFrame > loopEnd)
        {
            ScrubVanillaPreview(scene, loopStart);
        }
    }

    private void StepVanillaKeyframe(VanillaBrowserRow row, int direction)
    {
        VanillaAnimation? animation = GetVanillaAnimation(row);
        if (animation?.KeyFrames == null || animation.KeyFrames.Length == 0 || _vanillaPreviewScene == null) return;

        _vanillaSelection.KeyFrameIndex = Math.Clamp(_vanillaSelection.KeyFrameIndex + direction, 0, animation.KeyFrames.Length - 1);
        ScrubVanillaPreview(_vanillaPreviewScene, animation.KeyFrames[_vanillaSelection.KeyFrameIndex].Frame);
    }

    private void ScrubVanillaPreview(VanillaAnimationPreviewScene scene, float frame)
    {
        scene.Scrub(frame);
    }

    private void DrawVanillaViewport(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, NVector2 requestedSize)
    {
        NVector2 size = new(Math.Max(420f, requestedSize.X), Math.Max(240f, requestedSize.Y));
        ImGui.InvisibleButton($"##vanilla-viewport-{scene.Key}", size);
        NVector2 min = ImGui.GetItemRectMin();
        NVector2 max = ImGui.GetItemRectMax();
        bool hovered = ImGui.IsItemHovered();
        bool toolOverlayActive = HandleTransformViewportToolOverlayInput(min, max, TransformGizmoContext.Free, allowCut: true, modeChanged: ClearVanillaViewportGizmoDrag);
        if (toolOverlayActive) hovered = false;

        if (hovered)
        {
            NVector2 delta = ImGui.GetIO().MouseDelta;
            bool pan = ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
                (ImGui.IsMouseDragging(ImGuiMouseButton.Right) &&
                    (ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift)));

            if (pan)
            {
                _vanillaViewportPanX = Math.Clamp(_vanillaViewportPanX + delta.X, -size.X, size.X);
                _vanillaViewportPanY = Math.Clamp(_vanillaViewportPanY + delta.Y, -size.Y, size.Y);
            }
            else if (ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                _vanillaViewportYaw = NormalizeRadians(_vanillaViewportYaw + delta.X * 0.01f);
                _vanillaViewportPitch = Math.Clamp(_vanillaViewportPitch + delta.Y * 0.01f, -1.52f, 1.52f);
            }

            float wheel = ImGui.GetIO().MouseWheel;
            if (Math.Abs(wheel) > 0.001f)
            {
                _vanillaViewportZoom = Math.Clamp(_vanillaViewportZoom + wheel * 0.06f, 0.25f, 3.0f);
            }
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint background = ImGui.ColorConvertFloat4ToU32(DevToolsViewportBackground.FillColor);
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 1f));
        uint grid = ImGui.ColorConvertFloat4ToU32(DevToolsViewportBackground.GridMinorColor);
        uint gridMajor = ImGui.ColorConvertFloat4ToU32(DevToolsViewportBackground.GridMajorColor);
        uint text = ImGui.ColorConvertFloat4ToU32(DevToolsViewportBackground.TextColor);
        drawList.AddRectFilled(min, max, background, 4f);

        VanillaPreviewMode effectiveMode = GetVanillaEffectivePreviewMode(scene);
        float viewportWidth = Math.Max(1f, max.X - min.X);
        float viewportHeight = Math.Max(1f, max.Y - min.Y);
        VanillaPreviewGhost[] ghosts = BuildVanillaViewportGhosts(row, scene, effectiveMode, out string ghostOverlayStatus);

        VanillaAnimationViewport3DRenderer renderer = EnsureVanillaPreviewRenderer();
        int textureId = renderer.RenderToTexture(
            scene,
            viewportWidth,
            viewportHeight,
            _vanillaViewportYaw,
            _vanillaViewportPitch,
            _vanillaViewportZoom,
            _vanillaViewportPanX,
            _vanillaViewportPanY,
            effectiveMode,
            _vanillaFirstPersonInspectCamera,
            _vanillaFirstPersonLookPitchDegrees,
            _vanillaFirstPersonRightHandItemCode,
            _vanillaFirstPersonRenderLeftHandItem ? _vanillaFirstPersonLeftHandItemCode : "",
            _vanillaViewportWorldLighting,
            ghosts,
            _vanillaVerbosePreviewLogs,
            out string? previewSkipReason);
        if (textureId > 0)
        {
            drawList.AddImage(new IntPtr(textureId), min, max, new NVector2(0f, 1f), new NVector2(1f, 0f));
            SaveVanillaViewportScreenshotIfRequested(textureId, viewportWidth, viewportHeight, row);
        }
        else if (!string.IsNullOrWhiteSpace(previewSkipReason))
        {
            if (_vanillaViewportScreenshotRequested)
            {
                _vanillaViewportScreenshotRequested = false;
                _vanillaStatus = $"Viewport screenshot failed: preview skipped ({previewSkipReason}).";
            }
            _animationDiagnostics.Warning($"Preview skipped: {previewSkipReason}", $"Scene: {scene.Key}\nMode: {effectiveMode}\nSize: {viewportWidth:0}x{viewportHeight:0}");
            uint warning = ImGui.ColorConvertFloat4ToU32(new NVector4(0.95f, 0.72f, 0.43f, 1f));
            float skipY = string.IsNullOrWhiteSpace(ghostOverlayStatus) ? 54f : 70f;
            drawList.AddText(new NVector2(min.X + 12f, min.Y + skipY), warning, $"Preview skipped: {previewSkipReason}");
        }

        if (effectiveMode == VanillaPreviewMode.Orbit)
        {
            VanillaPreviewCameraState camera = BuildVanillaPreviewCamera(scene, viewportWidth, viewportHeight, _vanillaViewportYaw, _vanillaViewportPitch, _vanillaViewportZoom, _vanillaViewportPanX, _vanillaViewportPanY, effectiveMode);
            drawList.PushClipRect(min, max, true);
            DrawVanillaViewportGrid(drawList, camera, scene, min, viewportWidth, viewportHeight, grid, gridMajor);
            EnsureVanillaMotionTrail(row, scene);
            DrawVanillaMotionTrail(row, scene, drawList, camera, min, viewportWidth, viewportHeight);
            drawList.PopClipRect();
        }

        drawList.AddRect(min, max, border, 4f);
        drawList.AddText(new NVector2(min.X + 12f, min.Y + 10f), text, $"Preview: {scene.DisplayName}");
        if (!string.IsNullOrWhiteSpace(ghostOverlayStatus))
        {
            uint ghostText = ImGui.ColorConvertFloat4ToU32(ghosts.Length > 0
                ? new NVector4(0.54f, 0.86f, 1f, 1f)
                : new NVector4(0.95f, 0.72f, 0.43f, 1f));
            drawList.AddText(new NVector2(min.X + 12f, min.Y + 30f), ghostText, ghostOverlayStatus);
        }

        if (effectiveMode == VanillaPreviewMode.Orbit)
        {
            bool suppressBodyPick = GizmoMode == TransformGizmoMode.Cut
                ? DrawVanillaViewportCutTool(row, scene, drawList, min, max, hovered)
                : DrawVanillaViewportGizmo(row, scene, drawList, min, max, hovered);
            if (GizmoMode != TransformGizmoMode.Cut)
            {
                DrawVanillaViewportElementPicker(row, scene, drawList, min, max, hovered, suppressBodyPick);
            }
        }
        else
        {
            if (_vanillaViewportGizmoDragAxis != TransformGizmoAxis.None)
            {
                ClearVanillaViewportGizmoDrag();
            }

            if (GizmoMode != TransformGizmoMode.None)
            {
                uint hint = ImGui.ColorConvertFloat4ToU32(new NVector4(0.76f, 0.72f, 0.64f, 1f));
                drawList.AddText(new NVector2(min.X + 12f, min.Y + 50f), hint, "Edit gizmos are available in Orbit mode.");
            }
        }

        DrawTransformViewportToolOverlay(min, max, $"vanilla-{scene.Key}", TransformGizmoContext.Free, allowCut: true, modeChanged: ClearVanillaViewportGizmoDrag, allowVanillaCutOptions: true);
    }

    private void SaveVanillaViewportScreenshotIfRequested(int textureId, float viewportWidth, float viewportHeight, VanillaBrowserRow row)
    {
        if (!_vanillaViewportScreenshotRequested) return;
        _vanillaViewportScreenshotRequested = false;
        DevToolsTextureCapture.SaveTexture2D(textureId, (int)MathF.Ceiling(viewportWidth), (int)MathF.Ceiling(viewportHeight), $"animation-{row.Label}", out _vanillaStatus);
    }

    private static void DrawVanillaViewportGrid(ImDrawListPtr drawList, VanillaPreviewCameraState camera, VanillaAnimationPreviewScene scene, NVector2 min, float width, float height, uint color, uint majorColor)
    {
        float modelExtent = Math.Max(Math.Max(scene.ModelWidth, scene.ModelHeight), scene.ModelDepth);
        float centerExtent = Math.Max(Math.Max(Math.Abs(scene.ModelCenterX), Math.Abs(scene.ModelCenterY)), Math.Abs(scene.ModelCenterZ));
        int extent = Math.Clamp((int)Math.Ceiling(Math.Max(modelExtent * 1.5f, centerExtent + 2f)), 4, 16);

        DrawVanillaViewportGridPlane(drawList, camera, min, width, height, NVector3.UnitX, NVector3.UnitZ, extent, color, majorColor);
        DrawVanillaViewportGridPlane(drawList, camera, min, width, height, NVector3.UnitX, NVector3.UnitY, extent, color, majorColor);
        DrawVanillaViewportGridPlane(drawList, camera, min, width, height, NVector3.UnitZ, NVector3.UnitY, extent, color, majorColor);
    }

    private static void DrawVanillaViewportGridPlane(ImDrawListPtr drawList, VanillaPreviewCameraState camera, NVector2 min, float width, float height, NVector3 axisA, NVector3 axisB, int extent, uint color, uint majorColor)
    {
        for (int i = -extent; i <= extent; i++)
        {
            uint lineColor = i == 0 ? majorColor : color;
            float thickness = i == 0 ? 1.8f : 1f;
            DrawVanillaViewportGridLine(drawList, camera, min, width, height, axisA * -extent + axisB * i, axisA * extent + axisB * i, lineColor, thickness);
            DrawVanillaViewportGridLine(drawList, camera, min, width, height, axisA * i + axisB * -extent, axisA * i + axisB * extent, lineColor, thickness);
        }
    }

    private static void DrawVanillaViewportGridLine(ImDrawListPtr drawList, VanillaPreviewCameraState camera, NVector2 min, float width, float height, NVector3 start, NVector3 end, uint color, float thickness)
    {
        int segments = Math.Max(1, (int)Math.Ceiling((end - start).Length()));
        NVector3 step = (end - start) / segments;
        NVector3 previousPoint = start;
        bool previousVisible = ProjectVanillaPreviewPoint(camera.Model, camera, previousPoint, min, width, height, out NVector2 previousScreen);

        for (int segment = 1; segment <= segments; segment++)
        {
            NVector3 point = start + step * segment;
            bool visible = ProjectVanillaPreviewPoint(camera.Model, camera, point, min, width, height, out NVector2 screen);
            if (previousVisible && visible)
            {
                DrawVanillaViewportLine(drawList, previousScreen, screen, color, thickness);
            }

            previousPoint = point;
            previousScreen = screen;
            previousVisible = visible;
        }
    }

    private VanillaPreviewGhost[] BuildVanillaViewportGhosts(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, VanillaPreviewMode effectiveMode, out string overlayStatus)
    {
        overlayStatus = "";
        if (effectiveMode != VanillaPreviewMode.Orbit)
        {
            if (_vanillaOnionSkinEnabled || (_vanillaLiveSymmetryEnabled && _vanillaShowLiveSymmetryGhost))
            {
                overlayStatus = "Ghost overlays hidden: switch to Orbit mode.";
            }

            return [];
        }

        List<VanillaPreviewGhost> ghosts = [];
        List<string> hiddenReasons = [];
        AddVanillaOnionSkinGhosts(row, scene, ghosts, out string onionSkinStatus);
        if (!string.IsNullOrWhiteSpace(onionSkinStatus))
        {
            hiddenReasons.Add(onionSkinStatus);
        }

        VanillaPreviewGhost symmetry = BuildVanillaLiveSymmetryGhost(row, scene, effectiveMode, out string symmetryStatus);
        if (symmetry.Enabled) ghosts.Add(symmetry);
        else if (!string.IsNullOrWhiteSpace(symmetryStatus))
        {
            hiddenReasons.Add(symmetryStatus);
        }

        overlayStatus = ghosts.Count > 0
            ? GetVanillaViewportGhostStatus(ghosts)
            : string.Join(" ", hiddenReasons);
        return ghosts.ToArray();
    }

    private void AddVanillaOnionSkinGhosts(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, List<VanillaPreviewGhost> ghosts, out string hiddenReason)
    {
        hiddenReason = "";
        if (!_vanillaOnionSkinEnabled)
        {
            return;
        }

        if (row.ShapeAnimation == null)
        {
            hiddenReason = "Onion skins hidden: select a shape animation.";
            return;
        }

        if (scene.Playing)
        {
            hiddenReason = "Onion skins hidden while playback is running.";
            return;
        }

        VanillaAnimation animation = row.ShapeAnimation.Animation;
        if (animation.KeyFrames == null || animation.KeyFrames.Length <= 1)
        {
            hiddenReason = "Onion skins hidden: this animation has no neighboring keyframes.";
            return;
        }

        if (!_vanillaOnionSkinPrevious && !_vanillaOnionSkinNext)
        {
            hiddenReason = "Onion skins hidden: previous and next are disabled.";
            return;
        }

        int keyFrameIndex = Math.Clamp(_vanillaSelection.KeyFrameIndex, 0, animation.KeyFrames.Length - 1);
        float opacity = Math.Clamp(_vanillaOnionSkinOpacity, 0.05f, 0.6f);
        int initialCount = ghosts.Count;
        if (_vanillaOnionSkinPrevious && keyFrameIndex > 0)
        {
            float frame = animation.KeyFrames[keyFrameIndex - 1].Frame;
            if (!IsSamePreviewFrame(frame, scene.CurrentFrame))
            {
                ghosts.Add(new VanillaPreviewGhost(true, frame, opacity, 1.0f, 0.62f, 0.28f, $"prev {frame:0}"));
            }
        }

        if (_vanillaOnionSkinNext && keyFrameIndex < animation.KeyFrames.Length - 1)
        {
            float frame = animation.KeyFrames[keyFrameIndex + 1].Frame;
            if (!IsSamePreviewFrame(frame, scene.CurrentFrame))
            {
                ghosts.Add(new VanillaPreviewGhost(true, frame, opacity, 0.35f, 1.0f, 0.55f, $"next {frame:0}"));
            }
        }

        if (ghosts.Count == initialCount)
        {
            hiddenReason = "Onion skins hidden: no enabled neighboring keyframe differs from the current frame.";
        }
    }

    private VanillaPreviewGhost BuildVanillaLiveSymmetryGhost(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, VanillaPreviewMode effectiveMode, out string hiddenReason)
    {
        hiddenReason = "";
        if (!_vanillaLiveSymmetryEnabled)
        {
            return VanillaPreviewGhost.Disabled;
        }

        if (!_vanillaShowLiveSymmetryGhost)
        {
            hiddenReason = "Symmetry ghost hidden: ghost display is disabled.";
            return VanillaPreviewGhost.Disabled;
        }

        if (scene.Playing)
        {
            hiddenReason = "Symmetry ghost hidden while playback is running.";
            return VanillaPreviewGhost.Disabled;
        }

        if (effectiveMode != VanillaPreviewMode.Orbit)
        {
            hiddenReason = "Symmetry ghost hidden: switch to Orbit mode.";
            return VanillaPreviewGhost.Disabled;
        }

        if (row.ShapeAnimation == null)
        {
            hiddenReason = "Symmetry ghost hidden: select a shape animation.";
            return VanillaPreviewGhost.Disabled;
        }

        VanillaAnimation animation = row.ShapeAnimation.Animation;
        if (_vanillaLiveSymmetryMode == VanillaLiveSymmetryMode.InPlace)
        {
            hiddenReason = "Symmetry ghost hidden: in-place mode mirrors on the current frame.";
            return VanillaPreviewGhost.Disabled;
        }

        if (animation.QuantityFrames <= 1 ||
            animation.KeyFrames == null ||
            animation.KeyFrames.Length == 0)
        {
            hiddenReason = "Symmetry ghost hidden: half-cycle mode needs multiple frames.";
            return VanillaPreviewGhost.Disabled;
        }

        int phaseFrames = GetVanillaLiveSymmetryPhaseFrames(animation);
        if (phaseFrames <= 0)
        {
            hiddenReason = "Symmetry ghost hidden: phase is zero.";
            return VanillaPreviewGhost.Disabled;
        }

        int sourceFrame = (int)Math.Round(scene.CurrentFrame, MidpointRounding.AwayFromZero);
        int ghostFrame = GetVanillaPhaseTargetFrame(animation, sourceFrame, phaseFrames);
        if (ghostFrame == sourceFrame)
        {
            hiddenReason = "Symmetry ghost hidden: phase resolves to the current frame.";
            return VanillaPreviewGhost.Disabled;
        }

        return new VanillaPreviewGhost(true, ghostFrame, Math.Clamp(_vanillaLiveSymmetryGhostOpacity, 0.05f, 0.8f), 0.42f, 0.82f, 1f, $"sym {ghostFrame:0}");
    }

    private static bool IsSamePreviewFrame(float left, float right)
    {
        return Math.Abs(left - right) < 0.001f;
    }

    private static string GetVanillaViewportGhostStatus(IReadOnlyList<VanillaPreviewGhost> ghosts)
    {
        return ghosts.Count == 1
            ? $"Ghost: {ghosts[0].Label}"
            : $"Ghosts: {string.Join(", ", ghosts.Select(ghost => ghost.Label))}";
    }

    private VanillaPreviewMode GetVanillaEffectivePreviewMode(VanillaAnimationPreviewScene scene)
    {
        return _vanillaViewportMode switch
        {
            VanillaPreviewMode.FirstPerson when scene.ClassicFirstPersonAvailable => VanillaPreviewMode.FirstPerson,
            VanillaPreviewMode.ImmersiveFirstPerson when scene.ImmersiveFirstPersonAvailable => VanillaPreviewMode.ImmersiveFirstPerson,
            _ => VanillaPreviewMode.Orbit
        };
    }

    private bool DrawVanillaViewportCutTool(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, ImDrawListPtr drawList, NVector2 min, NVector2 max, bool hovered)
    {
        if (!hovered || row.ShapeAnimation == null && row.MetadataEntry?.ResolveCurrentShape() == null) return false;
        if (!TryPickVanillaCutPreview(scene, min, max, ImGui.GetMousePos(), out VanillaCutPreview preview))
        {
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _vanillaStatus = "Cut did not hit a usable cuboid face. Hover a visible face away from its edge, or pick a different Cut axis.";
            }

            return false;
        }

        uint plane = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.62f, 0.18f, 0.82f));
        uint line = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f));
        for (int index = 0; index < preview.PlaneCorners.Length; index++)
        {
            DrawVanillaViewportLine(drawList, preview.PlaneCorners[index], preview.PlaneCorners[(index + 1) & 3], plane, 1.8f);
        }
        DrawVanillaViewportLine(drawList, preview.LineStart, preview.LineEnd, line, 3.1f);
        drawList.AddText((preview.LineStart + preview.LineEnd) * 0.5f + new NVector2(8f, -18f), line, $"Cut {ModelAxisName(preview.CutAxis)} {preview.CutCoordinate:0.###}");

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            ApplyVanillaViewportCut(row, preview);
        }

        return true;
    }

    private bool TryPickVanillaCutPreview(VanillaAnimationPreviewScene scene, NVector2 min, NVector2 max, NVector2 mouse, out VanillaCutPreview preview)
    {
        preview = default;
        float width = Math.Max(1f, max.X - min.X);
        float height = Math.Max(1f, max.Y - min.Y);
        VanillaPreviewCameraState camera = BuildVanillaPreviewCamera(scene, width, height, _vanillaViewportYaw, _vanillaViewportPitch, _vanillaViewportZoom, _vanillaViewportPanX, _vanillaViewportPanY, VanillaPreviewMode.Orbit);

        bool found = false;
        double bestDistance = double.MaxValue;
        int bestDepth = -1;
        VanillaCutPreview bestPreview = default;

        void Visit(ElementPose pose, int depth)
        {
            if (pose.ForElement == null || string.IsNullOrWhiteSpace(pose.ForElement.Name)) return;

            Matrixf elementModel = BuildVanillaElementModelMatrix(camera.Model, pose);
            if (TryGetVanillaLocalCutHit(camera, elementModel, pose.ForElement, min, width, height, mouse, out double[] localUnits, out int faceAxis, out bool facePositive, out double distance) &&
                TryBuildVanillaCutPreview(camera, elementModel, pose.ForElement, localUnits, faceAxis, facePositive, min, width, height, out VanillaCutPreview candidate))
            {
                bool better = distance < bestDistance - 0.001 ||
                    (Math.Abs(distance - bestDistance) <= 0.001 && depth > bestDepth);
                if (better)
                {
                    bestPreview = candidate;
                    bestDistance = distance;
                    bestDepth = depth;
                    found = true;
                }
            }

            foreach (ElementPose child in pose.ChildElementPoses ?? [])
            {
                Visit(child, depth + 1);
            }
        }

        foreach (ElementPose root in scene.Animator.RootPoses ?? [])
        {
            Visit(root, 0);
        }

        preview = bestPreview;
        return found;
    }

    private bool TryBuildVanillaCutPreview(
        VanillaPreviewCameraState camera,
        Matrixf elementModel,
        ShapeElement element,
        double[] localUnits,
        int faceAxis,
        bool facePositive,
        NVector2 min,
        float width,
        float height,
        out VanillaCutPreview preview)
    {
        preview = default;
        if (element.From == null || element.To == null || element.From.Length < 3 || element.To.Length < 3) return false;
        if (faceAxis < 0 || faceAxis > 2) return false;
        string elementName = element.Name ?? "";
        if (string.IsNullOrWhiteSpace(elementName)) return false;

        double[] sizeUnits =
        [
            Math.Max(0.0, element.To[0] - element.From[0]),
            Math.Max(0.0, element.To[1] - element.From[1]),
            Math.Max(0.0, element.To[2] - element.From[2])
        ];
        double[] sizeBlocks = [sizeUnits[0] / 16.0, sizeUnits[1] / 16.0, sizeUnits[2] / 16.0];
        int[] candidates = ModelCutCandidateAxes(faceAxis);
        if (candidates.Length == 0) return false;

        bool alternate = _modelCutOrientation == ModelCutOrientation.Auto && IsDevToolsShiftDown();
        bool found = false;
        float bestScore = float.MinValue;
        VanillaCutPreview best = default;

        foreach (int cutAxis in candidates)
        {
            double cutLocalUnits = Math.Clamp(localUnits[cutAxis], 0.0, sizeUnits[cutAxis]);
            double cutCoordinate = Math.Round(element.From[cutAxis] + cutLocalUnits, 6);
            if (!VanillaIsCutCoordinateInside(element, cutAxis, cutCoordinate)) continue;

            double cutLocalBlocks = cutLocalUnits / 16.0;
            int lineAxis = 3 - faceAxis - cutAxis;
            NVector2[] planeCorners = ProjectVanillaCutPlaneCorners(camera, elementModel, sizeBlocks, cutAxis, cutLocalBlocks, min, width, height);
            if (planeCorners.Length < 4) continue;

            double[] lineStartUnits = [0, 0, 0];
            double[] lineEndUnits = [0, 0, 0];
            lineStartUnits[faceAxis] = facePositive ? sizeBlocks[faceAxis] : 0.0;
            lineEndUnits[faceAxis] = lineStartUnits[faceAxis];
            lineStartUnits[cutAxis] = cutLocalBlocks;
            lineEndUnits[cutAxis] = cutLocalBlocks;
            lineStartUnits[lineAxis] = 0.0;
            lineEndUnits[lineAxis] = sizeBlocks[lineAxis];
            if (!ProjectVanillaPreviewPoint(elementModel, camera, ToNVector3(lineStartUnits), min, width, height, out NVector2 lineStart) ||
                !ProjectVanillaPreviewPoint(elementModel, camera, ToNVector3(lineEndUnits), min, width, height, out NVector2 lineEnd))
            {
                continue;
            }

            float score = (lineEnd - lineStart).LengthSquared();
            if (alternate) score = -score;
            if (!found || score > bestScore)
            {
                bestScore = score;
                best = new VanillaCutPreview(elementName, faceAxis, facePositive, cutAxis, cutCoordinate, planeCorners, lineStart, lineEnd);
                found = true;
            }
        }

        preview = best;
        return found;
    }

    private static NVector2[] ProjectVanillaCutPlaneCorners(VanillaPreviewCameraState camera, Matrixf elementModel, double[] sizeBlocks, int cutAxis, double cutLocalBlocks, NVector2 min, float width, float height)
    {
        int[] axes = [0, 1, 2];
        int axisA = axes.First(axis => axis != cutAxis);
        int axisB = axes.Last(axis => axis != cutAxis);
        double[][] corners =
        [
            [0.0, 0.0, 0.0],
            [0.0, 0.0, 0.0],
            [0.0, 0.0, 0.0],
            [0.0, 0.0, 0.0]
        ];
        corners[0][axisA] = 0.0;
        corners[0][axisB] = 0.0;
        corners[1][axisA] = sizeBlocks[axisA];
        corners[1][axisB] = 0.0;
        corners[2][axisA] = sizeBlocks[axisA];
        corners[2][axisB] = sizeBlocks[axisB];
        corners[3][axisA] = 0.0;
        corners[3][axisB] = sizeBlocks[axisB];
        for (int index = 0; index < corners.Length; index++)
        {
            corners[index][cutAxis] = cutLocalBlocks;
        }

        NVector2[] projected = new NVector2[4];
        for (int index = 0; index < corners.Length; index++)
        {
            if (!ProjectVanillaPreviewPoint(elementModel, camera, ToNVector3(corners[index]), min, width, height, out projected[index]))
            {
                return [];
            }
        }

        return projected;
    }

    private static bool TryGetVanillaLocalCutHit(
        VanillaPreviewCameraState camera,
        Matrixf elementModel,
        ShapeElement element,
        NVector2 min,
        float width,
        float height,
        NVector2 mouse,
        out double[] localUnits,
        out int faceAxis,
        out bool facePositive,
        out double distance)
    {
        localUnits = [0, 0, 0];
        faceAxis = -1;
        facePositive = false;
        distance = 0;
        if (element.From == null || element.To == null || element.From.Length < 3 || element.To.Length < 3) return false;

        double[] sizeBlocks =
        [
            Math.Max(0.0, element.To[0] - element.From[0]) / 16.0,
            Math.Max(0.0, element.To[1] - element.From[1]) / 16.0,
            Math.Max(0.0, element.To[2] - element.From[2]) / 16.0
        ];
        if (sizeBlocks.Any(size => size <= 0.000001)) return false;

        Matrixf clipFromLocal = new();
        clipFromLocal.Set(elementModel.Values);
        clipFromLocal.ReverseMul(camera.ProjectionView.Values);

        double[] inverseClipFromLocal = Mat4d.Create();
        if (Mat4d.Invert(inverseClipFromLocal, VanillaToDoubleMatrix(clipFromLocal.Values)) == null) return false;
        if (!UnprojectVanillaViewportPoint(inverseClipFromLocal, min, width, height, mouse, -1.0, out Vec3d near)) return false;
        if (!UnprojectVanillaViewportPoint(inverseClipFromLocal, min, width, height, mouse, 1.0, out Vec3d far)) return false;

        Vec3d direction = new(far.X - near.X, far.Y - near.Y, far.Z - near.Z);
        if (direction.LengthSq() < 0.000001) return false;
        direction.Normalize();

        if (!TryIntersectVanillaLocalCutBox(near, direction, sizeBlocks, out distance)) return false;
        Vec3d hit = new(near.X + direction.X * distance, near.Y + direction.Y * distance, near.Z + direction.Z * distance);
        double[] localBlocks =
        [
            Math.Clamp(hit.X, 0.0, sizeBlocks[0]),
            Math.Clamp(hit.Y, 0.0, sizeBlocks[1]),
            Math.Clamp(hit.Z, 0.0, sizeBlocks[2])
        ];

        double bestFaceDistance = double.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            double minDistance = Math.Abs(localBlocks[axis]);
            if (minDistance < bestFaceDistance)
            {
                bestFaceDistance = minDistance;
                faceAxis = axis;
                facePositive = false;
            }

            double maxDistance = Math.Abs(sizeBlocks[axis] - localBlocks[axis]);
            if (maxDistance < bestFaceDistance)
            {
                bestFaceDistance = maxDistance;
                faceAxis = axis;
                facePositive = true;
            }
        }

        localUnits = [localBlocks[0] * 16.0, localBlocks[1] * 16.0, localBlocks[2] * 16.0];
        return faceAxis >= 0;
    }

    private static bool TryIntersectVanillaLocalCutBox(Vec3d origin, Vec3d direction, double[] sizeBlocks, out double distance)
    {
        distance = 0;
        double tMin = 0;
        double tMax = double.MaxValue;
        if (!UpdateVanillaRaySlab(origin.X, direction.X, 0.0, sizeBlocks[0], ref tMin, ref tMax)) return false;
        if (!UpdateVanillaRaySlab(origin.Y, direction.Y, 0.0, sizeBlocks[1], ref tMin, ref tMax)) return false;
        if (!UpdateVanillaRaySlab(origin.Z, direction.Z, 0.0, sizeBlocks[2], ref tMin, ref tMax)) return false;

        distance = tMin >= 0 ? tMin : tMax;
        return distance >= 0 && distance < double.MaxValue;
    }

    private static bool UpdateVanillaRaySlab(double origin, double direction, double min, double max, ref double tMin, ref double tMax)
    {
        const double epsilon = 0.000001;
        if (Math.Abs(direction) < epsilon)
        {
            return origin >= min && origin <= max;
        }

        double t1 = (min - origin) / direction;
        double t2 = (max - origin) / direction;
        if (t1 > t2) (t1, t2) = (t2, t1);
        tMin = Math.Max(tMin, t1);
        tMax = Math.Min(tMax, t2);
        return tMin <= tMax;
    }

    private static bool UnprojectVanillaViewportPoint(double[] inverseClipFromLocal, NVector2 viewportMin, float width, float height, NVector2 mouse, double clipZ, out Vec3d local)
    {
        local = new Vec3d();
        double ndcX = 2.0 * (mouse.X - viewportMin.X) / Math.Max(1f, width) - 1.0;
        double ndcY = 1.0 - 2.0 * (mouse.Y - viewportMin.Y) / Math.Max(1f, height);
        double[] result = Mat4d.MulWithVec4(inverseClipFromLocal, [ndcX, ndcY, clipZ, 1.0]);
        if (Math.Abs(result[3]) < 0.000001) return false;

        local.X = result[0] / result[3];
        local.Y = result[1] / result[3];
        local.Z = result[2] / result[3];
        return IsFinite((float)local.X) && IsFinite((float)local.Y) && IsFinite((float)local.Z);
    }

    private static double[] VanillaToDoubleMatrix(float[] values)
    {
        double[] result = new double[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            result[index] = values[index];
        }

        return result;
    }

    private static NVector3 ToNVector3(double[] values)
    {
        return new NVector3(
            values.Length > 0 ? (float)values[0] : 0f,
            values.Length > 1 ? (float)values[1] : 0f,
            values.Length > 2 ? (float)values[2] : 0f);
    }

    private void ApplyVanillaViewportCut(VanillaBrowserRow row, VanillaCutPreview preview)
    {
        VanillaShapeAnimationEntry? entry = row.ShapeAnimation ?? row.MetadataEntry?.ResolveCurrentShape();
        VanillaAnimationDocument? document = entry?.Document;
        Shape? shape = document?.Shape;
        if (entry == null || document == null || shape?.Elements == null)
        {
            _vanillaStatus = "Could not cut: selected animation has no editable shape document.";
            return;
        }

        int keyFrameIndex = entry.Animation.KeyFrames is { Length: > 0 }
            ? Math.Clamp(_vanillaSelection.KeyFrameIndex, 0, entry.Animation.KeyFrames.Length - 1)
            : 0;
        string[] symmetryUniverse = [];
        string symmetryPairName = "";
        if (_vanillaCutSymmetryEnabled && entry.Animation.KeyFrames is { Length: > 0 } keyFrames)
        {
            AnimationKeyFrame symmetryKeyFrame = keyFrames[keyFrameIndex];
            symmetryUniverse = BuildVanillaSymmetryElementUniverse(document, entry.Animation, symmetryKeyFrame);
            if (TryResolveVanillaSymmetryPair(document, preview.ElementName, symmetryUniverse, out string resolvedPair, out _, out _) &&
                !string.Equals(resolvedPair, preview.ElementName, StringComparison.OrdinalIgnoreCase))
            {
                symmetryPairName = resolvedPair;
            }
        }

        if (!TryApplyVanillaViewportCutSingle(document, entry, keyFrameIndex, preview.ElementName, preview.CutAxis, preview.CutCoordinate, out VanillaCutApplyResult primary, out string error))
        {
            _vanillaStatus = error;
            return;
        }

        List<string> changedElementNames = [primary.OriginalName, primary.NewName];
        VanillaCutApplyResult? symmetryResult = null;
        string symmetryStatus = "";
        if (_vanillaCutSymmetryEnabled)
        {
            if (!string.IsNullOrWhiteSpace(symmetryPairName) &&
                TryFindVanillaShapeElementSlot(shape, symmetryPairName, out ShapeElement? pairSource, out _, out _) &&
                pairSource?.From is { Length: >= 3 } pairFrom &&
                pairSource.To is { Length: >= 3 } pairTo)
            {
                int pairAxis = primary.Axis;
                double pairCoordinate = Math.Round(pairFrom[pairAxis] + (pairTo[pairAxis] - pairFrom[pairAxis]) * primary.CutRatio, 6);
                if (TryApplyVanillaViewportCutSingle(document, entry, keyFrameIndex, symmetryPairName, pairAxis, pairCoordinate, out VanillaCutApplyResult pairCut, out string pairError))
                {
                    symmetryResult = pairCut;
                    changedElementNames.Add(pairCut.OriginalName);
                    changedElementNames.Add(pairCut.NewName);
                    SetVanillaSymmetryPairOverride(document, primary.NewName, pairCut.NewName);
                }
                else
                {
                    symmetryStatus = $" Symmetry cut skipped: {pairError}";
                }
            }
            else
            {
                symmetryStatus = " Symmetry cut skipped: no mirrored element was found.";
            }
        }

        InvalidateVanillaShapeElementCaches(shape);
        InvalidateVanillaIkChainCache();
        _vanillaSelection.ElementName = primary.NewName;
        MarkVanillaDirty(document);
        RefreshVanillaPreviewAfterEdit(row, changedElementNames.ToArray());

        string hierarchyStatus = primary.InsertedManualIk ? " Added the new segment to the manual IK chain." : "";
        string symmetryText = symmetryResult is { } mirrored
            ? $" Symmetry also cut {mirrored.OriginalName} -> {mirrored.NewName}."
            : symmetryStatus;
        _vanillaStatus = $"Cut {primary.OriginalName} on {ModelAxisName(primary.Axis)} at {primary.Coordinate:0.###}; added child segment {primary.NewName} with {primary.RegisteredChannels} animation channel(s).{symmetryText}{hierarchyStatus}";
    }

    private bool TryApplyVanillaViewportCutSingle(
        VanillaAnimationDocument document,
        VanillaShapeAnimationEntry activeEntry,
        int keyFrameIndex,
        string elementName,
        int cutAxis,
        double cutCoordinate,
        out VanillaCutApplyResult result,
        out string error)
    {
        result = default;
        error = "";
        Shape? shape = document.Shape;
        if (shape?.Elements == null)
        {
            error = "Could not cut: selected animation has no editable shape document.";
            return false;
        }

        if (!TryFindVanillaShapeElementSlot(shape, elementName, out ShapeElement? source, out ShapeElement? parent, out int index) ||
            source?.From == null || source.To == null || source.From.Length < 3 || source.To.Length < 3)
        {
            error = $"Could not cut: shape element '{elementName}' was not found.";
            return false;
        }

        int axis = Math.Clamp(cutAxis, 0, 2);
        double coordinate = Math.Round(cutCoordinate, 6);
        if (!VanillaIsCutCoordinateInside(source, axis, coordinate))
        {
            error = $"Could not cut {elementName}: cut line is too close to the {ModelAxisName(axis)} edge.";
            return false;
        }

        string originalName = source.Name ?? "";
        if (string.IsNullOrWhiteSpace(originalName))
        {
            error = "Could not cut: selected shape element has no name.";
            return false;
        }

        double size = source.To[axis] - source.From[axis];
        double cutRatio = Math.Clamp((coordinate - source.From[axis]) / size, 0.0, 1.0);
        string newName = ReserveVanillaShapeElementName(shape, $"{originalName}_cut2");
        ShapeElement lower = CloneVanillaShapeElement(source);
        ShapeElement upper = CloneVanillaShapeElement(source);
        ShapeElement[] existingChildren = (source.Children ?? []).Select(CloneVanillaShapeElement).ToArray();
        lower.To![axis] = coordinate;
        upper.From![axis] = coordinate;
        lower.Children = [];
        upper.Children = [];

        bool lowerIsRootSide = VanillaCutLowerSegmentIsRootSide(source, axis, coordinate);
        ShapeElement rootSide = lowerIsRootSide ? lower : upper;
        ShapeElement distalSide = lowerIsRootSide ? upper : lower;
        rootSide.Name = originalName;
        distalSide.Name = newName;
        rootSide.Children = [];
        distalSide.Children = existingChildren;
        distalSide.StepParentName = null;

        ResetVanillaCutElementRuntimeState(rootSide, parent);
        ResetVanillaCutElementRuntimeState(distalSide, parent);
        bool distalStartsAtCutPlane = lowerIsRootSide;
        if (!TryPreserveVanillaCutChildTransform(distalSide, parent, rootSide, axis, distalStartsAtCutPlane))
        {
            error = $"Could not cut {originalName}: failed to preserve the new segment position under its parent.";
            return false;
        }

        rootSide.Children = [distalSide];
        ResetVanillaCutElementRuntimeState(rootSide, parent);

        ShapeElement[] siblings = parent?.Children ?? shape.Elements;
        List<ShapeElement> updated = siblings.ToList();
        updated[index] = rootSide;
        if (parent == null)
        {
            shape.Elements = updated.ToArray();
        }
        else
        {
            parent.Children = updated.ToArray();
        }

        int registeredChannels = RegisterVanillaCutAnimationChannels(document, activeEntry, keyFrameIndex, originalName, newName);
        if (!SyncVanillaCutToSourceJson(document, originalName, rootSide, distalSide))
        {
            SyncVanillaShapeElementsToSourceJson(document);
        }

        bool insertedManualIk = InsertVanillaCutChildIntoManualIkChain(originalName, newName);
        result = new VanillaCutApplyResult(originalName, newName, axis, coordinate, cutRatio, registeredChannels, insertedManualIk);
        return true;
    }

    private static bool VanillaCutLowerSegmentIsRootSide(ShapeElement source, int cutAxis, double coordinate)
    {
        if (source.From == null || source.To == null || source.From.Length < 3 || source.To.Length < 3) return true;

        cutAxis = Math.Clamp(cutAxis, 0, 2);
        double root = source.RotationOrigin != null && source.RotationOrigin.Length > cutAxis
            ? source.RotationOrigin[cutAxis]
            : 0.0;
        double lowerCenter = (source.From[cutAxis] + coordinate) * 0.5;
        double upperCenter = (coordinate + source.To[cutAxis]) * 0.5;
        return Math.Abs(root - lowerCenter) <= Math.Abs(root - upperCenter);
    }

    private static bool TryPreserveVanillaCutChildTransform(ShapeElement element, ShapeElement? oldParent, ShapeElement newParent, int cutAxis, bool cutAtFromSide)
    {
        try
        {
            if (element.From == null || element.To == null || element.From.Length < 3 || element.To.Length < 3) return false;

            Matrixf oldParentMatrix = VanillaComputeShapeParentMatrix(oldParent);
            Matrixf oldWorldMatrix = new();
            oldWorldMatrix.Identity();
            oldWorldMatrix.Mul(oldParentMatrix.Values);
            oldWorldMatrix.Mul(VanillaLocalShapeElementMatrix(element).Values);

            Matrixd oldWorld = ModelMatrixd(oldWorldMatrix);
            Matrixd newParentWorld = ModelMatrixd(VanillaComputeShapeElementMatrix(newParent));
            Matrixd inverseNewParent = newParentWorld.Clone().Invert();
            Matrixd newLocal = oldWorld.Clone().ReverseMul(inverseNewParent.Values);

            Vec3d localBoxOrigin = ModelTransformPoint(newLocal, new Vec3d(0, 0, 0));
            RigIkMatrix3 newRotation = RigIkMatrix3.FromMatrixd(newLocal).Orthonormalized();
            Vec3d euler = newRotation.ToEulerDegrees();

            double sizeX = element.To[0] - element.From[0];
            double sizeY = element.To[1] - element.From[1];
            double sizeZ = element.To[2] - element.From[2];
            cutAxis = Math.Clamp(cutAxis, 0, 2);
            Vec3d hingeOffset = new(
                cutAxis == 0 ? (cutAtFromSide ? 0.0 : sizeX) : sizeX * 0.5,
                cutAxis == 1 ? (cutAtFromSide ? 0.0 : sizeY) : sizeY * 0.5,
                cutAxis == 2 ? (cutAtFromSide ? 0.0 : sizeZ) : sizeZ * 0.5);
            Vec3d newFrom = Add(Sub(localBoxOrigin, hingeOffset), newRotation.TransformDirection(hingeOffset));
            Vec3d newOriginLocal = Add(newFrom, hingeOffset);

            element.From[0] = ModelRoundForReparent(newFrom.X);
            element.From[1] = ModelRoundForReparent(newFrom.Y);
            element.From[2] = ModelRoundForReparent(newFrom.Z);
            element.To[0] = ModelRoundForReparent(newFrom.X + sizeX);
            element.To[1] = ModelRoundForReparent(newFrom.Y + sizeY);
            element.To[2] = ModelRoundForReparent(newFrom.Z + sizeZ);
            element.RotationX = ModelWrapDegrees(euler.X);
            element.RotationY = ModelWrapDegrees(euler.Y);
            element.RotationZ = ModelWrapDegrees(euler.Z);
            element.RotationOrigin =
            [
                ModelRoundForReparent(newOriginLocal.X),
                ModelRoundForReparent(newOriginLocal.Y),
                ModelRoundForReparent(newOriginLocal.Z)
            ];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Matrixf VanillaComputeShapeParentMatrix(ShapeElement? parent)
    {
        if (parent == null)
        {
            Matrixf identity = new();
            identity.Identity();
            return identity;
        }

        return VanillaComputeShapeElementMatrix(parent);
    }

    private static Matrixf VanillaComputeShapeElementMatrix(ShapeElement element)
    {
        List<ShapeElement> chain = [];
        for (ShapeElement? current = element; current != null; current = current.ParentElement)
        {
            chain.Add(current);
        }
        chain.Reverse();

        Matrixf matrix = new();
        matrix.Identity();
        foreach (ShapeElement node in chain)
        {
            matrix.Mul(VanillaLocalShapeElementMatrix(node).Values);
        }

        return matrix;
    }

    private static Matrixf VanillaLocalShapeElementMatrix(ShapeElement element)
    {
        Matrixf matrix = new();
        matrix.Identity();
        double[] from = element.From is { Length: >= 3 } values ? values : [0.0, 0.0, 0.0];
        double[] origin = element.RotationOrigin is { Length: >= 3 } rotationOrigin ? rotationOrigin : [0.0, 0.0, 0.0];
        float originX = (float)(origin[0] / ModelUnitsPerBlock);
        float originY = (float)(origin[1] / ModelUnitsPerBlock);
        float originZ = (float)(origin[2] / ModelUnitsPerBlock);
        matrix.Translate(originX, originY, originZ);
        matrix.Rotate(
            (float)(element.RotationX * GameMath.DEG2RAD),
            (float)(element.RotationY * GameMath.DEG2RAD),
            (float)(element.RotationZ * GameMath.DEG2RAD));
        matrix.Scale((float)element.ScaleX, (float)element.ScaleY, (float)element.ScaleZ);
        matrix.Translate(
            (float)(from[0] / ModelUnitsPerBlock) - originX,
            (float)(from[1] / ModelUnitsPerBlock) - originY,
            (float)(from[2] / ModelUnitsPerBlock) - originZ);
        return matrix;
    }

    private static bool VanillaIsCutCoordinateInside(ShapeElement element, int axis, double coordinate)
    {
        if (element.From == null || element.To == null || element.From.Length < 3 || element.To.Length < 3) return false;
        axis = Math.Clamp(axis, 0, 2);
        double size = element.To[axis] - element.From[axis];
        double margin = Math.Max(0.0001, Math.Min(0.01, Math.Abs(size) * 0.001));
        return coordinate > element.From[axis] + margin &&
            coordinate < element.To[axis] - margin;
    }

    private static ShapeElement CloneVanillaShapeElement(ShapeElement source)
    {
        return source.Clone();
    }

    private static void ResetVanillaCutElementRuntimeState(ShapeElement element, ShapeElement? parent)
    {
        element.ParentElement = parent;
        element.JointId = 0;
        element.inverseModelTransform = null;
        if (element.AttachmentPoints != null)
        {
            foreach (AttachmentPoint attachmentPoint in element.AttachmentPoints)
            {
                attachmentPoint.ParentElement = element;
            }
        }

        if (element.Children == null) return;
        foreach (ShapeElement child in element.Children)
        {
            ResetVanillaCutElementRuntimeState(child, element);
        }
    }

    private static int RegisterVanillaCutAnimationChannels(VanillaAnimationDocument document, VanillaShapeAnimationEntry activeEntry, int keyFrameIndex, string originalName, string newName)
    {
        int registered = 0;
        foreach (VanillaShapeAnimationEntry animationEntry in document.ShapeAnimations)
        {
            foreach (AnimationKeyFrame keyFrame in animationEntry.Animation.KeyFrames ?? [])
            {
                keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
                if (!keyFrame.Elements.ContainsKey(originalName) || keyFrame.Elements.ContainsKey(newName))
                {
                    continue;
                }

                keyFrame.Elements[newName] = new AnimationKeyFrameElement();
                registered++;
            }
        }

        AnimationKeyFrame[] activeKeyFrames = activeEntry.Animation.KeyFrames ?? [];
        if (activeKeyFrames.Length > 0)
        {
            AnimationKeyFrame keyFrame = activeKeyFrames[Math.Clamp(keyFrameIndex, 0, activeKeyFrames.Length - 1)];
            keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
            if (!keyFrame.Elements.ContainsKey(originalName))
            {
                keyFrame.Elements[originalName] = new AnimationKeyFrameElement();
                registered++;
            }

            if (!keyFrame.Elements.ContainsKey(newName))
            {
                keyFrame.Elements[newName] = new AnimationKeyFrameElement();
                registered++;
            }
        }

        return registered;
    }

    private bool InsertVanillaCutChildIntoManualIkChain(string originalName, string newName)
    {
        if (_vanillaIkMode != VanillaIkChainMode.ManualOverride) return false;
        int originalIndex = _vanillaIkChainElementNames.FindIndex(name => string.Equals(name, originalName, StringComparison.OrdinalIgnoreCase));
        if (originalIndex < 0 || ContainsVanillaIkChainElement(newName)) return false;

        _vanillaIkChainElementNames.Insert(originalIndex + 1, newName);
        _vanillaIkHasTarget = false;
        InvalidateVanillaIkChainCache();
        return true;
    }

    private static void SyncVanillaShapeElementsToSourceJson(VanillaAnimationDocument document)
    {
        if (document.SourceJson is not JObject json || document.Shape?.Elements == null) return;
        json["elements"] = JToken.FromObject(document.Shape.Elements, JsonSerializer.Create(VanillaShapeElementJsonSettings));
    }

    private static bool SyncVanillaCutToSourceJson(VanillaAnimationDocument document, string originalName, ShapeElement firstElement, ShapeElement secondElement)
    {
        if (document.SourceJson is not JObject json) return false;
        if (!TryFindVanillaShapeElementJsonSlot(json, originalName, out JArray? siblings, out int index) ||
            siblings == null ||
            index < 0 ||
            index >= siblings.Count ||
            siblings[index] is not JObject sourceToken)
        {
            return false;
        }

        JObject first = (JObject)sourceToken.DeepClone();
        JObject second = (JObject)sourceToken.DeepClone();
        SetVanillaElementJsonFromShape(first, firstElement);
        SetVanillaElementJsonFromShape(second, secondElement);
        if (secondElement.Children is { Length: > 0 })
        {
            JToken? originalChildren = GetVanillaJsonProperty(sourceToken, "children")?.Value.DeepClone();
            JArray existingChildTokens = originalChildren as JArray ?? JArray.FromObject(secondElement.Children, JsonSerializer.Create(VanillaShapeElementJsonSettings));
            JProperty? childProperty = GetVanillaJsonProperty(second, "children");
            if (childProperty == null)
            {
                second["children"] = existingChildTokens;
            }
            else
            {
                childProperty.Value = existingChildTokens;
            }
        }
        else
        {
            RemoveVanillaElementJsonProperty(second, "children");
        }
        RemoveVanillaElementJsonProperty(second, "stepParentName");
        RemoveVanillaElementJsonProperty(second, "stepparentname");

        JArray children = new() { second };
        JProperty? childrenProperty = GetVanillaJsonProperty(first, "children");
        if (childrenProperty == null)
        {
            first["children"] = children;
        }
        else
        {
            childrenProperty.Value = children;
        }

        siblings[index] = first;
        return true;
    }

    private static bool TryFindVanillaShapeElementJsonSlot(JObject shapeJson, string elementName, out JArray? siblings, out int index)
    {
        siblings = null;
        index = -1;
        JArray? roots = GetVanillaJsonArray(shapeJson, "elements");
        return roots != null && TryFindVanillaShapeElementJsonSlotRecursive(roots, elementName, out siblings, out index);
    }

    private static bool TryFindVanillaShapeElementJsonSlotRecursive(JArray candidates, string elementName, out JArray? siblings, out int index)
    {
        siblings = null;
        index = -1;
        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            if (candidates[candidateIndex] is not JObject element) continue;

            string? name = GetVanillaJsonString(element, "name");
            if (string.Equals(name, elementName, StringComparison.OrdinalIgnoreCase))
            {
                siblings = candidates;
                index = candidateIndex;
                return true;
            }

            JArray? children = GetVanillaJsonArray(element, "children");
            if (children != null && TryFindVanillaShapeElementJsonSlotRecursive(children, elementName, out siblings, out index))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetVanillaJsonString(JObject obj, string propertyName)
    {
        return GetVanillaJsonProperty(obj, propertyName)?.Value.Type == JTokenType.String
            ? GetVanillaJsonProperty(obj, propertyName)?.Value.Value<string>()
            : GetVanillaJsonProperty(obj, propertyName)?.Value?.ToString();
    }

    private static JArray? GetVanillaJsonArray(JObject obj, string propertyName)
    {
        return GetVanillaJsonProperty(obj, propertyName)?.Value as JArray;
    }

    private static JProperty? GetVanillaJsonProperty(JObject obj, string propertyName)
    {
        return obj.Properties().FirstOrDefault(property => string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private static void SetVanillaElementJsonString(JObject obj, string propertyName, string value)
    {
        JProperty? property = GetVanillaJsonProperty(obj, propertyName);
        if (property == null)
        {
            obj[propertyName] = value;
        }
        else
        {
            property.Value = value;
        }
    }

    private static void SetVanillaElementJsonFromShape(JObject obj, ShapeElement element)
    {
        SetVanillaElementJsonString(obj, "name", element.Name ?? "");
        SetVanillaElementJsonVector(obj, "from", element.From);
        SetVanillaElementJsonVector(obj, "to", element.To);
        SetVanillaElementJsonOptionalVector(obj, "rotationOrigin", element.RotationOrigin);
        SetVanillaElementJsonOptionalNumber(obj, "rotationX", element.RotationX, 0.0);
        SetVanillaElementJsonOptionalNumber(obj, "rotationY", element.RotationY, 0.0);
        SetVanillaElementJsonOptionalNumber(obj, "rotationZ", element.RotationZ, 0.0);
        SetVanillaElementJsonOptionalNumber(obj, "scaleX", element.ScaleX, 1.0);
        SetVanillaElementJsonOptionalNumber(obj, "scaleY", element.ScaleY, 1.0);
        SetVanillaElementJsonOptionalNumber(obj, "scaleZ", element.ScaleZ, 1.0);
        if (string.IsNullOrWhiteSpace(element.StepParentName))
        {
            RemoveVanillaElementJsonProperty(obj, "stepParentName");
            RemoveVanillaElementJsonProperty(obj, "stepparentname");
        }
        else
        {
            SetVanillaElementJsonString(obj, "stepParentName", element.StepParentName!);
        }
    }

    private static void SetVanillaElementJsonVector(JObject obj, string propertyName, double[]? values)
    {
        if (values == null || values.Length < 3) return;

        JArray array =
        [
            RoundVanillaJsonNumber(values[0]),
            RoundVanillaJsonNumber(values[1]),
            RoundVanillaJsonNumber(values[2])
        ];
        JProperty? property = GetVanillaJsonProperty(obj, propertyName);
        if (property == null)
        {
            obj[propertyName] = array;
        }
        else
        {
            property.Value = array;
        }
    }

    private static void SetVanillaElementJsonOptionalVector(JObject obj, string propertyName, double[]? values)
    {
        if (values == null || values.Length < 3)
        {
            RemoveVanillaElementJsonProperty(obj, propertyName);
            return;
        }

        SetVanillaElementJsonVector(obj, propertyName, values);
    }

    private static void SetVanillaElementJsonOptionalNumber(JObject obj, string propertyName, double value, double defaultValue)
    {
        if (Math.Abs(value - defaultValue) < 0.000001)
        {
            RemoveVanillaElementJsonProperty(obj, propertyName);
            return;
        }

        JProperty? property = GetVanillaJsonProperty(obj, propertyName);
        if (property == null)
        {
            obj[propertyName] = RoundVanillaJsonNumber(value);
        }
        else
        {
            property.Value = RoundVanillaJsonNumber(value);
        }
    }

    private static double RoundVanillaJsonNumber(double value)
    {
        return Math.Abs(value) < 0.000001 ? 0.0 : Math.Round(value, 6);
    }

    private static void RemoveVanillaElementJsonProperty(JObject obj, string propertyName)
    {
        foreach (JProperty property in obj.Properties().Where(property => string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            property.Remove();
        }
    }

    private static void InvalidateVanillaShapeElementCaches(Shape shape)
    {
        VanillaShapeElementNamesCache.Remove(shape);
        VanillaShapeElementNameSetCache.Remove(shape);
    }

    private static bool TryFindVanillaShapeElementSlot(Shape shape, string elementName, out ShapeElement? element, out ShapeElement? parent, out int index)
    {
        element = null;
        parent = null;
        index = -1;
        if (shape.Elements == null || string.IsNullOrWhiteSpace(elementName)) return false;

        for (int rootIndex = 0; rootIndex < shape.Elements.Length; rootIndex++)
        {
            ShapeElement root = shape.Elements[rootIndex];
            if (string.Equals(root.Name, elementName, StringComparison.OrdinalIgnoreCase))
            {
                element = root;
                index = rootIndex;
                return true;
            }

            if (TryFindVanillaShapeElementSlotRecursive(root, elementName, out element, out parent, out index))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindVanillaShapeElementSlotRecursive(ShapeElement current, string elementName, out ShapeElement? element, out ShapeElement? parent, out int index)
    {
        element = null;
        parent = null;
        index = -1;
        ShapeElement[] children = current.Children ?? [];
        for (int childIndex = 0; childIndex < children.Length; childIndex++)
        {
            ShapeElement child = children[childIndex];
            if (string.Equals(child.Name, elementName, StringComparison.OrdinalIgnoreCase))
            {
                element = child;
                parent = current;
                index = childIndex;
                return true;
            }

            if (TryFindVanillaShapeElementSlotRecursive(child, elementName, out element, out parent, out index))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReserveVanillaShapeElementName(Shape shape, string desired)
    {
        HashSet<string> names = new((shape.Elements ?? []).SelectMany(GetShapeElementNamesRecursive), StringComparer.OrdinalIgnoreCase);
        desired = string.IsNullOrWhiteSpace(desired) ? "element_cut" : desired.Trim();
        if (names.Add(desired)) return desired;

        for (int counter = 2; counter < 10000; counter++)
        {
            string candidate = $"{desired}_{counter}";
            if (names.Add(candidate)) return candidate;
        }

        return $"{desired}_{Guid.NewGuid():N}";
    }

    private bool DrawVanillaViewportGizmo(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, ImDrawListPtr drawList, NVector2 min, NVector2 max, bool hovered)
    {
        if (GizmoMode == TransformGizmoMode.None) return false;
        if (GizmoMode == TransformGizmoMode.Cut) return false;
        if (!TryGetVanillaViewportGizmoTarget(row, out VanillaShapeAnimationEntry? entry, out VanillaAnimation? animation, out AnimationKeyFrame? keyFrame, out AnimationKeyFrameElement? element)) return false;
        if (!TryGetVanillaGizmoProjection(scene, element, _vanillaSelection.ElementName, min, max, out VanillaGizmoProjection projection)) return false;

        TransformGizmoAxis hoveredAxis = hovered ? PickVanillaViewportGizmoAxis(projection) : TransformGizmoAxis.None;
        if (hoveredAxis != TransformGizmoAxis.None)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && hoveredAxis != TransformGizmoAxis.None)
        {
            _vanillaViewportGizmoDragAxis = hoveredAxis;
            _vanillaViewportGizmoDragMode = GizmoMode;
            _vanillaViewportGizmoDragMouseStart = ImGui.GetMousePos();
            _vanillaViewportGizmoDragVector = GetVanillaGizmoDragVector(projection, hoveredAxis, _vanillaViewportGizmoDragMouseStart);
            _vanillaViewportGizmoDragModelDirection = GetVanillaViewportMoveModelDirection(projection, hoveredAxis);
            _vanillaViewportGizmoDragScale = Math.Max(1f, projection.Scale);
            _vanillaViewportGizmoDragTranslationBasis = projection.TranslationBasis;
            _vanillaViewportGizmoDragCenter = projection.Center;
            _vanillaViewportGizmoDragLastAngleRadians = GetVanillaViewportGizmoMouseAngle(projection.Center, _vanillaViewportGizmoDragMouseStart);
            _vanillaViewportGizmoDragAccumulatedDegrees = 0;
            _vanillaViewportGizmoDragRingScreenSign = GizmoMode == TransformGizmoMode.Rotate
                ? GetVanillaViewportGizmoRingScreenSign(projection, hoveredAxis)
                : -1.0;
            _vanillaViewportGizmoDragStartValue = GetVanillaGizmoAxisValue(element, GizmoMode, hoveredAxis);
            _vanillaViewportGizmoDragStartOffsetX = element.OffsetX ?? 0;
            _vanillaViewportGizmoDragStartOffsetY = element.OffsetY ?? 0;
            _vanillaViewportGizmoDragStartOffsetZ = element.OffsetZ ?? 0;
            _vanillaViewportGizmoDragStartRotationX = element.RotationX ?? 0;
            _vanillaViewportGizmoDragStartRotationY = element.RotationY ?? 0;
            _vanillaViewportGizmoDragStartRotationZ = element.RotationZ ?? 0;
            _vanillaViewportGizmoDragBaseRotationDegrees = projection.BaseRotationDegrees;
            _vanillaViewportGizmoDragRotationParentBasis = projection.RotationParentBasis;
            _vanillaViewportGizmoDragSpace = GizmoSpace;
            _vanillaViewportGizmoDragRowKey = row.Key;
            _vanillaViewportGizmoDragKeyFrameIndex = _vanillaSelection.KeyFrameIndex;
            _vanillaViewportGizmoDragElementName = _vanillaSelection.ElementName;
            _vanillaHistory.BeginEdit(entry.Document, _vanillaHistory.Capture(entry.Document, $"Gizmo {_vanillaSelection.ElementName}", row));
        }

        if (_vanillaViewportGizmoDragAxis != TransformGizmoAxis.None)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) ||
                _vanillaViewportGizmoDragRowKey != row.Key ||
                _vanillaViewportGizmoDragKeyFrameIndex != _vanillaSelection.KeyFrameIndex ||
                !string.Equals(_vanillaViewportGizmoDragElementName, _vanillaSelection.ElementName, StringComparison.OrdinalIgnoreCase))
            {
                CommitPendingVanillaHistory(entry.Document);
                FlushPendingVanillaPreviewMeshRebuild(row);
                ClearVanillaViewportGizmoDrag();
            }
            else
            {
                ApplyVanillaViewportGizmoDrag(row, entry, keyFrame, element, _vanillaViewportGizmoDragMode, _vanillaViewportGizmoDragAxis, _vanillaViewportGizmoDragVector, projection);
            }
        }

        drawList.PushClipRect(min, max, true);
        uint boundsColor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.15f, 0.78f, 1f, 0.72f));
        uint helperColor = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 0.58f));
        uint labelColor = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f));
        DrawVanillaViewportGizmoBounds(drawList, projection, boundsColor, helperColor);
        DrawVanillaViewportGizmoAxes(drawList, projection, hoveredAxis);
        drawList.AddText(projection.Center + new NVector2(8f, 8f), labelColor, _vanillaSelection.ElementName);
        drawList.PopClipRect();
        return hoveredAxis != TransformGizmoAxis.None || _vanillaViewportGizmoDragAxis != TransformGizmoAxis.None;
    }

    private void DrawVanillaViewportElementPicker(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, ImDrawListPtr drawList, NVector2 min, NVector2 max, bool hovered, bool suppressClick)
    {
        if (!hovered || _vanillaViewportGizmoDragAxis != TransformGizmoAxis.None) return;
        if (!TryPickVanillaViewportElement(scene, min, max, ImGui.GetMousePos(), out VanillaViewportElementHit hit)) return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        drawList.PushClipRect(min, max, true);
        bool manualChainHit = _vanillaIkMode == VanillaIkChainMode.ManualOverride && ContainsVanillaIkChainElement(hit.ElementName);
        uint boundsColor = manualChainHit
            ? ImGui.ColorConvertFloat4ToU32(new NVector4(0.42f, 0.86f, 1f, 0.95f))
            : ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.86f, 0.36f, 0.92f));
        uint labelColor = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f));
        DrawVanillaViewportBoxBounds(drawList, hit.BoundsCorners, boundsColor, 2.2f);
        string action = _vanillaIkMode == VanillaIkChainMode.ManualOverride ? "manual IK" : "select";
        drawList.AddText(hit.Center + new NVector2(8f, -18f), labelColor, $"{hit.ElementName} ({action})");
        drawList.PopClipRect();

        if (suppressClick || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;

        _vanillaSelection.ElementName = hit.ElementName;
        if (_vanillaIkMode == VanillaIkChainMode.ManualOverride)
        {
            ToggleVanillaIkChainElement(hit.ElementName);
        }
        else
        {
            ClearVanillaViewportGizmoDrag();
            _vanillaStatus = $"Selected {hit.ElementName}.";
        }
    }

    private bool TryPickVanillaViewportElement(VanillaAnimationPreviewScene scene, NVector2 min, NVector2 max, NVector2 mouse, out VanillaViewportElementHit hit)
    {
        hit = default;
        float width = Math.Max(1f, max.X - min.X);
        float height = Math.Max(1f, max.Y - min.Y);
        VanillaPreviewCameraState camera = BuildVanillaPreviewCamera(scene, width, height, _vanillaViewportYaw, _vanillaViewportPitch, _vanillaViewportZoom, _vanillaViewportPanX, _vanillaViewportPanY, VanillaPreviewMode.Orbit);
        bool found = false;

        foreach (ElementPose root in scene.Animator.RootPoses ?? [])
        {
            CollectVanillaViewportElementHits(root, camera, min, width, height, mouse, depth: 0, ref found, ref hit);
        }

        return found;
    }

    private static void CollectVanillaViewportElementHits(ElementPose pose, VanillaPreviewCameraState camera, NVector2 min, float width, float height, NVector2 mouse, int depth, ref bool found, ref VanillaViewportElementHit best)
    {
        if (TryBuildVanillaViewportElementHit(pose, camera, min, width, height, mouse, depth, out VanillaViewportElementHit candidate) &&
            (!found || IsBetterVanillaViewportElementHit(candidate, best)))
        {
            best = candidate;
            found = true;
        }

        if (pose.ChildElementPoses == null) return;
        foreach (ElementPose child in pose.ChildElementPoses)
        {
            CollectVanillaViewportElementHits(child, camera, min, width, height, mouse, depth + 1, ref found, ref best);
        }
    }

    private static bool IsBetterVanillaViewportElementHit(VanillaViewportElementHit candidate, VanillaViewportElementHit current)
    {
        if (candidate.Distance < current.Distance - 0.01) return true;
        if (candidate.Distance > current.Distance + 0.01) return false;
        if (candidate.HierarchyDepth != current.HierarchyDepth) return candidate.HierarchyDepth > current.HierarchyDepth;
        return candidate.ScreenArea < current.ScreenArea;
    }

    private static bool TryBuildVanillaViewportElementHit(ElementPose pose, VanillaPreviewCameraState camera, NVector2 min, float width, float height, NVector2 mouse, int depth, out VanillaViewportElementHit hit)
    {
        hit = default;
        if (pose.ForElement == null || string.IsNullOrWhiteSpace(pose.ForElement.Name)) return false;

        Matrixf elementModel = AnimationElementPicking.BuildPoseModelMatrix(camera.Model, pose);
        if (!TryIntersectVanillaViewportElementBox(camera, elementModel, pose.ForElement, min, width, height, mouse, out double distance)) return false;

        NVector2[] bounds = BuildVanillaElementBounds3D(camera, elementModel, pose.ForElement, min, width, height, out bool hasVisualCenter, out NVector2 visualCenter);
        if (bounds.Length < 8 || !hasVisualCenter) return false;

        hit = new(
            pose.ForElement.Name,
            bounds,
            visualCenter,
            distance,
            GetProjectedBoundsArea(bounds),
            depth);
        return true;
    }

    private static bool TryIntersectVanillaViewportElementBox(VanillaPreviewCameraState camera, Matrixf elementModel, ShapeElement element, NVector2 min, float width, float height, NVector2 mouse, out double distance)
    {
        return AnimationElementPicking.TryIntersectScreenLocalBox(camera.ProjectionView, elementModel, element, min, width, height, mouse, out distance);
    }

    private static float GetProjectedBoundsArea(NVector2[] bounds)
    {
        if (bounds.Length == 0) return float.MaxValue;

        float minX = bounds.Min(point => point.X);
        float minY = bounds.Min(point => point.Y);
        float maxX = bounds.Max(point => point.X);
        float maxY = bounds.Max(point => point.Y);
        return Math.Max(0.001f, (maxX - minX) * (maxY - minY));
    }

    private static void DrawVanillaViewportGizmoBounds(ImDrawListPtr drawList, VanillaGizmoProjection projection, uint boundsColor, uint helperColor)
    {
        if (projection.BoundsCorners.Length >= 8)
        {
            DrawVanillaViewportBoxBounds(drawList, projection.BoundsCorners, boundsColor, 2f);
        }

        if (projection.HasVisualCenter && (projection.VisualCenter - projection.Center).Length() > 12f)
        {
            DrawVanillaViewportLine(drawList, projection.Center, projection.VisualCenter, helperColor, 2f);
            drawList.AddCircleFilled(projection.VisualCenter, 4f, helperColor, 16);
        }
    }

    private static void DrawVanillaViewportBoxBounds(ImDrawListPtr drawList, NVector2[] points, uint color, float thickness)
    {
        if (points.Length < 8) return;

        DrawVanillaViewportLine(drawList, points[0], points[1], color, thickness);
        DrawVanillaViewportLine(drawList, points[1], points[2], color, thickness);
        DrawVanillaViewportLine(drawList, points[2], points[3], color, thickness);
        DrawVanillaViewportLine(drawList, points[3], points[0], color, thickness);
        DrawVanillaViewportLine(drawList, points[4], points[5], color, thickness);
        DrawVanillaViewportLine(drawList, points[5], points[6], color, thickness);
        DrawVanillaViewportLine(drawList, points[6], points[7], color, thickness);
        DrawVanillaViewportLine(drawList, points[7], points[4], color, thickness);
        DrawVanillaViewportLine(drawList, points[0], points[4], color, thickness);
        DrawVanillaViewportLine(drawList, points[1], points[5], color, thickness);
        DrawVanillaViewportLine(drawList, points[2], points[6], color, thickness);
        DrawVanillaViewportLine(drawList, points[3], points[7], color, thickness);
    }

    private void DrawVanillaViewportGizmoAxes(ImDrawListPtr drawList, VanillaGizmoProjection projection, TransformGizmoAxis hoveredAxis)
    {
        uint red = ImGui.ColorConvertFloat4ToU32(new NVector4(0.95f, 0.18f, 0.14f, 1f));
        uint green = ImGui.ColorConvertFloat4ToU32(new NVector4(0.20f, 0.84f, 0.28f, 1f));
        uint blue = ImGui.ColorConvertFloat4ToU32(new NVector4(0.22f, 0.48f, 1f, 1f));
        uint white = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f));
        uint xColor = hoveredAxis == TransformGizmoAxis.X || _vanillaViewportGizmoDragAxis == TransformGizmoAxis.X ? white : red;
        uint yColor = hoveredAxis == TransformGizmoAxis.Y || _vanillaViewportGizmoDragAxis == TransformGizmoAxis.Y ? white : green;
        uint zColor = hoveredAxis == TransformGizmoAxis.Z || _vanillaViewportGizmoDragAxis == TransformGizmoAxis.Z ? white : blue;

        drawList.AddCircleFilled(projection.Center, 4.5f, white, 16);

        if (GizmoMode == TransformGizmoMode.Rotate)
        {
            DrawVanillaViewportGizmoRing(drawList, projection.RingX, xColor);
            DrawVanillaViewportGizmoRing(drawList, projection.RingY, yColor);
            DrawVanillaViewportGizmoRing(drawList, projection.RingZ, zColor);
            return;
        }

        DrawVanillaViewportGizmoAxis(drawList, projection.Center, projection.AxisX, xColor);
        DrawVanillaViewportGizmoAxis(drawList, projection.Center, projection.AxisY, yColor);
        DrawVanillaViewportGizmoAxis(drawList, projection.Center, projection.AxisZ, zColor);

        if (GizmoMode == TransformGizmoMode.Scale)
        {
            DrawVanillaViewportGizmoCube(drawList, projection.Center + projection.AxisX, xColor);
            DrawVanillaViewportGizmoCube(drawList, projection.Center + projection.AxisY, yColor);
            DrawVanillaViewportGizmoCube(drawList, projection.Center + projection.AxisZ, zColor);
        }
        else
        {
            DrawVanillaViewportGizmoArrow(drawList, projection.Center, projection.AxisX, xColor);
            DrawVanillaViewportGizmoArrow(drawList, projection.Center, projection.AxisY, yColor);
            DrawVanillaViewportGizmoArrow(drawList, projection.Center, projection.AxisZ, zColor);
        }
    }

    private static void DrawVanillaViewportGizmoAxis(ImDrawListPtr drawList, NVector2 center, NVector2 axis, uint color)
    {
        DrawVanillaViewportLine(drawList, center, center + axis, color, 2.4f);
    }

    private static void DrawVanillaViewportGizmoArrow(ImDrawListPtr drawList, NVector2 center, NVector2 axis, uint color)
    {
        NVector2 tip = center + axis;
        NVector2 dir = NormalizeOrDefault(axis, new NVector2(1f, 0f));
        NVector2 normal = new(-dir.Y, dir.X);
        drawList.AddTriangleFilled(tip, tip - dir * 13f + normal * 5.5f, tip - dir * 13f - normal * 5.5f, color);
    }

    private static void DrawVanillaViewportGizmoCube(ImDrawListPtr drawList, NVector2 center, uint color)
    {
        NVector2 half = new(5.5f, 5.5f);
        drawList.AddRectFilled(center - half, center + half, color, 1.5f);
    }

    private static void DrawVanillaViewportGizmoRing(ImDrawListPtr drawList, NVector2[] points, uint color)
    {
        for (int i = 1; i < points.Length; i++)
        {
            DrawVanillaViewportLine(drawList, points[i - 1], points[i], color, 2.4f);
        }
    }

    private static void DrawVanillaViewportLine(ImDrawListPtr drawList, NVector2 start, NVector2 end, uint color, float thickness)
    {
        if (!IsFinite(start.X) || !IsFinite(start.Y) || !IsFinite(end.X) || !IsFinite(end.Y)) return;
        drawList.AddLine(start, end, color, thickness);
    }

    private TransformGizmoAxis PickVanillaViewportGizmoAxis(VanillaGizmoProjection projection)
    {
        NVector2 mouse = ImGui.GetMousePos();
        if (GizmoMode == TransformGizmoMode.Rotate)
        {
            float dx = DistanceToPolyline(mouse, projection.RingX);
            float dy = DistanceToPolyline(mouse, projection.RingY);
            float dz = DistanceToPolyline(mouse, projection.RingZ);
            float min = Math.Min(dx, Math.Min(dy, dz));
            if (min > 14f) return TransformGizmoAxis.None;
            if (min == dx) return TransformGizmoAxis.X;
            if (min == dy) return TransformGizmoAxis.Y;
            return TransformGizmoAxis.Z;
        }

        TransformGizmoAxis picked = TransformGizmoAxis.None;
        float best = 14f;
        TestAxis(TransformGizmoAxis.X, projection.AxisX);
        TestAxis(TransformGizmoAxis.Y, projection.AxisY);
        TestAxis(TransformGizmoAxis.Z, projection.AxisZ);
        return picked;

        void TestAxis(TransformGizmoAxis axis, NVector2 vector)
        {
            float distance = DistanceToSegment(mouse, projection.Center, projection.Center + vector);
            if (distance < best)
            {
                best = distance;
                picked = axis;
            }
        }
    }

    private NVector2 GetVanillaGizmoDragVector(VanillaGizmoProjection projection, TransformGizmoAxis axis, NVector2 mouse)
    {
        if (GizmoMode == TransformGizmoMode.Rotate)
        {
            NVector2 radial = mouse - projection.Center;
            NVector2 tangent = new(-radial.Y, radial.X);
            return NormalizeOrDefault(tangent, GetVanillaProjectedAxis(projection, axis));
        }

        return NormalizeOrDefault(GetVanillaProjectedAxis(projection, axis), new NVector2(1f, 0f));
    }

    private static NVector2 GetVanillaProjectedAxis(VanillaGizmoProjection projection, TransformGizmoAxis axis)
    {
        return axis switch
        {
            TransformGizmoAxis.X => projection.AxisX,
            TransformGizmoAxis.Y => projection.AxisY,
            TransformGizmoAxis.Z => projection.AxisZ,
            _ => projection.AxisX
        };
    }

    private static double GetVanillaViewportGizmoRingScreenSign(VanillaGizmoProjection projection, TransformGizmoAxis axis)
    {
        NVector2[] points = axis switch
        {
            TransformGizmoAxis.X => projection.RingX,
            TransformGizmoAxis.Y => projection.RingY,
            TransformGizmoAxis.Z => projection.RingZ,
            _ => []
        };

        for (int index = 1; index < points.Length; index++)
        {
            NVector2 from = points[index - 1] - projection.Center;
            NVector2 to = points[index] - projection.Center;
            float cross = from.X * to.Y - from.Y * to.X;
            if (Math.Abs(cross) > 0.001f)
            {
                double sign = Math.Sign(cross);
                return axis == TransformGizmoAxis.Y ? -sign : sign;
            }
        }

        return -1.0;
    }

    private bool ApplyVanillaViewportGizmoDrag(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, AnimationKeyFrameElement element, TransformGizmoMode mode, TransformGizmoAxis axis, NVector2 axisVector, VanillaGizmoProjection projection)
    {
        NVector2 direction = NormalizeOrDefault(axisVector, new NVector2(1f, 0f));
        NVector2 mouseDelta = ImGui.GetMousePos() - _vanillaViewportGizmoDragMouseStart;
        double projected = NVector2.Dot(mouseDelta, direction);

        switch (mode)
        {
            case TransformGizmoMode.Move:
                return ApplyVanillaViewportMoveGizmoDrag(row, entry, keyFrame, element, axis, projected, projection);
            case TransformGizmoMode.Scale:
            {
                element = GetOrCreateVanillaEditableKeyFrameElement(keyFrame, _vanillaSelection.ElementName, element);
                double value = _vanillaViewportGizmoDragStartValue;
                value += projected / Math.Max(1f, projection.Scale) * 16.0;
                value = SnapVanillaGizmoValue(value, Math.Max(0.001, TransformGizmoIncrement * 16.0));
                if (Math.Abs(value - GetVanillaGizmoAxisValue(element, mode, axis)) < 0.0001) return false;
                SetVanillaGizmoAxisValue(element, mode, axis, value);
                break;
            }
            case TransformGizmoMode.Rotate:
            {
                element = GetOrCreateVanillaEditableKeyFrameElement(keyFrame, _vanillaSelection.ElementName, element);
                double deltaDegrees = SnapVanillaGizmoValue(UpdateVanillaViewportGizmoRingDrag(), Math.Max(0.001, TransformGizmoIncrement));
                if (!ApplyVanillaViewportRotationGizmoDrag(element, axis, deltaDegrees)) return false;
                break;
            }
            default:
                return false;
        }

        ApplyVanillaElementEdit(row, entry, keyFrame, _vanillaSelection.ElementName);
        return true;
    }

    private bool ApplyVanillaViewportMoveGizmoDrag(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, AnimationKeyFrameElement element, TransformGizmoAxis axis, double projected, VanillaGizmoProjection projection)
    {
        double modelDelta = projected / Math.Max(1f, _vanillaViewportGizmoDragScale);
        modelDelta = SnapVanillaGizmoValue(modelDelta, Math.Max(0.001, TransformGizmoIncrement));
        NVector3 modelDeltaVector = GetVanillaViewportMoveModelDelta(projection, axis, modelDelta);
        NVector3 offsetDelta = _vanillaViewportGizmoDragTranslationBasis.ModelToOffsetDelta(modelDeltaVector) * 16f;
        double offsetX = _vanillaViewportGizmoDragStartOffsetX + offsetDelta.X;
        double offsetY = _vanillaViewportGizmoDragStartOffsetY + offsetDelta.Y;
        double offsetZ = _vanillaViewportGizmoDragStartOffsetZ + offsetDelta.Z;

        if (Math.Abs(offsetX - (element.OffsetX ?? 0)) < 0.0001 &&
            Math.Abs(offsetY - (element.OffsetY ?? 0)) < 0.0001 &&
            Math.Abs(offsetZ - (element.OffsetZ ?? 0)) < 0.0001)
        {
            return false;
        }

        AnimationKeyFrameElement desiredElement = CloneElement(element);
        SetVanillaGizmoMoveOffsetValues(desiredElement, offsetX, offsetY, offsetZ);
        if (_vanillaIkFollowMove)
        {
            return TryApplyVanillaViewportIkMove(row, entry, desiredElement, modelDeltaVector);
        }

        element = GetOrCreateVanillaEditableKeyFrameElement(keyFrame, _vanillaSelection.ElementName, element);
        SetVanillaGizmoMoveOffsetValues(element, offsetX, offsetY, offsetZ);
        ApplyVanillaElementEdit(row, entry, keyFrame, _vanillaSelection.ElementName);
        return true;
    }

    private bool ApplyVanillaViewportRotationGizmoDrag(AnimationKeyFrameElement element, TransformGizmoAxis axis, double deltaDegrees)
    {
        Vec3d baseRotation = _vanillaViewportGizmoDragBaseRotationDegrees;
        RigIkMatrix3 startLocalRotation = RigIkMatrix3.FromEulerDegrees(
            baseRotation.X + _vanillaViewportGizmoDragStartRotationX,
            baseRotation.Y + _vanillaViewportGizmoDragStartRotationY,
            baseRotation.Z + _vanillaViewportGizmoDragStartRotationZ);

        RigIkMatrix3 axisRotation = RigIkMatrix3.FromAxisAngle(GetVanillaCanonicalGizmoAxis(axis), deltaDegrees * GameMath.DEG2RAD);
        RigIkMatrix3 newLocalRotation;
        if (_vanillaViewportGizmoDragSpace == TransformGizmoSpace.World)
        {
            RigIkMatrix3 parent = _vanillaViewportGizmoDragRotationParentBasis.Orthonormalized();
            RigIkMatrix3 newWorldRotation = axisRotation.Mul(parent.Mul(startLocalRotation));
            newLocalRotation = parent.Inverted().Mul(newWorldRotation).Orthonormalized();
        }
        else
        {
            newLocalRotation = startLocalRotation.Mul(axisRotation).Orthonormalized();
        }

        Vec3d euler = newLocalRotation.ToEulerDegrees();
        double rotationX = NormalizeVanillaDegrees(euler.X - baseRotation.X);
        double rotationY = NormalizeVanillaDegrees(euler.Y - baseRotation.Y);
        double rotationZ = NormalizeVanillaDegrees(euler.Z - baseRotation.Z);

        if (Math.Abs(rotationX - (element.RotationX ?? 0)) < 0.0001 &&
            Math.Abs(rotationY - (element.RotationY ?? 0)) < 0.0001 &&
            Math.Abs(rotationZ - (element.RotationZ ?? 0)) < 0.0001)
        {
            return false;
        }

        element.RotationX = rotationX;
        element.RotationY = rotationY;
        element.RotationZ = rotationZ;
        CompleteVanillaRotationGroup(element);
        return true;
    }

    private static AnimationKeyFrameElement GetOrCreateVanillaEditableKeyFrameElement(AnimationKeyFrame keyFrame, string elementName, AnimationKeyFrameElement fallback)
    {
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(elementName)) return fallback;
        if (keyFrame.Elements.TryGetValue(elementName, out AnimationKeyFrameElement? existing) && existing != null) return existing;

        keyFrame.Elements[elementName] = fallback;
        return fallback;
    }

    private NVector3 GetVanillaViewportMoveModelDelta(VanillaGizmoProjection projection, TransformGizmoAxis axis, double modelDelta)
    {
        NVector3 direction = _vanillaViewportGizmoDragAxis == axis && _vanillaViewportGizmoDragMode == TransformGizmoMode.Move
            ? _vanillaViewportGizmoDragModelDirection
            : GetVanillaViewportMoveModelDirection(projection, axis);

        return direction * (float)modelDelta;
    }

    private NVector3 GetVanillaViewportMoveModelDirection(VanillaGizmoProjection projection, TransformGizmoAxis axis)
    {
        NVector3 direction = GizmoSpace == TransformGizmoSpace.World
            ? axis switch
            {
                TransformGizmoAxis.X => NVector3.UnitX,
                TransformGizmoAxis.Y => NVector3.UnitY,
                TransformGizmoAxis.Z => NVector3.UnitZ,
                _ => NVector3.UnitX
            }
            : axis switch
            {
                TransformGizmoAxis.X => projection.AxisXModel,
                TransformGizmoAxis.Y => projection.AxisYModel,
                TransformGizmoAxis.Z => projection.AxisZModel,
                _ => projection.AxisXModel
            };

        return NormalizeOrDefault(direction, NVector3.UnitX);
    }

    private static Vec3d GetVanillaCanonicalGizmoAxis(TransformGizmoAxis axis)
    {
        return axis switch
        {
            TransformGizmoAxis.X => new Vec3d(1, 0, 0),
            TransformGizmoAxis.Y => new Vec3d(0, 1, 0),
            TransformGizmoAxis.Z => new Vec3d(0, 0, 1),
            _ => new Vec3d(1, 0, 0)
        };
    }

    private static void SetVanillaGizmoMoveOffsetValues(AnimationKeyFrameElement element, double offsetX, double offsetY, double offsetZ)
    {
        element.OffsetX = offsetX;
        element.OffsetY = offsetY;
        element.OffsetZ = offsetZ;
        CompleteVanillaPositionGroup(element);
    }

    private double UpdateVanillaViewportGizmoRingDrag()
    {
        NVector2 radial = ImGui.GetMousePos() - _vanillaViewportGizmoDragCenter;
        if (radial.LengthSquared() < 16f)
        {
            return _vanillaViewportGizmoDragAccumulatedDegrees;
        }

        double angle = Math.Atan2(radial.Y, radial.X);
        double delta = NormalizeVanillaRadians(angle - _vanillaViewportGizmoDragLastAngleRadians);
        _vanillaViewportGizmoDragLastAngleRadians = angle;
        double sign = Math.Abs(_vanillaViewportGizmoDragRingScreenSign) < 0.001 ? -1.0 : _vanillaViewportGizmoDragRingScreenSign;
        _vanillaViewportGizmoDragAccumulatedDegrees += delta * 180.0 / Math.PI / sign;
        return _vanillaViewportGizmoDragAccumulatedDegrees;
    }

    private void DrawVanillaElementGizmoControls()
    {
        ImGui.SeparatorText("Gizmo");
        if (ImGui.RadioButton("Move##vanilla-gizmo-mode", GizmoMode == TransformGizmoMode.Move)) GizmoMode = TransformGizmoMode.Move;
        ImGui.SameLine();
        if (ImGui.RadioButton("Rotate##vanilla-gizmo-mode", GizmoMode == TransformGizmoMode.Rotate)) GizmoMode = TransformGizmoMode.Rotate;
        ImGui.SameLine();
        if (ImGui.RadioButton("Scale##vanilla-gizmo-mode", GizmoMode == TransformGizmoMode.Scale)) GizmoMode = TransformGizmoMode.Scale;
        ImGui.SameLine();
        if (ImGui.RadioButton("Off##vanilla-gizmo-mode", GizmoMode == TransformGizmoMode.None)) GizmoMode = TransformGizmoMode.None;

        if (GizmoSpace == TransformGizmoSpace.Parent) GizmoSpace = TransformGizmoSpace.World;
        if (ImGui.RadioButton("World axes##vanilla-gizmo-space", GizmoSpace == TransformGizmoSpace.World)) GizmoSpace = TransformGizmoSpace.World;
        ImGui.SameLine();
        if (ImGui.RadioButton("Local axes##vanilla-gizmo-space", GizmoSpace == TransformGizmoSpace.Local)) GizmoSpace = TransformGizmoSpace.Local;

        bool snap = IncludeGizmoInIncrement;
        if (ImGui.Checkbox("Snap drag##vanilla-gizmo-snap", ref snap))
        {
            IncludeGizmoInIncrement = snap;
        }

        ImGui.SameLine();
        float increment = TransformGizmoIncrement;
        ImGui.SetNextItemWidth(90);
        if (ImGui.DragFloat("Increment##vanilla-gizmo-increment", ref increment, 0.01f, 0.001f, 90f))
        {
            TransformGizmoIncrement = Math.Max(0.001f, increment);
        }
    }

    private bool TryGetVanillaViewportGizmoTarget(VanillaBrowserRow row, out VanillaShapeAnimationEntry entry, out VanillaAnimation animation, out AnimationKeyFrame keyFrame, out AnimationKeyFrameElement element)
    {
        entry = null!;
        animation = null!;
        keyFrame = null!;
        element = null!;

        VanillaShapeAnimationEntry? selectedEntry = row.ShapeAnimation ?? row.MetadataEntry?.ResolveCurrentShape();
        if (selectedEntry == null || selectedEntry.Animation.KeyFrames == null || selectedEntry.Animation.KeyFrames.Length == 0) return false;

        entry = selectedEntry;
        animation = selectedEntry.Animation;
        _vanillaSelection.KeyFrameIndex = Math.Clamp(_vanillaSelection.KeyFrameIndex, 0, animation.KeyFrames.Length - 1);
        keyFrame = animation.KeyFrames[_vanillaSelection.KeyFrameIndex];
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(_vanillaSelection.ElementName))
        {
            if (keyFrame.Elements.Count == 0) return false;
            _vanillaSelection.ElementName = keyFrame.Elements.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).First();
        }

        if (keyFrame.Elements.TryGetValue(_vanillaSelection.ElementName, out AnimationKeyFrameElement? found) && found != null)
        {
            element = found;
            return true;
        }

        if (IsKnownVanillaShapeElement(selectedEntry.Document, _vanillaSelection.ElementName))
        {
            element = new AnimationKeyFrameElement();
            return true;
        }

        if (keyFrame.Elements.Count == 0) return false;
        _vanillaSelection.ElementName = keyFrame.Elements.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).First();
        if (!keyFrame.Elements.TryGetValue(_vanillaSelection.ElementName, out found) || found == null) return false;
        element = found;
        return true;
    }

    private bool TryGetVanillaGizmoProjection(VanillaAnimationPreviewScene scene, AnimationKeyFrameElement keyFrameElement, string elementName, NVector2 min, NVector2 max, out VanillaGizmoProjection projection)
    {
        projection = default;
        float width = Math.Max(1f, max.X - min.X);
        float height = Math.Max(1f, max.Y - min.Y);

        ShapeElement? shapeElement = FindShapeElement(scene.Shape, elementName);
        ElementPose? pose = scene.Animator.GetPosebyName(elementName);
        if (pose?.ForElement == null && shapeElement != null)
        {
            pose = scene.Animator.GetPosebyName(shapeElement.Name);
        }
        if (pose?.ForElement == null) return false;

        VanillaPreviewCameraState camera = BuildVanillaPreviewCamera(scene, width, height, _vanillaViewportYaw, _vanillaViewportPitch, _vanillaViewportZoom, _vanillaViewportPanX, _vanillaViewportPanY, GetVanillaEffectivePreviewMode(scene));
        Matrixf elementModel = BuildVanillaElementModelMatrix(camera.Model, pose);
        NVector3 elementPoint = GetVanillaGizmoLocalPoint(pose);
        if (!ProjectVanillaPreviewPoint(elementModel, camera, elementPoint, min, width, height, out NVector2 center)) return false;

        VanillaGizmoTranslationBasis translationBasis = BuildVanillaGizmoTranslationBasis(pose);
        RigIkMatrix3 rotationParentBasis = RigIkMatrix3.Identity;
        Vec3d baseRotationDegrees = new(pose.ForElement.RotationX, pose.ForElement.RotationY, pose.ForElement.RotationZ);
        if (TryGetVanillaIkPoseInfo(scene, elementName, out VanillaIkPoseInfo poseInfo, out _))
        {
            rotationParentBasis = poseInfo.ParentWorldRotation;
            baseRotationDegrees = poseInfo.BaseRotationDegrees;
        }

        float modelAxisLength = Math.Clamp(Math.Max(Math.Max(scene.ModelWidth, scene.ModelHeight), scene.ModelDepth) * 0.16f, 0.12f, 0.85f);
        float modelRingRadius = Math.Clamp(modelAxisLength * 0.95f, 0.10f, 0.80f);
        NVector2 axisX;
        NVector2 axisY;
        NVector2 axisZ;
        NVector2[] ringX;
        NVector2[] ringY;
        NVector2[] ringZ;
        if (GizmoSpace == TransformGizmoSpace.World)
        {
            NVector3 centerWorld = TransformVanillaPreviewPoint(elementModel, elementPoint);
            NVector3 worldX = TransformVanillaPreviewDirection(camera.Model, new NVector3(modelAxisLength, 0f, 0f));
            NVector3 worldY = TransformVanillaPreviewDirection(camera.Model, new NVector3(0f, modelAxisLength, 0f));
            NVector3 worldZ = TransformVanillaPreviewDirection(camera.Model, new NVector3(0f, 0f, modelAxisLength));
            if (!ProjectVanillaPreviewWorldPoint(camera, centerWorld + worldX, min, width, height, out NVector2 axisXEnd)) return false;
            if (!ProjectVanillaPreviewWorldPoint(camera, centerWorld + worldY, min, width, height, out NVector2 axisYEnd)) return false;
            if (!ProjectVanillaPreviewWorldPoint(camera, centerWorld + worldZ, min, width, height, out NVector2 axisZEnd)) return false;
            axisX = axisXEnd - center;
            axisY = axisYEnd - center;
            axisZ = axisZEnd - center;

            float ringScale = modelRingRadius / Math.Max(0.0001f, modelAxisLength);
            ringX = BuildVanillaViewportGizmoRingWorld(camera, centerWorld, worldY * ringScale, worldZ * ringScale, min, width, height);
            ringY = BuildVanillaViewportGizmoRingWorld(camera, centerWorld, worldX * ringScale, worldZ * ringScale, min, width, height);
            ringZ = BuildVanillaViewportGizmoRingWorld(camera, centerWorld, worldX * ringScale, worldY * ringScale, min, width, height);
        }
        else
        {
            if (!ProjectVanillaPreviewPoint(elementModel, camera, elementPoint + new NVector3(modelAxisLength, 0f, 0f), min, width, height, out NVector2 axisXEnd)) return false;
            if (!ProjectVanillaPreviewPoint(elementModel, camera, elementPoint + new NVector3(0f, modelAxisLength, 0f), min, width, height, out NVector2 axisYEnd)) return false;
            if (!ProjectVanillaPreviewPoint(elementModel, camera, elementPoint + new NVector3(0f, 0f, modelAxisLength), min, width, height, out NVector2 axisZEnd)) return false;
            axisX = axisXEnd - center;
            axisY = axisYEnd - center;
            axisZ = axisZEnd - center;
            ringX = BuildVanillaViewportGizmoRing(camera, elementModel, elementPoint, modelRingRadius, min, width, height, TransformGizmoAxis.X);
            ringY = BuildVanillaViewportGizmoRing(camera, elementModel, elementPoint, modelRingRadius, min, width, height, TransformGizmoAxis.Y);
            ringZ = BuildVanillaViewportGizmoRing(camera, elementModel, elementPoint, modelRingRadius, min, width, height, TransformGizmoAxis.Z);
        }

        float pixelScale = Math.Max(1f, (axisX.Length() + axisY.Length() + axisZ.Length()) / Math.Max(0.001f, modelAxisLength * 3f));
        NVector2[] bounds = BuildVanillaElementBounds3D(camera, elementModel, pose.ForElement, min, width, height, out bool hasVisualCenter, out NVector2 visualCenter);
        if (GizmoMode == TransformGizmoMode.Rotate && (ringX.Length == 0 || ringY.Length == 0 || ringZ.Length == 0)) return false;

        projection = new(
            center,
            pixelScale,
            axisX,
            axisY,
            axisZ,
            ringX,
            ringY,
            ringZ,
            bounds,
            hasVisualCenter,
            visualCenter,
            translationBasis,
            translationBasis.AxisX,
            translationBasis.AxisY,
            translationBasis.AxisZ,
            rotationParentBasis,
            baseRotationDegrees);
        return true;
    }

    private static VanillaGizmoTranslationBasis BuildVanillaGizmoTranslationBasis(ElementPose pose)
    {
        if (!TryBuildVanillaPoseModelMatrix(pose, out Matrixf poseMatrix))
        {
            return VanillaGizmoTranslationBasis.Identity;
        }

        NVector3 axisX = NormalizeOrDefault(TransformVanillaPreviewDirection(poseMatrix, NVector3.UnitX), NVector3.UnitX);
        NVector3 axisY = NormalizeOrDefault(TransformVanillaPreviewDirection(poseMatrix, NVector3.UnitY), NVector3.UnitY);
        NVector3 axisZ = NormalizeOrDefault(TransformVanillaPreviewDirection(poseMatrix, NVector3.UnitZ), NVector3.UnitZ);
        return new VanillaGizmoTranslationBasis(axisX, axisY, axisZ);
    }

    private static Matrixf BuildVanillaElementModelMatrix(Matrixf model, ElementPose pose)
    {
        Matrixf result = new();
        result.Set(model.Values);
        result.Mul(pose.AnimModelMatrix);
        return result;
    }

    private static Matrixf BuildVanillaElementModelViewMatrix(Matrixf modelView, ElementPose pose)
    {
        Matrixf result = new();
        result.Set(modelView.Values);
        result.Mul(pose.AnimModelMatrix);
        return result;
    }

    private static NVector3 GetVanillaGizmoLocalPoint(ElementPose pose)
    {
        ShapeElement element = pose.ForElement;
        double[]? rotationOrigin = element.RotationOrigin;
        double originX = rotationOrigin != null && rotationOrigin.Length > 0 ? rotationOrigin[0] : element.From?[0] ?? 0;
        double originY = rotationOrigin != null && rotationOrigin.Length > 1 ? rotationOrigin[1] : element.From?[1] ?? 0;
        double originZ = rotationOrigin != null && rotationOrigin.Length > 2 ? rotationOrigin[2] : element.From?[2] ?? 0;
        double fromX = element.From != null && element.From.Length > 0 ? element.From[0] : 0;
        double fromY = element.From != null && element.From.Length > 1 ? element.From[1] : 0;
        double fromZ = element.From != null && element.From.Length > 2 ? element.From[2] : 0;

        return new NVector3(
            (float)((originX - fromX) / 16.0 - pose.translateX),
            (float)((originY - fromY) / 16.0 - pose.translateY),
            (float)((originZ - fromZ) / 16.0 - pose.translateZ));
    }

    private static bool TryGetShapeElementRotationOrigin(ShapeElement element, out double x, out double y, out double z)
    {
        x = 0;
        y = 0;
        z = 0;
        if (element.RotationOrigin == null || element.RotationOrigin.Length < 3) return false;

        x = element.RotationOrigin[0] / 16.0;
        y = element.RotationOrigin[1] / 16.0;
        z = element.RotationOrigin[2] / 16.0;
        return true;
    }

    private static NVector2[] BuildVanillaElementBounds(Matrixf elementModelView, ShapeElement? element, out bool hasVisualCenter, out NVector2 visualCenter)
    {
        hasVisualCenter = false;
        visualCenter = default;
        if (element?.From == null || element.To == null || element.From.Length < 3 || element.To.Length < 3) return [];

        float centerX = (float)((element.To[0] - element.From[0]) / 32.0);
        float centerY = (float)((element.To[1] - element.From[1]) / 32.0);
        float centerZ = (float)((element.To[2] - element.From[2]) / 32.0);
        float halfX = Math.Max(0.08f, (float)Math.Abs(element.To[0] - element.From[0]) / 32f);
        float halfY = Math.Max(0.08f, (float)Math.Abs(element.To[1] - element.From[1]) / 32f);
        float halfZ = Math.Max(0.08f, (float)Math.Abs(element.To[2] - element.From[2]) / 32f);

        const float padding = 0.035f;
        halfX += padding;
        halfY += padding;
        halfZ += padding;

        float fromX = centerX - halfX;
        float fromY = centerY - halfY;
        float fromZ = centerZ - halfZ;
        float toX = centerX + halfX;
        float toY = centerY + halfY;
        float toZ = centerZ + halfZ;

        NVector3[] corners =
        {
            new(fromX, fromY, fromZ),
            new(toX, fromY, fromZ),
            new(toX, toY, fromZ),
            new(fromX, toY, fromZ),
            new(fromX, fromY, toZ),
            new(toX, fromY, toZ),
            new(toX, toY, toZ),
            new(fromX, toY, toZ)
        };

        visualCenter = ProjectVanillaGuiPoint(elementModelView, new NVector3(centerX, centerY, centerZ));
        hasVisualCenter = true;
        NVector2[] projected = new NVector2[corners.Length];
        for (int index = 0; index < corners.Length; index++)
        {
            projected[index] = ProjectVanillaGuiPoint(elementModelView, corners[index]);
        }

        return projected;
    }

    private static NVector2[] BuildVanillaElementBounds3D(VanillaPreviewCameraState camera, Matrixf elementModel, ShapeElement? element, NVector2 min, float width, float height, out bool hasVisualCenter, out NVector2 visualCenter)
    {
        hasVisualCenter = false;
        visualCenter = default;
        if (element?.From == null || element.To == null || element.From.Length < 3 || element.To.Length < 3) return [];

        float centerX = (float)((element.To[0] - element.From[0]) / 32.0);
        float centerY = (float)((element.To[1] - element.From[1]) / 32.0);
        float centerZ = (float)((element.To[2] - element.From[2]) / 32.0);
        float halfX = Math.Max(0.08f, (float)Math.Abs(element.To[0] - element.From[0]) / 32f);
        float halfY = Math.Max(0.08f, (float)Math.Abs(element.To[1] - element.From[1]) / 32f);
        float halfZ = Math.Max(0.08f, (float)Math.Abs(element.To[2] - element.From[2]) / 32f);

        const float padding = 0.035f;
        halfX += padding;
        halfY += padding;
        halfZ += padding;

        float fromX = centerX - halfX;
        float fromY = centerY - halfY;
        float fromZ = centerZ - halfZ;
        float toX = centerX + halfX;
        float toY = centerY + halfY;
        float toZ = centerZ + halfZ;

        NVector3[] corners =
        {
            new(fromX, fromY, fromZ),
            new(toX, fromY, fromZ),
            new(toX, toY, fromZ),
            new(fromX, toY, fromZ),
            new(fromX, fromY, toZ),
            new(toX, fromY, toZ),
            new(toX, toY, toZ),
            new(fromX, toY, toZ)
        };

        hasVisualCenter = ProjectVanillaPreviewPoint(elementModel, camera, new NVector3(centerX, centerY, centerZ), min, width, height, out visualCenter);
        NVector2[] projected = new NVector2[corners.Length];
        for (int index = 0; index < corners.Length; index++)
        {
            if (!ProjectVanillaPreviewPoint(elementModel, camera, corners[index], min, width, height, out projected[index]))
            {
                hasVisualCenter = false;
                visualCenter = default;
                return [];
            }
        }

        return projected;
    }

    private static NVector2[] BuildVanillaViewportGizmoRing(Matrixf modelView, NVector3 center, float modelRadius, TransformGizmoAxis axis)
    {
        const int segments = 72;
        NVector2[] points = new NVector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float angle = (float)(i / (double)segments * Math.PI * 2.0);
            float cos = (float)Math.Cos(angle) * modelRadius;
            float sin = (float)Math.Sin(angle) * modelRadius;
            NVector3 local = axis switch
            {
                TransformGizmoAxis.X => new NVector3(0f, cos, sin),
                TransformGizmoAxis.Y => new NVector3(cos, 0f, sin),
                TransformGizmoAxis.Z => new NVector3(cos, sin, 0f),
                _ => new NVector3(cos, sin, 0f)
            };
            points[i] = ProjectVanillaGuiPoint(modelView, center + local);
        }

        return points;
    }

    private static NVector2[] BuildVanillaViewportGizmoRing(VanillaPreviewCameraState camera, Matrixf elementModel, NVector3 center, float modelRadius, NVector2 min, float width, float height, TransformGizmoAxis axis)
    {
        const int segments = 72;
        NVector2[] points = new NVector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float angle = (float)(i / (double)segments * Math.PI * 2.0);
            float cos = (float)Math.Cos(angle) * modelRadius;
            float sin = (float)Math.Sin(angle) * modelRadius;
            NVector3 local = axis switch
            {
                TransformGizmoAxis.X => new NVector3(0f, cos, sin),
                TransformGizmoAxis.Y => new NVector3(cos, 0f, sin),
                TransformGizmoAxis.Z => new NVector3(cos, sin, 0f),
                _ => new NVector3(cos, sin, 0f)
            };
            if (!ProjectVanillaPreviewPoint(elementModel, camera, center + local, min, width, height, out points[i]))
            {
                return [];
            }
        }

        return points;
    }

    private static NVector2[] BuildVanillaViewportGizmoRingWorld(VanillaPreviewCameraState camera, NVector3 centerWorld, NVector3 axisAWorld, NVector3 axisBWorld, NVector2 min, float width, float height)
    {
        const int segments = 72;
        NVector2[] points = new NVector2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float angle = (float)(i / (double)segments * Math.PI * 2.0);
            NVector3 world = centerWorld + axisAWorld * (float)Math.Cos(angle) + axisBWorld * (float)Math.Sin(angle);
            if (!ProjectVanillaPreviewWorldPoint(camera, world, min, width, height, out points[i]))
            {
                return [];
            }
        }

        return points;
    }

    private static Matrixf BuildVanillaGuiModelMatrix(float posX, float posY, float posZ, float guiSize, float entityScale, float rotX, float rotY, float rotZ)
    {
        Matrixf matrix = new();
        matrix.Identity();
        matrix.Translate(posX, posY, posZ);
        matrix.Translate(guiSize, 2f * guiSize, 0f);
        matrix.RotateX(rotX);
        matrix.RotateY(rotY);
        matrix.RotateZ(rotZ);
        matrix.Scale(entityScale, entityScale, entityScale);
        matrix.Translate(-0.5f, 0f, -0.5f);
        return matrix;
    }

    private static Matrixf BuildVanillaGuiModelViewMatrix(float posX, float posY, float posZ, float guiSize, float entityScale, float rotX, float rotY, float rotZ)
    {
        Matrixf matrix = BuildVanillaGuiModelMatrix(posX, posY, posZ, guiSize, entityScale, rotX, rotY, rotZ);
        ApplyVanillaGuiModelViewFlip(matrix);
        return matrix;
    }

    private static void ApplyVanillaGuiModelViewFlip(Matrixf matrix)
    {
        matrix.Translate(0.5f, 0f, 0.5f);
        matrix.Scale(1f, 1f, -1f);
        matrix.Translate(-0.5f, 0f, -0.5f);
    }

    private static NVector2 ProjectVanillaGuiPoint(Matrixf matrix, NVector3 point)
    {
        Vec4f transformed = matrix.TransformVector(new Vec4f(point.X, point.Y, point.Z, 1f));
        return new NVector2(transformed.X, transformed.Y);
    }

    private static VanillaPreviewCameraState BuildVanillaPreviewCamera(
        VanillaAnimationPreviewScene scene,
        float width,
        float height,
        float yaw,
        float pitch,
        float zoom,
        float panX,
        float panY,
        VanillaPreviewMode mode,
        bool firstPersonInspectCamera = false,
        float firstPersonLookPitchDegrees = 0f,
        string firstPersonRightHandItemCode = "")
    {
        return mode == VanillaPreviewMode.FirstPerson || mode == VanillaPreviewMode.ImmersiveFirstPerson
            ? BuildVanillaFirstPersonPreviewCamera(scene, width, height, yaw, pitch, zoom, panX, panY, mode, firstPersonInspectCamera, firstPersonLookPitchDegrees, firstPersonRightHandItemCode)
            : BuildVanillaOrbitPreviewCamera(scene, width, height, yaw, pitch, zoom, panX, panY);
    }

    private static VanillaPreviewCameraState BuildVanillaOrbitPreviewCamera(VanillaAnimationPreviewScene scene, float width, float height, float yaw, float pitch, float zoom, float panX, float panY)
    {
        float aspect = Math.Max(0.1f, width / Math.Max(1f, height));
        float fov = 35f * GameMath.DEG2RAD;
        float entitySize = Math.Max(0.001f, scene.GuiEntitySize);
        float radius = Math.Max(Math.Max(scene.ModelWidth, scene.ModelHeight), scene.ModelDepth) * entitySize * 0.62f;
        radius = Math.Max(0.35f, radius);
        float distance = Math.Clamp(radius / Math.Max(0.05f, (float)Math.Tan(fov * 0.5f)) * 1.45f / Math.Clamp(zoom, 0.25f, 3f), radius + 0.25f, radius * 10f + 16f);

        Matrixf model = BuildVanillaPreviewModelMatrix(scene);
        Vec4f modelCenter = model.TransformVector(new Vec4f(scene.ModelCenterX, scene.ModelCenterY, scene.ModelCenterZ, 1f));
        NVector3 target = new(modelCenter.X, modelCenter.Y, modelCenter.Z);

        pitch = Math.Clamp(pitch, -1.52f, 1.52f);
        float cosPitch = (float)Math.Cos(pitch);
        NVector3 outward = NormalizeOrDefault(new NVector3(
            (float)Math.Sin(yaw) * cosPitch,
            (float)Math.Sin(pitch),
            (float)Math.Cos(yaw) * cosPitch), new NVector3(0f, 0f, 1f));
        NVector3 right = NormalizeOrDefault(NVector3.Cross(NVector3.UnitY, outward), NVector3.UnitX);
        NVector3 up = NormalizeOrDefault(NVector3.Cross(outward, right), NVector3.UnitY);
        float panScale = 2f * distance * (float)Math.Tan(fov * 0.5f) / Math.Max(1f, height);
        target += -right * panX * panScale + up * panY * panScale;
        NVector3 eye = target + outward * distance;

        float near = Math.Max(0.01f, distance - radius * 6f);
        near = Math.Min(near, 0.05f);
        float far = Math.Max(64f, distance + radius * 8f + 8f);

        Matrixf projection = new();
        projection.Set(Mat4f.Perspective(Mat4f.Create(), fov, aspect, near, far));
        Matrixf view = new();
        view.Set(Mat4f.LookAt(Mat4f.Create(), [eye.X, eye.Y, eye.Z], [target.X, target.Y, target.Z], [up.X, up.Y, up.Z]));
        Matrixf projectionView = new();
        projectionView.Set(view.Values);
        projectionView.ReverseMul(projection.Values);

        return new(projection, view, projectionView, model, eye, target, distance);
    }

    private static VanillaPreviewCameraState BuildVanillaFirstPersonPreviewCamera(
        VanillaAnimationPreviewScene scene,
        float width,
        float height,
        float yaw,
        float pitch,
        float zoom,
        float panX,
        float panY,
        VanillaPreviewMode mode,
        bool inspectCamera,
        float lookPitchDegrees,
        string rightHandItemCode)
    {
        float aspect = Math.Max(0.1f, width / Math.Max(1f, height));
        float handsFov = Math.Clamp(scene.FirstPersonFovDegrees * PlayerRenderingPatches.HandsFovMultiplier, 25f, 130f);
        float baseFovDegrees = mode == VanillaPreviewMode.FirstPerson ? handsFov : scene.MainFovDegrees;
        float fov = Math.Clamp(inspectCamera ? baseFovDegrees / Math.Clamp(zoom, 0.25f, 3f) : baseFovDegrees, 25f, 130f) * GameMath.DEG2RAD;
        float entitySize = Math.Max(0.001f, scene.GuiEntitySize);
        float radius = Math.Max(Math.Max(scene.ModelWidth, scene.ModelHeight), scene.ModelDepth) * entitySize * 0.62f;
        radius = Math.Max(0.35f, radius);

        pitch = Math.Clamp(pitch, -1.52f, 1.52f);
        NVector3 forward;
        NVector3 up;
        Matrixf view = new();
        if (inspectCamera)
        {
            float cosPitch = (float)Math.Cos(pitch);
            forward = NormalizeOrDefault(new NVector3(
                (float)Math.Sin(yaw) * cosPitch,
                -(float)Math.Sin(pitch),
                -(float)Math.Cos(yaw) * cosPitch), new NVector3(0f, 0f, -1f));
            NVector3 right = NormalizeOrDefault(NVector3.Cross(forward, NVector3.UnitY), NVector3.UnitX);
            up = NormalizeOrDefault(NVector3.Cross(right, forward), NVector3.UnitY);
            view.Set(Mat4f.LookAt(Mat4f.Create(), [0f, 0f, 0f], [forward.X, forward.Y, forward.Z], [up.X, up.Y, up.Z]));
        }
        else
        {
            forward = new NVector3(0f, 0f, -1f);
            up = NVector3.UnitY;
            view.Identity();
        }

        Matrixf model = BuildVanillaFirstPersonModelMatrix(scene, yaw, pitch, panX, panY, width, height, fov, mode, inspectCamera, lookPitchDegrees, rightHandItemCode);
        NVector3 eye = NVector3.Zero;
        NVector3 target = forward * Math.Max(1f, radius * 2f);

        Matrixf projection = new();
        projection.Set(Mat4f.Perspective(Mat4f.Create(), fov, aspect, 0.005f, Math.Max(64f, radius * 18f + 16f)));
        Matrixf projectionView = new();
        projectionView.Set(view.Values);
        projectionView.ReverseMul(projection.Values);

        return new(projection, view, projectionView, model, eye, target, 0f);
    }

    private static Matrixf BuildVanillaFirstPersonModelMatrix(
        VanillaAnimationPreviewScene scene,
        float yaw,
        float pitch,
        float panX,
        float panY,
        float width,
        float height,
        float fov,
        VanillaPreviewMode mode,
        bool inspectCamera,
        float lookPitchDegrees,
        string rightHandItemCode)
    {
        float entitySize = Math.Max(0.001f, scene.GuiEntitySize);
        float localEyeHeight = Math.Clamp(scene.EntityEyeHeight, 0.05f, Math.Max(scene.ModelHeight + 1f, 0.25f));
        float panScale = 2f * (float)Math.Tan(fov * 0.5f) / Math.Max(1f, Math.Min(width, height));

        Matrixf model = new();
        model.Identity();
        if (inspectCamera)
        {
            model.Translate(-panX * panScale, panY * panScale, 0f);
        }
        model.RotateX(scene.GuiShapeRotateX * GameMath.DEG2RAD);
        model.RotateY((inspectCamera ? yaw : 0f) + (90f + scene.GuiShapeRotateY) * GameMath.DEG2RAD);
        model.RotateZ(scene.GuiShapeRotateZ * GameMath.DEG2RAD);

        if (mode == VanillaPreviewMode.FirstPerson)
        {
            float pitchFollow = GetVanillaFirstPersonPitchFollow(scene, rightHandItemCode);
            float posPitch = MathF.PI + Math.Clamp(lookPitchDegrees, -89f, 89f) * GameMath.DEG2RAD;
            model.Translate(0f, localEyeHeight, 0f);
            model.RotateZ((posPitch - MathF.PI) * pitchFollow);
            model.Translate(0f, -localEyeHeight, 0f);
            model.Translate(0f, scene.FirstPersonYOffset, 0f);
        }

        model.Scale(entitySize, entitySize, entitySize);
        model.Translate(-0.5f, 0f, -0.5f);
        return model;
    }

    private static float GetVanillaFirstPersonPitchFollow(VanillaAnimationPreviewScene scene, string rightHandItemCode)
    {
        if (string.IsNullOrWhiteSpace(rightHandItemCode)) return 0.75f;

        return VanillaAnimationPreviewScene.TryBuildFirstPersonItemStack(scene.Api, rightHandItemCode, out ItemStack? stack, out _)
            ? stack?.ItemAttributes?["heldItemPitchFollow"].AsFloat(0.75f) ?? 0.75f
            : 0.75f;
    }

    private static Matrixf BuildVanillaPreviewModelMatrix(VanillaAnimationPreviewScene scene)
    {
        float entitySize = Math.Max(0.001f, scene.GuiEntitySize);
        Matrixf model = new();
        model.Identity();
        model.Scale(entitySize, entitySize, entitySize);
        model.Translate(-0.5f, 0f, -0.5f);
        return model;
    }

    private static bool ProjectVanillaPreviewPoint(Matrixf localToWorld, VanillaPreviewCameraState camera, NVector3 point, NVector2 min, float width, float height, out NVector2 screen)
    {
        return ProjectVanillaPreviewWorldPoint(camera, TransformVanillaPreviewPoint(localToWorld, point), min, width, height, out screen);
    }

    private static bool ProjectVanillaPreviewWorldPoint(VanillaPreviewCameraState camera, NVector3 worldPoint, NVector2 min, float width, float height, out NVector2 screen)
    {
        Vec4f clip = camera.ProjectionView.TransformVector(new Vec4f(worldPoint.X, worldPoint.Y, worldPoint.Z, 1f));
        if (!IsFinite(clip.W) || clip.W <= 0.001f)
        {
            screen = default;
            return false;
        }

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        if (!IsFinite(ndcX) || !IsFinite(ndcY))
        {
            screen = default;
            return false;
        }

        screen = new NVector2(
            min.X + (ndcX * 0.5f + 0.5f) * width,
            min.Y + (0.5f - ndcY * 0.5f) * height);
        if (!IsFinite(screen.X) || !IsFinite(screen.Y))
        {
            screen = default;
            return false;
        }

        return ndcX > -2f && ndcX < 2f && ndcY > -2f && ndcY < 2f;
    }

    private static NVector3 TransformVanillaPreviewPoint(Matrixf matrix, NVector3 point)
    {
        Vec4f transformed = matrix.TransformVector(new Vec4f(point.X, point.Y, point.Z, 1f));
        return new NVector3(transformed.X, transformed.Y, transformed.Z);
    }

    private static NVector3 TransformVanillaPreviewDirection(Matrixf matrix, NVector3 direction)
    {
        Vec4f transformed = matrix.TransformVector(new Vec4f(direction.X, direction.Y, direction.Z, 0f));
        return new NVector3(transformed.X, transformed.Y, transformed.Z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static ShapeElement? FindShapeElement(Shape shape, string elementName)
    {
        if (shape.Elements == null || string.IsNullOrWhiteSpace(elementName)) return null;
        foreach (ShapeElement element in shape.Elements)
        {
            ShapeElement? found = FindShapeElementRecursive(element, elementName);
            if (found != null) return found;
        }

        return null;
    }

    private static ShapeElement? FindShapeElementRecursive(ShapeElement element, string elementName)
    {
        if (string.Equals(element.Name, elementName, StringComparison.OrdinalIgnoreCase)) return element;
        if (element.Children == null) return null;
        foreach (ShapeElement child in element.Children)
        {
            ShapeElement? found = FindShapeElementRecursive(child, elementName);
            if (found != null) return found;
        }

        return null;
    }

    private static void GetShapeElementCenter(ShapeElement element, out double x, out double y, out double z)
    {
        double fromX = element.From is { Length: >= 3 } from ? from[0] : 0;
        double fromY = element.From is { Length: >= 3 } from2 ? from2[1] : 0;
        double fromZ = element.From is { Length: >= 3 } from3 ? from3[2] : 0;
        double toX = element.To is { Length: >= 3 } to ? to[0] : fromX;
        double toY = element.To is { Length: >= 3 } to2 ? to2[1] : fromY;
        double toZ = element.To is { Length: >= 3 } to3 ? to3[2] : fromZ;
        x = (fromX + toX) / 32.0;
        y = (fromY + toY) / 32.0;
        z = (fromZ + toZ) / 32.0;
    }

    private static double GetVanillaGizmoAxisValue(AnimationKeyFrameElement element, TransformGizmoMode mode, TransformGizmoAxis axis)
    {
        return mode switch
        {
            TransformGizmoMode.Move => axis switch
            {
                TransformGizmoAxis.X => element.OffsetX ?? 0,
                TransformGizmoAxis.Y => element.OffsetY ?? 0,
                TransformGizmoAxis.Z => element.OffsetZ ?? 0,
                _ => 0
            },
            TransformGizmoMode.Rotate => axis switch
            {
                TransformGizmoAxis.X => element.RotationX ?? 0,
                TransformGizmoAxis.Y => element.RotationY ?? 0,
                TransformGizmoAxis.Z => element.RotationZ ?? 0,
                _ => 0
            },
            TransformGizmoMode.Scale => axis switch
            {
                TransformGizmoAxis.X => element.StretchX ?? 1,
                TransformGizmoAxis.Y => element.StretchY ?? 1,
                TransformGizmoAxis.Z => element.StretchZ ?? 1,
                _ => 0
            },
            _ => 0
        };
    }

    private static void SetVanillaGizmoAxisValue(AnimationKeyFrameElement element, TransformGizmoMode mode, TransformGizmoAxis axis, double value)
    {
        switch (mode)
        {
            case TransformGizmoMode.Move:
                if (axis == TransformGizmoAxis.X) element.OffsetX = value;
                if (axis == TransformGizmoAxis.Y) element.OffsetY = value;
                if (axis == TransformGizmoAxis.Z) element.OffsetZ = value;
                CompleteVanillaPositionGroup(element);
                break;
            case TransformGizmoMode.Rotate:
                if (axis == TransformGizmoAxis.X) element.RotationX = value;
                if (axis == TransformGizmoAxis.Y) element.RotationY = value;
                if (axis == TransformGizmoAxis.Z) element.RotationZ = value;
                CompleteVanillaRotationGroup(element);
                break;
            case TransformGizmoMode.Scale:
                if (axis == TransformGizmoAxis.X) element.StretchX = value;
                if (axis == TransformGizmoAxis.Y) element.StretchY = value;
                if (axis == TransformGizmoAxis.Z) element.StretchZ = value;
                CompleteVanillaStretchGroup(element);
                break;
        }
    }

    private static void CompleteVanillaElementTransformGroups(AnimationKeyFrameElement element)
    {
        CompleteVanillaPositionGroup(element);
        CompleteVanillaRotationGroup(element);
        CompleteVanillaStretchGroup(element);
    }

    private static void CompleteVanillaPositionGroup(AnimationKeyFrameElement element)
    {
        if (!element.PositionSet) return;
        element.OffsetX ??= 0;
        element.OffsetY ??= 0;
        element.OffsetZ ??= 0;
    }

    private static void CompleteVanillaRotationGroup(AnimationKeyFrameElement element)
    {
        if (!element.RotationSet) return;
        element.RotationX ??= 0;
        element.RotationY ??= 0;
        element.RotationZ ??= 0;
    }

    private static void CompleteVanillaStretchGroup(AnimationKeyFrameElement element)
    {
        if (!element.StretchSet) return;
        element.StretchX ??= 1;
        element.StretchY ??= 1;
        element.StretchZ ??= 1;
    }

    private void ClearVanillaViewportGizmoDrag()
    {
        _vanillaViewportGizmoDragAxis = TransformGizmoAxis.None;
        _vanillaViewportGizmoDragMode = TransformGizmoMode.None;
        _vanillaViewportGizmoDragVector = new NVector2(1f, 0f);
        _vanillaViewportGizmoDragModelDirection = NVector3.UnitX;
        _vanillaViewportGizmoDragScale = 1f;
        _vanillaViewportGizmoDragTranslationBasis = VanillaGizmoTranslationBasis.Identity;
        _vanillaViewportGizmoDragCenter = NVector2.Zero;
        _vanillaViewportGizmoDragLastAngleRadians = 0;
        _vanillaViewportGizmoDragAccumulatedDegrees = 0;
        _vanillaViewportGizmoDragRingScreenSign = -1.0;
        _vanillaViewportGizmoDragStartValue = 0;
        _vanillaViewportGizmoDragStartOffsetX = 0;
        _vanillaViewportGizmoDragStartOffsetY = 0;
        _vanillaViewportGizmoDragStartOffsetZ = 0;
        _vanillaViewportGizmoDragStartRotationX = 0;
        _vanillaViewportGizmoDragStartRotationY = 0;
        _vanillaViewportGizmoDragStartRotationZ = 0;
        _vanillaViewportGizmoDragBaseRotationDegrees = new Vec3d();
        _vanillaViewportGizmoDragRotationParentBasis = RigIkMatrix3.Identity;
        _vanillaViewportGizmoDragSpace = TransformGizmoSpace.World;
        _vanillaViewportGizmoDragRowKey = "";
        _vanillaViewportGizmoDragKeyFrameIndex = -1;
        _vanillaViewportGizmoDragElementName = "";
        _vanillaIkDragActive = false;
        _vanillaIkDragRowKey = "";
        _vanillaIkDragKeyFrameIndex = -1;
        _vanillaIkDragElementName = "";
        _vanillaIkDragCache = null;
    }

    private double SnapVanillaGizmoValue(double value, double step)
    {
        return IncludeGizmoInIncrement ? Math.Round(value / step) * step : value;
    }

    private static double NormalizeVanillaDegrees(double degrees)
    {
        while (degrees > 180) degrees -= 360;
        while (degrees < -180) degrees += 360;
        return degrees;
    }

    private static double NormalizeVanillaRadians(double radians)
    {
        const double twoPi = Math.PI * 2.0;
        while (radians > Math.PI) radians -= twoPi;
        while (radians < -Math.PI) radians += twoPi;
        return radians;
    }

    private static double GetVanillaViewportGizmoMouseAngle(NVector2 center, NVector2 mouse)
    {
        NVector2 radial = mouse - center;
        return Math.Atan2(radial.Y, radial.X);
    }

    private static float NormalizeRadians(float radians)
    {
        const float twoPi = (float)(Math.PI * 2.0);
        while (radians > Math.PI) radians -= twoPi;
        while (radians < -Math.PI) radians += twoPi;
        return radians;
    }

    private static NVector2 NormalizeOrDefault(NVector2 vector, NVector2 fallback)
    {
        float length = vector.Length();
        return length <= 0.0001f ? fallback : vector / length;
    }

    private static NVector3 NormalizeOrDefault(NVector3 vector, NVector3 fallback)
    {
        float length = vector.Length();
        return length <= 0.0001f ? fallback : vector / length;
    }

    private static float DistanceToSegment(NVector2 point, NVector2 start, NVector2 end)
    {
        NVector2 segment = end - start;
        float lengthSquared = segment.LengthSquared();
        if (lengthSquared <= 0.0001f) return (point - start).Length();
        float t = Math.Clamp(NVector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        return (point - (start + segment * t)).Length();
    }

    private static float DistanceToPolyline(NVector2 point, NVector2[] points)
    {
        if (points.Length == 0) return float.MaxValue;
        float best = float.MaxValue;
        for (int i = 1; i < points.Length; i++)
        {
            best = Math.Min(best, DistanceToSegment(point, points[i - 1], points[i]));
        }

        return best;
    }

    private VanillaAnimationViewport3DRenderer EnsureVanillaPreviewRenderer()
    {
        return _vanillaPreviewRenderer ??= new VanillaAnimationViewport3DRenderer(_api);
    }

    private VanillaAnimationPreviewScene? EnsureVanillaPreviewScene(VanillaBrowserRow row)
    {
        if (_vanillaPreviewScene == null || _vanillaPreviewScene.Key != row.Key)
        {
            BuildVanillaPreviewScene(row, rebuildMesh: true);
        }

        return _vanillaPreviewScene;
    }

    private void BuildVanillaPreviewScene(VanillaBrowserRow row, bool rebuildMesh)
    {
        try
        {
            bool sameScene = _vanillaPreviewScene?.Key == row.Key;
            if (!sameScene)
            {
                ClearPendingVanillaPreviewMeshRebuild();
            }

            float requestedFrame = sameScene ? _vanillaPreviewScene!.CurrentFrame : 0f;
            if (_vanillaPreviewScene == null || !sameScene || rebuildMesh)
            {
                DisposeVanillaPreviewScene();
                _vanillaPreviewScene = VanillaAnimationPreviewScene.Create(_api, row);
            }
            else
            {
                _vanillaPreviewScene.ReloadAnimator(row);
            }

            if (_vanillaPreviewScene != null)
            {
                _vanillaPreviewScene.Scrub(Math.Clamp(requestedFrame, 0, Math.Max(0, _vanillaPreviewScene.QuantityFrames - 1)));
                _vanillaStatus = _vanillaPreviewScene.Status;
            }
        }
        catch (Exception exception)
        {
            DisposeVanillaPreviewScene();
            _vanillaStatus = $"Preview failed for {row.Label}: {exception.Message}";
            _animationDiagnostics.Exception($"Preview failed for {row.Label}", exception);
            LoggerUtil.Warn(_api, this, $"Vanilla preview failed for '{row.Label}' ({row.Key}): {exception}");
        }
    }

    private void RefreshVanillaPreviewAfterEdit(VanillaBrowserRow row, params string[] changedElementNames)
    {
        if (_vanillaPreviewScene?.Key != row.Key) return;
        bool rebuildMesh = ShouldRebuildVanillaPreviewMeshAfterEdit(changedElementNames);
        if (!rebuildMesh && _vanillaPreviewScene.TryFastSyncAnimation(row)) return;

        if (rebuildMesh && IsVanillaViewportDraggingRow(row))
        {
            _vanillaPreviewMeshRebuildPending = true;
            _vanillaPreviewMeshRebuildPendingRowKey = row.Key;
            BuildVanillaPreviewScene(row, rebuildMesh: false);
            return;
        }

        BuildVanillaPreviewScene(row, rebuildMesh);
    }

    private bool IsVanillaViewportDraggingRow(VanillaBrowserRow row)
    {
        return _vanillaViewportGizmoDragAxis != TransformGizmoAxis.None &&
            string.Equals(_vanillaViewportGizmoDragRowKey, row.Key, StringComparison.Ordinal);
    }

    private void FlushPendingVanillaPreviewMeshRebuild(VanillaBrowserRow row)
    {
        if (!_vanillaPreviewMeshRebuildPending ||
            !string.Equals(_vanillaPreviewMeshRebuildPendingRowKey, row.Key, StringComparison.Ordinal))
        {
            return;
        }

        ClearPendingVanillaPreviewMeshRebuild();
        BuildVanillaPreviewScene(row, rebuildMesh: true);
    }

    private void ClearPendingVanillaPreviewMeshRebuild()
    {
        _vanillaPreviewMeshRebuildPending = false;
        _vanillaPreviewMeshRebuildPendingRowKey = "";
    }

    private bool ShouldRebuildVanillaPreviewMeshAfterEdit(IEnumerable<string>? changedElementNames)
    {
        if (_vanillaPreviewScene == null || changedElementNames == null) return false;

        foreach (string elementName in changedElementNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryFindVanillaPose(_vanillaPreviewScene.Animator.RootPoses, elementName, out ElementPose? pose, out _) ||
                pose?.ForElement == null ||
                pose.ForElement.JointId <= 0)
            {
                return true;
            }
        }

        return false;
    }

    private void PauseVanillaLiveSymmetryPreview(VanillaBrowserRow row, VanillaAnimation animation)
    {
        if (!_vanillaLiveSymmetryEnabled || _vanillaLiveSymmetryMode != VanillaLiveSymmetryMode.HalfCycle) return;
        if (_vanillaPreviewScene?.Key != row.Key) return;

        int maxFrame = Math.Max(0, Math.Max(1, animation.QuantityFrames) - 1);
        if (_vanillaSelection.LoopEndFrame <= _vanillaSelection.LoopStartFrame || _vanillaSelection.LoopStartFrame < 0 || _vanillaSelection.LoopEndFrame > maxFrame)
        {
            _vanillaSelection.LoopStartFrame = 0;
            _vanillaSelection.LoopEndFrame = maxFrame;
        }

        _vanillaPreviewScene.Playing = false;
    }

    private void DisposeVanillaPreviewScene()
    {
        ClearPendingVanillaPreviewMeshRebuild();
        _vanillaPreviewScene?.Dispose();
        _vanillaPreviewScene = null;
        _vanillaPreviewRenderer?.SetVisible(false);
    }

    private static VanillaAnimation? GetVanillaAnimation(VanillaBrowserRow row)
    {
        return row.ShapeAnimation?.Animation ?? row.MetadataEntry?.ResolveCurrentShape()?.Animation;
    }
}
