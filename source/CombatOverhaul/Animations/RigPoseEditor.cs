#if DEBUG
using CombatOverhaul.Integration.Transpilers;
using ImGuiNET;
using NVector3 = System.Numerics.Vector3;
using OpenTK.Mathematics;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CombatOverhaul.Animations;

public sealed partial class DebugWindowManager
{
    private static readonly EnumAnimatedElement[] RigEditableParts =
    [
        EnumAnimatedElement.UpperTorso,
        EnumAnimatedElement.LowerTorso,
        EnumAnimatedElement.Neck,
        EnumAnimatedElement.Head,
        EnumAnimatedElement.UpperArmR,
        EnumAnimatedElement.LowerArmR,
        EnumAnimatedElement.ItemAnchor,
        EnumAnimatedElement.UpperArmL,
        EnumAnimatedElement.LowerArmL,
        EnumAnimatedElement.ItemAnchorL,
        EnumAnimatedElement.UpperFootR,
        EnumAnimatedElement.LowerFootR,
        EnumAnimatedElement.UpperFootL,
        EnumAnimatedElement.LowerFootL,
        EnumAnimatedElement.DetachedAnchor
    ];

    private static readonly string[] RigEditablePartNames = RigEditableParts.Select(part => part.ToString()).ToArray();
    private static readonly Vec4f RigOnionPreviousColor = new(0.27f, 0.82f, 1.0f, 0.34f);
    private static readonly Vec4f RigOnionNextColor = new(1.0f, 0.66f, 0.18f, 0.34f);
    private static readonly int RigMotionPathPastColor = ColorUtil.ColorFromRgba(70, 210, 255, 210);
    private static readonly int RigMotionPathFutureColor = ColorUtil.ColorFromRgba(255, 176, 42, 210);
    private static readonly int RigMotionPathSelectedPastColor = ColorUtil.ColorFromRgba(120, 255, 180, 235);
    private static readonly int RigMotionPathSelectedFutureColor = ColorUtil.ColorFromRgba(255, 230, 90, 235);
    private static readonly int RigMotionPathCurrentColor = ColorUtil.ColorFromRgba(255, 255, 255, 245);
    private static readonly FieldInfo? EntityShapeRendererMeshRefOpaque = typeof(EntityShapeRenderer).GetField("meshRefOpaque", BindingFlags.Instance | BindingFlags.NonPublic);

    private bool _rigPoseEditorEnabled;
    private bool _highlightSelectedRigPart = true;
    private bool _rigOnionSkinEnabled = true;
    private bool _rigOnionSkinPrevious = true;
    private bool _rigOnionSkinNext = true;
    private Animation? _rigOnionSkinAnimation;
    private bool _rigMotionPathsEnabled;
    private bool _rigMotionPathHead = true;
    private bool _rigMotionPathRightHand = true;
    private bool _rigMotionPathLeftHand = true;
    private bool _rigMotionPathRightFoot = true;
    private bool _rigMotionPathLeftFoot = true;
    private bool _rigMotionPathMainHandAnchor;
    private bool _rigMotionPathOffHandAnchor;
    private bool _rigMotionPathWeaponTip;
    private bool _rigMotionPathSplit = true;
    private int _rigMotionPathSamples = 48;
    private float _rigMotionPathThickness = 1.5f;
    private Animation? _rigMotionPathAnimation;
    private string _rigMotionPathAnimationCode = "";
    private string _rigMotionPathCacheKey = "";
    private readonly List<TransformGizmoMotionPath> _rigMotionPathCache = [];
    private bool _rigIkFollowParents;
    private bool _rigIkDragActive;
    private int _rigIkDragKeyframeIndex = -1;
    private EnumAnimatedElement _rigIkDragPart = EnumAnimatedElement.Unknown;
    private PlayerFrame _rigIkDragStartFrame = PlayerFrame.Zero;
    private RigIkDragCache? _rigIkDragCache;
    private bool _rigPartClipboardHasValue;
    private EnumAnimatedElement _rigPartClipboardPart = EnumAnimatedElement.Unknown;
    private AnimationElement _rigPartClipboard = AnimationElement.Zero;
    private bool _rigFullPoseClipboardHasValue;
    private PlayerFrame _rigFullPoseClipboard = PlayerFrame.Zero;
    private int _rigPartIndex;
    private ModelTransform _rigGizmoTransform = CreateDefaultTransform();

    internal static bool DebugRigPoseOverrideActive { get; private set; }

    private bool DrawRigPoseEditor(string animationCode, Animation animation)
    {
        ImGui.SeparatorText("Rig pose editor");

        bool enabled = _rigPoseEditorEnabled;
        if (ImGui.Checkbox("Enable rig pose editor##rig", ref enabled))
        {
            _rigPoseEditorEnabled = enabled;
            DebugRigPoseOverrideActive = enabled;
            if (!enabled)
            {
                _detachedEditorCamera?.SetEnabled(false);
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Edits the selected player keyframe. Use Save to source above to persist.");

        if (!_rigPoseEditorEnabled)
        {
            DebugRigPoseOverrideActive = false;
            _rigOnionSkinAnimation = null;
            _rigMotionPathAnimation = null;
            return false;
        }

        DebugRigPoseOverrideActive = true;
        _rigOnionSkinAnimation = animation;
        _rigMotionPathAnimation = animation;
        _rigMotionPathAnimationCode = animationCode;
        if (animation.PlayerKeyFrames.Count == 0)
        {
            _rigOnionSkinAnimation = null;
            _rigMotionPathAnimation = null;
            ImGui.Text("No player keyframes.");
            return true;
        }

        if (animation._playerFrameIndex >= animation.PlayerKeyFrames.Count) animation._playerFrameIndex = animation.PlayerKeyFrames.Count - 1;
        if (animation._playerFrameIndex < 0) animation._playerFrameIndex = 0;
        if (_rigPartIndex >= RigEditableParts.Length) _rigPartIndex = 0;

        ImGui.SetNextItemWidth(220);
        ImGui.SliderInt("Player keyframe##rig", ref animation._playerFrameIndex, 0, animation.PlayerKeyFrames.Count - 1);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        ImGui.SliderFloat("Frame progress##rig", ref animation._frameProgress, 0, 1);
        ImGui.SetNextItemWidth(260);
        ImGui.Combo("Rig part##rig", ref _rigPartIndex, RigEditablePartNames, RigEditablePartNames.Length);
        ImGui.SameLine();
        ImGui.Checkbox("Highlight selected part##rig", ref _highlightSelectedRigPart);
        ImGui.SameLine();
        ImGui.Checkbox("Onion skins##rig", ref _rigOnionSkinEnabled);
        if (_rigOnionSkinEnabled)
        {
            ImGui.SameLine();
            ImGui.Checkbox("Previous##rig-onion", ref _rigOnionSkinPrevious);
            ImGui.SameLine();
            ImGui.Checkbox("Next##rig-onion", ref _rigOnionSkinNext);
        }
        ImGui.SameLine();
        ImGui.Checkbox("Two-bone IK on Move##rig", ref _rigIkFollowParents);
        ImGui.TextDisabled("Shift-click a player body part to select it.");
        if (_rigIkFollowParents)
        {
            ImGui.TextDisabled("Drag a hand/foot; shoulder/hip rotates and elbow/knee bends. Rotate-mode drags stay FK.");
        }

        EnumAnimatedElement selectedPart = RigEditableParts[_rigPartIndex];
        DrawRigMotionPathControls(animationCode, animation, selectedPart);
        PLayerKeyFrame keyFrame = animation.PlayerKeyFrames[animation._playerFrameIndex];
        AnimationElement element = GetRigElement(keyFrame.Frame, selectedPart, out bool exists);

        if (!exists)
        {
            ImGui.TextWrapped($"{selectedPart} is not present on this keyframe.");
            if (ImGui.Button("Create selected part frame##rig"))
            {
                _animationHistory.BeginEdit(animationCode, animation, $"Create {selectedPart} frame");
                SetRigElement(animation, selectedPart, AnimationElement.Zero);
                _animationHistory.CommitEdit(animationCode, animation);
                _animationHistoryExplicitEditThisFrame = true;
                element = AnimationElement.Zero;
                exists = true;
            }
        }

        _detachedEditorCamera?.DrawControls("rig");
        DrawRigPoseTools(animationCode, animation, selectedPart, ref element, ref exists);

        if (!exists) return true;

        bool changed = DrawRigElementNumericEditor(selectedPart, element, out AnimationElement editedElement);
        if (changed)
        {
            SetRigElement(animation, selectedPart, editedElement);
            element = editedElement;
        }

        _rigGizmoTransform = RigElementToTransform(element);
        TryGetRigPartWorldCenter(selectedPart, out Vec3d? worldCenter);
        TransformGizmoAxes? selectedAxes = TryGetRigPartWorldAxes(selectedPart, out TransformGizmoAxes localAxes) ? localAxes : null;
        TransformGizmoAxes? parentAxes = TryGetRigPartParentWorldAxes(selectedPart, out TransformGizmoAxes parentWorldAxes) ? parentWorldAxes : null;
        TransformGizmoAxes? activeAxes = GizmoSpace switch
        {
            TransformGizmoSpace.Local => selectedAxes,
            TransformGizmoSpace.Parent => parentAxes,
            _ => null
        };
        DrawTransformGizmoControls(
            "rig-pose",
            _rigGizmoTransform,
            TransformGizmoContext.RigPart,
            transform => ApplyRigTransform(animation, selectedPart, transform),
            worldCenter: worldCenter,
            worldAxes: activeAxes,
            parentAxes: parentAxes,
            allowScale: false,
            dragStarted: () => BeginRigGizmoDrag(animationCode, animation, selectedPart),
            dragEnded: () => EndRigGizmoDrag(animationCode, animation));

        ImGui.TextDisabled($"Editing {animationCode} / keyframe {animation._playerFrameIndex} / {selectedPart}.");
        return true;
    }

    private void DrawRigPoseTools(string animationCode, Animation animation, EnumAnimatedElement selectedPart, ref AnimationElement element, ref bool exists)
    {
        ImGui.SeparatorText("Pose tools");

        if (!exists) ImGui.BeginDisabled();
        if (ImGui.Button("Copy part##rig-pose-tools"))
        {
            _rigPartClipboard = element;
            _rigPartClipboardPart = selectedPart;
            _rigPartClipboardHasValue = true;
        }
        if (!exists) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!_rigPartClipboardHasValue) ImGui.BeginDisabled();
        if (ImGui.Button("Paste part##rig-pose-tools"))
        {
            ApplyRigPoseAction(animationCode, animation, "Paste rig part", () => SetRigElement(animation, selectedPart, _rigPartClipboard));
            element = _rigPartClipboard;
            exists = true;
        }
        if (!_rigPartClipboardHasValue) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!exists) ImGui.BeginDisabled();
        if (ImGui.Button("Reset part##rig-pose-tools"))
        {
            ApplyRigPoseAction(animationCode, animation, $"Reset {selectedPart}", () => SetRigElement(animation, selectedPart, AnimationElement.Zero));
            element = AnimationElement.Zero;
            exists = true;
        }
        if (!exists) ImGui.EndDisabled();

        ImGui.SameLine();
        EnumAnimatedElement oppositePart = EnumAnimatedElement.Unknown;
        bool canMirrorPart = exists && TryGetOppositeRigPart(selectedPart, out oppositePart);
        if (!canMirrorPart) ImGui.BeginDisabled();
        if (ImGui.Button("Mirror to opposite side##rig-pose-tools"))
        {
            AnimationElement mirrored = MirrorRigElement(element);
            ApplyRigPoseAction(animationCode, animation, $"Mirror {selectedPart} to {oppositePart}", () => SetRigElement(animation, oppositePart, mirrored));
        }
        if (!canMirrorPart) ImGui.EndDisabled();

        if (_rigPartClipboardHasValue)
        {
            ImGui.TextDisabled($"Part clipboard: {_rigPartClipboardPart}");
        }
        else
        {
            ImGui.TextDisabled("Part clipboard: empty");
        }

        if (ImGui.Button("Copy full pose##rig-pose-tools"))
        {
            _rigFullPoseClipboard = animation.PlayerKeyFrames[animation._playerFrameIndex].Frame;
            _rigFullPoseClipboardHasValue = true;
        }

        ImGui.SameLine();
        if (!_rigFullPoseClipboardHasValue) ImGui.BeginDisabled();
        if (ImGui.Button("Paste full pose##rig-pose-tools"))
        {
            ApplyRigPoseAction(animationCode, animation, "Paste full pose", () => SetCurrentRigFrame(animation, _rigFullPoseClipboard));
            element = GetRigElement(animation.PlayerKeyFrames[animation._playerFrameIndex].Frame, selectedPart, out exists);
        }
        if (!_rigFullPoseClipboardHasValue) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Mirror full pose##rig-pose-tools"))
        {
            ApplyRigPoseAction(animationCode, animation, "Mirror full pose", () => SetCurrentRigFrame(animation, MirrorRigFrame(animation.PlayerKeyFrames[animation._playerFrameIndex].Frame)));
            element = GetRigElement(animation.PlayerKeyFrames[animation._playerFrameIndex].Frame, selectedPart, out exists);
        }

        ImGui.TextDisabled(_rigFullPoseClipboardHasValue ? "Full pose clipboard: populated" : "Full pose clipboard: empty");
    }

