using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InGameDevTools.Utils;

internal enum DevToolsPatchOutputFormat
{
    JsonPatchesLib,
    Vanilla
}

internal sealed class DevToolsPatchDocumentDraft
{
    public DevToolsPatchOutputFormat Format { get; set; }
    public string Domain { get; set; } = "ingamedevtools";
    public string RelativePath { get; set; } = "generated-patch.json";
    public List<DevToolsPatchOperationDraft> Operations { get; } = [];

    public static DevToolsPatchDocumentDraft FromJson(string text, DevToolsPatchOutputFormat format, string domain, string relativePath)
    {
        JToken root = JToken.Parse(text);
        JArray operations = root as JArray ?? new JArray(root);
        DevToolsPatchDocumentDraft document = new()
        {
            Format = format,
            Domain = string.IsNullOrWhiteSpace(domain) ? "ingamedevtools" : domain,
            RelativePath = NormalizeRelativePath(relativePath)
        };

        foreach (JToken token in operations)
        {
            if (token is JObject obj)
            {
                document.Operations.Add(DevToolsPatchOperationDraft.FromJson(obj, format));
            }
        }

        return document;
    }

    public string ToJson()
    {
        JArray operations = [];
        foreach (DevToolsPatchOperationDraft operation in Operations)
        {
            operations.Add(operation.ToJson(Format));
        }

        return DevToolsPatchJson.ToString(operations, Formatting.Indented);
    }

    public static DevToolsPatchOutputFormat InferFormatFromAssetPath(string assetPath)
    {
        string path = assetPath.Replace('\\', '/');
        return path.StartsWith("jsonpatches/", StringComparison.OrdinalIgnoreCase)
            ? DevToolsPatchOutputFormat.JsonPatchesLib
            : DevToolsPatchOutputFormat.Vanilla;
    }

    public static string ExtractRelativePatchPath(string assetPath)
    {
        string path = assetPath.Replace('\\', '/').TrimStart('/');
        if (path.StartsWith("jsonpatches/", StringComparison.OrdinalIgnoreCase)) return NormalizeRelativePath(path["jsonpatches/".Length..]);
        if (path.StartsWith("patches/", StringComparison.OrdinalIgnoreCase)) return NormalizeRelativePath(path["patches/".Length..]);
        return NormalizeRelativePath(path);
    }

    public static string NormalizeRelativePath(string path)
    {
        string normalized = string.IsNullOrWhiteSpace(path)
            ? "generated-patch.json"
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

        normalized = parts.Count == 0 ? "generated-patch.json" : string.Join('/', parts);
        return normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? normalized : normalized + ".json";
    }

    public static string BuildAssetPath(DevToolsPatchOutputFormat format, string relativePath)
    {
        string folder = format == DevToolsPatchOutputFormat.JsonPatchesLib ? "jsonpatches" : "patches";
        return $"{folder}/{NormalizeRelativePath(relativePath)}";
    }

