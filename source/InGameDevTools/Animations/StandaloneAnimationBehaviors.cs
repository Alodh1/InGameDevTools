using InGameDevTools.Integration;
using InGameDevTools.Integration.Transpilers;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace InGameDevTools.Animations;

public sealed class FirstPersonAnimationsBehavior : EntityBehavior, IDisposable
{
    private readonly EntityPlayer _player;
    private readonly ICoreClientAPI? _api;
    private PlayerItemFrame _lastFrame = PlayerItemFrame.Zero;

    public FirstPersonAnimationsBehavior(Entity entity) : base(entity)
    {
        _player = entity as EntityPlayer ?? throw new ArgumentException("Only player entities can preview in-game devtools animations.");
        _api = entity.Api as ICoreClientAPI;
        Activate();
    }

    public PlayerItemFrame? FrameOverride { get; set; }
    public PlayerItemFrame CurrentFrame => FrameOverride ?? _lastFrame;
    public bool HasActiveAnimationFrame => FrameOverride != null;
    public static float CurrentFov { get; set; } = 90f;

    public override string PropertyName() => "InGameDevTools:FirstPersonAnimations";

    public override void AfterInitialized(bool onFirstSpawn)
    {
        Activate();
    }

    public void OnFrame(Entity targetEntity, ElementPose pose, AnimatorBase animator)
    {
        PlayerItemFrame? frame = FrameOverride;
        bool debugFreeze = DebugWindowManager.DebugPoseFreezeActive && IsLocalPlayer(targetEntity);
        if (frame == null && !debugFreeze) return;

        PlayerItemFrame frameToApply = frame ?? PlayerItemFrame.Zero;
        _lastFrame = frameToApply;
        ApplyFrame(frameToApply, targetEntity, pose, clearPose: debugFreeze);
    }

    public void Dispose()
    {
        if (AnimationPatches.FirstPersonAnimationBehavior == this)
        {
            AnimationPatches.FirstPersonAnimationBehavior = null;
        }
    }

    private void Activate()
    {
        if (_api?.World?.Player?.Entity?.EntityId != _player.EntityId) return;

        AnimationPatches.FirstPersonAnimationBehavior = this;
        AnimationPatches.OwnerEntityId = _player.EntityId;
    }

    private bool IsLocalPlayer(Entity targetEntity)
    {
        return _api?.World?.Player?.Entity?.EntityId == targetEntity.EntityId;
    }

    private static void ApplyFrame(PlayerItemFrame frame, Entity targetEntity, ElementPose pose, bool clearPose)
    {
        EnumAnimatedElement element = GetAnimatedElement(pose);
        if (clearPose && element != EnumAnimatedElement.Unknown)
        {
            pose.Clear();
        }

        Vector3 eyePosition = new((float)targetEntity.LocalEyePos.X, (float)targetEntity.LocalEyePos.Y, (float)targetEntity.LocalEyePos.Z);
        float eyeHeight = (float)targetEntity.Properties.EyeHeight;
        float pitch = targetEntity.Pos.HeadPitch;

        if (pose is ExtendedElementPose extendedPose)
        {
            frame.Apply(extendedPose, element, eyePosition, eyeHeight, pitch, applyCameraPitch: true);
        }
        else
        {
            frame.Apply(pose, element, eyePosition, eyeHeight, pitch, applyCameraPitch: true);
        }
    }

    private static EnumAnimatedElement GetAnimatedElement(ElementPose pose)
    {
        if (pose is ExtendedElementPose extendedPose)
        {
            return extendedPose.ElementNameEnum;
        }

        return Enum.TryParse(pose.ForElement?.Name, out EnumAnimatedElement element)
            ? element
            : EnumAnimatedElement.Unknown;
    }
}

public sealed class ThirdPersonAnimationsBehavior : EntityBehavior, IDisposable
{
    private readonly EntityPlayer _player;
    private readonly ICoreClientAPI? _api;
    private PlayerItemFrame _lastFrame = PlayerItemFrame.Zero;

    public ThirdPersonAnimationsBehavior(Entity entity) : base(entity)
    {
        _player = entity as EntityPlayer ?? throw new ArgumentException("Only player entities can preview in-game devtools animations.");
        _api = entity.Api as ICoreClientAPI;
        Activate();
    }

    public PlayerItemFrame? FrameOverride { get; set; }
    public PlayerItemFrame CurrentFrame => FrameOverride ?? _lastFrame;
    public bool HasActiveAnimationFrame => FrameOverride != null;

    public override string PropertyName() => "InGameDevTools:ThirdPersonAnimations";

    public override void AfterInitialized(bool onFirstSpawn)
    {
        Activate();
    }

    public void OnFrame(Entity targetEntity, ElementPose pose, AnimatorBase animator)
    {
        if (targetEntity.EntityId == AnimationPatches.OwnerEntityId) return;

        PlayerItemFrame? frame = FrameOverride;
        bool debugFreeze = DebugWindowManager.DebugPoseFreezeActive && IsLocalPlayer(targetEntity);
        if (frame == null && !debugFreeze) return;

        PlayerItemFrame frameToApply = frame ?? PlayerItemFrame.Zero;
        _lastFrame = frameToApply;
        FirstPersonAnimationsBehaviorApply(frameToApply, targetEntity, pose, clearPose: debugFreeze);
    }

    public void Dispose()
    {
        if (AnimationPatches.AnimationBehaviors.TryGetValue(_player.EntityId, out ThirdPersonAnimationsBehavior? behavior) && behavior == this)
        {
            AnimationPatches.AnimationBehaviors.Remove(_player.EntityId);
        }
        AnimationPatches.ActiveEntities.Remove(_player.EntityId);
    }

    private void Activate()
    {
        AnimationPatches.AnimationBehaviors[_player.EntityId] = this;
        AnimationPatches.ActiveEntities.Add(_player.EntityId);
    }

    private bool IsLocalPlayer(Entity targetEntity)
    {
        return _api?.World?.Player?.Entity?.EntityId == targetEntity.EntityId;
    }

    private static void FirstPersonAnimationsBehaviorApply(PlayerItemFrame frame, Entity targetEntity, ElementPose pose, bool clearPose)
    {
        EnumAnimatedElement element = pose is ExtendedElementPose extendedPose
            ? extendedPose.ElementNameEnum
            : Enum.TryParse(pose.ForElement?.Name, out EnumAnimatedElement parsed) ? parsed : EnumAnimatedElement.Unknown;

        if (clearPose && element != EnumAnimatedElement.Unknown)
        {
            pose.Clear();
        }

        Vector3 eyePosition = new((float)targetEntity.LocalEyePos.X, (float)targetEntity.LocalEyePos.Y, (float)targetEntity.LocalEyePos.Z);
        float eyeHeight = (float)targetEntity.Properties.EyeHeight;
        float pitch = targetEntity.Pos.HeadPitch;

        if (pose is ExtendedElementPose extended)
        {
            frame.Apply(extended, element, eyePosition, eyeHeight, pitch, applyCameraPitch: true);
        }
        else
        {
            frame.Apply(pose, element, eyePosition, eyeHeight, pitch, applyCameraPitch: true);
        }
    }
}
