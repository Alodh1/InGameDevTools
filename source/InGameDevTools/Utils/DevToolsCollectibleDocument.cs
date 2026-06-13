using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InGameDevTools.Utils;

internal enum DevToolsCollectibleKind
{
    Block,
    Item
}

internal enum DevToolsCollectibleIssueSeverity
{
    Warning,
    Error
}

internal sealed record DevToolsCollectibleValidationIssue(DevToolsCollectibleIssueSeverity Severity, string Message);

internal sealed class DevToolsCollectibleDocumentDraft
{
    private static readonly Regex VariantPlaceholderPattern = new(@"\{(?<code>[^{}]+)\}", RegexOptions.Compiled);

    public DevToolsCollectibleKind Kind { get; set; }
    public string Domain { get; set; } = "game";
    public string AssetPath { get; set; } = "blocktypes/unknown.json";
    public JObject Root { get; set; } = [];

    public static DevToolsCollectibleDocumentDraft FromJson(string text, DevToolsCollectibleKind kind, string domain, string assetPath)
    {
        JObject root = JObject.Parse(text);
        return new()
        {
            Kind = kind,
            Domain = SanitizeDomain(domain),
            AssetPath = string.IsNullOrWhiteSpace(assetPath) ? NormalizeAssetPath(kind, assetPath, root["code"]?.ToString()) : assetPath.Replace('\\', '/').Trim(),
            Root = root
        };
    }

    public static DevToolsCollectibleDocumentDraft Empty(DevToolsCollectibleKind kind, string domain, string code)
    {
        string cleanCode = string.IsNullOrWhiteSpace(code) ? "unknown" : code.Trim();
        JObject root = new()
        {
            ["code"] = cleanCode.Contains(':') ? cleanCode[(cleanCode.IndexOf(':') + 1)..] : cleanCode
        };
        return new()
        {
            Kind = kind,
            Domain = SanitizeDomain(domain),
            AssetPath = NormalizeAssetPath(kind, "", cleanCode),
            Root = root
        };
    }

    public string ToJson()
    {
        return JsonConvert.SerializeObject(Root, Formatting.Indented);
    }

    public string BuildAssetRelativePath()
    {
        return Path.Combine("assets", SanitizeDomain(Domain), NormalizeAssetPath(Kind, AssetPath, Root["code"]?.ToString()).Replace('/', Path.DirectorySeparatorChar));
    }

    public IReadOnlyList<DevToolsCollectibleVariantGroupDraft> GetVariantGroups()
    {
        List<DevToolsCollectibleVariantGroupDraft> groups = [];
        if (Root["variantgroups"] is not JArray array) return groups;

        foreach (JToken token in array)
        {
            if (token is JObject obj)
            {
                groups.Add(DevToolsCollectibleVariantGroupDraft.FromJson(obj));
            }
        }

        return groups;
    }

    public void SetVariantGroups(IEnumerable<DevToolsCollectibleVariantGroupDraft> groups)
    {
        JArray array = [];
        foreach (DevToolsCollectibleVariantGroupDraft group in groups)
        {
            array.Add(group.ToJson());
        }

        if (array.Count == 0) Root.Remove("variantgroups");
        else Root["variantgroups"] = array;
    }

    public IReadOnlyList<DevToolsCollectibleBehaviorDraft> GetBehaviors()
    {
        List<DevToolsCollectibleBehaviorDraft> behaviors = [];
        if (Root["behaviors"] is not JArray array) return behaviors;

        foreach (JToken token in array)
        {
            if (token is JObject obj)
            {
                behaviors.Add(DevToolsCollectibleBehaviorDraft.FromJson(obj));
            }
        }

        return behaviors;
    }

    public void SetBehaviors(IEnumerable<DevToolsCollectibleBehaviorDraft> behaviors)
    {
        JArray array = [];
        foreach (DevToolsCollectibleBehaviorDraft behavior in behaviors)
        {
            array.Add(behavior.ToJson());
        }

        if (array.Count == 0) Root.Remove("behaviors");
        else Root["behaviors"] = array;
    }

