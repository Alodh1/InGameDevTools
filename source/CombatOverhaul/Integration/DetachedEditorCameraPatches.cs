#if DEBUG
using CombatOverhaul.Animations;
using HarmonyLib;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace CombatOverhaul.Integration;

internal static class DetachedEditorCameraPatches
{
    public static void Patch(string harmonyId)
    {
        Harmony harmony = new(harmonyId);
        harmony.Patch(
            typeof(ClientMain).GetMethod(nameof(ClientMain.UpdateCameraYawPitch), BindingFlags.Instance | BindingFlags.Public),
            prefix: new HarmonyMethod(typeof(DetachedEditorCameraPatches), nameof(UpdateCameraYawPitchPrefix))
            {
                priority = Priority.First
            });

        harmony.Patch(
            typeof(EntityPlayerShapeRenderer).GetMethod("loadModelMatrixForPlayer", AccessTools.all),
            prefix: new HarmonyMethod(typeof(DetachedEditorCameraPatches), nameof(LoadModelMatrixForPlayerPrefix)));

        harmony.Patch(
            typeof(EntityPlayerShapeRenderer).GetMethod("getAboveHeadPosition", AccessTools.all),
            prefix: new HarmonyMethod(typeof(DetachedEditorCameraPatches), nameof(GetAboveHeadPositionPrefix)));
    }

    public static void Unpatch(string harmonyId)
    {
        Harmony harmony = new(harmonyId);
        harmony.Unpatch(typeof(ClientMain).GetMethod(nameof(ClientMain.UpdateCameraYawPitch), BindingFlags.Instance | BindingFlags.Public), HarmonyPatchType.Prefix, harmonyId);
        harmony.Unpatch(typeof(EntityPlayerShapeRenderer).GetMethod("loadModelMatrixForPlayer", AccessTools.all), HarmonyPatchType.Prefix, harmonyId);
        harmony.Unpatch(typeof(EntityPlayerShapeRenderer).GetMethod("getAboveHeadPosition", AccessTools.all), HarmonyPatchType.Prefix, harmonyId);
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
#endif
