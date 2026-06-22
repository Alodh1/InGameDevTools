using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InGameDevTools.Utils;

internal enum DevToolsConfigLibIssueSeverity
{
    Warning,
    Error
}

internal sealed record DevToolsConfigLibValidationIssue(DevToolsConfigLibIssueSeverity Severity, string Message);

internal sealed class DevToolsConfigLibDocumentDraft
{
    private static readonly HashSet<string> RootKnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "version", "settings", "formatting"
    };

    public string Domain { get; set; } = "generatedconfig";
    public string RelativePath { get; set; } = "config/configlib-patches.json";
    public string ModConfigRelativePath { get; set; } = "generatedconfig.json";
    public string CSharpNamespace { get; set; } = "GeneratedConfig";
    public string ConfigClassName { get; set; } = "GeneratedConfigConfig";
    public string LoaderClassName { get; set; } = "GeneratedConfigLoader";
    public string CurrentPropertyName { get; set; } = "Current";
    public int Version { get; set; }
    public List<DevToolsConfigLibSettingDraft> Settings { get; } = [];
    public List<DevToolsConfigLibFormattingDraft> Formatting { get; } = [];
    public JObject Extra { get; set; } = [];
    public JToken ModConfigRoot { get; set; } = new JObject();

    public static DevToolsConfigLibDocumentDraft Empty(string domain = "generatedconfig")
    {
        string sanitizedDomain = SanitizeDomain(domain);
        string csharpStem = ToPascalIdentifier(sanitizedDomain, "GeneratedConfig");
        return new()
        {
            Domain = sanitizedDomain,
            RelativePath = "config/configlib-patches.json",
            ModConfigRelativePath = $"{sanitizedDomain}.json",
            CSharpNamespace = csharpStem,
            ConfigClassName = $"{csharpStem}Config",
            LoaderClassName = $"{csharpStem}ConfigLoader"
        };
    }

    public static DevToolsConfigLibDocumentDraft Scratch(string domain = "generatedconfig")
    {
        DevToolsConfigLibDocumentDraft document = Empty(domain);
        DevToolsConfigLibSettingDraft setting = DevToolsConfigLibSettingDraft.FromInferred("enabled", "boolean", new JValue(true));
        setting.Title = "Enabled";
        setting.Comment = "Example setting generated for a new scratch config.";
        setting.Weight = 1;
        document.Settings.Add(setting);
        return document;
    }

    public static DevToolsConfigLibDocumentDraft FromPatchJson(string text, string domain, string relativePath)
    {
        JObject root = JObject.Parse(text);
        DevToolsConfigLibDocumentDraft document = new()
        {
            Domain = SanitizeDomain(domain),
            RelativePath = NormalizeRelativePath(relativePath, "config/configlib-patches.json"),
            Version = root["version"]?.Value<int?>() ?? 0
        };
        document.ApplyCSharpDefaultsFromDomain();

        foreach (JProperty property in root.Properties())
        {
            if (!RootKnownKeys.Contains(property.Name))
            {
                document.Extra[property.Name] = property.Value.DeepClone();
            }
        }

        if (root["settings"] is JArray settings)
        {
            foreach (JToken token in settings)
            {
                if (token is JObject setting)
                {
                    document.Settings.Add(DevToolsConfigLibSettingDraft.FromJson(setting));
                }
            }
        }

        if (root["formatting"] is JArray formatting)
        {
            foreach (JToken token in formatting)
            {
                if (token is JObject row)
                {
                    document.Formatting.Add(DevToolsConfigLibFormattingDraft.FromJson(row));
                }
            }
        }

        document.ModConfigRelativePath = $"{document.Domain}.json";
        return document;
    }

    public static DevToolsConfigLibDocumentDraft FromModConfig(JToken root, string modConfigRelativePath, string domain)
    {
        DevToolsConfigLibDocumentDraft document = Empty(domain);
        document.ModConfigRelativePath = NormalizeRelativePath(modConfigRelativePath, $"{document.Domain}.json");
        document.ModConfigRoot = root.DeepClone();

        foreach (DevToolsConfigLibSettingDraft setting in InferSettings(root))
        {
            document.Settings.Add(setting);
        }

        GenerateSeparators(document);
        return document;
    }

    public void MergeFromModConfig(JToken root, string modConfigRelativePath)
    {
        ModConfigRoot = root.DeepClone();
        ModConfigRelativePath = NormalizeRelativePath(modConfigRelativePath, ModConfigRelativePath);
        Dictionary<string, DevToolsConfigLibSettingDraft> existing = Settings
            .Where(setting => !string.IsNullOrWhiteSpace(setting.Code))
            .GroupBy(setting => setting.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (DevToolsConfigLibSettingDraft inferred in InferSettings(root))
        {
            if (existing.TryGetValue(inferred.Code, out DevToolsConfigLibSettingDraft? current))
            {
                current.Type = inferred.Type;
                current.DefaultJson = inferred.DefaultJson;
                if (string.IsNullOrWhiteSpace(current.Name)) current.Name = inferred.Name;
                if (string.IsNullOrWhiteSpace(current.Title)) current.Title = inferred.Title;
                continue;
            }

            Settings.Add(inferred);
        }
    }

    public string ToPatchJson(bool includeDisabled = false)
    {
        JObject root = (JObject)Extra.DeepClone();
        root["version"] = Math.Max(0, Version);
        JArray settings = [];
        foreach (DevToolsConfigLibSettingDraft setting in Settings)
        {
            if (!includeDisabled && !setting.Enabled) continue;
            settings.Add(setting.ToJson());
        }

        root["settings"] = settings;

        JArray formatting = [];
        foreach (DevToolsConfigLibFormattingDraft row in Formatting)
        {
            if (!includeDisabled && !row.Enabled) continue;
            formatting.Add(row.ToJson());
        }

        if (formatting.Count > 0)
        {
            root["formatting"] = formatting;
        }
        else
        {
            root.Remove("formatting");
        }

        return JsonConvert.SerializeObject(root, Newtonsoft.Json.Formatting.Indented);
    }

    public string ToModConfigJson(bool includedOnly)
    {
        if (!includedOnly && ModConfigRoot.HasValues)
        {
            return JsonConvert.SerializeObject(ModConfigRoot, Newtonsoft.Json.Formatting.Indented);
        }

        JToken root = new JObject();
        foreach (DevToolsConfigLibSettingDraft setting in Settings.Where(setting => setting.Enabled))
        {
            if (string.IsNullOrWhiteSpace(setting.Code)) continue;
            if (!setting.TryGetDefaultToken(out JToken? value, out _)) continue;
            SetTokenAtPath(ref root, setting.Code.Split('/', StringSplitOptions.RemoveEmptyEntries), value!.DeepClone());
        }

        return JsonConvert.SerializeObject(root, Newtonsoft.Json.Formatting.Indented);
    }

    public string BuildOrderSummary()
    {
        List<string> lines = [];
        foreach (DevToolsConfigLibFormattingDraft row in Formatting.Where(row => row.Enabled).OrderBy(row => row.Weight))
        {
            lines.Add($"{row.Weight.ToString("0.###", CultureInfo.InvariantCulture)}  [{row.Type}]  {row.Title}");
        }

        foreach (DevToolsConfigLibSettingDraft setting in Settings.Where(setting => setting.Enabled).OrderBy(setting => setting.Weight))
        {
            lines.Add($"{setting.Weight.ToString("0.###", CultureInfo.InvariantCulture)}  {setting.Code}  [{setting.Type}]  {setting.Title}");
        }

        return lines.Count == 0 ? "No enabled ConfigLib rows." : string.Join(Environment.NewLine, lines);
    }

    public string ToCSharpLoaderCode()
    {
        CSharpConfigNode root = BuildCSharpConfigNode(out bool needsJsonLinq, out _);
        string namespaceName = SanitizeNamespace(CSharpNamespace, "GeneratedConfig");
        string configClass = SanitizeCSharpTypeName(ConfigClassName, "GeneratedConfigConfig");
        string loaderClass = SanitizeCSharpTypeName(LoaderClassName, "GeneratedConfigLoader");
        string currentProperty = SanitizeCSharpPropertyName(CurrentPropertyName, "Current");
        string fileName = NormalizeRelativePath(ModConfigRelativePath, $"{SanitizeDomain(Domain)}.json");

        StringBuilder sb = new();
        if (needsJsonLinq)
        {
            sb.AppendLine("using Newtonsoft.Json.Linq;");
        }

        sb.AppendLine("using Vintagestory.API.Common;");
        sb.AppendLine();
        sb.AppendLine($"namespace {namespaceName};");
        sb.AppendLine();
        AppendCSharpConfigClass(sb, root, configClass, 0);
        sb.AppendLine();
        sb.AppendLine($"public static class {loaderClass}");
        sb.AppendLine("{");
        sb.AppendLine($"    public const string FileName = {ToCSharpStringLiteral(fileName)};");
        sb.AppendLine($"    public static {configClass} {currentProperty} {{ get; private set; }} = new();");
        sb.AppendLine();
        sb.AppendLine($"    public static {configClass} Load(ICoreAPI api)");
        sb.AppendLine("    {");
        sb.AppendLine($"        {currentProperty} = api.LoadModConfig<{configClass}>(FileName) ?? new {configClass}();");
        sb.AppendLine($"        api.StoreModConfig({currentProperty}, FileName);");
        sb.AppendLine($"        return {currentProperty};");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static void Save(ICoreAPI api)");
        sb.AppendLine("    {");
        sb.AppendLine($"        api.StoreModConfig({currentProperty}, FileName);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public List<DevToolsConfigLibValidationIssue> Validate(bool modConfigIncludedOnly)
    {
        List<DevToolsConfigLibValidationIssue> issues = [];
        if (Version < 0) issues.Add(new(DevToolsConfigLibIssueSeverity.Error, "Version cannot be negative."));
        if (string.IsNullOrWhiteSpace(Domain)) issues.Add(new(DevToolsConfigLibIssueSeverity.Error, "Output domain is required."));
        if (!string.Equals(Domain, SanitizeDomain(Domain), StringComparison.Ordinal))
        {
            issues.Add(new(DevToolsConfigLibIssueSeverity.Warning, $"Output domain will be saved as '{SanitizeDomain(Domain)}'."));
        }

        if (string.IsNullOrWhiteSpace(RelativePath)) issues.Add(new(DevToolsConfigLibIssueSeverity.Error, "ConfigLib relative path is required."));
        if (ContainsTraversal(RelativePath)) issues.Add(new(DevToolsConfigLibIssueSeverity.Error, "ConfigLib relative path cannot contain path traversal."));
        if (!string.Equals(RelativePath, NormalizeRelativePath(RelativePath, "config/configlib-patches.json"), StringComparison.Ordinal))
        {
            issues.Add(new(DevToolsConfigLibIssueSeverity.Warning, $"ConfigLib relative path will be saved as '{NormalizeRelativePath(RelativePath, "config/configlib-patches.json")}'."));
        }

        if (string.IsNullOrWhiteSpace(ModConfigRelativePath)) issues.Add(new(DevToolsConfigLibIssueSeverity.Warning, "ModConfig relative path is empty; a generated default name will be used."));
        if (ContainsTraversal(ModConfigRelativePath)) issues.Add(new(DevToolsConfigLibIssueSeverity.Error, "ModConfig relative path cannot contain path traversal."));
        if (modConfigIncludedOnly)
        {
            issues.Add(new(DevToolsConfigLibIssueSeverity.Warning, "ModConfig preview is emitting only enabled settings; omitted required keys can make the target mod reject the config."));
        }

        HashSet<string> codes = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        int enabledCount = 0;
        foreach (DevToolsConfigLibSettingDraft setting in Settings)
        {
            if (!setting.Enabled) continue;
            enabledCount++;
            foreach (DevToolsConfigLibValidationIssue issue in setting.Validate())
            {
                issues.Add(issue);
            }

            if (!string.IsNullOrWhiteSpace(setting.Code) && !codes.Add(setting.Code.Trim()))
            {
                issues.Add(new(DevToolsConfigLibIssueSeverity.Error, $"Duplicate setting code '{setting.Code}'."));
            }

            if (!string.IsNullOrWhiteSpace(setting.Name) && !names.Add(setting.Name.Trim()))
            {
                issues.Add(new(DevToolsConfigLibIssueSeverity.Warning, $"Duplicate setting name '{setting.Name}'."));
            }
        }

        if (enabledCount == 0) issues.Add(new(DevToolsConfigLibIssueSeverity.Error, "At least one setting must be enabled before saving."));
        ValidateCSharpOutput(issues);
        return issues;
    }

    public string BuildPatchAssetRelativePath()
    {
        return Path.Combine("assets", SanitizeDomain(Domain), NormalizeRelativePath(RelativePath, "config/configlib-patches.json").Replace('/', Path.DirectorySeparatorChar));
    }

    public string BuildModConfigRelativePath()
    {
        return Path.Combine("ModConfig", NormalizeRelativePath(ModConfigRelativePath, $"{SanitizeDomain(Domain)}.json").Replace('/', Path.DirectorySeparatorChar));
    }

    public string BuildCSharpRelativePath()
    {
        string namespaceName = SanitizeNamespace(CSharpNamespace, "GeneratedConfig");
        string loaderClass = SanitizeCSharpTypeName(LoaderClassName, "GeneratedConfigLoader");
        string namespacePath = namespaceName.Replace('.', Path.DirectorySeparatorChar);
        return Path.Combine("src", namespacePath, $"{loaderClass}.cs");
    }

    public void ApplyCSharpDefaultsFromDomain()
    {
        string stem = ToPascalIdentifier(SanitizeDomain(Domain), "GeneratedConfig");
        CSharpNamespace = stem;
        ConfigClassName = $"{stem}Config";
        LoaderClassName = $"{stem}ConfigLoader";
        CurrentPropertyName = "Current";
    }

    public static string ExtractRelativePatchPath(string assetPath)
    {
        string path = assetPath.Replace('\\', '/').TrimStart('/');
        int slash = path.IndexOf('/');
        if (slash >= 0 && path.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeRelativePath(path, "config/configlib-patches.json");
        }

        return NormalizeRelativePath(path, "config/configlib-patches.json");
    }

    public static string SanitizeDomain(string value)
    {
        string sanitized = new(value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')
            .ToArray());
        sanitized = sanitized.Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "generatedconfig" : sanitized;
    }

    public static string SanitizeName(string value)
    {
        string sanitized = new(value
            .Trim()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? char.ToLowerInvariant(character) : '-')
            .ToArray());

        while (sanitized.Contains("--", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);
        }

        sanitized = sanitized.Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "setting" : sanitized;
    }

    public static string HumanizeName(string value)
    {
        string normalized = value.Replace('/', ' ').Replace('-', ' ').Replace('_', ' ');
        return string.Join(' ', normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    public static string SuggestDomain(string relativeFilePath)
    {
        string stem = Path.GetFileNameWithoutExtension(relativeFilePath).Trim();
        if (string.IsNullOrWhiteSpace(stem)) return "generatedconfig";

        string lowered = stem.ToLowerInvariant();
        lowered = StripConfigAffix(lowered, "serverconfig");
        lowered = StripConfigAffix(lowered, "clientconfig");
        lowered = StripConfigAffix(lowered, "configserver");
        lowered = StripConfigAffix(lowered, "configclient");
        lowered = StripConfigAffix(lowered, "config");
        return SanitizeDomain(string.IsNullOrWhiteSpace(lowered) ? stem : lowered);
    }

    public static string NormalizeRelativePath(string path, string fallback)
    {
        string normalized = string.IsNullOrWhiteSpace(path)
            ? fallback
            : path.Replace('\\', '/').Trim().TrimStart('/');
        List<string> parts = [];
        foreach (string part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }

            string sanitized = SanitizePathPart(part);
            if (!string.IsNullOrWhiteSpace(sanitized)) parts.Add(sanitized);
        }

        normalized = parts.Count == 0 ? fallback : string.Join('/', parts);
        return normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? normalized : normalized + ".json";
    }

    public static bool IsClientSideConfig(string relativeFilePath)
    {
        string normalized = relativeFilePath.Replace('\\', '/').ToLowerInvariant();
        string stem = Path.GetFileNameWithoutExtension(normalized);
        if (stem.Contains("server", StringComparison.OrdinalIgnoreCase) && !stem.Contains("client", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.Contains("/client/", StringComparison.OrdinalIgnoreCase)) return true;
        if (stem.Contains("clientconfig", StringComparison.OrdinalIgnoreCase) || stem.Contains("configclient", StringComparison.OrdinalIgnoreCase)) return true;

        return stem
            .Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, "client", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<DevToolsConfigLibSettingDraft> InferSettings(JToken root)
    {
        List<DevToolsConfigLibSettingDraft> settings = [];
        VisitToken(root, "", settings);
        for (int index = 0; index < settings.Count; index++)
        {
            settings[index].Weight = index + 1;
        }

        return settings;
    }

    private static void VisitToken(JToken token, string path, List<DevToolsConfigLibSettingDraft> settings)
    {
        switch (token)
        {
            case JObject obj:
                if (string.IsNullOrWhiteSpace(path))
                {
                    foreach (JProperty property in obj.Properties())
                    {
                        VisitToken(property.Value, property.Name, settings);
                    }
                }
                else
                {
                    settings.Add(DevToolsConfigLibSettingDraft.FromInferred(path, "object", token));
                    foreach (JProperty property in obj.Properties())
                    {
                        VisitToken(property.Value, JoinPath(path, property.Name), settings);
                    }
                }

                break;
            case JArray array:
                if (!string.IsNullOrWhiteSpace(path))
                {
                    settings.Add(DevToolsConfigLibSettingDraft.FromInferred(path, "array", token));
                }

                for (int index = 0; index < array.Count; index++)
                {
                    VisitToken(array[index], JoinPath(path, index.ToString(CultureInfo.InvariantCulture)), settings);
                }

                break;
            default:
                if (!string.IsNullOrWhiteSpace(path) && TryGetSettingType(token, out string type))
                {
                    settings.Add(DevToolsConfigLibSettingDraft.FromInferred(path, type, token));
                }

                break;
        }
    }

    private static void GenerateSeparators(DevToolsConfigLibDocumentDraft document)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (DevToolsConfigLibSettingDraft setting in document.Settings)
        {
            string separator = GetSeparatorName(setting.Code);
            if (string.IsNullOrWhiteSpace(separator) || !seen.Add(separator)) continue;
            document.Formatting.Add(new DevToolsConfigLibFormattingDraft
            {
                Type = "separator",
                Title = HumanizeName(separator),
                Weight = Math.Max(0, setting.Weight - 0.5)
            });
        }
    }

    private static bool TryGetSettingType(JToken token, out string type)
    {
        type = token.Type switch
        {
            JTokenType.Boolean => "boolean",
            JTokenType.Integer => "integer",
            JTokenType.Float => "float",
            JTokenType.String => "string",
            _ => ""
        };
        return type.Length > 0;
    }

    private static bool IsPrimitiveToken(JToken token)
    {
        return token.Type is JTokenType.Boolean or JTokenType.Integer or JTokenType.Float or JTokenType.String;
    }

    private static string JoinPath(string path, string child)
    {
        return string.IsNullOrWhiteSpace(path) ? child : $"{path}/{child}";
    }

    private static string GetSeparatorName(string code)
    {
        int separator = code.IndexOf('/');
        return separator > 0 ? code[..separator] : "";
    }

    private static void SetTokenAtPath(ref JToken root, IReadOnlyList<string> parts, JToken value)
    {
        if (parts.Count == 0)
        {
            root = value;
            return;
        }

        if (root is not JObject && root is not JArray)
        {
            root = IsArrayIndex(parts[0]) ? new JArray() : new JObject();
        }

        JToken current = root;
        for (int index = 0; index < parts.Count; index++)
        {
            string part = parts[index];
            bool last = index == parts.Count - 1;
            bool nextArray = !last && IsArrayIndex(parts[index + 1]);

            if (current is JObject obj)
            {
                if (last)
                {
                    obj[part] = value;
                    return;
                }

                JToken? next = obj[part];
                if (next == null || next.Type == JTokenType.Null)
                {
                    next = nextArray ? new JArray() : new JObject();
                    obj[part] = next;
                }

                current = next;
                continue;
            }

            if (current is JArray array && int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int arrayIndex) && arrayIndex >= 0)
            {
                while (array.Count <= arrayIndex) array.Add(JValue.CreateNull());
                if (last)
                {
                    array[arrayIndex] = value;
                    return;
                }

                JToken? next = array[arrayIndex];
                if (next == null || next.Type == JTokenType.Null)
                {
                    next = nextArray ? new JArray() : new JObject();
                    array[arrayIndex] = next;
                }

                current = next;
            }
        }
    }

    private static bool IsArrayIndex(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index >= 0;
    }

    private static string StripConfigAffix(string value, string affix)
    {
        if (value.EndsWith(affix, StringComparison.OrdinalIgnoreCase) && value.Length > affix.Length)
        {
            return value[..^affix.Length].Trim('-', '_', '.', ' ');
        }

        if (value.StartsWith(affix, StringComparison.OrdinalIgnoreCase) && value.Length > affix.Length)
        {
            return value[affix.Length..].Trim('-', '_', '.', ' ');
        }

        return value;
    }

    private static bool ContainsTraversal(string path)
    {
        return path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part == "..");
    }

    private static string SanitizePathPart(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private void ValidateCSharpOutput(List<DevToolsConfigLibValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(CSharpNamespace))
        {
            issues.Add(new(DevToolsConfigLibIssueSeverity.Error, "C# namespace is required."));
        }
        else
        {
            string sanitized = SanitizeNamespace(CSharpNamespace, "GeneratedConfig");
            if (!string.Equals(CSharpNamespace.Trim(), sanitized, StringComparison.Ordinal))
            {
                issues.Add(new(DevToolsConfigLibIssueSeverity.Warning, $"C# namespace will be saved as '{sanitized}'."));
            }
        }

        ValidateCSharpTypeName(ConfigClassName, "Config class name", "GeneratedConfigConfig", issues);
        ValidateCSharpTypeName(LoaderClassName, "Loader class name", "GeneratedConfigLoader", issues);
        ValidateCSharpPropertyName(CurrentPropertyName, "Static instance property name", "Current", issues);

        BuildCSharpConfigNode(out _, out List<string> duplicateProperties);
        foreach (string duplicate in duplicateProperties.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new(DevToolsConfigLibIssueSeverity.Error, $"Generated C# property name conflict: {duplicate}."));
        }
    }

    private static void ValidateCSharpTypeName(string value, string label, string fallback, List<DevToolsConfigLibValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new(DevToolsConfigLibIssueSeverity.Error, $"{label} is required."));
            return;
        }

        string sanitized = SanitizeCSharpTypeName(value, fallback);
        if (!string.Equals(value.Trim(), sanitized, StringComparison.Ordinal))
        {
            issues.Add(new(DevToolsConfigLibIssueSeverity.Warning, $"{label} will be saved as '{sanitized}'."));
        }
    }

    private static void ValidateCSharpPropertyName(string value, string label, string fallback, List<DevToolsConfigLibValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new(DevToolsConfigLibIssueSeverity.Error, $"{label} is required."));
            return;
        }

        string sanitized = SanitizeCSharpPropertyName(value, fallback);
        if (!string.Equals(value.Trim(), sanitized, StringComparison.Ordinal))
        {
            issues.Add(new(DevToolsConfigLibIssueSeverity.Warning, $"{label} will be saved as '{sanitized}'."));
        }
    }

    private CSharpConfigNode BuildCSharpConfigNode(out bool needsJsonLinq, out List<string> duplicateProperties)
    {
        CSharpConfigNode root = new("", SanitizeCSharpTypeName(ConfigClassName, "GeneratedConfigConfig"));
        needsJsonLinq = false;
        duplicateProperties = [];

        List<DevToolsConfigLibSettingDraft> enabledSettings = Settings
            .Where(setting => setting.Enabled && !string.IsNullOrWhiteSpace(setting.Code))
            .ToList();
        List<string> codes = enabledSettings.Select(setting => setting.Code.Trim()).ToList();

        foreach (DevToolsConfigLibSettingDraft setting in enabledSettings)
        {
            if (!setting.TryGetDefaultToken(out JToken? defaultToken, out _) || defaultToken == null) continue;
            string code = setting.Code.Trim();
            if (HasDescendantSetting(code, codes)) continue;

            string[] parts = code.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            bool canNest = parts.Length > 1 && parts.All(IsPracticalNestedCSharpPart);
            CSharpConfigNode target = root;
            string propertyName;
            if (canNest)
            {
                for (int index = 0; index < parts.Length - 1; index++)
                {
                    string childProperty = SanitizeCSharpPropertyName(parts[index], $"Group{index + 1}");
                    if (target.Properties.Any(property => property.Name.Equals(childProperty, StringComparison.OrdinalIgnoreCase)))
                    {
                        duplicateProperties.Add($"{target.ClassName}.{childProperty}");
                    }

                    if (!target.Children.TryGetValue(childProperty, out CSharpConfigNode? child))
                    {
                        child = new(childProperty, $"{childProperty}Config");
                        target.Children[childProperty] = child;
                    }

                    target = child;
                }

                propertyName = SanitizeCSharpPropertyName(parts[^1], "Setting");
            }
            else
            {
                propertyName = SanitizeCSharpPropertyName(string.Join(" ", parts), "Setting");
            }

            if (target.Children.ContainsKey(propertyName) ||
                target.Properties.Any(property => property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)))
            {
                duplicateProperties.Add($"{target.ClassName}.{propertyName}");
            }

            CSharpProperty property = BuildCSharpProperty(setting, propertyName, defaultToken);
            needsJsonLinq |= property.NeedsJsonLinq;
            target.Properties.Add(property);
        }

        return root;
    }

    private static bool HasDescendantSetting(string code, IReadOnlyList<string> codes)
    {
        string prefix = code.TrimEnd('/') + "/";
        return codes.Any(candidate => candidate.Length > prefix.Length && candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPracticalNestedCSharpPart(string value)
    {
        return value.Any(char.IsLetter) && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ' ');
    }

    private static CSharpProperty BuildCSharpProperty(DevToolsConfigLibSettingDraft setting, string propertyName, JToken defaultToken)
    {
        string type = setting.Type.Trim().ToLowerInvariant();
        return type switch
        {
            "boolean" => new(propertyName, "bool", defaultToken.Value<bool?>() == true ? "true" : "false", false),
            "integer" => new(propertyName, "int", defaultToken.Value<int?>()?.ToString(CultureInfo.InvariantCulture) ?? "0", false),
            "float" => new(propertyName, "float", BuildCSharpFloatLiteral(defaultToken), false),
            "object" => new(propertyName, "JObject", defaultToken is JObject ? $"JObject.Parse({ToCSharpStringLiteral(JsonConvert.SerializeObject(defaultToken, Newtonsoft.Json.Formatting.None))})" : "new JObject()", true),
            "array" => new(propertyName, "JArray", defaultToken is JArray ? $"JArray.Parse({ToCSharpStringLiteral(JsonConvert.SerializeObject(defaultToken, Newtonsoft.Json.Formatting.None))})" : "new JArray()", true),
            _ => new(propertyName, "string", ToCSharpStringLiteral(defaultToken.Type == JTokenType.Null ? "" : defaultToken.ToString()), false)
        };
    }

    private static string BuildCSharpFloatLiteral(JToken token)
    {
        double value = token.Value<double?>() ?? 0;
        string text = value.ToString("0.########", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) ? $"{text}f" : $"{text}.0f";
    }

    private static void AppendCSharpConfigClass(StringBuilder sb, CSharpConfigNode node, string className, int indent)
    {
        string pad = new(' ', indent * 4);
        sb.AppendLine($"{pad}public sealed class {className}");
        sb.AppendLine($"{pad}{{");

        foreach (CSharpConfigNode child in node.Children.Values)
        {
            sb.AppendLine($"{pad}    public {child.ClassName} {child.PropertyName} {{ get; set; }} = new();");
        }

        foreach (CSharpProperty property in node.Properties)
        {
            sb.AppendLine($"{pad}    public {property.TypeName} {property.Name} {{ get; set; }} = {property.DefaultExpression};");
        }

        if (node.Children.Count > 0)
        {
            sb.AppendLine();
            bool first = true;
            foreach (CSharpConfigNode child in node.Children.Values)
            {
                if (!first) sb.AppendLine();
                AppendCSharpConfigClass(sb, child, child.ClassName, indent + 1);
                first = false;
            }
        }

        sb.AppendLine($"{pad}}}");
    }

    private static string SanitizeNamespace(string value, string fallback)
    {
        string[] rawParts = value
            .Replace('\\', '.')
            .Replace('/', '.')
            .Split('.', StringSplitOptions.RemoveEmptyEntries);
        List<string> parts = [];
        foreach (string rawPart in rawParts)
        {
            string part = SanitizeCSharpTypeName(rawPart, "");
            if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
        }

        return parts.Count == 0 ? fallback : string.Join('.', parts);
    }

    private static string SanitizeCSharpTypeName(string value, string fallback)
    {
        string identifier = ToPascalIdentifier(value, fallback);
        if (identifier.Length == 0) return fallback;
        return char.IsDigit(identifier[0]) ? $"{fallback}{identifier}" : identifier;
    }

    private static string SanitizeCSharpPropertyName(string value, string fallback)
    {
        string identifier = ToPascalIdentifier(value, fallback);
        if (identifier.Length == 0) return fallback;
        return char.IsDigit(identifier[0]) ? $"_{identifier}" : identifier;
    }

    private static string ToPascalIdentifier(string value, string fallback)
    {
        StringBuilder sb = new();
        bool newWord = true;
        foreach (char character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                sb.Append(newWord ? char.ToUpperInvariant(character) : character);
                newWord = false;
                continue;
            }

            newWord = true;
        }

        return sb.Length == 0 ? fallback : sb.ToString();
    }

    private static string ToCSharpStringLiteral(string value)
    {
        return JsonConvert.SerializeObject(value);
    }

    private sealed class CSharpConfigNode
    {
        public CSharpConfigNode(string propertyName, string className)
        {
            PropertyName = propertyName;
            ClassName = className;
        }

        public string PropertyName { get; }
        public string ClassName { get; }
        public Dictionary<string, CSharpConfigNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<CSharpProperty> Properties { get; } = [];
    }

    private sealed record CSharpProperty(string Name, string TypeName, string DefaultExpression, bool NeedsJsonLinq);
}