    private static string SanitizePathPart(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}

internal sealed class DevToolsPatchOperationDraft
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "op", "file", "path", "from", "frompath", "fromPath", "value", "enabled", "side", "priority", "condition", "dependsOn"
    };

    public string Op { get; set; } = "replace";
    public string File { get; set; } = "";
    public string Path { get; set; } = "";
    public string FromPath { get; set; } = "";
    public string ValueJson { get; set; } = "null";
    public bool HasValue { get; set; } = true;
    public bool? Enabled { get; set; } = true;
    public string Side { get; set; } = "Server";
    public int? Priority { get; set; }
    public string ConditionJson { get; set; } = "";
    public string DependsOnJson { get; set; } = "";
    public JObject Extra { get; set; } = [];

    public static DevToolsPatchOperationDraft FromJson(JObject json, DevToolsPatchOutputFormat format)
    {
        DevToolsPatchOperationDraft operation = new()
        {
            Op = json["op"]?.ToString() ?? "",
            File = json["file"]?.ToString() ?? "",
            Path = DevToolsPatchPaths.Normalize(json["path"]?.ToString() ?? ""),
            FromPath = DevToolsPatchPaths.Normalize(json["frompath"]?.ToString() ?? json["from"]?.ToString() ?? json["fromPath"]?.ToString() ?? ""),
            HasValue = json.TryGetValue("value", StringComparison.OrdinalIgnoreCase, out JToken? valueToken),
            ValueJson = valueToken == null ? "" : DevToolsPatchJson.ToString(valueToken, Formatting.Indented),
            Enabled = json.TryGetValue("enabled", StringComparison.OrdinalIgnoreCase, out JToken? enabledToken) && enabledToken.Type != JTokenType.Null
                ? enabledToken.Value<bool?>()
                : null,
            Side = json["side"]?.ToString() ?? "",
            Priority = json.TryGetValue("priority", StringComparison.OrdinalIgnoreCase, out JToken? priorityToken) && priorityToken.Type != JTokenType.Null
                ? priorityToken.Value<int?>()
                : null,
            ConditionJson = json["condition"] == null ? "" : DevToolsPatchJson.ToString(json["condition"]!, Formatting.Indented),
            DependsOnJson = json["dependsOn"] == null ? "" : DevToolsPatchJson.ToString(json["dependsOn"]!, Formatting.Indented)
        };

        if (operation.Enabled == null && format == DevToolsPatchOutputFormat.JsonPatchesLib && json["enabled"] == null)
        {
            operation.Enabled = null;
        }

        JObject extra = [];
        foreach (JProperty property in json.Properties())
        {
            if (KnownKeys.Contains(property.Name)) continue;
            extra[property.Name] = property.Value.DeepClone();
        }

        operation.Extra = extra;
        return operation;
    }

    public JObject ToJson(DevToolsPatchOutputFormat format)
    {
        JObject json = (JObject)Extra.DeepClone();
        if (!string.IsNullOrWhiteSpace(Op)) json["op"] = Op;
        if (!string.IsNullOrWhiteSpace(File)) json["file"] = File;
        json["path"] = DevToolsPatchPaths.Format(Path, format);

        if (format == DevToolsPatchOutputFormat.JsonPatchesLib)
        {
            if (Enabled.HasValue) json["enabled"] = Enabled.Value;
            if (!string.IsNullOrWhiteSpace(Side)) json["side"] = Side.ToLowerInvariant();
            if (Priority.HasValue && Priority.Value != 0) json["priority"] = Priority.Value;
            if (!string.IsNullOrWhiteSpace(FromPath)) json["frompath"] = DevToolsPatchPaths.Format(FromPath, format);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(Side)) json["side"] = Side;
            if (!string.IsNullOrWhiteSpace(FromPath)) json["from"] = DevToolsPatchPaths.Format(FromPath, format);
            if (DevToolsPatchJson.TryParse(ConditionJson, out JToken? condition) && condition != null)
            {
                json["condition"] = condition;
            }
        }

        if (DevToolsPatchJson.TryParse(DependsOnJson, out JToken? dependsOn) && dependsOn != null)
        {
            json["dependsOn"] = dependsOn;
        }

        bool removeHasSpecificValue = Op.Equals("remove", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(ValueJson) &&
            !ValueJson.Trim().Equals("null", StringComparison.OrdinalIgnoreCase);
        if (HasValue || removeHasSpecificValue || DevToolsPatchOperations.NeedsValue(Op))
        {
            if (Op.Equals("expression", StringComparison.OrdinalIgnoreCase))
            {
                json["value"] = ValueJson;
            }
            else if (DevToolsPatchJson.TryParse(ValueJson, out JToken? value) && value != null)
            {
                json["value"] = value;
            }
            else if (!string.IsNullOrWhiteSpace(ValueJson))
            {
                json["value"] = ValueJson;
            }
        }

        return json;
    }

    public DevToolsPatchOperationDraft Clone()
    {
        return new DevToolsPatchOperationDraft
        {
            Op = Op,
            File = File,
            Path = Path,
            FromPath = FromPath,
            ValueJson = ValueJson,
            HasValue = HasValue,
            Enabled = Enabled,
            Side = Side,
            Priority = Priority,
            ConditionJson = ConditionJson,
            DependsOnJson = DependsOnJson,
            Extra = (JObject)Extra.DeepClone()
        };
    }
}

