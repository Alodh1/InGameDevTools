using InGameDevTools.Utils;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private readonly RecipeEditorState _recipeEditor = new();

    private void RecipeEditorTab(float deltaSeconds)
    {
        ClearActiveTransformGizmo();
        try
        {
            _recipeEditor.Draw(_api, _devToolsUiScale, _liveApplyManager);
        }
        catch (Exception exception)
        {
            _api.Logger.Error("[InGameDevTools] Recipe editor draw failed: {0}", exception);
            _recipeEditor.SetStatus($"Recipe editor error: {exception.Message}");
            ImGui.TextWrapped("Recipe editor hit an error. The error was written to the client log.");
            ImGui.TextWrapped(exception.Message);
        }
    }

    private enum RecipeEditorKind
    {
        Grid,
        Smithing,
        Clayforming,
        Knapping,
        Barrel,
        Cooking,
        Alloy,
        Other
    }

    private sealed class RecipeEditorState
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        private static readonly string[] KindFilterLabels =
        [
            "All",
            "Grid",
            "Smithing",
            "Clayforming",
            "Knapping",
            "Barrel",
            "Cooking",
            "Alloy",
            "Other"
        ];

        private readonly List<RecipeDocument> _documents = [];
        private readonly List<RecipeEntry> _entries = [];
        private readonly List<RecipeEntry> _visibleEntries = [];
        private bool _loaded;
        private string _filter = "";
        private int _kindFilter;
        private int _selectedIndex;
        private bool _showDirtyOnly;
        private bool _overwriteExport;
        private string _domainFilter = "";
        private string _status = "";
        private int _patternLayer;
        private string _newDomain = "game";
        private string _newName = "new-recipe";
        private int _newKindIndex;
        private string _rawBuffer = "";
        private string _rawBufferKey = "";
        private string[] _itemCodes = [];
        private string[] _blockCodes = [];
        private string[] _liquidCodes = [];
        private readonly ImGuiThreePanelLayoutState _layout = new(0.24f, 0.30f);
        private readonly Dictionary<string, string> _stackCodeFilters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _recipeLiveAppliedHashes = new(StringComparer.OrdinalIgnoreCase);
        private ICoreClientAPI? _api;
        private DevToolsLiveApplyManager? _liveApplyManager;

        public void Draw(ICoreClientAPI api, float uiScale, DevToolsLiveApplyManager? liveApplyManager)
        {
            try
            {
                _api = api;
                _liveApplyManager = liveApplyManager;
                EnsureLoaded(api);

                NVector2 available = ImGui.GetContentRegionAvail();
                float scale = Math.Max(0.75f, uiScale);
                float splitterThickness = Math.Max(5f, 6f * scale);
                ImGuiLayoutHelper.CalculateThreePanelWidths(
                    available.X,
                    splitterThickness,
                    _layout,
                    250f * scale,
                    520f * scale,
                    360f * scale,
                    320f * scale,
                    680f * scale,
                    out float panelAvailableWidth,
                    out float leftWidth,
                    out float centerWidth,
                    out float rightWidth);

                DrawRecipeBrowser(api, new NVector2(leftWidth, available.Y));
                ImGui.SameLine(0, 0);
                ImGuiLayoutHelper.DrawVerticalSplitter("##recipe-left-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _layout.LeftFraction, 250f * scale, Math.Max(250f * scale, panelAvailableWidth - rightWidth - 360f * scale));
                ImGui.SameLine(0, 0);
                DrawRecipeCanvas(new NVector2(centerWidth, available.Y));
                ImGui.SameLine(0, 0);
                ImGuiLayoutHelper.DrawVerticalSplitter("##recipe-right-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _layout.RightFraction, 320f * scale, Math.Max(320f * scale, panelAvailableWidth - leftWidth - 360f * scale), invertDrag: true);
                ImGui.SameLine(0, 0);
                DrawRecipeInspector(new NVector2(rightWidth, available.Y));
            }
            catch (Exception exception)
            {
                _status = $"Recipe editor error: {exception.Message}";
                api.Logger.Error("[InGameDevTools] Recipe editor failed: {0}", exception);
                ImGui.TextWrapped(_status);
            }
        }

        public void SetStatus(string status)
        {
            _status = status;
        }

        public void ResetLayout()
        {
            _layout.Reset();
        }

        public void ApplyDirtyRecipeLive(DevToolsLiveApplyManager liveApplyManager, bool force = false)
        {
            _liveApplyManager = liveApplyManager;
            RecipeEntry? entry = SelectedEntry;
            if (entry == null)
            {
                liveApplyManager.LastStatus = "No selected recipe to apply.";
                return;
            }

            _status = ApplyRecipeLive(entry, force);
        }

        public void ClearRecipeLiveApplyState()
        {
            _recipeLiveAppliedHashes.Clear();
        }

        private void EnsureLoaded(ICoreClientAPI api)
        {
            if (_loaded) return;
            Reload(api);
        }

        private void Reload(ICoreClientAPI api)
        {
            _documents.Clear();
            _entries.Clear();
            _visibleEntries.Clear();
            _selectedIndex = 0;
            _patternLayer = 0;
            _rawBuffer = "";
            _rawBufferKey = "";
            BuildStackCodeLists(api);

            try
            {
                foreach (IAsset asset in api.Assets.GetManyInCategory("recipes", ""))
                {
                    try
                    {
                        JToken root = ParseRecipeJson(asset.ToText());
                        string assetPath = EnsureJsonPath(asset.Location.Path);
                        RecipeDocument document = new(asset.Location.Domain, assetPath, root, false);
                        AddDocument(document);
                    }
                    catch (Exception exception)
                    {
                        _status = $"Skipped {asset.Location}: {exception.Message}";
                    }
                }

                _loaded = true;
                RebuildVisibleEntries();
                _status = $"Loaded {_entries.Count} recipe entries from {_documents.Count} assets.";
            }
            catch (Exception exception)
            {
                _loaded = true;
                _status = $"Recipe scan failed: {exception.Message}";
            }
        }

        private void AddDocument(RecipeDocument document)
        {
            _documents.Add(document);
            if (document.Root is JArray array)
            {
                for (int index = 0; index < array.Count; index++)
                {
                    if (array[index] is JObject recipe)
                    {
                        _entries.Add(new RecipeEntry(document, recipe, index));
                    }
                }
            }
            else if (document.Root is JObject recipe)
            {
                _entries.Add(new RecipeEntry(document, recipe, -1));
            }
        }

        private void DrawRecipeBrowser(ICoreClientAPI api, NVector2 size)
        {
            ImGui.BeginChild("##recipe-browser", size, true);
            try
            {
                ImGui.SeparatorText("Recipes");

                if (ImGui.Button("Reload##recipes"))
                {
                    _loaded = false;
                    Reload(api);
                }
                ImGui.SameLine();
                if (ImGui.Button("Export dirty##recipes"))
                {
                    ExportDirty();
                }

                ImGui.Checkbox("Dirty only##recipes", ref _showDirtyOnly);
                ImGui.SameLine();
                ImGui.Checkbox("Overwrite exports##recipes", ref _overwriteExport);

                if (ImGuiLayoutHelper.DrawDomainCombo("Domain##recipe-domain-filter", ref _domainFilter, _documents.Select(document => document.Domain)))
                {
                    if (!string.IsNullOrWhiteSpace(_domainFilter))
                    {
                        _newDomain = _domainFilter;
                    }
                    RebuildVisibleEntries();
                }

                if (ImGui.InputText("Filter##recipe-filter", ref _filter, 300))
                {
                    RebuildVisibleEntries();
                }

                if (ImGui.Combo("Type##recipe-kind-filter", ref _kindFilter, KindFilterLabels, KindFilterLabels.Length))
                {
                    RebuildVisibleEntries();
                }

                ImGui.SeparatorText("New");
                ImGui.InputText("Domain##recipe-new-domain", ref _newDomain, 80);
                ImGui.InputText("Name##recipe-new-name", ref _newName, 140);
                ImGui.Combo("Kind##recipe-new-kind", ref _newKindIndex, KindFilterLabels.Skip(1).ToArray(), KindFilterLabels.Length - 1);
                if (ImGui.Button("Create draft##recipe-new"))
                {
                    TryCreateDraft();
                }

                ImGui.SeparatorText($"Loaded ({_visibleEntries.Count})");
                ImGui.BeginChild("##recipe-list", new NVector2(0, 0), false);
                try
                {
                    for (int index = 0; index < _visibleEntries.Count; index++)
                    {
                        RecipeEntry entry = _visibleEntries[index];
                        bool selected = index == _selectedIndex;
                        string dirty = entry.Document.Dirty ? "* " : "";
                        if (ImGui.Selectable($"{dirty}{entry.ShortLabel}##recipe-{entry.Key}", selected))
                        {
                            _selectedIndex = index;
                            _patternLayer = 0;
                            SyncRawBuffer(entry);
                        }

                        if (ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip(entry.Document.DisplayPath);
                        }
                    }
                }
                finally
                {
                    ImGui.EndChild();
                }
            }
            catch (Exception exception)
            {
                _status = $"Recipe browser error: {exception.Message}";
                ImGui.TextWrapped(_status);
            }
            finally
            {
                ImGui.EndChild();
            }
        }

        private void DrawRecipeCanvas(NVector2 size)
        {
            ImGui.BeginChild("##recipe-canvas", size, true);
            try
            {
                RecipeEntry? entry = SelectedEntry;
                if (entry == null)
                {
                    ImGui.TextDisabled("No recipe selected.");
                    return;
                }

                ImGui.SeparatorText($"{entry.KindLabel}: {entry.DisplayName}");
                ImGui.TextDisabled(entry.Document.DisplayPath);

                switch (entry.Kind)
                {
                    case RecipeEditorKind.Grid:
                        DrawGridRecipeVisual(entry);
                        break;
                    case RecipeEditorKind.Smithing:
                    case RecipeEditorKind.Clayforming:
                    case RecipeEditorKind.Knapping:
                        DrawPatternRecipeVisual(entry);
                        break;
                    default:
                        DrawRecipeFlowVisual(entry);
                        break;
                }
            }
            catch (Exception exception)
            {
                _status = $"Recipe preview error: {exception.Message}";
                ImGui.TextWrapped(_status);
            }
            finally
            {
                ImGui.EndChild();
            }
        }

        private void DrawRecipeInspector(NVector2 size)
        {
            ImGui.BeginChild("##recipe-inspector", size, true, ImGuiWindowFlags.HorizontalScrollbar);
            try
            {
                RecipeEntry? entry = SelectedEntry;
                if (entry == null)
                {
                    ImGui.TextDisabled("Select a recipe to inspect it.");
                    return;
                }

                ImGui.SeparatorText("Export");
                ImGui.TextDisabled(entry.Document.Dirty ? "Dirty" : "Clean");
                if (ImGui.Button("Export selected##recipe-export"))
                {
                    _status = Export(entry.Document);
                }
                ImGui.SameLine();
                if (ImGui.Button("Mark clean##recipe-clean"))
                {
                    entry.Document.MarkClean();
                    RebuildVisibleEntries();
                }
                if (ImGui.Button("Copy JSON##recipe-copy"))
                {
                    ImGui.SetClipboardText(SerializeToken(entry.Recipe));
                    _status = "Copied selected recipe JSON.";
                }

                DrawRecipeLiveControls(entry);

                ImGui.SeparatorText("Common");
                bool changed = false;
                changed |= EditString(entry.Recipe, "name", "Name##recipe-common-name", 240);
                changed |= EditString(entry.Recipe, "code", "Code##recipe-common-code", 240);
                changed |= EditBool(entry.Recipe, "enabled", "Enabled##recipe-common-enabled", defaultValue: true);
                changed |= EditStack(entry.Recipe, "ingredient", "Ingredient##recipe-common-ingredient");
                changed |= EditStack(entry.Recipe, "output", "Output##recipe-common-output");
                changed |= DrawArrayStackEditor(entry.Recipe, "ingredients", "Ingredients##recipe-common-ingredients");
                changed |= DrawArrayStackEditor(entry.Recipe, "outputs", "Outputs##recipe-common-outputs");
                if (changed) MarkChanged(entry);

                ImGui.SeparatorText("Raw JSON");
                if (_rawBufferKey != entry.Key) SyncRawBuffer(entry);
                ImGui.InputTextMultiline("##recipe-raw-json", ref _rawBuffer, 256 * 1024, new NVector2(-1, Math.Max(180f, ImGui.GetContentRegionAvail().Y - 72f)), ImGuiInputTextFlags.AllowTabInput);
                if (ImGui.Button("Apply raw JSON##recipe-raw-apply"))
                {
                    ApplyRawJson(entry);
                }

                if (!string.IsNullOrWhiteSpace(_status))
                {
                    ImGui.SeparatorText("Status");
                    ImGui.TextWrapped(_status);
                }
            }
            catch (Exception exception)
            {
                _status = $"Recipe inspector error: {exception.Message}";
                ImGui.TextWrapped(_status);
            }
            finally
            {
                ImGui.EndChild();
            }
        }

        private void DrawGridRecipeVisual(RecipeEntry entry)
        {
            JObject recipe = entry.Recipe;
            List<string> rows = GetGridRows(recipe, out int width, out int height);
            bool changed = false;

            ImGui.SeparatorText("Crafting grid");
            int editedWidth = width;
            int editedHeight = height;
            ImGui.SetNextItemWidth(90);
            if (ImGui.DragInt("Width##grid-width", ref editedWidth, 0.05f, 1, 3))
            {
                rows = ResizeRows(rows, Math.Clamp(editedWidth, 1, 3), height);
                width = Math.Clamp(editedWidth, 1, 3);
                changed = true;
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            if (ImGui.DragInt("Height##grid-height", ref editedHeight, 0.05f, 1, 3))
            {
                rows = ResizeRows(rows, width, Math.Clamp(editedHeight, 1, 3));
                height = Math.Clamp(editedHeight, 1, 3);
                changed = true;
            }

            JObject ingredients = GetOrCreateObject(recipe, "ingredients");
            List<char> symbols = GetIngredientSymbols(ingredients);
            if (symbols.Count == 0)
            {
                symbols.Add('A');
            }

            float cell = 58f;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    char current = GetCell(rows, x, y);
                    uint color = current == '_' ? SlotColor(0.16f, 0.14f, 0.11f) : SlotColor(0.36f, 0.25f, 0.13f);
                    ImGui.PushStyleColor(ImGuiCol.Button, color);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, SlotColor(0.50f, 0.36f, 0.18f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, SlotColor(0.64f, 0.46f, 0.22f));
                    string label = current == '_' ? " " : current.ToString();
                    if (ImGui.Button($"{label}##grid-cell-{x}-{y}", new NVector2(cell, cell)))
                    {
                        SetCell(rows, x, y, NextGridSymbol(current, symbols));
                        changed = true;
                    }
                    ImGui.PopStyleColor(3);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Click to cycle this slot through blank and ingredient symbols.");
                    }
                    if (x < width - 1) ImGui.SameLine();
                }
            }

            ImGui.SameLine();
            ImGui.Text("=>");
            ImGui.SameLine();
            ImGui.BeginGroup();
            DrawOutputSlot(recipe);
            ImGui.EndGroup();

            ImGui.SeparatorText("Ingredients");
            if (DrawIngredientMapEditor(recipe, ingredients, rows))
            {
                symbols = GetIngredientSymbols(ingredients);
                changed = true;
            }

            if (changed)
            {
                recipe["width"] = width;
                recipe["height"] = height;
                recipe["ingredientPattern"] = string.Join(",", rows);
                MarkChanged(entry);
            }
        }

        private void DrawPatternRecipeVisual(RecipeEntry entry)
        {
            JObject recipe = entry.Recipe;
            List<List<string>> layers = GetPatternLayers(recipe);
            if (layers.Count == 0)
            {
                layers.Add(["___", "___", "___"]);
            }
            _patternLayer = Math.Clamp(_patternLayer, 0, layers.Count - 1);

            ImGui.SeparatorText($"{entry.KindLabel} pattern");
            ImGui.TextDisabled("Left click toggles filled cells. Right click erases. Layers export as vanilla pattern arrays.");

            if (ImGui.Button("Add layer##pattern-layer-add"))
            {
                layers.Insert(_patternLayer + 1, CloneLayer(layers[_patternLayer]));
                _patternLayer++;
                SetPatternLayers(recipe, layers);
                MarkChanged(entry);
            }
            ImGui.SameLine();
            if (ImGui.Button("Duplicate layer##pattern-layer-dup"))
            {
                layers.Insert(_patternLayer + 1, CloneLayer(layers[_patternLayer]));
                _patternLayer++;
                SetPatternLayers(recipe, layers);
                MarkChanged(entry);
            }
            ImGui.SameLine();
            if (layers.Count <= 1) ImGui.BeginDisabled();
            if (ImGui.Button("Delete layer##pattern-layer-delete"))
            {
                layers.RemoveAt(_patternLayer);
                _patternLayer = Math.Clamp(_patternLayer, 0, layers.Count - 1);
                SetPatternLayers(recipe, layers);
                MarkChanged(entry);
            }
            if (layers.Count <= 1) ImGui.EndDisabled();

            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("Layer##pattern-layer", ref _patternLayer, 0, layers.Count - 1))
            {
                _patternLayer = Math.Clamp(_patternLayer, 0, layers.Count - 1);
            }

            List<string> layer = layers[_patternLayer];
            int rows = Math.Max(1, layer.Count);
            int cols = Math.Max(1, layer.Max(row => row.Length));
            int editedRows = rows;
            int editedCols = cols;
            ImGui.SetNextItemWidth(90);
            if (ImGui.DragInt("Rows##pattern-rows", ref editedRows, 0.05f, 1, 32))
            {
                layers[_patternLayer] = ResizeLayer(layer, cols, Math.Clamp(editedRows, 1, 32));
                SetPatternLayers(recipe, layers);
                MarkChanged(entry);
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            if (ImGui.DragInt("Columns##pattern-cols", ref editedCols, 0.05f, 1, 32))
            {
                layers[_patternLayer] = ResizeLayer(layer, Math.Clamp(editedCols, 1, 32), rows);
                SetPatternLayers(recipe, layers);
                MarkChanged(entry);
            }

            if (ImGui.Button("Mirror X##pattern-mirror-x"))
            {
                layers[_patternLayer] = layer.Select(row => new string(row.Reverse().ToArray())).ToList();
                SetPatternLayers(recipe, layers);
                MarkChanged(entry);
            }
            ImGui.SameLine();
            if (ImGui.Button("Mirror Y##pattern-mirror-y"))
            {
                layers[_patternLayer] = layer.AsEnumerable().Reverse().ToList();
                SetPatternLayers(recipe, layers);
                MarkChanged(entry);
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear##pattern-clear"))
            {
                layers[_patternLayer] = layer.Select(row => new string('_', Math.Max(1, row.Length))).ToList();
                SetPatternLayers(recipe, layers);
                MarkChanged(entry);
            }
            ImGui.SameLine();
            if (ImGui.Button("Fill##pattern-fill"))
            {
                layers[_patternLayer] = layer.Select(row => new string('#', Math.Max(1, row.Length))).ToList();
                SetPatternLayers(recipe, layers);
                MarkChanged(entry);
            }

            layer = layers[_patternLayer];
            rows = layer.Count;
            cols = Math.Max(1, layer.Max(row => row.Length));
            float cell = entry.Kind == RecipeEditorKind.Clayforming ? 22f : 28f;
            ImGui.BeginChild("##pattern-canvas", new NVector2(0, Math.Min(520f, ImGui.GetContentRegionAvail().Y * 0.58f)), true, ImGuiWindowFlags.HorizontalScrollbar);
            bool patternChanged = false;
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    char current = GetCell(layer, x, y);
                    bool filled = current != '_' && current != ' ';
                    ImGui.PushStyleColor(ImGuiCol.Button, filled ? SlotColor(0.48f, 0.31f, 0.13f) : SlotColor(0.12f, 0.12f, 0.10f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, filled ? SlotColor(0.62f, 0.42f, 0.18f) : SlotColor(0.24f, 0.21f, 0.16f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, SlotColor(0.78f, 0.54f, 0.24f));
                    if (ImGui.Button($"{(filled ? "#" : " ")}##pattern-cell-{x}-{y}", new NVector2(cell, cell)))
                    {
                        SetCell(layer, x, y, filled ? '_' : '#');
                        patternChanged = true;
                    }
                    if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    {
                        SetCell(layer, x, y, '_');
                        patternChanged = true;
                    }
                    ImGui.PopStyleColor(3);
                    if (x < cols - 1) ImGui.SameLine();
                }
            }
            ImGui.EndChild();

            if (patternChanged)
            {
                SetPatternLayers(recipe, layers);
                MarkChanged(entry);
            }

            bool changed = false;
            changed |= EditStack(recipe, "ingredient", "Ingredient##pattern-ingredient");
            changed |= EditStack(recipe, "output", "Output##pattern-output");
            if (changed) MarkChanged(entry);
        }

        private void DrawRecipeFlowVisual(RecipeEntry entry)
        {
            JObject recipe = entry.Recipe;
            ImGui.SeparatorText($"{entry.KindLabel} flow");
            ImGui.TextDisabled("Use the structured slots for common fields; preserve special mod fields in Raw JSON.");

            ImGui.BeginChild("##recipe-flow", new NVector2(0, 180f), true);
            NVector2 start = ImGui.GetCursorScreenPos();
            ImDrawListPtr drawList = ImGui.GetWindowDrawList();
            float y = start.Y + 48f;
            DrawFlowBox(drawList, start.X + 20f, y, 150f, 62f, "Input", DescribeInput(recipe));
            DrawFlowArrow(drawList, start.X + 190f, y + 31f, start.X + 260f, y + 31f);
            DrawFlowBox(drawList, start.X + 270f, y, 150f, 62f, entry.KindLabel, DescribeProcess(recipe));
            DrawFlowArrow(drawList, start.X + 440f, y + 31f, start.X + 510f, y + 31f);
            DrawFlowBox(drawList, start.X + 520f, y, 170f, 62f, "Output", DescribeOutput(recipe));
            ImGui.Dummy(new NVector2(720f, 150f));
            ImGui.EndChild();

            bool changed = false;
            changed |= EditStack(recipe, "ingredient", "Ingredient##flow-ingredient");
            changed |= DrawArrayStackEditor(recipe, "ingredients", "Ingredients##flow-ingredients");
            changed |= EditStack(recipe, "input", "Input##flow-input");
            changed |= DrawArrayStackEditor(recipe, "inputs", "Inputs##flow-inputs");
            changed |= EditStack(recipe, "output", "Output##flow-output");
            changed |= DrawArrayStackEditor(recipe, "outputs", "Outputs##flow-outputs");
            changed |= EditNumber(recipe, "litres", "Litres##flow-litres", 0, 10000);
            changed |= EditNumber(recipe, "duration", "Duration##flow-duration", 0, 100000);
            changed |= EditNumber(recipe, "temperature", "Temperature##flow-temperature", 0, 5000);
            if (changed) MarkChanged(entry);
        }

        private bool DrawIngredientMapEditor(JObject recipe, JObject ingredients, List<string> rows)
        {
            bool changed = false;
            foreach (JProperty property in ingredients.Properties().ToList())
            {
                if (property.Value is not JObject ingredient)
                {
                    ingredient = new JObject();
                    property.Value = ingredient;
                }

                ImGui.PushID($"ingredient-{property.Name}");
                ImGui.Separator();
                string symbol = property.Name;
                ImGui.SetNextItemWidth(38);
                if (ImGui.InputText("##symbol", ref symbol, 2))
                {
                    char newSymbol = NormalizeSymbol(symbol);
                    if (newSymbol != '_' && newSymbol.ToString() != property.Name && !ingredients.ContainsKey(newSymbol.ToString()))
                    {
                        ReplaceGridSymbol(rows, property.Name[0], newSymbol);
                        ingredients.Remove(property.Name);
                        ingredients[newSymbol.ToString()] = ingredient;
                        changed = true;
                    }
                }
                ImGui.SameLine();
                changed |= EditStackFields(ingredient, $"Ingredient {property.Name}");
                ImGui.SameLine();
                if (ImGui.Button("Remove"))
                {
                    ReplaceGridSymbol(rows, property.Name[0], '_');
                    ingredients.Remove(property.Name);
                    changed = true;
                }
                ImGui.PopID();
            }

            if (ImGui.Button("Add ingredient##grid-add-ingredient"))
            {
                char symbol = NextAvailableSymbol(ingredients);
                ingredients[symbol.ToString()] = new JObject
                {
                    ["type"] = "item",
                    ["code"] = "game:stick"
                };
                changed = true;
            }

            return changed;
        }

        private static void DrawOutputSlot(JObject recipe)
        {
            JObject output = GetOrCreateObject(recipe, "output");
            string title = output["code"]?.ToString() ?? "output";
            ImGui.PushStyleColor(ImGuiCol.Button, SlotColor(0.22f, 0.28f, 0.18f));
            ImGui.Button($"{TrimMiddle(title, 18)}##grid-output-preview", new NVector2(118f, 58f));
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(title);
        }

        private void BuildStackCodeLists(ICoreClientAPI api)
        {
            _itemCodes = api.World.Items
                .Where(item => item?.Code != null)
                .Select(item => item.Code.ToString())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _blockCodes = api.World.Blocks
                .Where(block => block?.Code != null)
                .Select(block => block.Code.ToString())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _liquidCodes = _blockCodes
                .Where(code => code.Contains("water", StringComparison.OrdinalIgnoreCase) ||
                    code.Contains("lava", StringComparison.OrdinalIgnoreCase) ||
                    code.Contains("liquid", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (_liquidCodes.Length == 0)
            {
                _liquidCodes = _blockCodes;
            }
        }

        private bool EditStack(JObject parent, string property, string label)
        {
            if (parent[property] == null)
            {
                if (!ImGui.Button($"Add {label}")) return false;
                parent[property] = new JObject
                {
                    ["type"] = "item",
                    ["code"] = "game:stick"
                };
                return true;
            }

            if (parent[property] is not JObject stack)
            {
                ImGui.TextDisabled($"{label}: not an object");
                return false;
            }

            ImGui.SeparatorText(label);
            bool changed = EditStackFields(stack, label);
            ImGui.SameLine();
            if (ImGui.Button($"Remove##remove-{label}"))
            {
                parent.Remove(property);
                return true;
            }
            return changed;
        }

        private bool EditStackFields(JObject stack, string label)
        {
            bool changed = false;
            string[] types = ["item", "block", "liquid"];
            string type = stack["type"]?.ToString() ?? "item";
            int typeIndex = Math.Max(0, Array.FindIndex(types, value => string.Equals(value, type, StringComparison.OrdinalIgnoreCase)));
            ImGui.SetNextItemWidth(72);
            if (ImGui.Combo($"Type##{label}", ref typeIndex, types, types.Length))
            {
                stack["type"] = types[typeIndex];
                changed = true;
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(260);
            if (DrawStackCodeCombo(stack, label, types[typeIndex]))
            {
                changed = true;
            }
            string quantityProperty = stack["stacksize"] != null ? "stacksize" : "quantity";
            int quantity = stack[quantityProperty]?.Value<int?>() ?? 1;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70);
            if (ImGui.DragInt($"Qty##{label}", ref quantity, 0.05f, 1, 9999))
            {
                stack[quantityProperty] = Math.Max(1, quantity);
                changed = true;
            }
            return changed;
        }

        private bool DrawStackCodeCombo(JObject stack, string label, string type)
        {
            string current = stack["code"]?.ToString() ?? "";
            string filterKey = $"{label}:{type}";
            if (!_stackCodeFilters.TryGetValue(filterKey, out string? filter))
            {
                filter = "";
            }

            string[] options = type.Equals("block", StringComparison.OrdinalIgnoreCase) ? _blockCodes :
                type.Equals("liquid", StringComparison.OrdinalIgnoreCase) ? _liquidCodes :
                _itemCodes;

            string preview = string.IsNullOrWhiteSpace(current) ? "(select code)" : TrimMiddle(current, 34);
            bool changed = false;
            if (ImGui.BeginCombo($"Code##{label}", preview))
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint($"##code-filter-{label}", "filter or type code", ref filter, 240))
                {
                    _stackCodeFilters[filterKey] = filter;
                }

                string trimmedFilter = filter.Trim();
                if (!string.IsNullOrWhiteSpace(trimmedFilter))
                {
                    if (ImGui.Button($"Use \"{TrimMiddle(trimmedFilter, 30)}\"##use-code-{label}"))
                    {
                        stack["code"] = trimmedFilter;
                        _stackCodeFilters[filterKey] = "";
                        changed = true;
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.SameLine();
                }

                if (ImGui.Button($"Clear##clear-code-{label}"))
                {
                    stack.Remove("code");
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.Separator();
                IEnumerable<string> visibleOptions = string.IsNullOrWhiteSpace(trimmedFilter)
                    ? options
                    : options.Where(code => code.Contains(trimmedFilter, StringComparison.OrdinalIgnoreCase));

                int shown = 0;
                foreach (string option in visibleOptions.Take(250))
                {
                    bool selected = string.Equals(option, current, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable($"{option}##stack-code-{label}-{shown}", selected))
                    {
                        stack["code"] = option;
                        _stackCodeFilters[filterKey] = "";
                        changed = true;
                        ImGui.CloseCurrentPopup();
                    }
                    shown++;
                }

                if (shown == 0)
                {
                    ImGui.TextDisabled("No loaded codes match the filter.");
                }
                else if (options.Length > shown && !string.IsNullOrWhiteSpace(trimmedFilter))
                {
                    ImGui.TextDisabled("Showing first 250 matches.");
                }

                ImGui.EndCombo();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(string.IsNullOrWhiteSpace(current) ? "Select a loaded code or type a wildcard/custom code in the filter." : current);
            }

            return changed;
        }

        private bool DrawArrayStackEditor(JObject recipe, string property, string label)
        {
            if (recipe[property] == null)
            {
                if (!ImGui.Button($"Add {label}")) return false;
                recipe[property] = new JArray(new JObject
                {
                    ["type"] = "item",
                    ["code"] = "game:stick"
                });
                return true;
            }

            if (recipe[property] is not JArray array) return false;
            bool changed = false;
            ImGui.SeparatorText(label);
            for (int index = 0; index < array.Count; index++)
            {
                if (array[index] is not JObject stack) continue;
                ImGui.PushID($"{property}-{index}");
                changed |= EditStackFields(stack, $"{label}-{index}");
                ImGui.SameLine();
                if (ImGui.Button("Remove"))
                {
                    array.RemoveAt(index);
                    changed = true;
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }
            if (ImGui.Button($"Add row##{property}"))
            {
                array.Add(new JObject
                {
                    ["type"] = "item",
                    ["code"] = "game:stick"
                });
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.Button($"Remove field##{property}"))
            {
                recipe.Remove(property);
                changed = true;
            }
            return changed;
        }

        private void TryCreateDraft()
        {
            try
            {
                CreateDraft();
            }
            catch (Exception exception)
            {
                _status = $"Could not create draft: {exception.Message}";
            }
        }

        private void CreateDraft()
        {
            RecipeEditorKind kind = (RecipeEditorKind)Math.Clamp(_newKindIndex, 0, KindFilterLabels.Length - 2);
            string domain = string.IsNullOrWhiteSpace(_newDomain) ? "game" : SanitizePathPart(_newDomain.Trim());
            string name = string.IsNullOrWhiteSpace(_newName) ? "new-recipe" : SanitizePathPart(_newName.Trim());
            string assetPath = $"recipes/{KindSegment(kind)}/{name}.json";
            JObject recipe = CreateDefaultRecipe(kind, name);
            RecipeDocument document = new(domain, assetPath, recipe, true);
            document.MarkDirty();
            AddDocument(document);
            _filter = "";
            _kindFilter = (int)kind + 1;
            RebuildVisibleEntries();
            int draftIndex = _visibleEntries.FindIndex(entry => entry.Document == document);
            if (draftIndex >= 0)
            {
                _selectedIndex = draftIndex;
                _patternLayer = 0;
                SyncRawBuffer(_visibleEntries[draftIndex]);
            }
            else
            {
                _selectedIndex = 0;
                SyncRawBuffer(SelectedEntry);
            }
            _status = $"Created draft {document.DisplayPath}.";
        }

        private void ExportDirty()
        {
            int exported = 0;
            foreach (RecipeDocument document in _documents.Where(document => document.Dirty).ToList())
            {
                string result = Export(document);
                if (result.StartsWith("Exported", StringComparison.OrdinalIgnoreCase)) exported++;
                _status = result;
            }
            if (exported == 0 && string.IsNullOrWhiteSpace(_status))
            {
                _status = "No dirty recipe documents to export.";
            }
            RebuildVisibleEntries();
        }

        private string Export(RecipeDocument document)
        {
            try
            {
                string relativePath = Path.Combine("assets", document.Domain, document.AssetPath.Replace('/', Path.DirectorySeparatorChar));
                string outputPath = GetToolAuthoredAssetPath("recipes", relativePath);

                if (File.Exists(outputPath) && !_overwriteExport)
                {
                    return $"Export exists: {outputPath}. Enable overwrite exports to replace it.";
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, SerializeToken(document.Root));
                JObject manifest = new()
                {
                    ["exportedAtUtc"] = DateTime.UtcNow.ToString("O"),
                    ["source"] = document.DisplayPath,
                    ["kind"] = "Recipe",
                    ["export"] = outputPath
                };
                File.WriteAllText(outputPath + ".ingamedevtools-manifest.json", manifest.ToString(Formatting.Indented));
                document.MarkClean();
                RebuildVisibleEntries();
                return $"Exported {document.DisplayPath} to {outputPath}.";
            }
            catch (Exception exception)
            {
                return $"Export failed for {document.DisplayPath}: {exception.Message}";
            }
        }

        private void ApplyRawJson(RecipeEntry entry)
        {
            try
            {
                JToken token = ParseRecipeJson(_rawBuffer);
                if (token is not JObject replacement)
                {
                    _status = "Raw JSON must be a single recipe object.";
                    return;
                }

                entry.ReplaceRecipe(replacement);
                entry.Document.MarkDirty();
                if (_liveApplyManager?.AutoApply == true)
                {
                    _status = ApplyRecipeLive(entry);
                }
                RebuildVisibleEntries();
                SyncRawBuffer(entry);
                if (_liveApplyManager?.AutoApply != true)
                {
                    _status = "Applied raw recipe JSON.";
                }
            }
            catch (Exception exception)
            {
                _status = $"Raw JSON parse failed: {exception.Message}";
            }
        }

        private void DrawRecipeLiveControls(RecipeEntry entry)
        {
            if (_liveApplyManager == null || _api == null) return;

            bool available = TryResolveRecipeLiveTarget(entry, out RecipeLiveListTarget? target, out string unavailableReason);
            string liveKey = target?.LiveKey ?? $"recipe:{entry.Kind}:{entry.Key}";

            _liveApplyManager.DrawRuntimeStatus(
                $"recipe-live-{entry.Key}",
                liveKey,
                entry.ShortLabel,
                available,
                () =>
                {
                    _recipeLiveAppliedHashes.Remove(liveKey);
                    return _liveApplyManager.Revert(liveKey);
                });

            if (!available && !string.IsNullOrWhiteSpace(unavailableReason))
            {
                ImGui.TextWrapped(unavailableReason);
            }
            else if (entry.Kind != RecipeEditorKind.Grid)
            {
                ImGui.TextWrapped("Recipe live apply is best-effort; some crafting caches may still require a reload.");
            }
        }

        private string ApplyRecipeLive(RecipeEntry entry, bool force = false)
        {
            if (_liveApplyManager == null || _api == null)
            {
                return "Live apply is not available.";
            }

            if (!TryResolveRecipeLiveTarget(entry, out RecipeLiveListTarget? target, out string unavailableReason) || target == null)
            {
                _liveApplyManager.LastStatus = $"Recipe live target unavailable: {unavailableReason}";
                return _liveApplyManager.LastStatus;
            }

            string hash = SerializeToken(entry.Recipe);
            if (!force &&
                _recipeLiveAppliedHashes.TryGetValue(target.LiveKey, out string? appliedHash) &&
                string.Equals(appliedHash, hash, StringComparison.Ordinal))
            {
                return _liveApplyManager.LastStatus;
            }

            string status = _liveApplyManager.Apply(
                target.LiveKey,
                target.Label,
                () => CaptureRecipeLiveSnapshot(target, entry),
                () => ApplyRecipeToTarget(target, entry),
                BuildRecipeLiveStatus(entry));
            _recipeLiveAppliedHashes[target.LiveKey] = hash;
            return status;
        }

        private bool TryResolveRecipeLiveTarget(RecipeEntry entry, out RecipeLiveListTarget? target, out string reason)
        {
            target = null;
            reason = "";
            if (_api == null)
            {
                reason = "No client API is available.";
                return false;
            }

            if (entry.Kind == RecipeEditorKind.Other)
            {
                reason = "Only known recipe registries can be live patched.";
                return false;
            }

            if (entry.Kind == RecipeEditorKind.Grid)
            {
                if (TryCreateRecipeListTarget(_api.World, "GridRecipes", "grid", out target, out reason))
                {
                    return true;
                }

                return TryFindRecipeListTarget(_api.World, "grid", out target, out reason);
            }

            string registryCode = KindSegment(entry.Kind);
            object? registry = _api.World.GetRecipeRegistry(registryCode);
            if (registry == null)
            {
                reason = $"The '{registryCode}' recipe registry is not loaded.";
                return false;
            }

            return TryFindRecipeListTarget(registry, registryCode, out target, out reason);
        }

        private DebugWindowManager.LivePatchSnapshot CaptureRecipeLiveSnapshot(RecipeLiveListTarget target, RecipeEntry entry)
        {
            object[] original = target.ReadItems();
            string backupPath = Path.Combine("assets", entry.Document.Domain, entry.Document.AssetPath.Replace('/', Path.DirectorySeparatorChar));
            return new(
                () => target.ReplaceAll(original),
                backupPath,
                () => SerializeToken(entry.Document.Root),
                "recipes");
        }

        private void ApplyRecipeToTarget(RecipeLiveListTarget target, RecipeEntry entry)
        {
            if (_api == null) throw new InvalidOperationException("No client API is available.");

            object edited = entry.Recipe.ToObject(target.ElementType, JsonSerializer.Create(JsonSettings))
                ?? throw new InvalidOperationException($"Could not convert recipe JSON to {target.ElementType.Name}.");
            ResolveRecipeObject(edited, entry, _api);

            object[] current = target.ReadItems();
            List<object> updated = current.ToList();
            int replaceIndex = FindRuntimeRecipeIndex(updated, entry, edited);
            if (replaceIndex >= 0)
            {
                updated[replaceIndex] = edited;
            }
            else
            {
                updated.Add(edited);
            }

            target.ReplaceAll(updated);
        }

        private string BuildRecipeLiveStatus(RecipeEntry entry)
        {
            return entry.Kind == RecipeEditorKind.Grid
                ? $"Live applied {entry.ShortLabel}."
                : $"Live applied {entry.ShortLabel}. Registry patched; cache may require reload.";
        }

        private static void ResolveRecipeObject(object recipe, RecipeEntry entry, ICoreClientAPI api)
        {
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            foreach (System.Reflection.MethodInfo method in recipe.GetType().GetMethods(flags).Where(method => method.Name == "Resolve"))
            {
                System.Reflection.ParameterInfo[] parameters = method.GetParameters();
                try
                {
                    if (parameters.Length == 2 &&
                        parameters[0].ParameterType.IsAssignableFrom(api.World.GetType()) &&
                        parameters[1].ParameterType == typeof(string))
                    {
                        method.Invoke(recipe, [api.World, entry.Document.DisplayPath]);
                        return;
                    }

                    if (parameters.Length == 2 &&
                        parameters[0].ParameterType == typeof(IWorldAccessor) &&
                        parameters[1].ParameterType == typeof(string))
                    {
                        method.Invoke(recipe, [api.World, entry.Document.DisplayPath]);
                        return;
                    }
                }
                catch
                {
                    return;
                }
            }
        }

        private static int FindRuntimeRecipeIndex(List<object> current, RecipeEntry entry, object edited)
        {
            string? desiredCode = GetRecipeIdentity(entry.Recipe, "code") ?? GetObjectIdentity(edited, "Code");
            string? desiredName = GetRecipeIdentity(entry.Recipe, "name") ?? GetObjectIdentity(edited, "Name");

            if (!string.IsNullOrWhiteSpace(desiredCode))
            {
                int index = current.FindIndex(recipe => string.Equals(GetObjectIdentity(recipe, "Code"), desiredCode, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) return index;
            }

            if (!string.IsNullOrWhiteSpace(desiredName))
            {
                int index = current.FindIndex(recipe => string.Equals(GetObjectIdentity(recipe, "Name"), desiredName, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) return index;
            }

            return -1;
        }

        private static string? GetRecipeIdentity(JObject recipe, string field)
        {
            return recipe[field]?.ToString();
        }

        private static string? GetObjectIdentity(object instance, string memberName)
        {
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.IgnoreCase;
            System.Reflection.MemberInfo? member = instance.GetType().GetMember(memberName, flags).FirstOrDefault();
            object? value = member switch
            {
                System.Reflection.PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(instance),
                System.Reflection.FieldInfo field => field.GetValue(instance),
                _ => null
            };
            return value?.ToString();
        }

        private static bool TryCreateRecipeListTarget(object owner, string memberName, string registryCode, out RecipeLiveListTarget? target, out string reason)
        {
            target = null;
            reason = "";
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            System.Reflection.MemberInfo? member = owner.GetType().GetMember(memberName, flags).FirstOrDefault();
            if (member == null)
            {
                reason = $"Could not find recipe list '{memberName}'.";
                return false;
            }

            return TryCreateRecipeListTarget(owner, member, registryCode, out target, out reason);
        }

        private static bool TryFindRecipeListTarget(object registry, string registryCode, out RecipeLiveListTarget? target, out string reason)
        {
            target = null;
            reason = "";
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;

            foreach (System.Reflection.MemberInfo member in registry.GetType().GetMembers(flags))
            {
                if (member.MemberType is not (System.Reflection.MemberTypes.Field or System.Reflection.MemberTypes.Property)) continue;
                if (!TryCreateRecipeListTarget(registry, member, registryCode, out target, out _)) continue;
                return true;
            }

            reason = $"Could not find a mutable recipe list in '{registryCode}'.";
            return false;
        }

        private static bool TryCreateRecipeListTarget(object owner, System.Reflection.MemberInfo member, string registryCode, out RecipeLiveListTarget? target, out string reason)
        {
            target = null;
            reason = "";
            Type? memberType = member switch
            {
                System.Reflection.FieldInfo field => field.FieldType,
                System.Reflection.PropertyInfo property when property.GetIndexParameters().Length == 0 => property.PropertyType,
                _ => null
            };
            if (memberType == null)
            {
                reason = "Unsupported recipe list member.";
                return false;
            }

            object? value = GetMemberValue(owner, member);
            Type? elementType = GetListElementType(memberType, value);
            if (elementType == null || !elementType.Name.Contains("Recipe", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Member is not a recipe list.";
                return false;
            }

            bool isArray = memberType.IsArray || value?.GetType().IsArray == true;
            bool mutableList = value is System.Collections.IList { IsReadOnly: false };
            bool canAssign = member is System.Reflection.FieldInfo || member is System.Reflection.PropertyInfo { CanWrite: true };
            if (!mutableList && !(isArray && canAssign))
            {
                reason = "Recipe list is not mutable.";
                return false;
            }

            target = new(owner, member, registryCode, elementType, isArray);
            return true;
        }

        private static object? GetMemberValue(object owner, System.Reflection.MemberInfo member)
        {
            return member switch
            {
                System.Reflection.FieldInfo field => field.GetValue(owner),
                System.Reflection.PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(owner),
                _ => null
            };
        }

        private static void SetMemberValue(object owner, System.Reflection.MemberInfo member, object value)
        {
            switch (member)
            {
                case System.Reflection.FieldInfo field:
                    field.SetValue(owner, value);
                    break;
                case System.Reflection.PropertyInfo { CanWrite: true } property:
                    property.SetValue(owner, value);
                    break;
            }
        }

        private static Type? GetListElementType(Type memberType, object? value)
        {
            if (memberType.IsArray) return memberType.GetElementType();
            Type runtimeType = value?.GetType() ?? memberType;
            if (runtimeType.IsArray) return runtimeType.GetElementType();
            return runtimeType.GetInterfaces()
                .Concat([runtimeType])
                .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IList<>))
                .Select(type => type.GetGenericArguments()[0])
                .FirstOrDefault();
        }

        private void MarkChanged(RecipeEntry entry)
        {
            entry.Document.MarkDirty();
            if (_rawBufferKey == entry.Key)
            {
                _rawBuffer = SerializeToken(entry.Recipe);
            }
            if (_liveApplyManager?.AutoApply == true)
            {
                _status = ApplyRecipeLive(entry);
            }
            RebuildVisibleEntries();
        }

        private void RebuildVisibleEntries()
        {
            RecipeEntry? selected = SelectedEntry;
            _visibleEntries.Clear();
            foreach (RecipeEntry entry in _entries)
            {
                if (!ImGuiLayoutHelper.MatchesDomain(_domainFilter, entry.Document.Domain)) continue;
                if (_showDirtyOnly && !entry.Document.Dirty) continue;
                if (_kindFilter > 0 && entry.Kind != (RecipeEditorKind)(_kindFilter - 1)) continue;
                if (!string.IsNullOrWhiteSpace(_filter) &&
                    !entry.SearchText.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                _visibleEntries.Add(entry);
            }

            if (selected != null)
            {
                int selectedIndex = _visibleEntries.FindIndex(entry => entry.Key == selected.Key);
                if (selectedIndex >= 0)
                {
                    _selectedIndex = selectedIndex;
                    return;
                }
            }

            _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, _visibleEntries.Count - 1));
        }

        private RecipeEntry? SelectedEntry => _visibleEntries.Count == 0 ? null : _visibleEntries[Math.Clamp(_selectedIndex, 0, _visibleEntries.Count - 1)];

        private void SyncRawBuffer(RecipeEntry? entry)
        {
            if (entry == null)
            {
                _rawBuffer = "";
                _rawBufferKey = "";
                return;
            }

            _rawBufferKey = entry.Key;
            _rawBuffer = SerializeToken(entry.Recipe);
        }

        private static JToken ParseRecipeJson(string text)
        {
            using StringReader stringReader = new(text);
            using JsonTextReader reader = new(stringReader)
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double
            };
            return JToken.ReadFrom(reader);
        }

        private static string SerializeToken(JToken token)
        {
            return JsonConvert.SerializeObject(token, Formatting.Indented, JsonSettings);
        }

        private static JObject CreateDefaultRecipe(RecipeEditorKind kind, string name)
        {
            return kind switch
            {
                RecipeEditorKind.Grid => new JObject
                {
                    ["ingredientPattern"] = "A__",
                    ["ingredients"] = new JObject
                    {
                        ["A"] = new JObject
                        {
                            ["type"] = "item",
                            ["code"] = "game:stick"
                        }
                    },
                    ["name"] = name,
                    ["width"] = 3,
                    ["height"] = 1,
                    ["output"] = DefaultStack("item", "game:stick")
                },
                RecipeEditorKind.Smithing => new JObject
                {
                    ["ingredient"] = DefaultStack("item", "game:ingot-*"),
                    ["pattern"] = new JArray(new JArray("_##_","_##_","____")),
                    ["name"] = name,
                    ["code"] = name,
                    ["output"] = DefaultStack("item", "game:pickaxehead-copper")
                },
                RecipeEditorKind.Clayforming => new JObject
                {
                    ["ingredient"] = DefaultStack("item", "game:clay-*"),
                    ["enabled"] = true,
                    ["pattern"] = new JArray(new JArray("____","_##_","_##_","____")),
                    ["name"] = name,
                    ["output"] = DefaultStack("block", "game:bowl-raw")
                },
                RecipeEditorKind.Knapping => new JObject
                {
                    ["ingredient"] = DefaultStack("item", "game:flint"),
                    ["pattern"] = new JArray(new JArray("____","_##_","_##_","____")),
                    ["name"] = name,
                    ["output"] = DefaultStack("item", "game:knifehead-flint")
                },
                RecipeEditorKind.Barrel => new JObject
                {
                    ["ingredients"] = new JArray(DefaultStack("item", "game:stick")),
                    ["outputs"] = new JArray(DefaultStack("item", "game:stick")),
                    ["duration"] = 24,
                    ["name"] = name
                },
                RecipeEditorKind.Cooking => new JObject
                {
                    ["ingredients"] = new JArray(DefaultStack("item", "game:vegetable-*")),
                    ["output"] = DefaultStack("item", "game:meal"),
                    ["name"] = name
                },
                RecipeEditorKind.Alloy => new JObject
                {
                    ["ingredients"] = new JArray(DefaultStack("item", "game:nugget-copper")),
                    ["output"] = DefaultStack("item", "game:ingot-copper"),
                    ["name"] = name
                },
                _ => new JObject
                {
                    ["name"] = name,
                    ["ingredient"] = DefaultStack("item", "game:stick"),
                    ["output"] = DefaultStack("item", "game:stick")
                }
            };
        }

        private static JObject DefaultStack(string type, string code)
        {
            return new JObject
            {
                ["type"] = type,
                ["code"] = code,
                ["quantity"] = 1
            };
        }

        private static List<string> GetGridRows(JObject recipe, out int width, out int height)
        {
            string pattern = recipe["ingredientPattern"]?.ToString() ?? recipe["pattern"]?.ToString() ?? "";
            List<string> rows = pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (rows.Count == 0) rows.Add("___");
            width = Math.Clamp(recipe["width"]?.Value<int?>() ?? rows.Max(row => row.Length), 1, 3);
            height = Math.Clamp(recipe["height"]?.Value<int?>() ?? rows.Count, 1, 3);
            return ResizeRows(rows, width, height);
        }

        private static List<string> ResizeRows(List<string> rows, int width, int height)
        {
            List<string> resized = [];
            for (int y = 0; y < height; y++)
            {
                string row = y < rows.Count ? rows[y] : "";
                if (row.Length < width) row = row.PadRight(width, '_');
                if (row.Length > width) row = row[..width];
                resized.Add(row);
            }
            return resized;
        }

        private static List<List<string>> GetPatternLayers(JObject recipe)
        {
            List<List<string>> layers = [];
            if (recipe["pattern"] is not JArray pattern) return layers;
            if (pattern.Count > 0 && pattern[0] is JArray)
            {
                foreach (JArray layer in pattern.OfType<JArray>())
                {
                    layers.Add(layer.Select(row => row?.ToString() ?? "").ToList());
                }
            }
            else
            {
                layers.Add(pattern.Select(row => row?.ToString() ?? "").ToList());
            }
            return layers;
        }

        private static void SetPatternLayers(JObject recipe, List<List<string>> layers)
        {
            JArray pattern = new();
            foreach (List<string> layer in layers)
            {
                pattern.Add(new JArray(layer));
            }
            recipe["pattern"] = pattern;
        }

        private static List<string> ResizeLayer(List<string> layer, int width, int height)
        {
            List<string> result = [];
            for (int y = 0; y < height; y++)
            {
                string row = y < layer.Count ? layer[y] : "";
                if (row.Length < width) row = row.PadRight(width, '_');
                if (row.Length > width) row = row[..width];
                result.Add(row);
            }
            return result;
        }

        private static List<string> CloneLayer(List<string> layer) => layer.ToList();

        private static char GetCell(List<string> rows, int x, int y)
        {
            if (y < 0 || y >= rows.Count || x < 0 || x >= rows[y].Length) return '_';
            char value = rows[y][x];
            return value == ' ' ? '_' : value;
        }

        private static void SetCell(List<string> rows, int x, int y, char value)
        {
            if (y < 0 || y >= rows.Count || x < 0 || x >= rows[y].Length) return;
            char[] chars = rows[y].ToCharArray();
            chars[x] = value == ' ' ? '_' : value;
            rows[y] = new string(chars);
        }

        private static List<char> GetIngredientSymbols(JObject ingredients)
        {
            return ingredients.Properties()
                .Select(property => NormalizeSymbol(property.Name))
                .Where(symbol => symbol != '_')
                .Distinct()
                .OrderBy(symbol => symbol)
                .ToList();
        }

        private static char NextGridSymbol(char current, List<char> ingredients)
        {
            List<char> symbols = ['_'];
            symbols.AddRange(ingredients);
            int index = symbols.IndexOf(current);
            if (index < 0) index = 0;
            return symbols[(index + 1) % symbols.Count];
        }

        private static char NormalizeSymbol(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? '_' : char.ToUpperInvariant(value.Trim()[0]);
        }

        private static void ReplaceGridSymbol(List<string> rows, char oldSymbol, char newSymbol)
        {
            for (int y = 0; y < rows.Count; y++)
            {
                rows[y] = new string(rows[y].Select(symbol => symbol == oldSymbol ? newSymbol : symbol).ToArray());
            }
        }

        private static char NextAvailableSymbol(JObject ingredients)
        {
            HashSet<char> used = GetIngredientSymbols(ingredients).ToHashSet();
            foreach (char symbol in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
            {
                if (!used.Contains(symbol)) return symbol;
            }
            return 'A';
        }

        private static JObject GetOrCreateObject(JObject parent, string property)
        {
            if (parent[property] is JObject existing) return existing;
            JObject created = new();
            parent[property] = created;
            return created;
        }

        private static bool EditString(JObject obj, string property, string label, int maxLength)
        {
            string value = obj[property]?.ToString() ?? "";
            if (!ImGui.InputText(label, ref value, (uint)maxLength)) return false;
            if (string.IsNullOrWhiteSpace(value)) obj.Remove(property);
            else obj[property] = value;
            return true;
        }

        private static bool EditBool(JObject obj, string property, string label, bool defaultValue)
        {
            bool value = obj[property]?.Value<bool?>() ?? defaultValue;
            if (!ImGui.Checkbox(label, ref value)) return false;
            obj[property] = value;
            return true;
        }

        private static bool EditNumber(JObject obj, string property, string label, int min, int max)
        {
            int value = obj[property]?.Value<int?>() ?? 0;
            ImGui.SetNextItemWidth(120);
            if (!ImGui.DragInt(label, ref value, 0.05f, min, max)) return false;
            obj[property] = Math.Clamp(value, min, max);
            return true;
        }

        private static void DrawFlowBox(ImDrawListPtr drawList, float x, float y, float width, float height, string title, string body)
        {
            uint fill = SlotColor(0.20f, 0.16f, 0.11f);
            uint border = SlotColor(0.58f, 0.45f, 0.27f);
            uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.92f, 0.86f, 0.74f, 1f));
            drawList.AddRectFilled(new NVector2(x, y), new NVector2(x + width, y + height), fill, 5f);
            drawList.AddRect(new NVector2(x, y), new NVector2(x + width, y + height), border, 5f, ImDrawFlags.None, 1.5f);
            drawList.AddText(new NVector2(x + 10f, y + 8f), text, title);
            drawList.AddText(new NVector2(x + 10f, y + 32f), text, TrimMiddle(body, 22));
        }

        private static void DrawFlowArrow(ImDrawListPtr drawList, float x1, float y1, float x2, float y2)
        {
            uint color = SlotColor(0.82f, 0.62f, 0.30f);
            drawList.AddLine(new NVector2(x1, y1), new NVector2(x2, y2), color, 2.5f);
            drawList.AddTriangleFilled(new NVector2(x2, y2), new NVector2(x2 - 10f, y2 - 6f), new NVector2(x2 - 10f, y2 + 6f), color);
        }

        private static string DescribeInput(JObject recipe)
        {
            return DescribeToken(recipe["ingredient"] ?? recipe["input"] ?? recipe["ingredients"] ?? recipe["inputs"]);
        }

        private static string DescribeOutput(JObject recipe)
        {
            return DescribeToken(recipe["output"] ?? recipe["outputs"]);
        }

        private static string DescribeProcess(JObject recipe)
        {
            string[] fields = ["duration", "litres", "temperature"];
            return string.Join(", ", fields.Where(field => recipe[field] != null).Select(field => $"{field}: {recipe[field]}"));
        }

        private static string DescribeToken(JToken? token)
        {
            if (token is JObject obj) return obj["code"]?.ToString() ?? obj["type"]?.ToString() ?? "object";
            if (token is JArray array) return $"{array.Count} entries";
            return "not set";
        }

        private static uint SlotColor(float r, float g, float b)
        {
            return ImGui.ColorConvertFloat4ToU32(new NVector4(r, g, b, 1f));
        }

        private static string TrimMiddle(string value, int maxLength)
        {
            if (value.Length <= maxLength) return value;
            int keep = Math.Max(2, (maxLength - 3) / 2);
            return value[..keep] + "..." + value[^keep..];
        }

        private static string EnsureJsonPath(string path)
        {
            path = path.Replace('\\', '/');
            return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : path + ".json";
        }

        private static string KindSegment(RecipeEditorKind kind)
        {
            return kind switch
            {
                RecipeEditorKind.Grid => "grid",
                RecipeEditorKind.Smithing => "smithing",
                RecipeEditorKind.Clayforming => "clayforming",
                RecipeEditorKind.Knapping => "knapping",
                RecipeEditorKind.Barrel => "barrel",
                RecipeEditorKind.Cooking => "cooking",
                RecipeEditorKind.Alloy => "alloy",
                _ => "custom"
            };
        }

        private static string SanitizePathPart(string value)
        {
            char[] chars = value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
            string result = new(chars);
            while (result.Contains("--", StringComparison.Ordinal)) result = result.Replace("--", "-", StringComparison.Ordinal);
            return string.IsNullOrWhiteSpace(result) ? "recipe" : result.Trim('-');
        }

        private sealed class RecipeDocument
        {
            private string _cleanSerialized;

            public RecipeDocument(string domain, string assetPath, JToken root, bool isDraft)
            {
                Domain = domain;
                AssetPath = assetPath;
                Root = root;
                IsDraft = isDraft;
                _cleanSerialized = isDraft ? "" : SerializeToken(root);
                Dirty = isDraft;
            }

            public string Domain { get; }
            public string AssetPath { get; }
            public JToken Root { get; set; }
            public bool IsDraft { get; }
            public bool Dirty { get; private set; }
            public string DisplayPath => $"{Domain}:{AssetPath}";

            public void MarkClean()
            {
                _cleanSerialized = SerializeToken(Root);
                Dirty = false;
            }

            public void MarkDirty()
            {
                if (string.IsNullOrEmpty(_cleanSerialized))
                {
                    Dirty = true;
                    return;
                }

                Dirty = !JToken.DeepEquals(ParseRecipeJson(_cleanSerialized), Root);
            }
        }

        private sealed class RecipeLiveListTarget(object owner, System.Reflection.MemberInfo member, string registryCode, Type elementType, bool preferArray)
        {
            public string RegistryCode { get; } = registryCode;
            public Type ElementType { get; } = elementType;
            public string LiveKey => $"recipe-registry:{RegistryCode}";
            public string Label => $"{RegistryCode} recipes";

            public object[] ReadItems()
            {
                object? value = GetMemberValue(owner, member);
                if (value is Array array)
                {
                    return array.Cast<object>().ToArray();
                }

                if (value is System.Collections.IEnumerable enumerable && value is not string)
                {
                    return enumerable.Cast<object>().ToArray();
                }

                return [];
            }

            public void ReplaceAll(IEnumerable<object> items)
            {
                object[] values = items.ToArray();
                object? value = GetMemberValue(owner, member);
                if (value is System.Collections.IList list && !list.IsReadOnly && !preferArray)
                {
                    list.Clear();
                    foreach (object item in values)
                    {
                        list.Add(item);
                    }
                    return;
                }

                Array array = Array.CreateInstance(ElementType, values.Length);
                for (int index = 0; index < values.Length; index++)
                {
                    array.SetValue(values[index], index);
                }
                SetMemberValue(owner, member, array);
            }
        }

        private sealed class RecipeEntry
        {
            public RecipeEntry(RecipeDocument document, JObject recipe, int arrayIndex)
            {
                Document = document;
                Recipe = recipe;
                ArrayIndex = arrayIndex;
                Kind = DetectKind(document.AssetPath, recipe);
            }

            public RecipeDocument Document { get; }
            public JObject Recipe { get; private set; }
            public int ArrayIndex { get; }
            public RecipeEditorKind Kind { get; }
            public string KindLabel => Kind.ToString();
            public string Key => $"{Document.DisplayPath}:{ArrayIndex}";
            public string DisplayName => Recipe["name"]?.ToString() ?? Recipe["code"]?.ToString() ?? Path.GetFileNameWithoutExtension(Document.AssetPath);
            public string ShortLabel => $"{KindLabel} | {DisplayName}";
            public string SearchText => $"{Document.DisplayPath} {KindLabel} {DisplayName} {Recipe["output"]?["code"]} {Recipe["ingredient"]?["code"]}";

            public void ReplaceRecipe(JObject replacement)
            {
                Recipe = replacement;
                if (Document.Root is JArray array && ArrayIndex >= 0 && ArrayIndex < array.Count)
                {
                    array[ArrayIndex] = replacement;
                }
                else
                {
                    Document.Root = replacement;
                }
            }

            private static RecipeEditorKind DetectKind(string assetPath, JObject recipe)
            {
                string path = assetPath.Replace('\\', '/').ToLowerInvariant();
                if (path.Contains("/grid/", StringComparison.Ordinal)) return RecipeEditorKind.Grid;
                if (path.Contains("/smithing/", StringComparison.Ordinal)) return RecipeEditorKind.Smithing;
                if (path.Contains("/clayforming/", StringComparison.Ordinal)) return RecipeEditorKind.Clayforming;
                if (path.Contains("/knapping/", StringComparison.Ordinal)) return RecipeEditorKind.Knapping;
                if (path.Contains("/barrel/", StringComparison.Ordinal)) return RecipeEditorKind.Barrel;
                if (path.Contains("/cooking/", StringComparison.Ordinal)) return RecipeEditorKind.Cooking;
                if (path.Contains("/alloy/", StringComparison.Ordinal)) return RecipeEditorKind.Alloy;
                if (recipe["ingredientPattern"] != null) return RecipeEditorKind.Grid;
                if (recipe["pattern"] != null) return RecipeEditorKind.Knapping;
                return RecipeEditorKind.Other;
            }
        }
    }
}
