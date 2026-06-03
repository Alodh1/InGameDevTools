using InGameDevTools.Animations;
using InGameDevTools.Integration;
using InGameDevTools.Integration.Transpilers;
using InGameDevTools.Utils;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using VSImGui;
using System.Reflection;

namespace InGameDevTools;

public sealed class InGameDevToolsModSystem : ModSystem
{
    private const string TranspilerHarmonyId = "InGameDevTools:Transpilers";
    private const string AnimationHarmonyId = "InGameDevTools:Animation";
    private const string DetachedCameraHarmonyId = "InGameDevTools:DetachedCamera";
    private const string ParticleRuntimeHarmonyId = "InGameDevTools:ParticleRuntime";

    private ICoreClientAPI? _api;
    private ParticleEffectsManager? _particleEffectsManager;
    private AnimationsManager? _animationsManager;
    private DebugWindowManager? _debugWindowManager;
    private long _ensureBehaviorsListener = -1;
    private static bool _fontExtractionAttempted;

    public static string? BundledOpenDyslexicFontPath { get; private set; }

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartPre(ICoreAPI api)
    {
        ExtendedElementPose.NameHashCache = new(api, "in-game devtools element pose name hash cache", 500000, 11 * 60 * 1000, threadSafe: true);
        ExtractBundledFonts(api);
    }

    public override void Start(ICoreAPI api)
    {
        RegisterStandaloneClasses(api);

        PlayerRenderingPatches.Api = api;
        new Harmony(TranspilerHarmonyId).PatchAll(typeof(ExtendedElementPose).Assembly);
        AnimationPatches.Patch(AnimationHarmonyId, api);
        DetachedEditorCameraPatches.Patch(DetachedCameraHarmonyId, api);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        _api = api;

        DevToolsConfig config;
        try
        {
            config = api.LoadModConfig<DevToolsConfig>(DevToolsConfig.FileName) ?? new DevToolsConfig();
        }
        catch (Exception exception)
        {
            LoggerUtil.Warn(api, this, $"Could not load {DevToolsConfig.FileName}; using defaults: {exception.Message}");
            config = new DevToolsConfig();
        }
        config.Normalize();
        api.StoreModConfig(config, DevToolsConfig.FileName);

        _particleEffectsManager = new ParticleEffectsManager(api);
        _animationsManager = new AnimationsManager(api, _particleEffectsManager);
        _debugWindowManager = new DebugWindowManager(api, _particleEffectsManager, config);

        api.Input.RegisterHotKey("ingamedevtools_toggle", "Show In-game devtools", GlKeys.L, ctrlPressed: true, shiftPressed: true);
        api.Input.SetHotKeyHandler("ingamedevtools_toggle", _ =>
        {
            _debugWindowManager?.ToggleExternalDevTools();
            return true;
        });

        api.Event.PlayerEntitySpawn += EnsurePlayerAnimationBehaviors;
        api.Event.LevelFinalize += EnsurePlayerAnimationBehaviors;
        _ensureBehaviorsListener = api.Event.RegisterGameTickListener(_ => EnsurePlayerAnimationBehaviors(), 1000, 1000);

        if (config.OpenOnStartup)
        {
            api.Event.EnqueueMainThreadTask(() => _debugWindowManager?.OpenExternalDevTools(), "ingamedevtools-open");
        }
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        _particleEffectsManager?.LoadAssets();
        ParticleRuntimePatches.Patch(ParticleRuntimeHarmonyId, api);
        _animationsManager?.Load();
        if (api is ICoreClientAPI clientApi)
        {
            _debugWindowManager?.Load(clientApi);
            EnsurePlayerAnimationBehaviors();
        }
    }

    public override void Dispose()
    {
        _debugWindowManager?.Dispose();

        if (_api != null)
        {
            _api.Event.PlayerEntitySpawn -= EnsurePlayerAnimationBehaviors;
            _api.Event.LevelFinalize -= EnsurePlayerAnimationBehaviors;
            StopEnsureBehaviorsListener();
        }

        new Harmony(TranspilerHarmonyId).UnpatchAll(TranspilerHarmonyId);
        AnimationPatches.Unpatch(AnimationHarmonyId);
        DetachedEditorCameraPatches.Unpatch(DetachedCameraHarmonyId);
        ParticleRuntimePatches.Unpatch(ParticleRuntimeHarmonyId);
        PlayerRenderingPatches.Api = null;
        ExtendedElementPose.NameHashCache?.Dispose();
        ExtendedElementPose.NameHashCache = null;

        _debugWindowManager = null;
        _animationsManager = null;
        _particleEffectsManager = null;
        _api = null;
    }

    private void EnsurePlayerAnimationBehaviors(IClientPlayer player)
    {
        if (player.Entity?.EntityId == _api?.World?.Player?.Entity?.EntityId)
        {
            EnsurePlayerAnimationBehaviors();
        }
    }