internal sealed record DevToolsPatchPreviewOptions(
    DevToolsPatchOutputFormat Format,
    string Side,
    JObject PreviewSettings,
    Func<string, int>? CountMatchingAssets = null);

internal sealed record DevToolsPatchPreviewResult(bool Success, string PreviewText, string Status, List<string> Warnings);

internal static class DevToolsPatchPreview
{
    public static DevToolsPatchPreviewResult Apply(
        JToken sourceRoot,
        IEnumerable<DevToolsPatchOperationDraft> operations,
        string sampleDomain,
        string sampleAssetPath,
        DevToolsPatchPreviewOptions options)
    {
        JToken working = sourceRoot.DeepClone();
        int applied = 0;
        List<string> warnings = [];
        foreach (DevToolsPatchOperationDraft operation in operations)
        {
            if (!OperationAppliesToSample(operation, sampleDomain, sampleAssetPath)) continue;

            if (options.CountMatchingAssets != null &&
                operation.File.StartsWith("@", StringComparison.Ordinal))
            {
                int count = options.CountMatchingAssets(operation.File);
                warnings.Add(count == 0
                    ? $"Wildcard/regex target '{operation.File}' matches no indexed assets."
                    : $"Wildcard/regex target '{operation.File}' matches {count} indexed asset(s); preview shows only the selected sample.");
            }

            if (options.Format == DevToolsPatchOutputFormat.JsonPatchesLib && operation.Enabled == false)
            {
                warnings.Add($"Skipped disabled operation {operation.Op} {operation.Path}.");
                continue;
            }

            if (!SideMatches(operation.Side, options.Side))
            {
                warnings.Add($"Skipped {operation.Op} {operation.Path} for side '{operation.Side}'.");
                continue;
            }

            JToken? conditionValue = null;
            if (options.Format == DevToolsPatchOutputFormat.Vanilla &&
                !TryEvaluateCondition(operation, options.PreviewSettings, out conditionValue, out string conditionStatus))
            {
                warnings.Add(conditionStatus);
                continue;
            }

            if (!TryApplyOperation(ref working, operation, conditionValue, out string error))
            {
                return new DevToolsPatchPreviewResult(false, "", $"Preview failed at {operation.Op} {operation.Path}: {error}", warnings);
            }

            applied++;
        }

        string sample = $"{sampleDomain}:{sampleAssetPath}";
        string status = applied == 0
            ? "Preview applied 0 operation(s) to the selected sample asset."
            : $"Preview applied {applied} operation(s) to sample {sample}.";
        return new DevToolsPatchPreviewResult(true, DevToolsPatchJson.ToString(working, Formatting.Indented), status, warnings);
    }

