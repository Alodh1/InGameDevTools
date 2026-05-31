using InGameDevTools.Utils;
using HarmonyLib;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace InGameDevTools.Integration;

internal static class ParticleRuntimePatches
{
    private static readonly object OverridesLock = new();
    private static readonly object PatchLock = new();
    private static readonly Dictionary<string, SortedDictionary<int, AdvancedParticleProperties>> Overrides = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<Block, object> BlockLocks = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static ICoreAPI? Api;
    private static string? HarmonyId;
    private static bool RuntimePatched;

    public static void Patch(string harmonyId, ICoreAPI api)
    {
        lock (PatchLock)
        {
            HarmonyId = harmonyId;
            Api = api;
        }

        if (HasOverrides())
        {
            EnsureRuntimePatched();
        }
    }

    public static void Unpatch(string harmonyId)
    {
        lock (PatchLock)
        {
            Harmony harmony = new(harmonyId);
            harmony.UnpatchAll(harmonyId);
            PatchedMethods.Clear();
            RuntimePatched = false;
            HarmonyId = null;
            Api = null;
        }

        lock (OverridesLock)
        {
            Overrides.Clear();
        }
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

        EnsureRuntimePatched();

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

        UnpatchRuntimeIfIdle();
    }

    public static void ClearOverrides()
    {
        lock (OverridesLock)
        {
            Overrides.Clear();
        }

        UnpatchRuntimeIfIdle();
    }

    private static void EnsureRuntimePatched()
    {
        lock (PatchLock)
        {
            if (RuntimePatched || string.IsNullOrWhiteSpace(HarmonyId) || Api == null) return;

            Harmony harmony = new(HarmonyId);
            int patchedCount = 0;
            foreach (MethodInfo method in EnumerateParticleEmitterMethods(Api))
            {
                string methodKey = $"{method.Module.ModuleVersionId}:{method.MetadataToken}";
                if (!PatchedMethods.Add(methodKey)) continue;

                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(typeof(ParticleRuntimePatches), nameof(ParticleEmitterPrefix)),
                        finalizer: new HarmonyMethod(typeof(ParticleRuntimePatches), nameof(ParticleEmitterFinalizer)));
                    patchedCount++;
                }
                catch (Exception exception)
                {
                    LoggerUtil.Warn(Api, typeof(ParticleRuntimePatches), $"Could not patch particle runtime target {method.DeclaringType?.FullName}.{method.Name}: {exception.Message}");
                }
            }

            RuntimePatched = patchedCount > 0;
            if (RuntimePatched)
            {
                LoggerUtil.Verbose(Api, typeof(ParticleRuntimePatches), $"Particle runtime override patches installed on {patchedCount} method(s).");
            }
            else
            {
                LoggerUtil.Warn(Api, typeof(ParticleRuntimePatches), "No particle runtime patch targets were found; particle overrides will be inactive.");
            }
        }
    }

    private static void UnpatchRuntimeIfIdle()
    {
        if (HasOverrides()) return;

        lock (PatchLock)
        {
            if (!RuntimePatched || string.IsNullOrWhiteSpace(HarmonyId) || HasOverrides()) return;

            Harmony harmony = new(HarmonyId);
            harmony.UnpatchAll(HarmonyId);
            PatchedMethods.Clear();
            RuntimePatched = false;
            LoggerUtil.Verbose(Api, typeof(ParticleRuntimePatches), "Particle runtime override patches removed because no overrides remain.");
        }
    }

    private static bool HasOverrides()
    {
        lock (OverridesLock)
        {
            return Overrides.Count > 0;
        }
    }

    private static IEnumerable<MethodInfo> EnumerateParticleEmitterMethods(ICoreAPI api)
    {
        Dictionary<string, MethodInfo> methods = new(StringComparer.Ordinal);
        HashSet<Type> blockTypes = new();
        AddParticleEmitterMethod(typeof(Block), methods);

        foreach (Block block in api.World.Blocks)
        {
            if (block == null) continue;
            Type blockType = block.GetType();
            if (blockTypes.Add(blockType))
            {
                AddParticleEmitterMethod(blockType, methods);
            }
        }

        return methods.Values;
    }

    private static void AddParticleEmitterMethod(Type type, Dictionary<string, MethodInfo> methods)
    {
        if (!typeof(Block).IsAssignableFrom(type)) return;

        MethodInfo? asyncTick = AccessTools.Method(
            type,
            nameof(Block.OnAsyncClientParticleTick),
            [typeof(IAsyncParticleManager), typeof(BlockPos), typeof(float), typeof(float)]);
        AddMethod(asyncTick, methods);
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

        object syncRoot = BlockLocks.GetValue(__instance, static _ => new object());
        bool lockTaken = false;
        Monitor.Enter(syncRoot, ref lockTaken);

        try
        {
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

            __instance.ParticleProperties = patched;
            __state = new ParticleEmitterState(original, syncRoot);
            lockTaken = false;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    private static Exception? ParticleEmitterFinalizer(Block __instance, ParticleEmitterState? __state, Exception? __exception)
    {
        if (__state != null)
        {
            try
            {
                __instance.ParticleProperties = __state.Original;
            }
            finally
            {
                Monitor.Exit(__state.SyncRoot);
            }
        }

        return __exception;
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

    private sealed class ParticleEmitterState(AdvancedParticleProperties[]? original, object syncRoot)
    {
        public AdvancedParticleProperties[]? Original { get; } = original;
        public object SyncRoot { get; } = syncRoot;
    }
}
