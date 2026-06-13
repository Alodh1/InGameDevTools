using Newtonsoft.Json.Linq;
using Vintagestory.API.Datastructures;

namespace InGameDevTools.Utils;

internal static class DevToolsBlockAnimationSetup
{
    public const string EntityClassName = "InGameDevToolsAnimatedBlock";
    public const string BehaviorName = "InGameDevTools:AnimatedBlock";

    public static DevToolsBlockAnimationSetupResult Apply(JObject root, string animationCode)
    {
        if (root == null) throw new ArgumentNullException(nameof(root));

        string code = string.IsNullOrWhiteSpace(animationCode) ? "new-animation" : animationCode.Trim();
        string entityClass = root["entityClass"]?.ToString() ?? "";
        if (!string.IsNullOrWhiteSpace(entityClass) &&
            !string.Equals(entityClass, EntityClassName, StringComparison.OrdinalIgnoreCase))
        {
            return DevToolsBlockAnimationSetupResult.Blocked(
                $"Block already uses entityClass '{entityClass}'. Shape animation was created, but placed playback setup was not changed.");
        }

        bool changed = false;
        if (!string.Equals(entityClass, EntityClassName, StringComparison.Ordinal))
        {
            root["entityClass"] = EntityClassName;
            changed = true;
        }

        JArray behaviors = root["entityBehaviors"] as JArray ?? [];
        if (root["entityBehaviors"] is not JArray)
        {
            root["entityBehaviors"] = behaviors;
            changed = true;
        }

        JObject? behavior = FindBehavior(behaviors, BehaviorName);
        if (behavior == null)
        {
            behavior = new JObject
            {
                ["name"] = BehaviorName
            };
            behaviors.Add(behavior);
            changed = true;
        }
        else if (!string.Equals(behavior["name"]?.ToString(), BehaviorName, StringComparison.Ordinal))
        {
            behavior["name"] = BehaviorName;
            changed = true;
        }

        JObject properties = behavior["properties"] as JObject ?? [];
        if (behavior["properties"] is not JObject)
        {
            behavior["properties"] = properties;
            changed = true;
        }

        changed |= SetIfDifferent(properties, "animation", code);
        changed |= SetIfDifferent(properties, "speed", 1f);
        changed |= SetIfDifferent(properties, "loop", true);

        return DevToolsBlockAnimationSetupResult.Applied(changed, $"Added placed block playback setup for '{code}'.");
    }

    public static DevToolsAnimatedBlockConfig ParseConfig(JObject? properties, string? fallbackAnimationCode = null)
    {
        string animation = properties?["animation"]?.ToString() ?? fallbackAnimationCode ?? "";
        float speed = TryReadFloat(properties?["speed"], 1f);
        bool loop = TryReadBool(properties?["loop"], true);
        return new(animation.Trim(), Math.Clamp(speed, 0.001f, 100f), loop);
    }

    public static DevToolsAnimatedBlockConfig ParseConfig(JsonObject properties, string? fallbackAnimationCode = null)
    {
        string animation = properties["animation"].AsString(fallbackAnimationCode ?? "");
        float speed = properties["speed"].AsFloat(1f);
        bool loop = properties["loop"].AsBool(true);
        return new(animation.Trim(), Math.Clamp(speed, 0.001f, 100f), loop);
    }

    private static JObject? FindBehavior(JArray behaviors, string name)
    {
        foreach (JToken token in behaviors)
        {
            if (token is not JObject behavior) continue;
            string behaviorName = behavior["name"]?.ToString() ?? behavior["code"]?.ToString() ?? "";
            if (string.Equals(behaviorName, name, StringComparison.OrdinalIgnoreCase))
            {
                return behavior;
            }
        }

        return null;
    }

    private static bool SetIfDifferent(JObject obj, string propertyName, string value)
    {
        if (string.Equals(obj[propertyName]?.ToString(), value, StringComparison.Ordinal)) return false;
        obj[propertyName] = value;
        return true;
    }

    private static bool SetIfDifferent(JObject obj, string propertyName, float value)
    {
        if (obj[propertyName]?.Type is JTokenType.Float or JTokenType.Integer &&
            Math.Abs(obj[propertyName]!.Value<float>() - value) < 0.0001f)
        {
            return false;
        }

        obj[propertyName] = value;
        return true;
    }

    private static bool SetIfDifferent(JObject obj, string propertyName, bool value)
    {
        if (obj[propertyName]?.Type == JTokenType.Boolean && obj[propertyName]!.Value<bool>() == value) return false;
        obj[propertyName] = value;
        return true;
    }

    private static float TryReadFloat(JToken? token, float fallback)
    {
        if (token == null) return fallback;
        return token.Type is JTokenType.Float or JTokenType.Integer && float.TryParse(token.ToString(), out float value)
            ? value
            : fallback;
    }

    private static bool TryReadBool(JToken? token, bool fallback)
    {
        if (token == null) return fallback;
        if (token.Type == JTokenType.Boolean) return token.Value<bool>();
        return bool.TryParse(token.ToString(), out bool value) ? value : fallback;
    }
}

internal readonly record struct DevToolsBlockAnimationSetupResult(bool Success, bool Changed, string Status)
{
    public static DevToolsBlockAnimationSetupResult Applied(bool changed, string status) => new(true, changed, status);
    public static DevToolsBlockAnimationSetupResult Blocked(string status) => new(false, false, status);
}

internal readonly record struct DevToolsAnimatedBlockConfig(string AnimationCode, float Speed, bool Loop)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(AnimationCode);
}