    public static bool OperationAppliesToSample(DevToolsPatchOperationDraft operation, string sampleDomain, string sampleAssetPath)
    {
        string exact = $"{sampleDomain}:{sampleAssetPath}";
        if (operation.File.Equals(exact, StringComparison.OrdinalIgnoreCase) ||
            operation.File.Equals(sampleAssetPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (operation.File.StartsWith("@@", StringComparison.Ordinal))
        {
            string pattern = operation.File[2..];
            return Regex.IsMatch(exact, pattern, RegexOptions.IgnoreCase) ||
                Regex.IsMatch(sampleAssetPath, pattern, RegexOptions.IgnoreCase);
        }

        if (operation.File.StartsWith("@", StringComparison.Ordinal))
        {
            string pattern = operation.File[1..];
            return WildcardMatches(pattern, exact) || WildcardMatches(pattern, sampleAssetPath);
        }

        return false;
    }

    public static bool WildcardMatches(string pattern, string value)
    {
        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private static bool SideMatches(string operationSide, string previewSide)
    {
        if (string.IsNullOrWhiteSpace(operationSide)) return true;
        if (operationSide.Equals("universal", StringComparison.OrdinalIgnoreCase)) return true;
        if (previewSide.Equals("universal", StringComparison.OrdinalIgnoreCase)) return true;
        return operationSide.Equals(previewSide, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryEvaluateCondition(
        DevToolsPatchOperationDraft operation,
        JObject settings,
        out JToken? conditionValue,
        out string status)
    {
        conditionValue = null;
        status = "";
        if (!DevToolsPatchJson.TryParse(operation.ConditionJson, out JToken? token) || token is not JObject condition) return true;

        string when = condition["when"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(when))
        {
            status = $"Skipped {operation.Op} {operation.Path}: condition is missing 'when'.";
            return false;
        }

        if (!TryGetSetting(settings, when, out JToken? setting))
        {
            status = $"Skipped {operation.Op} {operation.Path}: preview setting '{when}' is not set.";
            return false;
        }

        if (condition.TryGetValue("isValue", StringComparison.OrdinalIgnoreCase, out JToken? isValue))
        {
            if (!JToken.DeepEquals(setting, isValue))
            {
                status = $"Skipped {operation.Op} {operation.Path}: setting '{when}' did not match isValue.";
                return false;
            }
        }

        if (condition.TryGetValue("useValue", StringComparison.OrdinalIgnoreCase, out JToken? useValue) &&
            useValue?.Value<bool>() == true)
        {
            conditionValue = setting!.DeepClone();
        }

        return true;
    }

    private static bool TryGetSetting(JObject settings, string key, out JToken? value)
    {
        value = null;
        if (settings.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out value)) return true;
        string normalized = key.Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        JToken current = settings;
        foreach (string part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string unescaped = part.Replace("~1", "/").Replace("~0", "~");
            if (current is JObject obj && obj.TryGetValue(unescaped, StringComparison.OrdinalIgnoreCase, out JToken? next))
            {
                current = next;
                continue;
            }

            value = null;
            return false;
        }

        value = current;
        return true;
    }

    private static bool TryApplyOperation(ref JToken root, DevToolsPatchOperationDraft operation, JToken? conditionValue, out string error)
    {
        error = "";
        string op = operation.Op.ToLowerInvariant();
        try
        {
            switch (op)
            {
                case "add":
                    return TrySetToken(ref root, operation.Path, ParseValue(operation, conditionValue), add: true, out error);
                case "replace":
                    return TrySetToken(ref root, operation.Path, ParseValue(operation, conditionValue), add: false, out error);
                case "remove":
                    return TryRemoveToken(ref root, operation.Path, TryParseValue(operation.ValueJson, out JToken? removeValue) ? removeValue : null, out error);
                case "copy":
                    if (!TryGetToken(root, operation.FromPath, out JToken? copyToken, out error)) return false;
                    return TrySetToken(ref root, operation.Path, copyToken.DeepClone(), add: true, out error);
                case "move":
                    if (!TryGetToken(root, operation.FromPath, out JToken? moveToken, out error)) return false;
                    JToken clone = moveToken.DeepClone();
                    if (!TryRemoveToken(ref root, operation.FromPath, null, out error)) return false;
                    return TrySetToken(ref root, operation.Path, clone, add: true, out error);
                case "test":
                    if (!TryGetToken(root, operation.Path, out JToken? testToken, out error)) return false;
                    JToken expected = ParseValue(operation, conditionValue);
                    if (JToken.DeepEquals(testToken, expected)) return true;
                    error = $"Test failed. Expected {DevToolsPatchJson.ToString(expected, Formatting.None)}, found {DevToolsPatchJson.ToString(testToken, Formatting.None)}.";
                    return false;
                case "addmerge":
                    return TryAddMerge(ref root, operation.Path, ParseValue(operation, conditionValue), out error);
                case "addeach":
                    return TryAddEach(ref root, operation.Path, ParseValue(operation, conditionValue), out error);
                case "expression":
                    return TryApplyExpression(ref root, operation.Path, operation.ValueJson, out error);
                default:
                    error = $"Unsupported preview operation '{operation.Op}'.";
                    return false;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static JToken ParseValue(DevToolsPatchOperationDraft operation, JToken? conditionValue)
    {
        if (conditionValue != null) return conditionValue.DeepClone();
        if (operation.Op.Equals("expression", StringComparison.OrdinalIgnoreCase)) return new JValue(operation.ValueJson);
        return TryParseValue(operation.ValueJson, out JToken? token) ? token : new JValue(operation.ValueJson);
    }

    private static bool TryParseValue(string text, out JToken token)
    {
        if (DevToolsPatchJson.TryParse(text, out JToken? parsed) && parsed != null)
        {
            token = parsed;
            return true;
        }

        token = new JValue(text);
        return false;
    }

    private static bool TrySetToken(ref JToken root, string path, JToken value, bool add, out string error)
    {
        error = "";
        string[] parts = DevToolsPatchPaths.Split(path);
        if (parts.Length == 0)
        {
            root = value;
            return true;
        }

        if (!TryResolveParent(root, parts, out JToken? parent, out string last, out error)) return false;

        if (parent is JObject obj)
        {
            if (!add && obj[last] == null)
            {
                error = $"Object property '{last}' does not exist.";
                return false;
            }

            obj[last] = value;
            return true;
        }

        if (parent is JArray array)
        {
            if (last == "-")
            {
                array.Add(value);
                return true;
            }

            if (!int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                error = $"Invalid array index '{last}'.";
                return false;
            }

            if (add)
            {
                if (index < 0 || index > array.Count)
                {
                    error = $"Array insert index {index} out of range.";
                    return false;
                }

                array.Insert(index, value);
                return true;
            }

            if (index < 0 || index >= array.Count)
            {
                error = $"Array index {index} out of range.";
                return false;
            }

            array[index] = value;
            return true;
        }

        error = "Parent is not an object or array.";
        return false;
    }

    private static bool TryRemoveToken(ref JToken root, string path, JToken? value, out string error)
    {
        error = "";
        string[] parts = DevToolsPatchPaths.Split(path);
        if (parts.Length == 0)
        {
            root = JValue.CreateNull();
            return true;
        }

        if (!TryResolveParent(root, parts, out JToken? parent, out string last, out error)) return false;
        if (parent is JObject obj)
        {
            if (!obj.Remove(last))
            {
                error = $"Object property '{last}' not found.";
                return false;
            }

            return true;
        }

        if (parent is JArray array)
        {
            if (last == "-" && value != null)
            {
                bool removed = false;
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    if (!JToken.DeepEquals(array[i], value)) continue;
                    array.RemoveAt(i);
                    removed = true;
                }

                if (!removed) error = "Array value not found.";
                return removed;
            }

            if (int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) &&
                index >= 0 &&
                index < array.Count)
            {
                array.RemoveAt(index);
                return true;
            }
        }

        error = "Remove target not found.";
        return false;
    }

    private static bool TryAddMerge(ref JToken root, string path, JToken value, out string error)
    {
        if (!TryGetToken(root, path, out JToken? target, out error)) return false;
        if (target is JArray targetArray && value is JArray sourceArray)
        {
            foreach (JToken item in sourceArray)
            {
                if (!targetArray.Any(existing => JToken.DeepEquals(existing, item)))
                {
                    targetArray.Add(item.DeepClone());
                }
            }
            return true;
        }

        if (target is JObject targetObj && value is JObject sourceObj)
        {
            targetObj.Merge(sourceObj, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Union });
            return true;
        }

        error = "addmerge preview supports array or object targets.";
        return false;
    }

    private static bool TryAddEach(ref JToken root, string path, JToken value, out string error)
    {
        if (!TryGetToken(root, path, out JToken? target, out error)) return false;
        if (target is not JArray targetArray)
        {
            error = "addeach target must be an array.";
            return false;
        }

        if (value is JArray sourceArray)
        {
            foreach (JToken item in sourceArray)
            {
                targetArray.Add(item.DeepClone());
            }
            return true;
        }

        targetArray.Add(value.DeepClone());
        return true;
    }

    private static bool TryApplyExpression(ref JToken root, string path, string expression, out string error)
    {
        if (!TryGetToken(root, path, out JToken? target, out error)) return false;
        if (target.Type != JTokenType.Integer && target.Type != JTokenType.Float)
        {
            error = "Expression preview supports numeric targets only.";
            return false;
        }

        string value = target.Value<double>().ToString(CultureInfo.InvariantCulture);
        string formula = Regex.Replace(expression, @"\bvalue\b", value, RegexOptions.IgnoreCase);
        object result = new DataTable().Compute(formula, "");
        double parsed = Convert.ToDouble(result, CultureInfo.InvariantCulture);
        return TrySetToken(ref root, path, new JValue(parsed), add: false, out error);
    }

    private static bool TryGetToken(JToken root, string path, out JToken token, out string error)
    {
        error = "";
        token = root;
        foreach (string part in DevToolsPatchPaths.Split(path))
        {
            if (token is JObject obj)
            {
                JToken? child = obj[part];
                if (child == null)
                {
                    error = $"Property '{part}' not found.";
                    return false;
                }
                token = child;
            }
            else if (token is JArray array)
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
                    index < 0 ||
                    index >= array.Count)
                {
                    error = $"Array index '{part}' not found.";
                    return false;
                }
                token = array[index];
            }
            else
            {
                error = $"Cannot traverse through {token.Type}.";
                return false;
            }
        }
        return true;
    }

    private static bool TryResolveParent(JToken root, string[] parts, out JToken? parent, out string last, out string error)
    {
        parent = null;
        last = parts.Length == 0 ? "" : parts[^1];
        error = "";
        JToken current = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (current is JObject obj)
            {
                JToken? next = obj[parts[i]];
                if (next == null)
                {
                    error = $"Property '{parts[i]}' not found.";
                    return false;
                }
                current = next;
            }
            else if (current is JArray array)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
                    index < 0 ||
                    index >= array.Count)
                {
                    error = $"Array index '{parts[i]}' not found.";
                    return false;
                }
                current = array[index];
            }
            else
            {
                error = $"Cannot traverse through {current.Type}.";
                return false;
            }
        }

