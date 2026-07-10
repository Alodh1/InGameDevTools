using InGameDevTools.Animations;
using InGameDevTools.Integration.Transpilers;
using InGameDevTools.Utils;
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
    public static bool DisableAllAnimations { get; set; }
    public static bool DisableThirdPersonAnimations { get; set; }
    public static Dictionary<long, ThirdPersonAnimationsBehavior> AnimationBehaviors { get; } = new();
    public static FirstPersonAnimationsBehavior? FirstPersonAnimationBehavior { get; set; }
    public static long OwnerEntityId { get; set; }
    public static HashSet<long> ActiveEntities { get; } = new();
    public static ConditionalWeakTable<ClientAnimator, EntityPlayer>? Animators { get; private set; }

    private static readonly FieldInfo? AnimationManagerEntity = typeof(Vintagestory.API.Common.AnimationManager).GetField("entity", BindingFlags.NonPublic | BindingFlags.Instance);

    public static void Patch(string harmonyId, ICoreAPI api)
    {
        Animators = new();
        Harmony harmony = new(harmonyId);

        PatchIfFound(
            api,
            harmony,
            typeof(EntityShapeRenderer).GetMethod("BeforeRender", AccessTools.all),
            "EntityShapeRenderer.BeforeRender",
            prefix: new HarmonyMethod(AccessTools.Method(typeof(AnimationPatches), nameof(BeforeRender))));

        PatchIfFound(
            api,
            harmony,
            typeof(EntityPlayer).GetMethod(nameof(EntityPlayer.OnSelfBeforeRender), AccessTools.all),
            "EntityPlayer.OnSelfBeforeRender",
            postfix: new HarmonyMethod(AccessTools.Method(typeof(AnimationPatches), nameof(OnSelfBeforeRender))));

        PatchIfFound(
            api,
            harmony,
            typeof(Vintagestory.API.Common.AnimationManager).GetMethod("OnClientFrame", AccessTools.all),
            "AnimationManager.OnClientFrame",
            postfix: new HarmonyMethod(AccessTools.Method(typeof(AnimationPatches), nameof(AnimationManagerOnClientFrame))));
    }

    public static void Unpatch(string harmonyId)
    {
        Harmony harmony = new(harmonyId);
        UnpatchIfFound(harmony, typeof(EntityShapeRenderer).GetMethod("BeforeRender", AccessTools.all), HarmonyPatchType.Prefix, harmonyId);
        UnpatchIfFound(harmony, typeof(EntityPlayer).GetMethod(nameof(EntityPlayer.OnSelfBeforeRender), AccessTools.all), HarmonyPatchType.Postfix, harmonyId);
        UnpatchIfFound(harmony, typeof(Vintagestory.API.Common.AnimationManager).GetMethod("OnClientFrame", AccessTools.all), HarmonyPatchType.Postfix, harmonyId);

        Animators = null;
        AnimationBehaviors.Clear();
        ActiveEntities.Clear();
        FirstPersonAnimationBehavior = null;
        OwnerEntityId = 0;
    }

    private static void PatchIfFound(ICoreAPI api, Harmony harmony, MethodBase? target, string description, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null)
    {
        if (target == null)
        {
            LoggerUtil.Warn(api, typeof(AnimationPatches), $"Patch target not found: {description}. The game version may have changed; skipping this patch.");
            return;
        }

        harmony.Patch(target, prefix: prefix, postfix: postfix);
    }

    private static void UnpatchIfFound(Harmony harmony, MethodBase? target, HarmonyPatchType patchType, string harmonyId)
    {
        if (target == null) return;
        harmony.Unpatch(target, patchType, harmonyId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnFrameInvoke(ClientAnimator? animator, ElementPose pose)
    {
        if (DisableAllAnimations || animator == null) return;

        if (Animators?.TryGetValue(animator, out EntityPlayer? entity) != true || entity == null) return;

        if (entity.EntityId == OwnerEntityId)
        {
            FirstPersonAnimationBehavior?.OnFrame(entity, pose, animator);
            return;
        }

        if (!DisableThirdPersonAnimations &&
            AnimationBehaviors.TryGetValue(entity.EntityId, out ThirdPersonAnimationsBehavior? behavior))
        {
            behavior.OnFrame(entity, pose, animator);
        }
    }

    private static void BeforeRender(EntityShapeRenderer __instance, float dt)
    {
        if (DisableAllAnimations) return;
        OnBeforeFrame?.Invoke(__instance.entity, dt);
    }

    private static void OnSelfBeforeRender(EntityPlayer __instance, float dt)
    {
        if (DisableAllAnimations) return;
        OnBeforeFrame?.Invoke(__instance, dt);
    }

    private static void AnimationManagerOnClientFrame(Vintagestory.API.Common.AnimationManager __instance, float dt)
    {
        if (AnimationManagerEntity?.GetValue(__instance) is not EntityPlayer player) return;
        if (__instance.Animator is not ClientAnimator animator) return;

        Animators?.GetValue(animator, _ => player);
    }
}
