using InGameDevTools.Animations;
using InGameDevTools.Integration.Transpilers;
using HarmonyLib;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace InGameDevTools.Integration;

internal static class AnimationPatches
{
    public static event Action<Entity, float>? OnBeforeFrame;
    public static Settings ClientSettings { get; set; } = new();
    public static Dictionary<long, ThirdPersonAnimationsBehavior> AnimationBehaviors { get; } = new();
    public static FirstPersonAnimationsBehavior? FirstPersonAnimationBehavior { get; set; }
    public static long OwnerEntityId { get; set; }
    public static HashSet<long> ActiveEntities { get; } = new();
    public static ObjectCache<ClientAnimator, EntityPlayer>? Animators { get; private set; }

    private static readonly FieldInfo? AnimationManagerEntity = typeof(Vintagestory.API.Common.AnimationManager).GetField("entity", BindingFlags.NonPublic | BindingFlags.Instance);

    public static void Patch(string harmonyId, ICoreAPI api)
    {
        Animators = new(api, "in-game devtools animators to players cache", 10000, 5 * 60 * 1000, threadSafe: true);
        Harmony harmony = new(harmonyId);

        harmony.Patch(
            typeof(EntityShapeRenderer).GetMethod("BeforeRender", AccessTools.all),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(AnimationPatches), nameof(BeforeRender))));

        harmony.Patch(
            typeof(EntityPlayer).GetMethod(nameof(EntityPlayer.OnSelfBeforeRender), AccessTools.all),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(AnimationPatches), nameof(OnSelfBeforeRender))));

        harmony.Patch(
            typeof(Vintagestory.API.Common.AnimationManager).GetMethod("OnClientFrame", AccessTools.all),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(AnimationPatches), nameof(AnimationManagerOnClientFrame))));
    }

    public static void Unpatch(string harmonyId)
    {
        Harmony harmony = new(harmonyId);
        harmony.Unpatch(typeof(EntityShapeRenderer).GetMethod("BeforeRender", AccessTools.all), HarmonyPatchType.Prefix, harmonyId);
        harmony.Unpatch(typeof(EntityPlayer).GetMethod(nameof(EntityPlayer.OnSelfBeforeRender), AccessTools.all), HarmonyPatchType.Postfix, harmonyId);
        harmony.Unpatch(typeof(Vintagestory.API.Common.AnimationManager).GetMethod("OnClientFrame", AccessTools.all), HarmonyPatchType.Postfix, harmonyId);

        Animators?.Dispose();
        Animators = null;
        AnimationBehaviors.Clear();
        ActiveEntities.Clear();
        FirstPersonAnimationBehavior = null;
        OwnerEntityId = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnFrameInvoke(ClientAnimator? animator, ElementPose pose)
    {
        if (ClientSettings.DisableAllAnimations || animator == null) return;

        EntityPlayer? entity = null;
        if (pose is ExtendedElementPose { Player: not null } extendedPose)
        {
            entity = extendedPose.Player;
        }
        else if (Animators?.Get(animator, out EntityPlayer? cachedEntity) == true)
        {
            entity = cachedEntity;
            if (pose is ExtendedElementPose extendedPoseWithoutPlayer)
            {
                extendedPoseWithoutPlayer.Player = cachedEntity;
            }
        }

        if (entity == null) return;

        if (entity.EntityId == OwnerEntityId)
        {
            FirstPersonAnimationBehavior?.OnFrame(entity, pose, animator);
            return;
        }

        if (!ClientSettings.DisableThirdPersonAnimations &&
            AnimationBehaviors.TryGetValue(entity.EntityId, out ThirdPersonAnimationsBehavior? behavior))
        {
            behavior.OnFrame(entity, pose, animator);
        }
    }

    private static void BeforeRender(EntityShapeRenderer __instance, float dt)
    {
        if (ClientSettings.DisableAllAnimations) return;
        OnBeforeFrame?.Invoke(__instance.entity, dt);
    }

    private static void OnSelfBeforeRender(EntityPlayer __instance, float dt)
    {
        if (ClientSettings.DisableAllAnimations) return;
        OnBeforeFrame?.Invoke(__instance, dt);
    }

    private static void AnimationManagerOnClientFrame(Vintagestory.API.Common.AnimationManager __instance, float dt)
    {
        if (AnimationManagerEntity?.GetValue(__instance) is not EntityPlayer player) return;
        if (__instance.Animator is not ClientAnimator animator) return;

        Animators?.Add(animator, player);
    }
}