    private void DrawRigMotionPathControls(string animationCode, Animation animation, EnumAnimatedElement selectedPart)
    {
        ImGui.SeparatorText("Motion paths");
        bool enabled = _rigMotionPathsEnabled;
        if (ImGui.Checkbox("Show motion paths##rig-motion", ref enabled))
        {
            _rigMotionPathsEnabled = enabled;
            InvalidateRigMotionPathCache();
        }

        if (!_rigMotionPathsEnabled)
        {
            _rigMotionPathAnimation = null;
            return;
        }

        _rigMotionPathAnimation = animation;
        _rigMotionPathAnimationCode = animationCode;

        bool changed = false;
        changed |= ImGui.Checkbox("Head##rig-motion", ref _rigMotionPathHead);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Right hand##rig-motion", ref _rigMotionPathRightHand);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Left hand##rig-motion", ref _rigMotionPathLeftHand);
        changed |= ImGui.Checkbox("Right foot##rig-motion", ref _rigMotionPathRightFoot);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Left foot##rig-motion", ref _rigMotionPathLeftFoot);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Weapon tip##rig-motion", ref _rigMotionPathWeaponTip);
        changed |= ImGui.Checkbox("Main-hand anchor##rig-motion", ref _rigMotionPathMainHandAnchor);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Off-hand anchor##rig-motion", ref _rigMotionPathOffHandAnchor);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Split past/future##rig-motion", ref _rigMotionPathSplit);

        ImGui.SetNextItemWidth(180);
        changed |= ImGui.SliderInt("Samples##rig-motion", ref _rigMotionPathSamples, 8, 240);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        changed |= ImGui.SliderFloat("Trail thickness##rig-motion", ref _rigMotionPathThickness, 1f, 5f, "%.1f");

        if (ImGui.Button("Focus selected path##rig-motion"))
        {
            FocusRigMotionPath(selectedPart);
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear cached paths##rig-motion"))
        {
            InvalidateRigMotionPathCache();
        }

        if (_rigMotionPathWeaponTip)
        {
            ImGui.TextDisabled("Weapon tip currently falls back to the main-hand item anchor when no tip marker exists.");
        }

        if (changed) InvalidateRigMotionPathCache();
    }

    private void FocusRigMotionPath(EnumAnimatedElement selectedPart)
    {
        _rigMotionPathHead = selectedPart == EnumAnimatedElement.Head;
        _rigMotionPathRightHand = selectedPart is EnumAnimatedElement.LowerArmR or EnumAnimatedElement.ItemAnchor;
        _rigMotionPathLeftHand = selectedPart is EnumAnimatedElement.LowerArmL or EnumAnimatedElement.ItemAnchorL;
        _rigMotionPathRightFoot = selectedPart == EnumAnimatedElement.LowerFootR;
        _rigMotionPathLeftFoot = selectedPart == EnumAnimatedElement.LowerFootL;
        _rigMotionPathMainHandAnchor = selectedPart == EnumAnimatedElement.ItemAnchor;
        _rigMotionPathOffHandAnchor = selectedPart == EnumAnimatedElement.ItemAnchorL;
        _rigMotionPathWeaponTip = selectedPart == EnumAnimatedElement.ItemAnchor;

        if (!_rigMotionPathHead && !_rigMotionPathRightHand && !_rigMotionPathLeftHand &&
            !_rigMotionPathRightFoot && !_rigMotionPathLeftFoot && !_rigMotionPathMainHandAnchor &&
            !_rigMotionPathOffHandAnchor && !_rigMotionPathWeaponTip)
        {
            _rigMotionPathHead = true;
        }
    }

    private void ApplyRigPoseAction(string animationCode, Animation animation, string label, Action action)
    {
        _animationHistory.BeginEdit(animationCode, animation, label);
        action();
        _animationHistory.CommitEdit(animationCode, animation);
        _animationHistoryExplicitEditThisFrame = true;
    }

    private bool DrawRigElementNumericEditor(EnumAnimatedElement selectedPart, AnimationElement element, out AnimationElement editedElement)
    {
        float speed = ImGui.GetIO().KeysDown[(int)ImGuiKey.LeftShift] ? 0.05f : 0.25f;
        NVector3 offset = new(element.OffsetX ?? 0, element.OffsetY ?? 0, element.OffsetZ ?? 0);
        NVector3 rotation = new(element.RotationX ?? 0, element.RotationY ?? 0, element.RotationZ ?? 0);

        bool changed = ImGui.DragFloat3($"Offset px##rig-{selectedPart}", ref offset, speed, -64, 64);
        changed |= ImGui.DragFloat3($"Rotation deg##rig-{selectedPart}", ref rotation, speed * 4, -180, 180);

        editedElement = new AnimationElement(offset.X, offset.Y, offset.Z, rotation.X, rotation.Y, rotation.Z);
        return changed;
    }

    private void ApplyRigTransform(Animation animation, EnumAnimatedElement selectedPart, ModelTransform transform)
    {
        AnimationElement element = new(
            transform.Translation.X * 16f,
            transform.Translation.Y * 16f,
            transform.Translation.Z * 16f,
            transform.Rotation.X,
            transform.Rotation.Y,
            transform.Rotation.Z);

        if (_rigIkFollowParents && GizmoMode == TransformGizmoMode.Move && ApplyRigIkMove(animation, selectedPart, element)) return;

        SetRigElement(animation, selectedPart, element);
    }

    private void BeginRigGizmoDrag(string animationCode, Animation animation, EnumAnimatedElement selectedPart)
    {
        int index = animation._playerFrameIndex;
        if (index < 0 || index >= animation.PlayerKeyFrames.Count)
        {
            ClearRigGizmoDrag();
            return;
        }

        if (!string.IsNullOrEmpty(animationCode))
        {
            _animationHistory.BeginEdit(animationCode, animation, $"Rig gizmo {selectedPart}");
            _animationHistoryExternalDragActive = true;
        }
        _rigIkDragActive = true;
        _rigIkDragKeyframeIndex = index;
        _rigIkDragPart = selectedPart;
        _rigIkDragStartFrame = animation.PlayerKeyFrames[index].Frame;
        _rigIkDragCache = TryCreateRigIkDragCache(selectedPart, _rigIkDragStartFrame, out RigIkDragCache? cache) ? cache : null;
    }

    private void EndRigGizmoDrag(string animationCode, Animation animation)
    {
        _animationHistoryExternalDragActive = false;
        if (!string.IsNullOrEmpty(animationCode))
        {
            _animationHistory.CommitEdit(animationCode, animation);
            _animationHistoryExplicitEditThisFrame = true;
        }
        ClearRigGizmoDrag();
    }

    private void ClearRigGizmoDrag()
    {
        _animationHistoryExternalDragActive = false;
        _rigIkDragActive = false;
        _rigIkDragKeyframeIndex = -1;
        _rigIkDragPart = EnumAnimatedElement.Unknown;
        _rigIkDragStartFrame = PlayerFrame.Zero;
        _rigIkDragCache = null;
    }

    private bool ApplyRigIkMove(Animation animation, EnumAnimatedElement selectedPart, AnimationElement desiredElement)
    {
        if (!TryGetIkChain(selectedPart, out RigIkChain chain)) return false;

        int index = animation._playerFrameIndex;
        if (!_rigIkDragActive || _rigIkDragKeyframeIndex != index || _rigIkDragPart != selectedPart)
        {
            BeginRigGizmoDrag("", animation, selectedPart);
        }

        if (!_rigIkDragActive || index < 0 || index >= animation.PlayerKeyFrames.Count) return false;
        if (_rigIkDragCache == null || _rigIkDragCache.Chain.SelectedPart != selectedPart) return false;
        if (!TrySolveRigIk(_rigIkDragCache, desiredElement, out AnimationElement solvedUpper, out AnimationElement solvedLower)) return false;

        PLayerKeyFrame keyFrame = animation.PlayerKeyFrames[index];
        PlayerFrame solvedFrame = SetRigElement(_rigIkDragStartFrame, chain.UpperPart, solvedUpper);
        solvedFrame = SetRigElement(solvedFrame, chain.LowerPart, solvedLower);
        animation.PlayerKeyFrames[index] = new PLayerKeyFrame(solvedFrame, keyFrame.Time, keyFrame.EasingFunction, keyFrame.EasingType, keyFrame.FrameProgressRange);
        animation._playerFrameEdited = true;
        return true;
    }

    private bool TryCreateRigIkDragCache(EnumAnimatedElement selectedPart, PlayerFrame startFrame, out RigIkDragCache? cache)
    {
        cache = null;
        if (!TryGetIkChain(selectedPart, out RigIkChain chain)) return false;
        if (!TryGetRigPartWorldInfo(chain.UpperPart, out RigPoseWorldInfo upperInfo)) return false;
        if (!TryGetRigPartWorldInfo(chain.LowerPart, out RigPoseWorldInfo lowerInfo)) return false;
        if (!TryGetRigPartWorldOrigin(selectedPart, out Vec3d? selectedOrigin) || selectedOrigin == null) return false;
        if (!TryGetRigPartWorldAxes(selectedPart, out TransformGizmoAxes selectedAxes))
        {
            selectedAxes = new TransformGizmoAxes(new Vec3d(1, 0, 0), new Vec3d(0, 1, 0), new Vec3d(0, 0, 1));
        }

        Vec3d endOrigin;
        if (chain.EndPart != EnumAnimatedElement.Unknown && TryGetRigPartWorldOrigin(chain.EndPart, out Vec3d? realEndOrigin) && realEndOrigin != null)
        {
            endOrigin = realEndOrigin;
        }
        else if (!TryGetDistalEndpointWorld(chain.LowerPart, lowerInfo.Origin, out endOrigin))
        {
            return false;
        }

        double upperOriginDistance = Distance(upperInfo.Origin, lowerInfo.Origin);
        double lowerOriginDistance = Distance(lowerInfo.Origin, endOrigin);
        double upperLength = GetBoneLength(upperInfo.Pose.ForElement, upperOriginDistance);
        double lowerLength = GetBoneLength(lowerInfo.Pose.ForElement, lowerOriginDistance);
        if (upperLength <= 0.0001 || lowerLength <= 0.0001) return false;

        AnimationElement selectedStart = GetRigElement(startFrame, selectedPart, out bool selectedExists);
        if (!selectedExists) selectedStart = AnimationElement.Zero;
        AnimationElement upperStart = GetRigElement(startFrame, chain.UpperPart, out bool upperExists);
        if (!upperExists) upperStart = AnimationElement.Zero;
        AnimationElement lowerStart = GetRigElement(startFrame, chain.LowerPart, out bool lowerExists);
        if (!lowerExists) lowerStart = AnimationElement.Zero;

        Vec3d rootToEnd = SafeNormalize(Sub(endOrigin, upperInfo.Origin), upperInfo.WorldRotation.TransformDirection(new Vec3d(0, 0, 1)));
        Vec3d poleHint = ProjectOntoPlane(Sub(lowerInfo.Origin, upperInfo.Origin), rootToEnd);
        if (poleHint.LengthSq() < 0.000001)
        {
            // Preserve the side's natural bend when possible; right/left chains mirror through this cached pole.
            poleHint = ProjectOntoPlane(upperInfo.WorldRotation.TransformDirection(new Vec3d(0, 0, chain.PoleSign)), rootToEnd);
        }
        poleHint = SafeNormalize(poleHint, SafePoleFallback(rootToEnd));

        cache = new RigIkDragCache(
            chain,
            upperInfo,
            lowerInfo,
            selectedOrigin,
            endOrigin,
            selectedAxes,
            selectedStart,
            upperStart,
            lowerStart,
            upperLength,
            lowerLength,
            poleHint);
        return true;
    }

    private bool TrySolveRigIk(RigIkDragCache cache, AnimationElement desiredElement, out AnimationElement solvedUpper, out AnimationElement solvedLower)
    {
        solvedUpper = cache.UpperStartElement;
        solvedLower = cache.LowerStartElement;

        Vec3d selectedTarget = GetDesiredSelectedWorldOrigin(cache, desiredElement);
        Vec3d target = Add(cache.EndOrigin, Sub(selectedTarget, cache.SelectedOrigin));
        Vec3d root = cache.UpperInfo.Origin;
        Vec3d rootToTarget = Sub(target, root);
        double requestedDistance = rootToTarget.Length();
        if (requestedDistance < 0.0001) return false;

        Vec3d axis = Scale(rootToTarget, 1.0 / requestedDistance);
        double epsilon = 0.0001;
        double minDistance = Math.Abs(cache.UpperLength - cache.LowerLength) + epsilon;
        double maxDistance = cache.UpperLength + cache.LowerLength - epsilon;
        double distance = Math.Clamp(requestedDistance, minDistance, maxDistance);
        target = Add(root, Scale(axis, distance));

        double upperSquared = cache.UpperLength * cache.UpperLength;
        double lowerSquared = cache.LowerLength * cache.LowerLength;
        double along = (upperSquared - lowerSquared + distance * distance) / (2.0 * distance);
        double heightSquared = Math.Max(0, upperSquared - along * along);
        double height = Math.Sqrt(heightSquared);

        Vec3d pole = ProjectOntoPlane(cache.PoleHint, axis);
        if (pole.LengthSq() < 0.000001) pole = ProjectOntoPlane(new Vec3d(0, 1, 0), axis);
        if (pole.LengthSq() < 0.000001) pole = ProjectOntoPlane(new Vec3d(1, 0, 0), axis);
        pole = SafeNormalize(pole, SafePoleFallback(axis));

        Vec3d joint = Add(Add(root, Scale(axis, along)), Scale(pole, height));

        Vec3d upperStartDirection = SafeNormalize(Sub(cache.LowerInfo.Origin, cache.UpperInfo.Origin), cache.UpperInfo.WorldRotation.TransformDirection(new Vec3d(0, 0, 1)));
        Vec3d upperTargetDirection = SafeNormalize(Sub(joint, cache.UpperInfo.Origin), upperStartDirection);
        Vec3d lowerStartDirection = SafeNormalize(Sub(cache.EndOrigin, cache.LowerInfo.Origin), cache.LowerInfo.WorldRotation.TransformDirection(new Vec3d(0, 0, 1)));
        Vec3d lowerTargetDirection = SafeNormalize(Sub(target, joint), lowerStartDirection);

        RigIkMatrix3 upperWorld = RigIkMatrix3.FromTo(upperStartDirection, upperTargetDirection).Mul(cache.UpperInfo.WorldRotation).Orthonormalized();
        RigIkMatrix3 upperLocal = cache.UpperInfo.ParentWorldRotation.Inverted().Mul(upperWorld).Orthonormalized();
        Vec3d upperEuler = Sub(upperLocal.ToEulerDegrees(), cache.UpperInfo.BaseRotationDegrees);

        RigIkMatrix3 lowerWorld = RigIkMatrix3.FromTo(lowerStartDirection, lowerTargetDirection).Mul(cache.LowerInfo.WorldRotation).Orthonormalized();
        RigIkMatrix3 lowerLocal = upperWorld.Inverted().Mul(lowerWorld).Orthonormalized();
        Vec3d lowerEuler = Sub(lowerLocal.ToEulerDegrees(), cache.LowerInfo.BaseRotationDegrees);

        solvedUpper = WithRotation(cache.UpperStartElement, upperEuler);
        solvedLower = WithRotation(cache.LowerStartElement, lowerEuler);
        return true;
    }

    private static bool TryGetIkChain(EnumAnimatedElement selectedPart, out RigIkChain chain)
    {
        chain = selectedPart switch
        {
            EnumAnimatedElement.ItemAnchor or EnumAnimatedElement.LowerArmR => new RigIkChain(selectedPart, EnumAnimatedElement.UpperArmR, EnumAnimatedElement.LowerArmR, EnumAnimatedElement.ItemAnchor, 1),
            EnumAnimatedElement.ItemAnchorL or EnumAnimatedElement.LowerArmL => new RigIkChain(selectedPart, EnumAnimatedElement.UpperArmL, EnumAnimatedElement.LowerArmL, EnumAnimatedElement.ItemAnchorL, -1),
            EnumAnimatedElement.LowerFootR => new RigIkChain(selectedPart, EnumAnimatedElement.UpperFootR, EnumAnimatedElement.LowerFootR, EnumAnimatedElement.Unknown, 1),
            EnumAnimatedElement.LowerFootL => new RigIkChain(selectedPart, EnumAnimatedElement.UpperFootL, EnumAnimatedElement.LowerFootL, EnumAnimatedElement.Unknown, -1),
            _ => default
        };

        return chain.UpperPart != EnumAnimatedElement.Unknown;
    }

    private static AnimationElement WithRotation(AnimationElement source, Vec3d rotation)
    {
        return new AnimationElement(
            source.OffsetX,
            source.OffsetY,
            source.OffsetZ,
            NormalizeDegrees((float)rotation.X),
            NormalizeDegrees((float)rotation.Y),
            NormalizeDegrees((float)rotation.Z));
    }

    private static float NormalizeDegrees(float degrees)
    {
        while (degrees > 180) degrees -= 360;
        while (degrees < -180) degrees += 360;
        return degrees;
    }

    private static ModelTransform RigElementToTransform(AnimationElement element)
    {
        ModelTransform transform = CreateDefaultTransform();
        transform.Origin.Set(0, 0, 0);
        transform.Translation.Set((element.OffsetX ?? 0) / 16f, (element.OffsetY ?? 0) / 16f, (element.OffsetZ ?? 0) / 16f);
        transform.Rotation.Set(element.RotationX ?? 0, element.RotationY ?? 0, element.RotationZ ?? 0);
        transform.ScaleXYZ.Set(1, 1, 1);
        return transform;
    }

    private void SetRigElement(Animation animation, EnumAnimatedElement selectedPart, AnimationElement element)
    {
        int index = animation._playerFrameIndex;
        if (index < 0 || index >= animation.PlayerKeyFrames.Count) return;

        PLayerKeyFrame keyFrame = animation.PlayerKeyFrames[index];
        PlayerFrame frame = SetRigElement(keyFrame.Frame, selectedPart, element);
        animation.PlayerKeyFrames[index] = new PLayerKeyFrame(frame, keyFrame.Time, keyFrame.EasingFunction, keyFrame.EasingType, keyFrame.FrameProgressRange);
        animation._playerFrameEdited = true;
        InvalidateRigMotionPathCache();
    }

    private static void SetCurrentRigFrame(Animation animation, PlayerFrame frame)
    {
        int index = animation._playerFrameIndex;
        if (index < 0 || index >= animation.PlayerKeyFrames.Count) return;

        PLayerKeyFrame keyFrame = animation.PlayerKeyFrames[index];
        animation.PlayerKeyFrames[index] = new PLayerKeyFrame(frame, keyFrame.Time, keyFrame.EasingFunction, keyFrame.EasingType, keyFrame.FrameProgressRange);
        animation._playerFrameEdited = true;
    }

    private static PlayerFrame SetRigElement(PlayerFrame frame, EnumAnimatedElement selectedPart, AnimationElement element)
    {
        RightHandFrame? right = frame.RightHand;
        LeftHandFrame? left = frame.LeftHand;
        OtherPartsFrame? other = frame.OtherParts;
        AnimationElement? upperTorso = frame.UpperTorso;
        AnimationElement? lowerTorso = frame.LowerTorso;
        AnimationElement? detachedAnchor = frame.DetachedAnchorFrame;

        switch (selectedPart)
        {
            case EnumAnimatedElement.ItemAnchor:
            case EnumAnimatedElement.LowerArmR:
            case EnumAnimatedElement.UpperArmR:
                RightHandFrame r = right ?? RightHandFrame.Zero;
                right = selectedPart switch
                {
                    EnumAnimatedElement.ItemAnchor => new RightHandFrame(element, r.LowerArmR, r.UpperArmR),
                    EnumAnimatedElement.LowerArmR => new RightHandFrame(r.ItemAnchor, element, r.UpperArmR),
                    _ => new RightHandFrame(r.ItemAnchor, r.LowerArmR, element)
                };
                break;
            case EnumAnimatedElement.ItemAnchorL:
            case EnumAnimatedElement.LowerArmL:
            case EnumAnimatedElement.UpperArmL:
                LeftHandFrame l = left ?? LeftHandFrame.Zero;
                left = selectedPart switch
                {
                    EnumAnimatedElement.ItemAnchorL => new LeftHandFrame(element, l.LowerArmL, l.UpperArmL),
                    EnumAnimatedElement.LowerArmL => new LeftHandFrame(l.ItemAnchorL, element, l.UpperArmL),
                    _ => new LeftHandFrame(l.ItemAnchorL, l.LowerArmL, element)
                };
                break;
            case EnumAnimatedElement.Neck:
            case EnumAnimatedElement.Head:
            case EnumAnimatedElement.UpperFootR:
            case EnumAnimatedElement.UpperFootL:
            case EnumAnimatedElement.LowerFootR:
            case EnumAnimatedElement.LowerFootL:
                OtherPartsFrame o = other ?? OtherPartsFrame.Zero;
                other = selectedPart switch
                {
                    EnumAnimatedElement.Neck => new OtherPartsFrame(element, o.Head, o.UpperFootR, o.UpperFootL, o.LowerFootR, o.LowerFootL),
                    EnumAnimatedElement.Head => new OtherPartsFrame(o.Neck, element, o.UpperFootR, o.UpperFootL, o.LowerFootR, o.LowerFootL),
                    EnumAnimatedElement.UpperFootR => new OtherPartsFrame(o.Neck, o.Head, element, o.UpperFootL, o.LowerFootR, o.LowerFootL),
                    EnumAnimatedElement.UpperFootL => new OtherPartsFrame(o.Neck, o.Head, o.UpperFootR, element, o.LowerFootR, o.LowerFootL),
                    EnumAnimatedElement.LowerFootR => new OtherPartsFrame(o.Neck, o.Head, o.UpperFootR, o.UpperFootL, element, o.LowerFootL),
                    _ => new OtherPartsFrame(o.Neck, o.Head, o.UpperFootR, o.UpperFootL, o.LowerFootR, element)
                };
                break;
            case EnumAnimatedElement.UpperTorso:
                upperTorso = element;
                break;
            case EnumAnimatedElement.LowerTorso:
                lowerTorso = element;
                break;
            case EnumAnimatedElement.DetachedAnchor:
                detachedAnchor = element;
                break;
        }

        return new PlayerFrame(
            right,
            left,
            other,
            upperTorso,
            detachedAnchor,
            frame.DetachedAnchor,
            frame.SwitchArms,
            frame.PitchFollow,
            frame.FovMultiplier,
            frame.BobbingAmplitude,
            detachedAnchorFollow: frame.DetachedAnchorFollow,
            lowerTorso: lowerTorso);
    }

    private static bool TryGetOppositeRigPart(EnumAnimatedElement selectedPart, out EnumAnimatedElement oppositePart)
    {
        oppositePart = selectedPart switch
        {
            EnumAnimatedElement.ItemAnchor => EnumAnimatedElement.ItemAnchorL,
            EnumAnimatedElement.ItemAnchorL => EnumAnimatedElement.ItemAnchor,
            EnumAnimatedElement.LowerArmR => EnumAnimatedElement.LowerArmL,
            EnumAnimatedElement.LowerArmL => EnumAnimatedElement.LowerArmR,
            EnumAnimatedElement.UpperArmR => EnumAnimatedElement.UpperArmL,
            EnumAnimatedElement.UpperArmL => EnumAnimatedElement.UpperArmR,
            EnumAnimatedElement.UpperFootR => EnumAnimatedElement.UpperFootL,
            EnumAnimatedElement.UpperFootL => EnumAnimatedElement.UpperFootR,
            EnumAnimatedElement.LowerFootR => EnumAnimatedElement.LowerFootL,
            EnumAnimatedElement.LowerFootL => EnumAnimatedElement.LowerFootR,
            _ => EnumAnimatedElement.Unknown
        };

        return oppositePart != EnumAnimatedElement.Unknown;
    }

    private static AnimationElement MirrorRigElement(AnimationElement element)
    {
        return new AnimationElement(
            Negate(element.OffsetX),
            element.OffsetY,
            element.OffsetZ,
            element.RotationX,
            Negate(element.RotationY),
            Negate(element.RotationZ));
    }

    private static PlayerFrame MirrorRigFrame(PlayerFrame frame)
    {
        RightHandFrame? right = frame.LeftHand == null
            ? null
            : new RightHandFrame(
                MirrorRigElement(frame.LeftHand.Value.ItemAnchorL),
                MirrorRigElement(frame.LeftHand.Value.LowerArmL),
                MirrorRigElement(frame.LeftHand.Value.UpperArmL));

        LeftHandFrame? left = frame.RightHand == null
            ? null
            : new LeftHandFrame(
                MirrorRigElement(frame.RightHand.Value.ItemAnchor),
                MirrorRigElement(frame.RightHand.Value.LowerArmR),
                MirrorRigElement(frame.RightHand.Value.UpperArmR));

        OtherPartsFrame? other = frame.OtherParts == null
            ? null
            : new OtherPartsFrame(
                MirrorRigElement(frame.OtherParts.Value.Neck),
                MirrorRigElement(frame.OtherParts.Value.Head),
                MirrorRigElement(frame.OtherParts.Value.UpperFootL),
                MirrorRigElement(frame.OtherParts.Value.UpperFootR),
                MirrorRigElement(frame.OtherParts.Value.LowerFootL),
                MirrorRigElement(frame.OtherParts.Value.LowerFootR));

        return new PlayerFrame(
            right,
            left,
            other,
            frame.UpperTorso == null ? null : MirrorRigElement(frame.UpperTorso.Value),
            frame.DetachedAnchorFrame == null ? null : MirrorRigElement(frame.DetachedAnchorFrame.Value),
            frame.DetachedAnchor,
            frame.SwitchArms,
            frame.PitchFollow,
            frame.FovMultiplier,
            frame.BobbingAmplitude,
            frame.DetachedAnchorFollow,
            frame.LowerTorso == null ? null : MirrorRigElement(frame.LowerTorso.Value));
    }

    private static float? Negate(float? value) => value == null ? null : -value.Value;

    private static AnimationElement GetRigElement(PlayerFrame frame, EnumAnimatedElement selectedPart, out bool exists)
    {
        exists = true;
        switch (selectedPart)
        {
            case EnumAnimatedElement.ItemAnchor when frame.RightHand != null:
                return frame.RightHand.Value.ItemAnchor;
            case EnumAnimatedElement.LowerArmR when frame.RightHand != null:
                return frame.RightHand.Value.LowerArmR;
            case EnumAnimatedElement.UpperArmR when frame.RightHand != null:
                return frame.RightHand.Value.UpperArmR;
            case EnumAnimatedElement.ItemAnchorL when frame.LeftHand != null:
                return frame.LeftHand.Value.ItemAnchorL;
            case EnumAnimatedElement.LowerArmL when frame.LeftHand != null:
                return frame.LeftHand.Value.LowerArmL;
            case EnumAnimatedElement.UpperArmL when frame.LeftHand != null:
                return frame.LeftHand.Value.UpperArmL;
            case EnumAnimatedElement.Neck when frame.OtherParts != null:
                return frame.OtherParts.Value.Neck;
            case EnumAnimatedElement.Head when frame.OtherParts != null:
                return frame.OtherParts.Value.Head;
            case EnumAnimatedElement.UpperFootR when frame.OtherParts != null:
                return frame.OtherParts.Value.UpperFootR;
            case EnumAnimatedElement.UpperFootL when frame.OtherParts != null:
                return frame.OtherParts.Value.UpperFootL;
            case EnumAnimatedElement.LowerFootR when frame.OtherParts != null:
                return frame.OtherParts.Value.LowerFootR;
            case EnumAnimatedElement.LowerFootL when frame.OtherParts != null:
                return frame.OtherParts.Value.LowerFootL;
            case EnumAnimatedElement.UpperTorso when frame.UpperTorso != null:
                return frame.UpperTorso.Value;
            case EnumAnimatedElement.LowerTorso when frame.LowerTorso != null:
                return frame.LowerTorso.Value;
            case EnumAnimatedElement.DetachedAnchor when frame.DetachedAnchorFrame != null:
                return frame.DetachedAnchorFrame.Value;
            default:
                exists = false;
                return AnimationElement.Zero;
        }
    }

    private bool TryGetRigPartWorldCenter(EnumAnimatedElement selectedPart, out Vec3d? center)
    {
        center = null;
        return TryGetRigPartWorldOrigin(selectedPart, out center);
    }

    private bool TryGetRigPartWorldAxes(EnumAnimatedElement selectedPart, out TransformGizmoAxes axes)
    {
        axes = default;
        if (!TryGetRigPartPose(selectedPart, out EntityPlayer playerEntity, out ElementPose? pose)) return false;
        if (pose?.ForElement == null) return false;

        Matrixf matrix = new();
        BuildPlayerModelMatrix(matrix, playerEntity);
        matrix.Mul(pose.AnimModelMatrix);

        Vec3d origin = TransformLocalPoint(matrix, 0, 0, 0);
        Vec3d x = Sub(TransformLocalPoint(matrix, 1, 0, 0), origin);
        Vec3d y = Sub(TransformLocalPoint(matrix, 0, 1, 0), origin);
        Vec3d z = Sub(TransformLocalPoint(matrix, 0, 0, 1), origin);

        if (x.LengthSq() < 0.000001 || y.LengthSq() < 0.000001 || z.LengthSq() < 0.000001) return false;

        axes = new TransformGizmoAxes(x.Normalize(), y.Normalize(), z.Normalize());
        return true;
    }

    private bool TryGetRigPartParentWorldAxes(EnumAnimatedElement selectedPart, out TransformGizmoAxes axes)
    {
        axes = default;
        if (!TryGetRigPartPose(selectedPart, out EntityPlayer playerEntity, out ElementPose? pose, out ElementPose? parentPose)) return false;
        if (pose?.ForElement == null || parentPose == null) return false;

        Matrixf matrix = new();
        BuildPlayerModelMatrix(matrix, playerEntity);
        matrix.Mul(parentPose.AnimModelMatrix);

        Vec3d origin = TransformLocalPoint(matrix, 0, 0, 0);
        Vec3d x = Sub(TransformLocalPoint(matrix, 1, 0, 0), origin);
        Vec3d y = Sub(TransformLocalPoint(matrix, 0, 1, 0), origin);
        Vec3d z = Sub(TransformLocalPoint(matrix, 0, 0, 1), origin);

        if (x.LengthSq() < 0.000001 || y.LengthSq() < 0.000001 || z.LengthSq() < 0.000001) return false;

        axes = new TransformGizmoAxes(x.Normalize(), y.Normalize(), z.Normalize());
        return true;
    }

    private bool TryGetRigPartWorldOrigin(EnumAnimatedElement selectedPart, out Vec3d? origin)
    {
        origin = null;
        if (!TryGetRigPartPose(selectedPart, out EntityPlayer playerEntity, out ElementPose? pose)) return false;
        if (pose?.ForElement == null) return false;

        Matrixf matrix = new();
        BuildPlayerModelMatrix(matrix, playerEntity);
        matrix.Mul(pose.AnimModelMatrix);

        Vec3f localOrigin = GetElementLocalRotationOrigin(pose);
        Vec4f relative = matrix.TransformVector(new Vec4f(localOrigin.X, localOrigin.Y, localOrigin.Z, 1f));
        Vec3d camera = playerEntity.CameraPos;
        origin = new Vec3d(camera.X + relative.X, camera.Y + relative.Y, camera.Z + relative.Z);
        return true;
    }

    private static Vec3d TransformLocalPoint(Matrixf matrix, double x, double y, double z)
    {
        Vec4f relative = matrix.TransformVector(new Vec4f((float)x, (float)y, (float)z, 1f));
        return new Vec3d(relative.X, relative.Y, relative.Z);
    }

    internal bool TryGetRigPartHighlightCorners(out Vec3d[] corners)
    {
        corners = Array.Empty<Vec3d>();
        if (!_rigPoseEditorEnabled || !_highlightSelectedRigPart) return false;
        if (_rigPartIndex < 0 || _rigPartIndex >= RigEditableParts.Length) return false;

        return TryGetRigPartWorldBox(RigEditableParts[_rigPartIndex], out corners);
    }

    internal bool TryGetRigOnionSkinModels(out IReadOnlyList<TransformGizmoGhostModel> models)
    {
        List<TransformGizmoGhostModel> result = [];
        models = result;

        if (!_showAnimationEditor || !_rigPoseEditorEnabled || !_rigOnionSkinEnabled) return false;
        if (_rigOnionSkinAnimation == null || _rigOnionSkinAnimation.PlayerKeyFrames.Count == 0) return false;

        int currentIndex = Math.Clamp(_rigOnionSkinAnimation._playerFrameIndex, 0, _rigOnionSkinAnimation.PlayerKeyFrames.Count - 1);
        if (_rigOnionSkinPrevious && currentIndex > 0)
        {
            AppendRigOnionSkinFrame(_rigOnionSkinAnimation.PlayerKeyFrames[currentIndex - 1].Frame, RigOnionPreviousColor, result);
        }

        if (_rigOnionSkinNext && currentIndex + 1 < _rigOnionSkinAnimation.PlayerKeyFrames.Count)
        {
            AppendRigOnionSkinFrame(_rigOnionSkinAnimation.PlayerKeyFrames[currentIndex + 1].Frame, RigOnionNextColor, result);
        }

        models = result;
        return result.Count > 0;
    }

    internal bool TryGetRigMotionPaths(out IReadOnlyList<TransformGizmoMotionPath> paths)
    {
        paths = _rigMotionPathCache;
        if (!_showAnimationEditor || !_rigPoseEditorEnabled || !_rigMotionPathsEnabled) return false;
        if (_rigMotionPathAnimation == null || _rigMotionPathAnimation.PlayerKeyFrames.Count == 0) return false;

        List<RigMotionPathTarget> targets = GetRigMotionPathTargets(RigEditableParts[Math.Clamp(_rigPartIndex, 0, RigEditableParts.Length - 1)]);
        if (targets.Count == 0) return false;

        Animation animation = _rigMotionPathAnimation;
        string cacheKey = BuildRigMotionPathCacheKey(animation, targets);
        if (!string.Equals(cacheKey, _rigMotionPathCacheKey, StringComparison.Ordinal))
        {
            _rigMotionPathCache.Clear();
            _rigMotionPathCache.AddRange(BuildRigMotionPaths(animation, targets));
            _rigMotionPathCacheKey = cacheKey;
        }

        paths = _rigMotionPathCache;
        return _rigMotionPathCache.Count > 0;
    }

    private string BuildRigMotionPathCacheKey(Animation animation, IReadOnlyList<RigMotionPathTarget> targets)
    {
        int contentHash = StringComparer.Ordinal.GetHashCode(AnimationEditorHistory.Serialize(animation));
        double currentMs = GetEditorFrameTimeMs(animation);
        return $"{_rigMotionPathAnimationCode}|{contentHash}|{_rigMotionPathSamples}|{_rigMotionPathThickness:F2}|{_rigMotionPathSplit}|{_rigPartIndex}|{currentMs:F1}|{string.Join(",", targets.Select(target => target.Label))}";
    }

    private List<TransformGizmoMotionPath> BuildRigMotionPaths(Animation animation, IReadOnlyList<RigMotionPathTarget> targets)
    {
        int sampleCount = Math.Clamp(_rigMotionPathSamples, 8, 240);
        List<Vec3d>[] pointsByTarget = targets.Select(_ => new List<Vec3d>(sampleCount)).ToArray();

        for (int sample = 0; sample < sampleCount; sample++)
        {
            float progress = sampleCount <= 1 ? 0 : sample / (float)(sampleCount - 1);
            PlayerFrame frame = animation.StillFrame(progress).Player;
            if (!TryBuildRigPoseTreeForFrame(frame, out EntityPlayer playerEntity, out List<ElementPose> poses)) continue;

            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                if (TryGetRigPartWorldOriginFromPoses(playerEntity, poses, targets[targetIndex].Part, out Vec3d origin))
                {
                    pointsByTarget[targetIndex].Add(origin);
                }
            }
        }

        PlayerFrame currentFrame = animation.StillPlayerFrame(animation._playerFrameIndex, animation._frameProgress).Player;
        TryBuildRigPoseTreeForFrame(currentFrame, out EntityPlayer currentPlayerEntity, out List<ElementPose> currentPoses);
        double playerDurationMs = Math.Max(0.0001, GetEditorPlayerDurationMs(animation));
        double currentFraction = Math.Clamp(GetEditorFrameTimeMs(animation) / playerDurationMs, 0, 1);

        List<TransformGizmoMotionPath> result = [];
        for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
        {
            List<Vec3d> points = pointsByTarget[targetIndex];
            if (points.Count < 2) continue;

            RigMotionPathTarget target = targets[targetIndex];
            Vec3d? currentPoint = null;
            if (currentPoses.Count > 0 && TryGetRigPartWorldOriginFromPoses(currentPlayerEntity, currentPoses, target.Part, out Vec3d origin))
            {
                currentPoint = origin;
            }

            int pastColor = target.Selected ? RigMotionPathSelectedPastColor : RigMotionPathPastColor;
            int futureColor = target.Selected ? RigMotionPathSelectedFutureColor : RigMotionPathFutureColor;
            result.Add(new TransformGizmoMotionPath(points, currentPoint, pastColor, futureColor, RigMotionPathCurrentColor, _rigMotionPathThickness, _rigMotionPathSplit, currentFraction));
        }

        return result;
    }