    public IReadOnlyList<DevToolsCollectibleDropDraft> GetDrops()
    {
        List<DevToolsCollectibleDropDraft> drops = [];
        if (Root["drops"] is not JArray array) return drops;

        foreach (JToken token in array)
        {
            if (token is JObject obj)
            {
                drops.Add(DevToolsCollectibleDropDraft.FromJson(obj));
            }
        }

        return drops;
    }

    public void SetDrops(IEnumerable<DevToolsCollectibleDropDraft> drops)
    {
        JArray array = [];
        foreach (DevToolsCollectibleDropDraft drop in drops)
        {
            array.Add(drop.ToJson());
        }

        if (array.Count == 0) Root.Remove("drops");
        else Root["drops"] = array;
    }

    public Dictionary<string, JToken> GetTextures()
    {
        Dictionary<string, JToken> textures = new(StringComparer.OrdinalIgnoreCase);
        if (Root["textures"] is not JObject obj) return textures;

        foreach (JProperty property in obj.Properties())
        {
            textures[property.Name] = property.Value.DeepClone();
        }

        return textures;
    }

    public void SetTextures(IEnumerable<KeyValuePair<string, JToken>> textures)
    {
        JObject obj = [];
        foreach (KeyValuePair<string, JToken> texture in textures)
        {
            if (string.IsNullOrWhiteSpace(texture.Key)) continue;
            obj[texture.Key.Trim()] = texture.Value.DeepClone();
        }

        if (!obj.HasValues) Root.Remove("textures");
        else Root["textures"] = obj;
    }

    public bool TryGetAttribute(IEnumerable<string> path, out JToken? value)
    {
        value = Root["attributes"];
        foreach (string segment in path)
        {
            if (value is not JObject obj || !obj.TryGetValue(segment, StringComparison.OrdinalIgnoreCase, out value))
            {
                value = null;
                return false;
            }
        }

        return value != null;
    }

    public void SetAttribute(IEnumerable<string> path, object? value)
    {
        SetAttribute(path, value is JToken token ? token : value == null ? JValue.CreateNull() : JToken.FromObject(value));
    }

    public void SetAttribute(IEnumerable<string> path, JToken value)
    {
        string[] parts = path.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part.Trim()).ToArray();
        if (parts.Length == 0) return;

        JObject attributes = Root["attributes"] is JObject existing ? (JObject)existing.DeepClone() : new JObject();
        JObject current = attributes;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            if (current[parts[index]] is not JObject next)
            {
                next = new JObject();
                current[parts[index]] = next;
            }

