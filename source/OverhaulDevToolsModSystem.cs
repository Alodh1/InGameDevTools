using CombatOverhaul.Animations;
using CombatOverhaul.Integration;
using CombatOverhaul.Integration.Transpilers;
using CombatOverhaul.Utils;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using VSImGui;

namespace OverhaulDevTools;

public sealed class OverhaulDevToolsModSystem : ModSystem
{
    private const string TranspilerHarmonyId = "OverhaulDevTools:Transpilers";
    private const string AnimationHarmonyId = "OverhaulDevTools:Animation";
    private const string DetachedCameraHarmonyId = "OverhaulDevTools:DetachedCamera";

    private ICoreClientAPI? _api;
    private ParticleEffectsManager? _particleEffectsManager;
    private AnimationsManager? _animationsManager;
    private DebugWindowManager? _debugWindowManager;
    private long _ensureBehaviorsListener = -1;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartPre(ICoreAPI api)
    {
        ExtendedElementPose.NameHashCache = new(api, "overhaul devtools element pose name hash cache", 500000, 11 * 60 * 1000, threadSafe: true);
    }

    public override void Start(ICoreAPI api)
    {
        RegisterStandaloneClasses(api);

        new Harmony(TranspilerHarmonyId).PatchAll(typeof(ExtendedElementPose).Assembly);
        AnimationPatches.Patch(AnimationHarmonyId, api);
        DetachedEditorCameraPatches.Patch(DetachedCameraHarmonyId);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        _api = api;

        DevToolsConfig config = api.LoadModConfig<DevToolsConfig>(DevToolsConfig.FileName) ?? new DevToolsConfig();
        api.StoreModConfig(config, DevToolsConfig.FileName);

        _particleEffectsManager = new ParticleEffectsManager(api);
        _animationsManager = new AnimationsManager(api, _particleEffectsManager);
        _debugWindowManager = new DebugWindowManager(api, _particleEffectsManager);

        api.Input.RegisterHotKey("overhauldevtools_toggle", "Show Overhaul devtools", GlKeys.L, ctrlPressed: true, shiftPressed: true);
        api.Input.SetHotKeyHandler("overhauldevtools_toggle", _ =>
        {
            _debugWindowManager?.ToggleExternalDevTools();
            return true;
        });

        api.Event.PlayerEntitySpawn += EnsurePlayerAnimationBehaviors;
        api.Event.LevelFinalize += EnsurePlayerAnimationBehaviors;
        _ensureBehaviorsListener = api.Event.RegisterGameTickListener(_ => EnsurePlayerAnimationBehaviors(), 1000, 1000);

        if (config.OpenOnStartup)
        {
            api.Event.EnqueueMainThreadTask(() => _debugWindowManager?.OpenExternalDevTools(), "overhauldevtools-open");
        }
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        _animationsManager?.Load();
        if (api is ICoreClientAPI clientApi)
        {
            _debugWindowManager?.Load(clientApi);
            EnsurePlayerAnimationBehaviors();
        }
    }

    public override void Dispose()
    {
        if (_api != null)
        {
            _api.Event.PlayerEntitySpawn -= EnsurePlayerAnimationBehaviors;
            _api.Event.LevelFinalize -= EnsurePlayerAnimationBehaviors;
            if (_ensureBehaviorsListener != -1)
            {
                _api.Event.UnregisterGameTickListener(_ensureBehaviorsListener);
            }
        }

        new Harmony(TranspilerHarmonyId).UnpatchAll(TranspilerHarmonyId);
        AnimationPatches.Unpatch(AnimationHarmonyId);
        DetachedEditorCameraPatches.Unpatch(DetachedCameraHarmonyId);
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
        StandaloneDevtoolsRuntime.EnsurePlayerAnimationBehaviors(_api);
    }

    private static void RegisterStandaloneClasses(ICoreAPI api)
    {
        if (api.ModLoader.IsModEnabled("overhaulliblegacycompat") ||
            api.ModLoader.IsModEnabled("combatoverhaul") ||
            api.ModLoader.IsModEnabled("combatoverhaulfork"))
        {
            return;
        }

        TryRegister(() => api.RegisterEntityBehaviorClass("CombatOverhaul:FirstPersonAnimations", typeof(FirstPersonAnimationsBehavior)));
        TryRegister(() => api.RegisterEntityBehaviorClass("CombatOverhaul:ThirdPersonAnimations", typeof(ThirdPersonAnimationsBehavior)));
        TryRegister(() => api.RegisterEntityBehaviorClass("CombatOverhaul:EntityColliders", typeof(CombatOverhaul.Colliders.CollidersEntityBehavior)));
        TryRegister(() => api.RegisterCollectibleBehaviorClass("CombatOverhaul:Animatable", typeof(Animatable)));
        TryRegister(() => api.RegisterCollectibleBehaviorClass("CombatOverhaul:AnimatableAttachable", typeof(AnimatableAttachable)));
        TryRegister(() => api.RegisterCollectibleBehaviorClass("AnimationsLib:Animatable", typeof(Animatable)));
        TryRegister(() => api.RegisterCollectibleBehaviorClass("AnimationsLib:AnimatableAttachable", typeof(AnimatableAttachable)));
    }

    private static void TryRegister(Action register)
    {
        try
        {
            register();
        }
        catch
        {
            // Another mod may already own the legacy class code. The standalone editor can
            // still attach its local preview behaviors directly to the player.
        }
    }
}

public sealed class DevToolsConfig
{
    public const string FileName = "overhauldevtools.json";

    public bool OpenOnStartup { get; set; }
}

internal static class StandaloneDevtoolsRuntime
{
    public static void EnsurePlayerAnimationBehaviors(ICoreClientAPI? api)
    {
        try
        {
            EntityPlayer? playerEntity = api?.World?.Player?.Entity;
            if (playerEntity == null) return;

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
        }
        catch (Exception exception)
        {
            LoggerUtil.Warn(api, typeof(StandaloneDevtoolsRuntime), $"Could not attach standalone animation preview behaviors: {exception}");
        }
    }
}