    private List<RigMotionPathTarget> GetRigMotionPathTargets(EnumAnimatedElement selectedPart)
    {
        List<RigMotionPathTarget> targets = [];
        if (_rigMotionPathHead) targets.Add(new RigMotionPathTarget("Head", EnumAnimatedElement.Head, selectedPart == EnumAnimatedElement.Head));
        if (_rigMotionPathRightHand) targets.Add(new RigMotionPathTarget("Right hand", EnumAnimatedElement.LowerArmR, selectedPart is EnumAnimatedElement.LowerArmR or EnumAnimatedElement.ItemAnchor));
        if (_rigMotionPathLeftHand) targets.Add(new RigMotionPathTarget("Left hand", EnumAnimatedElement.LowerArmL, selectedPart is EnumAnimatedElement.LowerArmL or EnumAnimatedElement.ItemAnchorL));
        if (_rigMotionPathRightFoot) targets.Add(new RigMotionPathTarget("Right foot", EnumAnimatedElement.LowerFootR, selectedPart == EnumAnimatedElement.LowerFootR));
        if (_rigMotionPathLeftFoot) targets.Add(new RigMotionPathTarget("Left foot", EnumAnimatedElement.LowerFootL, selectedPart == EnumAnimatedElement.LowerFootL));
        if (_rigMotionPathMainHandAnchor) targets.Add(new RigMotionPathTarget("Main-hand anchor", EnumAnimatedElement.ItemAnchor, selectedPart == EnumAnimatedElement.ItemAnchor));
        if (_rigMotionPathOffHandAnchor) targets.Add(new RigMotionPathTarget("Off-hand anchor", EnumAnimatedElement.ItemAnchorL, selectedPart == EnumAnimatedElement.ItemAnchorL));
        if (_rigMotionPathWeaponTip) targets.Add(new RigMotionPathTarget("Weapon tip", EnumAnimatedElement.ItemAnchor, selectedPart == EnumAnimatedElement.ItemAnchor));
        return targets;
    }

