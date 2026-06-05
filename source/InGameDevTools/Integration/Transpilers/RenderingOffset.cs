using InGameDevTools.Utils;
using HarmonyLib;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace InGameDevTools.Integration.Transpilers;

internal static class PlayerRenderingPatches
{
    private const int OffsetAnchorSearchWindow = 18;
    private static readonly FieldInfo? ModSysField = typeof(EntityPlayerShapeRenderer).GetField("modSys", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo? GetMultiplierMethod = typeof(PlayerRenderingPatches).GetMethod(nameof(GetMultiplier));
    private static readonly MethodInfo? GetOffsetMethod = typeof(PlayerRenderingPatches).GetMethod(nameof(GetOffset));
    private static readonly MethodInfo? GetOffsetAdjustedMethod = typeof(PlayerRenderingPatches).GetMethod(nameof(GetOffsetAdjusted));

    internal static ICoreAPI? Api { private get; set; }

    public static readonly float DefaultFpHandsOffset = -0.3f;
    public static float HandsFovMultiplier { get; set; } = 1;
    public static float FpHandsOffset { get; set; } = DefaultFpHandsOffset;

    public static float ResetOffset() => FpHandsOffset = DefaultFpHandsOffset;
    public static float SetOffset(float offset) => FpHandsOffset = offset;
    public static float GetOffset(ModSystemFpHands modSys) => FpHandsOffset;
    public static float GetOffsetAdjusted(ModSystemFpHands modSys) => FpHandsOffset;
    public static float GetNetOffset() => FpHandsOffset - GameMath.Max(0f, ClientSettings.FieldOfView / 90f - 1f) / 2f;
    public static float GetMultiplier() => HandsFovMultiplier;

    [HarmonyPatch(typeof(EntityPlayerShapeRenderer), "DoRender3DOpaque")]
    [HarmonyPatchCategory("ingamedevtools")]
    public class EntityShapeRendererPatch
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new(instructions);
            bool fovPatched = false;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo propertyInfo && propertyInfo.Name == "get_HandRenderFov")
                {
                    if (GetMultiplierMethod == null)
                    {
                        LogTranspilerWarning("Could not resolve GetMultiplier helper; hand FOV multiplier patch skipped.");
                        continue;
                    }

                    codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, GetMultiplierMethod));
                    codes.Insert(i + 2, new CodeInstruction(OpCodes.Mul));
                    fovPatched = true;
                }
            }

            if (!fovPatched)
            {
                LogTranspilerWarning("EntityPlayerShapeRenderer.DoRender3DOpaque get_HandRenderFov anchor not found; hand FOV multiplier patch skipped.");
            }

            if (!TryReplaceOffsetNearFieldOfView(codes, GetOffsetAdjustedMethod, "EntityPlayerShapeRenderer.DoRender3DOpaque"))
            {
                LogTranspilerWarning("EntityPlayerShapeRenderer.DoRender3DOpaque hand offset pattern not found; first-person hand offset patch skipped.");
            }

            return codes;
        }
    }

    [HarmonyPatch(typeof(EntityPlayerShapeRenderer), "getReadyShader")]
    [HarmonyPatchCategory("ingamedevtools")]
    public class EntityShapeRendererShaderPatch
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new(instructions);
            if (!TryReplaceOffsetNearFieldOfView(codes, GetOffsetMethod, "EntityPlayerShapeRenderer.getReadyShader"))
            {
                LogTranspilerWarning("EntityPlayerShapeRenderer.getReadyShader hand offset pattern not found; shader hand offset patch skipped.");
            }

            return codes;
        }
    }

    private static bool TryReplaceOffsetNearFieldOfView(List<CodeInstruction> codes, MethodInfo? replacement, string patchTarget)
    {
        if (replacement == null)
        {
            LogTranspilerWarning($"{patchTarget} offset helper could not be resolved.");
            return false;
        }

        if (ModSysField == null)
        {
            LogTranspilerWarning($"{patchTarget} modSys field could not be resolved.");
            return false;
        }

        for (int i = 0; i < codes.Count; i++)
        {
            if (!IsDefaultHandsOffsetInstruction(codes[i]) || !HasFieldOfViewAnchor(codes, i)) continue;

            codes[i].opcode = OpCodes.Call;
            codes[i].operand = replacement;

            codes.Insert(i, new CodeInstruction(OpCodes.Ldarg_0));
            codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldfld, ModSysField));
            return true;
        }

        return false;
    }

    private static bool IsDefaultHandsOffsetInstruction(CodeInstruction instruction)
    {
        return instruction.opcode == OpCodes.Ldc_R4 &&
            instruction.operand is float value &&
            Math.Abs(value - DefaultFpHandsOffset) < 0.0001f;
    }

    private static bool HasFieldOfViewAnchor(List<CodeInstruction> codes, int offsetInstructionIndex)
    {
        int start = Math.Max(0, offsetInstructionIndex - OffsetAnchorSearchWindow);
        int end = Math.Min(codes.Count - 1, offsetInstructionIndex + OffsetAnchorSearchWindow);

        for (int i = start; i <= end; i++)
        {
            if (codes[i].operand is MemberInfo member &&
                member.Name.Contains("FieldOfView", StringComparison.Ordinal) &&
                member.DeclaringType?.FullName == typeof(ClientSettings).FullName)
            {
                return true;
            }
        }

        return false;
    }

    private static void LogTranspilerWarning(string message)
    {
        LoggerUtil.Warn(Api, typeof(PlayerRenderingPatches), message);
    }
}
