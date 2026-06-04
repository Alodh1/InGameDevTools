using InGameDevTools.Colliders;
using InGameDevTools.MeleeSystems;
using InGameDevTools.Utils;
using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace InGameDevTools.Animations;

public sealed class AnimationsManager
{
    public Dictionary<string, Animation> Animations { get; private set; } = new();
    internal Dictionary<string, AnimationSource> AnimationSources { get; } = new();

    public AnimationsManager(ICoreClientAPI api, ParticleEffectsManager particleEffectsManager)
    {
        _instance = this;

        _api = api;
    }
    public void Load()
    {
        List<IAsset> configAnimations = _api.Assets.GetManyInCategory("config", "animations");

        Dictionary<string, Animation> animationsByCode = new();
        AnimationSources.Clear();
        IEnumerable<LoadedAnimation> loadedAnimations = configAnimations.SelectMany(FromAsset);

        foreach (LoadedAnimation loadedAnimation in loadedAnimations)
        {
            if (!animationsByCode.TryAdd(loadedAnimation.Code, loadedAnimation.Animation))
            {
                LoggerUtil.Warn(_api, this, $"Duplicate animation code '{loadedAnimation.Code}' from '{loadedAnimation.Source.DisplayPath}' ignored.");
                continue;
            }

            AnimationSources[loadedAnimation.Code] = loadedAnimation.Source;
        }

        Animations = animationsByCode;
    }

    public Animation? GetAnimation(string code, params string[] tags)
    {
        return GetAnimationRecursive(code, tags);
    }

    public Animation? GetAnimation(string code, EntityPlayer player, bool firstPerson = true)
    {
        return GetAnimationRecursive(code, GetTags(player, firstPerson));
    }
    
    public bool GetAnimation([NotNullWhen(true)] out Animation? animation, string code, EntityPlayer player, bool firstPerson = true)
    {
        animation = GetAnimationRecursive(code, GetTags(player, firstPerson));
        return animation != null;
    }

    public IEnumerable<string> GetTags(EntityPlayer player, bool firstPerson = true)
    {
        List<string> tags = [];

        if (!firstPerson)
        {
            tags.Add("tp");
        }

        float intoxication = player.WatchedAttributes.GetFloat("intoxication");

        if (intoxication > 0.1f)
        {
            tags.Add("drunk");
        }

        string modelPrefix = player.WatchedAttributes.GetString("skinModel", "").Replace(':', '-');
        if (modelPrefix != "")
        {
            tags.Add(modelPrefix);
        }

        return tags;
    }

    [Obsolete("Use one from DebugWindowManager")]
    public static void RegisterTransformByCode(ModelTransform transform, string code) => DebugWindowManager.RegisterTransformByCode(transform, code);
    [Obsolete("Use one from DebugWindowManager")]
    public void RegisterTransform(ModelTransform transform, string code) => DebugWindowManager._instance.RegisterTransform(transform, code);
    [Obsolete("Use one from DebugWindowManager")]
    public static void RegisterCollider(string item, string type, MeleeDamageType collider) => DebugWindowManager.RegisterCollider(item, type, collider);
    [Obsolete("Use one from DebugWindowManager")]
    public static void RegisterCollider(string item, string type, Action<LineSegmentCollider> setter, System.Func<LineSegmentCollider> getter) => DebugWindowManager.RegisterCollider(item, type, setter, getter);

    private readonly ICoreClientAPI _api;
    internal static AnimationsManager _instance = null!;