    private void InvalidateRigMotionPathCache()
    {
        _rigMotionPathCacheKey = "";
        _rigMotionPathCache.Clear();
    }

    private void AppendRigOnionSkinFrame(PlayerFrame frame, Vec4f color, List<TransformGizmoGhostModel> models)
    {
        EntityPlayer playerEntity = _api.World.Player.Entity;
        if (playerEntity.AnimManager?.Animator is not AnimatorBase animator || animator.RootPoses == null) return;
        if (playerEntity.Properties.Client.Renderer is not EntityShapeRenderer renderer) return;
        if (EntityShapeRendererMeshRefOpaque?.GetValue(renderer) is not MultiTextureMeshRef meshRef || meshRef.Disposed || !meshRef.Initialized) return;

        List<ElementPose> ghostPoses = ClonePoseTree(animator.RootPoses);
        float[] ghostMatrices = CreateIdentityMatrices(animator.MaxJointId);
        Vector3 eyePosition = new((float)playerEntity.LocalEyePos.X, (float)playerEntity.LocalEyePos.Y, (float)playerEntity.LocalEyePos.Z);
        float eyeHeight = (float)playerEntity.Properties.EyeHeight;
        ApplyOnionSkinPlayerFrame(frame, ghostPoses, Mat4f.Create(), ghostMatrices, new HashSet<int>(), eyePosition, eyeHeight, playerEntity.Pos.HeadPitch);

        Matrixf modelMatrix = new();
        BuildPlayerModelMatrix(modelMatrix, playerEntity);
        models.Add(new TransformGizmoGhostModel(meshRef, (float[])modelMatrix.Values.Clone(), ghostMatrices, animator.MaxJointId, color, playerEntity.Pos.AsBlockPos));
    }

