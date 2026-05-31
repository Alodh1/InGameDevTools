using HarmonyLib;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace InGameDevTools.Integration;

internal static class ParticleRuntimePatches
{
    private static readonly object OverridesLock = new();
    private static readonly Dictionary<string, SortedDictionary<int, AdvancedParticleProperties>> Overrides = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);

    public static void Patch(string harmonyId, ICoreAPI api)
    {
        Harmony harmony = new(harmonyId);
        foreach (MethodInfo method in EnumerateParticleEmitterMethods(api))
        {
            string methodKey = $"{method.Module.ModuleVersionId}:{method.MetadataToken}";
            if (!PatchedMethods.Add(methodKey)) continue;

            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(ParticleRuntimePatches), nameof(ParticleEmitterPrefix)),
                postfix: new HarmonyMethod(typeof(ParticleRuntimePatches), nameof(ParticleEmitterPostfix)));
        }
    }

    public static void Unpatch(string harmonyId)
    {
        Harmony harmony = new(harmonyId);
        harmony.UnpatchAll(harmonyId);
        PatchedMethods.Clear();
        ClearOverrides();
    }

    public static string? SetOverride(CollectibleObject collectible, int index, AdvancedParticleProperties properties)
    {
        if (collectible is not Block || collectible.Code == null || index < 0) return null;

        string collectibleKey = collectible.Code.ToString();
        lock (OverridesLock)
        {
            if (!Overrides.TryGetValue(collectibleKey, out SortedDictionary<int, AdvancedParticleProperties>? byIndex))
            {
                Overrides[collectibleKey] = byIndex = new SortedDictionary<int, AdvancedParticleProperties>();
            }

            byIndex[index] = properties.Clone();
        }

        return GetOverrideKey(collectibleKey, index);
    }

    public static void RemoveOverride(string overrideKey)
    {
        (string collectibleKey, int index) = SplitOverrideKey(overrideKey);
        if (string.IsNullOrWhiteSpace(collectibleKey) || index < 0) return;

        lock (OverridesLock)
        {
            if (!Overrides.TryGetValue(collectibleKey, out SortedDictionary<int, AdvancedParticleProperties>? byIndex)) return;
            byIndex.Remove(index);
            if (byIndex.Count == 0)
            {
                Overrides.Remove(collectibleKey);
            }
        }
    }

    public static void ClearOverrides()
    {
        lock (OverridesLock)
        {
            Overrides.Clear();
        }
    }

    private static IEnumerable<MethodInfo> EnumerateParticleEmitterMethods(ICoreAPI api)
    {
        Dictionary<string, MethodInfo> methods = new(StringComparer.Ordinal);
        AddParticleEmitterMethods(typeof(Block), methods);

        foreach (Block block in api.World.Blocks)
        {
            if (block == null) continue;
            AddParticleEmitterMethods(block.GetType(), methods);
        }

        return methods.Values;
    }

    private static void AddParticleEmitterMethods(Type type, Dictionary<string, MethodInfo> methods)
    {
        if (!typeof(Block).IsAssignableFrom(type)) return;

        MethodInfo? asyncTick = AccessTools.Method(
            type,
            nameof(Block.OnAsyncClientParticleTick),
            [typeof(IAsyncParticleManager), typeof(BlockPos), typeof(float), typeof(float)]);
        AddMethod(asyncTick, methods);

        foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (!method.Name.Contains("Particle", StringComparison.OrdinalIgnoreCase)) continue;
            ParameterInfo[] parameters = method.GetParameters();
            bool hasParticleManager = parameters.Any(parameter => typeof(IAsyncParticleManager).IsAssignableFrom(parameter.ParameterType));
            bool hasBlockPos = parameters.Any(parameter => parameter.ParameterType == typeof(BlockPos));
            if (hasParticleManager && hasBlockPos)
            {
                AddMethod(method, methods);
            }
        }
    }

    private static void AddMethod(MethodInfo? method, Dictionary<string, MethodInfo> methods)
    {
        if (method == null) return;
        string methodKey = $"{method.Module.ModuleVersionId}:{method.MetadataToken}";
        methods[methodKey] = method;
    }

    private static void ParticleEmitterPrefix(Block __instance, out ParticleEmitterState? __state)
    {
        __state = null;
        if (__instance?.Code == null) return;

        SortedDictionary<int, AdvancedParticleProperties>? overrides;
        lock (OverridesLock)
        {
            if (!Overrides.TryGetValue(__instance.Code.ToString(), out SortedDictionary<int, AdvancedParticleProperties>? configured) || configured.Count == 0)
            {
                return;
            }

            overrides = new SortedDictionary<int, AdvancedParticleProperties>(configured);
        }

        AdvancedParticleProperties[]? original = __instance.ParticleProperties;
        int length = Math.Max(original?.Length ?? 0, overrides.Keys.Max() + 1);
        AdvancedParticleProperties[] patched = new AdvancedParticleProperties[length];
        for (int index = 0; index < length; index++)
        {
            if (original != null && index < original.Length && original[index] != null)
            {
                patched[index] = original[index].Clone();
            }
        }

        foreach ((int index, AdvancedParticleProperties properties) in overrides)
        {
            patched[index] = properties.Clone();
        }

        __state = new ParticleEmitterState(original);
        __instance.ParticleProperties = patched;
    }

    private static void ParticleEmitterPostfix(Block __instance, ParticleEmitterState? __state)
    {
        if (__state != null)
        {
            __instance.ParticleProperties = __state.Original;
        }
    }

    private static string GetOverrideKey(string collectibleKey, int index)
    {
        return $"{collectibleKey}#ParticleProperties[{index}]";
    }

    private static (string CollectibleKey, int Index) SplitOverrideKey(string overrideKey)
    {
        const string marker = "#ParticleProperties[";
        int markerIndex = overrideKey.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0) return ("", -1);

        int start = markerIndex + marker.Length;
        int end = overrideKey.IndexOf(']', start);
        if (end <= start || !int.TryParse(overrideKey[start..end], out int index)) return ("", -1);

        return (overrideKey[..markerIndex], index);
    }

    private sealed class ParticleEmitterState(AdvancedParticleProperties[]? original)
    {
        public AdvancedParticleProperties[]? Original { get; } = original;
    }
}
