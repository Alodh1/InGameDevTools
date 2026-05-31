using InGameDevTools.Animations;
using InGameDevTools.Utils;
using HarmonyLib;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace InGameDevTools.Integration;

internal static class DetachedEditorCameraPatches
{
    public static void Patch(string harmonyId, ICoreAPI api)
    {
        Harmony harmony = new(harmonyId);
        PatchIfFound(
            api,
            harmony,
            typeof(ClientMain).GetMethod(nameof(ClientMain.UpdateCameraYawPitch), BindingFlags.Instance | BindingFlags.Public),
            "ClientMain.UpdateCameraYawPitch",
            prefix: new HarmonyMethod(typeof(DetachedEditorCameraPatches), nameof(UpdateCameraYawPitchPrefix))
            {
                priority = Priority.First
            });

        PatchIfFound(
            api,
            harmony,
            typeof(EntityPlayerShapeRenderer).GetMethod("loadModelMatrixForPlayer", AccessTools.all),
            "EntityPlayerShapeRenderer.loadModelMatrixForPlayer",
            prefix: new HarmonyMethod(typeof(DetachedEditorCameraPatches), nameof(LoadModelMatrixForPlayerPrefix)));

        PatchIfFound(
            api,
            harmony,
            typeof(EntityPlayerShapeRenderer).GetMethod("getAboveHeadPosition", AccessTools.all),
            "EntityPlayerShapeRenderer.getAboveHeadPosition",
            prefix: new HarmonyMethod(typeof(DetachedEditorCameraPatches), nameof(GetAboveHeadPositionPrefix)));
    }

    public static void Unpatch(string harmonyId)
    {
        Harmony harmony = new(harmonyId);
        UnpatchIfFound(harmony, typeof(ClientMain).GetMethod(nameof(ClientMain.UpdateCameraYawPitch), BindingFlags.Instance | BindingFlags.Public), HarmonyPatchType.Prefix, harmonyId);
        UnpatchIfFound(harmony, typeof(EntityPlayerShapeRenderer).GetMethod("loadModelMatrixForPlayer", AccessTools.all), HarmonyPatchType.Prefix, harmonyId);
        UnpatchIfFound(harmony, typeof(EntityPlayerShapeRenderer).GetMethod("getAboveHeadPosition", AccessTools.all), HarmonyPatchType.Prefix, harmonyId);
    }

    private static void PatchIfFound(ICoreAPI api, Harmony harmony, MethodBase? target, string description, HarmonyMethod? prefix = null)
    {
        if (target == null)
        {
            LoggerUtil.Warn(api, typeof(DetachedEditorCameraPatches), $"Patch target not found: {description}. The game version may have changed; skipping this patch.");
            return;
        }

        harmony.Patch(target, prefix: prefix);
    }

    private static void UnpatchIfFound(Harmony harmony, MethodBase? target, HarmonyPatchType patchType, string harmonyId)
    {
        if (target == null) return;
        harmony.Unpatch(target, patchType, harmonyId);
    }

    private static bool UpdateCameraYawPitchPrefix(ClientMain __instance)
    {
        return !DetachedEditorCamera.SuppressVanillaCameraUpdate(__instance);
    }

    private static void LoadModelMatrixForPlayerPrefix(ref bool isSelf)
    {
        if (isSelf && DetachedEditorCamera.IsActive)
        {
            isSelf = false;
        }
    }

    private static bool GetAboveHeadPositionPrefix(EntityPlayer entityPlayer, ref Vec3d __result)
    {
        if (!DetachedEditorCamera.IsActive) return true;

        __result = new Vec3d(
            entityPlayer.Pos.X,
            entityPlayer.Pos.InternalY + entityPlayer.LocalEyePos.Y + 0.4,
            entityPlayer.Pos.Z);
        return false;
    }
}
