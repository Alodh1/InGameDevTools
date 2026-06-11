using InGameDevTools.Colliders;
using InGameDevTools.Integration;
using InGameDevTools.Integration.Transpilers;
using InGameDevTools.MeleeSystems;
using InGameDevTools.Utils;
using ImGuiNET;
using Newtonsoft.Json.Linq;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;
using VSImGui;
using VSImGui.API;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager : IDisposable
{
    public static bool PlayAnimationsInThirdPerson { get; set; } = false;

    public DebugWindowManager(ICoreClientAPI api, ParticleEffectsManager particleEffectsManager, DevToolsConfig config)
    {
        _api = api;
        _particleEffectsManager = particleEffectsManager;
        _devToolsConfig = config;
        _devToolsConfig.Normalize();
        ApplyDevToolsConfigToRuntime();
        _imguiModSystem = api.ModLoader.GetModSystem<ImGuiModSystem>();
        if (_imguiModSystem == null)
        {
            LoggerUtil.Error(api, this, "VSImGui not found; dev tools UI disabled.");
        }
        else
        {
            _imguiModSystem.Draw += DrawEditor;
            _drawSubscribed = true;
        }
        _transformGizmoRenderer = new TransformGizmoRenderer(api, this);
        _imguiAnimationViewportRenderer = new ImGuiAnimationViewportRenderer(api);
        _detachedEditorCamera = new DetachedEditorCamera(api);
        _instance = this;

        _colliders.Clear();
    }

    public void Load(ICoreClientAPI api)
    {
        _behavior = api.World.Player.Entity.GetBehavior<FirstPersonAnimationsBehavior>();
        _sourceAssetIndex = BuildSourceAssetIndex(api);
        RegisterCollectibleTransformAttributes(api);
    }

    public void Dispose()
    {
        ClearLiveApplyState();
        RestoreWorldgenPreviewForEditorTeardown("devtools disposed");
        DisposeWorldgenPreviewRasterTexture();
        ModelDisposePreviewResources();
        FlushDevToolsConfigSave(force: true);
        if (!_drawSubscribed || _imguiModSystem == null) return;

        _imguiModSystem.Draw -= DrawEditor;
        _drawSubscribed = false;
    }

    public static void RegisterTransformByCode(ModelTransform transform, string code)
    {
        if (_instance == null) return;

        _instance.RegisterTransform(transform, code);
    }
    public void RegisterTransform(ModelTransform transform, string code)
    {
        _transforms[code] = new EditableTransform(transform);
    }
    private void RegisterTransform(ModelTransform transform, string code, Action<ModelTransform> apply, System.Func<ModelTransform, SourceSaveResult>? saveToSource = null)
    {
        _transforms[code] = new EditableTransform(transform, apply, saveToSource);
    }

    private sealed class EditableTransform
    {
        public EditableTransform(ModelTransform transform, Action<ModelTransform>? apply = null, System.Func<ModelTransform, SourceSaveResult>? saveToSource = null)
        {
            Transform = transform;
            Apply = apply;
            SaveToSource = saveToSource;
        }

        public ModelTransform Transform { get; }
        public Action<ModelTransform>? Apply { get; }
        public System.Func<ModelTransform, SourceSaveResult>? SaveToSource { get; }
    }

    private sealed class SourceSaveRequest
    {
        private readonly System.Func<string> _commit;

        public SourceSaveRequest(string sourceFile, string oldText, string newText, string successStatus, System.Func<string> commit)
        {
            SourceFile = sourceFile;
            OldText = oldText;
            NewText = newText;
            SuccessStatus = successStatus;
            _commit = commit;
        }

        public string SourceFile { get; }
        public string OldText { get; }
        public string NewText { get; }
        public string SuccessStatus { get; }

        public string Commit()
        {
            string result = _commit();
            return string.IsNullOrWhiteSpace(result) ? SuccessStatus : result;
        }
    }

    private sealed class SourceSaveResult
    {
        private SourceSaveResult(string status, SourceSaveRequest? request)
        {
            Status = status;
            Request = request;
        }

        public string Status { get; }
        public SourceSaveRequest? Request { get; }

        public static SourceSaveResult Fail(string status) => new(status, null);
        public static SourceSaveResult Preview(SourceSaveRequest request) => new("", request);
    }

    private void RegisterCollectibleTransformAttributes(ICoreClientAPI api)
    {
        foreach (Item item in api.World.Items)
        {
            if (item?.Code == null) continue;
            RegisterCollectibleTransformAttributes(item);
        }

        foreach (Block block in api.World.Blocks)
        {
            if (block?.Code == null) continue;
            RegisterCollectibleTransformAttributes(block);
        }
    }

    private void RegisterCollectibleTransformAttributes(CollectibleObject collectible)
    {
        foreach (string attributeCode in DirectTransformAttributeCodes)
        {
            RegisterDirectTransformAttribute(collectible, attributeCode);
        }

        foreach (string attributeCode in TypedTransformAttributeCodes)
        {
            RegisterTypedTransformAttribute(collectible, attributeCode);
        }
    }

    private void RegisterDirectTransformAttribute(CollectibleObject collectible, string attributeCode)
    {
        JsonObject? jsonTransform = collectible.Attributes?[attributeCode];
        if (jsonTransform?.Exists != true) return;

        ModelTransform? transform = jsonTransform.AsObject<ModelTransform>();
        if (transform == null) return;

        transform.EnsureDefaultValues();
        RegisterTransform(
            transform,
            $"{collectible.Code} / {attributeCode}",
            value => ApplyDirectTransformAttribute(collectible, attributeCode, value),
            value => TrySaveTransformToSource(collectible, attributeCode, value));
    }

    private void RegisterTypedTransformAttribute(CollectibleObject collectible, string attributeCode)
    {
        if (collectible.Attributes?[attributeCode].Token is not JObject transformsByType) return;

        foreach (JProperty property in transformsByType.Properties())
        {
            ModelTransform? transform = new JsonObject(property.Value).AsObject<ModelTransform>();
            if (transform == null) continue;

            string typeCode = property.Name;
            transform.EnsureDefaultValues();
            RegisterTransform(
                transform,
                $"{collectible.Code} / {attributeCode} / {typeCode}",
                value => ApplyTypedTransformAttribute(collectible, attributeCode, typeCode, value),
                value => TrySaveTransformToSource(collectible, attributeCode, value, typeCode));
        }
    }

    private static void ApplyDirectTransformAttribute(CollectibleObject collectible, string attributeCode, ModelTransform transform)
    {
        if (collectible.Attributes?.Token is not JObject source) return;

        JObject attributes = (JObject)source.DeepClone();
        attributes[attributeCode] = TransformToToken(transform);
        collectible.Attributes = new JsonObject(attributes);
    }

    private static void ApplyTypedTransformAttribute(CollectibleObject collectible, string attributeCode, string typeCode, ModelTransform transform)
    {
        if (collectible.Attributes?.Token is not JObject source) return;

        JObject attributes = (JObject)source.DeepClone();
        JObject transformsByType = attributes[attributeCode] as JObject ?? new JObject();
        transformsByType[typeCode] = TransformToToken(transform);
        attributes[attributeCode] = transformsByType;
        collectible.Attributes = new JsonObject(attributes);
    }

    private static JToken TransformToToken(ModelTransform transform)
    {
        return JToken.Parse(JsonUtil.ToPrettyString(transform));
    }

    private static SourceSaveResult TrySaveTransformToSource(CollectibleObject collectible, string attributeCode, ModelTransform transform, string? typedKey = null)
    {
        try
        {
            IAsset? sourceAsset = FindCollectibleSourceAsset(collectible);
            string domain = sourceAsset?.Location.Domain ?? collectible.Code?.Domain ?? "game";
            string kind = collectible is Block ? "blocktypes" : "itemtypes";
            string assetPath = sourceAsset?.Location.Path ?? $"{kind}/{EnsureJsonFilePath(collectible.Code?.Path ?? "unknown")}";
            string outputPath = GetToolAuthoredAssetPath("transforms", Path.Combine("assets", domain, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            string sourceText = ReadAssetText(sourceAsset);
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : sourceText;
            JObject json = TryParseJsonObject(oldText) ?? TryParseJsonObject(sourceText) ?? CreateCollectibleAuthoringDocument(collectible);
            JObject attributes = json["attributes"] as JObject ?? new JObject();

            if (typedKey == null)
            {
                attributes[attributeCode] = TransformToToken(transform);
            }
            else
            {
                JObject transformsByType = attributes[attributeCode] as JObject ?? new JObject();
                transformsByType[typedKey] = TransformToToken(transform);
                attributes[attributeCode] = transformsByType;
            }

            json["attributes"] = attributes;
            string newText = JsonUtil.ToPrettyString(json);
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved authored {attributeCode} to {outputPath}.",
                () => WriteAuthoredFile(outputPath, newText));
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            return SourceSaveResult.Fail($"Save failed for {collectible.Code}: {exception.Message}");
        }
    }

    private static IAsset? FindCollectibleSourceAsset(CollectibleObject collectible)
    {
        if (collectible.Code == null) return null;

        string domain = collectible.Code.Domain;
        string kind = collectible is Block ? "blocktypes" : "itemtypes";
        string path = collectible.Code.Path;

        if (_instance._sourceAssetIndex == null ||
            !_instance._sourceAssetIndex.CollectiblesByDomainKind.TryGetValue(BuildCollectibleSourceKey(domain, kind), out List<CollectibleSourceAsset>? candidates))
        {
            return null;
        }

        IAsset? bestAsset = null;
        int bestScore = -1;

        foreach (CollectibleSourceAsset candidate in candidates)
        {
            string code = candidate.Code;
            int score = -1;
            if (string.Equals(code, path, StringComparison.OrdinalIgnoreCase))
            {
                score = 10000 + code.Length;
            }
            else if (path.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase))
            {
                score = 1000 + code.Length;
            }
            else if (Path.GetFileNameWithoutExtension(candidate.Asset.Location.Path).Equals(code, StringComparison.OrdinalIgnoreCase) &&
                path.Contains(code, StringComparison.OrdinalIgnoreCase))
            {
                score = 100 + code.Length;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestAsset = candidate.Asset;
            }
        }

        return bestAsset;
    }

    private static SourceSaveResult TrySaveAnimationToSource(string animationCode, Animation animation)
    {
        try
        {
            AnimationsManager._instance.AnimationSources.TryGetValue(animationCode, out AnimationSource? source);
            IAsset? sourceAsset = source == null ? null : FindAnimationSourceAsset(source);
            string domain = sourceAsset?.Location.Domain ?? source?.Domain ?? GetDomainFromAssetCode(animationCode);
            string sourceKey = source?.Kind == AnimationSourceKind.ConfigAnimation
                ? source.SourceKey
                : animationCode;
            string assetPath = sourceAsset?.Location.Path ?? Path.Combine("config", "animations", EnsureJsonFilePath(SanitizePathSegment(sourceKey.Replace(':', '_'))));
            string outputPath = GetToolAuthoredAssetPath("animations", Path.Combine("assets", domain, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            string sourceText = ReadAssetText(sourceAsset);
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : sourceText;
            JObject json = TryParseJsonObject(oldText) ?? TryParseJsonObject(sourceText) ?? new JObject();

            json[sourceKey] = JToken.Parse(AnimationJson.FromAnimation(animation).ToString());
            string newText = JsonUtil.ToPrettyString(json);
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved authored animation {animationCode} to {outputPath}.",
                () => WriteAuthoredFile(outputPath, newText));
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            return SourceSaveResult.Fail($"Save failed for {animationCode}: {exception.Message}");
        }
    }

    private static IAsset? FindAnimationSourceAsset(AnimationSource source)
    {
        if (source.Kind != AnimationSourceKind.ConfigAnimation)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(source.AssetPath))
        {
            IAsset? asset = _instance._api.Assets.TryGet(new AssetLocation(source.Domain, source.AssetPath), true);
            if (asset != null) return asset;
        }

        if (_instance._sourceAssetIndex?.ConfigAnimationsByKey.TryGetValue(BuildAnimationSourceKey(source.Domain, source.SourceKey), out IAsset? indexedAsset) == true)
        {
            return indexedAsset;
        }

        return null;
    }

    /// <summary>
    /// Read-only origin for files the user saved through the tool's authored folders
    /// (VintagestoryData/InGameDevTools/&lt;type&gt;/assets/...). It lets those files take part
    /// in editor indexes as regular <see cref="IAsset"/> instances without the game loading them.
    /// </summary>
    private sealed class ToolAuthoredAssetOrigin(string originPath) : IAssetOrigin
    {
        public string OriginPath { get; } = originPath;

        public void LoadAsset(IAsset asset)
        {
            TryLoadAsset(asset);
        }

        public bool TryLoadAsset(IAsset asset)
        {
            try
            {
                string filePath = Path.Combine(OriginPath, "assets", asset.Location.Domain, asset.Location.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(filePath)) return false;

                asset.Data = File.ReadAllBytes(filePath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public List<IAsset> GetAssets(AssetCategory category, bool shouldLoad = true) => [];

        public List<IAsset> GetAssets(AssetLocation baseLocation, bool shouldLoad = true) => [];

        public bool IsAllowedToAffectGameplay() => false;
    }

    /// <summary>
    /// Enumerates user-authored JSON files under VintagestoryData/InGameDevTools/&lt;assetType&gt;/assets
    /// as in-memory assets so editor indexes can list them next to loaded game assets.
    /// Tool manifests, live-backups, and buffer files are excluded.
    /// </summary>
    internal static List<IAsset> CollectToolAuthoredAssets(string assetType, string? pathPrefix = null)
    {
        List<IAsset> assets = [];
        try
        {
            string root = GetToolAuthoredAssetRoot(assetType);
            string assetsRoot = Path.Combine(root, "assets");
            if (!Directory.Exists(assetsRoot)) return assets;

            ToolAuthoredAssetOrigin origin = new(root);
            foreach (string domainDirectory in Directory.GetDirectories(assetsRoot))
            {
                string domain = Path.GetFileName(domainDirectory).ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(domain)) continue;

                foreach (string filePath in Directory.GetFiles(domainDirectory, "*.json", SearchOption.AllDirectories))
                {
                    if (filePath.EndsWith(".ingamedevtools-manifest.json", StringComparison.OrdinalIgnoreCase)) continue;

                    string assetPath = Path.GetRelativePath(domainDirectory, filePath).Replace(Path.DirectorySeparatorChar, '/').ToLowerInvariant();
                    if (pathPrefix != null && !assetPath.StartsWith(pathPrefix, StringComparison.Ordinal)) continue;

                    assets.Add(new Vintagestory.Common.Asset(File.ReadAllBytes(filePath), new AssetLocation(domain, assetPath), origin));
                }
            }
        }
        catch
        {
            // Authored files are an optional overlay; never break an editor index over them.
        }

        return assets;
    }

    private static readonly string[] CollectibleSourceAuthoredAssetTypes = ["transforms", "loot-drops", "block-item-json", "animations"];

    private static IEnumerable<IAsset> EnumerateSourceIndexAssets(ICoreClientAPI api)
    {
        // Authored copies come first so they win the source matching score over the
        // identically coded game assets they were saved from.
        foreach (string assetType in CollectibleSourceAuthoredAssetTypes)
        {
            foreach (IAsset asset in CollectToolAuthoredAssets(assetType))
            {
                yield return asset;
            }
        }

        foreach (IAsset asset in api.Assets.AllAssets.Values)
        {
            yield return asset;
        }
    }

    private static SourceAssetIndex BuildSourceAssetIndex(ICoreClientAPI api)
    {
        SourceAssetIndex index = new();

        foreach (IAsset asset in EnumerateSourceIndexAssets(api))
        {
            if (asset?.Location == null) continue;

            string assetPath = asset.Location.Path.Replace('\\', '/');
            if (!assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            if (assetPath.StartsWith("blocktypes/", StringComparison.OrdinalIgnoreCase) ||
                assetPath.StartsWith("itemtypes/", StringComparison.OrdinalIgnoreCase))
            {
                AddCollectibleSourceAsset(index, asset, assetPath);
                continue;
            }

            if (assetPath.StartsWith("config/animations/", StringComparison.OrdinalIgnoreCase))
            {
                AddConfigAnimationSourceAsset(index, asset);
            }
        }

        return index;
    }

    private static void AddCollectibleSourceAsset(SourceAssetIndex index, IAsset asset, string assetPath)
    {
        JObject? json = TryParseJsonObject(ReadAssetText(asset));
        string? code = json?["code"]?.ToString();
        if (string.IsNullOrWhiteSpace(code)) return;
        if (code.Contains(':')) code = code[(code.IndexOf(':') + 1)..];

        string kind = assetPath.StartsWith("blocktypes/", StringComparison.OrdinalIgnoreCase) ? "blocktypes" : "itemtypes";
        string key = BuildCollectibleSourceKey(asset.Location.Domain, kind);
        if (!index.CollectiblesByDomainKind.TryGetValue(key, out List<CollectibleSourceAsset>? assets))
        {
            assets = [];
            index.CollectiblesByDomainKind[key] = assets;
        }

        assets.Add(new(asset, code));
    }

    private static void AddConfigAnimationSourceAsset(SourceAssetIndex index, IAsset asset)
    {
        if (TryParseJsonObject(ReadAssetText(asset)) is not JObject json) return;

        foreach (JProperty property in json.Properties())
        {
            index.ConfigAnimationsByKey[BuildAnimationSourceKey(asset.Location.Domain, property.Name)] = asset;
        }
    }

    private static string BuildCollectibleSourceKey(string domain, string kind) => $"{domain}:{kind}";

    private static string BuildAnimationSourceKey(string domain, string sourceKey) => $"{domain}:{sourceKey}";

    private sealed class SourceAssetIndex
    {
        public Dictionary<string, List<CollectibleSourceAsset>> CollectiblesByDomainKind { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IAsset> ConfigAnimationsByKey { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record CollectibleSourceAsset(IAsset Asset, string Code);

    private static JObject CreateCollectibleAuthoringDocument(CollectibleObject collectible)
    {
        JObject json = new();
        if (collectible.Code != null)
        {
            json["code"] = collectible.Code.ToString();
        }

        return json;
    }

    private static JObject? TryParseJsonObject(string text)
    {
        return DevToolsJson.TryParseObject(text);
    }

    private static string ReadAssetText(IAsset? asset)
    {
        if (asset == null) return "";

        try
        {
            return asset.ToText();
        }
        catch
        {
            return "";
        }
    }

    private static string WriteAuthoredFile(string outputPath, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, text);
        return "";
    }

    private static string EnsureJsonFilePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? normalized : normalized + ".json";
    }

    private static string GetDomainFromAssetCode(string code)
    {
        int separator = code.IndexOf(':');
        return separator > 0 ? code[..separator] : "game";
    }

    internal static string GetToolAuthoredAssetPath(string assetType, string relativePath)
    {
        string root = GetToolAuthoredAssetRoot(assetType);
        string normalizedRelativePath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        string outputPath = Path.GetFullPath(Path.Combine(root, normalizedRelativePath));

        if (!outputPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outputPath, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invalid InGameDevTools output path: {relativePath}");
        }

        return outputPath;
    }

    internal static string GetToolAuthoredAssetRoot(string assetType)
    {
        string root = Path.GetFullPath(Path.Combine(GetVintageStoryDataDirectory(), "InGameDevTools", SanitizePathSegment(assetType)));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string GetVintageStoryDataDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VintagestoryData");
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "misc";

        char[] invalid = Path.GetInvalidFileNameChars();
        char[] sanitized = value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray();
        return new string(sanitized);
    }

    private static string GetAnimationSourceText(string animationCode)
    {
        if (!AnimationsManager._instance.AnimationSources.TryGetValue(animationCode, out AnimationSource? source))
        {
            return "Source: not tracked";
        }

        string kind = source.Kind == AnimationSourceKind.ShapeAnimation ? "shape" : "config";
        return $"Source: {kind} / {source.DisplayPath}";
    }

    private static ModelTransform CreateDefaultTransform()
    {
        ModelTransform transform = new()
        {
            Translation = new Vec3f(),
            Rotation = new Vec3f(),
            Origin = new Vec3f(0.5f, 0.5f, 0.5f),
            ScaleXYZ = new Vec3f(1, 1, 1)
        };
        transform.EnsureDefaultValues();
        return transform;
    }

    public static void RegisterCollider(string item, string type, MeleeDamageType collider)
    {
        if (!_colliders.ContainsKey(item))
        {
            _colliders.Add(item, new());
        }

        _colliders[item].Add(type, (value => collider.RelativeCollider = value, () => collider.RelativeCollider));
    }
    public static void RegisterCollider(string item, string type, Action<LineSegmentCollider> setter, System.Func<LineSegmentCollider> getter)
    {
        if (!_colliders.ContainsKey(item))
        {
            _colliders.Add(item, new());
        }

        _colliders[item].Add(type, (setter, getter));
    }

    private bool _showAnimationEditor = false;
    private bool _devToolsCollapsed;
    private bool _selectVanillaAnimationsTabOnNextDraw = true;
    private int _selectedAnimationIndex = 0;
    private int _selectedAnimationIndexFiltered = 0;
    private FirstPersonAnimationsBehavior? _behavior;
    private readonly ICoreClientAPI _api;
    private string _itemAnimation = "";
    private string _animationKey = "";
    private readonly FieldInfo? _mainCameraInfo = typeof(ClientMain).GetField("MainCamera", BindingFlags.NonPublic | BindingFlags.Instance);
    private readonly FieldInfo? _cameraFov = typeof(Camera).GetField("Fov", BindingFlags.NonPublic | BindingFlags.Instance);
    private string _playerAnimationKey = "";
    private float _animationSpeed = 1;
    private ParticleEffectsManager _particleEffectsManager;
    private AnimationJson? _animationBuffer;
    internal static DebugWindowManager _instance = null!;
    private readonly ImGuiModSystem? _imguiModSystem;
    private bool _drawSubscribed;
    private SourceAssetIndex? _sourceAssetIndex;
    private bool _fovReflectionWarningLogged;
    private bool _showEditorDiagnostics;
    private readonly DevToolsEditorDiagnostics _devToolsDiagnostics = new("Dev tools");
    private readonly DevToolsEditorDiagnostics _animationDiagnostics = new("Animations");
    private readonly DevToolsEditorDiagnostics _transformDiagnostics = new("Transforms");

    private string _animationsFilter = "";
    private string _legacyTransformFilter = "";
    private string _collidersItemsFilter = "";
    private int _transformIndex = 0;
    private int _colliderItemIndex = 0;
    private int _colliderIndex = 0;
    private int _heldTransformAttributeIndex = 0;
    private string _transformSaveStatus = "";
    private readonly Dictionary<string, EditableTransform> _transforms = new();
    private static Dictionary<string, Dictionary<string, (Action<LineSegmentCollider> setter, System.Func<LineSegmentCollider> getter)>> _colliders = new();
    internal static LineSegmentCollider? _currentCollider = null;
    private enum DevToolsTab
    {
        Animations,
        RecipeEditor,
        Particles,
        Transforms,
        Models,
        ConfigLib,
        BlockItemJson,
        LootDrops,
        Worldgen,
        Patches,
        EntityAi,
        Settings
    }

    private static readonly bool BlockItemJsonEditorVisible = true;

    private readonly AnimationEditorHistory _animationHistory = new();
    private TransformGizmoRenderer? _transformGizmoRenderer;
    private ImGuiAnimationViewportRenderer? _imguiAnimationViewportRenderer;
    private DetachedEditorCamera? _detachedEditorCamera;
    private float _devToolsUiScale = 1f;
    private DevToolsTab _activeDevToolsTab = DevToolsTab.Animations;
    private float _imguiViewportYaw;
    private float _imguiViewportZoom = 1f;
    private float _imguiViewportPanX;
    private float _imguiViewportPanY;
    private ModelTransform? _activeGizmoTransform;
    private TransformGizmoContext _activeGizmoContext = TransformGizmoContext.Free;
    private BlockPos? _activeGizmoBlockPos;
    private Vec3d? _activeGizmoWorldCenter;
    private Action<ModelTransform>? _activeGizmoApply;
    private Action? _activeGizmoDragStarted;
    private Action? _activeGizmoDragEnded;
    private TransformGizmoAxes? _activeGizmoWorldAxes;
    private TransformGizmoAxes? _activeGizmoParentAxes;
    private TransformGizmoAxes? _activeGizmoTranslationAxes;
    private bool _animationHistoryExternalDragActive;
    private bool _animationHistoryExplicitEditThisFrame;
    private SourceSaveRequest? _pendingSourceSaveRequest;
    private Action<string>? _pendingSourceSaveStatus;
    private bool _openSourceSavePopup;
    private string _editorPlaybackAnimationCode = "";
    private bool _editorPlaybackPlaying;
    private bool _editorPlaybackPaused = true;
    private int _editorPlaybackLoopStartKeyframe;
    private int _editorPlaybackLoopEndKeyframe;
    private double _editorPlaybackTimeMs;
    private string _timelineRetimingAnimationCode = "";
    private TimelineRetimingKind _timelineRetimingKind = TimelineRetimingKind.None;
    private TimelineEventTrack _timelineRetimingEventTrack = TimelineEventTrack.Sound;
    private int _timelineRetimingIndex = -1;
    private TimelineRetimingKind _timelineSelectedKind = TimelineRetimingKind.Player;
    private TimelineEventTrack _timelineSelectedEventTrack = TimelineEventTrack.Sound;
    private float _timelineNudgeMs = 50;
    private static readonly string[] TimelineTrackNames = new[] { "Player", "Item", "Sound", "Particle", "Callback" };
    private static readonly MethodInfo? ProcessPlayerKeyFramesMethod = typeof(Animation).GetMethod("ProcessPlayerKeyFrames", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
    internal TransformGizmoMode GizmoMode { get; private set; } = TransformGizmoMode.Move;
    internal TransformGizmoSpace GizmoSpace { get; private set; } = TransformGizmoSpace.World;
    internal bool IncludeGizmoInIncrement { get; private set; } = true;
    internal float TransformGizmoIncrement { get; private set; } = 0.1f;
    internal static bool DebugPoseFreezeActive { get; private set; }
    private static readonly string[] DirectTransformAttributeCodes = new[]
    {
        "groundStorageTransform",
        "guiTransform",
        "groundTransform",
        "tpHandTransform",
        "tpOffHandTransform",
        "onDisplayTransform",
        "onshelfTransform",
        "toolrackTransform",
        "onTongTransform",
        "onMetalTongTransform",
        "inForgeTransform",
        "infirepitTransform",
        "inTrapTransform",
        "onAntlerMountTransform",
        "onmoldrackTransform",
        "onOmokTransform",
        "onscrollrackTransform",
        "inGenericDisplayTransform",
        "inWeaponrackTransform",
        "inWallmountTransform",
        "inPistolstandTransform",
        "inViceTransform",
        "inCrossbowWallmountTransform"
    };
    private static readonly string[] TypedTransformAttributeCodes = new[]
    {
        "onTongTransformByType",
        "onMetalTongTransformByType",
        "inForgeTransformByType"
    };

    private bool ToggleDevTools()
    {
        bool wasOpen = _showAnimationEditor;
        _showAnimationEditor = !_showAnimationEditor;
        if (_showAnimationEditor && !wasOpen)
        {
            _devToolsCollapsed = false;
            _selectVanillaAnimationsTabOnNextDraw = true;
        }

        if (!_showAnimationEditor)
        {
            OnDebugEditorClosed();
        }

        return true;
    }

    public bool OpenExternalDevTools()
    {
        _showAnimationEditor = true;
        _devToolsCollapsed = false;
        _selectVanillaAnimationsTabOnNextDraw = true;
        return true;
    }

    public bool ToggleExternalDevTools() => ToggleDevTools();

    public string GetExternalDevToolsStatus()
    {
        string state = _showAnimationEditor ? _devToolsCollapsed ? "collapsed" : "open" : "closed";
        return $"In-game devtools ImGui editor {state}.";
    }

    private CallbackGUIStatus DrawEditor(float deltaSeconds)
    {
        _currentCollider = null;
        ClearActiveTransformGizmo();
        _imguiAnimationViewportRenderer?.SetVisible(false);
        _vanillaPreviewRenderer?.SetVisible(false);

        if (!_showAnimationEditor)
        {
            OnDebugEditorClosed();
            return CallbackGUIStatus.Closed;
        }

        NVector2 displaySize = ImGui.GetIO().DisplaySize;
        if (displaySize.X <= 0 || displaySize.Y <= 0)
        {
            displaySize = new NVector2(_api.Render.FrameWidth, _api.Render.FrameHeight);
        }

        using DevToolsStyleScope styleScope = BeginDevToolsStyleScope();
        ImGuiWindowFlags windowFlags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings;
        if (_devToolsCollapsed)
        {
            ClearAiBehaviorLiveApplyState();
            RestoreWorldgenPreviewForEditorTeardown("devtools collapsed");
            DrawCollapsedDevToolsWindow(displaySize, windowFlags);
            _detachedEditorCamera?.Update(deltaSeconds, editorOpen: false);
            FlushDevToolsConfigSave(force: false);
            return _showAnimationEditor ? CallbackGUIStatus.DontGrabMouse : CallbackGUIStatus.Closed;
        }

        ImGui.SetNextWindowPos(NVector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(displaySize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.96f);
        windowFlags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        if (_vanillaViewportPoppedOut)
        {
            windowFlags |= ImGuiWindowFlags.NoBringToFrontOnFocus;
        }

        bool windowVisible = ImGui.Begin("Dev tools", ref _showAnimationEditor, windowFlags);
        if (windowVisible)
        {
            ImGui.SetWindowFontScale(1f);
            DrawDevToolsToolbar();
            HandleCommandPaletteShortcut();
            DrawCommandPalette();
            ImGui.SetWindowFontScale(_devToolsUiScale);
            if (!BlockItemJsonEditorVisible && _activeDevToolsTab == DevToolsTab.BlockItemJson)
            {
                _activeDevToolsTab = DevToolsTab.Animations;
            }

            if (ImGui.BeginTabBar($"##main_tab_bar"))
            {
                ImGuiTabItemFlags vanillaTabFlags = GetMainTabFlags(DevToolsTab.Animations);
                bool vanillaTabOpen = true;
                if (ImGui.BeginTabItem("Animations##tab", ref vanillaTabOpen, vanillaTabFlags))
                {
                    AcceptMainTabSelection(DevToolsTab.Animations);
                    DrawGuardedEditorTab("Animations", _animationDiagnostics, () => VanillaAnimationsTab(deltaSeconds));
                    ImGui.EndTabItem();
                }
                bool recipeTabOpen = true;
                if (ImGui.BeginTabItem("Recipe Editor##tab", ref recipeTabOpen, GetMainTabFlags(DevToolsTab.RecipeEditor)))
                {
                    AcceptMainTabSelection(DevToolsTab.RecipeEditor);
                    DrawGuardedEditorTab("Recipe Editor", _recipeEditor.Diagnostics, () => RecipeEditorTab(deltaSeconds, _showEditorDiagnostics));
                    ImGui.EndTabItem();
                }
                bool particlesTabOpen = true;
                if (ImGui.BeginTabItem("Particles##tab", ref particlesTabOpen, GetMainTabFlags(DevToolsTab.Particles)))
                {
                    AcceptMainTabSelection(DevToolsTab.Particles);
                    DrawGuardedEditorTab("Particles", _devToolsDiagnostics, () => _particleEffectsManager.DrawEditor("devtools-particles", deltaSeconds, _devToolsUiScale, _liveApplyManager, _showEditorDiagnostics));
                    ImGui.EndTabItem();
                }
                bool transformsTabOpen = true;
                if (ImGui.BeginTabItem("Transforms##tab", ref transformsTabOpen, GetMainTabFlags(DevToolsTab.Transforms)))
                {
                    AcceptMainTabSelection(DevToolsTab.Transforms);
                    DrawGuardedEditorTab("Transforms", _transformDiagnostics, () => TransformsEditorTab(deltaSeconds));
                    ImGui.EndTabItem();
                }
                bool modelsTabOpen = true;
                if (ImGui.BeginTabItem("Models##tab", ref modelsTabOpen, GetMainTabFlags(DevToolsTab.Models)))
                {
                    AcceptMainTabSelection(DevToolsTab.Models);
                    DrawGuardedEditorTab("Models", _modelDiagnostics, () => ModelEditorTab(deltaSeconds, _showEditorDiagnostics));
                    ImGui.EndTabItem();
                }
                bool configLibTabOpen = true;
                if (ImGui.BeginTabItem("ConfigLib##tab", ref configLibTabOpen, GetMainTabFlags(DevToolsTab.ConfigLib)))
                {
                    AcceptMainTabSelection(DevToolsTab.ConfigLib);
                    DrawGuardedEditorTab("ConfigLib", _configLibDiagnostics, () => ConfigLibGeneratorTab(deltaSeconds, _showEditorDiagnostics));
                    ImGui.EndTabItem();
                }
                bool blockItemJsonTabOpen = true;
                if (BlockItemJsonEditorVisible && ImGui.BeginTabItem("Block/Item JSON##tab", ref blockItemJsonTabOpen, GetMainTabFlags(DevToolsTab.BlockItemJson)))
                {
                    AcceptMainTabSelection(DevToolsTab.BlockItemJson);
                    DrawGuardedEditorTab("Block/Item JSON", _blockItemJsonDiagnostics, () => BlockItemJsonEditorTab(deltaSeconds, _showEditorDiagnostics));
                    ImGui.EndTabItem();
                }
                bool lootDropsTabOpen = true;
                if (ImGui.BeginTabItem("Loot/Drops##tab", ref lootDropsTabOpen, GetMainTabFlags(DevToolsTab.LootDrops)))
                {
                    AcceptMainTabSelection(DevToolsTab.LootDrops);
                    DrawGuardedEditorTab("Loot/Drops", _lootDropDiagnostics, () => LootDropEditorTab(deltaSeconds, _showEditorDiagnostics));
                    ImGui.EndTabItem();
                }
                bool worldgenTabOpen = true;
                if (ImGui.BeginTabItem("Worldgen##tab", ref worldgenTabOpen, GetMainTabFlags(DevToolsTab.Worldgen)))
                {
                    AcceptMainTabSelection(DevToolsTab.Worldgen);
                    DrawGuardedEditorTab("Worldgen", _worldgenDiagnostics, () => WorldgenEditorTab(deltaSeconds, _showEditorDiagnostics));
                    ImGui.EndTabItem();
                }
                bool patchesTabOpen = true;
                if (ImGui.BeginTabItem("Patches##tab", ref patchesTabOpen, GetMainTabFlags(DevToolsTab.Patches)))
                {
                    AcceptMainTabSelection(DevToolsTab.Patches);
                    DrawGuardedEditorTab("Patches", _patchCreatorDiagnostics, () => PatchCreatorTab(deltaSeconds, _showEditorDiagnostics));
                    ImGui.EndTabItem();
                }
                bool entityAiTabOpen = true;
                if (ImGui.BeginTabItem("Entity AI##tab", ref entityAiTabOpen, GetMainTabFlags(DevToolsTab.EntityAi)))
                {
                    AcceptMainTabSelection(DevToolsTab.EntityAi);
                    DrawGuardedEditorTab("Entity AI", _aiBehaviorDiagnostics, () => AiBehaviorEditorTab(deltaSeconds, _showEditorDiagnostics));
                    ImGui.EndTabItem();
                }
                bool settingsTabOpen = true;
                if (ImGui.BeginTabItem("Settings##tab", ref settingsTabOpen, GetMainTabFlags(DevToolsTab.Settings)))
                {
                    AcceptMainTabSelection(DevToolsTab.Settings);
                    DrawGuardedEditorTab("Settings", _devToolsDiagnostics, () => SettingsTab(deltaSeconds));
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
            if (_activeDevToolsTab != DevToolsTab.Worldgen)
            {
                RestoreWorldgenPreviewForEditorTeardown("left Worldgen tab");
            }
            if (_activeDevToolsTab != DevToolsTab.EntityAi)
            {
                ClearAiBehaviorLiveApplyState();
            }
            ImGui.SetWindowFontScale(1f);
            DrawSourceSavePopup();
        }
        ImGui.End();
        if (!_showAnimationEditor)
        {
            RestoreWorldgenPreviewForEditorTeardown("devtools window closed");
        }

        DrawVanillaPoppedOutViewport();

        _detachedEditorCamera?.Update(deltaSeconds, _showAnimationEditor);
        FlushDevToolsConfigSave(force: false);

        return _showAnimationEditor ? CallbackGUIStatus.GrabMouse : CallbackGUIStatus.Closed;
    }

    private void DrawGuardedEditorTab(string editorName, DevToolsEditorDiagnostics diagnostics, Action draw)
    {
        try
        {
            draw();
        }
        catch (Exception exception)
        {
            _api.Logger.Error("[InGameDevTools] {0} tab draw failed: {1}", editorName, exception);
            diagnostics.Exception($"{editorName} tab draw failed", exception);
            ImGui.TextColored(new NVector4(1f, 0.42f, 0.34f, 1f), $"{editorName} hit an error.");
            ImGui.TextWrapped("The editor caught the error so the devtools window can stay open.");
            diagnostics.Draw($"{editorName.ToLowerInvariant().Replace(' ', '-')}-tab-guard", _showEditorDiagnostics);
        }
    }

    private void DrawDevToolsToolbar()
    {
        if (ImGui.Button("Collapse editor##devtools-collapse"))
        {
            _devToolsCollapsed = true;
        }
        ImGui.SameLine();

        ImGui.SetNextItemWidth(120f);
        if (ImGui.SliderFloat("UI scale##devtools-global-scale", ref _devToolsUiScale, 0.75f, 1.75f, "%.2f"))
        {
            _devToolsUiScale = Math.Clamp(_devToolsUiScale, 0.75f, 1.75f);
            _devToolsConfig.UiScale = _devToolsUiScale;
            QueueDevToolsConfigSave("UI scale updated.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset layout##devtools-reset-layout"))
        {
            ResetDevToolsLayout();
        }

        ImGui.SameLine();
        DrawCommandPaletteButton();

        if (_activeDevToolsTab == DevToolsTab.Animations)
        {
            ImGui.SameLine();
            ImGui.Checkbox("Pop out viewport##devtools-global-popout", ref _vanillaViewportPoppedOut);
        }

        ImGui.SameLine();
        bool autoApply = _liveApplyManager.AutoApply;
        if (ImGui.Checkbox("Runtime apply##devtools-live-auto", ref autoApply))
        {
            bool enabled = autoApply && !_liveApplyManager.AutoApply;
            _liveApplyManager.AutoApply = autoApply;
            _devToolsConfig.AutoRuntimeApply = autoApply;
            QueueDevToolsConfigSave("Runtime apply setting updated.");
            if (enabled)
            {
                ApplyDirtyLiveChangesForActiveTab(force: true);
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Automatically applies editor changes to loaded runtime objects for this session.");
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("Live backups##devtools-live-backups", ref _liveApplyManager.WriteBackups))
        {
            _devToolsConfig.WriteLiveBackups = _liveApplyManager.WriteBackups;
            QueueDevToolsConfigSave("Live backup setting updated.");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Write original runtime state copies before the first live patch.");
        }
        ImGui.SameLine();
        if (ImGui.Button("Revert live##devtools-live-revert-all"))
        {
            _liveApplyManager.RevertAll();
            ClearLiveApplyState();
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("Diagnostics##devtools-diagnostics", ref _showEditorDiagnostics))
        {
            _devToolsConfig.ShowDiagnostics = _showEditorDiagnostics;
            QueueDevToolsConfigSave("Diagnostics setting updated.");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Show detailed editor diagnostic messages for caught exceptions and skipped previews.");
        }

        ImGui.Separator();
    }

    private void DrawCollapsedDevToolsWindow(NVector2 displaySize, ImGuiWindowFlags baseFlags)
    {
        NVector2 windowPos = new(Math.Min(12f, Math.Max(0f, displaySize.X - 220f)), 12f);
        ImGui.SetNextWindowPos(windowPos, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.82f);
        ImGuiWindowFlags windowFlags = baseFlags |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.Begin("Dev tools", ref _showAnimationEditor, windowFlags))
        {
            if (ImGui.Button("Expand editor##devtools-expand"))
            {
                _devToolsCollapsed = false;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(GetCollapsedDevToolsStatusText());
        }
        ImGui.End();
    }

    private string GetCollapsedDevToolsStatusText()
    {
        return _liveApplyManager.AutoApply ? "Runtime apply on" : "Runtime apply off";
    }

    private void ApplyDirtyLiveChangesForActiveTab(bool force = false)
    {
        if (!_liveApplyManager.AutoApply) return;

        switch (_activeDevToolsTab)
        {
            case DevToolsTab.Animations:
                ApplyAllDirtyVanillaLive(force);
                break;
            case DevToolsTab.RecipeEditor:
                _recipeEditor.ApplyDirtyRecipeLive(_liveApplyManager, force);
                break;
            case DevToolsTab.Particles:
                _particleEffectsManager.ApplyDirtyParticleLive(_liveApplyManager, force);
                break;
            case DevToolsTab.Transforms:
                ApplySelectedTransformLive(force);
                break;
            case DevToolsTab.Models:
                ApplyModelRuntimeForActiveTab(force);
                break;
            case DevToolsTab.ConfigLib:
                _liveApplyManager.LastStatus = "ConfigLib generator writes authored files; it has no runtime apply target.";
                break;
            case DevToolsTab.BlockItemJson:
                if (BlockItemJsonEditorVisible)
                {
                    ApplyBlockItemJsonRuntime(force);
                }
                break;
            case DevToolsTab.LootDrops:
                ApplyLootDropRuntime(force);
                break;
            case DevToolsTab.Worldgen:
                ApplyWorldgenRuntime(force);
                break;
            case DevToolsTab.Patches:
                ApplyPatchCreatorRuntime(force);
                break;
            case DevToolsTab.EntityAi:
                _liveApplyManager.LastStatus = "Entity AI source edits save authored files; live AI tuning is not enabled yet.";
                break;
            case DevToolsTab.Settings:
                _liveApplyManager.LastStatus = "Settings apply directly to the editor UI.";
                break;
        }
    }

    private void ClearLiveApplyState()
    {
        ClearVanillaLiveApplyState();
        _recipeEditor.ClearRecipeLiveApplyState();
        _particleEffectsManager.ClearParticleLiveApplyState();
        ClearTransformLiveApplyState();
        ClearModelLiveApplyState();
        ClearBlockItemJsonLiveApplyState();
        ClearLootDropLiveApplyState();
        ClearWorldgenLiveApplyState();
        ClearPatchCreatorLiveApplyState();
        ClearAiBehaviorLiveApplyState();
    }

    private void ResetDevToolsLayout()
    {
        _devToolsUiScale = 1f;
        _devToolsConfig.UiScale = _devToolsUiScale;
        QueueDevToolsConfigSave("Layout reset.");
        ResetVanillaLayout();
        _recipeEditor.ResetLayout();
        _particleEffectsManager.ResetLayout();
        ResetTransformsLayout();
        ResetModelEditorLayout();
        ResetConfigLibGeneratorLayout();
        ResetBlockItemJsonLayout();
        ResetLootDropLayout();
        ResetWorldgenLayout();
        ResetPatchCreatorLayout();
        ResetAiBehaviorLayout();
    }

    private void CollidersTab()
    {
        ImGui.InputText("Items filter##colliders", ref _collidersItemsFilter, 200);
        VSImGui.EditorsUtils.FilterElements(_collidersItemsFilter, _colliders.Keys, out IEnumerable<string> filteredItems, out _);
        string[] filteredItemsArray = filteredItems.ToArray();
        if (filteredItemsArray.Length == 0)
        {
            _colliderItemIndex = 0;
            return;
        }

        _colliderItemIndex = Math.Clamp(_colliderItemIndex, 0, filteredItemsArray.Length - 1);
        ImGui.ListBox("Items##colliders", ref _colliderItemIndex, filteredItemsArray, filteredItemsArray.Length);
        _colliderItemIndex = Math.Clamp(_colliderItemIndex, 0, filteredItemsArray.Length - 1);
        string selectedItem = filteredItemsArray[_colliderItemIndex];

        Dictionary<string, (Action<LineSegmentCollider> setter, Func<LineSegmentCollider> getter)> selectedColliders = _colliders[selectedItem];
        string[] collidersTypes = selectedColliders.Select(entry => entry.Key).ToArray();

        if (collidersTypes.Length <= 0)
        {
            _colliderIndex = 0;
            return;
        }

        _colliderIndex = Math.Clamp(_colliderIndex, 0, collidersTypes.Length - 1);
        ImGui.ListBox("Colliders##colliders", ref _colliderIndex, collidersTypes, collidersTypes.Length);
        _colliderIndex = Math.Clamp(_colliderIndex, 0, collidersTypes.Length - 1);

        (Action<LineSegmentCollider> setter, Func<LineSegmentCollider> getter) = selectedColliders[collidersTypes[_colliderIndex]];
        System.Numerics.Vector3 position = getter().Position.ToSystem();
        System.Numerics.Vector3 direction = getter().Direction.ToSystem();

        float sliderSpeed = ImGui.IsKeyPressed(ImGuiKey.LeftShift) ? 0.01f : 0.1f;

        ImGui.DragFloat3("Position##colliders", ref position, sliderSpeed);
        ImGui.DragFloat3("Direction##colliders", ref direction, sliderSpeed);

        _currentCollider = new(position.ToOpenTK(), direction.ToOpenTK());

        setter(_currentCollider.Value);

        System.Numerics.Vector3 head = position + direction;

        string json = $"[{position.X}, {position.Y}, {position.Z}, {head.X}, {head.Y}, {head.Z}]";
        if (ImGui.Button("To clipboard##colliders"))
        {
            ImGui.SetClipboardText(json);
        }
        ImGui.SameLine();
        ImGui.Text($"JSON: {json}");
    }

    private void QueueSourceSave(SourceSaveResult result, Action<string> setStatus)
    {
        if (result.Request == null)
        {
            setStatus(result.Status);
            return;
        }

        _pendingSourceSaveRequest = result.Request;
        _pendingSourceSaveStatus = setStatus;
        _openSourceSavePopup = true;
    }

    private void DrawSourceSavePopup()
    {
        const string popupId = "Save authored file preview";
        if (_openSourceSavePopup)
        {
            ImGui.OpenPopup(popupId);
            _openSourceSavePopup = false;
        }

        bool open = true;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(1100, 650), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(popupId, ref open))
        {
            return;
        }

        SourceSaveRequest? request = _pendingSourceSaveRequest;
        if (request == null)
        {
            ImGui.TextUnformatted("No authored file save is pending.");
            if (ImGui.Button("Close"))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Save to {request.SourceFile}?");
        ImGui.Separator();

        string[] oldLines = SplitSourceLines(request.OldText);
        string[] newLines = SplitSourceLines(request.NewText);
        float paneWidth = Math.Max(320f, (ImGui.GetContentRegionAvail().X - 12f) * 0.5f);
        System.Numerics.Vector2 paneSize = new(paneWidth, 500f);

        DrawSourceSavePane("Current file##source-save-old", oldLines, newLines, paneSize);
        ImGui.SameLine();
        DrawSourceSavePane("New file##source-save-new", newLines, oldLines, paneSize);

        if (ImGui.Button("Save##source-save-commit"))
        {
            string status;
            try
            {
                status = request.Commit();
            }
            catch (Exception exception)
            {
                status = $"Save failed for {request.SourceFile}: {exception.Message}";
            }

            _pendingSourceSaveStatus?.Invoke(status);
            ClearSourceSavePopup();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##source-save-cancel"))
        {
            _pendingSourceSaveStatus?.Invoke($"Save canceled for {request.SourceFile}.");
            ClearSourceSavePopup();
            ImGui.CloseCurrentPopup();
        }

        if (!open)
        {
            ClearSourceSavePopup();
        }

        ImGui.EndPopup();
    }

    private static void DrawSourceSavePane(string title, string[] lines, string[] otherLines, System.Numerics.Vector2 size)
    {
        ImGui.BeginChild(title, size, true, ImGuiWindowFlags.HorizontalScrollbar);
        ImGui.TextUnformatted(title.Split("##", StringSplitOptions.None)[0]);
        ImGui.Separator();

        int count = Math.Max(lines.Length, otherLines.Length);
        for (int i = 0; i < count; i++)
        {
            string line = i < lines.Length ? lines[i] : "";
            bool changed = i >= lines.Length || i >= otherLines.Length || line != otherLines[i];
            if (changed)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.9f, 0.25f, 1f));
            }

            ImGui.TextUnformatted($"{i + 1,5}: {line}");

            if (changed)
            {
                ImGui.PopStyleColor();
            }
        }

        ImGui.EndChild();
    }

    private static string[] SplitSourceLines(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }

    private void ClearSourceSavePopup()
    {
        _pendingSourceSaveRequest = null;
        _pendingSourceSaveStatus = null;
        _openSourceSavePopup = false;
    }

    private void EditFov()
    {
        if (_mainCameraInfo == null || _cameraFov == null)
        {
            if (!_fovReflectionWarningLogged)
            {
                LoggerUtil.Warn(_api, this, "FOV editor disabled; camera reflection fields were not found.");
                _fovReflectionWarningLogged = true;
            }

            return;
        }

        ClientMain? client = _api.World as ClientMain;
        if (client == null) return;

        PlayerCamera? camera = (PlayerCamera?)_mainCameraInfo.GetValue(client);
        if (camera == null) return;

        float? fovField = (float?)_cameraFov.GetValue(camera);
        if (fovField == null) return;

        float fovMultiplier = PlayerRenderingPatches.HandsFovMultiplier;
        ImGui.SliderFloat("FOV", ref fovMultiplier, 0.5f, 1.5f);
        PlayerRenderingPatches.HandsFovMultiplier = fovMultiplier;
        _cameraFov.SetValue(camera, ClientSettings.FieldOfView * GameMath.DEG2RAD * fovMultiplier);

        ImGui.Text($"FOV: {ClientSettings.FieldOfView * fovMultiplier}");
    }

    private void AnimationsTab(float deltaSeconds)
    {
        string[] codes = AnimationsManager._instance.Animations.Keys.ToArray();
        if (codes.Length == 0)
        {
            ImGui.TextDisabled("No animations loaded.");
            if (ImGui.CollapsingHeader("Add animation", ImGuiTreeNodeFlags.DefaultOpen))
            {
                CreateAnimationGui();
            }
            return;
        }

        NVector2 available = ImGui.GetContentRegionAvail();
        float spacingX = ImGui.GetStyle().ItemSpacing.X;
        float spacingY = ImGui.GetStyle().ItemSpacing.Y;
        float bottomHeight = Math.Clamp(available.Y * 0.27f, 230f, 360f);
        float topHeight = Math.Max(360f, available.Y - bottomHeight - spacingY);
        float leftWidth = Math.Clamp(available.X * 0.22f, 280f, 430f);
        float rightWidth = Math.Clamp(available.X * 0.28f, 360f, 540f);
        float centerWidth = available.X - leftWidth - rightWidth - spacingX * 2f;
        if (centerWidth < 520f)
        {
            leftWidth = Math.Clamp(available.X * 0.20f, 240f, 330f);
            rightWidth = Math.Clamp(available.X * 0.25f, 300f, 420f);
            centerWidth = Math.Max(360f, available.X - leftWidth - rightWidth - spacingX * 2f);
        }

        ImGui.BeginChild("##animation-left-panel", new NVector2(leftWidth, topHeight), true);
        ImGui.SeparatorText("Animations");
        ImGui.InputTextWithHint("##animations-filter", "supports wildcards", ref _animationsFilter, 200);
        EditorsUtils.FilterElements(_animationsFilter, AnimationsManager._instance.Animations.Keys, out IEnumerable<string> filteredEnumerable, out _);
        string[] filtered = filteredEnumerable.ToArray();
        if (filtered.Length > 0)
        {
            if (_selectedAnimationIndexFiltered >= filtered.Length) _selectedAnimationIndexFiltered = filtered.Length - 1;
            if (_selectedAnimationIndexFiltered < 0) _selectedAnimationIndexFiltered = 0;
            ImGui.ListBox("##animations-list", ref _selectedAnimationIndexFiltered, filtered, filtered.Length);
            _selectedAnimationIndex = Array.IndexOf(codes, filtered[_selectedAnimationIndexFiltered]);
        }
        else
        {
            ImGui.TextDisabled("No matching animations.");
        }

        if (_selectedAnimationIndex < 0 || _selectedAnimationIndex >= codes.Length) _selectedAnimationIndex = 0;

        ImGui.SeparatorText("Buffer");
        if (ImGui.Button("Save to buffer", new NVector2(-1, 0)))
        {
            _animationBuffer = AnimationJson.FromAnimation(AnimationsManager._instance.Animations[codes[_selectedAnimationIndex]]);
        }

        if (ImGui.Button("Load from buffer", new NVector2(-1, 0)) && _animationBuffer != null)
        {
            string animationCode = codes[_selectedAnimationIndex];
            Animation currentAnimation = AnimationsManager._instance.Animations[animationCode];
            _animationHistory.BeginEdit(animationCode, currentAnimation, "Load from buffer");
            AnimationsManager._instance.Animations[animationCode] = _animationBuffer.ToAnimation();
            _animationHistory.CommitEdit(animationCode, AnimationsManager._instance.Animations[animationCode]);
        }

        if (ImGui.Button("Save buffer to file", new NVector2(-1, 0)))
        {
            if (_animationBuffer == null)
            {
                _transformSaveStatus = "No animation buffer to save.";
            }
            else
            {
                string outputPath = GetToolAuthoredAssetPath("animations", Path.Combine("buffers", "co-animation-export.json"));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, _animationBuffer.ToString());
                _transformSaveStatus = $"Saved animation buffer to {outputPath}.";
            }
        }

        if (ImGui.Button("Load buffer from file", new NVector2(-1, 0)))
        {
            string inputPath = GetToolAuthoredAssetPath("animations", Path.Combine("buffers", "co-animation-export.json"));
            if (File.Exists(inputPath))
            {
                _animationBuffer = JsonObject.FromJson(File.ReadAllText(inputPath)).AsObject<AnimationJson>();
                _transformSaveStatus = $"Loaded animation buffer from {inputPath}.";
            }
            else
            {
                _animationBuffer = _api.LoadModConfig<AnimationJson>("co-animation-export.json");
                _transformSaveStatus = _animationBuffer == null
                    ? $"Animation buffer file not found at {inputPath}."
                    : "Loaded animation buffer from legacy mod config.";
            }
        }

        ImGui.SeparatorText("Preview options");
        if (ImGui.Button("Toggle rendering offset", new NVector2(-1, 0)))
        {
            PlayerRenderingPatches.FpHandsOffset = PlayerRenderingPatches.FpHandsOffset != PlayerRenderingPatches.DefaultFpHandsOffset
                ? PlayerRenderingPatches.DefaultFpHandsOffset
                : 0;
        }

        bool tpAnimations = PlayAnimationsInThirdPerson;
        ImGui.Checkbox("Third person animations", ref tpAnimations);
        PlayAnimationsInThirdPerson = tpAnimations;

        bool disableRuntimeAnimations = AnimationPatches.DisableAllAnimations;
        if (ImGui.Checkbox("Disable runtime animations", ref disableRuntimeAnimations))
        {
            AnimationPatches.DisableAllAnimations = disableRuntimeAnimations;
        }

        bool disableThirdPersonRuntimeAnimations = AnimationPatches.DisableThirdPersonAnimations;
        if (ImGui.Checkbox("Disable third person runtime animations", ref disableThirdPersonRuntimeAnimations))
        {
            AnimationPatches.DisableThirdPersonAnimations = disableThirdPersonRuntimeAnimations;
        }

        if (ImGui.CollapsingHeader("Add animation"))
        {
            CreateAnimationGui();
        }
        ImGui.EndChild();

        ImGui.SameLine();

        string selectedAnimationCode = codes[_selectedAnimationIndex];
        Animation selectedAnimation = AnimationsManager._instance.Animations[selectedAnimationCode];

        ImGui.BeginChild("##animation-center-panel", new NVector2(centerWidth, topHeight), true);
        ImGui.TextWrapped(selectedAnimationCode);
        DrawAnimationPlaybackControls(selectedAnimationCode, selectedAnimation, deltaSeconds);
        NVector2 centerAvailable = ImGui.GetContentRegionAvail();
        DrawImGuiAnimationViewport(selectedAnimationCode, new NVector2(centerAvailable.X, Math.Max(260f, centerAvailable.Y)));
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##animation-right-panel", new NVector2(rightWidth, topHeight), true);
        ImGui.SeparatorText("Tools");
        ImGui.TextWrapped(GetAnimationSourceText(selectedAnimationCode));
        DrawAnimationHistoryControls(selectedAnimationCode);
        if (ImGui.Button("Export to clipboard") && AnimationsManager._instance.Animations.Count > 0)
        {
            ImGui.SetClipboardText(AnimationsManager._instance.Animations[selectedAnimationCode].ToString());
        }
        ImGui.SameLine();
        if (ImGui.Button("Save authored file##animation") && AnimationsManager._instance.Animations.Count > 0)
        {
            QueueSourceSave(TrySaveAnimationToSource(selectedAnimationCode, AnimationsManager._instance.Animations[selectedAnimationCode]), status => _transformSaveStatus = status);
        }
        if (!string.IsNullOrEmpty(_transformSaveStatus))
        {
            ImGui.TextWrapped(_transformSaveStatus);
        }

        ImGui.SetNextItemWidth(200);
        ImGui.SliderFloat("Animation speed", ref _animationSpeed, 0.1f, 2);

        DrawAnimationValidationPanel(selectedAnimationCode, selectedAnimation);

        selectedAnimation = AnimationsManager._instance.Animations[selectedAnimationCode];
        _animationHistoryExplicitEditThisFrame = false;
        Animation beforeEdit = selectedAnimation.Clone();
        string beforeEditSerialized = AnimationEditorHistory.Serialize(selectedAnimation);
        selectedAnimation.Edit(selectedAnimationCode);
        DrawRigPoseEditor(selectedAnimationCode, selectedAnimation);
        TrackAnimationEditorChanges(selectedAnimationCode, beforeEdit, beforeEditSerialized, selectedAnimation, "Editor edit");
        ImGui.EndChild();

        ImGui.BeginChild("##animation-bottom-panel", new NVector2(available.X, bottomHeight), true);
        selectedAnimation = AnimationsManager._instance.Animations[selectedAnimationCode];
        DrawAnimationTimeline(selectedAnimationCode, selectedAnimation);
        ImGui.EndChild();

        selectedAnimation = AnimationsManager._instance.Animations[selectedAnimationCode];
        if (_showAnimationEditor)
        {
            SetEditorFrameOverride(selectedAnimation.StillPlayerFrame(selectedAnimation._playerFrameIndex, selectedAnimation._frameProgress));
        }
        else
        {
            SetEditorFrameOverride(null);
        }
    }

    private void DrawAnimationTimeline(string animationCode, Animation animation)
    {
        if (animation.PlayerKeyFrames.Count == 0) return;

        ImGui.SeparatorText("Timeline");
        ImGui.TextDisabled("Click timeline to scrub. Click markers to select frames; drag markers to retime.");

        double durationMs = GetEditorAnimationDurationMs(animation);
        double playerDurationMs = GetEditorPlayerDurationMs(animation);
        float scrubMs = (float)Math.Clamp(GetEditorFrameTimeMs(animation), 0, durationMs);
        ImGui.SetNextItemWidth(320);
        if (ImGui.SliderFloat($"Time ms##editor-timeline-{animationCode}", ref scrubMs, 0, (float)durationMs, "%.0f"))
        {
            ScrubEditorTimeline(animation, scrubMs);
        }

        const float labelWidth = 76;
        const float rowHeight = 24;
        const int trackCount = 5;
        float width = Math.Max(420, ImGui.GetContentRegionAvail().X);
        float height = rowHeight * trackCount + 18;

        ImGui.InvisibleButton($"##editor-timeline-canvas-{animationCode}", new NVector2(width, height));
        NVector2 canvasMin = ImGui.GetItemRectMin();
        NVector2 canvasMax = ImGui.GetItemRectMax();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0.07f, 0.065f, 0.055f, 0.78f));
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.50f, 0.42f, 1f));
        uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.86f, 0.82f, 0.72f, 1f));
        uint line = ImGui.ColorConvertFloat4ToU32(new NVector4(0.36f, 0.34f, 0.30f, 1f));
        uint scrub = ImGui.ColorConvertFloat4ToU32(new NVector4(1.0f, 0.82f, 0.28f, 1f));

        drawList.AddRectFilled(canvasMin, canvasMax, background, 4);
        drawList.AddRect(canvasMin, canvasMax, border, 4);

        float trackStart = canvasMin.X + labelWidth;
        float trackEnd = canvasMax.X - 8;
        float trackWidth = Math.Max(1, trackEnd - trackStart);
        float TimeToX(double timeMs) => trackStart + (float)(Math.Clamp(timeMs, 0, durationMs) / durationMs * trackWidth);
        double XToTime(float x) => Math.Clamp((x - trackStart) / trackWidth, 0, 1) * durationMs;
        float RowY(int row) => canvasMin.Y + 16 + row * rowHeight;

        TimelineMarker[] playerMarkers = BuildPlayerTimelineMarkers(animation);
        TimelineMarker[] itemMarkers = BuildFractionTimelineMarkers(animation.ItemKeyFrames.Count, index => animation.ItemAnimationStart.TotalMilliseconds + (animation.ItemAnimationEnd - animation.ItemAnimationStart).TotalMilliseconds * animation.ItemKeyFrames[index].DurationFraction, "I", TimelineItemColor(), animation._itemFrameIndex);
        TimelineMarker[] soundMarkers = BuildFractionTimelineMarkers(animation.SoundFrames.Count, index => playerDurationMs * animation.SoundFrames[index].DurationFraction, "S", TimelineSoundColor(), animation._soundsFrameIndex);
        TimelineMarker[] particleMarkers = BuildFractionTimelineMarkers(animation.ParticlesFrames.Count, index => playerDurationMs * animation.ParticlesFrames[index].DurationFraction, "P", TimelineParticleColor(), animation._particlesFrameIndex);
        TimelineMarker[] callbackMarkers = BuildFractionTimelineMarkers(animation.CallbackFrames.Count, index => playerDurationMs * animation.CallbackFrames[index].DurationFraction, "C", TimelineCallbackColor(), animation._callbackFrameIndex);

        DrawTimelineTrack(drawList, "Player", 0, playerMarkers, canvasMin.X + 8, trackStart, trackEnd, RowY(0), text, line, durationMs, IsTimelineTrackSelected(TimelineRetimingKind.Player));
        DrawTimelineTrack(drawList, "Item", 1, itemMarkers, canvasMin.X + 8, trackStart, trackEnd, RowY(1), text, line, durationMs, IsTimelineTrackSelected(TimelineRetimingKind.Item));
        DrawTimelineTrack(drawList, "Sound", 2, soundMarkers, canvasMin.X + 8, trackStart, trackEnd, RowY(2), text, line, durationMs, IsTimelineTrackSelected(TimelineRetimingKind.Event, TimelineEventTrack.Sound));
        DrawTimelineTrack(drawList, "Particle", 3, particleMarkers, canvasMin.X + 8, trackStart, trackEnd, RowY(3), text, line, durationMs, IsTimelineTrackSelected(TimelineRetimingKind.Event, TimelineEventTrack.Particle));
        DrawTimelineTrack(drawList, "Callback", 4, callbackMarkers, canvasMin.X + 8, trackStart, trackEnd, RowY(4), text, line, durationMs, IsTimelineTrackSelected(TimelineRetimingKind.Event, TimelineEventTrack.Callback));

        float scrubX = TimeToX(GetEditorFrameTimeMs(animation));
        drawList.AddLine(new NVector2(scrubX, canvasMin.Y + 6), new NVector2(scrubX, canvasMax.Y - 6), scrub, 2);

        if (ImGui.IsItemHovered())
        {
            NVector2 mouse = ImGui.GetIO().MousePos;
            double hoverMs = XToTime(mouse.X);
            ImGui.SetTooltip($"{hoverMs:0} ms");
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            NVector2 mouse = ImGui.GetIO().MousePos;
            if (TryFindTimelineMarker(playerMarkers, mouse, trackStart, trackWidth, RowY(0), durationMs, out int markerIndex))
            {
                SelectEditorTimelinePlayerKeyframe(animation, markerIndex);
                BeginTimelineRetiming(animationCode, animation, TimelineRetimingKind.Player, markerIndex);
            }
            else if (TryFindTimelineMarker(itemMarkers, mouse, trackStart, trackWidth, RowY(1), durationMs, out markerIndex))
            {
                SelectEditorTimelineItemFrame(animation, markerIndex, itemMarkers[markerIndex].TimeMs);
                BeginTimelineRetiming(animationCode, animation, TimelineRetimingKind.Item, markerIndex);
            }
            else if (TryFindTimelineMarker(soundMarkers, mouse, trackStart, trackWidth, RowY(2), durationMs, out markerIndex))
            {
                SelectEditorTimelineEventFrame(animation, TimelineEventTrack.Sound, markerIndex, soundMarkers[markerIndex].TimeMs);
                BeginTimelineRetiming(animationCode, animation, TimelineRetimingKind.Event, markerIndex, TimelineEventTrack.Sound);
            }
            else if (TryFindTimelineMarker(particleMarkers, mouse, trackStart, trackWidth, RowY(3), durationMs, out markerIndex))
            {
                SelectEditorTimelineEventFrame(animation, TimelineEventTrack.Particle, markerIndex, particleMarkers[markerIndex].TimeMs);
                BeginTimelineRetiming(animationCode, animation, TimelineRetimingKind.Event, markerIndex, TimelineEventTrack.Particle);
            }
            else if (TryFindTimelineMarker(callbackMarkers, mouse, trackStart, trackWidth, RowY(4), durationMs, out markerIndex))
            {
                SelectEditorTimelineEventFrame(animation, TimelineEventTrack.Callback, markerIndex, callbackMarkers[markerIndex].TimeMs);
                BeginTimelineRetiming(animationCode, animation, TimelineRetimingKind.Event, markerIndex, TimelineEventTrack.Callback);
            }
            else
            {
                if (TryGetTimelineRow(mouse, RowY(0), rowHeight, trackCount, out int row))
                {
                    SelectTimelineTrack(row);
                }
                ScrubEditorTimeline(animation, XToTime(mouse.X));
            }
        }

        if (_timelineRetimingKind != TimelineRetimingKind.None && _timelineRetimingAnimationCode == animationCode)
        {
            NVector2 mouse = ImGui.GetIO().MousePos;
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                RetimingTimelineMarker(animation, XToTime(mouse.X));
            }
            else
            {
                EndTimelineRetiming(animationCode, animation);
            }
        }

        DrawTimelineActions(animationCode, animation);
    }

    private void DrawAnimationPlaybackControls(string animationCode, Animation animation, float deltaSeconds)
    {
        if (animation.PlayerKeyFrames.Count == 0) return;

        EnsureEditorPlaybackState(animationCode, animation);

        if (ImGui.Button("Play##editor-playback"))
        {
            StartEditorPlayback(animationCode, animation);
        }

        ImGui.SameLine();
        if (!_editorPlaybackPlaying) ImGui.BeginDisabled();
        if (ImGui.Button((_editorPlaybackPaused ? "Resume" : "Pause") + "##editor-playback"))
        {
            _editorPlaybackPaused = !_editorPlaybackPaused;
        }
        if (!_editorPlaybackPlaying) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Step keyframe <##editor-playback"))
        {
            StepEditorKeyframe(animation, -1);
        }

        ImGui.SameLine();
        if (ImGui.Button("Step keyframe >##editor-playback"))
        {
            StepEditorKeyframe(animation, 1);
        }

        ImGui.SameLine();
        if (ImGui.Button("Step frame <##editor-playback"))
        {
            StepEditorFrame(animation, -1);
        }

        ImGui.SameLine();
        if (ImGui.Button("Step frame >##editor-playback"))
        {
            StepEditorFrame(animation, 1);
        }

        int maxKeyframe = animation.PlayerKeyFrames.Count - 1;
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderInt("Loop start keyframe##editor-playback", ref _editorPlaybackLoopStartKeyframe, 0, maxKeyframe))
        {
            ClampEditorPlaybackRange(animation);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        if (ImGui.SliderInt("Loop end keyframe##editor-playback", ref _editorPlaybackLoopEndKeyframe, 0, maxKeyframe))
        {
            ClampEditorPlaybackRange(animation);
        }

        if (_editorPlaybackPlaying && !_editorPlaybackPaused && _editorPlaybackAnimationCode == animationCode)
        {
            AdvanceEditorPlayback(animation, deltaSeconds);
        }
    }

    private void DrawImGuiAnimationViewport(string animationCode, NVector2 requestedSize = default)
    {
        NVector2 available = requestedSize.X > 0 && requestedSize.Y > 0
            ? requestedSize
            : ImGui.GetContentRegionAvail();
        float width = Math.Max(420f, available.X);
        float height = requestedSize.X > 0 && requestedSize.Y > 0
            ? Math.Max(240f, available.Y)
            : Math.Clamp(ImGui.GetIO().DisplaySize.Y * 0.42f, 280f, Math.Max(280f, available.Y * 0.58f));
        NVector2 size = new(width, height);

        ImGui.InvisibleButton($"##animation-viewport-{animationCode}", size);
        NVector2 min = ImGui.GetItemRectMin();
        NVector2 max = ImGui.GetItemRectMax();
        bool hovered = ImGui.IsItemHovered();

        if (hovered)
        {
            NVector2 delta = ImGui.GetIO().MouseDelta;
            bool pan = ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
                (ImGui.IsMouseDragging(ImGuiMouseButton.Right) &&
                    (ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift)));
            if (pan)
            {
                _imguiViewportPanX = Math.Clamp(_imguiViewportPanX + delta.X, -size.X, size.X);
                _imguiViewportPanY = Math.Clamp(_imguiViewportPanY + delta.Y, -size.Y, size.Y);
            }
            else if (ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                _imguiViewportYaw += delta.X * 0.01f;
            }

            float wheel = ImGui.GetIO().MouseWheel;
            if (Math.Abs(wheel) > 0.001f)
            {
                _imguiViewportZoom = Math.Clamp(_imguiViewportZoom + wheel * 0.06f, 0.55f, 1.85f);
            }
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint background = ImGui.ColorConvertFloat4ToU32(DevToolsViewportBackground.FillColor);
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 1f));
        uint text = ImGui.ColorConvertFloat4ToU32(DevToolsViewportBackground.TextColor);
        drawList.AddRectFilled(min, max, background, 4f);
        drawList.AddRect(min, max, border, 4f);
        drawList.AddText(new NVector2(min.X + 12f, min.Y + 10f), text, $"Preview: {animationCode}");

        _imguiAnimationViewportRenderer?.SetViewport(
            min.X,
            min.Y,
            Math.Max(1f, max.X - min.X),
            Math.Max(1f, max.Y - min.Y),
            _imguiViewportYaw,
            _imguiViewportZoom,
            _imguiViewportPanX,
            _imguiViewportPanY);
    }

    private void DrawAnimationValidationPanel(string animationCode, Animation animation)
    {
        List<AnimationValidationMessage> messages = BuildAnimationValidationMessages(animation);
        int errorCount = messages.Count(message => message.Severity == AnimationValidationSeverity.Error);
        int warningCount = messages.Count(message => message.Severity == AnimationValidationSeverity.Warning);
        string label = messages.Count == 0
            ? "Validation: OK##animation-validation"
            : $"Validation: {errorCount} errors, {warningCount} warnings##animation-validation";

        if (!ImGui.CollapsingHeader(label)) return;

        if (messages.Count == 0)
        {
            ImGui.TextColored(new NVector4(0.45f, 1.0f, 0.45f, 1f), "No validation issues found.");
            return;
        }

        if (ImGui.Button("Copy validation report##animation-validation"))
        {
            ImGui.SetClipboardText(string.Join(Environment.NewLine, messages.Select(message => $"{message.Severity}: {message.Text}")));
        }

        ImGui.BeginChild($"##animation-validation-list-{animationCode}", new NVector2(0, Math.Min(220, 24 + messages.Count * 22)), true);
        foreach (AnimationValidationMessage message in messages)
        {
            NVector4 color = message.Severity == AnimationValidationSeverity.Error
                ? new NVector4(1.0f, 0.38f, 0.30f, 1f)
                : new NVector4(1.0f, 0.82f, 0.35f, 1f);
            ImGui.TextColored(color, $"{message.Severity}: {message.Text}");
        }
        ImGui.EndChild();
    }

    private static List<AnimationValidationMessage> BuildAnimationValidationMessages(Animation animation)
    {
        List<AnimationValidationMessage> messages = new();
        ValidatePlayerTimeline(animation, messages);
        ValidateItemTimeline(animation, messages);
        ValidateEventTimeline("Sound", animation.SoundFrames.Count, index => animation.SoundFrames[index].DurationFraction, index => IsBlankSoundFrame(animation.SoundFrames[index]), messages);
        ValidateEventTimeline("Particle", animation.ParticlesFrames.Count, index => animation.ParticlesFrames[index].DurationFraction, index => string.IsNullOrWhiteSpace(animation.ParticlesFrames[index].Code), messages);
        ValidateEventTimeline("Callback", animation.CallbackFrames.Count, index => animation.CallbackFrames[index].DurationFraction, index => string.IsNullOrWhiteSpace(animation.CallbackFrames[index].Code), messages);
        return messages;
    }

    private static void ValidatePlayerTimeline(Animation animation, List<AnimationValidationMessage> messages)
    {
        if (animation.PlayerKeyFrames.Count == 0)
        {
            messages.Add(AnimationValidationMessage.Error("Player timeline has no keyframes."));
            return;
        }

        double previousMs = double.NegativeInfinity;
        for (int index = 0; index < animation.PlayerKeyFrames.Count; index++)
        {
            double timeMs = animation.PlayerKeyFrames[index].Time.TotalMilliseconds;
            if (timeMs < 0)
            {
                messages.Add(AnimationValidationMessage.Error($"Player keyframe {index} has negative time {timeMs:0} ms."));
            }

            if (index > 0 && timeMs <= previousMs)
            {
                messages.Add(AnimationValidationMessage.Error($"Player keyframe {index} time {timeMs:0} ms is not after keyframe {index - 1} time {previousMs:0} ms."));
            }

            previousMs = timeMs;
        }

        if (animation.PlayerKeyFrames[0].Time.TotalMilliseconds > 0.0001)
        {
            messages.Add(AnimationValidationMessage.Warning($"First player keyframe starts at {animation.PlayerKeyFrames[0].Time.TotalMilliseconds:0} ms; playback begins from an implicit zero pose."));
        }
    }

    private static void ValidateItemTimeline(Animation animation, List<AnimationValidationMessage> messages)
    {
        if (animation.ItemKeyFrames.Count == 0) return;

        double playerDurationMs = GetEditorPlayerDurationMs(animation);
        double startMs = animation.ItemAnimationStart.TotalMilliseconds;
        double endMs = animation.ItemAnimationEnd.TotalMilliseconds;
        double spanMs = endMs - startMs;

        if (startMs < 0)
        {
            messages.Add(AnimationValidationMessage.Error($"Item animation starts before zero at {startMs:0} ms."));
        }

        if (endMs < startMs)
        {
            messages.Add(AnimationValidationMessage.Error($"Item animation end {endMs:0} ms is before start {startMs:0} ms."));
        }

        if (playerDurationMs > 0.0001 && endMs > playerDurationMs)
        {
            messages.Add(AnimationValidationMessage.Warning($"Item animation ends at {endMs:0} ms after player duration {playerDurationMs:0} ms."));
        }

        if (spanMs <= 0.0001)
        {
            messages.Add(AnimationValidationMessage.Error("Item timeline has keyframes but no positive animation duration."));
            return;
        }

        ValidateFractionSequence("Item", animation.ItemKeyFrames.Count, index => animation.ItemKeyFrames[index].DurationFraction, messages);
    }

    private static void ValidateEventTimeline(string trackName, int count, System.Func<int, float> fractionProvider, System.Func<int, bool> isBlank, List<AnimationValidationMessage> messages)
    {
        if (count == 0) return;

        ValidateFractionSequence(trackName, count, fractionProvider, messages);
        for (int index = 0; index < count; index++)
        {
            if (isBlank(index))
            {
                messages.Add(AnimationValidationMessage.Warning($"{trackName} marker {index} has blank content."));
            }
        }
    }

    private static void ValidateFractionSequence(string trackName, int count, System.Func<int, float> fractionProvider, List<AnimationValidationMessage> messages)
    {
        float previous = float.NegativeInfinity;
        for (int index = 0; index < count; index++)
        {
            float fraction = fractionProvider(index);
            if (float.IsNaN(fraction) || float.IsInfinity(fraction))
            {
                messages.Add(AnimationValidationMessage.Error($"{trackName} marker {index} has invalid fraction {fraction}."));
                continue;
            }

            if (fraction < 0 || fraction > 1)
            {
                messages.Add(AnimationValidationMessage.Error($"{trackName} marker {index} fraction {fraction:0.###} is outside 0..1."));
            }

            if (index > 0 && fraction < previous)
            {
                messages.Add(AnimationValidationMessage.Error($"{trackName} marker {index} fraction {fraction:0.###} is before marker {index - 1} fraction {previous:0.###}."));
            }
            else if (index > 0 && Math.Abs(fraction - previous) < 0.000001f)
            {
                messages.Add(AnimationValidationMessage.Warning($"{trackName} marker {index} has the same time as marker {index - 1}."));
            }

            previous = fraction;
        }
    }

    private static bool IsBlankSoundFrame(SoundFrame frame)
    {
        return frame.Code == null || frame.Code.Length == 0 || frame.Code.All(string.IsNullOrWhiteSpace);
    }

    private void EnsureEditorPlaybackState(string animationCode, Animation animation)
    {
        if (_editorPlaybackAnimationCode != animationCode)
        {
            _editorPlaybackAnimationCode = animationCode;
            _editorPlaybackLoopStartKeyframe = 0;
            _editorPlaybackLoopEndKeyframe = Math.Max(0, animation.PlayerKeyFrames.Count - 1);
            _editorPlaybackPlaying = false;
            _editorPlaybackPaused = true;
            _editorPlaybackTimeMs = GetEditorFrameTimeMs(animation);
            return;
        }

        ClampEditorPlaybackRange(animation);
        _editorPlaybackTimeMs = Math.Clamp(_editorPlaybackTimeMs, GetEditorLoopStartMs(animation), GetEditorLoopEndMs(animation));
    }

    private void StartEditorPlayback(string animationCode, Animation animation)
    {
        _editorPlaybackAnimationCode = animationCode;
        ClampEditorPlaybackRange(animation);

        double startMs = GetEditorLoopStartMs(animation);
        double endMs = GetEditorLoopEndMs(animation);
        _editorPlaybackTimeMs = GetEditorFrameTimeMs(animation);
        if (_editorPlaybackTimeMs < startMs || _editorPlaybackTimeMs >= endMs)
        {
            _editorPlaybackTimeMs = startMs;
            ApplyEditorPlaybackTime(animation, _editorPlaybackTimeMs);
        }

        _editorPlaybackPlaying = true;
        _editorPlaybackPaused = false;
    }

    private void StepEditorKeyframe(Animation animation, int direction)
    {
        if (animation.PlayerKeyFrames.Count == 0) return;

        _editorPlaybackPlaying = false;
        _editorPlaybackPaused = true;
        animation._playerFrameIndex = Math.Clamp(animation._playerFrameIndex + direction, 0, animation.PlayerKeyFrames.Count - 1);
        animation._frameProgress = 0;
        _editorPlaybackTimeMs = GetEditorFrameTimeMs(animation);
    }

    private void StepEditorFrame(Animation animation, int direction)
    {
        if (animation.PlayerKeyFrames.Count == 0) return;

        _editorPlaybackPlaying = false;
        _editorPlaybackPaused = true;
        double startMs = GetEditorLoopStartMs(animation);
        double endMs = GetEditorLoopEndMs(animation);
        _editorPlaybackTimeMs = Math.Clamp(GetEditorFrameTimeMs(animation) + direction * 50.0, startMs, endMs);
        ApplyEditorPlaybackTime(animation, _editorPlaybackTimeMs);
    }

    private void AdvanceEditorPlayback(Animation animation, float deltaSeconds)
    {
        double startMs = GetEditorLoopStartMs(animation);
        double endMs = GetEditorLoopEndMs(animation);
        if (endMs <= startMs)
        {
            _editorPlaybackTimeMs = startMs;
            ApplyEditorPlaybackTime(animation, _editorPlaybackTimeMs);
            return;
        }

        _editorPlaybackTimeMs += Math.Max(0, deltaSeconds) * 1000.0 * Math.Max(0.001f, _animationSpeed);
        if (_editorPlaybackTimeMs > endMs)
        {
            double duration = endMs - startMs;
            _editorPlaybackTimeMs = startMs + ((_editorPlaybackTimeMs - startMs) % duration);
        }

        ApplyEditorPlaybackTime(animation, _editorPlaybackTimeMs);
    }

    private void ClampEditorPlaybackRange(Animation animation)
    {
        int maxKeyframe = Math.Max(0, animation.PlayerKeyFrames.Count - 1);
        _editorPlaybackLoopStartKeyframe = Math.Clamp(_editorPlaybackLoopStartKeyframe, 0, maxKeyframe);
        _editorPlaybackLoopEndKeyframe = Math.Clamp(_editorPlaybackLoopEndKeyframe, 0, maxKeyframe);
        if (_editorPlaybackLoopStartKeyframe > _editorPlaybackLoopEndKeyframe)
        {
            _editorPlaybackLoopEndKeyframe = _editorPlaybackLoopStartKeyframe;
        }
    }

    private double GetEditorLoopStartMs(Animation animation)
    {
        if (animation.PlayerKeyFrames.Count == 0) return 0;
        return animation.PlayerKeyFrames[Math.Clamp(_editorPlaybackLoopStartKeyframe, 0, animation.PlayerKeyFrames.Count - 1)].Time.TotalMilliseconds;
    }

    private double GetEditorLoopEndMs(Animation animation)
    {
        if (animation.PlayerKeyFrames.Count == 0) return 0;
        return animation.PlayerKeyFrames[Math.Clamp(_editorPlaybackLoopEndKeyframe, 0, animation.PlayerKeyFrames.Count - 1)].Time.TotalMilliseconds;
    }

    private static double GetEditorFrameTimeMs(Animation animation)
    {
        if (animation.PlayerKeyFrames.Count == 0) return 0;

        int index = Math.Clamp(animation._playerFrameIndex, 0, animation.PlayerKeyFrames.Count - 1);
        double endMs = animation.PlayerKeyFrames[index].Time.TotalMilliseconds;
        double startMs = index == 0 ? 0 : animation.PlayerKeyFrames[index - 1].Time.TotalMilliseconds;
        double segmentMs = Math.Max(0, endMs - startMs);
        return startMs + segmentMs * Math.Clamp(animation._frameProgress, 0, 1);
    }

    private static void ApplyEditorPlaybackTime(Animation animation, double timeMs)
    {
        if (animation.PlayerKeyFrames.Count == 0) return;

        int lastIndex = animation.PlayerKeyFrames.Count - 1;
        if (timeMs >= animation.PlayerKeyFrames[lastIndex].Time.TotalMilliseconds)
        {
            animation._playerFrameIndex = lastIndex;
            animation._frameProgress = 1;
            return;
        }

        int targetIndex = 0;
        while (targetIndex < lastIndex && animation.PlayerKeyFrames[targetIndex].Time.TotalMilliseconds < timeMs)
        {
            targetIndex++;
        }

        double endMs = animation.PlayerKeyFrames[targetIndex].Time.TotalMilliseconds;
        double startMs = targetIndex == 0 ? 0 : animation.PlayerKeyFrames[targetIndex - 1].Time.TotalMilliseconds;
        double durationMs = endMs - startMs;

        animation._playerFrameIndex = targetIndex;
        animation._frameProgress = durationMs <= 0.0001 ? 1 : (float)Math.Clamp((timeMs - startMs) / durationMs, 0, 1);
    }

    private void ScrubEditorTimeline(Animation animation, double timeMs)
    {
        _editorPlaybackPlaying = false;
        _editorPlaybackPaused = true;
        ApplyEditorPlaybackTime(animation, timeMs);
        _editorPlaybackTimeMs = GetEditorFrameTimeMs(animation);
    }

    private void SelectEditorTimelinePlayerKeyframe(Animation animation, int index)
    {
        if (animation.PlayerKeyFrames.Count == 0) return;

        _editorPlaybackPlaying = false;
        _editorPlaybackPaused = true;
        animation._playerFrameIndex = Math.Clamp(index, 0, animation.PlayerKeyFrames.Count - 1);
        animation._frameProgress = 0;
        _timelineSelectedKind = TimelineRetimingKind.Player;
        _editorPlaybackTimeMs = GetEditorFrameTimeMs(animation);
    }

    private void SelectEditorTimelineItemFrame(Animation animation, int index, double timeMs)
    {
        if (animation.ItemKeyFrames.Count == 0) return;

        animation._itemFrameIndex = Math.Clamp(index, 0, animation.ItemKeyFrames.Count - 1);
        _timelineSelectedKind = TimelineRetimingKind.Item;
        ScrubEditorTimeline(animation, timeMs);
    }

    private void SelectEditorTimelineEventFrame(Animation animation, TimelineEventTrack track, int index, double timeMs)
    {
        switch (track)
        {
            case TimelineEventTrack.Sound when animation.SoundFrames.Count > 0:
                animation._soundsFrameIndex = Math.Clamp(index, 0, animation.SoundFrames.Count - 1);
                break;
            case TimelineEventTrack.Particle when animation.ParticlesFrames.Count > 0:
                animation._particlesFrameIndex = Math.Clamp(index, 0, animation.ParticlesFrames.Count - 1);
                break;
            case TimelineEventTrack.Callback when animation.CallbackFrames.Count > 0:
                animation._callbackFrameIndex = Math.Clamp(index, 0, animation.CallbackFrames.Count - 1);
                break;
            default:
                return;
        }

        _timelineSelectedKind = TimelineRetimingKind.Event;
        _timelineSelectedEventTrack = track;
        ScrubEditorTimeline(animation, timeMs);
    }

    private bool TryFindTimelineMarker(TimelineMarker[] markers, NVector2 mouse, float trackStart, float trackWidth, float rowY, double durationMs, out int markerIndex)
    {
        markerIndex = -1;
        const float hitRadius = 8;
        if (Math.Abs(mouse.Y - rowY) > hitRadius + 2) return false;

        float bestDistance = float.MaxValue;
        for (int index = 0; index < markers.Length; index++)
        {
            float x = trackStart + (float)(Math.Clamp(markers[index].TimeMs, 0, durationMs) / durationMs * trackWidth);
            float distance = Math.Abs(mouse.X - x);
            if (distance >= bestDistance || distance > hitRadius) continue;

            bestDistance = distance;
            markerIndex = index;
        }

        return markerIndex >= 0;
    }

    private static bool TryGetTimelineRow(NVector2 mouse, float firstRowY, float rowHeight, int trackCount, out int row)
    {
        row = -1;
        const float rowHitHalfHeight = 10;
        for (int index = 0; index < trackCount; index++)
        {
            if (Math.Abs(mouse.Y - (firstRowY + index * rowHeight)) > rowHitHalfHeight) continue;

            row = index;
            return true;
        }

        return false;
    }

    private void BeginTimelineRetiming(string animationCode, Animation animation, TimelineRetimingKind kind, int index, TimelineEventTrack eventTrack = TimelineEventTrack.Sound)
    {
        if (_timelineRetimingKind == kind && _timelineRetimingAnimationCode == animationCode && _timelineRetimingIndex == index && _timelineRetimingEventTrack == eventTrack) return;

        _timelineRetimingAnimationCode = animationCode;
        _timelineRetimingKind = kind;
        _timelineRetimingEventTrack = eventTrack;
        _timelineRetimingIndex = index;
        _animationHistory.BeginEdit(animationCode, animation, GetTimelineRetimingLabel(kind, eventTrack, index));
    }

    private void EndTimelineRetiming(string animationCode, Animation animation)
    {
        if (_timelineRetimingKind == TimelineRetimingKind.None || _timelineRetimingAnimationCode != animationCode) return;

        _animationHistory.CommitEdit(animationCode, animation);
        _timelineRetimingAnimationCode = "";
        _timelineRetimingKind = TimelineRetimingKind.None;
        _timelineRetimingEventTrack = TimelineEventTrack.Sound;
        _timelineRetimingIndex = -1;
    }

    private static string GetTimelineRetimingLabel(TimelineRetimingKind kind, TimelineEventTrack eventTrack, int index)
    {
        return kind switch
        {
            TimelineRetimingKind.Player => $"Retiming player keyframe {index}",
            TimelineRetimingKind.Item => $"Retiming item keyframe {index}",
            TimelineRetimingKind.Event => $"Retiming {eventTrack.ToString().ToLowerInvariant()} marker {index}",
            _ => "Retiming timeline marker"
        };
    }

    private void RetimingTimelineMarker(Animation animation, double requestedTimeMs)
    {
        switch (_timelineRetimingKind)
        {
            case TimelineRetimingKind.Player:
                RetimingTimelinePlayerKeyframe(animation, _timelineRetimingIndex, requestedTimeMs);
                break;
            case TimelineRetimingKind.Item:
                RetimingTimelineItemKeyframe(animation, _timelineRetimingIndex, requestedTimeMs);
                break;
            case TimelineRetimingKind.Event:
                RetimingTimelineEventFrame(animation, _timelineRetimingEventTrack, _timelineRetimingIndex, requestedTimeMs);
                break;
        }
    }

    private void RetimingTimelinePlayerKeyframe(Animation animation, int keyframeIndex, double requestedTimeMs)
    {
        if (keyframeIndex < 0 || keyframeIndex >= animation.PlayerKeyFrames.Count) return;

        double minMs = keyframeIndex == 0 ? 0 : animation.PlayerKeyFrames[keyframeIndex - 1].Time.TotalMilliseconds + 1;
        double maxMs = keyframeIndex == animation.PlayerKeyFrames.Count - 1
            ? Math.Max(minMs, animation.PlayerKeyFrames[keyframeIndex].Time.TotalMilliseconds)
            : animation.PlayerKeyFrames[keyframeIndex + 1].Time.TotalMilliseconds - 1;
        double timeMs = Math.Clamp(requestedTimeMs, minMs, Math.Max(minMs, maxMs));

        PLayerKeyFrame frame = animation.PlayerKeyFrames[keyframeIndex];
        animation.PlayerKeyFrames[keyframeIndex] = new PLayerKeyFrame(frame.Frame, TimeSpan.FromMilliseconds(timeMs), frame.EasingFunction, frame.EasingType, frame.FrameProgressRange);
        animation._playerFrameIndex = keyframeIndex;
        animation._frameProgress = 0;
        animation._playerFrameEdited = true;
        ProcessPlayerKeyFramesForEditor(animation);
        _editorPlaybackPlaying = false;
        _editorPlaybackPaused = true;
        _editorPlaybackTimeMs = GetEditorFrameTimeMs(animation);
    }

    private void DrawTimelineActions(string animationCode, Animation animation)
    {
        ImGui.SeparatorText("Timeline actions");
        string selection = GetTimelineSelectionLabel(animation);
        ImGui.TextDisabled(selection);

        int trackIndex = GetTimelineTrackIndex();
        ImGui.SetNextItemWidth(150);
        if (ImGui.Combo("Target track##timeline-actions", ref trackIndex, TimelineTrackNames, TimelineTrackNames.Length))
        {
            SelectTimelineTrack(trackIndex);
        }

        bool canRetiming = CanRetimingTimelineSelection(animation);
        if (!canRetiming) ImGui.BeginDisabled();
        float selectedTimeMs = canRetiming ? (float)GetSelectedTimelineMarkerTimeMs(animation) : 0;
        ImGui.SetNextItemWidth(150);
        if (ImGui.InputFloat("Selected time ms##timeline-actions", ref selectedTimeMs, 1, Math.Max(1, _timelineNudgeMs), "%.0f"))
        {
            BeginTimelineActionEdit(animationCode, animation, $"Retiming {selection}");
            RetimingTimelineSelection(animation, selectedTimeMs);
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            CommitPendingAnimationEdit(animationCode);
        }
        if (!canRetiming) ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputFloat("Nudge ms##timeline-actions", ref _timelineNudgeMs, 1, 50, "%.0f"))
        {
            _timelineNudgeMs = Math.Max(1, _timelineNudgeMs);
        }

        bool canInsert = CanInsertTimelineSelection(animation);
        bool canDuplicate = CanDuplicateTimelineSelection(animation);
        bool canDelete = CanDeleteTimelineSelection(animation);

        if (!canRetiming) ImGui.BeginDisabled();
        if (ImGui.Button("- nudge##timeline-actions"))
        {
            _animationHistory.BeginEdit(animationCode, animation, $"Nudge {selection}");
            RetimingTimelineSelection(animation, GetSelectedTimelineMarkerTimeMs(animation) - Math.Max(1, _timelineNudgeMs));
            _animationHistory.CommitEdit(animationCode, animation);
        }
        if (!canRetiming) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!canRetiming) ImGui.BeginDisabled();
        if (ImGui.Button("+ nudge##timeline-actions"))
        {
            _animationHistory.BeginEdit(animationCode, animation, $"Nudge {selection}");
            RetimingTimelineSelection(animation, GetSelectedTimelineMarkerTimeMs(animation) + Math.Max(1, _timelineNudgeMs));
            _animationHistory.CommitEdit(animationCode, animation);
        }
        if (!canRetiming) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!canRetiming) ImGui.BeginDisabled();
        if (ImGui.Button("Move to playhead##timeline-actions"))
        {
            _animationHistory.BeginEdit(animationCode, animation, $"Move {selection} to playhead");
            RetimingTimelineSelection(animation, GetEditorFrameTimeMs(animation));
            _animationHistory.CommitEdit(animationCode, animation);
        }
        if (!canRetiming) ImGui.EndDisabled();

        if (!canInsert) ImGui.BeginDisabled();
        if (ImGui.Button("Insert marker at time##timeline-actions"))
        {
            _animationHistory.BeginEdit(animationCode, animation, $"Insert {selection}");
            InsertTimelineSelection(animation);
            _animationHistory.CommitEdit(animationCode, animation);
        }
        if (!canInsert) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!canDuplicate) ImGui.BeginDisabled();
        if (ImGui.Button("Duplicate selected marker##timeline-actions"))
        {
            _animationHistory.BeginEdit(animationCode, animation, $"Duplicate {selection}");
            DuplicateTimelineSelection(animation);
            _animationHistory.CommitEdit(animationCode, animation);
        }
        if (!canDuplicate) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!canDelete) ImGui.BeginDisabled();
        if (ImGui.Button("Delete selected marker##timeline-actions"))
        {
            _animationHistory.BeginEdit(animationCode, animation, $"Delete {selection}");
            DeleteTimelineSelection(animation);
            _animationHistory.CommitEdit(animationCode, animation);
        }
        if (!canDelete) ImGui.EndDisabled();
    }

    private void BeginTimelineActionEdit(string animationCode, Animation animation, string label)
    {
        if (_animationHistory.HasPendingEdit(animationCode)) return;

        _animationHistory.BeginEdit(animationCode, animation, label);
    }

    private string GetTimelineSelectionLabel(Animation animation)
    {
        return _timelineSelectedKind switch
        {
            TimelineRetimingKind.Player => animation.PlayerKeyFrames.Count == 0
                ? "Selected player track (no marker)"
                : $"Selected player keyframe {Math.Clamp(animation._playerFrameIndex, 0, Math.Max(0, animation.PlayerKeyFrames.Count - 1))}",
            TimelineRetimingKind.Item => animation.ItemKeyFrames.Count == 0
                ? "Selected item track (no marker)"
                : $"Selected item keyframe {Math.Clamp(animation._itemFrameIndex, 0, Math.Max(0, animation.ItemKeyFrames.Count - 1))}",
            TimelineRetimingKind.Event => GetTimelineEventFrameCount(animation, _timelineSelectedEventTrack) == 0
                ? $"Selected {_timelineSelectedEventTrack.ToString().ToLowerInvariant()} track (no marker)"
                : $"Selected {_timelineSelectedEventTrack.ToString().ToLowerInvariant()} marker {GetSelectedTimelineEventIndex(animation, _timelineSelectedEventTrack)}",
            _ => "No timeline marker selected"
        };
    }

    private int GetTimelineTrackIndex()
    {
        return _timelineSelectedKind switch
        {
            TimelineRetimingKind.Player => 0,
            TimelineRetimingKind.Item => 1,
            TimelineRetimingKind.Event when _timelineSelectedEventTrack == TimelineEventTrack.Sound => 2,
            TimelineRetimingKind.Event when _timelineSelectedEventTrack == TimelineEventTrack.Particle => 3,
            TimelineRetimingKind.Event when _timelineSelectedEventTrack == TimelineEventTrack.Callback => 4,
            _ => 0
        };
    }

    private void SelectTimelineTrack(int trackIndex)
    {
        switch (trackIndex)
        {
            case 0:
                _timelineSelectedKind = TimelineRetimingKind.Player;
                break;
            case 1:
                _timelineSelectedKind = TimelineRetimingKind.Item;
                break;
            case 2:
                _timelineSelectedKind = TimelineRetimingKind.Event;
                _timelineSelectedEventTrack = TimelineEventTrack.Sound;
                break;
            case 3:
                _timelineSelectedKind = TimelineRetimingKind.Event;
                _timelineSelectedEventTrack = TimelineEventTrack.Particle;
                break;
            case 4:
                _timelineSelectedKind = TimelineRetimingKind.Event;
                _timelineSelectedEventTrack = TimelineEventTrack.Callback;
                break;
        }
    }

    private bool IsTimelineTrackSelected(TimelineRetimingKind kind, TimelineEventTrack eventTrack = TimelineEventTrack.Sound)
    {
        return _timelineSelectedKind == kind && (kind != TimelineRetimingKind.Event || _timelineSelectedEventTrack == eventTrack);
    }

    private bool CanInsertTimelineSelection(Animation animation)
    {
        return _timelineSelectedKind switch
        {
            TimelineRetimingKind.Player => animation.PlayerKeyFrames.Count > 0,
            TimelineRetimingKind.Item => (animation.ItemAnimationEnd - animation.ItemAnimationStart).TotalMilliseconds > 0.0001,
            TimelineRetimingKind.Event => GetEditorPlayerDurationMs(animation) > 0.0001,
            _ => false
        };
    }

    private bool CanRetimingTimelineSelection(Animation animation)
    {
        return _timelineSelectedKind switch
        {
            TimelineRetimingKind.Player => animation.PlayerKeyFrames.Count > 0,
            TimelineRetimingKind.Item => animation.ItemKeyFrames.Count > 0 && (animation.ItemAnimationEnd - animation.ItemAnimationStart).TotalMilliseconds > 0.0001,
            TimelineRetimingKind.Event => GetTimelineEventFrameCount(animation, _timelineSelectedEventTrack) > 0 && GetEditorPlayerDurationMs(animation) > 0.0001,
            _ => false
        };
    }

    private bool CanDuplicateTimelineSelection(Animation animation)
    {
        return _timelineSelectedKind switch
        {
            TimelineRetimingKind.Player => animation.PlayerKeyFrames.Count > 0,
            TimelineRetimingKind.Item => animation.ItemKeyFrames.Count > 0 && (animation.ItemAnimationEnd - animation.ItemAnimationStart).TotalMilliseconds > 0.0001,
            TimelineRetimingKind.Event => GetTimelineEventFrameCount(animation, _timelineSelectedEventTrack) > 0 && GetEditorPlayerDurationMs(animation) > 0.0001,
            _ => false
        };
    }

    private bool CanDeleteTimelineSelection(Animation animation)
    {
        return _timelineSelectedKind switch
        {
            TimelineRetimingKind.Player => animation.PlayerKeyFrames.Count > 1,
            TimelineRetimingKind.Item => animation.ItemKeyFrames.Count > 0,
            TimelineRetimingKind.Event => GetTimelineEventFrameCount(animation, _timelineSelectedEventTrack) > 0,
            _ => false
        };
    }

    private double GetSelectedTimelineMarkerTimeMs(Animation animation)
    {
        return _timelineSelectedKind switch
        {
            TimelineRetimingKind.Player when animation.PlayerKeyFrames.Count > 0 => animation.PlayerKeyFrames[Math.Clamp(animation._playerFrameIndex, 0, animation.PlayerKeyFrames.Count - 1)].Time.TotalMilliseconds,
            TimelineRetimingKind.Item when animation.ItemKeyFrames.Count > 0 => GetTimelineItemKeyframeTimeMs(animation, Math.Clamp(animation._itemFrameIndex, 0, animation.ItemKeyFrames.Count - 1)),
            TimelineRetimingKind.Event when GetTimelineEventFrameCount(animation, _timelineSelectedEventTrack) > 0 => GetTimelineEventFrameTimeMs(animation, _timelineSelectedEventTrack, GetSelectedTimelineEventIndex(animation, _timelineSelectedEventTrack)),
            _ => GetEditorFrameTimeMs(animation)
        };
    }

    private void RetimingTimelineSelection(Animation animation, double requestedTimeMs)
    {
        switch (_timelineSelectedKind)
        {
            case TimelineRetimingKind.Player:
                RetimingTimelinePlayerKeyframe(animation, Math.Clamp(animation._playerFrameIndex, 0, Math.Max(0, animation.PlayerKeyFrames.Count - 1)), requestedTimeMs);
                break;
            case TimelineRetimingKind.Item:
                RetimingTimelineItemKeyframe(animation, Math.Clamp(animation._itemFrameIndex, 0, Math.Max(0, animation.ItemKeyFrames.Count - 1)), requestedTimeMs);
                break;
            case TimelineRetimingKind.Event:
                RetimingTimelineEventFrame(animation, _timelineSelectedEventTrack, GetSelectedTimelineEventIndex(animation, _timelineSelectedEventTrack), requestedTimeMs);
                break;
        }
    }

    private void InsertTimelineSelection(Animation animation)
    {
        switch (_timelineSelectedKind)
        {
            case TimelineRetimingKind.Player:
                InsertTimelinePlayerKeyframe(animation);
                break;
            case TimelineRetimingKind.Item:
                InsertTimelineItemKeyframe(animation);
                break;
            case TimelineRetimingKind.Event:
                InsertTimelineEventFrame(animation, _timelineSelectedEventTrack);
                break;
        }
    }

    private void DuplicateTimelineSelection(Animation animation)
    {
        switch (_timelineSelectedKind)
        {
            case TimelineRetimingKind.Player:
                DuplicateTimelinePlayerKeyframe(animation);
                break;
            case TimelineRetimingKind.Item:
                DuplicateTimelineItemKeyframe(animation);
                break;
            case TimelineRetimingKind.Event:
                DuplicateTimelineEventFrame(animation, _timelineSelectedEventTrack);
                break;
        }
    }

    private void DeleteTimelineSelection(Animation animation)
    {
        switch (_timelineSelectedKind)
        {
            case TimelineRetimingKind.Player:
                DeleteTimelinePlayerKeyframe(animation);
                break;
            case TimelineRetimingKind.Item:
                DeleteTimelineItemKeyframe(animation);
                break;
            case TimelineRetimingKind.Event:
                DeleteTimelineEventFrame(animation, _timelineSelectedEventTrack);
                break;
        }
    }

    private void InsertTimelinePlayerKeyframe(Animation animation)
    {
        if (animation.PlayerKeyFrames.Count == 0) return;

        double playerDurationMs = GetEditorPlayerDurationMs(animation);
        double requestedMs = Math.Clamp(GetEditorFrameTimeMs(animation), 0, playerDurationMs);
        int insertIndex = GetTimelineInsertIndex(animation.PlayerKeyFrames.Count, index => animation.PlayerKeyFrames[index].Time.TotalMilliseconds, requestedMs);
        double insertMs = GetInsertMarkerTimeMs(requestedMs, insertIndex, animation.PlayerKeyFrames.Count, index => animation.PlayerKeyFrames[index].Time.TotalMilliseconds, 0, playerDurationMs);
        PlayerFrame frame = animation.StillPlayerFrame(animation._playerFrameIndex, animation._frameProgress).Player;

        animation.PlayerKeyFrames.Insert(insertIndex, new PLayerKeyFrame(frame, TimeSpan.FromMilliseconds(insertMs), EasingFunctionType.Linear));
        animation._playerFrameIndex = insertIndex;
        animation._frameProgress = 0;
        animation._playerFrameEdited = true;
        ProcessPlayerKeyFramesForEditor(animation);
        SelectEditorTimelinePlayerKeyframe(animation, insertIndex);
    }

    private void InsertTimelineItemKeyframe(Animation animation)
    {
        double startMs = animation.ItemAnimationStart.TotalMilliseconds;
        double endMs = animation.ItemAnimationEnd.TotalMilliseconds;
        double spanMs = endMs - startMs;
        if (spanMs <= 0.0001) return;

        double requestedMs = Math.Clamp(GetEditorFrameTimeMs(animation), startMs, endMs);
        int insertIndex = GetTimelineInsertIndex(animation.ItemKeyFrames.Count, index => GetTimelineItemKeyframeTimeMs(animation, index), requestedMs);
        double insertMs = GetInsertMarkerTimeMs(requestedMs, insertIndex, animation.ItemKeyFrames.Count, index => GetTimelineItemKeyframeTimeMs(animation, index), startMs, endMs);
        float fraction = (float)Math.Clamp((insertMs - startMs) / spanMs, 0, 1);
        ItemFrame frame = animation.ItemKeyFrames.Count > 0
            ? animation.ItemKeyFrames[Math.Clamp(animation._itemFrameIndex, 0, animation.ItemKeyFrames.Count - 1)].Frame
            : ItemKeyFrame.Empty.Frame;
        EasingFunctionType easing = animation.ItemKeyFrames.Count > 0
            ? animation.ItemKeyFrames[Math.Clamp(animation._itemFrameIndex, 0, animation.ItemKeyFrames.Count - 1)].EasingFunction
            : EasingFunctionType.Linear;

        animation.ItemKeyFrames.Insert(insertIndex, new ItemKeyFrame(frame, fraction, easing));
        animation._itemFrameIndex = insertIndex;
        SelectEditorTimelineItemFrame(animation, insertIndex, insertMs);
    }

    private void InsertTimelineEventFrame(Animation animation, TimelineEventTrack track)
    {
        double playerDurationMs = GetEditorPlayerDurationMs(animation);
        if (playerDurationMs <= 0.0001) return;

        int count = GetTimelineEventFrameCount(animation, track);
        double requestedMs = Math.Clamp(GetEditorFrameTimeMs(animation), 0, playerDurationMs);
        int insertIndex = GetTimelineInsertIndex(count, index => GetTimelineEventFrameTimeMs(animation, track, index), requestedMs);
        double insertMs = GetInsertMarkerTimeMs(requestedMs, insertIndex, count, index => GetTimelineEventFrameTimeMs(animation, track, index), 0, playerDurationMs);
        float fraction = (float)Math.Clamp(insertMs / playerDurationMs, 0, 1);

        InsertBlankTimelineEventFrame(animation, track, insertIndex, fraction);
        SelectEditorTimelineEventFrame(animation, track, insertIndex, insertMs);
    }

    private void DuplicateTimelinePlayerKeyframe(Animation animation)
    {
        if (animation.PlayerKeyFrames.Count == 0) return;

        int sourceIndex = Math.Clamp(animation._playerFrameIndex, 0, animation.PlayerKeyFrames.Count - 1);
        int insertIndex = sourceIndex + 1;
        double sourceMs = animation.PlayerKeyFrames[sourceIndex].Time.TotalMilliseconds;
        double duplicateMs = sourceIndex < animation.PlayerKeyFrames.Count - 1
            ? (sourceMs + animation.PlayerKeyFrames[sourceIndex + 1].Time.TotalMilliseconds) * 0.5
            : sourceMs + 50;

        PLayerKeyFrame source = animation.PlayerKeyFrames[sourceIndex];
        animation.PlayerKeyFrames.Insert(insertIndex, new PLayerKeyFrame(source.Frame, TimeSpan.FromMilliseconds(duplicateMs), source.EasingFunction, source.EasingType, source.FrameProgressRange));
        animation._playerFrameIndex = insertIndex;
        animation._frameProgress = 0;
        animation._playerFrameEdited = true;
        ProcessPlayerKeyFramesForEditor(animation);
        SelectEditorTimelinePlayerKeyframe(animation, insertIndex);
    }

    private void DeleteTimelinePlayerKeyframe(Animation animation)
    {
        if (animation.PlayerKeyFrames.Count <= 1) return;

        int index = Math.Clamp(animation._playerFrameIndex, 0, animation.PlayerKeyFrames.Count - 1);
        animation.PlayerKeyFrames.RemoveAt(index);
        animation._playerFrameIndex = Math.Clamp(index, 0, animation.PlayerKeyFrames.Count - 1);
        animation._frameProgress = 0;
        animation._playerFrameEdited = true;
        ProcessPlayerKeyFramesForEditor(animation);
        SelectEditorTimelinePlayerKeyframe(animation, animation._playerFrameIndex);
    }

    private void DuplicateTimelineItemKeyframe(Animation animation)
    {
        if (animation.ItemKeyFrames.Count == 0) return;

        int sourceIndex = Math.Clamp(animation._itemFrameIndex, 0, animation.ItemKeyFrames.Count - 1);
        int insertIndex = sourceIndex + 1;
        double duplicateMs = GetDuplicateMarkerTimeMs(GetTimelineItemKeyframeTimeMs(animation, sourceIndex), sourceIndex, animation.ItemKeyFrames.Count, index => GetTimelineItemKeyframeTimeMs(animation, index), animation.ItemAnimationStart.TotalMilliseconds, animation.ItemAnimationEnd.TotalMilliseconds);
        double spanMs = (animation.ItemAnimationEnd - animation.ItemAnimationStart).TotalMilliseconds;
        float fraction = spanMs <= 0.0001 ? animation.ItemKeyFrames[sourceIndex].DurationFraction : (float)Math.Clamp((duplicateMs - animation.ItemAnimationStart.TotalMilliseconds) / spanMs, 0, 1);

        ItemKeyFrame source = animation.ItemKeyFrames[sourceIndex];
        animation.ItemKeyFrames.Insert(insertIndex, new ItemKeyFrame(source.Frame, fraction, source.EasingFunction));
        animation._itemFrameIndex = insertIndex;
        SelectEditorTimelineItemFrame(animation, insertIndex, duplicateMs);
    }

    private void DeleteTimelineItemKeyframe(Animation animation)
    {
        if (animation.ItemKeyFrames.Count == 0) return;

        int index = Math.Clamp(animation._itemFrameIndex, 0, animation.ItemKeyFrames.Count - 1);
        animation.ItemKeyFrames.RemoveAt(index);
        animation._itemFrameIndex = Math.Clamp(index, 0, Math.Max(0, animation.ItemKeyFrames.Count - 1));
        if (animation.ItemKeyFrames.Count > 0)
        {
            SelectEditorTimelineItemFrame(animation, animation._itemFrameIndex, GetTimelineItemKeyframeTimeMs(animation, animation._itemFrameIndex));
        }
    }

    private void DuplicateTimelineEventFrame(Animation animation, TimelineEventTrack track)
    {
        int count = GetTimelineEventFrameCount(animation, track);
        if (count == 0) return;

        int sourceIndex = Math.Clamp(GetSelectedTimelineEventIndex(animation, track), 0, count - 1);
        int insertIndex = sourceIndex + 1;
        double playerDurationMs = GetEditorPlayerDurationMs(animation);
        double duplicateMs = GetDuplicateMarkerTimeMs(GetTimelineEventFrameTimeMs(animation, track, sourceIndex), sourceIndex, count, index => GetTimelineEventFrameTimeMs(animation, track, index), 0, playerDurationMs);
        float fraction = playerDurationMs <= 0.0001 ? GetTimelineEventFrameFraction(animation, track, sourceIndex) : (float)Math.Clamp(duplicateMs / playerDurationMs, 0, 1);

        InsertTimelineEventFrameCopy(animation, track, sourceIndex, insertIndex, fraction);
        SelectEditorTimelineEventFrame(animation, track, insertIndex, duplicateMs);
    }

    private void DeleteTimelineEventFrame(Animation animation, TimelineEventTrack track)
    {
        int count = GetTimelineEventFrameCount(animation, track);
        if (count == 0) return;

        int index = Math.Clamp(GetSelectedTimelineEventIndex(animation, track), 0, count - 1);
        switch (track)
        {
            case TimelineEventTrack.Sound:
                animation.SoundFrames.RemoveAt(index);
                animation._soundsFrameIndex = Math.Clamp(index, 0, Math.Max(0, animation.SoundFrames.Count - 1));
                break;
            case TimelineEventTrack.Particle:
                animation.ParticlesFrames.RemoveAt(index);
                animation._particlesFrameIndex = Math.Clamp(index, 0, Math.Max(0, animation.ParticlesFrames.Count - 1));
                break;
            case TimelineEventTrack.Callback:
                animation.CallbackFrames.RemoveAt(index);
                animation._callbackFrameIndex = Math.Clamp(index, 0, Math.Max(0, animation.CallbackFrames.Count - 1));
                break;
        }

        int newCount = GetTimelineEventFrameCount(animation, track);
        if (newCount > 0)
        {
            int selectedIndex = Math.Clamp(index, 0, newCount - 1);
            SelectEditorTimelineEventFrame(animation, track, selectedIndex, GetTimelineEventFrameTimeMs(animation, track, selectedIndex));
        }
    }

    private static double GetDuplicateMarkerTimeMs(double sourceMs, int sourceIndex, int count, System.Func<int, double> timeProvider, double minMs, double maxMs)
    {
        if (sourceIndex < count - 1)
        {
            double nextMs = timeProvider(sourceIndex + 1);
            return sourceMs + Math.Max(1, (nextMs - sourceMs) * 0.5);
        }

        if (sourceIndex > 0)
        {
            double previousMs = timeProvider(sourceIndex - 1);
            return Math.Clamp(sourceMs + Math.Max(1, (sourceMs - previousMs) * 0.5), minMs, maxMs);
        }

        return Math.Clamp(sourceMs + 50, minMs, maxMs);
    }

    private static int GetTimelineInsertIndex(int count, System.Func<int, double> timeProvider, double timeMs)
    {
        int index = 0;
        while (index < count && timeProvider(index) < timeMs)
        {
            index++;
        }

        return index;
    }

    private static double GetInsertMarkerTimeMs(double requestedMs, int insertIndex, int count, System.Func<int, double> timeProvider, double minMs, double maxMs)
    {
        double lowerMs = insertIndex <= 0 ? minMs : timeProvider(insertIndex - 1) + 1;
        double upperMs = insertIndex >= count ? maxMs : timeProvider(insertIndex) - 1;
        if (lowerMs <= upperMs)
        {
            return Math.Clamp(requestedMs, lowerMs, upperMs);
        }

        return Math.Clamp(requestedMs, minMs, maxMs);
    }

    private void RetimingTimelineItemKeyframe(Animation animation, int keyframeIndex, double requestedTimeMs)
    {
        if (keyframeIndex < 0 || keyframeIndex >= animation.ItemKeyFrames.Count) return;

        double startMs = animation.ItemAnimationStart.TotalMilliseconds;
        double endMs = animation.ItemAnimationEnd.TotalMilliseconds;
        double spanMs = endMs - startMs;
        if (spanMs <= 0.0001) return;

        double minMs = keyframeIndex == 0 ? startMs : GetTimelineItemKeyframeTimeMs(animation, keyframeIndex - 1) + 1;
        double maxMs = keyframeIndex == animation.ItemKeyFrames.Count - 1 ? endMs : GetTimelineItemKeyframeTimeMs(animation, keyframeIndex + 1) - 1;
        double timeMs = Math.Clamp(requestedTimeMs, minMs, Math.Max(minMs, maxMs));
        float fraction = (float)Math.Clamp((timeMs - startMs) / spanMs, 0, 1);

        ItemKeyFrame frame = animation.ItemKeyFrames[keyframeIndex];
        animation.ItemKeyFrames[keyframeIndex] = new ItemKeyFrame(frame.Frame, fraction, frame.EasingFunction);
        animation._itemFrameIndex = keyframeIndex;
        ScrubEditorTimeline(animation, timeMs);
    }

    private void RetimingTimelineEventFrame(Animation animation, TimelineEventTrack track, int frameIndex, double requestedTimeMs)
    {
        int count = GetTimelineEventFrameCount(animation, track);
        if (frameIndex < 0 || frameIndex >= count) return;

        double playerDurationMs = GetEditorPlayerDurationMs(animation);
        if (playerDurationMs <= 0.0001) return;

        double minMs = frameIndex == 0 ? 0 : GetTimelineEventFrameTimeMs(animation, track, frameIndex - 1) + 1;
        double maxMs = frameIndex == count - 1 ? playerDurationMs : GetTimelineEventFrameTimeMs(animation, track, frameIndex + 1) - 1;
        double timeMs = Math.Clamp(requestedTimeMs, minMs, Math.Max(minMs, maxMs));
        float fraction = (float)Math.Clamp(timeMs / playerDurationMs, 0, 1);

        SetTimelineEventFrameFraction(animation, track, frameIndex, fraction);
        SelectEditorTimelineEventFrame(animation, track, frameIndex, timeMs);
    }

    private static void ProcessPlayerKeyFramesForEditor(Animation animation)
    {
        ProcessPlayerKeyFramesMethod?.Invoke(animation, null);
    }

    private TimelineMarker[] BuildPlayerTimelineMarkers(Animation animation)
    {
        TimelineMarker[] markers = new TimelineMarker[animation.PlayerKeyFrames.Count];
        for (int index = 0; index < animation.PlayerKeyFrames.Count; index++)
        {
            bool selected = index == animation._playerFrameIndex;
            markers[index] = new TimelineMarker(
                animation.PlayerKeyFrames[index].Time.TotalMilliseconds,
                index.ToString(),
                selected ? TimelineSelectedColor() : TimelinePlayerColor(),
                selected);
        }

        return markers;
    }

    private static TimelineMarker[] BuildFractionTimelineMarkers(int count, System.Func<int, double> timeProvider, string prefix, uint color, int selectedIndex)
    {
        TimelineMarker[] markers = new TimelineMarker[count];
        for (int index = 0; index < count; index++)
        {
            bool selected = index == selectedIndex;
            markers[index] = new TimelineMarker(timeProvider(index), $"{prefix}{index}", selected ? TimelineSelectedColor() : color, selected);
        }

        return markers;
    }

    private static void DrawTimelineTrack(ImDrawListPtr drawList, string label, int row, TimelineMarker[] markers, float labelX, float trackStart, float trackEnd, float rowY, uint textColor, uint lineColor, double durationMs, bool selectedTrack)
    {
        drawList.AddText(new NVector2(labelX, rowY - 8), textColor, label);
        if (selectedTrack)
        {
            uint selectedFill = ImGui.ColorConvertFloat4ToU32(new NVector4(0.23f, 0.36f, 0.22f, 0.30f));
            drawList.AddRectFilled(new NVector2(trackStart, rowY - 9), new NVector2(trackEnd, rowY + 9), selectedFill, 3);
        }

        drawList.AddLine(new NVector2(trackStart, rowY), new NVector2(trackEnd, rowY), selectedTrack ? TimelineSelectedColor() : lineColor, selectedTrack ? 2 : 1);

        float trackWidth = Math.Max(1, trackEnd - trackStart);
        foreach (TimelineMarker marker in markers)
        {
            float x = trackStart + (float)(Math.Clamp(marker.TimeMs, 0, durationMs) / durationMs * trackWidth);
            float radius = marker.Selected ? 5.5f : 4f;
            drawList.AddCircleFilled(new NVector2(x, rowY), radius, marker.Color, 12);
            if (row == 0)
            {
                drawList.AddText(new NVector2(x + 5, rowY - 8), textColor, marker.Label);
            }
        }
    }

    private static double GetEditorAnimationDurationMs(Animation animation)
    {
        double baseDurationMs = GetEditorPlayerDurationMs(animation);
        double durationMs = baseDurationMs;
        durationMs = Math.Max(durationMs, animation.ItemAnimationEnd.TotalMilliseconds);

        foreach (SoundFrame frame in animation.SoundFrames) durationMs = Math.Max(durationMs, baseDurationMs * frame.DurationFraction);
        foreach (ParticlesFrame frame in animation.ParticlesFrames) durationMs = Math.Max(durationMs, baseDurationMs * frame.DurationFraction);
        foreach (CallbackFrame frame in animation.CallbackFrames) durationMs = Math.Max(durationMs, baseDurationMs * frame.DurationFraction);

        return Math.Max(1, durationMs);
    }

    private static double GetEditorPlayerDurationMs(Animation animation)
    {
        return animation.PlayerKeyFrames.Count == 0 ? 0 : animation.PlayerKeyFrames[^1].Time.TotalMilliseconds;
    }

    private static double GetTimelineItemKeyframeTimeMs(Animation animation, int index)
    {
        return animation.ItemAnimationStart.TotalMilliseconds + (animation.ItemAnimationEnd - animation.ItemAnimationStart).TotalMilliseconds * animation.ItemKeyFrames[index].DurationFraction;
    }

    private static int GetTimelineEventFrameCount(Animation animation, TimelineEventTrack track)
    {
        return track switch
        {
            TimelineEventTrack.Sound => animation.SoundFrames.Count,
            TimelineEventTrack.Particle => animation.ParticlesFrames.Count,
            TimelineEventTrack.Callback => animation.CallbackFrames.Count,
            _ => 0
        };
    }

    private static double GetTimelineEventFrameTimeMs(Animation animation, TimelineEventTrack track, int index)
    {
        double durationMs = GetEditorPlayerDurationMs(animation);
        return track switch
        {
            TimelineEventTrack.Sound => durationMs * animation.SoundFrames[index].DurationFraction,
            TimelineEventTrack.Particle => durationMs * animation.ParticlesFrames[index].DurationFraction,
            TimelineEventTrack.Callback => durationMs * animation.CallbackFrames[index].DurationFraction,
            _ => 0
        };
    }

    private static void SetTimelineEventFrameFraction(Animation animation, TimelineEventTrack track, int index, float fraction)
    {
        switch (track)
        {
            case TimelineEventTrack.Sound:
                SoundFrame sound = animation.SoundFrames[index];
                animation.SoundFrames[index] = new SoundFrame(sound.Code, fraction, sound.RandomizePitch, sound.Range, sound.Volume, sound.Synchronize);
                animation._soundsFrameIndex = index;
                break;
            case TimelineEventTrack.Particle:
                ParticlesFrame particle = animation.ParticlesFrames[index];
                animation.ParticlesFrames[index] = new ParticlesFrame(particle.Code, fraction, particle.Position, particle.Velocity, particle.Intensity);
                animation._particlesFrameIndex = index;
                break;
            case TimelineEventTrack.Callback:
                CallbackFrame callback = animation.CallbackFrames[index];
                animation.CallbackFrames[index] = new CallbackFrame(callback.Code, fraction);
                animation._callbackFrameIndex = index;
                break;
        }
    }

    private static int GetSelectedTimelineEventIndex(Animation animation, TimelineEventTrack track)
    {
        return track switch
        {
            TimelineEventTrack.Sound => Math.Clamp(animation._soundsFrameIndex, 0, Math.Max(0, animation.SoundFrames.Count - 1)),
            TimelineEventTrack.Particle => Math.Clamp(animation._particlesFrameIndex, 0, Math.Max(0, animation.ParticlesFrames.Count - 1)),
            TimelineEventTrack.Callback => Math.Clamp(animation._callbackFrameIndex, 0, Math.Max(0, animation.CallbackFrames.Count - 1)),
            _ => 0
        };
    }

    private static float GetTimelineEventFrameFraction(Animation animation, TimelineEventTrack track, int index)
    {
        return track switch
        {
            TimelineEventTrack.Sound => animation.SoundFrames[index].DurationFraction,
            TimelineEventTrack.Particle => animation.ParticlesFrames[index].DurationFraction,
            TimelineEventTrack.Callback => animation.CallbackFrames[index].DurationFraction,
            _ => 0
        };
    }

    private static void InsertBlankTimelineEventFrame(Animation animation, TimelineEventTrack track, int insertIndex, float fraction)
    {
        switch (track)
        {
            case TimelineEventTrack.Sound:
                animation.SoundFrames.Insert(insertIndex, new SoundFrame(new[] { "" }, fraction));
                animation._soundsFrameIndex = insertIndex;
                break;
            case TimelineEventTrack.Particle:
                animation.ParticlesFrames.Insert(insertIndex, new ParticlesFrame("", fraction, OpenTK.Mathematics.Vector3.Zero, OpenTK.Mathematics.Vector3.Zero, 1));
                animation._particlesFrameIndex = insertIndex;
                break;
            case TimelineEventTrack.Callback:
                animation.CallbackFrames.Insert(insertIndex, new CallbackFrame("", fraction));
                animation._callbackFrameIndex = insertIndex;
                break;
        }
    }

    private static void InsertTimelineEventFrameCopy(Animation animation, TimelineEventTrack track, int sourceIndex, int insertIndex, float fraction)
    {
        switch (track)
        {
            case TimelineEventTrack.Sound:
                SoundFrame sound = animation.SoundFrames[sourceIndex];
                animation.SoundFrames.Insert(insertIndex, new SoundFrame(sound.Code, fraction, sound.RandomizePitch, sound.Range, sound.Volume, sound.Synchronize));
                animation._soundsFrameIndex = insertIndex;
                break;
            case TimelineEventTrack.Particle:
                ParticlesFrame particle = animation.ParticlesFrames[sourceIndex];
                animation.ParticlesFrames.Insert(insertIndex, new ParticlesFrame(particle.Code, fraction, particle.Position, particle.Velocity, particle.Intensity));
                animation._particlesFrameIndex = insertIndex;
                break;
            case TimelineEventTrack.Callback:
                CallbackFrame callback = animation.CallbackFrames[sourceIndex];
                animation.CallbackFrames.Insert(insertIndex, new CallbackFrame(callback.Code, fraction));
                animation._callbackFrameIndex = insertIndex;
                break;
        }
    }

    private static uint TimelinePlayerColor() => ImGui.ColorConvertFloat4ToU32(new NVector4(0.30f, 0.72f, 1.0f, 1f));
    private static uint TimelineSelectedColor() => ImGui.ColorConvertFloat4ToU32(new NVector4(0.26f, 1.0f, 0.48f, 1f));
    private static uint TimelineItemColor() => ImGui.ColorConvertFloat4ToU32(new NVector4(0.98f, 0.65f, 0.22f, 1f));
    private static uint TimelineSoundColor() => ImGui.ColorConvertFloat4ToU32(new NVector4(0.90f, 0.42f, 0.88f, 1f));
    private static uint TimelineParticleColor() => ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.92f, 0.55f, 1f));
    private static uint TimelineCallbackColor() => ImGui.ColorConvertFloat4ToU32(new NVector4(0.94f, 0.86f, 0.38f, 1f));

    private enum TimelineEventTrack
    {
        Sound,
        Particle,
        Callback
    }

    private enum TimelineRetimingKind
    {
        None,
        Player,
        Item,
        Event
    }

    private enum AnimationValidationSeverity
    {
        Warning,
        Error
    }

    private readonly struct AnimationValidationMessage
    {
        public AnimationValidationMessage(AnimationValidationSeverity severity, string text)
        {
            Severity = severity;
            Text = text;
        }

        public AnimationValidationSeverity Severity { get; }
        public string Text { get; }

        public static AnimationValidationMessage Warning(string text) => new(AnimationValidationSeverity.Warning, text);
        public static AnimationValidationMessage Error(string text) => new(AnimationValidationSeverity.Error, text);
    }

    private readonly struct TimelineMarker
    {
        public TimelineMarker(double timeMs, string label, uint color, bool selected)
        {
            TimeMs = timeMs;
            Label = label;
            Color = color;
            Selected = selected;
        }

        public double TimeMs { get; }
        public string Label { get; }
        public uint Color { get; }
        public bool Selected { get; }
    }

    private void DrawAnimationHistoryControls(string animationCode)
    {
        HandleAnimationHistoryShortcuts(animationCode);

        bool canUndo = _animationHistory.UndoCount(animationCode) > 0;
        bool canRedo = _animationHistory.RedoCount(animationCode) > 0;

        if (!canUndo) ImGui.BeginDisabled();
        if (ImGui.Button("Undo##animation"))
        {
            CommitPendingAnimationEdit(animationCode);
            if (_animationHistory.Undo(animationCode, AnimationsManager._instance.Animations, out string status))
            {
                _transformSaveStatus = status;
            }
            else
            {
                _transformSaveStatus = status;
            }
        }
        if (!canUndo) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!canRedo) ImGui.BeginDisabled();
        if (ImGui.Button("Redo##animation"))
        {
            CommitPendingAnimationEdit(animationCode);
            if (_animationHistory.Redo(animationCode, AnimationsManager._instance.Animations, out string status))
            {
                _transformSaveStatus = status;
            }
            else
            {
                _transformSaveStatus = status;
            }
        }
        if (!canRedo) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Clear history##animation"))
        {
            _animationHistory.Clear(animationCode);
            _transformSaveStatus = "Animation edit history cleared.";
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"Undo: {_animationHistory.UndoCount(animationCode)}  Redo: {_animationHistory.RedoCount(animationCode)}");
    }

    private void HandleAnimationHistoryShortcuts(string animationCode)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        if (io.WantTextInput || !io.KeyCtrl) return;

        if (ImGui.IsKeyPressed(ImGuiKey.Z))
        {
            CommitPendingAnimationEdit(animationCode);
            _animationHistory.Undo(animationCode, AnimationsManager._instance.Animations, out _transformSaveStatus);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Y))
        {
            CommitPendingAnimationEdit(animationCode);
            _animationHistory.Redo(animationCode, AnimationsManager._instance.Animations, out _transformSaveStatus);
        }
    }

    private void TrackAnimationEditorChanges(string animationCode, Animation beforeEdit, string beforeEditSerialized, Animation afterEdit, string label)
    {
        string afterEditSerialized = AnimationEditorHistory.Serialize(afterEdit);
        bool changed = beforeEditSerialized != afterEditSerialized;
        bool anyItemActive = ImGui.IsAnyItemActive();

        if (_animationHistoryExplicitEditThisFrame) return;

        if (_animationHistory.HasPendingEdit(animationCode))
        {
            if (!anyItemActive && !_animationHistoryExternalDragActive)
            {
                _animationHistory.CommitEdit(animationCode, afterEdit);
            }
            return;
        }

        if (!changed) return;

        if (anyItemActive)
        {
            _animationHistory.BeginEdit(animationCode, beforeEdit, label);
        }
        else
        {
            _animationHistory.RecordSnapshot(animationCode, beforeEdit, label);
        }
    }

    private void CommitPendingAnimationEdit(string animationCode)
    {
        if (!AnimationsManager._instance.Animations.TryGetValue(animationCode, out Animation? animation)) return;

        _animationHistory.CommitEdit(animationCode, animation);
    }

    private void TransformEditorTab()
    {
        DrawHeldTransformRegistration();
        ImGui.Separator();

        ImGui.InputTextWithHint("Filter##" + "transforms", "supports wildcards", ref _legacyTransformFilter, 200);
        EditorsUtils.FilterElements(_legacyTransformFilter, _transforms.Keys, out IEnumerable<string> filtered, out IEnumerable<int> indexes);

        string[] filteredTransforms = filtered.ToArray();
        if (_transformIndex >= filteredTransforms.Length)
        {
            _transformIndex = 0;
        }

        ImGui.ListBox("transforms", ref _transformIndex, filteredTransforms, filteredTransforms.Length);

        if (filteredTransforms.Length == 0) return;

        string currentTransform = filteredTransforms[_transformIndex];

        if (!_transforms.ContainsKey(currentTransform)) return;

        EditableTransform editableTransform = _transforms[currentTransform];
        ModelTransform transform = editableTransform.Transform;

        if (ImGui.Button($"Export to clipboard"))
        {
            ImGui.SetClipboardText(JsonUtil.ToPrettyString(transform));
        }
        ImGui.SameLine();
        if (editableTransform.SaveToSource != null && ImGui.Button("Save authored file##transform"))
        {
            editableTransform.Apply?.Invoke(transform);
            QueueSourceSave(editableTransform.SaveToSource(transform), status => _transformSaveStatus = status);
        }
        if (!string.IsNullOrEmpty(_transformSaveStatus))
        {
            ImGui.TextWrapped(_transformSaveStatus);
        }

        DrawTransformGizmoControls("transform", transform, GetGizmoContextForTransformCode(currentTransform), editableTransform.Apply);

        float speed = ImGui.GetIO().KeysDown[(int)ImGuiKey.LeftShift] ? 0.1f : 1;

        float scale = transform.ScaleXYZ.X;
        bool changed = ImGui.DragFloat("Scale##transform", ref scale);
        transform.Scale = scale;

        System.Numerics.Vector3 translation = new(transform.Translation.X, transform.Translation.Y, transform.Translation.Z);
        System.Numerics.Vector3 origin = new(transform.Origin.X, transform.Origin.Y, transform.Origin.Z);
        System.Numerics.Vector3 rotation = new(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z);

        changed |= ImGui.DragFloat3("Translation##transform", ref translation, speed);
        changed |= ImGui.DragFloat3("Origin##transform", ref origin, speed);
        changed |= ImGui.DragFloat3("Rotation##transform", ref rotation, speed);

        transform.Translation.X = translation.X;
        transform.Translation.Y = translation.Y;
        transform.Translation.Z = translation.Z;
        transform.Origin.X = origin.X;
        transform.Origin.Y = origin.Y;
        transform.Origin.Z = origin.Z;
        transform.Rotation.X = rotation.X;
        transform.Rotation.Y = rotation.Y;
        transform.Rotation.Z = rotation.Z;

        if (changed)
        {
            editableTransform.Apply?.Invoke(transform);
        }
    }

    private void DrawHeldTransformRegistration()
    {
        ItemStack? heldStack = _api.World.Player.Entity.RightHandItemSlot.Itemstack;
        CollectibleObject? collectible = heldStack?.Collectible;
        if (collectible == null) return;

        ImGui.Text($"Held collectible: {collectible.Code}");
        ImGui.SetNextItemWidth(260);
        ImGui.Combo("Transform attribute##held-transform-attribute", ref _heldTransformAttributeIndex, DirectTransformAttributeCodes, DirectTransformAttributeCodes.Length);

        string attributeCode = DirectTransformAttributeCodes[_heldTransformAttributeIndex];
        bool exists = collectible.Attributes?[attributeCode].Exists == true;

        if (ImGui.Button(exists ? "Register held transform##held-transform" : "Create held transform##held-transform"))
        {
            ModelTransform transform = exists
                ? collectible.Attributes?[attributeCode].AsObject<ModelTransform>() ?? CreateDefaultTransform()
                : CreateDefaultTransform();

            transform.EnsureDefaultValues();
            if (!exists)
            {
                ApplyDirectTransformAttribute(collectible, attributeCode, transform);
            }

            RegisterTransform(
                transform,
                $"{collectible.Code} / {attributeCode}",
                value => ApplyDirectTransformAttribute(collectible, attributeCode, value),
                value => TrySaveTransformToSource(collectible, attributeCode, value));
        }
    }

    private ModelTransform? _currentTransform;
    private GenericDisplayProto? _currentBlock;
    private BlockPos? _currentDisplayBlockPos;
    private Action<ModelTransform>? _currentDisplayApply;
    private System.Func<ModelTransform, SourceSaveResult>? _currentDisplaySaveToSource;
    private Action? _currentDisplayRedraw;
    private string _currentDisplayContext = "";
    private string _displaySaveStatus = "";
    private bool _selected = false;
    private bool _updateMesh = false;
    private void GenericDisplayTab()
    {
        BlockSelection? selection = _api.World.Player.CurrentBlockSelection;
        CollectibleObject? collectible = _api.World.Player.Entity.RightHandItemSlot.Itemstack?.Collectible;

        if (ImGui.Button("Select##GenericDisplayTab") && !_selected && selection?.Block != null && collectible != null)
        {
            SelectDisplayTransform(selection, collectible);
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear selection##GenericDisplayTab"))
        {
            ClearDisplayTransformSelection();
        }
        ImGui.SameLine();
        if (ImGui.Button("Redraw##GenericDisplayTab"))
        {
            _currentDisplayRedraw?.Invoke();
        }

        if (_currentTransform == null || !_selected) return;

        if (!string.IsNullOrEmpty(_currentDisplayContext))
        {
            ImGui.TextWrapped(_currentDisplayContext);
        }

        ModelTransform transform = _currentTransform;

        if (ImGui.Button($"Export to clipboard##GenericDisplayTab"))
        {
            ImGui.SetClipboardText(JsonUtil.ToPrettyString(transform));
        }

        ImGui.SameLine();
        if (_currentDisplaySaveToSource != null && ImGui.Button("Save authored file##GenericDisplayTab"))
        {
            _currentDisplayApply?.Invoke(transform);
            QueueSourceSave(_currentDisplaySaveToSource(transform), status => _displaySaveStatus = status);
        }

        ImGui.SameLine();
        ImGui.Checkbox("Update mesh", ref _updateMesh);

        if (!string.IsNullOrEmpty(_displaySaveStatus))
        {
            ImGui.TextWrapped(_displaySaveStatus);
        }

        DrawTransformGizmoControls("GenericDisplayTab", transform, TransformGizmoContext.Display, _currentDisplayApply, _currentDisplayBlockPos);

        float speed = ImGui.GetIO().KeysDown[(int)ImGuiKey.LeftShift] ? 0.1f : 1;

        float scale = transform.ScaleXYZ.X;
        bool changed = ImGui.DragFloat("Scale##GenericDisplayTab", ref scale, speed * 0.1f);
        transform.Scale = scale;

        System.Numerics.Vector3 translation = new(transform.Translation.X, transform.Translation.Y, transform.Translation.Z);
        System.Numerics.Vector3 origin = new(transform.Origin.X, transform.Origin.Y, transform.Origin.Z);
        System.Numerics.Vector3 rotation = new(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z);

        changed |= ImGui.DragFloat3("Translation##GenericDisplayTab", ref translation, speed * 0.1f);
        changed |= ImGui.DragFloat3("Origin##GenericDisplayTab", ref origin, speed * 0.1f);
        changed |= ImGui.DragFloat3("Rotation##GenericDisplayTab", ref rotation, speed);

        transform.Translation.X = translation.X;
        transform.Translation.Y = translation.Y;
        transform.Translation.Z = translation.Z;
        transform.Origin.X = origin.X;
        transform.Origin.Y = origin.Y;
        transform.Origin.Z = origin.Z;
        transform.Rotation.X = rotation.X;
        transform.Rotation.Y = rotation.Y;
        transform.Rotation.Z = rotation.Z;

        if (changed)
        {
            _currentDisplayApply?.Invoke(transform);
        }

        if (_updateMesh && changed)
        {
            _currentDisplayRedraw?.Invoke();
        }

    }

    private void SelectDisplayTransform(BlockSelection selection, CollectibleObject collectible)
    {
        ClearDisplayTransformSelection();

        _currentBlock = selection.Block.GetBlockEntity<GenericDisplayProto>(selection);
        string? transformCode = _currentBlock?.AttributeTransformCode ?? GetSelectedBlockDisplayTransformCode(selection.Block);
        if (transformCode == null)
        {
            _displaySaveStatus = $"No known display transform context for {selection.Block.Code}.";
            return;
        }

        ModelTransform transform = collectible.Attributes?[transformCode].AsObject<ModelTransform>() ?? CreateDefaultTransform();
        transform.EnsureDefaultValues();

        _currentTransform = transform;
        _currentDisplayBlockPos = selection.Position.Copy();
        _currentDisplayContext = $"{selection.Block.Code} -> {collectible.Code} / {transformCode}";
        _currentDisplayApply = value =>
        {
            ApplyDirectTransformAttribute(collectible, transformCode, value);
            if (_currentBlock != null)
            {
                _currentBlock.EditedTransforms[collectible.Id] = value;
            }
        };
        _currentDisplaySaveToSource = value => TrySaveTransformToSource(collectible, transformCode, value);
        _currentDisplayRedraw = () =>
        {
            if (_currentBlock != null)
            {
                _currentBlock.EditedTransforms[collectible.Id] = transform;
                _currentBlock.RegenerateMeshes();
                return;
            }

            selection.Block.GetBlockEntity<BlockEntity>(selection)?.MarkDirty(true);
        };

        _currentDisplayApply(transform);
        _selected = true;
    }

    private void ClearDisplayTransformSelection()
    {
        _currentTransform = null;
        _currentBlock = null;
        _currentDisplayBlockPos = null;
        _currentDisplayApply = null;
        _currentDisplaySaveToSource = null;
        _currentDisplayRedraw = null;
        _currentDisplayContext = "";
        _displaySaveStatus = "";
        _selected = false;
    }

    private static string? GetSelectedBlockDisplayTransformCode(Block block)
    {
        string? configured = block.Attributes?["inventoryTransformAttribute"].AsString();
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        string className = block.GetType().Name;
        string path = block.Code.Path;

        if (className.Contains("Forge", StringComparison.OrdinalIgnoreCase) || path.Contains("forge", StringComparison.OrdinalIgnoreCase)) return "inForgeTransform";
        if (className.Contains("DisplayCase", StringComparison.OrdinalIgnoreCase) || path.Contains("displaycase", StringComparison.OrdinalIgnoreCase)) return "onDisplayTransform";
        if (className.Contains("Shelf", StringComparison.OrdinalIgnoreCase) || path.Contains("shelf", StringComparison.OrdinalIgnoreCase)) return "onshelfTransform";
        if (className.Contains("ToolRack", StringComparison.OrdinalIgnoreCase) || path.Contains("toolrack", StringComparison.OrdinalIgnoreCase)) return "toolrackTransform";
        if (className.Contains("Firepit", StringComparison.OrdinalIgnoreCase) || path.Contains("firepit", StringComparison.OrdinalIgnoreCase)) return "infirepitTransform";
        if (className.Contains("BasketTrap", StringComparison.OrdinalIgnoreCase) || path.Contains("trap", StringComparison.OrdinalIgnoreCase)) return "inTrapTransform";
        if (path.Contains("moldrack", StringComparison.OrdinalIgnoreCase)) return "onmoldrackTransform";
        if (path.Contains("omok", StringComparison.OrdinalIgnoreCase)) return "onOmokTransform";
        if (path.Contains("scrollrack", StringComparison.OrdinalIgnoreCase)) return "onscrollrackTransform";
        if (path.Contains("groundstorage", StringComparison.OrdinalIgnoreCase)) return "groundStorageTransform";

        return null;
    }

    internal bool TryGetActiveTransformGizmo(out ModelTransform transform, out TransformGizmoContext context, out BlockPos? blockPos)
    {
        return TryGetActiveTransformGizmo(out transform, out context, out blockPos, out _);
    }

    internal bool TryGetActiveTransformGizmo(out ModelTransform transform, out TransformGizmoContext context, out BlockPos? blockPos, out Vec3d? worldCenter)
    {
        return TryGetActiveTransformGizmo(out transform, out context, out blockPos, out worldCenter, out _);
    }

    internal bool TryGetActiveTransformGizmo(out ModelTransform transform, out TransformGizmoContext context, out BlockPos? blockPos, out Vec3d? worldCenter, out TransformGizmoAxes? worldAxes)
    {
        return TryGetActiveTransformGizmo(out transform, out context, out blockPos, out worldCenter, out worldAxes, out _);
    }

    internal bool TryGetActiveTransformGizmo(out ModelTransform transform, out TransformGizmoContext context, out BlockPos? blockPos, out Vec3d? worldCenter, out TransformGizmoAxes? worldAxes, out TransformGizmoAxes? parentAxes)
    {
        return TryGetActiveTransformGizmo(out transform, out context, out blockPos, out worldCenter, out worldAxes, out parentAxes, out _);
    }

    internal bool TryGetActiveTransformGizmo(out ModelTransform transform, out TransformGizmoContext context, out BlockPos? blockPos, out Vec3d? worldCenter, out TransformGizmoAxes? worldAxes, out TransformGizmoAxes? parentAxes, out TransformGizmoAxes? translationAxes)
    {
        transform = _activeGizmoTransform!;
        context = _activeGizmoContext;
        blockPos = _activeGizmoBlockPos;
        worldCenter = _activeGizmoWorldCenter;
        worldAxes = _activeGizmoWorldAxes;
        parentAxes = _activeGizmoParentAxes;
        translationAxes = _activeGizmoTranslationAxes;
        return _activeGizmoTransform != null;
    }

    internal void SetGizmoTranslation(float x, float y, float z)
    {
        if (_activeGizmoTransform == null) return;

        _activeGizmoTransform.Translation.X = x;
        _activeGizmoTransform.Translation.Y = y;
        _activeGizmoTransform.Translation.Z = z;
        _activeGizmoApply?.Invoke(_activeGizmoTransform);
    }

    internal void SetGizmoRotation(float x, float y, float z)
    {
        if (_activeGizmoTransform == null) return;

        _activeGizmoTransform.Rotation.X = x;
        _activeGizmoTransform.Rotation.Y = y;
        _activeGizmoTransform.Rotation.Z = z;
        _activeGizmoApply?.Invoke(_activeGizmoTransform);
    }

    internal void SetGizmoScale(float x, float y, float z)
    {
        if (_activeGizmoTransform == null) return;

        _activeGizmoTransform.ScaleXYZ.X = x;
        _activeGizmoTransform.ScaleXYZ.Y = y;
        _activeGizmoTransform.ScaleXYZ.Z = z;
        _activeGizmoApply?.Invoke(_activeGizmoTransform);
    }

    internal bool PointInsideDebugUi()
    {
        return ImGui.GetIO().WantCaptureMouse;
    }

    internal void NotifyActiveTransformGizmoDragStarted()
    {
        _activeGizmoDragStarted?.Invoke();
    }

    internal void NotifyActiveTransformGizmoDragEnded()
    {
        _activeGizmoDragEnded?.Invoke();
    }

    private void ClearActiveTransformGizmo()
    {
        _activeGizmoTransform = null;
        _activeGizmoApply = null;
        _activeGizmoDragStarted = null;
        _activeGizmoDragEnded = null;
        _activeGizmoBlockPos = null;
        _activeGizmoWorldCenter = null;
        _activeGizmoWorldAxes = null;
        _activeGizmoParentAxes = null;
        _activeGizmoTranslationAxes = null;
        _activeGizmoContext = TransformGizmoContext.Free;
    }

    private void DrawTransformGizmoControls(string id, ModelTransform transform, TransformGizmoContext context, Action<ModelTransform>? apply, BlockPos? blockPos = null, Vec3d? worldCenter = null, TransformGizmoAxes? worldAxes = null, TransformGizmoAxes? parentAxes = null, TransformGizmoAxes? translationAxes = null, bool allowMove = true, bool allowScale = true, bool allowRotate = true, Action? dragStarted = null, Action? dragEnded = null, bool registerActive = true)
    {
        ImGui.SeparatorText("Gizmo");

        if (GizmoMode == TransformGizmoMode.Move && !allowMove) GizmoMode = allowRotate ? TransformGizmoMode.Rotate : TransformGizmoMode.None;
        if (GizmoMode == TransformGizmoMode.Scale && !allowScale) GizmoMode = allowRotate ? TransformGizmoMode.Rotate : TransformGizmoMode.None;
        if (GizmoMode == TransformGizmoMode.Rotate && !allowRotate) GizmoMode = allowMove ? TransformGizmoMode.Move : TransformGizmoMode.None;

        if (allowMove)
        {
            if (ImGui.RadioButton($"Move##gizmo-mode-{id}", GizmoMode == TransformGizmoMode.Move)) GizmoMode = TransformGizmoMode.Move;
            ImGui.SameLine();
        }
        if (allowScale)
        {
            if (ImGui.RadioButton($"Scale##gizmo-mode-{id}", GizmoMode == TransformGizmoMode.Scale)) GizmoMode = TransformGizmoMode.Scale;
            ImGui.SameLine();
        }
        if (allowRotate)
        {
            if (ImGui.RadioButton($"Rotate##gizmo-mode-{id}", GizmoMode == TransformGizmoMode.Rotate)) GizmoMode = TransformGizmoMode.Rotate;
            ImGui.SameLine();
        }
        if (ImGui.RadioButton($"Off##gizmo-mode-{id}", GizmoMode == TransformGizmoMode.None)) GizmoMode = TransformGizmoMode.None;

        if (context != TransformGizmoContext.RigPart && GizmoSpace == TransformGizmoSpace.Parent)
        {
            GizmoSpace = TransformGizmoSpace.Local;
        }

        if (ImGui.RadioButton($"World axes##gizmo-space-world-{id}", GizmoSpace == TransformGizmoSpace.World)) GizmoSpace = TransformGizmoSpace.World;
        ImGui.SameLine();
        if (ImGui.RadioButton($"Local axes##gizmo-space-local-{id}", GizmoSpace == TransformGizmoSpace.Local)) GizmoSpace = TransformGizmoSpace.Local;
        ImGui.SameLine();
        if (context != TransformGizmoContext.RigPart) ImGui.BeginDisabled();
        if (ImGui.RadioButton($"Parent axes##gizmo-space-parent-{id}", GizmoSpace == TransformGizmoSpace.Parent)) GizmoSpace = TransformGizmoSpace.Parent;
        if (context != TransformGizmoContext.RigPart) ImGui.EndDisabled();
        ImGui.SameLine();
        bool snap = IncludeGizmoInIncrement;
        if (ImGui.Checkbox($"Snap drag##gizmo-snap-{id}", ref snap)) IncludeGizmoInIncrement = snap;

        float increment = TransformGizmoIncrement;
        ImGui.SetNextItemWidth(120);
        if (ImGui.DragFloat($"Increment##gizmo-increment-{id}", ref increment, 0.01f, 0.001f, 10f))
        {
            TransformGizmoIncrement = Math.Max(0.001f, increment);
        }

        ImGui.TextDisabled("Drag colored axes in-world.");

        if (!registerActive) return;

        _activeGizmoTransform = transform;
        _activeGizmoContext = context;
        _activeGizmoApply = apply;
        _activeGizmoDragStarted = dragStarted;
        _activeGizmoDragEnded = dragEnded;
        _activeGizmoBlockPos = blockPos;
        _activeGizmoWorldCenter = worldCenter;
        _activeGizmoWorldAxes = worldAxes;
        _activeGizmoParentAxes = parentAxes;
        _activeGizmoTranslationAxes = translationAxes;
    }

    private void SetEditorFrameOverride(PlayerItemFrame? frame)
    {
        InGameDevTools.StandaloneDevtoolsRuntime.EnsurePlayerAnimationBehaviors(_api);
        _behavior ??= _api.World.Player.Entity.GetBehavior<FirstPersonAnimationsBehavior>();
        ThirdPersonAnimationsBehavior? thirdPersonBehavior = _api.World.Player.Entity.GetBehavior<ThirdPersonAnimationsBehavior>();

        DebugPoseFreezeActive = frame != null;
        if (_behavior != null) _behavior.FrameOverride = frame;
        if (thirdPersonBehavior != null) thirdPersonBehavior.FrameOverride = frame;
    }

    private static TransformGizmoContext GetGizmoContextForTransformCode(string transformCode)
    {
        if (transformCode.Contains("tpOffHandTransform", StringComparison.OrdinalIgnoreCase)) return TransformGizmoContext.OffHand;
        if (transformCode.Contains("tpHandTransform", StringComparison.OrdinalIgnoreCase)) return TransformGizmoContext.MainHand;
        if (transformCode.Contains("onTongTransform", StringComparison.OrdinalIgnoreCase) ||
            transformCode.Contains("onMetalTongTransform", StringComparison.OrdinalIgnoreCase)) return TransformGizmoContext.MainHand;
        if (transformCode.Contains("groundTransform", StringComparison.OrdinalIgnoreCase) ||
            transformCode.Contains("groundStorageTransform", StringComparison.OrdinalIgnoreCase)) return TransformGizmoContext.Ground;
        if (transformCode.Contains("inForgeTransform", StringComparison.OrdinalIgnoreCase) ||
            transformCode.Contains("DisplayTransform", StringComparison.OrdinalIgnoreCase) ||
            transformCode.Contains("shelfTransform", StringComparison.OrdinalIgnoreCase) ||
            transformCode.Contains("toolrackTransform", StringComparison.OrdinalIgnoreCase) ||
            transformCode.Contains("rackTransform", StringComparison.OrdinalIgnoreCase)) return TransformGizmoContext.Display;

        return TransformGizmoContext.Free;
    }

    private void CreateAnimationGui()
    {
        ImGui.Indent();
        ImGui.SeparatorText("Just player");

        ImGui.InputText("Animation code##playeranimation", ref _playerAnimationKey, 300);

        bool canAddAnimation = !AnimationsManager._instance.Animations.ContainsKey(_playerAnimationKey) && _playerAnimationKey != "";
        if (!canAddAnimation) ImGui.BeginDisabled();
        if (ImGui.Button($"Create##playeranimation"))
        {
            AnimationsManager._instance.Animations.Add(_playerAnimationKey, new Animation(new PLayerKeyFrame[] { PLayerKeyFrame.Zero }));
        }
        if (!canAddAnimation) ImGui.EndDisabled();

        ImGui.SeparatorText("Item + Player");
        CreateFromItemAnimation();
        ImGui.Unindent();
    }
    private void CreateFromItemAnimation()
    {
        Item? item = _api.World.Player.Entity.RightHandItemSlot.Itemstack?.Item;
        if (item == null)
        {
            ImGui.Text("Take item in right hand");
            return;
        }

        Animatable? behavior = item.GetCollectibleBehavior(typeof(Animatable), true) as Animatable;
        if (behavior == null)
        {
            ImGui.Text("Take item with animatable behavior in right hand");
            return;
        }

        Shape? shape = behavior.CurrentShape;
        if (shape == null)
        {
            ImGui.Text("Take item with animatable behavior in right hand");
            return;
        }

        ImGui.InputText($"Item animation code", ref _itemAnimation, 300);
        ImGui.InputText($"New animation code", ref _animationKey, 300);

        bool canCreate = !AnimationsManager._instance.Animations.ContainsKey(_animationKey);

        if (!canCreate) ImGui.BeginDisabled();
        if (ImGui.Button("Create##itemanimation"))
        {
            try
            {
                AnimationsManager._instance.Animations.Add(_animationKey, new Animation(new PLayerKeyFrame[] { PLayerKeyFrame.Zero }, _itemAnimation, shape));
            }
            catch (Exception exception)
            {
                LoggerUtil.Warn(_api, this, $"Error on creating animation: {exception}");
            }
        }
        if (!canCreate) ImGui.EndDisabled();
    }
}