internal sealed class DevToolsConfigLibSettingDraft
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "name", "title", "comment", "type", "default", "range", "values", "logarithmic", "clientSide", "weight"
    };

    public bool Enabled { get; set; } = true;
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Comment { get; set; } = "";
    public string Type { get; set; } = "string";
    public string DefaultJson { get; set; } = "\"\"";
    public bool HasRange { get; set; }
    public string RangeMinJson { get; set; } = "0";
    public string RangeMaxJson { get; set; } = "1";
    public string RangeStepJson { get; set; } = "1";
    public JObject RangeExtra { get; set; } = [];
    public string ValuesJson { get; set; } = "";
    public bool Logarithmic { get; set; }
    public bool ClientSide { get; set; }
    public double Weight { get; set; } = 1;
    public JObject Extra { get; set; } = [];

    public static DevToolsConfigLibSettingDraft FromInferred(string code, string type, JToken defaultValue)
    {
        DevToolsConfigLibSettingDraft draft = new()
        {
            Code = code,
            Name = DevToolsConfigLibDocumentDraft.SanitizeName(code),
            Title = DevToolsConfigLibDocumentDraft.HumanizeName(code),
            Type = type,
            DefaultJson = JsonConvert.SerializeObject(defaultValue.DeepClone(), Formatting.Indented),
            Comment = BuildGeneratedComment(code, defaultValue),
            Enabled = !string.Equals(type, "other", StringComparison.OrdinalIgnoreCase)
        };

        draft.InitializeNumericDefaults(defaultValue);
        return draft;
    }

    public static DevToolsConfigLibSettingDraft FromJson(JObject json)
    {
        DevToolsConfigLibSettingDraft draft = new()
        {
            Code = json["code"]?.ToString() ?? "",
            Name = json["name"]?.ToString() ?? "",
            Title = json["title"]?.ToString() ?? "",
            Comment = json["comment"]?.ToString() ?? "",
            Type = json["type"]?.ToString() ?? "string",
            DefaultJson = json.TryGetValue("default", StringComparison.OrdinalIgnoreCase, out JToken? defaultToken)
                ? JsonConvert.SerializeObject(defaultToken, Formatting.Indented)
                : "null",
            ValuesJson = json.TryGetValue("values", StringComparison.OrdinalIgnoreCase, out JToken? valuesToken)
                ? JsonConvert.SerializeObject(valuesToken, Formatting.Indented)
                : "",
            Logarithmic = json["logarithmic"]?.Value<bool?>() ?? false,
            ClientSide = json["clientSide"]?.Value<bool?>() ?? false,
            Weight = json["weight"]?.Value<double?>() ?? 1
        };

        if (json["range"] is JObject range)
        {
            draft.HasRange = true;
            draft.RangeMinJson = range.TryGetValue("min", StringComparison.OrdinalIgnoreCase, out JToken? min) ? JsonConvert.SerializeObject(min, Formatting.None) : "0";
            draft.RangeMaxJson = range.TryGetValue("max", StringComparison.OrdinalIgnoreCase, out JToken? max) ? JsonConvert.SerializeObject(max, Formatting.None) : "1";
            draft.RangeStepJson = range.TryGetValue("step", StringComparison.OrdinalIgnoreCase, out JToken? step) ? JsonConvert.SerializeObject(step, Formatting.None) : "1";
            foreach (JProperty property in range.Properties())
            {
                if (property.Name.Equals("min", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("max", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("step", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                draft.RangeExtra[property.Name] = property.Value.DeepClone();
            }
        }

        foreach (JProperty property in json.Properties())
        {
            if (!KnownKeys.Contains(property.Name))
            {
                draft.Extra[property.Name] = property.Value.DeepClone();
            }
        }

        return draft;
    }

    public JObject ToJson()
    {
        JObject json = (JObject)Extra.DeepClone();
        json["code"] = Code.Trim();
        if (!string.IsNullOrWhiteSpace(Name)) json["name"] = Name.Trim();
        if (!string.IsNullOrWhiteSpace(Title)) json["title"] = Title.Trim();
        if (!string.IsNullOrWhiteSpace(Comment)) json["comment"] = Comment.Trim();
        json["type"] = Type.Trim();

        if (TryGetDefaultToken(out JToken? defaultToken, out _))
        {
            json["default"] = defaultToken;
        }
        else
        {
            json["default"] = DefaultJson;
        }

        json["weight"] = Weight;
        if (ClientSide) json["clientSide"] = true;
        if (Logarithmic) json["logarithmic"] = true;

        if (HasRange)
        {
            JObject range = (JObject)RangeExtra.DeepClone();
            range["min"] = ParseJsonValueOrString(RangeMinJson);
            range["max"] = ParseJsonValueOrString(RangeMaxJson);
            if (!string.IsNullOrWhiteSpace(RangeStepJson))
            {
                range["step"] = ParseJsonValueOrString(RangeStepJson);
            }

            json["range"] = range;
        }

        if (!string.IsNullOrWhiteSpace(ValuesJson) && DevToolsJson.TryParseToken(ValuesJson, out JToken? values, out _, useVintageStoryFallback: false) && values != null)
        {
            json["values"] = values;
        }

        return json;
    }

    public DevToolsConfigLibSettingDraft Clone()
    {
        return new()
        {
            Enabled = Enabled,
            Code = Code,
            Name = Name,
            Title = Title,
            Comment = Comment,
            Type = Type,
            DefaultJson = DefaultJson,
            HasRange = HasRange,
            RangeMinJson = RangeMinJson,
            RangeMaxJson = RangeMaxJson,
            RangeStepJson = RangeStepJson,
            RangeExtra = (JObject)RangeExtra.DeepClone(),
            ValuesJson = ValuesJson,
            Logarithmic = Logarithmic,
            ClientSide = ClientSide,
            Weight = Weight,
            Extra = (JObject)Extra.DeepClone()
        };
    }

    public IEnumerable<DevToolsConfigLibValidationIssue> Validate()
    {
        if (string.IsNullOrWhiteSpace(Code)) yield return new(DevToolsConfigLibIssueSeverity.Error, "A setting is missing code.");
        if (string.IsNullOrWhiteSpace(Type)) yield return new(DevToolsConfigLibIssueSeverity.Error, $"Setting '{DisplayCode}' is missing type.");
        if (!TryGetDefaultToken(out JToken? defaultToken, out string defaultError))
        {
            yield return new(DevToolsConfigLibIssueSeverity.Error, $"Setting '{DisplayCode}' has invalid default JSON: {defaultError}");
        }

        if (HasRange)
        {
            bool minOk = TryParseNumericJson(RangeMinJson, out double min, out string minError);
            bool maxOk = TryParseNumericJson(RangeMaxJson, out double max, out string maxError);
            bool stepOk = TryParseNumericJson(RangeStepJson, out double step, out string stepError);
            if (!minOk) yield return new(DevToolsConfigLibIssueSeverity.Error, $"Setting '{DisplayCode}' range min is invalid: {minError}");
            if (!maxOk) yield return new(DevToolsConfigLibIssueSeverity.Error, $"Setting '{DisplayCode}' range max is invalid: {maxError}");
            if (!stepOk) yield return new(DevToolsConfigLibIssueSeverity.Error, $"Setting '{DisplayCode}' range step is invalid: {stepError}");
            if (minOk && maxOk && min > max) yield return new(DevToolsConfigLibIssueSeverity.Error, $"Setting '{DisplayCode}' range min is greater than max.");
            if (stepOk && step <= 0) yield return new(DevToolsConfigLibIssueSeverity.Error, $"Setting '{DisplayCode}' range step must be greater than zero.");
        }

        if (!string.IsNullOrWhiteSpace(ValuesJson))
        {
            if (!DevToolsJson.TryParseToken(ValuesJson, out JToken? valuesToken, out string valuesError, useVintageStoryFallback: false) || valuesToken is not JArray values)
            {
                yield return new(DevToolsConfigLibIssueSeverity.Error, $"Setting '{DisplayCode}' values JSON is invalid: {valuesError}");
            }
            else if (string.Equals(Type, "string", StringComparison.OrdinalIgnoreCase) && defaultToken?.Type == JTokenType.String)
            {
                string defaultValue = defaultToken.ToString();
                bool found = values.Any(value => value.Type == JTokenType.String && string.Equals(value.ToString(), defaultValue, StringComparison.Ordinal));
                if (!found)
                {
                    yield return new(DevToolsConfigLibIssueSeverity.Warning, $"Setting '{DisplayCode}' string default is not present in dropdown values.");
                }
            }
        }
    }

    public bool TryGetDefaultToken(out JToken? token, out string error)
    {
        return DevToolsJson.TryParseToken(DefaultJson, out token, out error, useVintageStoryFallback: false);
    }

    public string DisplayCode => string.IsNullOrWhiteSpace(Code) ? "<missing code>" : Code;

    private void InitializeNumericDefaults(JToken defaultValue)
    {
        bool numeric = string.Equals(Type, "integer", StringComparison.OrdinalIgnoreCase) || string.Equals(Type, "float", StringComparison.OrdinalIgnoreCase);
        if (!numeric) return;

        double value = defaultValue.Value<double?>() ?? 0;
        double spread = Math.Max(1, Math.Abs(value));
        RangeMinJson = Math.Floor(value - spread).ToString(CultureInfo.InvariantCulture);
        RangeMaxJson = Math.Ceiling(value + spread).ToString(CultureInfo.InvariantCulture);
        RangeStepJson = string.Equals(Type, "integer", StringComparison.OrdinalIgnoreCase) ? "1" : "0.1";
    }

    private static JToken ParseJsonValueOrString(string text)
    {
        return DevToolsJson.TryParseToken(text, out JToken? token, out _, useVintageStoryFallback: false) && token != null
            ? token
            : new JValue(text);
    }

    private static bool TryParseNumericJson(string text, out double value, out string error)
    {
        value = 0;
        if (!DevToolsJson.TryParseToken(text, out JToken? token, out error, useVintageStoryFallback: false) || token == null)
        {
            return false;
        }

        if (token.Type is not (JTokenType.Integer or JTokenType.Float))
        {
            error = "value is not numeric";
            return false;
        }

        value = token.Value<double>();
        return true;
    }

    private static string BuildGeneratedComment(string code, JToken defaultValue)
    {
        string preview = JsonConvert.SerializeObject(defaultValue, Formatting.None);
        if (preview.Length > 80) preview = preview[..80] + "...";
        return $"Generated from {code}. Default: {preview}.";
    }
}

internal sealed class DevToolsConfigLibFormattingDraft
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "title", "weight"
    };

    public bool Enabled { get; set; } = true;
    public string Type { get; set; } = "separator";
    public string Title { get; set; } = "";
    public double Weight { get; set; }
    public JObject Extra { get; set; } = [];

    public static DevToolsConfigLibFormattingDraft FromJson(JObject json)
    {
        DevToolsConfigLibFormattingDraft draft = new()
        {
            Type = json["type"]?.ToString() ?? "separator",
            Title = json["title"]?.ToString() ?? "",
            Weight = json["weight"]?.Value<double?>() ?? 0
        };

        foreach (JProperty property in json.Properties())
        {
            if (!KnownKeys.Contains(property.Name))
            {
                draft.Extra[property.Name] = property.Value.DeepClone();
            }
        }

        return draft;
    }

    public JObject ToJson()
    {
        JObject json = (JObject)Extra.DeepClone();
        json["type"] = string.IsNullOrWhiteSpace(Type) ? "separator" : Type.Trim();
        if (!string.IsNullOrWhiteSpace(Title)) json["title"] = Title.Trim();
        json["weight"] = Weight;
        return json;
    }

    public DevToolsConfigLibFormattingDraft Clone()
    {
        return new()
        {
            Enabled = Enabled,
            Type = Type,
            Title = Title,
            Weight = Weight,
            Extra = (JObject)Extra.DeepClone()
        };
    }
}
