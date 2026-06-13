using ImGuiNET;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VanillaAnimation = Vintagestory.API.Common.Animation;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private static readonly string[] VanillaEasingBakeFunctionNames = Enum.GetNames<EasingFunctionType>()
        .Where(name => !string.Equals(name, nameof(EasingFunctionType.Skip), StringComparison.Ordinal))
        .ToArray();

    private int _vanillaEasingBakeFunctionIndex = Array.IndexOf(VanillaEasingBakeFunctionNames, nameof(EasingFunctionType.EaseInOutSine)) >= 0
        ? Array.IndexOf(VanillaEasingBakeFunctionNames, nameof(EasingFunctionType.EaseInOutSine))
        : 0;
    private int _vanillaEasingBakeSteps = 4;
    private bool _vanillaEasingBakeAllElements = true;
    private Dictionary<string, AnimationKeyFrameElement>? _vanillaPoseClipboard;
    private string _vanillaPoseClipboardLabel = "";
    private float _vanillaPlaybackSpeed = 1f;
    private bool _vanillaMotionTrailEnabled = false;
    private readonly List<NVector3> _vanillaMotionTrailPoints = [];
    private readonly List<int> _vanillaMotionTrailPointFrames = [];
    private string _vanillaMotionTrailCacheKey = "";

    private void DrawVanillaKeyframeProTools(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame)
    {
        DrawVanillaPoseClipboardControls(row, entry, keyFrame);
        DrawVanillaEasingBakeControls(row, entry, keyFrame);
    }

    private void DrawVanillaPoseClipboardControls(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame)
    {
        if (!ImGui.TreeNodeEx("Pose clipboard##vanilla-pose-clipboard", ImGuiTreeNodeFlags.None)) return;

        bool hasElements = keyFrame.Elements is { Count: > 0 };
        if (!hasElements) ImGui.BeginDisabled();
        if (ImGui.Button("Copy pose##vanilla-pose-copy"))
        {
            _vanillaPoseClipboard = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string name, AnimationKeyFrameElement element) in keyFrame.Elements!)
            {
                _vanillaPoseClipboard[name] = CloneElement(element);
            }

            _vanillaPoseClipboardLabel = $"frame {keyFrame.Frame} ({_vanillaPoseClipboard.Count} element(s))";
            _vanillaStatus = $"Copied pose from {_vanillaPoseClipboardLabel}.";
        }
        if (!hasElements) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Copies every element channel in this keyframe so the pose can be pasted onto another keyframe or animation.");
        }

        ImGui.SameLine();
        bool hasSelectedElement = hasElements &&
            !string.IsNullOrWhiteSpace(_vanillaSelection.ElementName) &&
            keyFrame.Elements!.ContainsKey(_vanillaSelection.ElementName);
        if (!hasSelectedElement) ImGui.BeginDisabled();
        if (ImGui.Button("Copy selected element##vanilla-pose-copy-element"))
        {
            _vanillaPoseClipboard = new(StringComparer.OrdinalIgnoreCase)
            {
                [_vanillaSelection.ElementName] = CloneElement(keyFrame.Elements![_vanillaSelection.ElementName])
            };
            _vanillaPoseClipboardLabel = $"{_vanillaSelection.ElementName} @ frame {keyFrame.Frame}";
            _vanillaStatus = $"Copied {_vanillaPoseClipboardLabel}.";
        }
        if (!hasSelectedElement) ImGui.EndDisabled();

        bool hasClipboard = _vanillaPoseClipboard is { Count: > 0 };
        if (!hasClipboard) ImGui.BeginDisabled();
        if (ImGui.Button("Paste (merge)##vanilla-pose-paste-merge"))
        {
            PasteVanillaPose(row, entry, keyFrame, replace: false, flipped: false);
        }
        ImGui.SameLine();
        if (ImGui.Button("Paste (replace)##vanilla-pose-paste-replace"))
        {
            PasteVanillaPose(row, entry, keyFrame, replace: true, flipped: false);
        }
        ImGui.SameLine();
        if (ImGui.Button("Paste flipped##vanilla-pose-paste-flipped"))
        {
            PasteVanillaPose(row, entry, keyFrame, replace: false, flipped: true);
        }
        if (!hasClipboard) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Pastes the copied pose mirrored left<->right, swapping paired elements. The classic way to author the second half of a walk cycle.");
        }

        ImGui.TextDisabled(hasClipboard
            ? $"Clipboard: {_vanillaPoseClipboardLabel}"
            : "Clipboard: empty. Copy a pose first; it can be pasted into any keyframe of any animation.");
        ImGui.TreePop();
    }

    private void PasteVanillaPose(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, bool replace, bool flipped)
    {
        if (_vanillaPoseClipboard is not { Count: > 0 }) return;

        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        if (replace)
        {
            keyFrame.Elements.Clear();
        }

        string[] allElements = BuildVanillaSymmetryElementUniverse(entry.Document, entry.Animation, keyFrame);
        List<string> written = [];
        foreach ((string name, AnimationKeyFrameElement element) in _vanillaPoseClipboard)
        {
            string targetName = name;
            AnimationKeyFrameElement targetElement = CloneElement(element);
            if (flipped)
            {
                targetElement = MirrorVanillaElement(element);
                if (TryResolveVanillaSymmetryPair(entry.Document, name, allElements, out string pairName, out _, out _) &&
                    !string.Equals(pairName, name, StringComparison.OrdinalIgnoreCase))
                {
                    targetName = pairName;
                }
            }

            keyFrame.Elements[targetName] = targetElement;
            written.Add(targetName);
        }

        ApplyVanillaElementEdit(row, entry, keyFrame, written.ToArray());
        string mode = replace ? "replaced" : flipped ? "pasted flipped" : "merged";
        _vanillaStatus = $"Pose {mode}: {written.Count} element(s) written to frame {keyFrame.Frame}.";
    }

    private void DrawVanillaEasingBakeControls(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame)
    {
        if (!ImGui.TreeNodeEx("Easing bake##vanilla-easing-bake", ImGuiTreeNodeFlags.None)) return;

        ImGui.TextDisabled("Vintage Story interpolates keyframes linearly. Baking inserts eased in-between keyframes for professional motion curves.");

        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Easing##vanilla-easing-bake-function", ref _vanillaEasingBakeFunctionIndex, VanillaEasingBakeFunctionNames, VanillaEasingBakeFunctionNames.Length);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Curve applied to interpolation between this keyframe and the next. EaseInOut* curves accelerate then decelerate; Back/Elastic/Bounce overshoot.");
        }

        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("In-between keyframes##vanilla-easing-bake-steps", ref _vanillaEasingBakeSteps))
        {
            _vanillaEasingBakeSteps = Math.Clamp(_vanillaEasingBakeSteps, 1, 60);
        }

        ImGui.SameLine();
        ImGui.Checkbox("All elements##vanilla-easing-bake-scope", ref _vanillaEasingBakeAllElements);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("On bakes every element channel present in both keyframes. Off bakes only the selected element.");
        }

        if (ImGui.Button("Bake eased in-betweens to next keyframe##vanilla-easing-bake-apply"))
        {
            BakeVanillaEasingToNextKeyFrame(row, entry, keyFrame);
        }

        ImGui.TreePop();
    }

    private void BakeVanillaEasingToNextKeyFrame(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame)
    {
        VanillaAnimation animation = entry.Animation;
        animation.KeyFrames ??= [];
        AnimationKeyFrame? next = animation.KeyFrames
            .Where(candidate => candidate.Frame > keyFrame.Frame)
            .OrderBy(candidate => candidate.Frame)
            .FirstOrDefault();
        if (next == null)
        {
            _vanillaStatus = "Easing bake needs a later keyframe to interpolate toward.";
            return;
        }

        if (next.Frame - keyFrame.Frame < 2)
        {
            _vanillaStatus = "Easing bake needs at least one empty frame between this keyframe and the next.";
            return;
        }

        if (keyFrame.Elements == null || keyFrame.Elements.Count == 0 || next.Elements == null || next.Elements.Count == 0)
        {
            _vanillaStatus = "Easing bake needs element channels in both keyframes.";
            return;
        }

        string functionName = VanillaEasingBakeFunctionNames[Math.Clamp(_vanillaEasingBakeFunctionIndex, 0, VanillaEasingBakeFunctionNames.Length - 1)];
        EasingFunctionType functionType = Enum.Parse<EasingFunctionType>(functionName);
        EasingFunctions.EasingFunctionDelegate easing = EasingFunctions.Get(functionType);

        List<(string Name, AnimationKeyFrameElement Start, AnimationKeyFrameElement End)> channels = [];
        foreach ((string name, AnimationKeyFrameElement start) in keyFrame.Elements)
        {
            if (!_vanillaEasingBakeAllElements && !string.Equals(name, _vanillaSelection.ElementName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!next.Elements.TryGetValue(name, out AnimationKeyFrameElement? end) || end == null) continue;
            channels.Add((name, start, end));
        }

        if (channels.Count == 0)
        {
            _vanillaStatus = _vanillaEasingBakeAllElements
                ? "Easing bake found no element present in both keyframes."
                : $"Easing bake: {_vanillaSelection.ElementName} must be present in both keyframes.";
            return;
        }

        int steps = Math.Clamp(_vanillaEasingBakeSteps, 1, Math.Max(1, next.Frame - keyFrame.Frame - 1));
        HashSet<int> writtenFrames = [];
        HashSet<string> writtenElements = new(StringComparer.OrdinalIgnoreCase);
        int createdKeyFrames = 0;
        int writtenChannels = 0;

        for (int step = 1; step <= steps; step++)
        {
            float t = step / (float)(steps + 1);
            int frame = (int)Math.Round(keyFrame.Frame + (next.Frame - keyFrame.Frame) * (double)t);
            if (frame <= keyFrame.Frame || frame >= next.Frame || !writtenFrames.Add(frame)) continue;

            float eased = Math.Clamp(easing(t), -4f, 4f);
            AnimationKeyFrame target = GetOrCreateVanillaTargetKeyFrame(animation, frame, out bool created);
            if (created) createdKeyFrames++;
            target.Elements ??= new(StringComparer.OrdinalIgnoreCase);

            foreach ((string name, AnimationKeyFrameElement start, AnimationKeyFrameElement end) in channels)
            {
                if (!target.Elements.TryGetValue(name, out AnimationKeyFrameElement? element) || element == null)
                {
                    element = new AnimationKeyFrameElement();
                    target.Elements[name] = element;
                }

                element.OffsetX = EaseVanillaChannel(start.OffsetX, end.OffsetX, eased) ?? element.OffsetX;
                element.OffsetY = EaseVanillaChannel(start.OffsetY, end.OffsetY, eased) ?? element.OffsetY;
                element.OffsetZ = EaseVanillaChannel(start.OffsetZ, end.OffsetZ, eased) ?? element.OffsetZ;
                element.RotationX = EaseVanillaChannel(start.RotationX, end.RotationX, eased) ?? element.RotationX;
                element.RotationY = EaseVanillaChannel(start.RotationY, end.RotationY, eased) ?? element.RotationY;
                element.RotationZ = EaseVanillaChannel(start.RotationZ, end.RotationZ, eased) ?? element.RotationZ;
                element.StretchX = EaseVanillaChannel(start.StretchX, end.StretchX, eased) ?? element.StretchX;
                element.StretchY = EaseVanillaChannel(start.StretchY, end.StretchY, eased) ?? element.StretchY;
                element.StretchZ = EaseVanillaChannel(start.StretchZ, end.StretchZ, eased) ?? element.StretchZ;
                element.OriginX = EaseVanillaChannel(start.OriginX, end.OriginX, eased) ?? element.OriginX;
                element.OriginY = EaseVanillaChannel(start.OriginY, end.OriginY, eased) ?? element.OriginY;
                element.OriginZ = EaseVanillaChannel(start.OriginZ, end.OriginZ, eased) ?? element.OriginZ;
                element.RotShortestDistanceX = start.RotShortestDistanceX;
                element.RotShortestDistanceY = start.RotShortestDistanceY;
                element.RotShortestDistanceZ = start.RotShortestDistanceZ;
                CompleteVanillaElementTransformGroups(element);
                writtenElements.Add(name);
                writtenChannels++;
            }
        }

        if (writtenFrames.Count == 0)
        {
            _vanillaStatus = "Easing bake produced no in-between frames; reduce steps or move the keyframes apart.";
            return;
        }

        animation.KeyFrames = animation.KeyFrames.OrderBy(candidate => candidate.Frame).ToArray();
        PreserveVanillaSelectedKeyFrame(animation, keyFrame);
        MarkVanillaDirty(entry.Document);
        RefreshVanillaPreviewAfterEdit(row, writtenElements.ToArray());
        _vanillaStatus = $"Baked {functionName} easing: {writtenFrames.Count} in-between keyframe(s) ({createdKeyFrames} new), {writtenChannels} channel write(s) between frames {keyFrame.Frame} and {next.Frame}.";
    }

    private static double? EaseVanillaChannel(double? start, double? end, float eased)
    {
        if (!start.HasValue || !end.HasValue) return null;
        return start.Value + (end.Value - start.Value) * eased;
    }

    private void EnsureVanillaMotionTrail(VanillaBrowserRow row, VanillaAnimationPreviewScene scene)
    {
        if (!_vanillaMotionTrailEnabled)
        {
            if (_vanillaMotionTrailPoints.Count > 0)
            {
                _vanillaMotionTrailPoints.Clear();
                _vanillaMotionTrailPointFrames.Clear();
                _vanillaMotionTrailCacheKey = "";
            }
            return;
        }

        string elementName = _vanillaSelection.ElementName;
        if (string.IsNullOrWhiteSpace(elementName))
        {
            _vanillaMotionTrailPoints.Clear();
            _vanillaMotionTrailPointFrames.Clear();
            _vanillaMotionTrailCacheKey = "";
            return;
        }

        VanillaAnimationDocument document = (row.ShapeAnimation ?? row.MetadataEntry?.ResolveCurrentShape())?.Document ?? row.Document;
        string cacheKey = $"{row.Key}|{elementName}|{document.EditVersion}|{scene.QuantityFrames}";
        if (string.Equals(cacheKey, _vanillaMotionTrailCacheKey, StringComparison.Ordinal)) return;
        if (_vanillaViewportGizmoDragAxis != TransformGizmoAxis.None || _vanillaIkDragActive) return;

        _vanillaMotionTrailPoints.Clear();
        _vanillaMotionTrailPointFrames.Clear();

        float restoreFrame = scene.CurrentFrame;
        int totalFrames = Math.Max(1, scene.QuantityFrames);
        int step = Math.Max(1, totalFrames / 120);
        try
        {
            for (int frame = 0; frame < totalFrames; frame += step)
            {
                scene.Scrub(frame);
                if (TryFindVanillaPose(scene.Animator.RootPoses, elementName, out ElementPose? pose, out _) &&
                    pose != null &&
                    TryGetVanillaPoseModelOrigin(pose, out Vec3d origin))
                {
                    _vanillaMotionTrailPoints.Add(new NVector3((float)origin.X, (float)origin.Y, (float)origin.Z));
                    _vanillaMotionTrailPointFrames.Add(frame);
                }
            }
        }
        finally
        {
            scene.Scrub(restoreFrame);
        }

        _vanillaMotionTrailCacheKey = cacheKey;
    }

    private void DrawVanillaMotionTrail(VanillaBrowserRow row, VanillaAnimationPreviewScene scene, ImDrawListPtr drawList, VanillaPreviewCameraState camera, NVector2 min, float width, float height)
    {
        if (!_vanillaMotionTrailEnabled || _vanillaMotionTrailPoints.Count < 2) return;

        VanillaAnimation? animation = GetVanillaAnimation(row);
        HashSet<int> keyFrameFrames = [];
        foreach (AnimationKeyFrame keyFrame in animation?.KeyFrames ?? [])
        {
            keyFrameFrames.Add(keyFrame.Frame);
        }

        uint trailStart = ImGui.ColorConvertFloat4ToU32(new NVector4(0.30f, 0.75f, 1f, 0.85f));
        uint trailEnd = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.62f, 0.25f, 0.85f));
        uint keyColor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.95f, 0.85f, 0.25f, 1f));
        uint playColor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.35f, 1f, 0.55f, 1f));

        bool previousVisible = ProjectVanillaPreviewPoint(camera.Model, camera, _vanillaMotionTrailPoints[0], min, width, height, out NVector2 previousScreen);
        int currentFrame = (int)Math.Round(scene.CurrentFrame);
        for (int index = 1; index < _vanillaMotionTrailPoints.Count; index++)
        {
            bool visible = ProjectVanillaPreviewPoint(camera.Model, camera, _vanillaMotionTrailPoints[index], min, width, height, out NVector2 screen);
            if (previousVisible && visible)
            {
                float t = index / (float)Math.Max(1, _vanillaMotionTrailPoints.Count - 1);
                uint color = LerpVanillaTrailColor(trailStart, trailEnd, t);
                drawList.AddLine(previousScreen, screen, color, 2f);
            }

            previousScreen = screen;
            previousVisible = visible;
        }

        for (int index = 0; index < _vanillaMotionTrailPoints.Count; index++)
        {
            int frame = _vanillaMotionTrailPointFrames[index];
            bool isKeyFrame = keyFrameFrames.Contains(frame);
            bool isCurrent = frame == currentFrame;
            if (!isKeyFrame && !isCurrent) continue;
            if (!ProjectVanillaPreviewPoint(camera.Model, camera, _vanillaMotionTrailPoints[index], min, width, height, out NVector2 screen)) continue;

            if (isKeyFrame)
            {
                drawList.AddCircleFilled(screen, 4f, keyColor);
            }
            if (isCurrent)
            {
                drawList.AddCircle(screen, 6f, playColor, 12, 2f);
            }
        }
    }

    private static uint LerpVanillaTrailColor(uint from, uint to, float t)
    {
        NVector4 a = ImGui.ColorConvertU32ToFloat4(from);
        NVector4 b = ImGui.ColorConvertU32ToFloat4(to);
        return ImGui.ColorConvertFloat4ToU32(a + (b - a) * Math.Clamp(t, 0f, 1f));
    }
}