    private static List<ElementPose> ClonePoseTree(IEnumerable<ElementPose> poses)
    {
        List<ElementPose> result = [];
        foreach (ElementPose pose in poses)
        {
            result.Add(new ElementPose
            {
                ForElement = pose.ForElement,
                AnimModelMatrix = Mat4f.Create(),
                ChildElementPoses = ClonePoseTree(pose.ChildElementPoses)
            });
        }

        return result;
    }

    private bool TryBuildRigPoseTreeForFrame(PlayerFrame frame, out EntityPlayer playerEntity, out List<ElementPose> poses)
    {
        playerEntity = _api.World.Player.Entity;
        poses = [];
        if (playerEntity.AnimManager?.Animator is not AnimatorBase animator || animator.RootPoses == null) return false;

        poses = ClonePoseTree(animator.RootPoses);
        float[] matrices = CreateIdentityMatrices(animator.MaxJointId);
        Vector3 eyePosition = new((float)playerEntity.LocalEyePos.X, (float)playerEntity.LocalEyePos.Y, (float)playerEntity.LocalEyePos.Z);
        float eyeHeight = (float)playerEntity.Properties.EyeHeight;
        ApplyOnionSkinPlayerFrame(frame, poses, Mat4f.Create(), matrices, new HashSet<int>(), eyePosition, eyeHeight, playerEntity.Pos.HeadPitch);
        return true;
    }

