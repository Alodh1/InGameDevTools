using InGameDevTools.Animations;
using InGameDevTools.Integration;
using InGameDevTools.Integration.Transpilers;
using InGameDevTools.Utils;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using VSImGui;

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

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartPre(ICoreAPI api)
    {
        ExtendedElementPose.NameHashCache = new(api, "in-game devtools element pose name hash cache", 500000, 11 * 60 * 1000, threadSafe: true);
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

        DevToolsConfig config = api.LoadModConfig<DevToolsConfig>(DevToolsConfig.FileName) ?? new DevToolsConfig();
        api.StoreModConfig(config, DevToolsConfig.FileName);

        _particleEffectsManager = new ParticleEffectsManager(api);
        _animationsManager = new AnimationsManager(api, _particleEffectsManager);
        _debugWindowManager = new DebugWindowManager(api, _particleEffectsManager);

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
}

public sealed class DevToolsConfig
{
    public const string FileName = "ingamedevtools.json";

    public bool OpenOnStartup { get; set; }
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