    private Animation? GetAnimationRecursive(string code, IEnumerable<string> tags)
    {
        foreach (string candidate in BuildAnimationCandidates(code, tags))
        {
            if (Animations.TryGetValue(candidate, out Animation? animation))
            {
                return animation;
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildAnimationCandidates(string code, IEnumerable<string> tags)
    {
        string[] tagArray = tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);

        foreach (string candidate in BuildTaggedCandidates(code, tagArray))
        {
            if (emitted.Add(candidate)) yield return candidate;
        }

        if (emitted.Add(code)) yield return code;
    }

    private static IEnumerable<string> BuildTaggedCandidates(string prefix, string[] remainingTags)
    {
        for (int index = 0; index < remainingTags.Length; index++)
        {
            string tag = remainingTags[index];
            string candidate = $"{prefix}-{tag}";
            string[] nextRemaining = remainingTags
                .Where((_, candidateIndex) => candidateIndex != index)
                .ToArray();

            foreach (string nested in BuildTaggedCandidates(candidate, nextRemaining))
            {
                yield return nested;
            }

            yield return candidate;
        }
    }

    private IEnumerable<LoadedAnimation> FromAsset(IAsset asset)
    {
        List<LoadedAnimation> result = new();

        string domain = asset.Location.Domain;

        JsonObject json;

        try
        {
            json = JsonObject.FromJson(asset.ToText());
        }
        catch (Exception exception)
        {
            LoggerUtil.Error(_api, this, $"Error on parsing animations file '{asset.Location}'.\nException: {exception}");
            return result;
        }

        if (json.Token is not JObject root)
        {
            LoggerUtil.Error(_api, this, $"Error on parsing animations file '{asset.Location}'. Expected a JSON object.");
            return result;
        }

        foreach (KeyValuePair<string, JToken?> entry in root)
        {
            string code = entry.Key;

            try
            {
                if (entry.Value == null)
                {
                    LoggerUtil.Error(_api, this, $"Error on parsing animation '{code}' from '{asset.Location}'. Animation entry is null.");
                    continue;
                }

                JsonObject animationJson = new(entry.Value);

                AnimationJson? animationDto = animationJson.AsObject<AnimationJson>();
                if (animationDto == null)
                {
                    LoggerUtil.Error(_api, this, $"Error on parsing animation '{code}' from '{asset.Location}'. Animation entry could not be deserialized.");
                    continue;
                }

                Animation animation = animationDto.ToAnimation();

                string animationCode = code.Contains(':') ? code : $"{domain}:{code}";

                result.Add(new(animationCode, animation, new(domain, code, AssetPath: asset.Location.Path)));
            }
            catch (Exception exception)
            {
                LoggerUtil.Error(_api, this, $"Error on parsing animation '{code}' from '{asset.Location}'.\nException: {exception}");
            }
        }

        return result;
    }

    private IEnumerable<LoadedAnimation> FromShapeAsset(IAsset asset)
    {
        List<LoadedAnimation> result = new();

        Shape shape;
        try
        {
            shape = asset.ToObject<Shape>();
            shape.ResolveReferences(_api.Logger, asset.Location.ToString());
        }
        catch (Exception exception)
        {
            LoggerUtil.Warn(_api, this, $"Error on parsing shape animations file '{asset.Location}'.\nException: {exception}");
            return result;
        }

        if (shape.Animations == null || shape.Animations.Length == 0)
        {
            return result;
        }

        foreach (Vintagestory.API.Common.Animation vanillaAnimation in shape.Animations)
        {
            if (string.IsNullOrWhiteSpace(vanillaAnimation.Code) || vanillaAnimation.KeyFrames == null || vanillaAnimation.KeyFrames.Length == 0)
            {
                continue;
            }

            try
            {
                string animationCode = BuildShapeAnimationCode(asset.Location, vanillaAnimation.Code);
                Animation animation = FromVanillaShapeAnimation(vanillaAnimation);
                AnimationSource source = new(
                    asset.Location.Domain,
                    vanillaAnimation.Code,
                    AnimationSourceKind.ShapeAnimation,
                    asset.Location.Path,
                    vanillaAnimation.Code);

                result.Add(new(animationCode, animation, source));
            }
            catch (Exception exception)
            {
                LoggerUtil.Warn(_api, this, $"Error on importing shape animation '{vanillaAnimation.Code}' from '{asset.Location}'.\nException: {exception}");
            }
        }

        return result;
    }

    private static Animation FromVanillaShapeAnimation(Vintagestory.API.Common.Animation vanillaAnimation)
    {
        List<PLayerKeyFrame> playerFrames = PLayerKeyFrame.FromVanillaAnimation(vanillaAnimation, out bool hasPlayerFrames);
        if (hasPlayerFrames)
        {
            return new(playerFrames);
        }

        List<ItemKeyFrame> itemFrames = ItemKeyFrame.FromVanillaAnimation(vanillaAnimation);
        return new(playerFrames, itemFrames);
    }

    private static string BuildShapeAnimationCode(AssetLocation location, string vanillaAnimationCode)
    {
        string shapePath = GetShapePathWithoutPrefixAndExtension(location.Path);
        return $"{location.Domain}:shape/{shapePath}/{vanillaAnimationCode}";
    }

    private static string GetShapePathWithoutPrefixAndExtension(string assetPath)
    {
        string path = assetPath.Replace('\\', '/');
        const string prefix = "shapes/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[prefix.Length..];
        }

        const string extension = ".json";
        if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^extension.Length];
        }

        return path;
    }

    private sealed record LoadedAnimation(string Code, Animation Animation, AnimationSource Source);
}

internal enum AnimationSourceKind
{
    ConfigAnimation,
    ShapeAnimation
}

internal sealed record AnimationSource(
    string Domain,
    string SourceKey,
    AnimationSourceKind Kind = AnimationSourceKind.ConfigAnimation,
    string? AssetPath = null,
    string? ShapeAnimationCode = null)
{
    public string DisplayPath => Kind == AnimationSourceKind.ShapeAnimation && AssetPath != null
        ? $"{Domain}:{AssetPath}#{ShapeAnimationCode}"
        : $"{Domain}:{SourceKey}";
}
