using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NVector2 = System.Numerics.Vector2;
using Vintagestory.API.Client;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private readonly List<ConfigLibSourceEntry> _configLibSources = [];
    private readonly List<ConfigLibSourceEntry> _visibleConfigLibSources = [];
    private readonly ImGuiThreePanelLayoutState _configLibLayout = new(0.28f, 0.34f);
    private readonly DevToolsEditorDiagnostics _configLibDiagnostics = new("ConfigLib");
    private bool _configLibIndexed;
    private string _configLibFilter = "";
    private int _configLibSelectedIndex;
    private bool _configLibPrimitiveOnly = true;
    private bool _configLibIncludeStrings = true;
    private bool _configLibGenerateSeparators = true;
    private string _configLibTargetDomain = "";
    private int _configLibVersion;
    private string _configLibStatus = "";

    private void ConfigLibGeneratorTab(float deltaSeconds, bool showDiagnostics)
    {
        _ = deltaSeconds;
        ClearActiveTransformGizmo();

        try
        {
            EnsureConfigLibSourcesIndexed();

            NVector2 available = ImGui.GetContentRegionAvail();
            float scale = Math.Max(0.75f, _devToolsUiScale);
            float splitterThickness = Math.Max(5f, 6f * scale);
            ImGuiLayoutHelper.CalculateThreePanelWidths(
                available.X,
                splitterThickness,
                _configLibLayout,
                260f * scale,
                520f * scale,
                420f * scale,
                360f * scale,
                760f * scale,
                out float panelAvailableWidth,
                out float leftWidth,
                out float centerWidth,
                out float rightWidth);

            DrawConfigLibBrowser(new NVector2(leftWidth, available.Y));
            ImGui.SameLine(0, 0);
            ImGuiLayoutHelper.DrawVerticalSplitter("##configlib-left-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _configLibLayout.LeftFraction, 260f * scale, Math.Max(260f * scale, panelAvailableWidth - rightWidth - 420f * scale));
            ImGui.SameLine(0, 0);
            DrawConfigLibSettingsPanel(new NVector2(centerWidth, available.Y));
            ImGui.SameLine(0, 0);
            ImGuiLayoutHelper.DrawVerticalSplitter("##configlib-right-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _configLibLayout.RightFraction, 360f * scale, Math.Max(360f * scale, panelAvailableWidth - leftWidth - 420f * scale), invertDrag: true);
            ImGui.SameLine(0, 0);
            DrawConfigLibOutputPanel(new NVector2(rightWidth, available.Y), showDiagnostics);
        }
        catch (Exception exception)
        {
            _configLibStatus = $"ConfigLib generator error: {exception.Message}";
            _configLibDiagnostics.Exception("ConfigLib generator failed", exception);
            _api.Logger.Error("[InGameDevTools] ConfigLib generator failed: {0}", exception);
            ImGui.TextWrapped(_configLibStatus);
            _configLibDiagnostics.Draw("configlib-error", showDiagnostics);
        }
    }

    private void ResetConfigLibGeneratorLayout()
    {
        _configLibLayout.Reset();
    }

    private void EnsureConfigLibSourcesIndexed()
    {
        if (_configLibIndexed) return;

        _configLibSources.Clear();
        _visibleConfigLibSources.Clear();
        _configLibSelectedIndex = 0;

        string modConfigPath = Path.Combine(GetVintageStoryDataDirectory(), "ModConfig");
        if (!Directory.Exists(modConfigPath))
        {
            _configLibIndexed = true;
            _configLibStatus = $"ModConfig folder not found: {modConfigPath}";
            return;
        }

        foreach (string filePath in Directory.EnumerateFiles(modConfigPath, "*.json", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string fileName = Path.GetFileName(filePath);
            if (fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || fileName.Contains(".bak.", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                string text = File.ReadAllText(filePath);
                JToken root = JToken.Parse(text);
                string relativePath = Path.GetRelativePath(modConfigPath, filePath).Replace('\\', '/');
                List<ConfigLibSettingDraft> settings = BuildConfigLibSettingDrafts(root);
                if (settings.Count == 0) continue;

                _configLibSources.Add(new ConfigLibSourceEntry(
                    filePath,
                    relativePath,
                    fileName,
                    SuggestConfigLibDomain(relativePath),
                    settings));
            }
            catch (Exception exception)
            {
                _configLibDiagnostics.Warning($"Skipped {fileName}: {exception.Message}", exception.ToString());
            }
        }

        _configLibIndexed = true;
        RebuildVisibleConfigLibSources();
        _configLibStatus = $"Indexed {_configLibSources.Count} JSON config file(s).";
        SyncConfigLibTargetDomain(SelectedConfigLibSource);
    }

    private void RebuildVisibleConfigLibSources()
    {
        string filter = _configLibFilter.Trim();
        ConfigLibSourceEntry? selected = SelectedConfigLibSource;
        _visibleConfigLibSources.Clear();

        foreach (ConfigLibSourceEntry entry in _configLibSources)
        {
            if (!string.IsNullOrWhiteSpace(filter) && !entry.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            _visibleConfigLibSources.Add(entry);
        }

        if (selected != null)
        {
            int selectedIndex = _visibleConfigLibSources.FindIndex(entry => entry.Key == selected.Key);
            if (selectedIndex >= 0)
            {
                _configLibSelectedIndex = selectedIndex;
                return;
            }
        }

        _configLibSelectedIndex = Math.Clamp(_configLibSelectedIndex, 0, Math.Max(0, _visibleConfigLibSources.Count - 1));
    }

    private ConfigLibSourceEntry? SelectedConfigLibSource =>
        _visibleConfigLibSources.Count == 0
            ? null
            : _visibleConfigLibSources[Math.Clamp(_configLibSelectedIndex, 0, _visibleConfigLibSources.Count - 1)];

    private void DrawConfigLibBrowser(NVector2 size)
    {
        ImGui.BeginChild("##configlib-browser", size, true);
        ImGui.SeparatorText("ModConfig JSON");

        if (ImGui.Button("Reload##configlib-reload", new NVector2(-1, 0)))
        {
            _configLibIndexed = false;
            _configLibStatus = "";
            EnsureConfigLibSourcesIndexed();
        }

        if (ImGui.InputText("Filter##configlib-filter", ref _configLibFilter, 200))
        {
            RebuildVisibleConfigLibSources();
        }

        ImGui.TextDisabled($"{_visibleConfigLibSources.Count} / {_configLibSources.Count}");
        if (_visibleConfigLibSources.Count == 0)
        {
            ImGui.TextDisabled("No JSON ModConfig files with editable values.");
            ImGui.EndChild();
            return;
        }

        _configLibSelectedIndex = Math.Clamp(_configLibSelectedIndex, 0, _visibleConfigLibSources.Count - 1);
        if (ImGui.BeginListBox("##configlib-source-list", new NVector2(-float.Epsilon, Math.Max(140f, ImGui.GetContentRegionAvail().Y))))
        {
            for (int index = 0; index < _visibleConfigLibSources.Count; index++)
            {
                ConfigLibSourceEntry entry = _visibleConfigLibSources[index];
                bool selected = index == _configLibSelectedIndex;
                if (ImGui.Selectable($"{entry.DisplayName}##configlib-source-{index}", selected))
                {
                    _configLibSelectedIndex = index;
                    SyncConfigLibTargetDomain(entry);
                    _configLibStatus = $"Selected {entry.RelativeFilePath}.";
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{entry.RelativeFilePath}\n{entry.Settings.Count} inferred setting(s)\nSuggested domain: {entry.SuggestedDomain}");
                }
            }

            ImGui.EndListBox();
        }

        ImGui.EndChild();
    }

    private void DrawConfigLibSettingsPanel(NVector2 size)
    {
        ImGui.BeginChild("##configlib-settings", size, true);
        ConfigLibSourceEntry? entry = SelectedConfigLibSource;
        if (entry == null)
        {
            ImGui.TextDisabled("Select a ModConfig JSON file.");
            ImGui.EndChild();
            return;
        }

        ImGui.SeparatorText("Detected settings");
        bool primitiveOnly = _configLibPrimitiveOnly;
        if (ImGui.Checkbox("Primitive values only##configlib-primitive-only", ref primitiveOnly))
        {
            _configLibPrimitiveOnly = primitiveOnly;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("When enabled, arrays and objects are skipped instead of emitted as ConfigLib 'other' settings.");
        }

        ImGui.SameLine();
        bool includeStrings = _configLibIncludeStrings;
        if (ImGui.Checkbox("Strings##configlib-include-strings", ref includeStrings))
        {
            _configLibIncludeStrings = includeStrings;
        }

        int enabledCount = GetIncludedConfigLibSettings(entry).Count();
        ImGui.TextDisabled($"{enabledCount} included / {entry.Settings.Count} detected");
        if (ImGui.BeginChild("##configlib-setting-list", new NVector2(-float.Epsilon, Math.Max(120f, ImGui.GetContentRegionAvail().Y)), true))
        {
            for (int index = 0; index < entry.Settings.Count; index++)
            {
                ConfigLibSettingDraft setting = entry.Settings[index];
                bool selectableByType = IsConfigLibSettingSelectable(setting);
                bool include = setting.Include && selectableByType;
                if (!selectableByType)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.Checkbox($"##configlib-setting-enabled-{index}", ref include))
                {
                    setting.Include = include;
                }

                ImGui.SameLine();
                ImGui.TextUnformatted($"{setting.Code}  [{setting.Type}]");
                ImGui.SameLine();
                ImGui.TextDisabled(setting.DefaultPreview);

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(setting.Tooltip);
                }

                if (!selectableByType)
                {
                    ImGui.EndDisabled();
                }
            }

            ImGui.EndChild();
        }

        ImGui.EndChild();
    }

    private void DrawConfigLibOutputPanel(NVector2 size, bool showDiagnostics)
    {
        ImGui.BeginChild("##configlib-output", size, true);
        ConfigLibSourceEntry? entry = SelectedConfigLibSource;
        ImGui.SeparatorText("Output");

        if (entry == null)
        {
            ImGui.TextDisabled("No selected config file.");
            _configLibDiagnostics.Draw("configlib", showDiagnostics);
            ImGui.EndChild();
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Target domain##configlib-target-domain", ref _configLibTargetDomain, 120);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("The mod asset domain that will receive assets/<domain>/config/configlib-patches.json.");
        }

        if (ImGui.Button("Use suggested##configlib-use-suggested"))
        {
            _configLibTargetDomain = entry.SuggestedDomain;
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        ImGui.InputInt("Version##configlib-version", ref _configLibVersion);
        _configLibVersion = Math.Max(0, _configLibVersion);

        ImGui.Checkbox("Separators##configlib-separators", ref _configLibGenerateSeparators);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Adds ConfigLib separator rows for top-level JSON objects.");
        }

        string targetDomain = GetConfigLibTargetDomain(entry);
        string outputPath = GetConfigLibOutputPath(targetDomain);
        ImGui.TextWrapped($"File: {entry.RelativeFilePath}");
        ImGui.TextWrapped($"Output: {outputPath}");

        string preview = BuildConfigLibPatchJson(entry, targetDomain);
        if (ImGui.Button("Copy JSON##configlib-copy"))
        {
            ImGui.SetClipboardText(preview);
            _configLibStatus = "Copied ConfigLib JSON to clipboard.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Save authored file##configlib-save"))
        {
            QueueSourceSave(TrySaveConfigLibPatch(entry, targetDomain, preview), status => _configLibStatus = status);
        }

        ImGui.TextWrapped(_configLibStatus);
        ImGui.SeparatorText("Preview");
        ImGui.InputTextMultiline(
            "##configlib-preview-json",
            ref preview,
            (uint)Math.Max(4096, preview.Length + 1024),
            new NVector2(-1, Math.Max(140f, ImGui.GetContentRegionAvail().Y - 70f)),
            ImGuiInputTextFlags.ReadOnly);

        _configLibDiagnostics.Draw("configlib", showDiagnostics);
        ImGui.EndChild();
    }

    private void SyncConfigLibTargetDomain(ConfigLibSourceEntry? entry)
    {
        if (entry == null) return;
        _configLibTargetDomain = entry.SuggestedDomain;
    }

    private SourceSaveResult TrySaveConfigLibPatch(ConfigLibSourceEntry entry, string targetDomain, string newText)
    {
        try
        {
            string outputPath = GetConfigLibOutputPath(targetDomain);
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : "";
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved ConfigLib definition for {entry.RelativeFilePath} to {outputPath}.",
                () => WriteAuthoredFile(outputPath, newText));
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            _configLibDiagnostics.Exception($"ConfigLib save failed for {entry.RelativeFilePath}", exception);
            return SourceSaveResult.Fail($"Save failed for {entry.RelativeFilePath}: {exception.Message}");
        }
    }

    private string BuildConfigLibPatchJson(ConfigLibSourceEntry entry, string targetDomain)
    {
        JArray settings = [];
        string lastSeparator = "";
        int order = 1;

        foreach (ConfigLibSettingDraft draft in GetIncludedConfigLibSettings(entry))
        {
            string separator = GetConfigLibSeparatorName(draft.Code);
            if (_configLibGenerateSeparators && !string.IsNullOrWhiteSpace(separator) && !string.Equals(separator, lastSeparator, StringComparison.OrdinalIgnoreCase))
            {
                settings.Add(new JObject
                {
                    ["type"] = "separator",
                    ["title"] = HumanizeConfigLibName(separator),
                    ["weight"] = order - 0.5
                });
                lastSeparator = separator;
            }

            JObject setting = new()
            {
                ["code"] = draft.Code,
                ["comment"] = BuildConfigLibComment(draft),
                ["type"] = draft.Type,
                ["default"] = draft.Default.DeepClone(),
                ["weight"] = order
            };

            if (!string.IsNullOrWhiteSpace(draft.Name) && !string.Equals(draft.Name, draft.Code, StringComparison.Ordinal))
            {
                setting["name"] = draft.Name;
            }

            settings.Add(setting);
            order++;
        }

        JObject root = new()
        {
            ["version"] = _configLibVersion,
            ["file"] = entry.RelativeFilePath,
            ["patches"] = new JObject(),
            ["settings"] = settings
        };

        _ = targetDomain;
        return root.ToString(Formatting.Indented);
    }

    private IEnumerable<ConfigLibSettingDraft> GetIncludedConfigLibSettings(ConfigLibSourceEntry entry)
    {
        return entry.Settings.Where(setting => setting.Include && IsConfigLibSettingSelectable(setting));
    }

    private bool IsConfigLibSettingSelectable(ConfigLibSettingDraft setting)
    {
        if (!_configLibIncludeStrings && string.Equals(setting.Type, "string", StringComparison.OrdinalIgnoreCase)) return false;
        if (_configLibPrimitiveOnly && string.Equals(setting.Type, "other", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private string GetConfigLibTargetDomain(ConfigLibSourceEntry entry)
    {
        string domain = _configLibTargetDomain.Trim();
        if (string.IsNullOrWhiteSpace(domain)) domain = entry.SuggestedDomain;
        return SanitizeConfigLibDomain(domain);
    }

    private static string GetConfigLibOutputPath(string targetDomain)
    {
        return GetToolAuthoredAssetPath("configlib", Path.Combine("assets", targetDomain, "config", "configlib-patches.json"));
    }

    private static List<ConfigLibSettingDraft> BuildConfigLibSettingDrafts(JToken root)
    {
        List<ConfigLibSettingDraft> settings = [];
        VisitConfigLibToken(root, "", settings);
        return settings;
    }

    private static void VisitConfigLibToken(JToken token, string path, List<ConfigLibSettingDraft> settings)
    {
        switch (token)
        {
            case JObject obj:
                if (string.IsNullOrWhiteSpace(path))
                {
                    foreach (JProperty property in obj.Properties())
                    {
                        VisitConfigLibToken(property.Value, property.Name, settings);
                    }
                }
                else if (obj.Properties().All(property => IsConfigLibPrimitiveToken(property.Value)))
                {
                    foreach (JProperty property in obj.Properties())
                    {
                        VisitConfigLibToken(property.Value, $"{path}/{property.Name}", settings);
                    }
                }
                else
                {
                    settings.Add(ConfigLibSettingDraft.From(path, "other", MakeJsonCompatibleDefault(token)));
                    foreach (JProperty property in obj.Properties())
                    {
                        VisitConfigLibToken(property.Value, $"{path}/{property.Name}", settings);
                    }
                }
                break;
            case JArray array:
                if (array.Count > 0 && array.All(IsConfigLibPrimitiveToken))
                {
                    settings.Add(ConfigLibSettingDraft.From(path, "other", MakeJsonCompatibleDefault(token)));
                }
                else
                {
                    settings.Add(ConfigLibSettingDraft.From(path, "other", MakeJsonCompatibleDefault(token)));
                    for (int index = 0; index < array.Count; index++)
                    {
                        VisitConfigLibToken(array[index], $"{path}/{index}", settings);
                    }
                }
                break;
            default:
                if (!string.IsNullOrWhiteSpace(path) && TryGetConfigLibSettingType(token, out string type))
                {
                    settings.Add(ConfigLibSettingDraft.From(path, type, MakeJsonCompatibleDefault(token)));
                }
                break;
        }
    }

    private static bool IsConfigLibPrimitiveToken(JToken token)
    {
        return token.Type is JTokenType.Boolean or JTokenType.Integer or JTokenType.Float or JTokenType.String;
    }

    private static bool TryGetConfigLibSettingType(JToken token, out string type)
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

    private static JToken MakeJsonCompatibleDefault(JToken token)
    {
        if (token.Type == JTokenType.Null) return JValue.CreateNull();
        return token.DeepClone();
    }

    private static string BuildConfigLibComment(ConfigLibSettingDraft draft)
    {
        string defaultText = draft.DefaultPreview.Length > 80 ? draft.DefaultPreview[..80] + "..." : draft.DefaultPreview;
        return $"Generated from {draft.Code}. Default: {defaultText}.";
    }

    private static string GetConfigLibSeparatorName(string code)
    {
        int separator = code.IndexOf('/');
        return separator > 0 ? code[..separator] : "";
    }

    private static string SuggestConfigLibDomain(string relativeFilePath)
    {
        string stem = Path.GetFileNameWithoutExtension(relativeFilePath).Trim();
        if (string.IsNullOrWhiteSpace(stem)) return "generatedconfig";

        string lowered = stem.ToLowerInvariant();
        lowered = StripConfigAffix(lowered, "serverconfig");
        lowered = StripConfigAffix(lowered, "clientconfig");
        lowered = StripConfigAffix(lowered, "configserver");
        lowered = StripConfigAffix(lowered, "configclient");
        lowered = StripConfigAffix(lowered, "config");
        return SanitizeConfigLibDomain(string.IsNullOrWhiteSpace(lowered) ? stem : lowered);
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

    private static string SanitizeConfigLibDomain(string value)
    {
        string sanitized = new(value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')
            .ToArray());
        sanitized = sanitized.Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "generatedconfig" : sanitized;
    }

    private static string SanitizeConfigLibName(string value)
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

    private static string HumanizeConfigLibName(string value)
    {
        string normalized = value.Replace('/', ' ').Replace('-', ' ').Replace('_', ' ');
        return string.Join(' ', normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private sealed class ConfigLibSourceEntry
    {
        public ConfigLibSourceEntry(string filePath, string relativeFilePath, string displayName, string suggestedDomain, List<ConfigLibSettingDraft> settings)
        {
            FilePath = filePath;
            RelativeFilePath = relativeFilePath;
            DisplayName = displayName;
            SuggestedDomain = suggestedDomain;
            Settings = settings;
            Key = filePath;
            SearchText = $"{displayName} {relativeFilePath} {suggestedDomain} {string.Join(' ', settings.Select(setting => setting.Code))}";
        }

        public string Key { get; }
        public string FilePath { get; }
        public string RelativeFilePath { get; }
        public string DisplayName { get; }
        public string SuggestedDomain { get; }
        public string SearchText { get; }
        public List<ConfigLibSettingDraft> Settings { get; }
    }

    private sealed class ConfigLibSettingDraft
    {
        private ConfigLibSettingDraft(string code, string type, JToken defaultValue)
        {
            Code = code;
            Type = type;
            Default = defaultValue;
            Name = SanitizeConfigLibName(code);
            DefaultPreview = BuildDefaultPreview(defaultValue);
            Tooltip = $"{code}\nType: {type}\nDefault: {DefaultPreview}";
        }

        public string Code { get; }
        public string Name { get; }
        public string Type { get; }
        public JToken Default { get; }
        public bool Include { get; set; } = true;
        public string DefaultPreview { get; }
        public string Tooltip { get; }

        public static ConfigLibSettingDraft From(string code, string type, JToken defaultValue)
        {
            return new(code, type, defaultValue);
        }

        private static string BuildDefaultPreview(JToken token)
        {
            return token.Type switch
            {
                JTokenType.String => token.ToString(),
                JTokenType.Boolean or JTokenType.Integer or JTokenType.Float => token.ToString(Formatting.None),
                _ => token.ToString(Formatting.None)
            };
        }
    }
}
