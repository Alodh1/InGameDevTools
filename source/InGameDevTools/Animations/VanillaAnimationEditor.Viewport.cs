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
            scene.Tick(deltaSeconds);
            ApplyVanillaLoop(scene);
        }

        if (ImGui.Button("Play##vanilla-playback"))
        {
            scene.Play();
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

        ImGui.SameLine();
        if (ImGui.Button("Step keyframe <##vanilla-playback"))
        {
            StepVanillaKeyframe(row, -1);
        }

        ImGui.SameLine();
        if (ImGui.Button("Step keyframe >##vanilla-playback"))
        {
            StepVanillaKeyframe(row, 1);
        }

        ImGui.SameLine();
        if (ImGui.Button("Step frame <##vanilla-playback"))
        {
            ScrubVanillaPreview(scene, Math.Max(0, scene.CurrentFrame - 1));
        }

        ImGui.SameLine();
        if (ImGui.Button("Step frame >##vanilla-playback"))
        {
            ScrubVanillaPreview(scene, Math.Min(Math.Max(0, scene.QuantityFrames - 1), scene.CurrentFrame + 1));
        }

        int maxFrame = Math.Max(0, scene.QuantityFrames - 1);
        int loopStart = Math.Clamp(_vanillaSelection.LoopStartFrame, 0, maxFrame);
        int loopEnd = Math.Clamp(_vanillaSelection.LoopEndFrame, 0, maxFrame);
        if (loopEnd < loopStart) loopEnd = loopStart;

        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderInt("Loop start frame##vanilla-playback", ref loopStart, 0, maxFrame))
        {
            _vanillaSelection.LoopStartFrame = Math.Min(loopStart, _vanillaSelection.LoopEndFrame);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
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

        ImGui.SameLine();
        if (ImGui.Button("Reset view##vanilla-preview-camera-reset"))
        {
            _vanillaViewportYaw = 0;
            _vanillaViewportPitch = 0;
            _vanillaViewportZoom = 1f;
            _vanillaViewportPanX = 0;
            _vanillaViewportPanY = 0;
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
        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0.055f, 0.052f, 0.045f, 1f));
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 1f));
        uint grid = ImGui.ColorConvertFloat4ToU32(new NVector4(0.28f, 0.27f, 0.22f, 0.42f));
        uint gridMajor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.45f, 0.42f, 0.33f, 0.72f));
        uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.86f, 0.82f, 0.72f, 1f));
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
            _vanillaViewportWorldLighting,
            ghosts,
            _vanillaVerbosePreviewLogs,
            out string? previewSkipReason);
        if (textureId > 0)
        {
            drawList.AddImage(new IntPtr(textureId), min, max, new NVector2(0f, 1f), new NVector2(1f, 0f));
        }
        else if (!string.IsNullOrWhiteSpace(previewSkipReason))
        {
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
            bool suppressBodyPick = DrawVanillaViewportGizmo(row, scene, drawList, min, max, hovered);
            DrawVanillaViewportElementPicker(row, scene, drawList, min, max, hovered, suppressBodyPick);
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

    private bool DrawVanillaViewportGizmo(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, ImDrawListPtr drawList, NVector2 min, NVector2 max, bool hovered)
    {
        if (GizmoMode == TransformGizmoMode.None) return false;
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

    private static VanillaPreviewCameraState BuildVanillaPreviewCamera(VanillaAnimationPreviewScene scene, float width, float height, float yaw, float pitch, float zoom, float panX, float panY, VanillaPreviewMode mode)
    {
        return mode == VanillaPreviewMode.FirstPerson || mode == VanillaPreviewMode.ImmersiveFirstPerson
            ? BuildVanillaFirstPersonPreviewCamera(scene, width, height, yaw, pitch, zoom, panX, panY, mode)
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

    private static VanillaPreviewCameraState BuildVanillaFirstPersonPreviewCamera(VanillaAnimationPreviewScene scene, float width, float height, float yaw, float pitch, float zoom, float panX, float panY, VanillaPreviewMode mode)
    {
        float aspect = Math.Max(0.1f, width / Math.Max(1f, height));
        float handsFov = Math.Clamp(scene.FirstPersonFovDegrees * PlayerRenderingPatches.HandsFovMultiplier, 25f, 130f);
        float fov = Math.Clamp(handsFov / Math.Clamp(zoom, 0.25f, 3f), 25f, 130f) * GameMath.DEG2RAD;
        float entitySize = Math.Max(0.001f, scene.GuiEntitySize);
        float radius = Math.Max(Math.Max(scene.ModelWidth, scene.ModelHeight), scene.ModelDepth) * entitySize * 0.62f;
        radius = Math.Max(0.35f, radius);

        pitch = Math.Clamp(pitch, -1.52f, 1.52f);
        float cosPitch = (float)Math.Cos(pitch);
        NVector3 forward = NormalizeOrDefault(new NVector3(
            (float)Math.Sin(yaw) * cosPitch,
            -(float)Math.Sin(pitch),
            -(float)Math.Cos(yaw) * cosPitch), new NVector3(0f, 0f, -1f));
        NVector3 right = NormalizeOrDefault(NVector3.Cross(forward, NVector3.UnitY), NVector3.UnitX);
        NVector3 up = NormalizeOrDefault(NVector3.Cross(right, forward), NVector3.UnitY);

        Matrixf model = BuildVanillaFirstPersonModelMatrix(scene, yaw, pitch, panX, panY, width, height, fov, mode);
        NVector3 eye = NVector3.Zero;
        NVector3 target = forward * Math.Max(1f, radius * 2f);

        Matrixf projection = new();
        projection.Set(Mat4f.Perspective(Mat4f.Create(), fov, aspect, 0.005f, Math.Max(64f, radius * 18f + 16f)));
        Matrixf view = new();
        view.Set(Mat4f.LookAt(Mat4f.Create(), [eye.X, eye.Y, eye.Z], [target.X, target.Y, target.Z], [up.X, up.Y, up.Z]));
        Matrixf projectionView = new();
        projectionView.Set(view.Values);
        projectionView.ReverseMul(projection.Values);

        return new(projection, view, projectionView, model, eye, target, 0f);
    }

    private static Matrixf BuildVanillaFirstPersonModelMatrix(VanillaAnimationPreviewScene scene, float yaw, float pitch, float panX, float panY, float width, float height, float fov, VanillaPreviewMode mode)
    {
        float entitySize = Math.Max(0.001f, scene.GuiEntitySize);
        float localEyeHeight = scene.EntityEyeHeight / entitySize;
        localEyeHeight = Math.Clamp(localEyeHeight, 0.05f, Math.Max(scene.ModelHeight + 1f, 0.25f));
        float panScale = 2f * (float)Math.Tan(fov * 0.5f) / Math.Max(1f, Math.Min(width, height));

        Matrixf model = new();
        model.Identity();
        model.Translate(-panX * panScale, panY * panScale, 0f);
        model.RotateX(scene.GuiShapeRotateX * GameMath.DEG2RAD);
        model.RotateY(yaw + (90f + scene.GuiShapeRotateY) * GameMath.DEG2RAD);

        if (mode == VanillaPreviewMode.FirstPerson)
        {
            model.RotateZ(scene.GuiShapeRotateZ * GameMath.DEG2RAD);
            model.Translate(0f, localEyeHeight, 0f);
            model.RotateZ(pitch * 0.75f);
            model.Translate(0f, -localEyeHeight, 0f);
            model.Translate(0f, scene.FirstPersonYOffset, 0f);
        }
        else
        {
            model.RotateZ(scene.GuiShapeRotateZ * GameMath.DEG2RAD);
        }

        model.Scale(entitySize, entitySize, entitySize);
        model.Translate(-0.5f, -localEyeHeight, -0.5f);
        return model;
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
