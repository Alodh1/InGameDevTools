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
    private bool _configLibShowComplexValues = true;
    private bool _configLibIncludeStrings = true;
    private bool _configLibGenerateSeparators = true;
    private bool _configLibModConfigIncludedOnly;
    private int _configLibPreviewMode;
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
                    root,
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
        bool showComplexValues = _configLibShowComplexValues;
        if (ImGui.Checkbox("Show complex values##configlib-show-complex", ref showComplexValues))
        {
            _configLibShowComplexValues = showComplexValues;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Arrays and objects are listed for visibility, but are disabled until ConfigLib mapping export is implemented.");
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
                if (!_configLibShowComplexValues && IsConfigLibComplexSetting(setting)) continue;

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
                bool open = ImGui.TreeNodeEx($"##configlib-setting-node-{index}", ImGuiTreeNodeFlags.SpanAvailWidth, $"{setting.Code}  [{setting.Type}]  {setting.DefaultPreview}");

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(setting.Tooltip);
                }

                if (!selectableByType)
                {
                    ImGui.EndDisabled();
                }

                if (open)
                {
                    DrawConfigLibSettingAuthoringControls(setting, index, selectableByType);
                    ImGui.TreePop();
                }
            }

            ImGui.EndChild();
        }

        ImGui.EndChild();
    }

    private void DrawConfigLibSettingAuthoringControls(ConfigLibSettingDraft setting, int index, bool selectableByType)
    {
        if (!selectableByType)
        {
            ImGui.TextDisabled("Complex values are visible only until ConfigLib mapping export is implemented.");
            return;
        }

        string title = setting.Title;
        ImGui.SetNextItemWidth(-float.Epsilon);
        if (ImGui.InputText($"GUI title##configlib-title-{index}", ref title, 200))
        {
            setting.Title = title;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Emitted as title. The schema name remains the normalized kebab-case name.");
        }

        ImGui.TextDisabled($"Schema name: {setting.Name}");

        if (setting.IsNumeric)
        {
            bool useRange = setting.UseNumericRange;
            if (ImGui.Checkbox($"Range##configlib-range-{index}", ref useRange))
            {
                setting.UseNumericRange = useRange;
            }

            ImGui.SameLine();
            bool logarithmic = setting.Logarithmic;
            if (ImGui.Checkbox($"Logarithmic##configlib-log-{index}", ref logarithmic))
            {
                setting.Logarithmic = logarithmic;
            }

            if (!setting.UseNumericRange) ImGui.BeginDisabled();
            if (string.Equals(setting.Type, "integer", StringComparison.OrdinalIgnoreCase))
            {
                int min = (int)Math.Round(setting.Min);
                int max = (int)Math.Round(setting.Max);
                int step = Math.Max(1, (int)Math.Round(setting.Step));

                ImGui.SetNextItemWidth(120);
                if (ImGui.InputInt($"Min##configlib-min-{index}", ref min)) setting.Min = min;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputInt($"Max##configlib-max-{index}", ref max)) setting.Max = max;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputInt($"Step##configlib-step-{index}", ref step)) setting.Step = Math.Max(1, step);
            }
            else
            {
                float min = (float)setting.Min;
                float max = (float)setting.Max;
                float step = (float)setting.Step;

                ImGui.SetNextItemWidth(120);
                if (ImGui.DragFloat($"Min##configlib-min-{index}", ref min, 0.05f)) setting.Min = min;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                if (ImGui.DragFloat($"Max##configlib-max-{index}", ref max, 0.05f)) setting.Max = max;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                if (ImGui.DragFloat($"Step##configlib-step-{index}", ref step, 0.01f)) setting.Step = Math.Max(0.0001, step);
            }
            if (!setting.UseNumericRange) ImGui.EndDisabled();
        }

        if (string.Equals(setting.Type, "string", StringComparison.OrdinalIgnoreCase))
        {
            string values = setting.ValuesText;
            ImGui.SetNextItemWidth(-float.Epsilon);
            if (ImGui.InputText($"Dropdown values##configlib-values-{index}", ref values, 1024))
            {
                setting.ValuesText = values;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Comma or semicolon separated. The current default is included automatically when values are emitted.");
            }
        }
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

        bool clientSide = entry.ClientSide;
        if (ImGui.Checkbox("Client side settings##configlib-client-side", ref clientSide))
        {
            entry.ClientSide = clientSide;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("When enabled, generated settings emit clientSide: true. This is inferred from client-style config filenames and can be changed per file.");
        }

        ImGui.Checkbox("Separators##configlib-separators", ref _configLibGenerateSeparators);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Adds ConfigLib separator rows for top-level JSON objects.");
        }

        string targetDomain = GetConfigLibTargetDomain(entry);
        string patchOutputPath = GetConfigLibPatchOutputPath(targetDomain);
        string modConfigOutputPath = GetConfigLibModConfigOutputPath(entry);
        ImGui.TextWrapped($"File: {entry.RelativeFilePath}");
        ImGui.TextWrapped($"ConfigLib: {patchOutputPath}");
        ImGui.TextWrapped($"ModConfig: {modConfigOutputPath}");

        ImGui.Checkbox("ModConfig included settings only##configlib-modconfig-included-only", ref _configLibModConfigIncludedOnly);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Off copies the full selected ModConfig JSON. On emits only enabled settings; excluding required keys can make the target mod reject the generated config.");
        }

        string patchPreview = BuildConfigLibPatchJson(entry, targetDomain);
        string modConfigPreview = BuildConfigLibModConfigJson(entry);
        string[] previewModes = ["ConfigLib patches", "ModConfig JSON"];
        ImGui.Combo("Preview##configlib-preview-mode", ref _configLibPreviewMode, previewModes, previewModes.Length);
        _configLibPreviewMode = Math.Clamp(_configLibPreviewMode, 0, previewModes.Length - 1);
        string preview = _configLibPreviewMode == 0 ? patchPreview : modConfigPreview;

        if (ImGui.Button("Copy preview##configlib-copy"))
        {
            ImGui.SetClipboardText(preview);
            _configLibStatus = $"Copied {previewModes[_configLibPreviewMode]} to clipboard.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Save ConfigLib##configlib-save-patch"))
        {
            QueueSourceSave(TrySaveConfigLibPatch(entry, targetDomain, patchPreview), status => _configLibStatus = status);
        }
        ImGui.SameLine();
        if (ImGui.Button("Save ModConfig##configlib-save-modconfig"))
        {
            QueueSourceSave(TrySaveConfigLibModConfig(entry, modConfigPreview), status => _configLibStatus = status);
        }
        if (ImGui.Button("Save both authored files##configlib-save-both", new NVector2(-1, 0)))
        {
            QueueSourceSave(TrySaveConfigLibBundle(entry, targetDomain, patchPreview, modConfigPreview), status => _configLibStatus = status);
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
            string outputPath = GetConfigLibPatchOutputPath(targetDomain);
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

    private SourceSaveResult TrySaveConfigLibModConfig(ConfigLibSourceEntry entry, string newText)
    {
        try
        {
            string outputPath = GetConfigLibModConfigOutputPath(entry);
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : "";
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved ModConfig default for {entry.RelativeFilePath} to {outputPath}.",
                () => WriteAuthoredFile(outputPath, newText));
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            _configLibDiagnostics.Exception($"ModConfig save failed for {entry.RelativeFilePath}", exception);
            return SourceSaveResult.Fail($"ModConfig save failed for {entry.RelativeFilePath}: {exception.Message}");
        }
    }

    private SourceSaveResult TrySaveConfigLibBundle(ConfigLibSourceEntry entry, string targetDomain, string patchText, string modConfigText)
    {
        try
        {
            string patchPath = GetConfigLibPatchOutputPath(targetDomain);
            string modConfigPath = GetConfigLibModConfigOutputPath(entry);
            string oldText = BuildConfigLibBundlePreview(patchPath, File.Exists(patchPath) ? File.ReadAllText(patchPath) : "", modConfigPath, File.Exists(modConfigPath) ? File.ReadAllText(modConfigPath) : "");
            string newText = BuildConfigLibBundlePreview(patchPath, patchText, modConfigPath, modConfigText);
            SourceSaveRequest request = new(
                patchPath,
                oldText,
                newText,
                $"Saved ConfigLib definition and ModConfig default for {entry.RelativeFilePath}.",
                () =>
                {
                    WriteAuthoredFile(patchPath, patchText);
                    WriteAuthoredFile(modConfigPath, modConfigText);
                    return "";
                });
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            _configLibDiagnostics.Exception($"ConfigLib bundle save failed for {entry.RelativeFilePath}", exception);
            return SourceSaveResult.Fail($"Save failed for {entry.RelativeFilePath}: {exception.Message}");
        }
    }

    private string BuildConfigLibPatchJson(ConfigLibSourceEntry entry, string targetDomain)
    {
        JArray settings = [];
        JArray formatting = [];
        string lastSeparator = "";
        int order = 1;

        foreach (ConfigLibSettingDraft draft in GetIncludedConfigLibSettings(entry))
        {
            string separator = GetConfigLibSeparatorName(draft.Code);
            if (_configLibGenerateSeparators && !string.IsNullOrWhiteSpace(separator) && !string.Equals(separator, lastSeparator, StringComparison.OrdinalIgnoreCase))
            {
                formatting.Add(new JObject
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
            if (!string.IsNullOrWhiteSpace(draft.Title))
            {
                setting["title"] = draft.Title.Trim();
            }
            if (entry.ClientSide)
            {
                setting["clientSide"] = true;
            }
            if (draft.IsNumeric)
            {
                if (draft.UseNumericRange)
                {
                    double min = Math.Min(draft.Min, draft.Max);
                    double max = Math.Max(draft.Min, draft.Max);
                    setting["min"] = BuildConfigLibNumericToken(draft, min);
                    setting["max"] = BuildConfigLibNumericToken(draft, max);
                    if (draft.Step > 0)
                    {
                        setting["step"] = BuildConfigLibNumericToken(draft, draft.Step);
                    }
                }

                if (draft.Logarithmic)
                {
                    setting["logarithmic"] = true;
                }
            }
            if (string.Equals(draft.Type, "string", StringComparison.OrdinalIgnoreCase))
            {
                JArray values = BuildConfigLibStringValues(draft);
                if (values.Count > 0)
                {
                    setting["values"] = values;
                }
            }

            settings.Add(setting);
            order++;
        }

        JObject root = new()
        {
            ["version"] = _configLibVersion,
            ["file"] = entry.RelativeFilePath,
            ["settings"] = settings
        };

        if (formatting.Count > 0)
        {
            root["formatting"] = formatting;
        }

        _ = targetDomain;
        return root.ToString(Formatting.Indented);
    }

    private string BuildConfigLibModConfigJson(ConfigLibSourceEntry entry)
    {
        if (!_configLibModConfigIncludedOnly)
        {
            return entry.Root.ToString(Formatting.Indented);
        }

        JToken root = entry.Root is JArray ? new JArray() : new JObject();
        foreach (ConfigLibSettingDraft draft in GetIncludedConfigLibSettings(entry))
        {
            SetConfigLibTokenAtPath(ref root, draft.Code.Split('/', StringSplitOptions.RemoveEmptyEntries), draft.Default.DeepClone());
        }

        return root.ToString(Formatting.Indented);
    }

    private IEnumerable<ConfigLibSettingDraft> GetIncludedConfigLibSettings(ConfigLibSourceEntry entry)
    {
        return entry.Settings.Where(setting => setting.Include && IsConfigLibSettingSelectable(setting));
    }

    private bool IsConfigLibSettingSelectable(ConfigLibSettingDraft setting)
    {
        if (string.IsNullOrWhiteSpace(setting.Code)) return false;
        if (IsConfigLibComplexSetting(setting)) return false;
        if (!_configLibIncludeStrings && string.Equals(setting.Type, "string", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool IsConfigLibComplexSetting(ConfigLibSettingDraft setting)
    {
        return string.Equals(setting.Type, "other", StringComparison.OrdinalIgnoreCase);
    }

    private static JValue BuildConfigLibNumericToken(ConfigLibSettingDraft draft, double value)
    {
        return string.Equals(draft.Type, "integer", StringComparison.OrdinalIgnoreCase)
            ? new JValue((int)Math.Round(value))
            : new JValue(value);
    }

    private static JArray BuildConfigLibStringValues(ConfigLibSettingDraft draft)
    {
        JArray values = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        void AddValue(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed)) return;
            values.Add(trimmed);
        }

        foreach (string value in draft.ValuesText.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            AddValue(value);
        }

        if (values.Count == 0) return values;

        string defaultValue = draft.Default.Type == JTokenType.String ? draft.Default.ToString() : "";
        if (!string.IsNullOrWhiteSpace(defaultValue) && !seen.Contains(defaultValue))
        {
            values.Insert(0, new JValue(defaultValue));
            seen.Add(defaultValue);
        }

        return values;
    }

    private string GetConfigLibTargetDomain(ConfigLibSourceEntry entry)
    {
        string domain = _configLibTargetDomain.Trim();
        if (string.IsNullOrWhiteSpace(domain)) domain = entry.SuggestedDomain;
        return SanitizeConfigLibDomain(domain);
    }

    private static string GetConfigLibPatchOutputPath(string targetDomain)
    {
        return GetToolAuthoredAssetPath("configlib", Path.Combine("assets", targetDomain, "config", "configlib-patches.json"));
    }

    private static string GetConfigLibModConfigOutputPath(ConfigLibSourceEntry entry)
    {
        return GetToolAuthoredAssetPath("configlib", Path.Combine("ModConfig", entry.RelativeFilePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string BuildConfigLibBundlePreview(string patchPath, string patchText, string modConfigPath, string modConfigText)
    {
        return
            $"// {patchPath}" + Environment.NewLine +
            patchText + Environment.NewLine + Environment.NewLine +
            $"// {modConfigPath}" + Environment.NewLine +
            modConfigText;
    }

    private static void SetConfigLibTokenAtPath(ref JToken root, IReadOnlyList<string> parts, JToken value)
    {
        if (parts.Count == 0)
        {
            root = value;
            return;
        }

        if (root is not JObject && root is not JArray)
        {
            root = IsConfigLibArrayIndex(parts[0]) ? new JArray() : new JObject();
        }

        JToken current = root;
        for (int index = 0; index < parts.Count; index++)
        {
            string part = parts[index];
            bool last = index == parts.Count - 1;
            bool nextArray = !last && IsConfigLibArrayIndex(parts[index + 1]);

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

            if (current is JArray array && int.TryParse(part, out int arrayIndex) && arrayIndex >= 0)
            {
                EnsureConfigLibArraySize(array, arrayIndex + 1);
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
                continue;
            }

            return;
        }
    }

    private static bool IsConfigLibArrayIndex(string value)
    {
        return int.TryParse(value, out int index) && index >= 0;
    }

    private static void EnsureConfigLibArraySize(JArray array, int size)
    {
        while (array.Count < size)
        {
            array.Add(JValue.CreateNull());
        }
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
                        VisitConfigLibToken(property.Value, JoinConfigLibPath(path, property.Name), settings);
                    }
                }
                else
                {
                    settings.Add(ConfigLibSettingDraft.From(path, "other", MakeJsonCompatibleDefault(token)));
                    foreach (JProperty property in obj.Properties())
                    {
                        VisitConfigLibToken(property.Value, JoinConfigLibPath(path, property.Name), settings);
                    }
                }
                break;
            case JArray array:
                if (!string.IsNullOrWhiteSpace(path))
                {
                    settings.Add(ConfigLibSettingDraft.From(path, "other", MakeJsonCompatibleDefault(token)));
                }

                for (int index = 0; index < array.Count; index++)
                {
                    VisitConfigLibToken(array[index], JoinConfigLibPath(path, index.ToString()), settings);
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

    private static string JoinConfigLibPath(string path, string child)
    {
        return string.IsNullOrWhiteSpace(path) ? child : $"{path}/{child}";
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

    private static bool IsConfigLibClientSideConfig(string relativeFilePath)
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
        public ConfigLibSourceEntry(string filePath, string relativeFilePath, string displayName, string suggestedDomain, JToken root, List<ConfigLibSettingDraft> settings)
        {
            FilePath = filePath;
            RelativeFilePath = relativeFilePath;
            DisplayName = displayName;
            SuggestedDomain = suggestedDomain;
            Root = root.DeepClone();
            Settings = settings;
            ClientSide = IsConfigLibClientSideConfig(relativeFilePath);
            Key = filePath;
            SearchText = $"{displayName} {relativeFilePath} {suggestedDomain} {string.Join(' ', settings.Select(setting => setting.Code))}";
        }

        public string Key { get; }
        public string FilePath { get; }
        public string RelativeFilePath { get; }
        public string DisplayName { get; }
        public string SuggestedDomain { get; }
        public JToken Root { get; }
        public string SearchText { get; }
        public List<ConfigLibSettingDraft> Settings { get; }
        public bool ClientSide { get; set; }
    }

    private sealed class ConfigLibSettingDraft
    {
        private ConfigLibSettingDraft(string code, string type, JToken defaultValue)
        {
            Code = code;
            Type = type;
            Default = defaultValue;
            Include = !string.Equals(type, "other", StringComparison.OrdinalIgnoreCase);
            Name = SanitizeConfigLibName(code);
            Title = HumanizeConfigLibName(code);
            IsNumeric = string.Equals(type, "integer", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "float", StringComparison.OrdinalIgnoreCase);
            InitializeNumericDefaults(defaultValue);
            DefaultPreview = BuildDefaultPreview(defaultValue);
            Tooltip = string.Equals(type, "other", StringComparison.OrdinalIgnoreCase)
                ? $"{code}\nType: {type}\nArrays and objects need ConfigLib mapping support and are not exported yet.\nDefault: {DefaultPreview}"
                : $"{code}\nType: {type}\nDefault: {DefaultPreview}";
        }

        public string Code { get; }
        public string Name { get; }
        public string Type { get; }
        public JToken Default { get; }
        public bool Include { get; set; }
        public string Title { get; set; }
        public bool IsNumeric { get; }
        public bool UseNumericRange { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Step { get; set; }
        public bool Logarithmic { get; set; }
        public string ValuesText { get; set; } = "";
        public string DefaultPreview { get; }
        public string Tooltip { get; }

        public static ConfigLibSettingDraft From(string code, string type, JToken defaultValue)
        {
            return new(code, type, defaultValue);
        }

        private void InitializeNumericDefaults(JToken defaultValue)
        {
            if (!IsNumeric)
            {
                Min = 0;
                Max = 0;
                Step = 1;
                return;
            }

            double value = defaultValue.Value<double?>() ?? 0;
            double spread = Math.Max(1, Math.Abs(value));
            Min = Math.Floor(value - spread);
            Max = Math.Ceiling(value + spread);
            if (Math.Abs(Max - Min) < 0.0001)
            {
                Max = Min + 1;
            }

            Step = string.Equals(Type, "integer", StringComparison.OrdinalIgnoreCase) ? 1 : 0.1;
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
