using ImGuiNET;
using InGameDevTools.Utils;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private const int CommandPaletteRecentLimit = 8;

    private DevToolsTab? _requestedDevToolsTab;
    private bool _commandPaletteOpen;
    private bool _commandPaletteFocusSearch;
    private string _commandPaletteFilter = "";
    private int _commandPaletteSelectedIndex;
    private string _commandPaletteStatus = "";
    private readonly List<DevToolsCommandPaletteEntry> _commandPaletteRecentEntries = [];

    private sealed record DevToolsCommandPaletteEntry(
        string Title,
        string Subtitle,
        string SearchText,
        DevToolsTab TargetTab,
        Action Execute,
        int Score = 0);

    private void RequestDevToolsTab(DevToolsTab tab)
    {
        _requestedDevToolsTab = tab;
        _activeDevToolsTab = tab;
        if (tab == DevToolsTab.Animations)
        {
            _selectVanillaAnimationsTabOnNextDraw = true;
        }
    }

    private ImGuiTabItemFlags GetMainTabFlags(DevToolsTab tab)
    {
        if (_requestedDevToolsTab == tab) return ImGuiTabItemFlags.SetSelected;
        if (tab == DevToolsTab.Animations && _selectVanillaAnimationsTabOnNextDraw) return ImGuiTabItemFlags.SetSelected;
        return ImGuiTabItemFlags.None;
    }

    private void AcceptMainTabSelection(DevToolsTab tab)
    {
        _activeDevToolsTab = tab;
        if (_requestedDevToolsTab == tab)
        {
            _requestedDevToolsTab = null;
        }

        if (tab == DevToolsTab.Animations)
        {
            _selectVanillaAnimationsTabOnNextDraw = false;
        }
    }

    private void DrawCommandPaletteButton()
    {
        if (ImGui.Button("Command##devtools-command-palette"))
        {
            OpenCommandPalette();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Open global command palette (Ctrl+P).");
        }
    }

    private void HandleCommandPaletteShortcut()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        if (io.WantTextInput || !IsDevToolsCtrlDown()) return;
        if (IsDevToolsShortcutPressed(ImGuiKey.P, GlKeys.P))
        {
            OpenCommandPalette();
        }
    }

    private void OpenCommandPalette()
    {
        _commandPaletteOpen = true;
        _commandPaletteFocusSearch = true;
        _commandPaletteSelectedIndex = 0;
        ImGui.OpenPopup("Command palette##devtools-command-palette-popup");
    }

    private void DrawCommandPalette()
    {
        if (_commandPaletteOpen)
        {
            ImGui.OpenPopup("Command palette##devtools-command-palette-popup");
        }

        NVector2 displaySize = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowSize(new NVector2(Math.Clamp(displaySize.X * 0.48f, 520f, 860f), Math.Clamp(displaySize.Y * 0.58f, 360f, 660f)), ImGuiCond.Appearing);

        bool open = _commandPaletteOpen;
        if (!ImGui.BeginPopupModal("Command palette##devtools-command-palette-popup", ref open, ImGuiWindowFlags.NoSavedSettings))
        {
            _commandPaletteOpen = open;
            return;
        }

        if (_commandPaletteFocusSearch)
        {
            ImGui.SetKeyboardFocusHere();
            _commandPaletteFocusSearch = false;
        }

        if (ImGui.InputTextWithHint("##devtools-command-palette-filter", "Search tabs, blocks, items, entities, recipes, worldgen, patches...", ref _commandPaletteFilter, 256))
        {
            _commandPaletteSelectedIndex = 0;
        }

        List<DevToolsCommandPaletteEntry> entries = BuildCommandPaletteEntries(_commandPaletteFilter);
        _commandPaletteSelectedIndex = Math.Clamp(_commandPaletteSelectedIndex, 0, Math.Max(0, entries.Count - 1));

        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
        {
            _commandPaletteSelectedIndex = Math.Min(entries.Count - 1, _commandPaletteSelectedIndex + 1);
        }
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
        {
            _commandPaletteSelectedIndex = Math.Max(0, _commandPaletteSelectedIndex - 1);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Enter) && entries.Count > 0)
        {
            ExecuteCommandPaletteEntry(entries[_commandPaletteSelectedIndex]);
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _commandPaletteOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.Separator();
        if (!string.IsNullOrWhiteSpace(_commandPaletteStatus))
        {
            ImGui.TextDisabled(_commandPaletteStatus);
        }

        if (ImGui.BeginChild("##devtools-command-palette-results", new NVector2(-float.Epsilon, -float.Epsilon), true))
        {
            for (int i = 0; i < entries.Count; i++)
            {
                DevToolsCommandPaletteEntry entry = entries[i];
                bool selected = i == _commandPaletteSelectedIndex;
                if (ImGui.Selectable($"{entry.Title}##devtools-command-{i}", selected))
                {
                    ExecuteCommandPaletteEntry(entry);
                }
                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }

                ImGui.TextDisabled(entry.Subtitle);
            }
        }
        ImGui.EndChild();

        ImGui.EndPopup();
        _commandPaletteOpen = open;
    }

    private void ExecuteCommandPaletteEntry(DevToolsCommandPaletteEntry entry)
    {
        try
        {
            entry.Execute();
            _commandPaletteStatus = $"Opened {entry.Title}.";
            RememberCommandPaletteEntry(entry);
        }
        catch (Exception exception)
        {
            _commandPaletteStatus = $"Command failed: {exception.Message}";
            _devToolsDiagnostics.Exception($"Command palette failed: {entry.Title}", exception);
        }

        _commandPaletteOpen = false;
        ImGui.CloseCurrentPopup();
    }

    private void RememberCommandPaletteEntry(DevToolsCommandPaletteEntry entry)
    {
        _commandPaletteRecentEntries.RemoveAll(recent => recent.Title.Equals(entry.Title, StringComparison.OrdinalIgnoreCase));
        _commandPaletteRecentEntries.Insert(0, entry);
        if (_commandPaletteRecentEntries.Count > CommandPaletteRecentLimit)
        {
            _commandPaletteRecentEntries.RemoveRange(CommandPaletteRecentLimit, _commandPaletteRecentEntries.Count - CommandPaletteRecentLimit);
        }
    }

    private List<DevToolsCommandPaletteEntry> BuildCommandPaletteEntries(string filter)
    {
        string normalizedFilter = filter.Trim();
        bool includeAssets = normalizedFilter.Length >= 2;
        List<DevToolsCommandPaletteEntry> entries = [];

        AddCommandPaletteEntry(entries, normalizedFilter, "Open Animations", "Tab", "animations animation entity model", DevToolsTab.Animations, () => RequestDevToolsTab(DevToolsTab.Animations));
        AddCommandPaletteEntry(entries, normalizedFilter, "Open Recipe Editor", "Tab", "recipe recipes crafting", DevToolsTab.RecipeEditor, () => RequestDevToolsTab(DevToolsTab.RecipeEditor));
        AddCommandPaletteEntry(entries, normalizedFilter, "Open Particles", "Tab", "particle particles effects", DevToolsTab.Particles, () => RequestDevToolsTab(DevToolsTab.Particles));
        AddCommandPaletteEntry(entries, normalizedFilter, "Open Transforms", "Tab", "transform item block model placement", DevToolsTab.Transforms, () => RequestDevToolsTab(DevToolsTab.Transforms));
        AddCommandPaletteEntry(entries, normalizedFilter, "Open Models", "Tab", "model models shape shapes cube editor uv texture", DevToolsTab.Models, () => RequestDevToolsTab(DevToolsTab.Models));
        AddCommandPaletteEntry(entries, normalizedFilter, "Open ConfigLib", "Tab", "config configlib modconfig", DevToolsTab.ConfigLib, () => RequestDevToolsTab(DevToolsTab.ConfigLib));
        AddCommandPaletteEntry(entries, normalizedFilter, "Open Block/Item JSON", "Tab", "block item json attributes source authoring", DevToolsTab.BlockItemJson, () => RequestDevToolsTab(DevToolsTab.BlockItemJson));
        AddCommandPaletteEntry(entries, normalizedFilter, "Open Loot/Drops", "Tab", "loot drops trade table", DevToolsTab.LootDrops, () => RequestDevToolsTab(DevToolsTab.LootDrops));
        AddCommandPaletteEntry(entries, normalizedFilter, "Open Worldgen", "Tab", "worldgen deposits block patches landforms strata", DevToolsTab.Worldgen, () => RequestDevToolsTab(DevToolsTab.Worldgen));
        AddCommandPaletteEntry(entries, normalizedFilter, "Open Patches", "Tab", "patches jsonpatcheslib patch creator", DevToolsTab.Patches, () => RequestDevToolsTab(DevToolsTab.Patches));
        AddCommandPaletteEntry(entries, normalizedFilter, "Open Entity AI", "Tab", "entity ai taskai behavior tasks", DevToolsTab.EntityAi, () => RequestDevToolsTab(DevToolsTab.EntityAi));
        AddCommandPaletteEntry(entries, normalizedFilter, "Open Settings", "Tab", "settings theme font colors", DevToolsTab.Settings, () => RequestDevToolsTab(DevToolsTab.Settings));

        if (!includeAssets)
        {
            _commandPaletteStatus = _commandPaletteRecentEntries.Count > 0
                ? "Recent commands first. Type at least two characters to search assets."
                : "Type at least two characters to search assets.";
            return PrependCommandPaletteRecents(entries, normalizedFilter);
        }

        AddRuntimeCollectibleCommands(entries, normalizedFilter);
        AddRuntimeEntityCommands(entries, normalizedFilter);
        AddModelShapeCommands(entries, normalizedFilter);
        AddRecipeCommands(entries, normalizedFilter);
        AddLootDropCommands(entries, normalizedFilter);
        AddWorldgenCommands(entries, normalizedFilter);
        AddPatchCreatorCommands(entries, normalizedFilter);
        AddAiBehaviorCommands(entries, normalizedFilter);

        _commandPaletteStatus = $"{entries.Count} command(s), best matches first.";
        return entries
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => GetCommandPaletteTabOrder(entry.TargetTab))
            .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .Take(220)
            .ToList();
    }

    private List<DevToolsCommandPaletteEntry> PrependCommandPaletteRecents(List<DevToolsCommandPaletteEntry> entries, string filter)
    {
        if (_commandPaletteRecentEntries.Count == 0) return entries;

        List<DevToolsCommandPaletteEntry> combined = [];
        foreach (DevToolsCommandPaletteEntry recent in _commandPaletteRecentEntries)
        {
            if (!DevToolsFuzzyMatch.Matches(recent.SearchText, filter)) continue;
            combined.Add(recent with { Subtitle = $"Recent - {recent.Subtitle}".TrimEnd(' ', '-') });
        }

        foreach (DevToolsCommandPaletteEntry entry in entries)
        {
            if (combined.Any(existing => existing.Title.Equals(entry.Title, StringComparison.OrdinalIgnoreCase))) continue;
            combined.Add(entry);
        }

        return combined;
    }

    private static int GetCommandPaletteTabOrder(DevToolsTab tab) => tab switch
    {
        DevToolsTab.Animations => 0,
        DevToolsTab.Transforms => 1,
        DevToolsTab.Models => 2,
        DevToolsTab.Particles => 3,
        DevToolsTab.RecipeEditor => 4,
        DevToolsTab.LootDrops => 5,
        DevToolsTab.Worldgen => 6,
        DevToolsTab.Patches => 7,
        DevToolsTab.EntityAi => 8,
        DevToolsTab.ConfigLib => 9,
        DevToolsTab.Settings => 10,
        _ => 99
    };

    private static void AddCommandPaletteEntry(List<DevToolsCommandPaletteEntry> entries, string filter, string title, string subtitle, string search, DevToolsTab tab, Action execute)
    {
        TryAddCommandPaletteEntry(entries, filter, title, subtitle, $"{title} {subtitle} {search}", tab, execute);
    }

    private static bool TryAddCommandPaletteEntry(List<DevToolsCommandPaletteEntry> entries, string filter, string title, string subtitle, string search, DevToolsTab tab, Action execute)
    {
        int score = DevToolsFuzzyMatch.Score(search, filter);
        if (score < 0) return false;
        entries.Add(new(title, subtitle, search, tab, execute, score));
        return true;
    }

    private void AddRuntimeCollectibleCommands(List<DevToolsCommandPaletteEntry> entries, string filter)
    {
        foreach (Block block in _api.World.Blocks)
        {
            if (block?.Code == null) continue;
            string code = block.Code.ToString();
            string compact = ImGuiLayoutHelper.CompactAssetCode(code);
            TryAddCommandPaletteEntry(entries, filter, $"Transform block {compact}", code, $"transform block {code}", DevToolsTab.Transforms, () => JumpToTransformAsset($"block:{code}"));
            TryAddCommandPaletteEntry(entries, filter, $"Patch block JSON {compact}", code, $"patch block json {code}", DevToolsTab.Patches, () => JumpToPatchCreatorAsset($"blocktypes/{block.Code.Path}.json", block.Code.Domain));
            if (entries.Count > 180) return;
        }

        foreach (Item item in _api.World.Items)
        {
            if (item?.Code == null) continue;
            string code = item.Code.ToString();
            string compact = ImGuiLayoutHelper.CompactAssetCode(code);
            TryAddCommandPaletteEntry(entries, filter, $"Transform item {compact}", code, $"transform item {code}", DevToolsTab.Transforms, () => JumpToTransformAsset($"item:{code}"));
            TryAddCommandPaletteEntry(entries, filter, $"Patch item JSON {compact}", code, $"patch item json {code}", DevToolsTab.Patches, () => JumpToPatchCreatorAsset($"itemtypes/{item.Code.Path}.json", item.Code.Domain));
            if (entries.Count > 180) return;
        }
    }

    private void AddRuntimeEntityCommands(List<DevToolsCommandPaletteEntry> entries, string filter)
    {
        foreach (EntityProperties entityType in _api.World.EntityTypes ?? [])
        {
            if (entityType?.Code == null) continue;
            string code = entityType.Code.ToString();
            string compact = ImGuiLayoutHelper.CompactAssetCode(code);
            TryAddCommandPaletteEntry(entries, filter, $"Animate entity {compact}", code, $"animate entity {code}", DevToolsTab.Animations, () => JumpToVanillaEntity(code));
            TryAddCommandPaletteEntry(entries, filter, $"Entity AI {compact}", code, $"entity ai {code}", DevToolsTab.EntityAi, () => JumpToAiBehaviorEntity(code));
            TryAddCommandPaletteEntry(entries, filter, $"Loot drops for entity {compact}", code, $"loot drops entity {code}", DevToolsTab.LootDrops, () => JumpToLootDropEntry($"entity:{code}"));
            TryAddCommandPaletteEntry(entries, filter, $"Patch entity JSON {compact}", code, $"patch entity json {code}", DevToolsTab.Patches, () => JumpToPatchCreatorAsset($"entities/{entityType.Code.Path}.json", entityType.Code.Domain));
            if (entries.Count > 210) return;
        }
    }

    private void AddModelShapeCommands(List<DevToolsCommandPaletteEntry> entries, string filter)
    {
        EnsureModelShapeIndex();
        foreach (ModelShapeAssetEntry entry in _modelShapeIndex ?? [])
        {
            string suffix = entry.Authored ? " [authored]" : "";
            TryAddCommandPaletteEntry(
                entries,
                filter,
                $"Model shape {entry.Domain}:{entry.AssetPath}{suffix}",
                entry.Authored ? "Authored shape file" : "Shape asset",
                $"model shape {entry.Domain}:{entry.AssetPath}",
                DevToolsTab.Models,
                () => JumpToModelShape(entry));
            if (entries.Count > 210) return;
        }
    }

    private void JumpToModelShape(ModelShapeAssetEntry entry)
    {
        RequestDevToolsTab(DevToolsTab.Models);
        ModelRequestOpenDocument(entry);
        _modelStatus = $"Opening {entry.Domain}:{entry.AssetPath} from command palette.";
    }

    private void AddRecipeCommands(List<DevToolsCommandPaletteEntry> entries, string filter)
    {
        foreach ((string key, string title, string subtitle, string search) in _recipeEditor.GetCommandPaletteEntries())
        {
            TryAddCommandPaletteEntry(entries, filter, $"Recipe {title}", subtitle, $"recipe {title} {search}", DevToolsTab.RecipeEditor, () => JumpToRecipe(key));
            if (entries.Count > 210) return;
        }
    }

    private void AddLootDropCommands(List<DevToolsCommandPaletteEntry> entries, string filter)
    {
        foreach (LootDropEntry entry in _lootDropEntries)
        {
            TryAddCommandPaletteEntry(entries, filter, $"Loot/drop {entry.Label}", entry.Tooltip, $"loot drop {entry.SearchText}", DevToolsTab.LootDrops, () => JumpToLootDropEntry(entry.Key));
            if (entries.Count > 210) return;
        }
    }

    private void AddWorldgenCommands(List<DevToolsCommandPaletteEntry> entries, string filter)
    {
        foreach (WorldgenAssetEntry entry in _worldgenEntries)
        {
            string title = $"Worldgen {entry.KindLabel} {entry.Domain}:{entry.AssetPath}";
            TryAddCommandPaletteEntry(entries, filter, title, entry.Key, $"{title} {entry.SearchText}", DevToolsTab.Worldgen, () => JumpToWorldgenAsset(entry.Key));
            if (entries.Count > 210) return;
        }
    }

    private void AddPatchCreatorCommands(List<DevToolsCommandPaletteEntry> entries, string filter)
    {
        foreach (PatchCreatorAssetEntry entry in _patchCreatorAssets)
        {
            string title = $"Patch target {entry.Domain}:{entry.AssetPath}";
            TryAddCommandPaletteEntry(entries, filter, title, entry.Category, $"{title} {entry.Category}", DevToolsTab.Patches, () => JumpToPatchCreatorAsset(entry.Key));
            if (entries.Count > 210) return;
        }
    }

    private void AddAiBehaviorCommands(List<DevToolsCommandPaletteEntry> entries, string filter)
    {
        foreach (AiBehaviorEntry entry in _aiBehaviorEntries)
        {
            TryAddCommandPaletteEntry(entries, filter, $"Entity AI source {entry.DisplayCode}", entry.AssetPath, $"entity ai source {entry.SearchText}", DevToolsTab.EntityAi, () => JumpToAiBehaviorEntry(entry.Key));
            if (entries.Count > 210) return;
        }
    }

    private void JumpToRecipe(string key)
    {
        RequestDevToolsTab(DevToolsTab.RecipeEditor);
        if (!_recipeEditor.JumpToRecipe(key, out string status))
        {
            _commandPaletteStatus = status;
        }
    }

    private void JumpToTransformAsset(string key)
    {
        RequestDevToolsTab(DevToolsTab.Transforms);
        EnsureTransformAssetsIndexed();
        _transformsFilter = key;
        _transformsDomainFilter = ExtractCommandPaletteDomain(key);
        _transformsTypeFilter = key.StartsWith("block:", StringComparison.OrdinalIgnoreCase) ? 2 : key.StartsWith("item:", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _transformsDirtyOnly = false;
        _transformsOnlyApplicable = false;
        _transformsShowUncertain = true;
        RebuildVisibleTransformAssets();

        int index = _visibleTransformAssets.FindIndex(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _transformsAssetIndex = index;
            _transformsStatus = $"Selected {key} from command palette.";
        }
        else
        {
            _transformsStatus = $"Command palette could not find transform asset {key}.";
        }
    }

    private void JumpToVanillaEntity(string code)
    {
        RequestDevToolsTab(DevToolsTab.Animations);
        RequestVanillaAnimationSourceTab(VanillaAnimationSourceMode.Entities);
        _vanillaIndex.EnsureEntityList(_api);
        _vanillaEntitySelectorMode = VanillaEntitySelectorMode.Exact;
        _vanillaShowHiddenEntities = true;
        _vanillaEntityFilter = code;
        IReadOnlyList<VanillaEntityOption> options = _vanillaIndex.GetEntityOptions(VanillaEntitySelectorMode.Exact, showHidden: true);
        for (int optionIndex = 0; optionIndex < options.Count; optionIndex++)
        {
            VanillaEntityOption option = options[optionIndex];
            int memberIndex = option.Members.ToList().FindIndex(member => member.Code.Equals(code, StringComparison.OrdinalIgnoreCase) || member.FullLabel.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (memberIndex < 0) continue;
            _vanillaIndex.SelectEntity(_api, option, memberIndex, groupEdit: false);
            ResetVanillaEntitySelectionState();
            _vanillaStatus = $"Selected {code} from command palette.";
            return;
        }

        _vanillaStatus = $"Command palette could not find entity {code}.";
    }

    private void JumpToLootDropEntry(string key)
    {
        RequestDevToolsTab(DevToolsTab.LootDrops);
        EnsureLootDropEntriesIndexed();
        _lootDropFilter = key;
        _lootDropDomainFilter = ExtractCommandPaletteDomain(key);
        _lootDropKindFilter = key.StartsWith("block:", StringComparison.OrdinalIgnoreCase) ? 1 : key.StartsWith("entity:", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        _lootDropDirtyOnly = false;
        RebuildVisibleLootDropEntries();
        int index = _visibleLootDropEntries.FindIndex(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _lootDropEntryIndex = index;
            LoadLootDropEntry(_visibleLootDropEntries[index], keepDirty: true);
            _lootDropStatus = $"Selected {key} from command palette.";
        }
        else
        {
            _lootDropStatus = $"Command palette could not find loot/drop source {key}.";
        }
    }

    private void JumpToWorldgenAsset(string key)
    {
        RequestDevToolsTab(DevToolsTab.Worldgen);
        EnsureWorldgenEntriesIndexed();
        _worldgenFilter = key;
        _worldgenDomainFilter = ExtractCommandPaletteDomain(key);
        _worldgenKindFilter = 0;
        _worldgenDirtyOnly = false;
        RebuildVisibleWorldgenEntries();
        int index = _visibleWorldgenEntries.FindIndex(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _worldgenEntryIndex = index;
            LoadWorldgenEntry(_visibleWorldgenEntries[index]);
            _worldgenStatus = $"Selected {key} from command palette.";
        }
        else
        {
            _worldgenStatus = $"Command palette could not find worldgen asset {key}.";
        }
    }

    private void JumpToPatchCreatorAsset(string keyOrAssetPath, string? domain = null)
    {
        RequestDevToolsTab(DevToolsTab.Patches);
        EnsurePatchCreatorAssetsIndexed();
        string key = keyOrAssetPath.Contains(':', StringComparison.Ordinal)
            ? keyOrAssetPath
            : $"{domain ?? "game"}:{keyOrAssetPath}";
        _patchCreatorFilter = keyOrAssetPath.Contains(':', StringComparison.Ordinal) ? keyOrAssetPath : key;
        _patchCreatorDomainFilter = domain ?? ExtractCommandPaletteDomain(key);
        _patchCreatorCategoryFilter = "";
        RebuildVisiblePatchCreatorAssets();
        int index = _visiblePatchCreatorAssets.FindIndex(entry =>
            entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ||
            ($"{entry.Domain}:{entry.AssetPath}").Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _patchCreatorAssetIndex = index;
            SyncPatchCreatorSelection();
            _patchCreatorStatus = $"Selected {key} from command palette.";
        }
        else
        {
            _patchCreatorStatus = $"Command palette could not find patch target {key}.";
        }
    }

    private void JumpToAiBehaviorEntity(string code)
    {
        RequestDevToolsTab(DevToolsTab.EntityAi);
        EnsureAiBehaviorEntriesIndexed();
        string key = NormalizeCommandPaletteEntityCode(code);
        _aiBehaviorFilter = key;
        _aiBehaviorDomainFilter = ExtractCommandPaletteDomain(key);
        _aiBehaviorDirtyOnly = false;
        RebuildVisibleAiBehaviorEntries();
        int index = _visibleAiBehaviorEntries.FindIndex(entry => entry.RuntimeCodes.Contains(key));
        if (index >= 0)
        {
            _aiBehaviorEntryIndex = index;
            LoadAiBehaviorEntry(_visibleAiBehaviorEntries[index], keepDirty: true);
            _aiBehaviorStatus = $"Selected {key} from command palette.";
        }
        else
        {
            _aiBehaviorStatus = $"Command palette could not find Entity AI source for {key}.";
        }
    }

    private void JumpToAiBehaviorEntry(string key)
    {
        RequestDevToolsTab(DevToolsTab.EntityAi);
        EnsureAiBehaviorEntriesIndexed();
        _aiBehaviorFilter = key;
        _aiBehaviorDomainFilter = ExtractCommandPaletteDomain(key);
        _aiBehaviorDirtyOnly = false;
        RebuildVisibleAiBehaviorEntries();
        int index = _visibleAiBehaviorEntries.FindIndex(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _aiBehaviorEntryIndex = index;
            LoadAiBehaviorEntry(_visibleAiBehaviorEntries[index], keepDirty: true);
            _aiBehaviorStatus = $"Selected {key} from command palette.";
        }
        else
        {
            _aiBehaviorStatus = $"Command palette could not find Entity AI source {key}.";
        }
    }

    private static string ExtractCommandPaletteDomain(string value)
    {
        string text = value;
        int firstColon = text.IndexOf(':');
        if (firstColon >= 0 && (text.StartsWith("block:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("item:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("entity:", StringComparison.OrdinalIgnoreCase)))
        {
            text = text[(firstColon + 1)..];
        }

        int separator = text.IndexOf(':');
        return separator > 0 ? text[..separator] : "";
    }

    private static string NormalizeCommandPaletteEntityCode(string code)
    {
        return code.StartsWith("entity:", StringComparison.OrdinalIgnoreCase) ? code[7..] : code;
    }
}