            current = next;
        }

        current[parts[^1]] = value.DeepClone();
        Root["attributes"] = attributes;
    }

    public List<string> ExpandVariantCodes(int limit = 512)
    {
        string template = Root["code"]?.ToString() ?? "";
        List<Match> placeholders = VariantPlaceholderPattern.Matches(template).Cast<Match>().ToList();
        if (placeholders.Count == 0) return string.IsNullOrWhiteSpace(template) ? [] : [template];

        Dictionary<string, List<string>> statesByCode = GetVariantGroups()
            .Where(group => !string.IsNullOrWhiteSpace(group.Code))
            .GroupBy(group => group.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().States.Where(state => !string.IsNullOrWhiteSpace(state)).ToList(), StringComparer.OrdinalIgnoreCase);

        List<string> results = [template];
        foreach (Match placeholder in placeholders)
        {
            string code = placeholder.Groups["code"].Value;
            if (!statesByCode.TryGetValue(code, out List<string>? states) || states.Count == 0)
            {
                states = [$"{{{code}}}"];
            }

            List<string> nextResults = [];
            foreach (string current in results)
            {
                foreach (string state in states)
                {
                    nextResults.Add(current.Replace($"{{{code}}}", state, StringComparison.Ordinal));
                    if (nextResults.Count >= limit) break;
                }

                if (nextResults.Count >= limit) break;
            }

            results = nextResults;
            if (results.Count >= limit) break;
        }

        return results;
    }

    public List<string> FindByTypeMatches(string collectibleCode)
    {
        List<string> matches = [];
        if (Root["byType"] is not JObject byType) return matches;

        string path = collectibleCode.Contains(':') ? collectibleCode[(collectibleCode.IndexOf(':') + 1)..] : collectibleCode;
        foreach (JProperty property in byType.Properties())
        {
            if (WildcardMatches(property.Name, path))
            {
                matches.Add(property.Name);
            }
        }

        return matches;
    }

    public IReadOnlyList<DevToolsCollectibleValidationIssue> Validate(
        Func<string, bool>? shapeExists = null,
        Func<string, bool>? textureExists = null,
        Func<string, bool>? stackExists = null)
    {
        List<DevToolsCollectibleValidationIssue> issues = [];
        string code = Root["code"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(code))
        {
            issues.Add(Error("Missing required code."));
        }

        try
        {
            _ = NormalizeAssetPath(Kind, AssetPath, code);
        }
        catch (Exception exception)
        {
            issues.Add(Error($"Invalid save path: {exception.Message}"));
        }

        if (Root["shape"] is JToken shapeToken)
        {
            if (shapeToken is not JObject shape)
            {
                issues.Add(Error("shape must be an object."));
            }
            else
            {
                string shapeBase = shape["base"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(shapeBase))
                {
                    issues.Add(Warning("shape.base is empty."));
                }
                else if (shapeExists != null && !shapeExists(shapeBase))
                {
                    issues.Add(Error($"shape.base does not resolve: {shapeBase}"));
                }
            }
        }

        if (Root["textures"] is JToken texturesToken)
        {
            if (texturesToken is not JObject textures)
            {
                issues.Add(Error("textures must be an object."));
            }
            else if (textureExists != null)
            {
                foreach (JProperty texture in textures.Properties())
                {
                    foreach (string reference in ExtractTextureReferences(texture.Value))
                    {
                        if (!textureExists(reference))
                        {
                            issues.Add(Error($"Texture '{texture.Name}' does not resolve: {reference}"));
                        }
                    }
                }
            }
        }

        ValidateVariantGroups(issues);
        ValidateBehaviors(issues);
        ValidateDrops(issues, stackExists);

        if (Root["byType"] is JToken byType && byType is not JObject)
        {
            issues.Add(Error("byType must be an object."));
        }

        if (Root["attributes"] is JToken attributes && attributes is not JObject)
        {
            issues.Add(Error("attributes must be an object."));
        }

        return issues;
    }

    public static string NormalizeAssetPath(DevToolsCollectibleKind kind, string path, string? code)
    {
        string folder = kind == DevToolsCollectibleKind.Block ? "blocktypes" : "itemtypes";
        string normalized = string.IsNullOrWhiteSpace(path)
            ? $"{folder}/{CodeToFileName(code)}.json"
            : path.Replace('\\', '/').Trim().TrimStart('/');

        if (normalized.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = normalized.Split('/', 4, StringSplitOptions.RemoveEmptyEntries);
            normalized = parts.Length >= 4 ? parts[3] : "";
        }

        if (!normalized.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"{folder}/{normalized}";
        }

        List<string> partsOut = [];
        foreach (string part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                throw new InvalidOperationException("path traversal is not allowed");
            }

            string sanitized = SanitizePathPart(part);
            if (!string.IsNullOrWhiteSpace(sanitized)) partsOut.Add(sanitized);
        }

        if (partsOut.Count == 0 || !string.Equals(partsOut[0], folder, StringComparison.OrdinalIgnoreCase))
        {
            partsOut.Insert(0, folder);
        }

        string result = string.Join('/', partsOut);
        return result.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? result : result + ".json";
    }

    public static string SanitizeDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return "game";
        return SanitizePathPart(domain.Trim().ToLowerInvariant());
    }

    private void ValidateVariantGroups(List<DevToolsCollectibleValidationIssue> issues)
    {
        if (Root["variantgroups"] == null) return;
        if (Root["variantgroups"] is not JArray groups)
        {
            issues.Add(Error("variantgroups must be an array."));
            return;
        }

        HashSet<string> groupCodes = new(StringComparer.OrdinalIgnoreCase);
        foreach (JToken token in groups)
        {
            if (token is not JObject obj)
            {
                issues.Add(Error("variantgroups entries must be objects."));
                continue;
            }

            string groupCode = obj["code"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(groupCode))
            {
                issues.Add(Error("variantgroup is missing code."));
            }
            else if (!groupCodes.Add(groupCode))
            {
                issues.Add(Error($"Duplicate variantgroup code: {groupCode}"));
            }

            if (obj["states"] is not JArray states)
            {
                issues.Add(Error($"variantgroup '{groupCode}' is missing states array."));
                continue;
            }

            HashSet<string> stateValues = new(StringComparer.OrdinalIgnoreCase);
            foreach (JToken state in states)
            {
                string value = state.ToString();
                if (string.IsNullOrWhiteSpace(value)) issues.Add(Error($"variantgroup '{groupCode}' has an empty state."));
                else if (!stateValues.Add(value)) issues.Add(Error($"variantgroup '{groupCode}' has duplicate state: {value}"));
            }
        }
    }

    private void ValidateBehaviors(List<DevToolsCollectibleValidationIssue> issues)
    {
        if (Root["behaviors"] == null) return;
        if (Root["behaviors"] is not JArray behaviors)
        {
            issues.Add(Error("behaviors must be an array."));
            return;
        }

        for (int index = 0; index < behaviors.Count; index++)
        {
            if (behaviors[index] is not JObject obj)
            {
                issues.Add(Error($"behavior #{index + 1} must be an object."));
                continue;
            }

            string name = obj["name"]?.ToString() ?? obj["code"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                issues.Add(Error($"behavior #{index + 1} is missing name."));
            }
        }
    }

    private void ValidateDrops(List<DevToolsCollectibleValidationIssue> issues, Func<string, bool>? stackExists)
    {
        if (Root["drops"] == null) return;
        if (Root["drops"] is not JArray drops)
        {
            issues.Add(Error("drops must be an array."));
            return;
        }

        for (int index = 0; index < drops.Count; index++)
        {
            if (drops[index] is not JObject obj)
            {
                issues.Add(Error($"drop #{index + 1} must be an object."));
                continue;
            }

            string code = obj["code"]?.ToString() ?? obj["resolvedItemstack"]?["code"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(code))
            {
                issues.Add(Error($"drop #{index + 1} is missing code."));
            }
            else if (stackExists != null && !stackExists(code))
            {
                issues.Add(Error($"drop #{index + 1} stack does not resolve: {code}"));
            }
        }
    }

    private static IEnumerable<string> ExtractTextureReferences(JToken token)
    {
        if (token.Type == JTokenType.String)
        {
            string value = token.ToString();
            if (!string.IsNullOrWhiteSpace(value)) yield return value;
            yield break;
        }

        if (token is JObject obj)
        {
            string baseRef = obj["base"]?.ToString() ?? obj["path"]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(baseRef)) yield return baseRef;
        }
    }

    private static DevToolsCollectibleValidationIssue Error(string message) => new(DevToolsCollectibleIssueSeverity.Error, message);

    private static DevToolsCollectibleValidationIssue Warning(string message) => new(DevToolsCollectibleIssueSeverity.Warning, message);

    private static bool WildcardMatches(string pattern, string value)
    {
        if (string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase)) return true;
        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string CodeToFileName(string? code)
    {
        string value = string.IsNullOrWhiteSpace(code) ? "unknown" : code.Trim();
        if (value.Contains(':')) value = value[(value.IndexOf(':') + 1)..];
        return value.Replace('\\', '/').Trim('/').Replace('/', '-');
    }

    private static string SanitizePathPart(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}