    private void EnsurePlayerAnimationBehaviors()
    {
        if (StandaloneDevtoolsRuntime.EnsurePlayerAnimationBehaviors(_api))
        {
            StopEnsureBehaviorsListener();
        }
    }

    private void StopEnsureBehaviorsListener()
    {
        if (_api == null || _ensureBehaviorsListener == -1) return;

        _api.Event.UnregisterGameTickListener(_ensureBehaviorsListener);
        _ensureBehaviorsListener = -1;
    }

    private static void RegisterStandaloneClasses(ICoreAPI api)
    {
        TryRegister(api, "entity behavior InGameDevTools:FirstPersonAnimations", () => api.RegisterEntityBehaviorClass("InGameDevTools:FirstPersonAnimations", typeof(FirstPersonAnimationsBehavior)));
        TryRegister(api, "entity behavior InGameDevTools:ThirdPersonAnimations", () => api.RegisterEntityBehaviorClass("InGameDevTools:ThirdPersonAnimations", typeof(ThirdPersonAnimationsBehavior)));
        TryRegister(api, "entity behavior InGameDevTools:EntityColliders", () => api.RegisterEntityBehaviorClass("InGameDevTools:EntityColliders", typeof(InGameDevTools.Colliders.CollidersEntityBehavior)));
        TryRegister(api, "collectible behavior InGameDevTools:Animatable", () => api.RegisterCollectibleBehaviorClass("InGameDevTools:Animatable", typeof(Animatable)));
        TryRegister(api, "collectible behavior InGameDevTools:AnimatableAttachable", () => api.RegisterCollectibleBehaviorClass("InGameDevTools:AnimatableAttachable", typeof(AnimatableAttachable)));
        TryRegister(api, "collectible behavior AnimationsLib:Animatable", () => api.RegisterCollectibleBehaviorClass("AnimationsLib:Animatable", typeof(Animatable)));
        TryRegister(api, "collectible behavior AnimationsLib:AnimatableAttachable", () => api.RegisterCollectibleBehaviorClass("AnimationsLib:AnimatableAttachable", typeof(AnimatableAttachable)));
    }

    private static void TryRegister(ICoreAPI api, string target, Action register)
    {
        try
        {
            register();
        }
        catch (Exception exception)
        {
            LoggerUtil.Verbose(api, typeof(InGameDevToolsModSystem), $"Could not register {target}: {exception}");
        }
    }

    private static void ExtractBundledFonts(ICoreAPI api)
    {
        if (_fontExtractionAttempted) return;
        _fontExtractionAttempted = true;

        try
        {
            string? fontPath = ExtractEmbeddedFont(api, "InGameDevTools.Fonts.OpenDyslexic-Regular.otf", "OpenDyslexic-Regular.otf");
            if (string.IsNullOrWhiteSpace(fontPath)) return;
            BundledOpenDyslexicFontPath = fontPath;
        }
        catch (Exception exception)
        {
            LoggerUtil.Warn(api, typeof(InGameDevToolsModSystem), $"Could not extract bundled OpenDyslexic font: {exception}");
        }
    }

    private static string? ExtractEmbeddedFont(ICoreAPI api, string resourceName, string fileName)
    {
        Assembly assembly = typeof(InGameDevToolsModSystem).Assembly;
        using Stream? resource = assembly.GetManifestResourceStream(resourceName);
        if (resource == null)
        {
            LoggerUtil.Warn(api, typeof(InGameDevToolsModSystem), $"Bundled font resource not found: {resourceName}");
            return null;
        }

        string directory = Path.Combine(GamePaths.Cache, "ingamedevtools", "fonts");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        if (File.Exists(path) && new FileInfo(path).Length == resource.Length)
        {
            return path;
        }

        using FileStream output = File.Create(path);
        resource.CopyTo(output);
        return path;
    }
}

public sealed class DevToolsConfig
{
    public const string FileName = "ingamedevtools.json";
    public const string PresetVintageBrown = "Vintage Brown";
    public const string FontDefault = "Default";

    public bool OpenOnStartup { get; set; }
    public float UiScale { get; set; } = 1f;
    public bool ShowDiagnostics { get; set; }
    public bool AutoRuntimeApply { get; set; }
    public bool WriteLiveBackups { get; set; }
    public string ThemePreset { get; set; } = PresetVintageBrown;
    public bool ApplyStyleGlobally { get; set; }
    public string FontName { get; set; } = FontDefault;
    public int FontSize { get; set; } = 16;
    public string AnimationIkMode { get; set; } = "AutoConservative";
    public bool AnimationIkPreserveDraggedPartRotation { get; set; } = true;
    public bool AnimationIkLockMoveToDragAxis { get; set; } = true;
    public Dictionary<string, string[]> AnimationIkAnchors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, float[]> AdvancedColorOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public float FramePaddingX { get; set; } = -1f;
    public float FramePaddingY { get; set; } = -1f;
    public float ItemSpacingX { get; set; } = -1f;
    public float ItemSpacingY { get; set; } = -1f;
    public float FrameRounding { get; set; } = -1f;
    public float WindowRounding { get; set; } = -1f;
    public float HoverDelayNormal { get; set; } = -1f;
    public float HoverDelayShort { get; set; } = -1f;