    private static bool TryGetRigPartWorldOriginFromPoses(EntityPlayer playerEntity, IEnumerable<ElementPose> poses, EnumAnimatedElement selectedPart, out Vec3d origin)
    {
        origin = new Vec3d();
        if (!TryFindPose(poses, selectedPart, out ElementPose? pose) || pose?.ForElement == null) return false;

        Matrixf matrix = new();
        BuildPlayerModelMatrix(matrix, playerEntity);
        matrix.Mul(pose.AnimModelMatrix);

        Vec3f localOrigin = GetElementLocalRotationOrigin(pose);
        Vec4f relative = matrix.TransformVector(new Vec4f(localOrigin.X, localOrigin.Y, localOrigin.Z, 1f));
        Vec3d camera = playerEntity.CameraPos;
        origin = new Vec3d(camera.X + relative.X, camera.Y + relative.Y, camera.Z + relative.Z);
        return true;
    }

    private static float[] CreateIdentityMatrices(int maxJointId)
    {
        float[] matrices = new float[Math.Max(1, maxJointId) * 16];
        for (int index = 0; index < matrices.Length; index += 16)
        {
            Array.Copy(Mat4f.Create(), 0, matrices, index, 16);
        }

        return matrices;
    }

    private static void ApplyOnionSkinPlayerFrame(PlayerFrame frame, List<ElementPose>? poses, float[] parentMatrix, float[] transformationMatrices, HashSet<int> jointsDone, Vector3 eyePosition, float eyeHeight, float pitch)
    {
        if (poses == null) return;

        float[] localTransform = Mat4f.Create();
        float[] jointTransform = Mat4f.Create();
        foreach (ElementPose pose in poses)
        {
            ShapeElement element = pose.ForElement;
            if (pose.AnimModelMatrix == null) pose.AnimModelMatrix = Mat4f.Create();
            Array.Copy(parentMatrix, pose.AnimModelMatrix, Math.Min(parentMatrix.Length, pose.AnimModelMatrix.Length));

            if (Enum.TryParse(element.Name, out EnumAnimatedElement animatedElement) && animatedElement != EnumAnimatedElement.Unknown)
            {
                pose.Clear();
                frame.Apply(pose, animatedElement, eyePosition, eyeHeight, pitch, applyCameraPitch: false, overrideTorso: true);
            }

            Mat4f.Identity(localTransform);
            element.GetLocalTransformMatrix(0, localTransform, pose);
            Mat4f.Mul(pose.AnimModelMatrix, pose.AnimModelMatrix, localTransform);

            if (element.JointId > 0 && jointsDone.Add(element.JointId))
            {
                int matrixIndex = 16 * element.JointId;
                if (matrixIndex + 16 <= transformationMatrices.Length)
                {
                    Mat4f.Mul(jointTransform, pose.AnimModelMatrix, element.inverseModelTransform);
                    Array.Copy(jointTransform, 0, transformationMatrices, matrixIndex, 16);
                }
            }

            ApplyOnionSkinPlayerFrame(frame, pose.ChildElementPoses, pose.AnimModelMatrix, transformationMatrices, jointsDone, eyePosition, eyeHeight, pitch);
        }
    }

    internal bool TryPickRigPart(Vec3d rayOrigin, Vec3d rayDirection)
    {
        if (!_rigPoseEditorEnabled || !_showAnimationEditor) return false;

        int bestIndex = -1;
        double bestDistance = double.PositiveInfinity;

        for (int index = 0; index < RigEditableParts.Length; index++)
        {
            if (!TryGetRigPartWorldBox(RigEditableParts[index], out Vec3d[] corners)) continue;
            if (!TryIntersectRayBox(rayOrigin, rayDirection, corners, out double distance)) continue;
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            bestIndex = index;
        }

        if (bestIndex < 0) return false;

        _rigPartIndex = bestIndex;
        return true;
    }

    private bool TryGetRigPartWorldBox(EnumAnimatedElement selectedPart, out Vec3d[] corners)
    {
        corners = Array.Empty<Vec3d>();
        if (!TryGetRigPartPose(selectedPart, out EntityPlayer playerEntity, out ElementPose? pose)) return false;
        if (pose?.ForElement == null) return false;

        return TryGetPoseWorldBox(playerEntity, pose, out corners);
    }