internal sealed class DevToolsCollectibleVariantGroupDraft
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "states", "loadFromProperties"
    };

    public string Code { get; set; } = "";
    public List<string> States { get; } = [];
    public string LoadFromProperties { get; set; } = "";
    public JObject Extra { get; set; } = [];

    public static DevToolsCollectibleVariantGroupDraft FromJson(JObject json)
    {
        DevToolsCollectibleVariantGroupDraft group = new()
        {
            Code = json["code"]?.ToString() ?? "",
            LoadFromProperties = json["loadFromProperties"]?.ToString() ?? ""
        };

        if (json["states"] is JArray states)
        {
            foreach (JToken state in states)
            {
                group.States.Add(state.ToString());
            }
        }

        foreach (JProperty property in json.Properties())
        {
            if (!KnownKeys.Contains(property.Name))
            {
                group.Extra[property.Name] = property.Value.DeepClone();
            }
        }

        return group;
    }

    public JObject ToJson()
    {
        JObject json = (JObject)Extra.DeepClone();
        if (!string.IsNullOrWhiteSpace(Code)) json["code"] = Code.Trim();
        if (!string.IsNullOrWhiteSpace(LoadFromProperties)) json["loadFromProperties"] = LoadFromProperties.Trim();
        JArray states = [];
        foreach (string state in States.Where(state => !string.IsNullOrWhiteSpace(state)))
        {
            states.Add(state.Trim());
        }

        json["states"] = states;
        return json;
    }
}