    public void Normalize()
    {
        UiScale = ClampOrDefault(UiScale, 0.75f, 1.75f, 1f);
        FontSize = Math.Clamp(FontSize <= 0 ? 16 : FontSize, 12, 28);
        ThemePreset = string.IsNullOrWhiteSpace(ThemePreset) ? PresetVintageBrown : ThemePreset.Trim();
        FontName = string.IsNullOrWhiteSpace(FontName) ? FontDefault : FontName.Trim();
        AnimationIkMode = NormalizeAnimationIkMode(AnimationIkMode);
        AnimationIkAnchors ??= new(StringComparer.OrdinalIgnoreCase);
        AnimationIkAnchors = AnimationIkAnchors
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
            .ToDictionary(
                pair => pair.Key.Trim(),
                pair => pair.Value
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        AdvancedColorOverrides ??= new(StringComparer.OrdinalIgnoreCase);
        AdvancedColorOverrides = AdvancedColorOverrides
            .Where(pair => pair.Value is { Length: >= 4 })
            .ToDictionary(
                pair => pair.Key,
                pair => new[]
                {
                    ClampOrDefault(pair.Value[0], 0f, 1f, 1f),
                    ClampOrDefault(pair.Value[1], 0f, 1f, 1f),
                    ClampOrDefault(pair.Value[2], 0f, 1f, 1f),
                    ClampOrDefault(pair.Value[3], 0f, 1f, 1f)
                },
                StringComparer.OrdinalIgnoreCase);

        FramePaddingX = NormalizeOptional(FramePaddingX, 0f, 32f);
        FramePaddingY = NormalizeOptional(FramePaddingY, 0f, 32f);
        ItemSpacingX = NormalizeOptional(ItemSpacingX, 0f, 48f);
        ItemSpacingY = NormalizeOptional(ItemSpacingY, 0f, 48f);
        FrameRounding = NormalizeOptional(FrameRounding, 0f, 16f);
        WindowRounding = NormalizeOptional(WindowRounding, 0f, 16f);
        HoverDelayNormal = NormalizeOptional(HoverDelayNormal, 0f, 3f);
        HoverDelayShort = NormalizeOptional(HoverDelayShort, 0f, 3f);
    }

    private static float NormalizeOptional(float value, float min, float max)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f) return -1f;
        return Math.Clamp(value, min, max);
    }

    private static float ClampOrDefault(float value, float min, float max, float fallback)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return fallback;
        return Math.Clamp(value, min, max);
    }

    private static string NormalizeAnimationIkMode(string? value)
    {
        if (string.Equals(value, "AutoExtended", StringComparison.OrdinalIgnoreCase)) return "AutoExtended";
        if (string.Equals(value, "ManualOverride", StringComparison.OrdinalIgnoreCase)) return "ManualOverride";
        return "AutoConservative";
    }
}

internal static class StandaloneDevtoolsRuntime
{
    public static bool EnsurePlayerAnimationBehaviors(ICoreClientAPI? api)
    {
        try
        {
            EntityPlayer? playerEntity = api?.World?.Player?.Entity;
            if (playerEntity == null) return false;

            JsonObject emptyAttributes = new(new JObject());

            if (playerEntity.GetBehavior<FirstPersonAnimationsBehavior>() == null)
            {
                FirstPersonAnimationsBehavior firstPerson = new(playerEntity);
                playerEntity.AddBehavior(firstPerson);
                firstPerson.Initialize(playerEntity.Properties, emptyAttributes);
                firstPerson.AfterInitialized(false);
            }

            if (playerEntity.GetBehavior<ThirdPersonAnimationsBehavior>() == null)
            {
                ThirdPersonAnimationsBehavior thirdPerson = new(playerEntity);
                playerEntity.AddBehavior(thirdPerson);
                thirdPerson.Initialize(playerEntity.Properties, emptyAttributes);
                thirdPerson.AfterInitialized(false);
            }

            return playerEntity.GetBehavior<FirstPersonAnimationsBehavior>() != null &&
                playerEntity.GetBehavior<ThirdPersonAnimationsBehavior>() != null;
        }
        catch (Exception exception)
        {
            LoggerUtil.Warn(api, typeof(StandaloneDevtoolsRuntime), $"Could not attach standalone animation preview behaviors: {exception}");
            return false;
        }
    }
}