        parent = current;
        return true;
    }
}

internal static class DevToolsPatchOperations
{
    public static bool NeedsFromPath(string op) =>
        op.Equals("copy", StringComparison.OrdinalIgnoreCase) ||
        op.Equals("move", StringComparison.OrdinalIgnoreCase);

    public static bool NeedsValue(string op) =>
        op.Equals("add", StringComparison.OrdinalIgnoreCase) ||
        op.Equals("replace", StringComparison.OrdinalIgnoreCase) ||
        op.Equals("test", StringComparison.OrdinalIgnoreCase) ||
        op.Equals("addmerge", StringComparison.OrdinalIgnoreCase) ||
        op.Equals("addeach", StringComparison.OrdinalIgnoreCase) ||
        op.Equals("expression", StringComparison.OrdinalIgnoreCase);
}

internal static class DevToolsPatchPaths
{
    public static string Normalize(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? "" : path.Trim().TrimStart('/');
    }

    public static string[] Split(string path)
    {
        string normalized = Normalize(path);
        if (string.IsNullOrWhiteSpace(normalized)) return [];
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Replace("~1", "/").Replace("~0", "~"))
            .ToArray();
    }

    public static string Join(string basePath, string part)
    {
        string normalized = part.Replace("~", "~0").Replace("/", "~1");
        return string.IsNullOrWhiteSpace(basePath) ? normalized : $"{basePath}/{normalized}";
    }

    public static string Format(string path, DevToolsPatchOutputFormat format)
    {
        string normalized = Normalize(path);
        return format == DevToolsPatchOutputFormat.Vanilla ? "/" + normalized : normalized;
    }
}

internal static class DevToolsPatchJson
{
    public static string ToString(JToken token, Formatting formatting)
    {
        return JsonConvert.SerializeObject(token, formatting);
    }

    public static bool TryParse(string text, out JToken? token)
    {
        token = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            token = JToken.Parse(text);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
