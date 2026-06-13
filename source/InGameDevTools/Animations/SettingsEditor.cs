using InGameDevTools.Utils;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Numerics;
using System.Reflection;
using VSImGui;
using VSImGui.API;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private const string SettingsPresetVintageBrown = DevToolsConfig.PresetVintageBrown;
    private const string SettingsPresetClassicDark = "Classic Dark";
    private const string SettingsPresetHighContrastDark = "High Contrast Dark";
    private const string SettingsPresetHighContrastLight = "High Contrast Light";
    private const string SettingsPresetLowContrastNeutral = "Low Contrast Neutral";
    private const string SettingsPresetColorblindSafeDark = "Colorblind Safe Dark";
    private const string SettingsPresetDyslexiaFriendly = "Dyslexia Friendly";
    private const string SettingsPresetCustom = "Custom";
    private const string SettingsFontDefault = DevToolsConfig.FontDefault;
    private const string SettingsFontOpenDyslexic = "OpenDyslexic-Regular";
    private static readonly int[] SettingsOpenDyslexicSizes = Enumerable.Range(12, 17).ToArray();
    private static readonly string[] SettingsAnimationIkModeLabels = ["Auto conservative", "Auto extended", "Manual override"];
    private static readonly string[] SettingsThemePresets =
    [
        SettingsPresetVintageBrown,
        SettingsPresetClassicDark,
        SettingsPresetHighContrastDark,
        SettingsPresetHighContrastLight,
        SettingsPresetLowContrastNeutral,
        SettingsPresetColorblindSafeDark,
        SettingsPresetDyslexiaFriendly,
        SettingsPresetCustom
    ];

    private readonly DevToolsConfig _devToolsConfig;
    private bool _devToolsConfigSaveQueued;
    private double _devToolsConfigSaveAfter;
    private string _settingsStatus = "Settings ready.";
    private string _settingsImportJson = "";
    private bool _settingsOpenDyslexicLoadQueued;
    private string _settingsFontRuntimeStatus = "";
    private readonly HashSet<string> _settingsFailedRuntimeFontLoads = new(StringComparer.OrdinalIgnoreCase);

    private void ApplyDevToolsConfigToRuntime()
    {
        _devToolsConfig.Normalize();
        _devToolsUiScale = _devToolsConfig.UiScale;
        _showEditorDiagnostics = _devToolsConfig.ShowDiagnostics;
        _liveApplyManager.AutoApply = _devToolsConfig.AutoRuntimeApply;
        _liveApplyManager.WriteBackups = _devToolsConfig.WriteLiveBackups;
        _vanillaIkMode = ParseVanillaIkChainMode(_devToolsConfig.AnimationIkMode);
        _vanillaIkSolver = ParseVanillaIkSolverKind(_devToolsConfig.AnimationIkSolver);
        _vanillaIkPreserveDraggedPartRotation = _devToolsConfig.AnimationIkPreserveDraggedPartRotation;
        _vanillaIkLockMoveToDragAxis = _devToolsConfig.AnimationIkLockMoveToDragAxis;
        DevToolsViewportBackground.Style = DevToolsViewportBackground.Parse(_devToolsConfig.ViewportBackground);
    }

    private DevToolsStyleScope BeginDevToolsStyleScope()
    {
        Style style = BuildCurrentDevToolsStyle();
        if (_devToolsConfig.ApplyStyleGlobally && _imguiModSystem?.DefaultStyle != null)
        {
            _imguiModSystem.DefaultStyle.SetFrom(style);
        }

        return new DevToolsStyleScope(_devToolsConfig.ApplyStyleGlobally ? null : new StyleApplier(style));
    }

    private Style BuildCurrentDevToolsStyle()
    {
        Style style = new(_imguiModSystem?.DefaultStyle ?? new Style());
        ApplySettingsPreset(style, _devToolsConfig.ThemePreset);
        ApplySettingsAccessibility(style);
        ApplySettingsColorOverrides(style);
        ApplySettingsFont(style);
        return style;
    }

    private void SettingsTab(float deltaSeconds)
    {
        _ = deltaSeconds;
        ImGui.BeginChild("##settings-tab", new NVector2(-float.Epsilon, -float.Epsilon), true);

        ImGui.SeparatorText("General");
        bool changed = false;
        bool openOnStartup = _devToolsConfig.OpenOnStartup;
        if (ImGui.Checkbox("Open on startup##settings-open-startup", ref openOnStartup))
        {
            _devToolsConfig.OpenOnStartup = openOnStartup;
            changed = true;
        }

        bool diagnostics = _showEditorDiagnostics;
        if (ImGui.Checkbox("Diagnostics##settings-diagnostics", ref diagnostics))
        {
            _showEditorDiagnostics = diagnostics;
            _devToolsConfig.ShowDiagnostics = diagnostics;
            changed = true;
        }

        float scale = _devToolsUiScale;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.SliderFloat("UI scale##settings-scale", ref scale, 0.75f, 1.75f, "%.2f"))
        {
            _devToolsUiScale = Math.Clamp(scale, 0.75f, 1.75f);
            _devToolsConfig.UiScale = _devToolsUiScale;
            changed = true;
        }

        bool autoApply = _liveApplyManager.AutoApply;
        if (ImGui.Checkbox("Runtime apply by default##settings-runtime-apply", ref autoApply))
        {
            _liveApplyManager.AutoApply = autoApply;
            _devToolsConfig.AutoRuntimeApply = autoApply;
            changed = true;
        }

        bool backups = _liveApplyManager.WriteBackups;
        if (ImGui.Checkbox("Write live backup copies##settings-live-backups", ref backups))
        {
            _liveApplyManager.WriteBackups = backups;
            _devToolsConfig.WriteLiveBackups = backups;
            changed = true;
        }

        ImGui.SeparatorText("Theme");
        int presetIndex = Array.FindIndex(SettingsThemePresets, preset => preset.Equals(_devToolsConfig.ThemePreset, StringComparison.OrdinalIgnoreCase));
        if (presetIndex < 0) presetIndex = SettingsThemePresets.Length - 1;
        if (ImGui.Combo("Preset##settings-theme-preset", ref presetIndex, SettingsThemePresets, SettingsThemePresets.Length))
        {
            ApplyPresetToConfig(SettingsThemePresets[presetIndex]);
            changed = true;
        }

        int viewportBgIndex = Array.FindIndex(
            DevToolsViewportBackground.StyleNames,
            name => name.Equals(_devToolsConfig.ViewportBackground, StringComparison.OrdinalIgnoreCase));
        if (viewportBgIndex < 0) viewportBgIndex = 0;
        if (ImGui.Combo("Viewport background##settings-viewport-bg", ref viewportBgIndex, DevToolsViewportBackground.StyleNames, DevToolsViewportBackground.StyleNames.Length))
        {
            _devToolsConfig.ViewportBackground = DevToolsViewportBackground.StyleNames[viewportBgIndex];
            DevToolsViewportBackground.Style = DevToolsViewportBackground.Parse(_devToolsConfig.ViewportBackground);
            changed = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Background tone behind 3D/preview viewports. Use Grey or Light to inspect dark models.");
        }

        bool global = _devToolsConfig.ApplyStyleGlobally;
        if (ImGui.Checkbox("Apply style globally to VSImGui##settings-style-global", ref global))
        {
            _devToolsConfig.ApplyStyleGlobally = global;
            changed = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Off applies this style only to InGameDevTools. On changes VSImGui's shared default style for other ImGui mods too.");
        }

        DrawSettingsFontControls(ref changed);
        DrawSettingsAccessibilityControls(ref changed);
        DrawSettingsAnimationControls(ref changed);
        DrawSettingsAdvancedColors(ref changed);
        DrawSettingsImportExport(ref changed);

        ImGui.SeparatorText("Status");
        ImGui.TextWrapped(_settingsStatus);

        if (changed)
        {
            _devToolsConfig.ThemePreset = string.IsNullOrWhiteSpace(_devToolsConfig.ThemePreset) ? SettingsPresetCustom : _devToolsConfig.ThemePreset;
            _devToolsConfig.Normalize();
            QueueDevToolsConfigSave("Settings updated.");
        }

        ImGui.EndChild();
    }

    private void DrawSettingsFontControls(ref bool changed)
    {
        ImGui.SeparatorText("Font");
        List<string> fontOptions = GetSettingsFontOptions();
        int fontIndex = fontOptions.FindIndex(font => font.Equals(_devToolsConfig.FontName, StringComparison.OrdinalIgnoreCase));
        if (fontIndex < 0) fontIndex = 0;
        if (ImGui.Combo("Font##settings-font", ref fontIndex, fontOptions.ToArray(), fontOptions.Count))
        {
            _devToolsConfig.FontName = fontOptions[fontIndex];
            QueueSettingsFontRuntimeLoadIfNeeded(_devToolsConfig.FontName);
            changed |= SnapSettingsFontSizeToLoadedOption();
            changed = true;
        }

        QueueSettingsFontRuntimeLoadIfNeeded(_devToolsConfig.FontName);
        DrawSettingsFontSizeControl(ref changed);

        ImGui.TextDisabled(GetSettingsFontStatus());
        if (!string.IsNullOrWhiteSpace(_settingsFontRuntimeStatus))
        {
            ImGui.TextDisabled(_settingsFontRuntimeStatus);
        }
    }

    private void DrawSettingsFontSizeControl(ref bool changed)
    {
        bool isDefaultFont = _devToolsConfig.FontName.Equals(SettingsFontDefault, StringComparison.OrdinalIgnoreCase);
        int[] sizeOptions = isDefaultFont ? [] : GetSettingsFontSizeOptions(_devToolsConfig.FontName);
        if (sizeOptions.Length == 0)
        {
            ImGui.BeginDisabled();
            int size = _devToolsConfig.FontSize;
            ImGui.SetNextItemWidth(180f);
            ImGui.InputInt("Font size##settings-font-size-unloaded", ref size);
            ImGui.EndDisabled();
            return;
        }

        int selectedIndex = Array.IndexOf(sizeOptions, _devToolsConfig.FontSize);
        if (selectedIndex < 0)
        {
            selectedIndex = FindNearestSizeIndex(sizeOptions, _devToolsConfig.FontSize);
            _devToolsConfig.FontSize = sizeOptions[selectedIndex];
            changed = true;
        }

        string[] labels = sizeOptions.Select(size => $"{size}px").ToArray();
        ImGui.SetNextItemWidth(180f);
        if (isDefaultFont) ImGui.BeginDisabled();
        if (ImGui.Combo("Font size##settings-font-size", ref selectedIndex, labels, labels.Length))
        {
            _devToolsConfig.FontSize = sizeOptions[selectedIndex];
            changed = true;
        }
        if (isDefaultFont) ImGui.EndDisabled();
    }

    private void DrawSettingsAccessibilityControls(ref bool changed)
    {
        if (!ImGui.CollapsingHeader("Accessibility spacing##settings-accessibility")) return;

        changed |= DrawOptionalConfigFloat("Frame padding X", "frame-padding-x", 0f, 32f, () => _devToolsConfig.FramePaddingX, value => _devToolsConfig.FramePaddingX = value);
        changed |= DrawOptionalConfigFloat("Frame padding Y", "frame-padding-y", 0f, 32f, () => _devToolsConfig.FramePaddingY, value => _devToolsConfig.FramePaddingY = value);
        changed |= DrawOptionalConfigFloat("Item spacing X", "item-spacing-x", 0f, 48f, () => _devToolsConfig.ItemSpacingX, value => _devToolsConfig.ItemSpacingX = value);
        changed |= DrawOptionalConfigFloat("Item spacing Y", "item-spacing-y", 0f, 48f, () => _devToolsConfig.ItemSpacingY, value => _devToolsConfig.ItemSpacingY = value);
        changed |= DrawOptionalConfigFloat("Frame rounding", "frame-rounding", 0f, 16f, () => _devToolsConfig.FrameRounding, value => _devToolsConfig.FrameRounding = value);
        changed |= DrawOptionalConfigFloat("Window rounding", "window-rounding", 0f, 16f, () => _devToolsConfig.WindowRounding, value => _devToolsConfig.WindowRounding = value);
        changed |= DrawOptionalConfigFloat("Hover delay normal", "hover-delay-normal", 0f, 3f, () => _devToolsConfig.HoverDelayNormal, value => _devToolsConfig.HoverDelayNormal = value);
        changed |= DrawOptionalConfigFloat("Hover delay short", "hover-delay-short", 0f, 3f, () => _devToolsConfig.HoverDelayShort, value => _devToolsConfig.HoverDelayShort = value);
    }

    private static bool DrawOptionalConfigFloat(string label, string id, float min, float max, Func<float> getValue, Action<float> setValue)
    {
        float value = getValue();
        if (!DrawOptionalFloatSetting(label, id, ref value, min, max)) return false;

        setValue(value);
        return true;
    }

    private static bool DrawOptionalFloatSetting(string label, string id, ref float value, float min, float max)
    {
        bool enabled = value >= 0f;
        bool changed = false;
        if (ImGui.Checkbox($"##enable-{id}", ref enabled))
        {
            value = enabled ? Math.Clamp(value < 0f ? min : value, min, max) : -1f;
            changed = true;
        }
        ImGui.SameLine();
        if (!enabled) ImGui.BeginDisabled();
        float edit = enabled ? value : min;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderFloat($"{label}##settings-{id}", ref edit, min, max, "%.1f"))
        {
            value = Math.Clamp(edit, min, max);
            changed = true;
        }
        if (!enabled) ImGui.EndDisabled();
        return changed;
    }

    private void DrawSettingsAnimationControls(ref bool changed)
    {
        if (!ImGui.CollapsingHeader("Animation##settings-animation")) return;

        int modeIndex = _vanillaIkMode switch
        {
            VanillaIkChainMode.AutoExtended => 1,
            VanillaIkChainMode.ManualOverride => 2,
            _ => 0
        };
        if (ImGui.Combo("IK mode##settings-animation-ik-mode", ref modeIndex, SettingsAnimationIkModeLabels, SettingsAnimationIkModeLabels.Length))
        {
            _vanillaIkMode = modeIndex switch
            {
                1 => VanillaIkChainMode.AutoExtended,
                2 => VanillaIkChainMode.ManualOverride,
                _ => VanillaIkChainMode.AutoConservative
            };
            _devToolsConfig.AnimationIkMode = FormatVanillaIkChainMode(_vanillaIkMode);
            changed = true;
        }

        bool lockMoveToDragAxis = _vanillaIkLockMoveToDragAxis;
        if (ImGui.Checkbox("Lock IK move to drag axis##settings-animation-ik-axis", ref lockMoveToDragAxis))
        {
            _vanillaIkLockMoveToDragAxis = lockMoveToDragAxis;
            _devToolsConfig.AnimationIkLockMoveToDragAxis = lockMoveToDragAxis;
            changed = true;
        }

        bool preserveDraggedPartRotation = _vanillaIkPreserveDraggedPartRotation;
        if (ImGui.Checkbox("Preserve dragged part rotation##settings-animation-ik-preserve", ref preserveDraggedPartRotation))
        {
            _vanillaIkPreserveDraggedPartRotation = preserveDraggedPartRotation;
            _devToolsConfig.AnimationIkPreserveDraggedPartRotation = preserveDraggedPartRotation;
            changed = true;
        }

        int anchorDocumentCount = _devToolsConfig.AnimationIkAnchors.Count;
        int anchorCount = _devToolsConfig.AnimationIkAnchors.Values.Sum(anchors => anchors?.Length ?? 0);
        ImGui.TextDisabled($"Saved manual IK anchors: {anchorCount} anchor(s) across {anchorDocumentCount} shape(s).");
        if (anchorCount == 0) ImGui.BeginDisabled();
        if (ImGui.Button("Clear saved IK anchors##settings-animation-ik-clear-anchors"))
        {
            _devToolsConfig.AnimationIkAnchors.Clear();
            changed = true;
        }
        if (anchorCount == 0) ImGui.EndDisabled();
    }

    private void DrawSettingsAdvancedColors(ref bool changed)
    {
        if (!ImGui.CollapsingHeader("Advanced colors##settings-colors")) return;

        if (ImGui.Button("Reset all colors##settings-reset-colors"))
        {
            _devToolsConfig.AdvancedColorOverrides.Clear();
            _devToolsConfig.ThemePreset = SettingsPresetCustom;
            changed = true;
        }

        float height = 420f * Math.Max(0.75f, _devToolsUiScale);
        ImGuiTableFlags flags =
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.BordersInnerV |
            ImGuiTableFlags.BordersOuter |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##settings-color-table", 4, flags, new NVector2(-float.Epsilon, height))) return;

        ImGui.TableSetupColumn("Color", ImGuiTableColumnFlags.WidthFixed, 58f);
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("RGBA", ImGuiTableColumnFlags.WidthFixed, 190f);
        ImGui.TableSetupColumn("Reset", ImGuiTableColumnFlags.WidthFixed, 58f);
        ImGui.TableHeadersRow();

        foreach (ImGuiCol colorSlot in Enum.GetValues<ImGuiCol>())
        {
            if (colorSlot == ImGuiCol.COUNT) continue;

            string name = colorSlot.ToString();
            NVector4 color = GetCurrentStyleColor(colorSlot);
            ImGui.PushID($"settings-color-{name}");
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (ImGui.ColorButton("##swatch", color, ImGuiColorEditFlags.AlphaPreviewHalf, new NVector2(34f, 20f)))
            {
                ImGui.OpenPopup("picker");
            }
            if (ImGui.BeginPopup("picker"))
            {
                NVector4 edit = color;
                if (ImGui.ColorPicker4("##picker", ref edit, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                {
                    SetSettingsColorOverride(name, edit);
                    changed = true;
                }
                if (ImGui.Button("Reset color##settings-popup-reset"))
                {
                    _devToolsConfig.AdvancedColorOverrides.Remove(name);
                    _devToolsConfig.ThemePreset = SettingsPresetCustom;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(name);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextDisabled($"{ColorByte(color.X),3}, {ColorByte(color.Y),3}, {ColorByte(color.Z),3}, {ColorByte(color.W),3}");
            ImGui.TableSetColumnIndex(3);
            bool hasOverride = _devToolsConfig.AdvancedColorOverrides.ContainsKey(name);
            if (!hasOverride) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Reset"))
            {
                _devToolsConfig.AdvancedColorOverrides.Remove(name);
                _devToolsConfig.ThemePreset = SettingsPresetCustom;
                changed = true;
            }
            if (!hasOverride) ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawSettingsImportExport(ref bool changed)
    {
        if (!ImGui.CollapsingHeader("Import / export##settings-import-export")) return;

        if (ImGui.Button("Copy theme JSON##settings-copy-theme"))
        {
            ImGui.SetClipboardText(BuildSettingsThemeJson());
            _settingsStatus = "Copied theme JSON to clipboard.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Paste theme JSON##settings-paste-theme"))
        {
            _settingsImportJson = ImGui.GetClipboardText();
        }

        ImGui.InputTextMultiline("##settings-theme-json", ref _settingsImportJson, 256 * 1024, new NVector2(-float.Epsilon, 120f), ImGuiInputTextFlags.AllowTabInput);
        if (ImGui.Button("Import theme##settings-import-theme"))
        {
            try
            {
                ImportSettingsThemeJson(_settingsImportJson);
                _settingsStatus = "Imported theme JSON.";
                changed = true;
            }
            catch (Exception exception)
            {
                _settingsStatus = $"Theme import failed: {exception.Message}";
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Reset settings to defaults##settings-reset-defaults"))
        {
            bool keepOpenOnStartup = _devToolsConfig.OpenOnStartup;
            _devToolsConfig.ThemePreset = SettingsPresetVintageBrown;
            _devToolsConfig.ApplyStyleGlobally = false;
            _devToolsConfig.FontName = SettingsFontDefault;
            _devToolsConfig.FontSize = 16;
            _devToolsConfig.AdvancedColorOverrides.Clear();
            _devToolsConfig.FramePaddingX = -1f;
            _devToolsConfig.FramePaddingY = -1f;
            _devToolsConfig.ItemSpacingX = -1f;
            _devToolsConfig.ItemSpacingY = -1f;
            _devToolsConfig.FrameRounding = -1f;
            _devToolsConfig.WindowRounding = -1f;
            _devToolsConfig.HoverDelayNormal = -1f;
            _devToolsConfig.HoverDelayShort = -1f;
            _devToolsConfig.OpenOnStartup = keepOpenOnStartup;
            _devToolsConfig.UiScale = 1f;
            _devToolsUiScale = 1f;
            changed = true;
        }
    }

    private void SetSettingsColorOverride(string name, NVector4 value)
    {
        _devToolsConfig.AdvancedColorOverrides[name] =
        [
            Math.Clamp(value.X, 0f, 1f),
            Math.Clamp(value.Y, 0f, 1f),
            Math.Clamp(value.Z, 0f, 1f),
            Math.Clamp(value.W, 0f, 1f)
        ];
        _devToolsConfig.ThemePreset = SettingsPresetCustom;
    }

    private static int ColorByte(float value)
    {
        return (int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
    }

    private List<string> GetSettingsFontOptions()
    {
        SortedSet<string> fonts = new(StringComparer.OrdinalIgnoreCase)
        {
            SettingsFontDefault,
            SettingsFontOpenDyslexic
        };

        try
        {
            foreach ((string name, int _) in FontManager.GetLoadedFonts())
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    fonts.Add(Path.GetFileNameWithoutExtension(name));
                    fonts.Add(name);
                }
            }
        }
        catch
        {
            // FontManager may not be ready during early frames.
        }

        return fonts.ToList();
    }

    private string GetSettingsFontStatus()
    {
        if (_devToolsConfig.FontName.Equals(SettingsFontDefault, StringComparison.OrdinalIgnoreCase))
        {
            return "Default uses the font selected by VSImGui.";
        }

        bool loaded = TryResolveSettingsFontName(_devToolsConfig.FontName, _devToolsConfig.FontSize, out _);
        return loaded
            ? $"Loaded: {_devToolsConfig.FontName} {_devToolsConfig.FontSize}px."
            : $"{_devToolsConfig.FontName} is not loaded yet.";
    }

    private int[] GetSettingsFontSizeOptions(string fontName)
    {
        if (fontName.Equals(SettingsFontDefault, StringComparison.OrdinalIgnoreCase)) return [];
        if (fontName.Equals(SettingsFontOpenDyslexic, StringComparison.OrdinalIgnoreCase) && IsAnyOpenDyslexicSizeLoaded())
        {
            return SettingsOpenDyslexicSizes;
        }

        try
        {
            return FontManager.GetLoadedFonts()
                .Where(entry =>
                    entry.font.Equals(fontName, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileNameWithoutExtension(entry.font).Equals(fontName, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.size)
                .Distinct()
                .OrderBy(size => size)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private bool SnapSettingsFontSizeToLoadedOption()
    {
        int[] sizes = GetSettingsFontSizeOptions(_devToolsConfig.FontName);
        if (sizes.Length == 0 || sizes.Contains(_devToolsConfig.FontSize)) return false;

        _devToolsConfig.FontSize = sizes[FindNearestSizeIndex(sizes, _devToolsConfig.FontSize)];
        return true;
    }

    private static int FindNearestSizeIndex(int[] sizes, int target)
    {
        int bestIndex = 0;
        int bestDistance = int.MaxValue;
        for (int index = 0; index < sizes.Length; index++)
        {
            int distance = Math.Abs(sizes[index] - target);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            bestIndex = index;
        }

        return bestIndex;
    }

    private static bool IsAnyOpenDyslexicSizeLoaded()
    {
        try
        {
            return FontManager.GetLoadedFonts().Any(entry => entry.font.Equals(SettingsFontOpenDyslexic, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveSettingsFontName(string fontName, int size, out string resolvedName)
    {
        resolvedName = "";
        try
        {
            foreach ((string loadedName, int loadedSize) in FontManager.GetLoadedFonts())
            {
                if (loadedSize != size) continue;
                if (loadedName.Equals(fontName, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileNameWithoutExtension(loadedName).Equals(fontName, StringComparison.OrdinalIgnoreCase))
                {
                    resolvedName = loadedName;
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private void QueueSettingsFontRuntimeLoadIfNeeded(string fontName)
    {
        if (!fontName.Equals(SettingsFontOpenDyslexic, StringComparison.OrdinalIgnoreCase)) return;
        if (IsAnyOpenDyslexicSizeLoaded()) return;
        if (_settingsOpenDyslexicLoadQueued) return;

        string loadKey = fontName;
        if (_settingsFailedRuntimeFontLoads.Contains(loadKey)) return;

        _settingsOpenDyslexicLoadQueued = true;
        _settingsFontRuntimeStatus = $"Loading {SettingsFontOpenDyslexic} sizes...";
        _api.Event.EnqueueMainThreadTask(() =>
        {
            _settingsOpenDyslexicLoadQueued = false;
            _settingsFontRuntimeStatus = TryLoadOpenDyslexicRuntime();
            if (!IsAnyOpenDyslexicSizeLoaded())
            {
                _settingsFailedRuntimeFontLoads.Add(loadKey);
            }
            else
            {
                SnapSettingsFontSizeToLoadedOption();
            }
        }, "ingamedevtools-load-opendyslexic-font");
    }

    private string TryLoadOpenDyslexicRuntime()
    {
        try
        {
            if (IsAnyOpenDyslexicSizeLoaded())
            {
                return $"{SettingsFontOpenDyslexic} is loaded.";
            }

            string? path = InGameDevToolsModSystem.BundledOpenDyslexicFontPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return "OpenDyslexic font file was not extracted; restart may be required.";
            }

            int loadedCount = 0;
            foreach (int size in SettingsOpenDyslexicSizes)
            {
                if (TryResolveSettingsFontName(SettingsFontOpenDyslexic, size, out _)) continue;

                ImFontPtr font = ImGui.GetIO().Fonts.AddFontFromFileTTF(path, size);
                RegisterRuntimeFontWithVSImGui(path, size, font);
                loadedCount++;
            }

            bool refreshed = loadedCount == 0 || RefreshVSImGuiFontTexture();
            return refreshed
                ? $"Loaded {SettingsFontOpenDyslexic} sizes at runtime."
                : $"Loaded {SettingsFontOpenDyslexic} sizes; texture refresh may require reopening the ImGui window.";
        }
        catch (Exception exception)
        {
            _api.Logger.Warning("[InGameDevTools] OpenDyslexic runtime load failed: {0}", exception);
            return $"OpenDyslexic runtime load failed: {exception.Message}";
        }
    }

    private static void RegisterRuntimeFontWithVSImGui(string path, int size, ImFontPtr font)
    {
        AddToFontManagerSet("Fonts", path);
        AddToFontManagerSet("Sizes", size);

        object? loaded = typeof(FontManager).GetProperty("Loaded", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        if (loaded == null) return;

        object key = ValueTuple.Create(Path.GetFileNameWithoutExtension(path), size);
        PropertyInfo? indexer = loaded.GetType().GetProperty("Item");
        indexer?.SetValue(loaded, font, [key]);
    }

    private static void AddToFontManagerSet(string propertyName, object value)
    {
        object? set = typeof(FontManager).GetProperty(propertyName, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        set?.GetType().GetMethod("Add")?.Invoke(set, [value]);
    }

    private bool RefreshVSImGuiFontTexture()
    {
        try
        {
            object? controller = GetFieldRecursive(_imguiModSystem, "_controller")?.GetValue(_imguiModSystem);
            if (controller == null) return false;

            object? mainWindow = GetFieldRecursive(controller, "mMainWindow")?.GetValue(controller);
            if (mainWindow == null) return false;

            mainWindow.GetType().GetMethod("ContextMakeCurrent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(mainWindow, null);
            object? renderer = mainWindow.GetType().GetProperty("ImGuiRenderer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(mainWindow);
            if (renderer == null) return false;

            MethodInfo? recreate = GetMethodRecursive(renderer.GetType(), "RecreateFontDeviceTexture");
            recreate?.Invoke(renderer, null);
            return recreate != null;
        }
        catch (Exception exception)
        {
            _api.Logger.Warning("[InGameDevTools] Could not refresh VSImGui font texture: {0}", exception);
            return false;
        }
    }

    private static FieldInfo? GetFieldRecursive(object? instance, string name)
    {
        if (instance == null) return null;
        for (Type? type = instance.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field;
        }

        return null;
    }

    private static MethodInfo? GetMethodRecursive(Type? type, string name)
    {
        for (; type != null; type = type.BaseType)
        {
            MethodInfo? method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null) return method;
        }

        return null;
    }

    private void ApplyPresetToConfig(string preset)
    {
        _devToolsConfig.ThemePreset = preset;
        _devToolsConfig.AdvancedColorOverrides.Clear();
        if (preset == SettingsPresetDyslexiaFriendly)
        {
            _devToolsConfig.FontName = SettingsFontOpenDyslexic;
            _devToolsConfig.FontSize = 18;
            _devToolsConfig.FramePaddingX = 8f;
            _devToolsConfig.FramePaddingY = 5f;
            _devToolsConfig.ItemSpacingX = 10f;
            _devToolsConfig.ItemSpacingY = 7f;
            _devToolsConfig.FrameRounding = 4f;
            _devToolsConfig.WindowRounding = 4f;
            _devToolsConfig.HoverDelayNormal = 0.45f;
            _devToolsConfig.HoverDelayShort = 0.20f;
        }
    }

    private void ApplySettingsPreset(Style style, string preset)
    {
        switch (preset)
        {
            case SettingsPresetClassicDark:
                ApplyStylePalette(style, new NVector4(0.09f, 0.09f, 0.10f, 1f), new NVector4(0.90f, 0.90f, 0.86f, 1f), new NVector4(0.66f, 0.48f, 0.25f, 1f), new NVector4(0.18f, 0.18f, 0.19f, 1f));
                break;
            case SettingsPresetHighContrastDark:
                ApplyStylePalette(style, new NVector4(0.02f, 0.02f, 0.02f, 1f), new NVector4(0.95f, 0.95f, 0.90f, 1f), new NVector4(0.95f, 0.72f, 0.12f, 1f), new NVector4(0.10f, 0.26f, 0.44f, 1f));
                break;
            case SettingsPresetHighContrastLight:
                ApplyStylePalette(style, new NVector4(0.94f, 0.93f, 0.88f, 1f), new NVector4(0.02f, 0.02f, 0.02f, 1f), new NVector4(0.04f, 0.24f, 0.56f, 1f), new NVector4(0.76f, 0.80f, 0.88f, 1f));
                break;
            case SettingsPresetLowContrastNeutral:
                ApplyStylePalette(style, new NVector4(0.20f, 0.20f, 0.18f, 1f), new NVector4(0.82f, 0.80f, 0.72f, 1f), new NVector4(0.50f, 0.56f, 0.50f, 1f), new NVector4(0.31f, 0.30f, 0.26f, 1f));
                break;
            case SettingsPresetColorblindSafeDark:
                ApplyStylePalette(style, new NVector4(0.05f, 0.06f, 0.07f, 1f), new NVector4(0.90f, 0.88f, 0.80f, 1f), new NVector4(0.90f, 0.62f, 0.00f, 1f), new NVector4(0.00f, 0.45f, 0.70f, 1f));
                break;
            case SettingsPresetDyslexiaFriendly:
                ApplyStylePalette(style, new NVector4(0.12f, 0.11f, 0.09f, 1f), new NVector4(0.91f, 0.86f, 0.74f, 1f), new NVector4(0.78f, 0.54f, 0.24f, 1f), new NVector4(0.24f, 0.30f, 0.28f, 1f));
                break;
        }
    }

    private static void ApplyStylePalette(Style style, NVector4 background, NVector4 text, NVector4 accent, NVector4 surface)
    {
        Value4 bg = ToValue4(background);
        Value4 txt = ToValue4(text);
        Value4 disabled = ToValue4(SettingsLerpColor(text, background, 0.45f));
        Value4 acc = ToValue4(accent);
        Value4 surf = ToValue4(surface);
        Value4 hover = ToValue4(SettingsLerpColor(surface, accent, 0.35f));
        Value4 active = ToValue4(SettingsLerpColor(surface, accent, 0.55f));

        style.ColorText = txt;
        style.ColorTextDisabled = disabled;
        style.ColorBackgroundWindow = bg;
        style.ColorBackgroundChild = ToValue4(SettingsLerpColor(background, surface, 0.30f));
        style.ColorBackgroundPopup = ToValue4(SettingsLerpColor(background, surface, 0.45f));
        style.ColorBackgroundFrame = surf;
        style.ColorBackgroundFrameHovered = hover;
        style.ColorBackgroundFrameActive = active;
        style.ColorBackgroundTitle = surf;
        style.ColorBackgroundTitleActive = active;
        style.ColorBackgroundTitleCollapsed = surf;
        style.ColorButton = surf;
        style.ColorButtonHovered = hover;
        style.ColorButtonActive = active;
        style.ColorHeader = surf;
        style.ColorHeaderHovered = hover;
        style.ColorHeaderActive = active;
        style.ColorTab = surf;
        style.ColorTabHovered = hover;
        style.ColorTabActive = active;
        style.ColorTabUnfocused = ToValue4(SettingsLerpColor(background, surface, 0.28f));
        style.ColorTabUnfocusedActive = surf;
        style.ColorCheckMark = acc;
        style.ColorSliderGrab = acc;
        style.ColorSliderGrabActive = active;
        style.ColorSeparator = ToValue4(SettingsLerpColor(surface, text, 0.20f));
        style.ColorSeparatorHovered = hover;
        style.ColorSeparatorActive = active;
        style.ColorBorder = ToValue4(SettingsLerpColor(surface, text, 0.25f));
        style.ColorScrollbarGrab = ToValue4(SettingsLerpColor(surface, text, 0.15f));
        style.ColorScrollbarGrabHovered = hover;
        style.ColorScrollbarGrabActive = active;
        style.ColorBackgroundScrollbar = ToValue4(SettingsLerpColor(background, surface, 0.25f));
        style.ColorBackgroundTextSelected = ToValue4(new NVector4(accent.X, accent.Y, accent.Z, 0.42f));
    }

    private void ApplySettingsAccessibility(Style style)
    {
        if (_devToolsConfig.FramePaddingX >= 0f || _devToolsConfig.FramePaddingY >= 0f)
        {
            style.PaddingFrame = new Value2(
                _devToolsConfig.FramePaddingX >= 0f ? _devToolsConfig.FramePaddingX : style.PaddingFrame.X,
                _devToolsConfig.FramePaddingY >= 0f ? _devToolsConfig.FramePaddingY : style.PaddingFrame.Y);
        }
        if (_devToolsConfig.ItemSpacingX >= 0f || _devToolsConfig.ItemSpacingY >= 0f)
        {
            style.SpacingItem = new Value2(
                _devToolsConfig.ItemSpacingX >= 0f ? _devToolsConfig.ItemSpacingX : style.SpacingItem.X,
                _devToolsConfig.ItemSpacingY >= 0f ? _devToolsConfig.ItemSpacingY : style.SpacingItem.Y);
        }
        if (_devToolsConfig.FrameRounding >= 0f) style.RoundingFrame = _devToolsConfig.FrameRounding;
        if (_devToolsConfig.WindowRounding >= 0f) style.RoundingWindow = _devToolsConfig.WindowRounding;
        if (_devToolsConfig.HoverDelayNormal >= 0f) style.HoverDelayNormal = _devToolsConfig.HoverDelayNormal;
        if (_devToolsConfig.HoverDelayShort >= 0f) style.HoverDelayShort = _devToolsConfig.HoverDelayShort;
    }

    private void ApplySettingsColorOverrides(Style style)
    {
        foreach ((string key, float[] rgba) in _devToolsConfig.AdvancedColorOverrides)
        {
            if (!Enum.TryParse(key, ignoreCase: true, out ImGuiCol colorSlot)) continue;
            SetStyleColor(style, colorSlot, new NVector4(rgba[0], rgba[1], rgba[2], rgba[3]));
        }
    }

    private void ApplySettingsFont(Style style)
    {
        if (_devToolsConfig.FontName.Equals(SettingsFontDefault, StringComparison.OrdinalIgnoreCase)) return;
        if (!TryResolveSettingsFontName(_devToolsConfig.FontName, _devToolsConfig.FontSize, out string resolvedName)) return;

        style.FontName = resolvedName;
        style.FontSize = _devToolsConfig.FontSize;
    }

    private Vector4 GetCurrentStyleColor(ImGuiCol slot)
    {
        if (_devToolsConfig.AdvancedColorOverrides.TryGetValue(slot.ToString(), out float[]? rgba) && rgba.Length >= 4)
        {
            return new Vector4(rgba[0], rgba[1], rgba[2], rgba[3]);
        }

        Style style = BuildCurrentDevToolsStyle();
        return ToVector4(GetStyleColor(style, slot));
    }

    private string BuildSettingsThemeJson()
    {
        JObject root = new()
        {
            ["themePreset"] = _devToolsConfig.ThemePreset,
            ["fontName"] = _devToolsConfig.FontName,
            ["fontSize"] = _devToolsConfig.FontSize,
            ["applyStyleGlobally"] = _devToolsConfig.ApplyStyleGlobally,
            ["framePaddingX"] = _devToolsConfig.FramePaddingX,
            ["framePaddingY"] = _devToolsConfig.FramePaddingY,
            ["itemSpacingX"] = _devToolsConfig.ItemSpacingX,
            ["itemSpacingY"] = _devToolsConfig.ItemSpacingY,
            ["frameRounding"] = _devToolsConfig.FrameRounding,
            ["windowRounding"] = _devToolsConfig.WindowRounding,
            ["hoverDelayNormal"] = _devToolsConfig.HoverDelayNormal,
            ["hoverDelayShort"] = _devToolsConfig.HoverDelayShort,
            ["advancedColorOverrides"] = JObject.FromObject(_devToolsConfig.AdvancedColorOverrides)
        };
        return root.ToString(Formatting.Indented);
    }

    private void ImportSettingsThemeJson(string json)
    {
        JObject root = JObject.Parse(json);
        _devToolsConfig.ThemePreset = root["themePreset"]?.ToString() ?? SettingsPresetCustom;
        _devToolsConfig.FontName = root["fontName"]?.ToString() ?? SettingsFontDefault;
        _devToolsConfig.FontSize = root["fontSize"]?.Value<int?>() ?? 16;
        _devToolsConfig.ApplyStyleGlobally = root["applyStyleGlobally"]?.Value<bool?>() ?? false;
        _devToolsConfig.FramePaddingX = ReadOptionalFloat(root, "framePaddingX");
        _devToolsConfig.FramePaddingY = ReadOptionalFloat(root, "framePaddingY");
        _devToolsConfig.ItemSpacingX = ReadOptionalFloat(root, "itemSpacingX");
        _devToolsConfig.ItemSpacingY = ReadOptionalFloat(root, "itemSpacingY");
        _devToolsConfig.FrameRounding = ReadOptionalFloat(root, "frameRounding");
        _devToolsConfig.WindowRounding = ReadOptionalFloat(root, "windowRounding");
        _devToolsConfig.HoverDelayNormal = ReadOptionalFloat(root, "hoverDelayNormal");
        _devToolsConfig.HoverDelayShort = ReadOptionalFloat(root, "hoverDelayShort");
        _devToolsConfig.AdvancedColorOverrides.Clear();
        if (root["advancedColorOverrides"] is JObject colors)
        {
            foreach (JProperty property in colors.Properties())
            {
                if (property.Value is not JArray array || array.Count < 4) continue;
                _devToolsConfig.AdvancedColorOverrides[property.Name] =
                [
                    array[0]?.Value<float>() ?? 1f,
                    array[1]?.Value<float>() ?? 1f,
                    array[2]?.Value<float>() ?? 1f,
                    array[3]?.Value<float>() ?? 1f
                ];
            }
        }
        _devToolsConfig.Normalize();
    }

    private static float ReadOptionalFloat(JObject root, string name)
    {
        return root[name]?.Value<float?>() ?? -1f;
    }

    private void QueueDevToolsConfigSave(string status)
    {
        _settingsStatus = status;
        _devToolsConfigSaveQueued = true;
        _devToolsConfigSaveAfter = DateTime.UtcNow.AddMilliseconds(350).Ticks;
    }

    private void FlushDevToolsConfigSave(bool force)
    {
        if (!_devToolsConfigSaveQueued) return;
        if (!force && DateTime.UtcNow.Ticks < _devToolsConfigSaveAfter) return;

        try
        {
            _devToolsConfig.Normalize();
            _api.StoreModConfig(_devToolsConfig, DevToolsConfig.FileName);
            _devToolsConfigSaveQueued = false;
            _settingsStatus = "Settings saved.";
        }
        catch (Exception exception)
        {
            _settingsStatus = $"Settings save failed: {exception.Message}";
            _api.Logger.Warning("[InGameDevTools] Settings save failed: {0}", exception);
        }
    }

    private static Value4 ToValue4(NVector4 value) => new(value.X, value.Y, value.Z, value.W);

    private static Vector4 ToVector4(Value4 value) => new(value.X, value.Y, value.Z, value.W);

    private static NVector4 SettingsLerpColor(NVector4 a, NVector4 b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new NVector4(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t,
            a.W + (b.W - a.W) * t);
    }

    private static Value4 GetStyleColor(Style style, ImGuiCol slot)
    {
        return slot switch
        {
            ImGuiCol.Text => style.ColorText,
            ImGuiCol.TextDisabled => style.ColorTextDisabled,
            ImGuiCol.WindowBg => style.ColorBackgroundWindow,
            ImGuiCol.ChildBg => style.ColorBackgroundChild,
            ImGuiCol.PopupBg => style.ColorBackgroundPopup,
            ImGuiCol.Border => style.ColorBorder,
            ImGuiCol.BorderShadow => style.ColorBorderShadow,
            ImGuiCol.FrameBg => style.ColorBackgroundFrame,
            ImGuiCol.FrameBgHovered => style.ColorBackgroundFrameHovered,
            ImGuiCol.FrameBgActive => style.ColorBackgroundFrameActive,
            ImGuiCol.TitleBg => style.ColorBackgroundTitle,
            ImGuiCol.TitleBgActive => style.ColorBackgroundTitleActive,
            ImGuiCol.TitleBgCollapsed => style.ColorBackgroundTitleCollapsed,
            ImGuiCol.MenuBarBg => style.ColorBackgroundMenuBar,
            ImGuiCol.ScrollbarBg => style.ColorBackgroundScrollbar,
            ImGuiCol.ScrollbarGrab => style.ColorScrollbarGrab,
            ImGuiCol.ScrollbarGrabHovered => style.ColorScrollbarGrabHovered,
            ImGuiCol.ScrollbarGrabActive => style.ColorScrollbarGrabActive,
            ImGuiCol.CheckMark => style.ColorCheckMark,
            ImGuiCol.SliderGrab => style.ColorSliderGrab,
            ImGuiCol.SliderGrabActive => style.ColorSliderGrabActive,
            ImGuiCol.Button => style.ColorButton,
            ImGuiCol.ButtonHovered => style.ColorButtonHovered,
            ImGuiCol.ButtonActive => style.ColorButtonActive,
            ImGuiCol.Header => style.ColorHeader,
            ImGuiCol.HeaderHovered => style.ColorHeaderHovered,
            ImGuiCol.HeaderActive => style.ColorHeaderActive,
            ImGuiCol.Separator => style.ColorSeparator,
            ImGuiCol.SeparatorHovered => style.ColorSeparatorHovered,
            ImGuiCol.SeparatorActive => style.ColorSeparatorActive,
            ImGuiCol.ResizeGrip => style.ColorResizeGrip,
            ImGuiCol.ResizeGripHovered => style.ColorResizeGripHovered,
            ImGuiCol.ResizeGripActive => style.ColorResizeGripActive,
            ImGuiCol.Tab => style.ColorTab,
            ImGuiCol.TabHovered => style.ColorTabHovered,
            ImGuiCol.TabActive => style.ColorTabActive,
            ImGuiCol.TabUnfocused => style.ColorTabUnfocused,
            ImGuiCol.TabUnfocusedActive => style.ColorTabUnfocusedActive,
            ImGuiCol.DockingPreview => style.ColorDockingPreview,
            ImGuiCol.DockingEmptyBg => style.ColorBackgroundDockingEmpty,
            ImGuiCol.PlotLines => style.ColorPlotLines,
            ImGuiCol.PlotLinesHovered => style.ColorPlotLinesHovered,
            ImGuiCol.PlotHistogram => style.ColorPlotHistogram,
            ImGuiCol.PlotHistogramHovered => style.ColorPlotHistogramHovered,
            ImGuiCol.TableHeaderBg => style.ColorBackgroundTableHeader,
            ImGuiCol.TableBorderStrong => style.ColorTableBorderStrong,
            ImGuiCol.TableBorderLight => style.ColorTableBorderLight,
            ImGuiCol.TableRowBg => style.ColorBackgroundTableRow,
            ImGuiCol.TableRowBgAlt => style.ColorBackgroundTableRowAlt,
            ImGuiCol.TextSelectedBg => style.ColorBackgroundTextSelected,
            ImGuiCol.DragDropTarget => style.ColorDragDropTarget,
            ImGuiCol.NavHighlight => style.ColorNavHighlight,
            ImGuiCol.NavWindowingHighlight => style.ColorNavWindowingHighlight,
            ImGuiCol.NavWindowingDimBg => style.ColorBackgroundNavWindowingDim,
            ImGuiCol.ModalWindowDimBg => style.ColorBackgroundModalWindowDim,
            _ => ToValue4(ImGui.GetStyle().Colors[(int)slot])
        };
    }

    private static void SetStyleColor(Style style, ImGuiCol slot, NVector4 value)
    {
        Value4 color = ToValue4(value);
        switch (slot)
        {
            case ImGuiCol.Text: style.ColorText = color; break;
            case ImGuiCol.TextDisabled: style.ColorTextDisabled = color; break;
            case ImGuiCol.WindowBg: style.ColorBackgroundWindow = color; break;
            case ImGuiCol.ChildBg: style.ColorBackgroundChild = color; break;
            case ImGuiCol.PopupBg: style.ColorBackgroundPopup = color; break;
            case ImGuiCol.Border: style.ColorBorder = color; break;
            case ImGuiCol.BorderShadow: style.ColorBorderShadow = color; break;
            case ImGuiCol.FrameBg: style.ColorBackgroundFrame = color; break;
            case ImGuiCol.FrameBgHovered: style.ColorBackgroundFrameHovered = color; break;
            case ImGuiCol.FrameBgActive: style.ColorBackgroundFrameActive = color; break;
            case ImGuiCol.TitleBg: style.ColorBackgroundTitle = color; break;
            case ImGuiCol.TitleBgActive: style.ColorBackgroundTitleActive = color; break;
            case ImGuiCol.TitleBgCollapsed: style.ColorBackgroundTitleCollapsed = color; break;
            case ImGuiCol.MenuBarBg: style.ColorBackgroundMenuBar = color; break;
            case ImGuiCol.ScrollbarBg: style.ColorBackgroundScrollbar = color; break;
            case ImGuiCol.ScrollbarGrab: style.ColorScrollbarGrab = color; break;
            case ImGuiCol.ScrollbarGrabHovered: style.ColorScrollbarGrabHovered = color; break;
            case ImGuiCol.ScrollbarGrabActive: style.ColorScrollbarGrabActive = color; break;
            case ImGuiCol.CheckMark: style.ColorCheckMark = color; break;
            case ImGuiCol.SliderGrab: style.ColorSliderGrab = color; break;
            case ImGuiCol.SliderGrabActive: style.ColorSliderGrabActive = color; break;
            case ImGuiCol.Button: style.ColorButton = color; break;
            case ImGuiCol.ButtonHovered: style.ColorButtonHovered = color; break;
            case ImGuiCol.ButtonActive: style.ColorButtonActive = color; break;
            case ImGuiCol.Header: style.ColorHeader = color; break;
            case ImGuiCol.HeaderHovered: style.ColorHeaderHovered = color; break;
            case ImGuiCol.HeaderActive: style.ColorHeaderActive = color; break;
            case ImGuiCol.Separator: style.ColorSeparator = color; break;
            case ImGuiCol.SeparatorHovered: style.ColorSeparatorHovered = color; break;
            case ImGuiCol.SeparatorActive: style.ColorSeparatorActive = color; break;
            case ImGuiCol.ResizeGrip: style.ColorResizeGrip = color; break;
            case ImGuiCol.ResizeGripHovered: style.ColorResizeGripHovered = color; break;
            case ImGuiCol.ResizeGripActive: style.ColorResizeGripActive = color; break;
            case ImGuiCol.Tab: style.ColorTab = color; break;
            case ImGuiCol.TabHovered: style.ColorTabHovered = color; break;
            case ImGuiCol.TabActive: style.ColorTabActive = color; break;
            case ImGuiCol.TabUnfocused: style.ColorTabUnfocused = color; break;
            case ImGuiCol.TabUnfocusedActive: style.ColorTabUnfocusedActive = color; break;
            case ImGuiCol.DockingPreview: style.ColorDockingPreview = color; break;
            case ImGuiCol.DockingEmptyBg: style.ColorBackgroundDockingEmpty = color; break;
            case ImGuiCol.PlotLines: style.ColorPlotLines = color; break;
            case ImGuiCol.PlotLinesHovered: style.ColorPlotLinesHovered = color; break;
            case ImGuiCol.PlotHistogram: style.ColorPlotHistogram = color; break;
            case ImGuiCol.PlotHistogramHovered: style.ColorPlotHistogramHovered = color; break;
            case ImGuiCol.TableHeaderBg: style.ColorBackgroundTableHeader = color; break;
            case ImGuiCol.TableBorderStrong: style.ColorTableBorderStrong = color; break;
            case ImGuiCol.TableBorderLight: style.ColorTableBorderLight = color; break;
            case ImGuiCol.TableRowBg: style.ColorBackgroundTableRow = color; break;
            case ImGuiCol.TableRowBgAlt: style.ColorBackgroundTableRowAlt = color; break;
            case ImGuiCol.TextSelectedBg: style.ColorBackgroundTextSelected = color; break;
            case ImGuiCol.DragDropTarget: style.ColorDragDropTarget = color; break;
            case ImGuiCol.NavHighlight: style.ColorNavHighlight = color; break;
            case ImGuiCol.NavWindowingHighlight: style.ColorNavWindowingHighlight = color; break;
            case ImGuiCol.NavWindowingDimBg: style.ColorBackgroundNavWindowingDim = color; break;
            case ImGuiCol.ModalWindowDimBg: style.ColorBackgroundModalWindowDim = color; break;
        }
    }

    private readonly struct DevToolsStyleScope : IDisposable
    {
        private readonly StyleApplier? _applier;

        public DevToolsStyleScope(StyleApplier? applier)
        {
            _applier = applier;
        }

        public void Dispose()
        {
            _applier?.Dispose();
        }
    }
}