    private static bool TryGetPoseWorldBox(EntityPlayer playerEntity, ElementPose pose, out Vec3d[] corners)
    {
        corners = Array.Empty<Vec3d>();
        if (pose.ForElement == null) return false;

        Matrixf matrix = new();
        BuildPlayerModelMatrix(matrix, playerEntity);
        matrix.Mul(pose.AnimModelMatrix);

        Vec3f[] localCorners = GetElementLocalBoxCorners(pose.ForElement);
        corners = new Vec3d[localCorners.Length];
        Vec3d camera = playerEntity.CameraPos;

        for (int index = 0; index < localCorners.Length; index++)
        {
            Vec3f local = localCorners[index];
            Vec4f relative = matrix.TransformVector(new Vec4f(local.X, local.Y, local.Z, 1f));
            corners[index] = new Vec3d(camera.X + relative.X, camera.Y + relative.Y, camera.Z + relative.Z);
        }

        return true;
    }

    private bool TryGetRigPartPose(EnumAnimatedElement selectedPart, out EntityPlayer playerEntity, out ElementPose? pose)
    {
        return TryGetRigPartPose(selectedPart, out playerEntity, out pose, out _);
    }

    private bool TryGetRigPartPose(EnumAnimatedElement selectedPart, out EntityPlayer playerEntity, out ElementPose? pose, out ElementPose? parentPose)
    {
        playerEntity = _api.World.Player.Entity;
        pose = null;
        parentPose = null;
        if (playerEntity.AnimManager?.Animator is not AnimatorBase animator || animator.RootPoses == null) return false;
        return TryFindPose(animator.RootPoses, selectedPart, null, out pose, out parentPose);
    }

    private static bool TryFindPose(IEnumerable<ElementPose> poses, EnumAnimatedElement selectedPart, out ElementPose? result)
    {
        return TryFindPose(poses, selectedPart, null, out result, out _);
    }

    private static bool TryFindPose(IEnumerable<ElementPose> poses, EnumAnimatedElement selectedPart, ElementPose? parent, out ElementPose? result, out ElementPose? parentResult)
    {
        foreach (ElementPose pose in poses)
        {
            if (PoseMatches(pose, selectedPart))
            {
                result = pose;
                parentResult = parent;
                return true;
            }

            if (TryFindPose(pose.ChildElementPoses, selectedPart, pose, out result, out parentResult)) return true;
        }

        result = null;
        parentResult = null;
        return false;
    }

    private static bool PoseMatches(ElementPose pose, EnumAnimatedElement selectedPart)
    {
        if (pose is ExtendedElementPose extendedPose && extendedPose.ElementNameEnum == selectedPart) return true;
        return Enum.TryParse(pose.ForElement?.Name, out EnumAnimatedElement parsed) && parsed == selectedPart;
    }

    private static Vec3f GetElementLocalCenter(ShapeElement element)
    {
        if (element.From == null || element.To == null || element.From.Length < 3 || element.To.Length < 3) return new Vec3f();
        return new Vec3f(
            (float)((element.To[0] - element.From[0]) / 32.0),
            (float)((element.To[1] - element.From[1]) / 32.0),
            (float)((element.To[2] - element.From[2]) / 32.0));
    }

    private static Vec3f GetElementLocalRotationOrigin(ElementPose pose)
    {
        ShapeElement element = pose.ForElement;
        if (element.From == null || element.From.Length < 3) return new Vec3f(-pose.translateX, -pose.translateY, -pose.translateZ);

        double[]? rotationOrigin = element.RotationOrigin;
        double originX = rotationOrigin != null && rotationOrigin.Length > 0 ? rotationOrigin[0] : 0;
        double originY = rotationOrigin != null && rotationOrigin.Length > 1 ? rotationOrigin[1] : 0;
        double originZ = rotationOrigin != null && rotationOrigin.Length > 2 ? rotationOrigin[2] : 0;

        return new Vec3f(
            (float)((originX - element.From[0]) / 16.0 - pose.translateX),
            (float)((originY - element.From[1]) / 16.0 - pose.translateY),
            (float)((originZ - element.From[2]) / 16.0 - pose.translateZ));
    }

    private static Vec3f[] GetElementLocalBoxCorners(ShapeElement element)
    {
        Vec3f center = GetElementLocalCenter(element);
        float halfX = 0.12f;
        float halfY = 0.12f;
        float halfZ = 0.12f;

        if (element.From != null && element.To != null && element.From.Length >= 3 && element.To.Length >= 3)
        {
            halfX = Math.Max(0.08f, (float)Math.Abs(element.To[0] - element.From[0]) / 32f);
            halfY = Math.Max(0.08f, (float)Math.Abs(element.To[1] - element.From[1]) / 32f);
            halfZ = Math.Max(0.08f, (float)Math.Abs(element.To[2] - element.From[2]) / 32f);
        }

        const float padding = 0.035f;
        halfX += padding;
        halfY += padding;
        halfZ += padding;

        float minX = center.X - halfX;
        float minY = center.Y - halfY;
        float minZ = center.Z - halfZ;
        float maxX = center.X + halfX;
        float maxY = center.Y + halfY;
        float maxZ = center.Z + halfZ;

        return
        [
            new Vec3f(minX, minY, minZ),
            new Vec3f(maxX, minY, minZ),
            new Vec3f(maxX, maxY, minZ),
            new Vec3f(minX, maxY, minZ),
            new Vec3f(minX, minY, maxZ),
            new Vec3f(maxX, minY, maxZ),
            new Vec3f(maxX, maxY, maxZ),
            new Vec3f(minX, maxY, maxZ)
        ];
    }

    private bool TryGetRigPartWorldInfo(EnumAnimatedElement selectedPart, out RigPoseWorldInfo info)
    {
        info = default;
        if (!TryGetRigPartPose(selectedPart, out EntityPlayer playerEntity, out ElementPose? pose, out ElementPose? parentPose)) return false;
        if (pose?.ForElement == null) return false;

        Matrixf worldMatrix = new();
        BuildPlayerModelMatrix(worldMatrix, playerEntity);
        worldMatrix.Mul(pose.AnimModelMatrix);

        Matrixf parentMatrix = new();
        BuildPlayerModelMatrix(parentMatrix, playerEntity);
        if (parentPose != null) parentMatrix.Mul(parentPose.AnimModelMatrix);

        if (!TryGetRigPartWorldOrigin(selectedPart, out Vec3d? origin) || origin == null) return false;

        info = new RigPoseWorldInfo(
            pose,
            origin,
            RigIkMatrix3.FromMatrixf(worldMatrix).Orthonormalized(),
            RigIkMatrix3.FromMatrixf(parentMatrix).Orthonormalized(),
            new Vec3d(pose.ForElement.RotationX, pose.ForElement.RotationY, pose.ForElement.RotationZ));
        return true;
    }

    private bool TryGetDistalEndpointWorld(EnumAnimatedElement lowerPart, Vec3d jointOrigin, out Vec3d endpoint)
    {
        endpoint = jointOrigin;
        if (!TryGetRigPartPose(lowerPart, out EntityPlayer playerEntity, out ElementPose? pose)) return false;
        if (pose?.ForElement == null) return false;

        Matrixf matrix = new();
        BuildPlayerModelMatrix(matrix, playerEntity);
        matrix.Mul(pose.AnimModelMatrix);

        Vec3f[] localCorners = GetElementLocalBoxCorners(pose.ForElement);
        Vec3d camera = playerEntity.CameraPos;
        double best = -1;

        foreach (Vec3f local in localCorners)
        {
            Vec4f relative = matrix.TransformVector(new Vec4f(local.X, local.Y, local.Z, 1f));
            Vec3d world = new(camera.X + relative.X, camera.Y + relative.Y, camera.Z + relative.Z);
            double distance = Sub(world, jointOrigin).LengthSq();
            if (distance <= best) continue;

            best = distance;
            endpoint = world;
        }

        return best > 0.000001;
    }

    private Vec3d GetDesiredSelectedWorldOrigin(RigIkDragCache cache, AnimationElement desiredElement)
    {
        double dx = ((desiredElement.OffsetX ?? 0) - (cache.SelectedStartElement.OffsetX ?? 0)) / 16.0;
        double dy = ((desiredElement.OffsetY ?? 0) - (cache.SelectedStartElement.OffsetY ?? 0)) / 16.0;
        double dz = ((desiredElement.OffsetZ ?? 0) - (cache.SelectedStartElement.OffsetZ ?? 0)) / 16.0;

        return Add(cache.SelectedOrigin, Add(Add(Scale(cache.SelectedAxes.X, dx), Scale(cache.SelectedAxes.Y, dy)), Scale(cache.SelectedAxes.Z, dz)));
    }

    private static double GetBoneLength(ShapeElement element, double originDistance)
    {
        if (originDistance > 0.0001) return originDistance;
        if (element.From == null || element.To == null || element.From.Length < 3 || element.To.Length < 3) return 0;

        double x = Math.Abs(element.To[0] - element.From[0]) / 16.0;
        double y = Math.Abs(element.To[1] - element.From[1]) / 16.0;
        double z = Math.Abs(element.To[2] - element.From[2]) / 16.0;
        double length = Math.Max(x, Math.Max(y, z));
        return length > 0.0001 ? length : 0;
    }

    private static bool TryIntersectRayBox(Vec3d origin, Vec3d direction, Vec3d[] corners, out double distance)
    {
        distance = double.PositiveInfinity;
        if (corners.Length < 8) return false;

        ReadOnlySpan<(int A, int B, int C)> triangles =
        [
            (0, 1, 2), (0, 2, 3),
            (4, 6, 5), (4, 7, 6),
            (0, 4, 5), (0, 5, 1),
            (1, 5, 6), (1, 6, 2),
            (2, 6, 7), (2, 7, 3),
            (3, 7, 4), (3, 4, 0)
        ];

        bool hit = false;
        foreach ((int a, int b, int c) in triangles)
        {
            if (!TryIntersectRayTriangle(origin, direction, corners[a], corners[b], corners[c], out double triangleDistance)) continue;
            if (triangleDistance >= distance) continue;

            distance = triangleDistance;
            hit = true;
        }

        return hit;
    }