internal sealed class DevToolsCollectibleBehaviorDraft
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "code"
    };

    public string Name { get; set; } = "";
    public JObject Extra { get; set; } = [];

    public static DevToolsCollectibleBehaviorDraft FromJson(JObject json)
    {
        DevToolsCollectibleBehaviorDraft behavior = new()
        {
            Name = json["name"]?.ToString() ?? json["code"]?.ToString() ?? ""
        };

        foreach (JProperty property in json.Properties())
        {
            if (!KnownKeys.Contains(property.Name))
            {
                behavior.Extra[property.Name] = property.Value.DeepClone();
            }
        }

        return behavior;
    }

    public JObject ToJson()
    {
        JObject json = (JObject)Extra.DeepClone();
        if (!string.IsNullOrWhiteSpace(Name)) json["name"] = Name.Trim();
        return json;
    }

    public DevToolsCollectibleBehaviorDraft Clone()
    {
        return new()
        {
            Name = Name,
            Extra = (JObject)Extra.DeepClone()
        };
    }
}

internal sealed class DevToolsCollectibleDropDraft
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "code", "quantity", "quantityAvg", "quantityVar"
    };

    public string Type { get; set; } = "item";
    public string Code { get; set; } = "";
    public string QuantityJson { get; set; } = "1";
    public JObject Extra { get; set; } = [];

    public static DevToolsCollectibleDropDraft FromJson(JObject json)
    {
        JToken? quantity = json["quantity"] ?? json["quantityAvg"] ?? json["quantityVar"];
        DevToolsCollectibleDropDraft drop = new()
        {
            Type = json["type"]?.ToString() ?? "item",
            Code = json["code"]?.ToString() ?? json["resolvedItemstack"]?["code"]?.ToString() ?? "",
            QuantityJson = quantity == null ? "1" : JsonConvert.SerializeObject(quantity, Formatting.Indented)
        };

        foreach (JProperty property in json.Properties())
        {
            if (!KnownKeys.Contains(property.Name))
            {
                drop.Extra[property.Name] = property.Value.DeepClone();
            }
        }

        return drop;
    }

    public JObject ToJson()
    {
        JObject json = (JObject)Extra.DeepClone();
        if (!string.IsNullOrWhiteSpace(Type)) json["type"] = Type.Trim();
        if (!string.IsNullOrWhiteSpace(Code)) json["code"] = Code.Trim();
        if (TryParseQuantity(QuantityJson, out JToken? quantity))
        {
            json["quantity"] = quantity!;
        }

        return json;
    }

    public DevToolsCollectibleDropDraft Clone()
    {
        return new()
        {
            Type = Type,
            Code = Code,
            QuantityJson = QuantityJson,
            Extra = (JObject)Extra.DeepClone()
        };
    }

    private static bool TryParseQuantity(string text, out JToken? quantity)
    {
        quantity = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (DevToolsJson.TryParseToken(text, useVintageStoryFallback: false) is JToken token)
        {
            quantity = token;
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            quantity = number;
            return true;
        }

        return false;
    }
}
