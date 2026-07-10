using System.Globalization;
using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Common;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private static readonly string[] ConfigLibBrowserModeLabels = ["ModConfig JSON", "ConfigLib patch files", "Authored outputs"];
    private static readonly string[] ConfigLibPreviewModeLabels = ["ConfigLib patch JSON", "ModConfig JSON", "C# loader", "Order summary", "Diff"];
    private static readonly string[] ConfigLibSettingTypeLabels = ["boolean", "integer", "float", "string", "object", "array"];

    private readonly List<ConfigLibModConfigEntry> _configLibSources = [];
    private readonly List<ConfigLibModConfigEntry> _visibleConfigLibSources = [];
    private readonly List<ConfigLibPatchEntry> _configLibPatchEntries = [];
    private readonly List<ConfigLibPatchEntry> _visibleConfigLibPatchEntries = [];
    private readonly ImGuiThreePanelLayoutState _configLibLayout = new(0.28f, 0.34f);
    private readonly DevToolsEditorDiagnostics _configLibDiagnostics = new("ConfigLib");
    private readonly Dictionary<string, string> _configLibJsonBuffers = new(StringComparer.Ordinal);
    private DevToolsConfigLibDocumentDraft _configLibDocument = DevToolsConfigLibDocumentDraft.Empty();
    private bool _configLibIndexed;
    private int _configLibBrowserMode;
    private string _configLibFilter = "";
    private int _configLibSelectedIndex;
    private int _configLibSelectedSettingIndex;
    private int _configLibSelectedFormattingIndex;
    private string _configLibSettingFilter = "";
    private string _configLibNewStringValue = "";
    private bool _configLibShowDisabled;
    private bool _configLibModConfigIncludedOnly;
    private int _configLibPreviewMode;
    private string _configLibLoadedDocumentKey = "";
    private string _configLibOriginalPatchJson = "";
    private bool _configLibDocumentDirty;
    private string _configLibPendingDocumentAction = "";
    private string _configLibPendingDocumentKey = "";
    private bool _configLibOpenDiscardPopup;
    private string _configLibStatus = "ConfigLib editor ready.";

    private void ConfigLibGeneratorTab(float deltaSeconds, bool showDiagnostics)
    {
        _ = deltaSeconds;
        ClearActiveTransformGizmo();

        try
        {
            EnsureConfigLibSourcesIndexed();
            DrawConfigLibDiscardPopup();

            NVector2 available = ImGui.GetContentRegionAvail();
            float scale = Math.Max(0.75f, _devToolsUiScale);
            float splitterThickness = Math.Max(5f, 6f * scale);
            ImGuiLayoutHelper.CalculateThreePanelWidths(
                available.X,
                splitterThickness,
                _configLibLayout,
                280f * scale,
                540f * scale,
                460f * scale,
                380f * scale,
                820f * scale,
                out float panelAvailableWidth,
                out float leftWidth,
                out float centerWidth,
                out float rightWidth);

            DrawConfigLibBrowser(new NVector2(leftWidth, available.Y));
            ImGui.SameLine(0, 0);
            ImGuiLayoutHelper.DrawVerticalSplitter("##configlib-left-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _configLibLayout.LeftFraction, 280f * scale, Math.Max(280f * scale, panelAvailableWidth - rightWidth - 460f * scale));
            ImGui.SameLine(0, 0);
            DrawConfigLibDocumentPanel(new NVector2(centerWidth, available.Y));
            ImGui.SameLine(0, 0);
            ImGuiLayoutHelper.DrawVerticalSplitter("##configlib-right-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _configLibLayout.RightFraction, 380f * scale, Math.Max(380f * scale, panelAvailableWidth - leftWidth - 460f * scale), invertDrag: true);
            ImGui.SameLine(0, 0);
            DrawConfigLibOutputPanel(new NVector2(rightWidth, available.Y), showDiagnostics);
        }
        catch (Exception exception)
        {
            _configLibStatus = $"ConfigLib editor error: {exception.Message}";
            _configLibDiagnostics.Exception("ConfigLib editor failed", exception);
            _api.Logger.Error("[InGameDevTools] ConfigLib editor failed: {0}", exception);
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
        _configLibPatchEntries.Clear();
        _visibleConfigLibPatchEntries.Clear();
        _configLibSelectedIndex = 0;
        _configLibDiagnostics.Clear();

        IndexConfigLibModConfigSources();
        IndexConfigLibPatchSources();

        _configLibIndexed = true;
        RebuildVisibleConfigLibEntries();
        _configLibStatus = $"Indexed {_configLibSources.Count} ModConfig file(s), {_configLibPatchEntries.Count} ConfigLib patch file(s).";

        if (_configLibDocument.Settings.Count == 0 && _configLibSources.Count > 0)
        {
            ExecuteConfigLibGenerateFromModConfig(_configLibSources[0], markDirty: false);
        }
    }

    private void IndexConfigLibModConfigSources()
    {
        string modConfigPath = Path.Combine(GetVintageStoryDataDirectory(), "ModConfig");
        if (!Directory.Exists(modConfigPath))
        {
            _configLibDiagnostics.Warning($"ModConfig folder not found: {modConfigPath}", "Run the target mod once so it writes ModConfig JSON, then reload.");
            return;
        }

        foreach (string filePath in Directory.EnumerateFiles(modConfigPath, "*.json", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string fileName = Path.GetFileName(filePath);
            if (fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || fileName.Contains(".bak.", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                string text = File.ReadAllText(filePath);
                if (!DevToolsJson.TryParseToken(text, out JToken? root, out string error, useVintageStoryFallback: false) || root == null)
                {
                    _configLibDiagnostics.Warning($"Skipped {fileName}: {error}", text);
                    continue;
                }

                string relativePath = Path.GetRelativePath(modConfigPath, filePath).Replace('\\', '/');
                DevToolsConfigLibDocumentDraft inferred = DevToolsConfigLibDocumentDraft.FromModConfig(root, relativePath, DevToolsConfigLibDocumentDraft.SuggestDomain(relativePath));
                if (inferred.Settings.Count == 0) continue;

                _configLibSources.Add(new ConfigLibModConfigEntry(filePath, relativePath, root, inferred.Settings.Count));
            }
            catch (Exception exception)
            {
                _configLibDiagnostics.Warning($"Skipped {fileName}: {exception.Message}", exception.ToString());
            }
        }
    }

    private void IndexConfigLibPatchSources()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (IAsset asset in CollectToolAuthoredAssets("configlib", "config/"))
        {
            TryAddConfigLibPatchEntry(asset, authored: true, seen);
        }

        foreach (IAsset asset in _api.Assets.AllAssets.Values)
        {
            TryAddConfigLibPatchEntry(asset, authored: false, seen);
        }
    }

    private void TryAddConfigLibPatchEntry(IAsset? asset, bool authored, HashSet<string> seen)
    {
        if (asset?.Location == null) return;
        string assetPath = asset.Location.Path.Replace('\\', '/');
        if (!assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;
        if (!assetPath.StartsWith("config/", StringComparison.OrdinalIgnoreCase) &&
            !assetPath.Contains("configlib", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string text = ReadAssetText(asset);
        if (!DevToolsJson.TryParseObject(text, out JObject? root, out _, useVintageStoryFallback: false) || root == null) return;
        if (root["settings"] is not JArray) return;

        string key = $"{asset.Location.Domain}:{assetPath}:{authored}";
        if (!seen.Add(key)) return;

        _configLibPatchEntries.Add(new ConfigLibPatchEntry(asset, asset.Location.Domain ?? "game", assetPath, authored, root["settings"] is JArray settings ? settings.Count : 0));
    }

    private void RebuildVisibleConfigLibEntries()
    {
        string filter = _configLibFilter.Trim();
        _visibleConfigLibSources.Clear();
        _visibleConfigLibPatchEntries.Clear();

        foreach (ConfigLibModConfigEntry entry in _configLibSources)
        {
            if (!string.IsNullOrWhiteSpace(filter) && !entry.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            _visibleConfigLibSources.Add(entry);
        }

        foreach (ConfigLibPatchEntry entry in _configLibPatchEntries)
        {
            if (_configLibBrowserMode == 2 && !entry.Authored) continue;
            if (!string.IsNullOrWhiteSpace(filter) && !entry.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            _visibleConfigLibPatchEntries.Add(entry);
        }

        _configLibSelectedIndex = Math.Clamp(_configLibSelectedIndex, 0, Math.Max(0, CurrentConfigLibBrowserCount() - 1));
    }

    private int CurrentConfigLibBrowserCount()
    {
        return _configLibBrowserMode == 0 ? _visibleConfigLibSources.Count : _visibleConfigLibPatchEntries.Count;
    }

    private ConfigLibModConfigEntry? SelectedConfigLibSource =>
        _visibleConfigLibSources.Count == 0
            ? null
            : _visibleConfigLibSources[Math.Clamp(_configLibSelectedIndex, 0, _visibleConfigLibSources.Count - 1)];

    private ConfigLibPatchEntry? SelectedConfigLibPatch =>
        _visibleConfigLibPatchEntries.Count == 0
            ? null
            : _visibleConfigLibPatchEntries[Math.Clamp(_configLibSelectedIndex, 0, _visibleConfigLibPatchEntries.Count - 1)];

    private void DrawConfigLibBrowser(NVector2 size)
    {
        ImGui.BeginChild("##configlib-browser", size, true);
        ImGui.SeparatorText("ConfigLib sources");

        int browserMode = _configLibBrowserMode;
        if (ImGui.Combo("Mode##configlib-browser-mode", ref browserMode, ConfigLibBrowserModeLabels, ConfigLibBrowserModeLabels.Length))
        {
            _configLibBrowserMode = Math.Clamp(browserMode, 0, ConfigLibBrowserModeLabels.Length - 1);
            _configLibSelectedIndex = 0;
            RebuildVisibleConfigLibEntries();
        }

        if (ImGui.Button("New document##configlib-new", new NVector2(-1, 0)))
        {
            RequestConfigLibDocumentAction("new", "");
        }

        if (ImGui.Button("New scratch config##configlib-scratch", new NVector2(-1, 0)))
        {
            RequestConfigLibDocumentAction("scratch", "");
        }

        if (ImGui.Button("Clear document##configlib-clear", new NVector2(-1, 0)))
        {
            RequestConfigLibDocumentAction("clear", "");
        }

        if (ImGui.Button("Reload sources##configlib-reload", new NVector2(-1, 0)))
        {
            _configLibIndexed = false;
            _configLibStatus = "";
            EnsureConfigLibSourcesIndexed();
        }

        if (ImGui.InputText("Filter##configlib-filter", ref _configLibFilter, 200))
        {
            RebuildVisibleConfigLibEntries();
        }

        ImGui.TextDisabled(_configLibBrowserMode == 0
            ? $"{_visibleConfigLibSources.Count} / {_configLibSources.Count}"
            : $"{_visibleConfigLibPatchEntries.Count} / {_configLibPatchEntries.Count}");

        if (_configLibBrowserMode == 0)
        {
            DrawConfigLibModConfigBrowser();
        }
        else
        {
            DrawConfigLibPatchBrowser();
        }

        ImGui.EndChild();
    }

    private void DrawConfigLibModConfigBrowser()
    {
        if (_visibleConfigLibSources.Count == 0)
        {
            string modConfigPath = Path.Combine(GetVintageStoryDataDirectory(), "ModConfig");
            ImGui.TextWrapped(_configLibSources.Count == 0
                ? $"No editable JSON ModConfig files were found in {modConfigPath}."
                : "No ModConfig files match the current filter.");
            return;
        }

        _configLibSelectedIndex = Math.Clamp(_configLibSelectedIndex, 0, _visibleConfigLibSources.Count - 1);
        if (ImGui.BeginListBox("##configlib-modconfig-list", new NVector2(-float.Epsilon, Math.Max(140f, ImGui.GetContentRegionAvail().Y - 104f))))
        {
            for (int index = 0; index < _visibleConfigLibSources.Count; index++)
            {
                ConfigLibModConfigEntry entry = _visibleConfigLibSources[index];
                if (ImGui.Selectable($"{entry.DisplayName}##configlib-source-{index}", index == _configLibSelectedIndex))
                {
                    _configLibSelectedIndex = index;
                    _configLibStatus = $"Selected {entry.RelativeFilePath}.";
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{entry.RelativeFilePath}\n{entry.SettingCount} inferred setting(s)\nSuggested domain: {entry.SuggestedDomain}");
                }
            }

            ImGui.EndListBox();
        }

        ConfigLibModConfigEntry? selected = SelectedConfigLibSource;
        if (selected == null) return;

        if (ImGui.Button("Generate from ModConfig##configlib-generate", new NVector2(-1, 0)))
        {
            RequestConfigLibDocumentAction("generate", selected.Key);
        }

        if (ImGui.Button("Merge from ModConfig##configlib-merge", new NVector2(-1, 0)))
        {
            ExecuteConfigLibMergeFromModConfig(selected);
        }
    }

    private void DrawConfigLibPatchBrowser()
    {
        if (_visibleConfigLibPatchEntries.Count == 0)
        {
            ImGui.TextWrapped(_configLibBrowserMode == 2
                ? "No authored ConfigLib patch files match the current filter."
                : "No ConfigLib patch files match the current filter.");
            return;
        }

        _configLibSelectedIndex = Math.Clamp(_configLibSelectedIndex, 0, _visibleConfigLibPatchEntries.Count - 1);
        if (ImGui.BeginListBox("##configlib-patch-list", new NVector2(-float.Epsilon, Math.Max(140f, ImGui.GetContentRegionAvail().Y - 72f))))
        {
            for (int index = 0; index < _visibleConfigLibPatchEntries.Count; index++)
            {
                ConfigLibPatchEntry entry = _visibleConfigLibPatchEntries[index];
                string label = entry.Authored ? $"{entry.DisplayName} [authored]" : entry.DisplayName;
                if (ImGui.Selectable($"{label}##configlib-patch-{index}", index == _configLibSelectedIndex))
                {
                    _configLibSelectedIndex = index;
                    _configLibStatus = $"Selected {entry.Domain}:{entry.AssetPath}.";
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{entry.Domain}:{entry.AssetPath}\n{entry.SettingCount} setting(s)");
                }
            }

            ImGui.EndListBox();
        }

        ConfigLibPatchEntry? selected = SelectedConfigLibPatch;
        if (selected == null) return;

        if (ImGui.Button("Import selected patch##configlib-import", new NVector2(-1, 0)))
        {
            RequestConfigLibDocumentAction("import", selected.Key);
        }
    }

    private void RequestConfigLibDocumentAction(string action, string key)
    {
        if (_configLibDocumentDirty)
        {
            _configLibPendingDocumentAction = action;
            _configLibPendingDocumentKey = key;
            _configLibOpenDiscardPopup = true;
            return;
        }

        ExecuteConfigLibDocumentAction(action, key);
    }

    private void DrawConfigLibDiscardPopup()
    {
        const string popupId = "Discard ConfigLib document changes?";
        if (_configLibOpenDiscardPopup)
        {
            ImGui.OpenPopup(popupId);
            _configLibOpenDiscardPopup = false;
        }

        bool open = true;
        if (!ImGui.BeginPopupModal(popupId, ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped("The current ConfigLib document has unsaved changes.");
        ImGui.TextWrapped("Discard them and continue?");
        if (ImGui.Button("Discard changes##configlib-discard-yes"))
        {
            ExecuteConfigLibDocumentAction(_configLibPendingDocumentAction, _configLibPendingDocumentKey);
            _configLibPendingDocumentAction = "";
            _configLibPendingDocumentKey = "";
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep editing##configlib-discard-no"))
        {
            _configLibPendingDocumentAction = "";
            _configLibPendingDocumentKey = "";
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void ExecuteConfigLibDocumentAction(string action, string key)
    {
        if (action.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            _configLibDocument = DevToolsConfigLibDocumentDraft.Empty("generatedconfig");
            _configLibLoadedDocumentKey = "";
            _configLibOriginalPatchJson = _configLibDocument.ToPatchJson();
            _configLibDocumentDirty = false;
            _configLibSelectedSettingIndex = 0;
            _configLibSelectedFormattingIndex = 0;
            _configLibJsonBuffers.Clear();
            _configLibStatus = "Started a new ConfigLib document.";
            return;
        }

        if (action.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _configLibDocument = DevToolsConfigLibDocumentDraft.Empty(_configLibDocument.Domain);
            _configLibLoadedDocumentKey = "";
            _configLibDocumentDirty = true;
            _configLibSelectedSettingIndex = 0;
            _configLibSelectedFormattingIndex = 0;
            _configLibJsonBuffers.Clear();
            _configLibStatus = "Cleared ConfigLib document.";
            return;
        }

        if (action.Equals("scratch", StringComparison.OrdinalIgnoreCase))
        {
            _configLibDocument = DevToolsConfigLibDocumentDraft.Scratch(_configLibDocument.Domain);
            _configLibLoadedDocumentKey = "";
            _configLibOriginalPatchJson = _configLibDocument.ToPatchJson();
            _configLibDocumentDirty = true;
            _configLibSelectedSettingIndex = 0;
            _configLibSelectedFormattingIndex = 0;
            _configLibJsonBuffers.Clear();
            _configLibStatus = "Started a scratch ConfigLib document with ModConfig and C# loader outputs.";
            return;
        }

        if (action.Equals("generate", StringComparison.OrdinalIgnoreCase))
        {
            ConfigLibModConfigEntry? entry = _configLibSources.FirstOrDefault(source => source.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (entry != null) ExecuteConfigLibGenerateFromModConfig(entry, markDirty: true);
            return;
        }

        if (action.Equals("import", StringComparison.OrdinalIgnoreCase))
        {
            ConfigLibPatchEntry? entry = _configLibPatchEntries.FirstOrDefault(patch => patch.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (entry != null) ExecuteConfigLibImportPatch(entry);
        }
    }

    private void ExecuteConfigLibGenerateFromModConfig(ConfigLibModConfigEntry entry, bool markDirty)
    {
        _configLibDocument = DevToolsConfigLibDocumentDraft.FromModConfig(entry.Root, entry.RelativeFilePath, entry.SuggestedDomain);
        foreach (DevToolsConfigLibSettingDraft setting in _configLibDocument.Settings)
        {
            setting.ClientSide = entry.ClientSide;
        }

        _configLibLoadedDocumentKey = $"modconfig:{entry.Key}";
        _configLibOriginalPatchJson = _configLibDocument.ToPatchJson();
        _configLibDocumentDirty = markDirty;
        _configLibSelectedSettingIndex = 0;
        _configLibSelectedFormattingIndex = 0;
        _configLibJsonBuffers.Clear();
        _configLibStatus = $"Generated ConfigLib document from {entry.RelativeFilePath}.";
    }

    private void ExecuteConfigLibMergeFromModConfig(ConfigLibModConfigEntry entry)
    {
        _configLibDocument.MergeFromModConfig(entry.Root, entry.RelativeFilePath);
        _configLibDocument.ModConfigRelativePath = entry.RelativeFilePath;
        MarkConfigLibDocumentDirty($"Merged settings from {entry.RelativeFilePath}.");
    }

    private void ExecuteConfigLibImportPatch(ConfigLibPatchEntry entry)
    {
        try
        {
            string text = ReadAssetText(entry.Asset);
            _configLibDocument = DevToolsConfigLibDocumentDraft.FromPatchJson(text, entry.Domain, DevToolsConfigLibDocumentDraft.ExtractRelativePatchPath(entry.AssetPath));
            _configLibLoadedDocumentKey = entry.Key;
            _configLibOriginalPatchJson = _configLibDocument.ToPatchJson();
            _configLibDocumentDirty = false;
            _configLibSelectedSettingIndex = 0;
            _configLibSelectedFormattingIndex = 0;
            _configLibJsonBuffers.Clear();
            _configLibStatus = $"Imported {entry.Domain}:{entry.AssetPath}.";
        }
        catch (Exception exception)
        {
            _configLibStatus = $"Import failed: {exception.Message}";
            _configLibDiagnostics.Exception($"ConfigLib import failed for {entry.Domain}:{entry.AssetPath}", exception);
        }
    }

    private void MarkConfigLibDocumentDirty(string status)
    {
        _configLibDocumentDirty = true;
        _configLibStatus = status;
    }

    private void DrawConfigLibDocumentPanel(NVector2 size)
    {
        ImGui.BeginChild("##configlib-document", size, true);
        ImGui.SeparatorText("Settings");

        ImGui.TextDisabled(_configLibDocumentDirty ? "Unsaved changes" : "Saved/imported state");
        ImGui.SameLine();
        ImGui.TextWrapped(_configLibStatus);

        if (ImGui.InputText("Filter settings##configlib-setting-filter", ref _configLibSettingFilter, 200))
        {
            _configLibSelectedSettingIndex = Math.Clamp(_configLibSelectedSettingIndex, 0, Math.Max(0, _configLibDocument.Settings.Count - 1));
        }
        ImGui.SameLine();
        ImGui.Checkbox("Show disabled##configlib-show-disabled", ref _configLibShowDisabled);

        DrawConfigLibSettingToolbar();
        DrawConfigLibSettingListAndEditor();
        DrawConfigLibFormattingEditor();

        ImGui.EndChild();
    }

    private void DrawConfigLibSettingToolbar()
    {
        if (ImGui.Button("Add setting##configlib-add-setting"))
        {
            DevToolsConfigLibSettingDraft setting = DevToolsConfigLibSettingDraft.FromInferred(MakeUniqueConfigLibSettingCode("new-setting"), "string", new JValue(""));
            setting.Weight = _configLibDocument.Settings.Count == 0 ? 1 : _configLibDocument.Settings.Max(row => row.Weight) + 1;
            _configLibDocument.Settings.Add(setting);
            _configLibSelectedSettingIndex = _configLibDocument.Settings.Count - 1;
            MarkConfigLibDocumentDirty("Added ConfigLib setting.");
        }

        bool hasSelection = _configLibDocument.Settings.Count > 0 &&
            _configLibSelectedSettingIndex >= 0 &&
            _configLibSelectedSettingIndex < _configLibDocument.Settings.Count;
        if (!hasSelection) ImGui.BeginDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Duplicate##configlib-duplicate-setting"))
        {
            DevToolsConfigLibSettingDraft clone = _configLibDocument.Settings[_configLibSelectedSettingIndex].Clone();
            clone.Code = MakeUniqueConfigLibSettingCode($"{clone.Code}-copy");
            clone.Name = DevToolsConfigLibDocumentDraft.SanitizeName(clone.Code);
            _configLibDocument.Settings.Insert(_configLibSelectedSettingIndex + 1, clone);
            _configLibSelectedSettingIndex++;
            MarkConfigLibDocumentDirty("Duplicated ConfigLib setting.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Remove##configlib-remove-setting"))
        {
            _configLibDocument.Settings.RemoveAt(_configLibSelectedSettingIndex);
            _configLibSelectedSettingIndex = Math.Clamp(_configLibSelectedSettingIndex, 0, Math.Max(0, _configLibDocument.Settings.Count - 1));
            MarkConfigLibDocumentDirty("Removed ConfigLib setting.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Top##configlib-setting-top")) MoveConfigLibSetting(_configLibSelectedSettingIndex, 0);
        ImGui.SameLine();
        if (ImGui.Button("Up##configlib-setting-up")) MoveConfigLibSetting(_configLibSelectedSettingIndex, _configLibSelectedSettingIndex - 1);
        ImGui.SameLine();
        if (ImGui.Button("Down##configlib-setting-down")) MoveConfigLibSetting(_configLibSelectedSettingIndex, _configLibSelectedSettingIndex + 1);
        ImGui.SameLine();
        if (ImGui.Button("Bottom##configlib-setting-bottom")) MoveConfigLibSetting(_configLibSelectedSettingIndex, _configLibDocument.Settings.Count - 1);
        if (!hasSelection) ImGui.EndDisabled();
    }

    private void DrawConfigLibSettingListAndEditor()
    {
        float listHeight = MathF.Max(120f, MathF.Min(240f, ImGui.GetContentRegionAvail().Y * 0.32f));
        if (ImGui.BeginChild("##configlib-setting-list", new NVector2(-float.Epsilon, listHeight), true))
        {
            for (int index = 0; index < _configLibDocument.Settings.Count; index++)
            {
                DevToolsConfigLibSettingDraft setting = _configLibDocument.Settings[index];
                if (!_configLibShowDisabled && !setting.Enabled) continue;
                if (!ConfigLibSettingMatchesFilter(setting)) continue;

                string disabled = setting.Enabled ? "" : " [disabled]";
                string label = $"{setting.Code}  [{setting.Type}]  {BuildConfigLibDefaultPreview(setting)}{disabled}##configlib-setting-{index}";
                if (ImGui.Selectable(label, index == _configLibSelectedSettingIndex))
                {
                    _configLibSelectedSettingIndex = index;
                }
            }

            ImGui.EndChild();
        }

        if (_configLibDocument.Settings.Count == 0)
        {
            ImGui.TextDisabled("No ConfigLib settings. Add one or generate from a ModConfig file.");
            return;
        }

        _configLibSelectedSettingIndex = Math.Clamp(_configLibSelectedSettingIndex, 0, _configLibDocument.Settings.Count - 1);
        DrawConfigLibSettingDetails(_configLibDocument.Settings[_configLibSelectedSettingIndex], _configLibSelectedSettingIndex);
    }

    private void DrawConfigLibSettingDetails(DevToolsConfigLibSettingDraft setting, int index)
    {
        ImGui.SeparatorText($"Setting {index + 1}");

        bool enabled = setting.Enabled;
        if (ImGui.Checkbox($"Enabled##configlib-setting-enabled-{index}", ref enabled))
        {
            setting.Enabled = enabled;
            MarkConfigLibDocumentDirty("Updated setting enabled state.");
        }

        ImGui.SameLine();
        bool clientSide = setting.ClientSide;
        if (ImGui.Checkbox($"Client side##configlib-setting-client-{index}", ref clientSide))
        {
            setting.ClientSide = clientSide;
            MarkConfigLibDocumentDirty("Updated setting side.");
        }

        ImGui.SameLine();
        bool logarithmic = setting.Logarithmic;
        if (ImGui.Checkbox($"Logarithmic##configlib-setting-log-{index}", ref logarithmic))
        {
            setting.Logarithmic = logarithmic;
            MarkConfigLibDocumentDirty("Updated setting scale.");
        }

        string code = setting.Code;
        if (ImGui.InputText($"Code##configlib-setting-code-{index}", ref code, 256))
        {
            setting.Code = code;
            MarkConfigLibDocumentDirty("Updated setting code.");
        }

        string name = setting.Name;
        if (ImGui.InputText($"Name##configlib-setting-name-{index}", ref name, 256))
        {
            setting.Name = name;
            MarkConfigLibDocumentDirty("Updated setting name.");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Leave blank to omit the optional ConfigLib name field.");
        }

        string title = setting.Title;
        if (ImGui.InputText($"Title##configlib-setting-title-{index}", ref title, 256))
        {
            setting.Title = title;
            MarkConfigLibDocumentDirty("Updated setting title.");
        }

        string comment = setting.Comment;
        if (ImGui.InputTextMultiline($"Comment##configlib-setting-comment-{index}", ref comment, 4096, new NVector2(-float.Epsilon, 54f), ImGuiInputTextFlags.AllowTabInput))
        {
            setting.Comment = comment;
            MarkConfigLibDocumentDirty("Updated setting comment.");
        }

        DrawConfigLibTypeEditor(setting, index);
        DrawConfigLibDefaultEditor(setting, index);
        DrawConfigLibRangeEditor(setting, index);
        DrawConfigLibValuesEditor(setting, index);

        float weight = (float)setting.Weight;
        if (ImGui.DragFloat($"Weight##configlib-setting-weight-{index}", ref weight, 0.1f, -100000f, 100000f, "%.2f"))
        {
            setting.Weight = weight;
            MarkConfigLibDocumentDirty("Updated setting weight.");
        }

        DrawConfigLibJsonObjectEditor(
            "Extra fields",
            $"setting-extra-{index}-{setting.Code}",
            setting.Extra,
            obj =>
            {
                setting.Extra = obj;
                MarkConfigLibDocumentDirty("Updated setting extra fields.");
            });
    }

    private void DrawConfigLibTypeEditor(DevToolsConfigLibSettingDraft setting, int index)
    {
        int typeIndex = Array.FindIndex(ConfigLibSettingTypeLabels, label => label.Equals(setting.Type, StringComparison.OrdinalIgnoreCase));
        string type = setting.Type;
        if (typeIndex >= 0)
        {
            if (ImGui.Combo($"Type##configlib-setting-type-{index}", ref typeIndex, ConfigLibSettingTypeLabels, ConfigLibSettingTypeLabels.Length))
            {
                setting.Type = ConfigLibSettingTypeLabels[Math.Clamp(typeIndex, 0, ConfigLibSettingTypeLabels.Length - 1)];
                MarkConfigLibDocumentDirty("Updated setting type.");
            }
        }
        else
        {
            ImGui.TextDisabled($"Custom type: {setting.Type}");
        }

        if (ImGui.InputText($"Custom type##configlib-setting-type-custom-{index}", ref type, 120))
        {
            setting.Type = type.Trim();
            MarkConfigLibDocumentDirty("Updated setting type.");
        }
    }

    private void DrawConfigLibDefaultEditor(DevToolsConfigLibSettingDraft setting, int index)
    {
        ImGui.SeparatorText("Default");
        string type = setting.Type.Trim();
        if (type.Equals("boolean", StringComparison.OrdinalIgnoreCase) &&
            setting.TryGetDefaultToken(out JToken? boolToken, out _) &&
            boolToken?.Type == JTokenType.Boolean)
        {
            bool value = boolToken.Value<bool>();
            if (ImGui.Checkbox($"Value##configlib-default-bool-{index}", ref value))
            {
                setting.DefaultJson = value ? "true" : "false";
                MarkConfigLibDocumentDirty("Updated default value.");
            }
            return;
        }

        if (type.Equals("integer", StringComparison.OrdinalIgnoreCase) &&
            setting.TryGetDefaultToken(out JToken? intToken, out _) &&
            intToken?.Type is JTokenType.Integer or JTokenType.Float)
        {
            int value = intToken.Value<int>();
            if (ImGui.InputInt($"Value##configlib-default-int-{index}", ref value))
            {
                setting.DefaultJson = value.ToString(CultureInfo.InvariantCulture);
                MarkConfigLibDocumentDirty("Updated default value.");
            }
            return;
        }

        if (type.Equals("float", StringComparison.OrdinalIgnoreCase) &&
            setting.TryGetDefaultToken(out JToken? floatToken, out _) &&
            floatToken?.Type is JTokenType.Integer or JTokenType.Float)
        {
            float value = floatToken.Value<float>();
            if (ImGui.DragFloat($"Value##configlib-default-float-{index}", ref value, 0.01f, -1_000_000f, 1_000_000f, "%.4f"))
            {
                setting.DefaultJson = value.ToString("0.####", CultureInfo.InvariantCulture);
                MarkConfigLibDocumentDirty("Updated default value.");
            }
            return;
        }

        if (type.Equals("string", StringComparison.OrdinalIgnoreCase) &&
            setting.TryGetDefaultToken(out JToken? stringToken, out _) &&
            stringToken?.Type == JTokenType.String)
        {
            string value = stringToken.ToString();
            if (ImGui.InputText($"Value##configlib-default-string-{index}", ref value, 4096))
            {
                setting.DefaultJson = JsonConvert.SerializeObject(value);
                MarkConfigLibDocumentDirty("Updated default value.");
            }
        }

        string defaultJson = setting.DefaultJson;
        if (ImGui.InputTextMultiline($"Default JSON##configlib-default-json-{index}", ref defaultJson, DevToolsImGuiTextBuffer.Capacity(defaultJson), new NVector2(-float.Epsilon, 96f), ImGuiInputTextFlags.AllowTabInput))
        {
            setting.DefaultJson = defaultJson;
            MarkConfigLibDocumentDirty("Updated default JSON.");
        }
        ImGui.SameLine();
        if (ImGui.Button($"Format default##configlib-format-default-{index}"))
        {
            if (DevToolsJsonTextTools.TryFormat(setting.DefaultJson, out string formatted, out string error))
            {
                setting.DefaultJson = formatted;
                MarkConfigLibDocumentDirty("Formatted default JSON.");
            }
            else
            {
                _configLibStatus = $"Default format failed: {error}";
            }
        }
    }

    private void DrawConfigLibRangeEditor(DevToolsConfigLibSettingDraft setting, int index)
    {
        bool numeric = setting.Type.Equals("integer", StringComparison.OrdinalIgnoreCase) ||
            setting.Type.Equals("float", StringComparison.OrdinalIgnoreCase);
        if (!numeric) return;

        ImGui.SeparatorText("Range");
        bool hasRange = setting.HasRange;
        if (ImGui.Checkbox($"Emit range##configlib-range-enabled-{index}", ref hasRange))
        {
            setting.HasRange = hasRange;
            MarkConfigLibDocumentDirty("Updated range state.");
        }

        if (!setting.HasRange) return;

        float min = ParseConfigLibFloat(setting.RangeMinJson);
        float max = ParseConfigLibFloat(setting.RangeMaxJson);
        float step = ParseConfigLibFloat(setting.RangeStepJson);
        ImGui.SetNextItemWidth(120);
        if (ImGui.DragFloat($"Min##configlib-range-min-{index}", ref min, 0.05f, -1_000_000f, 1_000_000f, "%.4f"))
        {
            setting.RangeMinJson = min.ToString("0.####", CultureInfo.InvariantCulture);
            MarkConfigLibDocumentDirty("Updated range min.");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.DragFloat($"Max##configlib-range-max-{index}", ref max, 0.05f, -1_000_000f, 1_000_000f, "%.4f"))
        {
            setting.RangeMaxJson = max.ToString("0.####", CultureInfo.InvariantCulture);
            MarkConfigLibDocumentDirty("Updated range max.");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.DragFloat($"Step##configlib-range-step-{index}", ref step, 0.01f, 0f, 1_000_000f, "%.4f"))
        {
            setting.RangeStepJson = step.ToString("0.####", CultureInfo.InvariantCulture);
            MarkConfigLibDocumentDirty("Updated range step.");
        }

        DrawConfigLibJsonObjectEditor(
            "Range extra fields",
            $"setting-range-extra-{index}-{setting.Code}",
            setting.RangeExtra,
            obj =>
            {
                setting.RangeExtra = obj;
                MarkConfigLibDocumentDirty("Updated range extra fields.");
            });
    }

    private void DrawConfigLibValuesEditor(DevToolsConfigLibSettingDraft setting, int index)
    {
        if (!setting.Type.Equals("string", StringComparison.OrdinalIgnoreCase)) return;

        ImGui.SeparatorText("Dropdown values");
        JArray values = [];
        bool valuesValid = true;
        if (!string.IsNullOrWhiteSpace(setting.ValuesJson))
        {
            valuesValid = DevToolsJson.TryParseToken(setting.ValuesJson, out JToken? valuesToken, out _, useVintageStoryFallback: false) && valuesToken is JArray;
            if (valuesValid && valuesToken is JArray parsed)
            {
                values = parsed;
            }
        }

        if (valuesValid)
        {
            for (int valueIndex = 0; valueIndex < values.Count; valueIndex++)
            {
                string value = values[valueIndex]?.ToString() ?? "";
                ImGui.SetNextItemWidth(-80f);
                if (ImGui.InputText($"##configlib-value-{index}-{valueIndex}", ref value, 512))
                {
                    values[valueIndex] = value;
                    setting.ValuesJson = values.Count == 0 ? "" : JsonConvert.SerializeObject(values, Formatting.Indented);
                    MarkConfigLibDocumentDirty("Updated dropdown value.");
                }
                ImGui.SameLine();
                if (ImGui.Button($"Remove##configlib-remove-value-{index}-{valueIndex}"))
                {
                    values.RemoveAt(valueIndex);
                    setting.ValuesJson = values.Count == 0 ? "" : JsonConvert.SerializeObject(values, Formatting.Indented);
                    MarkConfigLibDocumentDirty("Removed dropdown value.");
                    break;
                }
            }

            ImGui.InputText($"New value##configlib-new-value-{index}", ref _configLibNewStringValue, 512);
            ImGui.SameLine();
            if (ImGui.Button($"Add value##configlib-add-value-{index}"))
            {
                if (!string.IsNullOrWhiteSpace(_configLibNewStringValue))
                {
                    values.Add(_configLibNewStringValue);
                    setting.ValuesJson = JsonConvert.SerializeObject(values, Formatting.Indented);
                    _configLibNewStringValue = "";
                    MarkConfigLibDocumentDirty("Added dropdown value.");
                }
            }
        }

        if (!valuesValid)
        {
            ImGui.TextColored(new NVector4(1f, 0.52f, 0.32f, 1f), "Values JSON is invalid; edit raw values below.");
        }

        string valuesJson = setting.ValuesJson;
        if (ImGui.InputTextMultiline($"Values JSON##configlib-values-json-{index}", ref valuesJson, DevToolsImGuiTextBuffer.Capacity(valuesJson), new NVector2(-float.Epsilon, 74f), ImGuiInputTextFlags.AllowTabInput))
        {
            setting.ValuesJson = valuesJson;
            MarkConfigLibDocumentDirty("Updated values JSON.");
        }
    }

    private void DrawConfigLibFormattingEditor()
    {
        if (!ImGui.CollapsingHeader("Formatting rows##configlib-formatting", ImGuiTreeNodeFlags.DefaultOpen)) return;

        if (ImGui.Button("Add separator##configlib-format-add"))
        {
            _configLibDocument.Formatting.Add(new DevToolsConfigLibFormattingDraft
            {
                Type = "separator",
                Title = "New Section",
                Weight = _configLibDocument.Settings.Count + _configLibDocument.Formatting.Count + 0.5
            });
            _configLibSelectedFormattingIndex = _configLibDocument.Formatting.Count - 1;
            MarkConfigLibDocumentDirty("Added formatting row.");
        }

        bool hasSelection = _configLibDocument.Formatting.Count > 0 &&
            _configLibSelectedFormattingIndex >= 0 &&
            _configLibSelectedFormattingIndex < _configLibDocument.Formatting.Count;
        if (!hasSelection) ImGui.BeginDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Remove row##configlib-format-remove"))
        {
            _configLibDocument.Formatting.RemoveAt(_configLibSelectedFormattingIndex);
            _configLibSelectedFormattingIndex = Math.Clamp(_configLibSelectedFormattingIndex, 0, Math.Max(0, _configLibDocument.Formatting.Count - 1));
            MarkConfigLibDocumentDirty("Removed formatting row.");
        }
        ImGui.SameLine();
        if (ImGui.Button("Up##configlib-format-up")) MoveConfigLibFormatting(_configLibSelectedFormattingIndex, _configLibSelectedFormattingIndex - 1);
        ImGui.SameLine();
        if (ImGui.Button("Down##configlib-format-down")) MoveConfigLibFormatting(_configLibSelectedFormattingIndex, _configLibSelectedFormattingIndex + 1);
        if (!hasSelection) ImGui.EndDisabled();

        if (_configLibDocument.Formatting.Count == 0)
        {
            ImGui.TextDisabled("No formatting rows.");
            return;
        }

        _configLibSelectedFormattingIndex = Math.Clamp(_configLibSelectedFormattingIndex, 0, _configLibDocument.Formatting.Count - 1);
        if (ImGui.BeginListBox("##configlib-format-list", new NVector2(-float.Epsilon, 90f)))
        {
            for (int index = 0; index < _configLibDocument.Formatting.Count; index++)
            {
                DevToolsConfigLibFormattingDraft row = _configLibDocument.Formatting[index];
                string disabled = row.Enabled ? "" : " [disabled]";
                if (ImGui.Selectable($"{row.Weight:0.##} {row.Type}: {row.Title}{disabled}##configlib-format-row-{index}", index == _configLibSelectedFormattingIndex))
                {
                    _configLibSelectedFormattingIndex = index;
                }
            }

            ImGui.EndListBox();
        }

        DevToolsConfigLibFormattingDraft selected = _configLibDocument.Formatting[_configLibSelectedFormattingIndex];
        bool enabled = selected.Enabled;
        if (ImGui.Checkbox($"Enabled##configlib-format-enabled-{_configLibSelectedFormattingIndex}", ref enabled))
        {
            selected.Enabled = enabled;
            MarkConfigLibDocumentDirty("Updated formatting row.");
        }

        string type = selected.Type;
        if (ImGui.InputText($"Type##configlib-format-type-{_configLibSelectedFormattingIndex}", ref type, 120))
        {
            selected.Type = type;
            MarkConfigLibDocumentDirty("Updated formatting type.");
        }

        string title = selected.Title;
        if (ImGui.InputText($"Title##configlib-format-title-{_configLibSelectedFormattingIndex}", ref title, 256))
        {
            selected.Title = title;
            MarkConfigLibDocumentDirty("Updated formatting title.");
        }

        float weight = (float)selected.Weight;
        if (ImGui.DragFloat($"Weight##configlib-format-weight-{_configLibSelectedFormattingIndex}", ref weight, 0.1f, -100000f, 100000f, "%.2f"))
        {
            selected.Weight = weight;
            MarkConfigLibDocumentDirty("Updated formatting weight.");
        }

        DrawConfigLibJsonObjectEditor(
            "Formatting extra fields",
            $"format-extra-{_configLibSelectedFormattingIndex}",
            selected.Extra,
            obj =>
            {
                selected.Extra = obj;
                MarkConfigLibDocumentDirty("Updated formatting extra fields.");
            });
    }

    private void DrawConfigLibOutputPanel(NVector2 size, bool showDiagnostics)
    {
        ImGui.BeginChild("##configlib-output", size, true);
        ImGui.SeparatorText("Output");

        string domain = _configLibDocument.Domain;
        if (ImGui.InputText("Domain##configlib-domain", ref domain, 120))
        {
            _configLibDocument.Domain = domain;
            MarkConfigLibDocumentDirty("Updated ConfigLib domain.");
        }

        string relativePath = _configLibDocument.RelativePath;
        if (ImGui.InputText("Patch path##configlib-relative-path", ref relativePath, 260))
        {
            _configLibDocument.RelativePath = relativePath;
            MarkConfigLibDocumentDirty("Updated ConfigLib patch path.");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Saved under assets/<domain>/<path>. Example: config/configlib-patches.json");
        }

        string modConfigPath = _configLibDocument.ModConfigRelativePath;
        if (ImGui.InputText("ModConfig path##configlib-modconfig-path", ref modConfigPath, 260))
        {
            _configLibDocument.ModConfigRelativePath = modConfigPath;
            MarkConfigLibDocumentDirty("Updated ModConfig path.");
        }

        string csharpNamespace = _configLibDocument.CSharpNamespace;
        if (ImGui.InputText("C# namespace##configlib-csharp-namespace", ref csharpNamespace, 180))
        {
            _configLibDocument.CSharpNamespace = csharpNamespace;
            MarkConfigLibDocumentDirty("Updated C# namespace.");
        }

        string configClassName = _configLibDocument.ConfigClassName;
        if (ImGui.InputText("Config class##configlib-csharp-config-class", ref configClassName, 180))
        {
            _configLibDocument.ConfigClassName = configClassName;
            MarkConfigLibDocumentDirty("Updated C# config class.");
        }

        string loaderClassName = _configLibDocument.LoaderClassName;
        if (ImGui.InputText("Loader class##configlib-csharp-loader-class", ref loaderClassName, 180))
        {
            _configLibDocument.LoaderClassName = loaderClassName;
            MarkConfigLibDocumentDirty("Updated C# loader class.");
        }

        string currentPropertyName = _configLibDocument.CurrentPropertyName;
        if (ImGui.InputText("Static instance##configlib-csharp-current-property", ref currentPropertyName, 120))
        {
            _configLibDocument.CurrentPropertyName = currentPropertyName;
            MarkConfigLibDocumentDirty("Updated C# static instance property.");
        }

        if (ImGui.Button("Reset C# names from domain##configlib-csharp-reset"))
        {
            _configLibDocument.ApplyCSharpDefaultsFromDomain();
            MarkConfigLibDocumentDirty("Reset C# names from domain.");
        }

        int version = _configLibDocument.Version;
        if (ImGui.InputInt("Version##configlib-version", ref version))
        {
            _configLibDocument.Version = version;
            MarkConfigLibDocumentDirty("Updated ConfigLib version.");
        }

        if (ImGui.Checkbox("ModConfig included settings only##configlib-modconfig-included-only", ref _configLibModConfigIncludedOnly))
        {
            _configLibStatus = "Updated ModConfig preview mode.";
        }

        DrawConfigLibJsonObjectEditor(
            "Root extra fields",
            "root-extra",
            _configLibDocument.Extra,
            obj =>
            {
                _configLibDocument.Extra = obj;
                MarkConfigLibDocumentDirty("Updated root extra fields.");
            });

        string patchPreview = _configLibDocument.ToPatchJson();
        string modConfigPreview = _configLibDocument.ToModConfigJson(_configLibModConfigIncludedOnly);
        string csharpPreview = _configLibDocument.ToCSharpLoaderCode();
        string patchOutputPath = GetToolAuthoredAssetPath("configlib", _configLibDocument.BuildPatchAssetRelativePath());
        string modConfigOutputPath = GetToolAuthoredAssetPath("configlib", _configLibDocument.BuildModConfigRelativePath());
        string csharpOutputPath = GetToolAuthoredAssetPath("configlib", _configLibDocument.BuildCSharpRelativePath());
        List<DevToolsConfigLibValidationIssue> issues = _configLibDocument.Validate(_configLibModConfigIncludedOnly);
        bool hasErrors = issues.Any(issue => issue.Severity == DevToolsConfigLibIssueSeverity.Error);

        ImGui.TextWrapped($"ConfigLib: {patchOutputPath}");
        ImGui.TextWrapped($"ModConfig: {modConfigOutputPath}");
        ImGui.TextWrapped($"C#: {csharpOutputPath}");
        if (File.Exists(patchOutputPath) && !string.Equals(File.ReadAllText(patchOutputPath), patchPreview, StringComparison.Ordinal))
        {
            ImGui.TextColored(new NVector4(1f, 0.72f, 0.32f, 1f), "Saving will overwrite the existing authored ConfigLib patch file.");
        }

        DrawConfigLibValidationIssues(issues);

        if (hasErrors) ImGui.BeginDisabled();
        if (ImGui.Button("Save ConfigLib##configlib-save-patch"))
        {
            QueueSourceSave(TrySaveConfigLibPatch(patchOutputPath, patchPreview), status => _configLibStatus = status);
        }
        ImGui.SameLine();
        if (ImGui.Button("Save ModConfig##configlib-save-modconfig"))
        {
            QueueSourceSave(TrySaveConfigLibModConfig(modConfigOutputPath, modConfigPreview), status => _configLibStatus = status);
        }
        ImGui.SameLine();
        if (ImGui.Button("Save C# loader##configlib-save-csharp"))
        {
            QueueSourceSave(TrySaveConfigLibCSharp(csharpOutputPath, csharpPreview), status => _configLibStatus = status);
        }
        if (ImGui.Button("Save both authored files##configlib-save-both", new NVector2(-1, 0)))
        {
            QueueSourceSave(TrySaveConfigLibBundle(patchOutputPath, patchPreview, modConfigOutputPath, modConfigPreview), status => _configLibStatus = status);
        }
        if (ImGui.Button("Save All##configlib-save-all", new NVector2(-1, 0)))
        {
            QueueSourceSave(TrySaveConfigLibAll(patchOutputPath, patchPreview, modConfigOutputPath, modConfigPreview, csharpOutputPath, csharpPreview), status => _configLibStatus = status);
        }
        if (hasErrors) ImGui.EndDisabled();

        if (ImGui.Button("Copy preview##configlib-copy-preview"))
        {
            ImGui.SetClipboardText(BuildConfigLibPreviewText(patchPreview, modConfigPreview, csharpPreview, patchOutputPath));
            _configLibStatus = $"Copied {ConfigLibPreviewModeLabels[Math.Clamp(_configLibPreviewMode, 0, ConfigLibPreviewModeLabels.Length - 1)]}.";
        }

        ImGui.TextWrapped(_configLibStatus);
        ImGui.SeparatorText("Preview");
        ImGui.Combo("Preview##configlib-preview-mode", ref _configLibPreviewMode, ConfigLibPreviewModeLabels, ConfigLibPreviewModeLabels.Length);
        _configLibPreviewMode = Math.Clamp(_configLibPreviewMode, 0, ConfigLibPreviewModeLabels.Length - 1);
        string preview = BuildConfigLibPreviewText(patchPreview, modConfigPreview, csharpPreview, patchOutputPath);
        ImGui.InputTextMultiline(
            "##configlib-preview-json",
            ref preview,
            (uint)Math.Max(4096, preview.Length + 1024),
            new NVector2(-1, Math.Max(140f, ImGui.GetContentRegionAvail().Y - 70f)),
            ImGuiInputTextFlags.ReadOnly);

        _configLibDiagnostics.Draw("configlib", showDiagnostics);
        ImGui.EndChild();
    }

    private string BuildConfigLibPreviewText(string patchPreview, string modConfigPreview, string csharpPreview, string patchOutputPath)
    {
        return _configLibPreviewMode switch
        {
            1 => modConfigPreview,
            2 => csharpPreview,
            3 => _configLibDocument.BuildOrderSummary(),
            4 => BuildConfigLibDiffPreview(patchOutputPath, patchPreview),
            _ => patchPreview
        };
    }

    private string BuildConfigLibDiffPreview(string outputPath, string patchPreview)
    {
        string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : "";
        List<DevToolsDiffLine> diff = DevToolsTextDiff.CollapseContext(DevToolsTextDiff.BuildLineDiff(oldText, patchPreview), contextLines: 3);
        (int added, int removed) = DevToolsTextDiff.CountChanges(diff);
        if (added == 0 && removed == 0) return "No changes from the current authored ConfigLib patch file.";

        List<string> lines = [$"{added} added line(s), {removed} removed line(s)."];
        foreach (DevToolsDiffLine line in diff)
        {
            string prefix = line.Kind switch
            {
                DevToolsDiffLineKind.Added => "+ ",
                DevToolsDiffLineKind.Removed => "- ",
                DevToolsDiffLineKind.Skip => "  ",
                _ => "  "
            };
            lines.Add(prefix + line.Text);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void DrawConfigLibValidationIssues(IReadOnlyList<DevToolsConfigLibValidationIssue> issues)
    {
        if (issues.Count == 0)
        {
            ImGui.TextDisabled("Validation: no issues.");
            return;
        }

        if (!ImGui.CollapsingHeader($"Validation ({issues.Count})##configlib-validation", ImGuiTreeNodeFlags.DefaultOpen)) return;
        foreach (DevToolsConfigLibValidationIssue issue in issues)
        {
            NVector4 color = issue.Severity == DevToolsConfigLibIssueSeverity.Error
                ? new NVector4(1f, 0.36f, 0.28f, 1f)
                : new NVector4(1f, 0.72f, 0.32f, 1f);
            ImGui.TextColored(color, $"{issue.Severity}: {issue.Message}");
        }
    }

    private SourceSaveResult TrySaveConfigLibPatch(string outputPath, string newText)
    {
        try
        {
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : "";
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved ConfigLib patch to {outputPath}.",
                () =>
                {
                    string result = WriteAuthoredFile(outputPath, newText);
                    _configLibDocument.Domain = DevToolsConfigLibDocumentDraft.SanitizeDomain(_configLibDocument.Domain);
                    _configLibDocument.RelativePath = DevToolsConfigLibDocumentDraft.NormalizeRelativePath(_configLibDocument.RelativePath, "config/configlib-patches.json");
                    _configLibOriginalPatchJson = _configLibDocument.ToPatchJson();
                    _configLibDocumentDirty = false;
                    _configLibLoadedDocumentKey = $"{_configLibDocument.Domain}:{_configLibDocument.RelativePath}:authored";
                    _configLibIndexed = false;
                    return result;
                });
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            _configLibDiagnostics.Exception("ConfigLib patch save failed", exception);
            return SourceSaveResult.Fail($"ConfigLib patch save failed: {exception.Message}");
        }
    }

    private SourceSaveResult TrySaveConfigLibModConfig(string outputPath, string newText)
    {
        try
        {
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : "";
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved ModConfig default to {outputPath}.",
                () => WriteAuthoredFile(outputPath, newText));
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            _configLibDiagnostics.Exception("ConfigLib ModConfig save failed", exception);
            return SourceSaveResult.Fail($"ModConfig save failed: {exception.Message}");
        }
    }

    private SourceSaveResult TrySaveConfigLibCSharp(string outputPath, string newText)
    {
        try
        {
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : "";
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved C# config loader to {outputPath}.",
                () => WriteAuthoredFile(outputPath, newText));
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            _configLibDiagnostics.Exception("ConfigLib C# loader save failed", exception);
            return SourceSaveResult.Fail($"C# loader save failed: {exception.Message}");
        }
    }

    private SourceSaveResult TrySaveConfigLibBundle(string patchPath, string patchText, string modConfigPath, string modConfigText)
    {
        try
        {
            string oldText = BuildConfigLibBundlePreview(patchPath, File.Exists(patchPath) ? File.ReadAllText(patchPath) : "", modConfigPath, File.Exists(modConfigPath) ? File.ReadAllText(modConfigPath) : "");
            string newText = BuildConfigLibBundlePreview(patchPath, patchText, modConfigPath, modConfigText);
            SourceSaveRequest request = new(
                $"ConfigLib bundle: {patchPath}; {modConfigPath}",
                oldText,
                newText,
                $"Saved ConfigLib patch to {patchPath} and ModConfig default to {modConfigPath}.",
                () =>
                {
                    WriteAuthoredFile(patchPath, patchText);
                    WriteAuthoredFile(modConfigPath, modConfigText);
                    _configLibDocument.Domain = DevToolsConfigLibDocumentDraft.SanitizeDomain(_configLibDocument.Domain);
                    _configLibDocument.RelativePath = DevToolsConfigLibDocumentDraft.NormalizeRelativePath(_configLibDocument.RelativePath, "config/configlib-patches.json");
                    _configLibDocument.ModConfigRelativePath = DevToolsConfigLibDocumentDraft.NormalizeRelativePath(_configLibDocument.ModConfigRelativePath, $"{_configLibDocument.Domain}.json");
                    _configLibOriginalPatchJson = _configLibDocument.ToPatchJson();
                    _configLibDocumentDirty = false;
                    _configLibLoadedDocumentKey = $"{_configLibDocument.Domain}:{_configLibDocument.RelativePath}:authored";
                    _configLibIndexed = false;
                    return "";
                });
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            _configLibDiagnostics.Exception("ConfigLib bundle save failed", exception);
            return SourceSaveResult.Fail($"ConfigLib bundle save failed: {exception.Message}");
        }
    }

    private SourceSaveResult TrySaveConfigLibAll(string patchPath, string patchText, string modConfigPath, string modConfigText, string csharpPath, string csharpText)
    {
        try
        {
            string oldText = BuildConfigLibBundlePreview(
                patchPath,
                File.Exists(patchPath) ? File.ReadAllText(patchPath) : "",
                modConfigPath,
                File.Exists(modConfigPath) ? File.ReadAllText(modConfigPath) : "",
                csharpPath,
                File.Exists(csharpPath) ? File.ReadAllText(csharpPath) : "");
            string newText = BuildConfigLibBundlePreview(patchPath, patchText, modConfigPath, modConfigText, csharpPath, csharpText);
            SourceSaveRequest request = new(
                $"ConfigLib scratch bundle: {patchPath}; {modConfigPath}; {csharpPath}",
                oldText,
                newText,
                $"Saved ConfigLib patch, ModConfig default, and C# loader to authored ConfigLib outputs.",
                () =>
                {
                    WriteAuthoredFile(patchPath, patchText);
                    WriteAuthoredFile(modConfigPath, modConfigText);
                    WriteAuthoredFile(csharpPath, csharpText);
                    _configLibDocument.Domain = DevToolsConfigLibDocumentDraft.SanitizeDomain(_configLibDocument.Domain);
                    _configLibDocument.RelativePath = DevToolsConfigLibDocumentDraft.NormalizeRelativePath(_configLibDocument.RelativePath, "config/configlib-patches.json");
                    _configLibDocument.ModConfigRelativePath = DevToolsConfigLibDocumentDraft.NormalizeRelativePath(_configLibDocument.ModConfigRelativePath, $"{_configLibDocument.Domain}.json");
                    _configLibOriginalPatchJson = _configLibDocument.ToPatchJson();
                    _configLibDocumentDirty = false;
                    _configLibLoadedDocumentKey = $"{_configLibDocument.Domain}:{_configLibDocument.RelativePath}:authored";
                    _configLibIndexed = false;
                    return "";
                });
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            _configLibDiagnostics.Exception("ConfigLib scratch bundle save failed", exception);
            return SourceSaveResult.Fail($"ConfigLib scratch bundle save failed: {exception.Message}");
        }
    }

    private static string BuildConfigLibBundlePreview(string patchPath, string patchText, string modConfigPath, string modConfigText)
    {
        return
            $"// {patchPath}" + Environment.NewLine +
            patchText + Environment.NewLine + Environment.NewLine +
            $"// {modConfigPath}" + Environment.NewLine +
            modConfigText;
    }

    private static string BuildConfigLibBundlePreview(string patchPath, string patchText, string modConfigPath, string modConfigText, string csharpPath, string csharpText)
    {
        return
            BuildConfigLibBundlePreview(patchPath, patchText, modConfigPath, modConfigText) +
            Environment.NewLine + Environment.NewLine +
            $"// {csharpPath}" + Environment.NewLine +
            csharpText;
    }

    private bool DrawConfigLibJsonObjectEditor(string title, string key, JObject current, Action<JObject> apply)
    {
        if (!ImGui.TreeNode($"{title}##configlib-json-{key}")) return false;

        if (!_configLibJsonBuffers.TryGetValue(key, out string? buffer))
        {
            buffer = JsonConvert.SerializeObject(current, Formatting.Indented);
            _configLibJsonBuffers[key] = buffer;
        }

        bool changed = false;
        if (ImGui.InputTextMultiline($"##configlib-json-buffer-{key}", ref buffer, DevToolsImGuiTextBuffer.Capacity(buffer), new NVector2(-float.Epsilon, 96f), ImGuiInputTextFlags.AllowTabInput))
        {
            _configLibJsonBuffers[key] = buffer;
        }

        if (ImGui.Button($"Apply##configlib-json-apply-{key}"))
        {
            if (DevToolsJson.TryParseObject(buffer, out JObject? parsed, out string error, useVintageStoryFallback: false) && parsed != null)
            {
                apply((JObject)parsed.DeepClone());
                _configLibJsonBuffers[key] = JsonConvert.SerializeObject(parsed, Formatting.Indented);
                changed = true;
            }
            else
            {
                _configLibStatus = $"{title} JSON is invalid: {error}";
            }
        }
        ImGui.SameLine();
        if (ImGui.Button($"Format##configlib-json-format-{key}"))
        {
            if (DevToolsJsonTextTools.TryFormat(buffer, out string formatted, out string error))
            {
                _configLibJsonBuffers[key] = formatted;
            }
            else
            {
                _configLibStatus = $"{title} format failed: {error}";
            }
        }
        ImGui.SameLine();
        if (ImGui.Button($"Reset##configlib-json-reset-{key}"))
        {
            _configLibJsonBuffers[key] = JsonConvert.SerializeObject(current, Formatting.Indented);
        }
        ImGui.SameLine();
        if (ImGui.Button($"Clear##configlib-json-clear-{key}"))
        {
            apply([]);
            _configLibJsonBuffers[key] = "{\n}";
            changed = true;
        }

        ImGui.TreePop();
        return changed;
    }

    private void MoveConfigLibSetting(int from, int to)
    {
        if (from < 0 || from >= _configLibDocument.Settings.Count) return;
        to = Math.Clamp(to, 0, _configLibDocument.Settings.Count - 1);
        if (from == to) return;

        DevToolsConfigLibSettingDraft item = _configLibDocument.Settings[from];
        _configLibDocument.Settings.RemoveAt(from);
        _configLibDocument.Settings.Insert(to, item);
        _configLibSelectedSettingIndex = to;
        MarkConfigLibDocumentDirty("Reordered ConfigLib settings.");
    }

    private void MoveConfigLibFormatting(int from, int to)
    {
        if (from < 0 || from >= _configLibDocument.Formatting.Count) return;
        to = Math.Clamp(to, 0, _configLibDocument.Formatting.Count - 1);
        if (from == to) return;

        DevToolsConfigLibFormattingDraft item = _configLibDocument.Formatting[from];
        _configLibDocument.Formatting.RemoveAt(from);
        _configLibDocument.Formatting.Insert(to, item);
        _configLibSelectedFormattingIndex = to;
        MarkConfigLibDocumentDirty("Reordered ConfigLib formatting rows.");
    }

    private string MakeUniqueConfigLibSettingCode(string baseCode)
    {
        string stem = string.IsNullOrWhiteSpace(baseCode) ? "new-setting" : baseCode.Trim();
        HashSet<string> existing = _configLibDocument.Settings.Select(setting => setting.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(stem)) return stem;

        for (int index = 2; index < 10_000; index++)
        {
            string candidate = $"{stem}-{index}";
            if (!existing.Contains(candidate)) return candidate;
        }

        return $"{stem}-{DateTime.UtcNow.Ticks}";
    }

    private bool ConfigLibSettingMatchesFilter(DevToolsConfigLibSettingDraft setting)
    {
        string filter = _configLibSettingFilter.Trim();
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return setting.Code.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            setting.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            setting.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            setting.Type.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildConfigLibDefaultPreview(DevToolsConfigLibSettingDraft setting)
    {
        string value = setting.DefaultJson.Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return value.Length > 64 ? value[..64] + "..." : value;
    }

    private static float ParseConfigLibFloat(string json)
    {
        if (DevToolsJson.TryParseToken(json, out JToken? token, out _, useVintageStoryFallback: false) &&
            token != null &&
            token.Type is JTokenType.Integer or JTokenType.Float)
        {
            return token.Value<float>();
        }

        return 0f;
    }

    private sealed class ConfigLibModConfigEntry
    {
        public ConfigLibModConfigEntry(string filePath, string relativeFilePath, JToken root, int settingCount)
        {
            FilePath = filePath;
            RelativeFilePath = relativeFilePath;
            DisplayName = Path.GetFileName(relativeFilePath);
            SuggestedDomain = DevToolsConfigLibDocumentDraft.SuggestDomain(relativeFilePath);
            Root = root.DeepClone();
            SettingCount = settingCount;
            ClientSide = DevToolsConfigLibDocumentDraft.IsClientSideConfig(relativeFilePath);
            Key = filePath;
            SearchText = $"{DisplayName} {RelativeFilePath} {SuggestedDomain}";
        }

        public string Key { get; }
        public string FilePath { get; }
        public string RelativeFilePath { get; }
        public string DisplayName { get; }
        public string SuggestedDomain { get; }
        public JToken Root { get; }
        public int SettingCount { get; }
        public bool ClientSide { get; }
        public string SearchText { get; }
    }

    private sealed class ConfigLibPatchEntry
    {
        public ConfigLibPatchEntry(IAsset asset, string domain, string assetPath, bool authored, int settingCount)
        {
            Asset = asset;
            Domain = string.IsNullOrWhiteSpace(domain) ? "game" : domain;
            AssetPath = assetPath;
            Authored = authored;
            SettingCount = settingCount;
            DisplayName = $"{Domain}:{AssetPath}";
            Key = $"{Domain}:{AssetPath}:{Authored}";
            SearchText = $"{DisplayName} {(Authored ? "authored" : "")}";
        }

        public IAsset Asset { get; }
        public string Domain { get; }
        public string AssetPath { get; }
        public bool Authored { get; }
        public int SettingCount { get; }
        public string DisplayName { get; }
        public string Key { get; }
        public string SearchText { get; }
    }
}