    private static bool TryIntersectRayTriangle(Vec3d origin, Vec3d direction, Vec3d a, Vec3d b, Vec3d c, out double distance)
    {
        distance = 0;
        const double epsilon = 0.0000001;
        Vec3d edge1 = Sub(b, a);
        Vec3d edge2 = Sub(c, a);
        Vec3d h = direction.Cross(edge2);
        double det = Dot(edge1, h);
        if (det > -epsilon && det < epsilon) return false;

        double invDet = 1.0 / det;
        Vec3d s = Sub(origin, a);
        double u = invDet * Dot(s, h);
        if (u < 0 || u > 1) return false;

        Vec3d q = s.Cross(edge1);
        double v = invDet * Dot(direction, q);
        if (v < 0 || u + v > 1) return false;

        distance = invDet * Dot(edge2, q);
        return distance >= 0;
    }

    private static double Distance(Vec3d left, Vec3d right) => Sub(left, right).Length();
    private static double Dot(Vec3d left, Vec3d right) => left.X * right.X + left.Y * right.Y + left.Z * right.Z;
    private static Vec3d Add(Vec3d left, Vec3d right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
    private static Vec3d Sub(Vec3d left, Vec3d right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
    private static Vec3d Scale(Vec3d value, double scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
    private static Vec3d ProjectOntoPlane(Vec3d vector, Vec3d normal) => Sub(vector, Scale(normal, Dot(vector, normal)));
    private static Vec3d SafeNormalize(Vec3d value, Vec3d fallback) => value.LengthSq() < 0.000001 ? fallback : value.Normalize();

    private static Vec3d SafePoleFallback(Vec3d axis)
    {
        Vec3d fallback = ProjectOntoPlane(new Vec3d(0, 1, 0), axis);
        if (fallback.LengthSq() < 0.000001) fallback = ProjectOntoPlane(new Vec3d(1, 0, 0), axis);
        return SafeNormalize(fallback, new Vec3d(0, 0, 1));
    }

    private static void BuildPlayerModelMatrix(Matrixf matrix, EntityPlayer playerEntity)
    {
        matrix.Identity();
        Vec3d camera = playerEntity.CameraPos;
        matrix.Translate(playerEntity.Pos.X - camera.X, playerEntity.Pos.InternalY - camera.Y, playerEntity.Pos.Z - camera.Z);

        float rotX = playerEntity.Properties.Client.Shape?.rotateX ?? 0;
        float rotY = playerEntity.Properties.Client.Shape?.rotateY ?? 0;
        float rotZ = playerEntity.Properties.Client.Shape?.rotateZ ?? 0;

        matrix.Translate(0, playerEntity.SelectionBox.Y2 / 2f, 0);
        matrix.RotateX(playerEntity.Pos.Roll + rotX * GameMath.DEG2RAD);
        matrix.RotateY(playerEntity.BodyYaw + (90f + rotY) * GameMath.DEG2RAD);
        matrix.RotateZ(playerEntity.WalkPitch + rotZ * GameMath.DEG2RAD);
        matrix.Translate(0, -playerEntity.SelectionBox.Y2 / 2f, 0);

        float size = playerEntity.Properties.Client.Size;
        matrix.Scale(size, size, size);
        matrix.Translate(-0.5f, 0, -0.5f);
    }

    private void OnDebugEditorClosed()
    {
        SetEditorFrameOverride(null);
        _detachedEditorCamera?.SetEnabled(false);
        _rigPoseEditorEnabled = false;
        _rigOnionSkinAnimation = null;
        _rigMotionPathAnimation = null;
        InvalidateRigMotionPathCache();
        DebugPoseFreezeActive = false;
        DebugRigPoseOverrideActive = false;
    }

    private readonly record struct RigMotionPathTarget(string Label, EnumAnimatedElement Part, bool Selected);

    private readonly record struct RigIkChain(EnumAnimatedElement SelectedPart, EnumAnimatedElement UpperPart, EnumAnimatedElement LowerPart, EnumAnimatedElement EndPart, double PoleSign);

    private readonly record struct RigPoseWorldInfo(ElementPose Pose, Vec3d Origin, RigIkMatrix3 WorldRotation, RigIkMatrix3 ParentWorldRotation, Vec3d BaseRotationDegrees);

    private sealed record RigIkDragCache(
        RigIkChain Chain,
        RigPoseWorldInfo UpperInfo,
        RigPoseWorldInfo LowerInfo,
        Vec3d SelectedOrigin,
        Vec3d EndOrigin,
        TransformGizmoAxes SelectedAxes,
        AnimationElement SelectedStartElement,
        AnimationElement UpperStartElement,
        AnimationElement LowerStartElement,
        double UpperLength,
        double LowerLength,
        Vec3d PoleHint);

    private readonly struct RigIkMatrix3
    {
        public static RigIkMatrix3 Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

        private readonly double m00, m01, m02, m10, m11, m12, m20, m21, m22;

        public RigIkMatrix3(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
        {
            this.m00 = m00;
            this.m01 = m01;
            this.m02 = m02;
            this.m10 = m10;
            this.m11 = m11;
            this.m12 = m12;
            this.m20 = m20;
            this.m21 = m21;
            this.m22 = m22;
        }

        public static RigIkMatrix3 FromMatrixf(Matrixf matrix)
        {
            float[] v = matrix.Values;
            return new RigIkMatrix3(v[0], v[4], v[8], v[1], v[5], v[9], v[2], v[6], v[10]);
        }

        public static RigIkMatrix3 FromEulerDegrees(double xDegrees, double yDegrees, double zDegrees)
        {
            double x = xDegrees * GameMath.DEG2RAD;
            double y = yDegrees * GameMath.DEG2RAD;
            double z = zDegrees * GameMath.DEG2RAD;
            double sx = Math.Sin(x), cx = Math.Cos(x), sy = Math.Sin(y), cy = Math.Cos(y), sz = Math.Sin(z), cz = Math.Cos(z);

            return new RigIkMatrix3(
                cy * cz, -cy * sz, sy,
                cx * sz + sx * sy * cz, cx * cz - sx * sy * sz, -sx * cy,
                -cx * sy * cz + sx * sz, cx * sy * sz + sx * cz, cx * cy
            );
        }

        public static RigIkMatrix3 FromAxisAngle(Vec3d axis, double radians)
        {
            axis = SafeNormalize(new Vec3d(axis.X, axis.Y, axis.Z), new Vec3d(1, 0, 0));
            double x = axis.X, y = axis.Y, z = axis.Z, s = Math.Sin(radians), c = Math.Cos(radians), t = 1 - c;

            return new RigIkMatrix3(
                t * x * x + c, t * x * y - s * z, t * x * z + s * y,
                t * x * y + s * z, t * y * y + c, t * y * z - s * x,
                t * x * z - s * y, t * y * z + s * x, t * z * z + c
            );
        }

        public static RigIkMatrix3 FromTo(Vec3d from, Vec3d to)
        {
            from = SafeNormalize(from, new Vec3d(1, 0, 0));
            to = SafeNormalize(to, new Vec3d(1, 0, 0));
            double dot = Math.Clamp(Dot(from, to), -1, 1);
            if (dot > 0.999999) return Identity;

            if (dot < -0.999999)
            {
                return FromAxisAngle(SafePoleFallback(from), Math.PI);
            }

            Vec3d axis = from.Cross(to);
            return FromAxisAngle(axis, Math.Acos(dot));
        }

        public RigIkMatrix3 Mul(RigIkMatrix3 right)
        {
            return new RigIkMatrix3(
                m00 * right.m00 + m01 * right.m10 + m02 * right.m20, m00 * right.m01 + m01 * right.m11 + m02 * right.m21, m00 * right.m02 + m01 * right.m12 + m02 * right.m22,
                m10 * right.m00 + m11 * right.m10 + m12 * right.m20, m10 * right.m01 + m11 * right.m11 + m12 * right.m21, m10 * right.m02 + m11 * right.m12 + m12 * right.m22,
                m20 * right.m00 + m21 * right.m10 + m22 * right.m20, m20 * right.m01 + m21 * right.m11 + m22 * right.m21, m20 * right.m02 + m21 * right.m12 + m22 * right.m22
            );
        }

        public RigIkMatrix3 Inverted()
        {
            double determinant = m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20);
            if (Math.Abs(determinant) < 0.000001) return Identity;

            double inv = 1 / determinant;
            return new RigIkMatrix3(
                (m11 * m22 - m12 * m21) * inv, (m02 * m21 - m01 * m22) * inv, (m01 * m12 - m02 * m11) * inv,
                (m12 * m20 - m10 * m22) * inv, (m00 * m22 - m02 * m20) * inv, (m02 * m10 - m00 * m12) * inv,
                (m10 * m21 - m11 * m20) * inv, (m01 * m20 - m00 * m21) * inv, (m00 * m11 - m01 * m10) * inv
            );
        }

        public RigIkMatrix3 Orthonormalized()
        {
            Vec3d x = SafeNormalize(new Vec3d(m00, m10, m20), new Vec3d(1, 0, 0));
            Vec3d y = Sub(new Vec3d(m01, m11, m21), Scale(x, Dot(new Vec3d(m01, m11, m21), x)));
            y = SafeNormalize(y, SafePoleFallback(x));
            Vec3d z = SafeNormalize(x.Cross(y), new Vec3d(0, 0, 1));
            y = SafeNormalize(z.Cross(x), new Vec3d(0, 1, 0));
            return new RigIkMatrix3(x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z);
        }

        public Vec3d TransformDirection(Vec3d direction) => new(
            m00 * direction.X + m01 * direction.Y + m02 * direction.Z,
            m10 * direction.X + m11 * direction.Y + m12 * direction.Z,
            m20 * direction.X + m21 * direction.Y + m22 * direction.Z
        );

        public Vec3d ToEulerDegrees()
        {
            double y = Math.Asin(Math.Clamp(m02, -1, 1));
            double cy = Math.Cos(y);
            double x;
            double z;

            if (Math.Abs(cy) > 0.00001)
            {
                x = Math.Atan2(-m12, m22);
                z = Math.Atan2(-m01, m00);
            }
            else
            {
                z = 0;
                x = m02 > 0 ? Math.Atan2(m10, m11) : Math.Atan2(-m10, m11);
            }

            return new Vec3d(x * GameMath.RAD2DEG, y * GameMath.RAD2DEG, z * GameMath.RAD2DEG);
        }
    }
}
#endif
