using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private static readonly string[] ModelFaceNames = ["north", "east", "south", "west", "up", "down"];
    private const int ModelBrowserMaxVisibleEntries = 600;
    private const int ModelHistoryLimit = 120;
    private const string ModelNewDocumentTemplateLocation = "game:shapes/block/basic/cube.json";

    private enum ModelGizmoTool
    {
        None,
        Move,
        Resize,
        Rotate
    }

    private sealed class ModelTextureEntry
    {
        public string Code = "";
        public string Path = "";
    }

    private sealed class ModelFaceData
    {
        public string Texture = "";
        public float[] Uv = new float[4];
        public float Rotation;
        public int Glow;
        public bool Enabled = true;
        public JObject? Extra;

        public ModelFaceData Clone()
        {
            return new ModelFaceData
            {
                Texture = Texture,
                Uv = (float[])Uv.Clone(),
                Rotation = Rotation,
                Glow = Glow,
                Enabled = Enabled,
                Extra = (JObject?)Extra?.DeepClone()
            };
        }
    }

    private sealed class ModelElementData
    {
        public string Name = "";
        public double[] From = new double[3];
        public double[] To = new double[3];
        public double[]? RotationOrigin;
        public double RotationX;
        public double RotationY;
        public double RotationZ;
        public bool Shade = true;
        public string StepParentName = "";
        public ModelFaceData?[] Faces = new ModelFaceData?[6];
        public List<ModelElementData> Children = [];
        public ModelElementData? Parent;
        public JObject? Extra;
        public bool Visible = true;

        public double SizeX => To[0] - From[0];
        public double SizeY => To[1] - From[1];
        public double SizeZ => To[2] - From[2];

        public ModelElementData CloneSubtree()
        {
            ModelElementData clone = new()
            {
                Name = Name,
                From = (double[])From.Clone(),
                To = (double[])To.Clone(),
                RotationOrigin = (double[]?)RotationOrigin?.Clone(),
                RotationX = RotationX,
                RotationY = RotationY,
                RotationZ = RotationZ,
                Shade = Shade,
                StepParentName = StepParentName,
                Extra = (JObject?)Extra?.DeepClone(),
                Visible = Visible
            };
            for (int face = 0; face < 6; face++)
            {
                clone.Faces[face] = Faces[face]?.Clone();
            }
            foreach (ModelElementData child in Children)
            {
                ModelElementData childClone = child.CloneSubtree();
                childClone.Parent = clone;
                clone.Children.Add(childClone);
            }
            return clone;
        }

        public IEnumerable<ModelElementData> EnumerateSubtree()
        {
            yield return this;
            foreach (ModelElementData child in Children)
            {
                foreach (ModelElementData descendant in child.EnumerateSubtree())
                {
                    yield return descendant;
                }
            }
        }
    }

    private sealed class ModelDocumentData
    {
        public string Domain = "game";
        public string AssetPath = "";
        public int TextureWidth = 16;
        public int TextureHeight = 16;
        public List<ModelTextureEntry> Textures = [];
        public Dictionary<string, int[]> TextureSizes = new(StringComparer.Ordinal);
        public List<ModelElementData> Roots = [];
        public JObject? Extra;
        public string SourceText = "";
        public bool IsNew;
        public bool Dirty;
        public bool FromAuthoredFile;

        public string DisplayPath => $"{Domain}:{AssetPath}";

        public IEnumerable<ModelElementData> EnumerateElements()
        {
            foreach (ModelElementData root in Roots)
            {
                foreach (ModelElementData element in root.EnumerateSubtree())
                {
                    yield return element;
                }
            }
        }

        public (int Width, int Height) GetTextureSize(string code)
        {
            if (!string.IsNullOrWhiteSpace(code) &&
                TextureSizes.TryGetValue(code, out int[]? size) &&
                size.Length >= 2 && size[0] > 0 && size[1] > 0)
            {
                return (size[0], size[1]);
            }

            return (Math.Max(1, TextureWidth), Math.Max(1, TextureHeight));
        }
    }

    private sealed record ModelShapeAssetEntry(string Domain, string AssetPath, IAsset Asset, bool Authored = false)
    {
        public string Display => $"{Domain}:{AssetPath}";
        public string SearchText { get; } = $"{Domain}:{AssetPath}{(Authored ? " authored" : "")}".ToLowerInvariant();
    }

    private sealed record ModelHistoryEntry(string Label, string Json, int[]? SelectionPath, int SelectedFace, int[][]? SelectionPaths = null);

    private sealed record ModelGizmoDragElementState(
        ModelElementData Element,
        double[] From,
        double[] To,
        double[]? RotationOrigin,
        double RotationX,
        double RotationY,
        double RotationZ);

    private readonly ImGuiThreePanelLayoutState _modelLayout = new(0.21f, 0.30f);
    private readonly DevToolsEditorDiagnostics _modelDiagnostics = new("Models");
    private List<ModelShapeAssetEntry>? _modelShapeIndex;
    private string _modelBrowserFilter = "";
    private string _modelBrowserDomain = "";
    private float _modelTreePanelFraction = 0.55f;
    private ModelDocumentData? _modelDoc;
    private ModelElementData? _modelSelectedElement;
    private readonly HashSet<ModelElementData> _modelSelectedElements = [];
    private readonly List<ModelElementData> _modelSelectionOrder = [];
    private int _modelSelectedFace = -1;
    private string _modelSelectedTextureCode = "";
    private string _modelStatus = "";
    private readonly List<ModelHistoryEntry> _modelUndoStack = [];
    private readonly List<ModelHistoryEntry> _modelRedoStack = [];
    private string? _modelPendingEditSnapshot;
    private bool _modelPreviewDirty;
    private ModelGizmoTool _modelGizmoTool = ModelGizmoTool.Move;
    private bool _modelSnapEnabled = true;
    private float _modelSnapMoveUnits = 0.5f;
    private float _modelSnapRotateDegrees = 5f;
    private ModelShapeAssetEntry? _modelPendingOpenEntry;
    private bool _modelPendingNewDocument;
    private bool _modelOpenDiscardPopup;
    private ModelElementData? _modelReparentSource;
    private readonly Dictionary<string, string> _modelComboFilters = new(StringComparer.Ordinal);
    private List<string>? _modelTextureAssetIndex;
    private List<string>? _modelStepParentNameIndex;

    /// <summary>
    /// Searchable replacement for plain combos: opens with a filter box and caps the
    /// visible options. With <paramref name="allowCustom"/> the typed filter text itself
    /// becomes selectable, so values outside the option list stay reachable.
    /// </summary>
    private bool ModelFilteredCombo(string id, string preview, IReadOnlyList<string> options, out string selected, bool allowCustom, string filterHint = "type to filter")
    {
        selected = "";
        if (!ImGui.BeginCombo(id, string.IsNullOrEmpty(preview) ? "(pick)" : preview, ImGuiComboFlags.HeightLarge))
        {
            return false;
        }

        bool changed = false;
        try
        {
            string filter = _modelComboFilters.TryGetValue(id, out string? existing) ? existing : "";
            if (ImGui.IsWindowAppearing())
            {
                ImGui.SetKeyboardFocusHere();
            }
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint($"##{id}-filter", filterHint, ref filter, 200);
            _modelComboFilters[id] = filter;
            string normalized = filter.Trim();

            if (allowCustom && normalized.Length > 0 &&
                !options.Any(option => option.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                if (ImGui.Selectable($"Use \"{normalized}\"##{id}-custom"))
                {
                    selected = normalized;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
            }

            int shown = 0;
            foreach (string option in options)
            {
                if (normalized.Length > 0 && !option.Contains(normalized, StringComparison.OrdinalIgnoreCase)) continue;
                if (shown >= 250)
                {
                    ImGui.TextDisabled("... refine the filter for more");
                    break;
                }

                shown++;
                bool isCurrent = option.Equals(preview, StringComparison.Ordinal);
                if (ImGui.Selectable($"{option}##{id}-option-{shown}", isCurrent))
                {
                    selected = option;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                if (isCurrent && ImGui.IsWindowAppearing())
                {
                    ImGui.SetScrollHereY();
                }
            }

            if (shown == 0 && (!allowCustom || normalized.Length == 0))
            {
                ImGui.TextDisabled("No matches.");
            }
        }
        finally
        {
            ImGui.EndCombo();
        }

        return changed;
    }

    private List<string> EnsureModelTextureAssetIndex()
    {
        if (_modelTextureAssetIndex != null) return _modelTextureAssetIndex;

        List<string> index = [];
        try
        {
            foreach (IAsset asset in _api.Assets.AllAssets.Values)
            {
                if (asset?.Location == null) continue;

                string path = asset.Location.Path.Replace('\\', '/');
                if (!path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) ||
                    !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                index.Add($"{asset.Location.Domain}:{path["textures/".Length..^".png".Length]}");
            }

            index.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            _modelDiagnostics.Exception("Texture index build failed", exception);
        }

        _modelTextureAssetIndex = index;
        return index;
    }

    private List<string> EnsureModelStepParentNameIndex()
    {
        if (_modelStepParentNameIndex != null) return _modelStepParentNameIndex;

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (EntityProperties entityType in _api.World.EntityTypes)
            {
                Shape? shape = entityType?.Client?.LoadedShapeForEntity;
                if (shape?.Elements == null) continue;

                foreach (ShapeElement element in shape.Elements)
                {
                    element.WalkRecursive(walked =>
                    {
                        if (!string.IsNullOrWhiteSpace(walked.Name)) names.Add(walked.Name);
                    });
                }
            }
        }
        catch (Exception exception)
        {
            _modelDiagnostics.Exception("Step parent index build failed", exception);
        }

        List<string> index = [.. names];
        index.Sort(StringComparer.OrdinalIgnoreCase);
        _modelStepParentNameIndex = index;
        return index;
    }

    private List<string> ModelCollectKnownDomains()
    {
        EnsureModelShapeIndex();
        List<string> domains = (_modelShapeIndex ?? [])
            .Select(entry => entry.Domain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain.Equals("game", StringComparison.OrdinalIgnoreCase) ? "" : domain, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return domains;
    }

    private void ModelEditorTab(float deltaSeconds, bool showDiagnostics)
    {
        _ = deltaSeconds;
        ClearActiveTransformGizmo();
        EnsureModelShapeIndex();
        ModelHandleShortcuts();

        DrawModelToolbar();

        NVector2 available = ImGui.GetContentRegionAvail();
        float height = Math.Max(280f, available.Y - 4f);
        ImGuiLayoutHelper.CalculateThreePanelWidths(
            available.X,
            8f,
            _modelLayout,
            220f,
            520f,
            420f,
            260f,
            560f,
            out float panelAvailableWidth,
            out float leftWidth,
            out float centerWidth,
            out float rightWidth);

        DrawModelLeftPanel(new NVector2(leftWidth, height));
        ImGui.SameLine(0f, 0f);
        ImGuiLayoutHelper.DrawVerticalSplitter("##model-splitter-left", height, 8f, panelAvailableWidth, ref _modelLayout.LeftFraction, 220f, 520f);
        ImGui.SameLine(0f, 0f);
        DrawModelCenterPanel(new NVector2(centerWidth, height));
        ImGui.SameLine(0f, 0f);
        ImGuiLayoutHelper.DrawVerticalSplitter("##model-splitter-right", height, 8f, panelAvailableWidth, ref _modelLayout.RightFraction, 260f, 560f, invertDrag: true);
        ImGui.SameLine(0f, 0f);
        DrawModelInspectorPanel(new NVector2(rightWidth, height));

        DrawModelDiscardPopup();
        DrawModelPrimitiveWindow();
        ModelMaybeAutoApplyLive(force: false);
        _modelDiagnostics.Draw("models-tab", showDiagnostics);
    }

    private void DrawModelToolbar()
    {
        if (ImGui.Button("New shape##model-new"))
        {
            ModelRequestNewDocument();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Create a new shape document from the basic cube template.");
        }

        ImGui.SameLine();
        bool canUndo = _modelUndoStack.Count > 0 && _modelDoc != null;
        if (!canUndo) ImGui.BeginDisabled();
        if (ImGui.Button("Undo##model-undo"))
        {
            ModelUndo();
        }
        if (!canUndo) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(canUndo ? $"Undo: {_modelUndoStack[^1].Label} (Ctrl+Z)" : "Nothing to undo (Ctrl+Z)");
        }

        ImGui.SameLine();
        bool canRedo = _modelRedoStack.Count > 0 && _modelDoc != null;
        if (!canRedo) ImGui.BeginDisabled();
        if (ImGui.Button("Redo##model-redo"))
        {
            ModelRedo();
        }
        if (!canRedo) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(canRedo ? $"Redo: {_modelRedoStack[^1].Label} (Ctrl+Y)" : "Nothing to redo (Ctrl+Y)");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        int tool = (int)_modelGizmoTool;
        ImGui.RadioButton("Select##model-tool-none", ref tool, (int)ModelGizmoTool.None);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Select tool: click elements in the viewport without a gizmo (Ctrl+Shift+1).");
        }
        ImGui.SameLine();
        ImGui.RadioButton("Move##model-tool-move", ref tool, (int)ModelGizmoTool.Move);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Move tool: drag the axis arrows to translate the element (Ctrl+Shift+2).");
        }
        ImGui.SameLine();
        ImGui.RadioButton("Resize##model-tool-resize", ref tool, (int)ModelGizmoTool.Resize);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Resize tool: drag face handles on cuboids, or drag corner handles on generated primitive groups to deform them. Hold Shift for uniform scale (Ctrl+Shift+3).");
        }
        ImGui.SameLine();
        ImGui.RadioButton("Rotate##model-tool-rotate", ref tool, (int)ModelGizmoTool.Rotate);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Rotate tool: drag the rings to rotate around the rotation origin (Ctrl+Shift+4).");
        }
        _modelGizmoTool = (ModelGizmoTool)tool;

        ImGui.SameLine();
        bool primitiveOpen = _modelPrimitiveWindowOpen;
        if (primitiveOpen) ImGui.PushStyleColor(ImGuiCol.Button, new NVector4(0.55f, 0.42f, 0.2f, 1f));
        if (ImGui.Button("Prism helper##model-primitive-toggle"))
        {
            _modelPrimitiveWindowOpen = !_modelPrimitiveWindowOpen;
            _modelPrimitivePreviewDirty = true;
        }
        if (primitiveOpen) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Generate smooth shapes (spheres, cylinders, cones, tori, arches) out of cuboids, with a live viewport preview.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Shortcuts##model-shortcuts"))
        {
            ImGui.OpenPopup("##model-shortcuts-popup");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("List every Models tab keyboard and mouse shortcut.");
        }
        DrawModelShortcutsPopup();

        DrawModelSelectionToolbar();

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.Checkbox("Snap##model-snap", ref _modelSnapEnabled);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Snap gizmo move/resize to the unit grid and rotation to the angle step. Hold Alt while dragging to bypass.");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(72f);
        if (ImGui.DragFloat("##model-snap-move", ref _modelSnapMoveUnits, 0.05f, 0.0625f, 8f, "%.3f u"))
        {
            _modelSnapMoveUnits = Math.Clamp(_modelSnapMoveUnits, 0.0625f, 8f);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Move/resize snap step in shape units (16 units = 1 block).");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(72f);
        if (ImGui.DragFloat("##model-snap-rotate", ref _modelSnapRotateDegrees, 0.5f, 1f, 45f, "%.1f deg"))
        {
            _modelSnapRotateDegrees = Math.Clamp(_modelSnapRotateDegrees, 1f, 45f);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Rotation snap step in degrees.");
        }

        if (_modelDoc != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            ImGui.TextUnformatted($"{_modelDoc.DisplayPath}{(_modelDoc.Dirty ? " *" : "")}");
        }

        if (!string.IsNullOrWhiteSpace(_modelStatus))
        {
            ImGui.SameLine();
            ImGui.TextColored(new NVector4(0.62f, 0.8f, 0.62f, 1f), _modelStatus);
        }

        ImGui.Separator();
    }

    private void DrawModelSelectionToolbar()
    {
        if (_modelDoc == null) return;

        List<ModelElementData> selected = ModelSelectedElementsInDocument();
        bool hasSelection = selected.Count > 0;

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextDisabled($"{selected.Count} selected");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Plain click selects one element. Ctrl+click toggles multi-selection. The inspector edits the active element.");
        }

        ImGui.SameLine();
        if (!hasSelection) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Clear##model-selection-clear"))
        {
            ModelSelectElement(null);
        }
        if (!hasSelection) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.SmallButton("All##model-selection-all"))
        {
            ModelSelectElements(_modelDoc.EnumerateElements(), _modelDoc.Roots.FirstOrDefault());
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Select every element in this shape.");
        }

        ImGui.SameLine();
        if (!hasSelection) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Duplicate##model-selection-duplicate"))
        {
            ModelDuplicateSelectedElements();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Delete##model-selection-delete"))
        {
            ModelDeleteSelectedElements();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Mirror X##model-selection-mirror-x"))
        {
            ModelMirrorSelectedElements(0);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Mirror Y##model-selection-mirror-y"))
        {
            ModelMirrorSelectedElements(1);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Mirror Z##model-selection-mirror-z"))
        {
            ModelMirrorSelectedElements(2);
        }
        if (!hasSelection) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Mirror selected top-level elements around the model origin on this axis. Selected descendants of selected parents are skipped to avoid double mirroring.");
        }
    }

    private void DrawModelShortcutsPopup()
    {
        if (!ImGui.BeginPopup("##model-shortcuts-popup")) return;

        try
        {
            ImGui.SeparatorText("Keyboard");
            ImGui.TextUnformatted("Ctrl+Shift+1..4   Select / Move / Resize / Rotate tool");
            ImGui.TextUnformatted("Ctrl+Z / Ctrl+Y   Undo / Redo");
            ImGui.TextUnformatted("Ctrl+D            Duplicate selected element");
            ImGui.TextUnformatted("Delete            Delete selected element");
            ImGui.TextUnformatted("Home              Focus camera on selection");
            ImGui.TextUnformatted("Hold Alt          Bypass snapping while dragging");
            ImGui.TextDisabled("Plain letter keys are not used; the game still receives them.");

            ImGui.SeparatorText("Viewport mouse");
            ImGui.TextUnformatted("Left click        Select element / drag gizmo");
            ImGui.TextUnformatted("Ctrl+Left click   Toggle element in multi-selection");
            ImGui.TextUnformatted("Right drag        Orbit camera");
            ImGui.TextUnformatted("Middle or Shift+Right drag   Pan camera");
            ImGui.TextUnformatted("Mouse wheel       Zoom");

            ImGui.SeparatorText("UV canvas mouse");
            ImGui.TextUnformatted("Left drag         Move UV rectangle");
            ImGui.TextUnformatted("Corner drag       Resize UV rectangle");
            ImGui.TextUnformatted("Right/Middle drag Pan, wheel zooms");

            ImGui.TextDisabled("Everything also has a button: toolbar tools, Undo/Redo, Focus selection,");
            ImGui.TextDisabled("and Duplicate/Delete in the inspector and the tree right-click menu.");
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void DrawModelLeftPanel(NVector2 size)
    {
        ImGui.BeginChild("##model-left-panel", size, false);
        try
        {
            float browserHeight = Math.Max(120f, size.Y * (1f - _modelTreePanelFraction) - 5f);
            float treeHeight = Math.Max(120f, size.Y - browserHeight - 10f);

            DrawModelBrowserPanel(new NVector2(size.X, browserHeight));
            ImGuiLayoutHelper.DrawHorizontalSplitter("##model-tree-splitter", size.X, 8f, size.Y, ref _modelTreePanelFraction, 120f, Math.Max(140f, size.Y - 140f));
            DrawModelTreePanel(new NVector2(size.X, treeHeight));
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawModelBrowserPanel(NVector2 size)
    {
        ImGui.BeginChild("##model-browser", size, true);
        try
        {
            ImGui.SeparatorText("Shapes");
            ImGui.SetNextItemWidth(-78f);
            ImGui.InputTextWithHint("##model-browser-filter", "filter shapes", ref _modelBrowserFilter, 200);
            ImGui.SameLine();
            if (ImGui.Button("Refresh##model-browser-refresh"))
            {
                _modelShapeIndex = null;
                EnsureModelShapeIndex();
            }

            List<ModelShapeAssetEntry> index = _modelShapeIndex ?? [];
            ImGui.SetNextItemWidth(-1f);
            ImGuiLayoutHelper.DrawDomainCombo("##model-browser-domain", ref _modelBrowserDomain, index.Select(entry => entry.Domain));

            string filter = _modelBrowserFilter.Trim().ToLowerInvariant();
            List<ModelShapeAssetEntry> filtered = index
                .Where(entry => ImGuiLayoutHelper.MatchesDomain(_modelBrowserDomain, entry.Domain))
                .Where(entry => filter.Length == 0 || entry.SearchText.Contains(filter, StringComparison.Ordinal))
                .ToList();

            ImGui.TextDisabled($"{filtered.Count} of {index.Count} shape file(s)");
            ImGui.BeginChild("##model-browser-list", new NVector2(0f, 0f), false);
            try
            {
                int shown = 0;
                foreach (ModelShapeAssetEntry entry in filtered)
                {
                    if (shown >= ModelBrowserMaxVisibleEntries)
                    {
                        ImGui.TextDisabled($"... {filtered.Count - shown} more. Refine the filter.");
                        break;
                    }

                    shown++;
                    bool selected = _modelDoc != null && !_modelDoc.IsNew &&
                        _modelDoc.FromAuthoredFile == entry.Authored &&
                        string.Equals(_modelDoc.Domain, entry.Domain, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(_modelDoc.AssetPath, entry.AssetPath, StringComparison.OrdinalIgnoreCase);
                    string label = (_modelBrowserDomain.Length > 0 ? entry.AssetPath : entry.Display) + (entry.Authored ? " [authored]" : "");
                    if (ImGui.Selectable($"{label}##model-asset-{shown}", selected) && !selected)
                    {
                        ModelRequestOpenDocument(entry);
                    }
                    if (entry.Authored && ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("A file you saved through the devtools authored models folder.");
                    }
                }
            }
            finally
            {
                ImGui.EndChild();
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawModelTreePanel(NVector2 size)
    {
        ImGui.BeginChild("##model-tree", size, true);
        try
        {
            ImGui.SeparatorText("Elements");
            if (_modelDoc == null)
            {
                ImGui.TextDisabled("Open a shape or create a new one.");
                return;
            }

            if (ImGui.SmallButton("Add cube##model-tree-add-root"))
            {
                ModelAddElement(null);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Add a new root level cube element.");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Add primitive##model-tree-add-primitive"))
            {
                _modelPrimitiveWindowOpen = true;
                _modelPrimitivePreviewDirty = true;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Open the prism helper to generate spheres, cylinders, cones, tori, and arches from cuboids.");
            }
            if (_modelReparentSource != null)
            {
                ImGui.SameLine();
                ImGui.TextColored(new NVector4(1f, 0.76f, 0.32f, 1f), $"Pick new parent for '{_modelReparentSource.Name}'");
                ImGui.SameLine();
                if (ImGui.SmallButton("To root##model-reparent-root"))
                {
                    ModelReparentElement(_modelReparentSource, null);
                    _modelReparentSource = null;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel##model-reparent-cancel"))
                {
                    _modelReparentSource = null;
                }
            }

            ImGui.BeginChild("##model-tree-list", new NVector2(0f, 0f), false, ImGuiWindowFlags.HorizontalScrollbar);
            try
            {
                for (int index = 0; index < _modelDoc.Roots.Count; index++)
                {
                    DrawModelTreeNode(_modelDoc.Roots[index], index, depth: 0);
                }
            }
            finally
            {
                ImGui.EndChild();
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawModelTreeNode(ModelElementData element, int index, int depth)
    {
        ImGui.PushID(index);
        try
        {
            if (ImGui.SmallButton(element.Visible ? "O##model-vis" : "-##model-vis"))
            {
                element.Visible = !element.Visible;
                _modelPreviewDirty = true;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(element.Visible ? "Visible in viewport. Click to hide." : "Hidden in viewport. Click to show. Hidden elements still save.");
            }
            ImGui.SameLine();

            ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth;
            if (element.Children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf;
            if (ModelIsElementSelected(element)) flags |= ImGuiTreeNodeFlags.Selected;
            if (depth < 2 && element.Children.Count > 0) flags |= ImGuiTreeNodeFlags.DefaultOpen;

            string name = string.IsNullOrWhiteSpace(element.Name) ? "(unnamed)" : element.Name;
            if (ReferenceEquals(element, _modelSelectedElement) && _modelSelectedElements.Count > 1)
            {
                name = "> " + name;
            }
            bool open = ImGui.TreeNodeEx($"{name}###model-node", flags);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
            {
                if (_modelReparentSource != null)
                {
                    if (!ReferenceEquals(_modelReparentSource, element))
                    {
                        ModelReparentElement(_modelReparentSource, element);
                    }
                    _modelReparentSource = null;
                }
                else
                {
                    ModelSelectElement(element, additive: ImGui.GetIO().KeyCtrl);
                }
            }

            if (ImGui.BeginPopupContextItem("##model-node-context"))
            {
                if (!ModelIsElementSelected(element))
                {
                    ModelSelectElement(element);
                }
                if (ImGui.MenuItem("Add child cube"))
                {
                    ModelAddElement(element);
                }
                if (ImGui.MenuItem("Duplicate", "Ctrl+D"))
                {
                    ModelDuplicateSelectedElements();
                }
                if (ImGui.MenuItem("Delete", "Del"))
                {
                    ModelDeleteSelectedElements();
                }
                if (ImGui.BeginMenu("Mirror selected"))
                {
                    if (ImGui.MenuItem("Around X origin")) ModelMirrorSelectedElements(0);
                    if (ImGui.MenuItem("Around Y origin")) ModelMirrorSelectedElements(1);
                    if (ImGui.MenuItem("Around Z origin")) ModelMirrorSelectedElements(2);
                    ImGui.EndMenu();
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Move up"))
                {
                    ModelMoveElement(element, -1);
                }
                if (ImGui.MenuItem("Move down"))
                {
                    ModelMoveElement(element, 1);
                }
                if (ImGui.MenuItem("Reparent..."))
                {
                    _modelReparentSource = element;
                }
                if (element.Parent != null && ImGui.MenuItem("Unparent to root"))
                {
                    ModelReparentElement(element, null);
                }
                ImGui.EndPopup();
            }

            if (open)
            {
                for (int child = 0; child < element.Children.Count; child++)
                {
                    DrawModelTreeNode(element.Children[child], child, depth + 1);
                }
                ImGui.TreePop();
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    private void DrawModelCenterPanel(NVector2 size)
    {
        ImGui.BeginChild("##model-center-panel", size, true);
        try
        {
            if (ImGui.BeginTabBar("##model-center-tabs"))
            {
                if (ImGui.BeginTabItem("Viewport##model-center-viewport"))
                {
                    DrawModelViewportPanel();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("UV / Textures##model-center-uv"))
                {
                    DrawModelUvPanel();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("JSON##model-center-json"))
                {
                    DrawModelJsonPanel();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawModelInspectorPanel(NVector2 size)
    {
        ImGui.BeginChild("##model-inspector", size, true, ImGuiWindowFlags.HorizontalScrollbar);
        try
        {
            if (_modelDoc == null)
            {
                ImGui.TextDisabled("Open a shape or create a new one.");
                return;
            }

            DrawModelDocumentSection(_modelDoc);
            DrawModelElementSection(_modelDoc);
            DrawModelFacesSection(_modelDoc);
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawModelDocumentSection(ModelDocumentData doc)
    {
        ImGui.SeparatorText("Shape document");
        if (doc.IsNew)
        {
            ImGui.SetNextItemWidth(170f);
            if (ModelFilteredCombo("Domain##model-doc-domain", doc.Domain, ModelCollectKnownDomains(), out string pickedDomain, allowCustom: true, filterHint: "filter domains"))
            {
                doc.Domain = string.IsNullOrWhiteSpace(pickedDomain) ? "game" : pickedDomain.Trim().ToLowerInvariant();
                doc.Dirty = true;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Mod domain for the new shape. Pick a loaded domain or type a new one in the filter.");
            }
            string assetPath = doc.AssetPath;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputTextWithHint("##model-doc-path", "shapes/block/mymodel.json", ref assetPath, 240))
            {
                doc.AssetPath = assetPath.Trim().Replace('\\', '/');
                doc.Dirty = true;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Asset path for the new shape, e.g. shapes/block/chair.json");
            }
        }
        else
        {
            ImGui.TextWrapped(doc.DisplayPath);
        }

        int textureWidth = doc.TextureWidth;
        int textureHeight = doc.TextureHeight;
        ImGui.SetNextItemWidth(90f);
        bool sizeChanged = ImGui.InputInt("##model-doc-texw", ref textureWidth, 0);
        if (ImGui.IsItemActivated()) ModelBeginEdit();
        bool sizeCommitted = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SameLine();
        ImGui.TextUnformatted("x");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        sizeChanged |= ImGui.InputInt("Texture size##model-doc-texh", ref textureHeight, 0);
        if (ImGui.IsItemActivated()) ModelBeginEdit();
        sizeCommitted |= ImGui.IsItemDeactivatedAfterEdit();
        if (sizeChanged)
        {
            doc.TextureWidth = Math.Clamp(textureWidth, 1, 4096);
            doc.TextureHeight = Math.Clamp(textureHeight, 1, 4096);
            ModelMarkChanged();
        }
        if (sizeCommitted) ModelEndEdit("Edit texture size");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("UV coordinate space of the shape (textureWidth/textureHeight). Default 16x16.");
        }

        if (ImGui.Button("Save authored copy##model-save"))
        {
            QueueSourceSave(TrySaveModelToSource(), status => _modelStatus = status);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Write the shape JSON to the InGameDevTools authored models folder with a diff preview.");
        }

        DrawModelRuntimeControls(doc);
        ImGui.Spacing();
    }

    private void DrawModelElementSection(ModelDocumentData doc)
    {
        ImGui.SeparatorText("Element");
        ModelElementData? element = _modelSelectedElement;
        if (element == null)
        {
            ImGui.TextDisabled("Select an element in the tree or viewport.");
            return;
        }

        string name = element.Name;
        ImGui.SetNextItemWidth(-1f);
        bool nameChanged = ImGui.InputTextWithHint("##model-elem-name", "element name", ref name, 120);
        if (ImGui.IsItemActivated()) ModelBeginEdit();
        if (nameChanged)
        {
            element.Name = name;
            ModelMarkChanged();
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) ModelEndEdit("Rename element");

        NVector3 from = new((float)element.From[0], (float)element.From[1], (float)element.From[2]);
        bool fromChanged = ImGui.DragFloat3("From##model-elem-from", ref from, 0.05f);
        if (ImGui.IsItemActivated()) ModelBeginEdit();
        if (fromChanged)
        {
            element.From[0] = from.X;
            element.From[1] = from.Y;
            element.From[2] = from.Z;
            ModelMarkChanged();
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) ModelEndEdit("Edit from");

        NVector3 to = new((float)element.To[0], (float)element.To[1], (float)element.To[2]);
        bool toChanged = ImGui.DragFloat3("To##model-elem-to", ref to, 0.05f);
        if (ImGui.IsItemActivated()) ModelBeginEdit();
        if (toChanged)
        {
            element.To[0] = to.X;
            element.To[1] = to.Y;
            element.To[2] = to.Z;
            ModelMarkChanged();
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) ModelEndEdit("Edit to");

        NVector3 sizeVec = new((float)element.SizeX, (float)element.SizeY, (float)element.SizeZ);
        bool sizeChanged = ImGui.DragFloat3("Size##model-elem-size", ref sizeVec, 0.05f);
        if (ImGui.IsItemActivated()) ModelBeginEdit();
        if (sizeChanged)
        {
            element.To[0] = element.From[0] + Math.Max(0f, sizeVec.X);
            element.To[1] = element.From[1] + Math.Max(0f, sizeVec.Y);
            element.To[2] = element.From[2] + Math.Max(0f, sizeVec.Z);
            ModelMarkChanged();
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) ModelEndEdit("Edit size");

        bool hasOrigin = element.RotationOrigin != null;
        if (ImGui.Checkbox("Rotation origin##model-elem-has-origin", ref hasOrigin))
        {
            ModelBeginEdit();
            element.RotationOrigin = hasOrigin
                ? [element.From[0] + element.SizeX * 0.5, element.From[1] + element.SizeY * 0.5, element.From[2] + element.SizeZ * 0.5]
                : null;
            ModelMarkChanged();
            ModelEndEdit(hasOrigin ? "Add rotation origin" : "Clear rotation origin");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Without an origin the engine rotates around 0,0,0 of the parent space. Editor rotation tools center it on the element before rotating.");
        }
        if (element.RotationOrigin != null)
        {
            NVector3 origin = new((float)element.RotationOrigin[0], (float)element.RotationOrigin[1], (float)element.RotationOrigin[2]);
            bool originChanged = ImGui.DragFloat3("##model-elem-origin", ref origin, 0.05f);
            if (ImGui.IsItemActivated()) ModelBeginEdit();
            if (originChanged)
            {
                element.RotationOrigin[0] = origin.X;
                element.RotationOrigin[1] = origin.Y;
                element.RotationOrigin[2] = origin.Z;
                ModelMarkChanged();
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) ModelEndEdit("Edit rotation origin");
        }

        NVector3 rotation = new((float)element.RotationX, (float)element.RotationY, (float)element.RotationZ);
        bool rotationChanged = ImGui.DragFloat3("Rotation##model-elem-rotation", ref rotation, 0.25f, -360f, 360f, "%.2f");
        if (ImGui.IsItemActivated()) ModelBeginEdit();
        if (rotationChanged)
        {
            ModelEnsureRotationOrigin(element);
            element.RotationX = rotation.X;
            element.RotationY = rotation.Y;
            element.RotationZ = rotation.Z;
            ModelMarkChanged();
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) ModelEndEdit("Edit rotation");

        bool shade = element.Shade;
        if (ImGui.Checkbox("Shade##model-elem-shade", ref shade))
        {
            ModelBeginEdit();
            element.Shade = shade;
            ModelMarkChanged();
            ModelEndEdit("Toggle shade");
        }
        ImGui.SameLine();
        if (ImGui.Button("Auto UV element##model-elem-autouv"))
        {
            ModelBeginEdit();
            for (int face = 0; face < 6; face++)
            {
                ModelAutoUvFace(element, face);
            }
            ModelMarkChanged();
            ModelEndEdit("Auto UV element");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Set every face UV to [0, 0, faceWidth, faceHeight] from the element dimensions.");
        }

        List<string> stepParentOptions = ["(none)"];
        stepParentOptions.AddRange(doc.EnumerateElements()
            .Select(candidate => candidate.Name)
            .Where(candidateName => !string.IsNullOrWhiteSpace(candidateName) && !candidateName.Equals(element.Name, StringComparison.Ordinal)));
        stepParentOptions.AddRange(EnsureModelStepParentNameIndex());
        List<string> dedupedStepParents = stepParentOptions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string stepPreview = string.IsNullOrWhiteSpace(element.StepParentName) ? "(none)" : element.StepParentName;
        ImGui.SetNextItemWidth(-92f);
        if (ModelFilteredCombo("Step parent##model-elem-stepparent", stepPreview, dedupedStepParents, out string pickedStepParent, allowCustom: true, filterHint: "filter element names"))
        {
            ModelBeginEdit();
            element.StepParentName = pickedStepParent == "(none)" ? "" : pickedStepParent;
            ModelMarkChanged();
            ModelEndEdit("Edit step parent");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Element in another shape this element attaches to when step-parented (e.g. a seraph bone for wearables). Options come from this shape and loaded entity shapes; type in the filter for anything else.");
        }

        int selectedCount = ModelSelectedElementsInDocument().Count;
        if (ImGui.SmallButton(selectedCount > 1 ? "Duplicate selected##model-elem-duplicate" : "Duplicate##model-elem-duplicate"))
        {
            ModelDuplicateSelectedElements();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Duplicate the selected element and its children (Ctrl+D).");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(selectedCount > 1 ? "Delete selected##model-elem-delete" : "Delete##model-elem-delete"))
        {
            ModelDeleteSelectedElements();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Delete the selected element and its children (Delete).");
        }
        ImGui.Spacing();
    }

    private void DrawModelFacesSection(ModelDocumentData doc)
    {
        ImGui.SeparatorText("Faces");
        ModelElementData? element = _modelSelectedElement;
        if (element == null)
        {
            ImGui.TextDisabled("No element selected.");
            return;
        }

        string[] textureCodes = doc.Textures.Select(texture => texture.Code).ToArray();
        for (int faceIndex = 0; faceIndex < 6; faceIndex++)
        {
            ImGui.PushID(faceIndex);
            try
            {
                ModelFaceData? face = element.Faces[faceIndex];
                bool present = face != null;
                if (ImGui.Checkbox($"##model-face-present", ref present))
                {
                    ModelBeginEdit();
                    if (present)
                    {
                        face = new ModelFaceData
                        {
                            Texture = textureCodes.Length > 0 ? textureCodes[0] : ""
                        };
                        element.Faces[faceIndex] = face;
                        ModelAutoUvFace(element, faceIndex);
                    }
                    else
                    {
                        element.Faces[faceIndex] = null;
                        face = null;
                    }
                    ModelMarkChanged();
                    ModelEndEdit(present ? "Add face" : "Remove face");
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Whether this face exists in the shape JSON.");
                }
                ImGui.SameLine();

                bool selected = ReferenceEquals(element, _modelSelectedElement) && _modelSelectedFace == faceIndex;
                ImGuiTreeNodeFlags headerFlags = selected ? ImGuiTreeNodeFlags.Selected : ImGuiTreeNodeFlags.None;
                bool openHeader = ImGui.CollapsingHeader($"{ModelFaceNames[faceIndex]}##model-face-header", headerFlags);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    _modelSelectedFace = faceIndex;
                }
                if (!openHeader || face == null)
                {
                    continue;
                }

                bool enabled = face.Enabled;
                if (ImGui.Checkbox("Enabled##model-face-enabled", ref enabled))
                {
                    ModelBeginEdit();
                    face.Enabled = enabled;
                    ModelMarkChanged();
                    ModelEndEdit("Toggle face enabled");
                }

                bool faceTextureKnown = textureCodes.Any(code => string.Equals(code, face.Texture, StringComparison.Ordinal));
                string texturePreview = string.IsNullOrEmpty(face.Texture)
                    ? "(none)"
                    : faceTextureKnown ? face.Texture : $"{face.Texture} (not in shape)";
                ImGui.SetNextItemWidth(-1f);
                if (ModelFilteredCombo($"##model-face-texture-{faceIndex}", texturePreview, textureCodes, out string pickedTexture, allowCustom: true, filterHint: "filter texture codes"))
                {
                    ModelBeginEdit();
                    face.Texture = pickedTexture;
                    ModelMarkChanged();
                    ModelEndEdit("Set face texture");
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Texture code for this face. Codes outside the shape (e.g. defined by the block type) can be typed into the filter.");
                }

                NVector4 uv = new(face.Uv[0], face.Uv[1], face.Uv[2], face.Uv[3]);
                bool uvChanged = ImGui.DragFloat4("UV##model-face-uv", ref uv, 0.05f);
                if (ImGui.IsItemActivated()) ModelBeginEdit();
                if (uvChanged)
                {
                    face.Uv[0] = uv.X;
                    face.Uv[1] = uv.Y;
                    face.Uv[2] = uv.Z;
                    face.Uv[3] = uv.W;
                    ModelMarkChanged();
                }
                if (ImGui.IsItemDeactivatedAfterEdit()) ModelEndEdit("Edit face UV");

                if (ImGui.SmallButton("Auto##model-face-autouv"))
                {
                    ModelBeginEdit();
                    ModelAutoUvFace(element, faceIndex);
                    ModelMarkChanged();
                    ModelEndEdit("Auto UV face");
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Flip U##model-face-flipu"))
                {
                    ModelBeginEdit();
                    (face.Uv[0], face.Uv[2]) = (face.Uv[2], face.Uv[0]);
                    ModelMarkChanged();
                    ModelEndEdit("Flip face U");
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Flip V##model-face-flipv"))
                {
                    ModelBeginEdit();
                    (face.Uv[1], face.Uv[3]) = (face.Uv[3], face.Uv[1]);
                    ModelMarkChanged();
                    ModelEndEdit("Flip face V");
                }

                int rotationIndex = (int)Math.Round(face.Rotation / 90f) & 3;
                bool customRotation = Math.Abs(face.Rotation - rotationIndex * 90f) > 0.001f;
                string[] rotationItems = customRotation
                    ? ["0", "90", "180", "270", $"{face.Rotation:0.##} (custom)"]
                    : ["0", "90", "180", "270"];
                int rotationCombo = customRotation ? 4 : rotationIndex;
                ImGui.SetNextItemWidth(120f);
                if (ImGui.Combo("Rotation##model-face-rotation", ref rotationCombo, rotationItems, rotationItems.Length) && rotationCombo < 4)
                {
                    ModelBeginEdit();
                    face.Rotation = rotationCombo * 90f;
                    ModelMarkChanged();
                    ModelEndEdit("Rotate face UV");
                }

                int glow = face.Glow;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(110f);
                bool glowChanged = ImGui.InputInt("Glow##model-face-glow", ref glow, 8);
                if (ImGui.IsItemActivated()) ModelBeginEdit();
                if (glowChanged)
                {
                    face.Glow = Math.Clamp(glow, 0, 255);
                    ModelMarkChanged();
                }
                if (ImGui.IsItemDeactivatedAfterEdit()) ModelEndEdit("Edit face glow");
            }
            finally
            {
                ImGui.PopID();
            }
        }
    }

    // Single bare letters and Ctrl+digit combos pass through to the game (E opens the
    // inventory, Q drops items, Ctrl+1..4 select backpack slots), so every shortcut here
    // uses a combination the vanilla hotkey table leaves unbound. Each one also has a
    // clickable equivalent; the toolbar Shortcuts button documents them.
    private void ModelHandleShortcuts()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        if (io.WantTextInput) return;

        if (io.KeyCtrl && !io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Z))
        {
            ModelUndo();
        }
        else if (io.KeyCtrl && !io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Y))
        {
            ModelRedo();
        }
        else if (io.KeyCtrl && !io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.D) && _modelSelectedElement != null)
        {
            ModelDuplicateSelectedElements();
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Delete) && _modelSelectedElement != null)
        {
            ModelDeleteSelectedElements();
        }
        else if (io.KeyCtrl && io.KeyShift && ImGui.IsKeyPressed(ImGuiKey._1))
        {
            _modelGizmoTool = ModelGizmoTool.None;
        }
        else if (io.KeyCtrl && io.KeyShift && ImGui.IsKeyPressed(ImGuiKey._2))
        {
            _modelGizmoTool = ModelGizmoTool.Move;
        }
        else if (io.KeyCtrl && io.KeyShift && ImGui.IsKeyPressed(ImGuiKey._3))
        {
            _modelGizmoTool = ModelGizmoTool.Resize;
        }
        else if (io.KeyCtrl && io.KeyShift && ImGui.IsKeyPressed(ImGuiKey._4))
        {
            _modelGizmoTool = ModelGizmoTool.Rotate;
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Home))
        {
            ModelFocusCameraOnSelection();
        }
    }

    private void ResetModelEditorLayout()
    {
        _modelLayout.Reset();
        _modelTreePanelFraction = 0.55f;
        _modelUvFitPending = true;
    }

    private void ModelSelectElement(ModelElementData? element, bool additive = false)
    {
        if (_modelDoc == null || element == null)
        {
            _modelSelectedElement = null;
            _modelSelectedElements.Clear();
            _modelSelectionOrder.Clear();
            _modelSelectedFace = -1;
            return;
        }

        if (!additive)
        {
            bool changed = !ReferenceEquals(_modelSelectedElement, element) || _modelSelectedElements.Count != 1 || !_modelSelectedElements.Contains(element);
            _modelSelectedElement = element;
            _modelSelectedElements.Clear();
            _modelSelectionOrder.Clear();
            _modelSelectedElements.Add(element);
            _modelSelectionOrder.Add(element);
            if (changed) _modelSelectedFace = -1;
            return;
        }

        if (_modelSelectedElements.Contains(element))
        {
            _modelSelectedElements.Remove(element);
            _modelSelectionOrder.RemoveAll(candidate => ReferenceEquals(candidate, element));
            if (ReferenceEquals(_modelSelectedElement, element))
            {
                _modelSelectedElement = _modelSelectionOrder.LastOrDefault();
                _modelSelectedFace = -1;
            }
        }
        else
        {
            _modelSelectedElements.Add(element);
            _modelSelectionOrder.RemoveAll(candidate => ReferenceEquals(candidate, element));
            _modelSelectionOrder.Add(element);
            _modelSelectedElement = element;
            _modelSelectedFace = -1;
        }

        if (_modelSelectedElement == null && _modelSelectionOrder.Count > 0)
        {
            _modelSelectedElement = _modelSelectionOrder[^1];
        }
    }

    private void ModelSelectElements(IEnumerable<ModelElementData> elements, ModelElementData? active)
    {
        _modelSelectedElements.Clear();
        _modelSelectionOrder.Clear();
        if (_modelDoc == null)
        {
            _modelSelectedElement = null;
            _modelSelectedFace = -1;
            return;
        }

        foreach (ModelElementData element in elements)
        {
            if (!_modelSelectedElements.Add(element)) continue;
            _modelSelectionOrder.Add(element);
        }

        _modelSelectedElement = active != null && _modelSelectedElements.Contains(active)
            ? active
            : _modelSelectionOrder.FirstOrDefault();
        _modelSelectedFace = -1;
    }

    private bool ModelIsElementSelected(ModelElementData element)
    {
        ModelPruneSelection();
        return _modelSelectedElements.Contains(element);
    }

    private List<ModelElementData> ModelSelectedElementsInDocument()
    {
        ModelPruneSelection();
        return [.. _modelSelectionOrder];
    }

    private void ModelPruneSelection()
    {
        if (_modelDoc == null)
        {
            _modelSelectedElement = null;
            _modelSelectedElements.Clear();
            _modelSelectionOrder.Clear();
            return;
        }

        HashSet<ModelElementData> live = _modelDoc.EnumerateElements().ToHashSet();
        _modelSelectionOrder.RemoveAll(element => !live.Contains(element));
        _modelSelectedElements.RemoveWhere(element => !live.Contains(element));

        if (_modelSelectedElement != null && !live.Contains(_modelSelectedElement))
        {
            _modelSelectedElement = _modelSelectionOrder.LastOrDefault();
            _modelSelectedFace = -1;
        }
        if (_modelSelectedElement != null && !_modelSelectedElements.Contains(_modelSelectedElement))
        {
            _modelSelectedElement = _modelSelectionOrder.LastOrDefault();
            _modelSelectedFace = -1;
        }
    }

    private List<ModelElementData> ModelEffectiveSelectedRoots()
    {
        List<ModelElementData> selected = ModelSelectedElementsInDocument();
        if (selected.Count == 0 && _modelSelectedElement != null)
        {
            selected.Add(_modelSelectedElement);
        }

        HashSet<ModelElementData> selectedSet = new(selected);
        List<ModelElementData> roots = [];
        foreach (ModelElementData element in selected)
        {
            bool hasSelectedAncestor = false;
            for (ModelElementData? parent = element.Parent; parent != null; parent = parent.Parent)
            {
                if (!selectedSet.Contains(parent)) continue;
                hasSelectedAncestor = true;
                break;
            }

            if (!hasSelectedAncestor) roots.Add(element);
        }

        return roots;
    }

    private void EnsureModelShapeIndex()
    {
        if (_modelShapeIndex != null) return;

        List<ModelShapeAssetEntry> index = [];
        try
        {
            foreach (IAsset asset in CollectToolAuthoredAssets("models", "shapes/"))
            {
                index.Add(new ModelShapeAssetEntry(asset.Location.Domain, asset.Location.Path, asset, Authored: true));
            }

            foreach (IAsset asset in _api.Assets.AllAssets.Values)
            {
                if (asset?.Location == null) continue;

                string path = asset.Location.Path.Replace('\\', '/');
                if (!path.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase) ||
                    !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                index.Add(new ModelShapeAssetEntry(asset.Location.Domain, path, asset));
            }

            index.Sort((left, right) =>
            {
                int byDomain = string.Compare(left.Domain, right.Domain, StringComparison.OrdinalIgnoreCase);
                if (byDomain != 0) return byDomain;
                int byPath = string.Compare(left.AssetPath, right.AssetPath, StringComparison.OrdinalIgnoreCase);
                return byPath != 0 ? byPath : left.Authored.CompareTo(right.Authored);
            });
        }
        catch (Exception exception)
        {
            _modelDiagnostics.Exception("Shape index build failed", exception);
        }

        _modelShapeIndex = index;
    }

    private void ModelRequestOpenDocument(ModelShapeAssetEntry entry)
    {
        if (_modelDoc?.Dirty == true)
        {
            _modelPendingOpenEntry = entry;
            _modelPendingNewDocument = false;
            _modelOpenDiscardPopup = true;
            return;
        }

        ModelOpenDocument(entry);
    }

    private void ModelRequestNewDocument()
    {
        if (_modelDoc?.Dirty == true)
        {
            _modelPendingOpenEntry = null;
            _modelPendingNewDocument = true;
            _modelOpenDiscardPopup = true;
            return;
        }

        ModelCreateNewDocument();
    }

    private void DrawModelDiscardPopup()
    {
        const string popupId = "Discard model changes?";
        if (_modelOpenDiscardPopup)
        {
            ImGui.OpenPopup(popupId);
            _modelOpenDiscardPopup = false;
        }

        bool open = true;
        if (!ImGui.BeginPopupModal(popupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped($"'{_modelDoc?.DisplayPath ?? "current shape"}' has unsaved changes.");
        ImGui.TextWrapped("Discard them and continue?");
        if (ImGui.Button("Discard changes##model-discard-yes"))
        {
            if (_modelPendingNewDocument)
            {
                ModelCreateNewDocument();
            }
            else if (_modelPendingOpenEntry != null)
            {
                ModelOpenDocument(_modelPendingOpenEntry);
            }
            _modelPendingOpenEntry = null;
            _modelPendingNewDocument = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep editing##model-discard-no"))
        {
            _modelPendingOpenEntry = null;
            _modelPendingNewDocument = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void ModelOpenDocument(ModelShapeAssetEntry entry)
    {
        string text;
        try
        {
            text = entry.Asset.ToText();
        }
        catch (Exception exception)
        {
            _modelDiagnostics.Exception($"Could not read {entry.Display}", exception);
            _modelStatus = $"Could not read {entry.Display}: {exception.Message}";
            return;
        }

        if (!ModelTryParseDocument(text, entry.Domain, entry.AssetPath, isNew: false, out ModelDocumentData? doc, out string error) || doc == null)
        {
            _modelDiagnostics.Error($"Could not parse {entry.Display}", error);
            _modelStatus = $"Could not parse {entry.Display}: {error}";
            return;
        }

        doc.FromAuthoredFile = entry.Authored;
        ModelSetDocument(doc);
        _modelStatus = entry.Authored ? $"Opened authored copy of {entry.Display}." : $"Opened {entry.Display}.";
    }

    private void ModelCreateNewDocument()
    {
        ModelDocumentData? doc = null;
        try
        {
            IAsset? template = _api.Assets.TryGet(AssetLocation.Create(ModelNewDocumentTemplateLocation, "game"));
            if (template != null &&
                ModelTryParseDocument(template.ToText(), "game", "shapes/block/new-shape.json", isNew: true, out ModelDocumentData? parsed, out _))
            {
                doc = parsed;
            }
        }
        catch (Exception exception)
        {
            LoggerUtil.Verbose(_api, this, $"Model template load failed: {exception.Message}");
        }

        doc ??= ModelBuildFallbackDocument();
        doc.IsNew = true;
        doc.Domain = "game";
        doc.AssetPath = "shapes/block/new-shape.json";
        doc.SourceText = "";
        if (doc.Textures.Count == 0)
        {
            doc.Textures.Add(new ModelTextureEntry { Code = "all", Path = "" });
        }

        ModelSetDocument(doc);
        _modelStatus = "Created new shape document.";
    }

    private static ModelDocumentData ModelBuildFallbackDocument()
    {
        ModelDocumentData doc = new();
        ModelElementData cube = new()
        {
            Name = "Cube1",
            From = [0, 0, 0],
            To = [16, 16, 16]
        };
        for (int face = 0; face < 6; face++)
        {
            cube.Faces[face] = new ModelFaceData { Texture = "all", Uv = [0f, 0f, 16f, 16f] };
        }
        doc.Roots.Add(cube);
        doc.Textures.Add(new ModelTextureEntry { Code = "all", Path = "" });
        return doc;
    }

    private void ModelSetDocument(ModelDocumentData doc)
    {
        _modelDoc = doc;
        ModelSelectElement(doc.Roots.FirstOrDefault());
        _modelSelectedTextureCode = doc.Textures.FirstOrDefault()?.Code ?? "";
        _modelUndoStack.Clear();
        _modelRedoStack.Clear();
        _modelPendingEditSnapshot = null;
        _modelPreviewDirty = true;
        _modelJsonBufferStale = true;
        _modelReparentSource = null;
        _modelPrimitivePreviewDirty = true;
        ModelResetCameraToFit();
    }

    private void ModelAddElement(ModelElementData? parent)
    {
        if (_modelDoc == null) return;

        ModelBeginEdit();
        string defaultTexture = _modelDoc.Textures.FirstOrDefault()?.Code ?? "";
        ModelElementData element = new()
        {
            Name = ModelGenerateElementName(parent == null ? "Cube" : parent.Name + "Child"),
            From = [0, 0, 0],
            To = [4, 4, 4],
            Parent = parent
        };
        for (int face = 0; face < 6; face++)
        {
            element.Faces[face] = new ModelFaceData { Texture = defaultTexture };
            ModelAutoUvFace(element, face);
        }

        (parent?.Children ?? _modelDoc.Roots).Add(element);
        ModelSelectElement(element);
        ModelMarkChanged();
        ModelEndEdit("Add element");
        _modelStatus = $"Added element {element.Name}.";
    }

    private string ModelGenerateElementName(string baseName)
    {
        if (_modelDoc == null) return baseName;

        HashSet<string> names = new(_modelDoc.EnumerateElements().Select(element => element.Name), StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;

        for (int counter = 2; counter < 10000; counter++)
        {
            string candidate = $"{baseName}{counter}";
            if (!names.Contains(candidate)) return candidate;
        }

        return baseName + Guid.NewGuid().ToString("N")[..6];
    }

    private void ModelDeleteElement(ModelElementData element)
    {
        if (_modelDoc == null) return;

        ModelBeginEdit();
        List<ModelElementData> siblings = element.Parent?.Children ?? _modelDoc.Roots;
        if (!siblings.Remove(element))
        {
            ModelCancelEdit();
            return;
        }

        if (_modelSelectedElement != null && element.EnumerateSubtree().Contains(_modelSelectedElement))
        {
            ModelSelectElement(element.Parent ?? _modelDoc.Roots.FirstOrDefault());
        }
        if (ReferenceEquals(_modelReparentSource, element)) _modelReparentSource = null;
        ModelMarkChanged();
        ModelEndEdit("Delete element");
        _modelStatus = $"Deleted element {element.Name}.";
    }

    private void ModelDeleteSelectedElements()
    {
        if (_modelDoc == null) return;

        List<ModelElementData> targets = ModelEffectiveSelectedRoots();
        if (targets.Count == 0) return;
        if (targets.Count == 1)
        {
            ModelDeleteElement(targets[0]);
            return;
        }

        ModelBeginEdit();
        int removed = 0;
        foreach (ModelElementData element in targets)
        {
            List<ModelElementData> siblings = element.Parent?.Children ?? _modelDoc.Roots;
            if (siblings.Remove(element)) removed++;
            if (ReferenceEquals(_modelReparentSource, element)) _modelReparentSource = null;
        }

        ModelSelectElement(_modelDoc.Roots.FirstOrDefault());
        if (removed == 0)
        {
            ModelCancelEdit();
            return;
        }

        ModelMarkChanged();
        ModelEndEdit("Delete selected elements");
        _modelStatus = $"Deleted {removed} selected element(s).";
    }

    private void ModelDuplicateElement(ModelElementData element)
    {
        if (_modelDoc == null) return;

        ModelBeginEdit();
        ModelElementData clone = element.CloneSubtree();
        clone.Parent = element.Parent;
        clone.Name = ModelGenerateElementName(element.Name);
        List<ModelElementData> siblings = element.Parent?.Children ?? _modelDoc.Roots;
        siblings.Insert(siblings.IndexOf(element) + 1, clone);
        ModelSelectElement(clone);
        ModelMarkChanged();
        ModelEndEdit("Duplicate element");
        _modelStatus = $"Duplicated {element.Name} as {clone.Name}.";
    }

    private void ModelDuplicateSelectedElements()
    {
        if (_modelDoc == null || _modelSelectedElement == null) return;

        List<ModelElementData> targets = ModelEffectiveSelectedRoots();
        if (targets.Count <= 1)
        {
            ModelDuplicateElement(_modelSelectedElement);
            return;
        }

        ModelBeginEdit();
        List<ModelElementData> clones = [];
        foreach (ModelElementData element in targets)
        {
            ModelElementData clone = element.CloneSubtree();
            clone.Parent = element.Parent;
            clone.Name = ModelGenerateElementName(element.Name);
            List<ModelElementData> siblings = element.Parent?.Children ?? _modelDoc.Roots;
            int insertIndex = siblings.IndexOf(element);
            if (insertIndex < 0) continue;
            siblings.Insert(insertIndex + 1, clone);
            clones.Add(clone);
        }

        if (clones.Count == 0)
        {
            ModelCancelEdit();
            return;
        }

        ModelSelectElements(clones, clones[^1]);
        ModelMarkChanged();
        ModelEndEdit("Duplicate selected elements");
        _modelStatus = $"Duplicated {clones.Count} selected element(s).";
    }

    private void ModelMirrorSelectedElements(int axis)
    {
        if (_modelDoc == null) return;

        List<ModelElementData> targets = ModelEffectiveSelectedRoots();
        if (targets.Count == 0) return;

        ModelBeginEdit();
        foreach (ModelElementData element in targets)
        {
            ModelMirrorElementSubtree(element, axis);
        }
        ModelMarkChanged();
        ModelEndEdit($"Mirror selected on {ModelAxisName(axis)}");
        _modelStatus = $"Mirrored {targets.Count} selected top-level element(s) around {ModelAxisName(axis)} origin.";
    }

    private static string ModelAxisName(int axis) => axis switch
    {
        0 => "X",
        1 => "Y",
        _ => "Z"
    };

    private void ModelMirrorElementSubtree(ModelElementData element, int axis)
    {
        double oldFrom = element.From[axis];
        double oldTo = element.To[axis];
        element.From[axis] = -oldTo;
        element.To[axis] = -oldFrom;
        if (element.RotationOrigin != null)
        {
            element.RotationOrigin[axis] = -element.RotationOrigin[axis];
        }

        switch (axis)
        {
            case 0:
                element.RotationY = ModelWrapDegrees(-element.RotationY);
                element.RotationZ = ModelWrapDegrees(-element.RotationZ);
                (element.Faces[1], element.Faces[3]) = (element.Faces[3], element.Faces[1]);
                break;
            case 1:
                element.RotationX = ModelWrapDegrees(-element.RotationX);
                element.RotationZ = ModelWrapDegrees(-element.RotationZ);
                (element.Faces[4], element.Faces[5]) = (element.Faces[5], element.Faces[4]);
                break;
            default:
                element.RotationX = ModelWrapDegrees(-element.RotationX);
                element.RotationY = ModelWrapDegrees(-element.RotationY);
                (element.Faces[0], element.Faces[2]) = (element.Faces[2], element.Faces[0]);
                break;
        }

        for (int child = 0; child < element.Children.Count; child++)
        {
            ModelMirrorElementSubtree(element.Children[child], axis);
        }
    }

    private void ModelMoveElement(ModelElementData element, int direction)
    {
        if (_modelDoc == null) return;

        List<ModelElementData> siblings = element.Parent?.Children ?? _modelDoc.Roots;
        int index = siblings.IndexOf(element);
        int target = index + direction;
        if (index < 0 || target < 0 || target >= siblings.Count) return;

        ModelBeginEdit();
        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        ModelMarkChanged();
        ModelEndEdit("Reorder element");
    }

    private void ModelReparentElement(ModelElementData element, ModelElementData? newParent)
    {
        if (_modelDoc == null) return;
        if (newParent != null && element.EnumerateSubtree().Contains(newParent))
        {
            _modelStatus = "Cannot reparent an element into its own subtree.";
            return;
        }
        if (ReferenceEquals(element.Parent, newParent)) return;

        ModelBeginEdit();
        bool compensated = ModelTryCompensateReparentOffsets(element, newParent);
        List<ModelElementData> oldSiblings = element.Parent?.Children ?? _modelDoc.Roots;
        oldSiblings.Remove(element);
        element.Parent = newParent;
        (newParent?.Children ?? _modelDoc.Roots).Add(element);
        ModelMarkChanged();
        ModelEndEdit("Reparent element");
        _modelStatus = compensated
            ? $"Reparented {element.Name}; coordinates were adjusted to keep its position."
            : $"Reparented {element.Name}. Rotated parents prevent coordinate compensation; from/to kept as-is.";
    }

    private bool ModelTryCompensateReparentOffsets(ModelElementData element, ModelElementData? newParent)
    {
        static bool ChainHasRotation(ModelElementData? node)
        {
            for (ModelElementData? current = node; current != null; current = current.Parent)
            {
                if (Math.Abs(current.RotationX) > 0.0001 || Math.Abs(current.RotationY) > 0.0001 || Math.Abs(current.RotationZ) > 0.0001)
                {
                    return true;
                }
            }
            return false;
        }

        static double[] ChainOffset(ModelElementData? node)
        {
            double[] offset = new double[3];
            for (ModelElementData? current = node; current != null; current = current.Parent)
            {
                offset[0] += current.From[0];
                offset[1] += current.From[1];
                offset[2] += current.From[2];
            }
            return offset;
        }

        if (ChainHasRotation(element.Parent) || ChainHasRotation(newParent)) return false;

        double[] oldOffset = ChainOffset(element.Parent);
        double[] newOffset = ChainOffset(newParent);
        for (int axis = 0; axis < 3; axis++)
        {
            double delta = oldOffset[axis] - newOffset[axis];
            element.From[axis] += delta;
            element.To[axis] += delta;
            if (element.RotationOrigin != null) element.RotationOrigin[axis] += delta;
        }

        return true;
    }

    private void ModelAutoUvFace(ModelElementData element, int faceIndex)
    {
        ModelFaceData? face = element.Faces[faceIndex];
        if (face == null) return;

        (double width, double height) = faceIndex switch
        {
            0 or 2 => (element.SizeX, element.SizeY),
            1 or 3 => (element.SizeZ, element.SizeY),
            _ => (element.SizeX, element.SizeZ)
        };
        face.Uv[0] = 0f;
        face.Uv[1] = 0f;
        face.Uv[2] = (float)Math.Max(0.0, width);
        face.Uv[3] = (float)Math.Max(0.0, height);
    }

    private void ModelMarkChanged()
    {
        if (_modelDoc == null) return;

        _modelDoc.Dirty = true;
        _modelPreviewDirty = true;
        _modelJsonBufferStale = true;
        _modelLiveChangedAtMs = _api.World?.ElapsedMilliseconds ?? 0;
        _modelLiveDirty = true;
    }

    private void ModelBeginEdit()
    {
        if (_modelDoc == null || _modelPendingEditSnapshot != null) return;

        try
        {
            _modelPendingEditSnapshot = ModelSerializeDocument(_modelDoc, includeInvisible: true, indented: false);
        }
        catch (Exception exception)
        {
            _modelDiagnostics.Exception("History snapshot failed", exception);
        }
    }

    private void ModelCancelEdit()
    {
        _modelPendingEditSnapshot = null;
    }

    private void ModelEndEdit(string label)
    {
        if (_modelDoc == null || _modelPendingEditSnapshot == null)
        {
            _modelPendingEditSnapshot = null;
            return;
        }

        try
        {
            string current = ModelSerializeDocument(_modelDoc, includeInvisible: true, indented: false);
            if (!string.Equals(current, _modelPendingEditSnapshot, StringComparison.Ordinal))
            {
                _modelUndoStack.Add(new ModelHistoryEntry(label, _modelPendingEditSnapshot, ModelGetSelectionPath(), _modelSelectedFace, ModelGetSelectionPaths()));
                if (_modelUndoStack.Count > ModelHistoryLimit)
                {
                    _modelUndoStack.RemoveAt(0);
                }
                _modelRedoStack.Clear();
            }
        }
        catch (Exception exception)
        {
            _modelDiagnostics.Exception("History commit failed", exception);
        }
        finally
        {
            _modelPendingEditSnapshot = null;
        }
    }

    private void ModelUndo()
    {
        if (_modelDoc == null || _modelUndoStack.Count == 0) return;

        ModelHistoryEntry entry = _modelUndoStack[^1];
        _modelUndoStack.RemoveAt(_modelUndoStack.Count - 1);
        try
        {
            string current = ModelSerializeDocument(_modelDoc, includeInvisible: true, indented: false);
            _modelRedoStack.Add(new ModelHistoryEntry(entry.Label, current, ModelGetSelectionPath(), _modelSelectedFace, ModelGetSelectionPaths()));
            ModelRestoreFromHistory(entry);
            _modelStatus = $"Undid: {entry.Label}.";
        }
        catch (Exception exception)
        {
            _modelDiagnostics.Exception("Undo failed", exception);
        }
    }

    private void ModelRedo()
    {
        if (_modelDoc == null || _modelRedoStack.Count == 0) return;

        ModelHistoryEntry entry = _modelRedoStack[^1];
        _modelRedoStack.RemoveAt(_modelRedoStack.Count - 1);
        try
        {
            string current = ModelSerializeDocument(_modelDoc, includeInvisible: true, indented: false);
            _modelUndoStack.Add(new ModelHistoryEntry(entry.Label, current, ModelGetSelectionPath(), _modelSelectedFace, ModelGetSelectionPaths()));
            ModelRestoreFromHistory(entry);
            _modelStatus = $"Redid: {entry.Label}.";
        }
        catch (Exception exception)
        {
            _modelDiagnostics.Exception("Redo failed", exception);
        }
    }

    private void ModelRestoreFromHistory(ModelHistoryEntry entry)
    {
        if (_modelDoc == null) return;

        if (!ModelTryParseDocument(entry.Json, _modelDoc.Domain, _modelDoc.AssetPath, _modelDoc.IsNew, out ModelDocumentData? restored, out string error) || restored == null)
        {
            _modelDiagnostics.Error("History restore failed", error);
            return;
        }

        restored.SourceText = _modelDoc.SourceText;
        restored.Dirty = true;
        restored.FromAuthoredFile = _modelDoc.FromAuthoredFile;
        _modelDoc = restored;
        ModelSelectElements(ModelResolveSelectionPaths(entry.SelectionPaths), ModelResolveSelectionPath(entry.SelectionPath));
        if (_modelSelectedElement == null)
        {
            ModelSelectElement(restored.Roots.FirstOrDefault());
        }
        _modelSelectedFace = entry.SelectedFace;
        _modelPreviewDirty = true;
        _modelJsonBufferStale = true;
        _modelLiveDirty = true;
        _modelLiveChangedAtMs = _api.World?.ElapsedMilliseconds ?? 0;
        _modelReparentSource = null;
    }

    private int[]? ModelGetSelectionPath()
    {
        if (_modelDoc == null || _modelSelectedElement == null) return null;

        List<int> path = [];
        ModelElementData? current = _modelSelectedElement;
        while (current != null)
        {
            List<ModelElementData> siblings = current.Parent?.Children ?? _modelDoc.Roots;
            int index = siblings.IndexOf(current);
            if (index < 0) return null;
            path.Insert(0, index);
            current = current.Parent;
        }

        return [.. path];
    }

    private int[][] ModelGetSelectionPaths()
    {
        List<int[]> paths = [];
        foreach (ModelElementData element in ModelSelectedElementsInDocument())
        {
            int[]? path = ModelGetElementPath(element);
            if (path != null) paths.Add(path);
        }

        return [.. paths];
    }

    private int[]? ModelGetElementPath(ModelElementData element)
    {
        if (_modelDoc == null) return null;

        List<int> path = [];
        ModelElementData? current = element;
        while (current != null)
        {
            List<ModelElementData> siblings = current.Parent?.Children ?? _modelDoc.Roots;
            int index = siblings.IndexOf(current);
            if (index < 0) return null;
            path.Insert(0, index);
            current = current.Parent;
        }

        return [.. path];
    }

    private ModelElementData? ModelResolveSelectionPath(int[]? path)
    {
        if (_modelDoc == null || path == null || path.Length == 0) return _modelDoc?.Roots.FirstOrDefault();

        List<ModelElementData> level = _modelDoc.Roots;
        ModelElementData? current = null;
        foreach (int index in path)
        {
            if (index < 0 || index >= level.Count) return current ?? _modelDoc.Roots.FirstOrDefault();
            current = level[index];
            level = current.Children;
        }

        return current;
    }

    private IEnumerable<ModelElementData> ModelResolveSelectionPaths(int[][]? paths)
    {
        if (paths == null) yield break;
        HashSet<ModelElementData> seen = [];
        foreach (int[] path in paths)
        {
            ModelElementData? element = ModelResolveSelectionPath(path);
            if (element == null || !seen.Add(element)) continue;
            yield return element;
        }
    }
}
