using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private static readonly string[] ModelFaceNames = ["north", "east", "south", "west", "up", "down"];
    private static readonly string[] ModelGeneratorToolLabels = ["None", "Prism helper", "Creature generator", "PlayerModel generator", "Clothing generator", "Tool / weapon generator"];
    private const int ModelBrowserMaxVisibleEntries = 600;
    private const int ModelHistoryLimit = 120;
    private const long ModelHistoryCharacterBudget = 16_000_000;
    private const int ModelCutMaxPiecesPerElement = 512;
    private const string ModelNewDocumentTemplateLocation = "game:shapes/block/basic/cube.json";

    private enum ModelGizmoTool
    {
        None,
        Move,
        Resize,
        Rotate,
        Cut,
        Chisel,
        Extrude,
        Inset,
        Subdivide
    }

    private enum ModelCutOrientation
    {
        Auto,
        X,
        Y,
        Z
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
        public ModelNonCuboidData? NonCuboid;
        // Generator-only profile metadata. Materialized into NonCuboid before a preview is committed;
        // intentionally omitted from cloning and the hand-written shape serializer.
        public ModelGeneratedMeshSpec? GeneratedMeshSpec;
        public List<ModelElementData> Children = [];
        public ModelElementData? Parent;
        public JObject? Extra;
        public bool Visible = true;

        public double SizeX => To[0] - From[0];
        public double SizeY => To[1] - From[1];
        public double SizeZ => To[2] - From[2];

        public ModelElementData CloneShallow()
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
                NonCuboid = NonCuboid?.Clone(),
                Extra = (JObject?)Extra?.DeepClone(),
                Visible = Visible
            };
            for (int face = 0; face < 6; face++)
            {
                clone.Faces[face] = Faces[face]?.Clone();
            }
            return clone;
        }

        public ModelElementData CloneSubtree()
        {
            ModelElementData clone = CloneShallow();
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
        public string RecoveryKey = $"model-session:{Guid.NewGuid():N}";

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

    private sealed record ModelShapeAssetEntry(string Domain, string AssetPath, IAsset Asset, bool Authored = false, bool MeshLib = false)
    {
        public string Display => $"{Domain}:{AssetPath}";
        public string SearchText { get; } = $"{Domain}:{AssetPath}{(Authored ? " authored" : "")}{(MeshLib ? " meshlib noncuboid" : "")}".ToLowerInvariant();
    }

    private sealed record ModelHistoryEntry(
        string Label,
        string Json,
        int[]? SelectionPath,
        int SelectedFace,
        int[][]? SelectionPaths = null,
        ModelMeshSelectionMode MeshSelectionMode = ModelMeshSelectionMode.Face,
        int[]? MeshVertices = null,
        int[][]? MeshEdges = null,
        int[]? MeshFaces = null);

    private sealed record ModelGizmoDragElementState(
        ModelElementData Element,
        double[] From,
        double[] To,
        double[]? RotationOrigin,
        double RotationX,
        double RotationY,
        double RotationZ,
        double[][]? MeshVertices);

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
    private int _modelCutPartsX = 2;
    private int _modelCutPartsY = 1;
    private int _modelCutPartsZ = 1;
    private readonly List<ModelHistoryEntry> _modelUndoStack = [];
    private readonly List<ModelHistoryEntry> _modelRedoStack = [];
    private string? _modelPendingEditSnapshot;
    private bool _modelPreviewDirty;
    private ModelGizmoTool _modelGizmoTool = ModelGizmoTool.Move;
    private ModelCutOrientation _modelCutOrientation = ModelCutOrientation.Auto;
    private bool _modelSnapEnabled = true;
    private float _modelSnapMoveUnits = 0.5f;
    private float _modelSnapRotateDegrees = 5f;
    private string _modelChiselTexture = "";
    private float _modelChiselSize = 1f;
    private int _modelArrowNudgePlane;
    private int _modelWheelNudgeAxis = 2;
    private ModelShapeAssetEntry? _modelPendingOpenEntry;
    private bool _modelPendingNewDocument;
    private bool _modelOpenDiscardPopup;
    private ModelElementData? _modelReparentSource;
    private ModelElementData? _modelDragDropElement;
    private ModelShapeAssetEntry? _modelDragDropShapeEntry;
    private ModelShapeAssetEntry? _modelBrowserFileActionEntry;
    private string _modelBrowserPendingFilePopup = "";
    private string _modelBrowserRenameName = "";
    private string _modelBrowserMoveFolder = "";
    private string _modelTreeFilter = "";
    private HashSet<ModelElementData>? _modelTreeFilterMatches;
    private readonly Dictionary<string, string> _modelComboFilters = new(StringComparer.Ordinal);
    private List<string>? _modelTextureAssetIndex;
    private List<string>? _modelStepParentNameIndex;
    private static readonly string[] ModelNudgePlaneLabels = ["XY", "XZ", "YZ"];
    private static readonly string[] ModelAxisLabels = ["X", "Y", "Z"];

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

        DrawModelModePicker();

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
        DrawModelGeneratorToolPicker("##model-generator-toolbar");

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
        DrawModelMeshToolbar();

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

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextDisabled("Nudge");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f);
        if (ImGui.Combo("Arrows##model-arrow-nudge-plane", ref _modelArrowNudgePlane, ModelNudgePlaneLabels, ModelNudgePlaneLabels.Length))
        {
            _modelArrowNudgePlane = Math.Clamp(_modelArrowNudgePlane, 0, ModelNudgePlaneLabels.Length - 1);
        }
        if (ImGui.IsItemHovered())
        {
            (int horizontalAxis, int verticalAxis) = ModelArrowNudgeAxes();
            ImGui.SetTooltip($"Arrow nudge plane. Left/right move {ModelAxisLabel(horizontalAxis)}; up/down move {ModelAxisLabel(verticalAxis)}.");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(58f);
        if (ImGui.Combo("Wheel##model-wheel-nudge-axis", ref _modelWheelNudgeAxis, ModelAxisLabels, ModelAxisLabels.Length))
        {
            _modelWheelNudgeAxis = Math.Clamp(_modelWheelNudgeAxis, 0, ModelAxisLabels.Length - 1);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Ctrl+mouse wheel nudges selected elements on this axis.");
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

        DrawModelChiselToolbar();
        ImGui.Separator();
    }

    private void DrawModelGeneratorToolPicker(string id)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Generator");
        ImGui.SameLine();
        int tool = _modelPrimitiveWindowOpen ? 1 : _modelCreatureWindowOpen ? 2 : _playerModelWindowOpen ? 3 : _clothingWindowOpen ? 4 : _weaponWindowOpen ? 5 : 0;
        ImGui.SetNextItemWidth(160f * _devToolsUiScale);
        if (ImGui.Combo(id, ref tool, ModelGeneratorToolLabels, ModelGeneratorToolLabels.Length))
        {
            SetModelGeneratorTool(tool);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Show a model generator inside the main editor.");
        }
    }

    private void SetModelGeneratorTool(int tool)
    {
        bool primitiveOpen = tool == 1;
        bool creatureOpen = tool == 2;
        bool playerModelOpen = tool == 3;
        bool clothingOpen = tool == 4;
        bool weaponOpen = tool == 5;
        if (primitiveOpen && !_modelPrimitiveWindowOpen)
        {
            _modelPrimitivePreviewDirty = true;
        }
        if (creatureOpen && !_modelCreatureWindowOpen)
        {
            _modelCreaturePreviewDirty = true;
        }
        if (playerModelOpen && !_playerModelWindowOpen)
        {
            _playerModelPreviewDirty = true;
        }
        if (clothingOpen && !_clothingWindowOpen)
        {
            _clothingPreviewDirty = true;
        }
        if (weaponOpen && !_weaponWindowOpen)
        {
            _weaponPreviewDirty = true;
        }

        _modelPrimitiveWindowOpen = primitiveOpen;
        _modelCreatureWindowOpen = creatureOpen;
        _playerModelWindowOpen = playerModelOpen;
        _clothingWindowOpen = clothingOpen;
        _weaponWindowOpen = weaponOpen;
    }

    // Floating tool window (drawn on top of the editor) rather than an inline drawer that ate a large slice
    // of the Models tab. Drawn after the main window via DrawDevToolsGeneratorOverlays.
    private void DrawModelGeneratorOverlay()
    {
        if (!_modelPrimitiveWindowOpen && !_modelCreatureWindowOpen && !_playerModelWindowOpen && !_clothingWindowOpen && !_weaponWindowOpen) return;

        bool open = true;
        // The visible label switches with the active tool while the id after '###' stays fixed, so the window
        // keeps its position and size when you toggle between the tools.
        string title = _modelCreatureWindowOpen
            ? "Creature generator###model-generator-overlay"
            : _playerModelWindowOpen
                ? "PlayerModel generator###model-generator-overlay"
                : _clothingWindowOpen
                    ? "Clothing generator###model-generator-overlay"
                    : _weaponWindowOpen
                        ? "Tool / weapon generator###model-generator-overlay"
                        : "Prism helper###model-generator-overlay";
        if (BeginDevToolsFloatingTool(title, ref open, new NVector2(480f, 580f)))
        {
            DrawModelGeneratorToolPicker("##model-generator-overlay-picker");
            ImGui.Separator();
            if (_modelPrimitiveWindowOpen)
            {
                DrawModelPrimitivePanel();
            }
            else if (_modelCreatureWindowOpen)
            {
                DrawModelCreaturePanel();
            }
            else if (_playerModelWindowOpen)
            {
                DrawPlayerModelPanel();
            }
            else if (_clothingWindowOpen)
            {
                DrawClothingPanel();
            }
            else if (_weaponWindowOpen)
            {
                DrawWeaponPanel();
            }
        }
        ImGui.End();

        if (!open)
        {
            SetModelGeneratorTool(0);
        }
    }

    private void DrawModelChiselToolbar()
    {
        if (_modelDoc == null || _modelGizmoTool != ModelGizmoTool.Chisel) return;

        List<string> textureCodes = _modelDoc.Textures.Select(texture => texture.Code).ToList();
        if (string.IsNullOrWhiteSpace(_modelChiselTexture))
        {
            _modelChiselTexture = textureCodes.FirstOrDefault() ?? "";
        }

        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Chisel texture");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f);
        if (ModelFilteredCombo("Place texture##model-chisel-texture", _modelChiselTexture, textureCodes, out string pickedTexture, allowCustom: true, filterHint: "filter texture codes"))
        {
            _modelChiselTexture = pickedTexture.Trim();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Texture code used by newly added chisel microblocks. Same-texture neighbors merge; different textures stay separate.");
        }
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Size");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(88f);
        if (ImGui.DragFloat("##model-chisel-size", ref _modelChiselSize, 0.03125f, 0.0625f, 8f, "%.4g u"))
        {
            _modelChiselSize = Math.Clamp(_modelChiselSize, 0.0625f, 8f);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Placed/removed chisel cell size in shape units. 1 unit = 1/16 block; smaller values allow finer model details.");
        }
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
        if (ImGui.SmallButton("Cut##model-selection-cut"))
        {
            ModelCutSelectedElements(_modelCutPartsX, _modelCutPartsY, _modelCutPartsZ);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Split selected cuboids into separate elements using the inspector X/Y/Z piece counts.");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy##model-selection-copy"))
        {
            ModelCopySelectedElementsToClipboard();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Copy selected element subtrees to the clipboard as shape JSON (Ctrl+C). Paste into this or another shape.");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Delete##model-selection-delete"))
        {
            ModelDeleteSelectedElements();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Center pivot##model-selection-center-pivot"))
        {
            ModelCenterPivotSelectedElements();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Move each selected element's rotation origin to its box center without moving the rendered box.");
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

        ImGui.SameLine();
        if (!hasSelection) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Isolate##model-selection-isolate"))
        {
            ModelIsolateSelectedElements();
        }
        if (!hasSelection) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Hide everything except the selected subtrees in the viewport. Hidden elements still save.");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Show all##model-selection-show-all"))
        {
            ModelShowAllElements();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Paste##model-selection-paste"))
        {
            ModelPasteElementsFromClipboard(_modelSelectedElement?.Parent);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Paste elements from clipboard shape JSON next to the selection, or at root level (Ctrl+V).");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Rename...##model-selection-rename"))
        {
            ImGui.OpenPopup("##model-batch-rename-popup");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Find/replace text in element names (selection or whole shape).");
        }
        DrawModelBatchRenamePopup();
    }

    private void DrawModelShortcutsPopup()
    {
        if (!ImGui.BeginPopup("##model-shortcuts-popup")) return;

        try
        {
            ImGui.SeparatorText("Keyboard");
            ImGui.TextUnformatted(ModelIsMeshLibMode
                ? "Ctrl+Shift+1..7   Select / Move / Resize / Rotate / Extrude / Inset / Subdivide"
                : "Ctrl+Shift+1..6   Select / Move / Resize / Rotate / Cut / Chisel tool");
            ImGui.TextUnformatted("Ctrl+Z / Ctrl+Y   Undo / Redo");
            ImGui.TextUnformatted("Ctrl+D            Duplicate selected element");
            ImGui.TextUnformatted("Ctrl+C            Copy selected elements as JSON");
            ImGui.TextUnformatted("Ctrl+V            Paste elements from clipboard JSON");
            ImGui.TextUnformatted("Delete            Delete selected element");
            ImGui.TextUnformatted("Arrow keys        Nudge on selected arrow plane");
            ImGui.TextUnformatted("Ctrl+Mouse wheel  Nudge on selected wheel axis");
            ImGui.TextUnformatted("Home              Focus camera on selection");
            ImGui.TextUnformatted("Shift / Alt       Coarse / fine nudge or drag");
            ImGui.TextDisabled("Plain letter keys are not used; the game still receives them.");

            ImGui.SeparatorText("Viewport mouse");
            ImGui.TextUnformatted("Left click        Select element / drag gizmo");
            ImGui.TextUnformatted("Ctrl+Left click   Toggle element in multi-selection");
            ImGui.TextUnformatted("Chisel mode       Left click adds, right click removes");
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

            ImGui.TextDisabled($"{filtered.Count} / {index.Count} shapes");
            ImGui.BeginChild("##model-browser-list", new NVector2(0f, 0f), false);
            try
            {
                bool showDomain = string.IsNullOrWhiteSpace(_modelBrowserDomain) &&
                    filtered.Select(entry => entry.Domain).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() > 1;
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
                    string label = ModelBrowserEntryLabel(entry, showDomain);
                    if (ImGui.Selectable($"{label}##model-asset-{shown}", selected) && !selected)
                    {
                        ModelRequestOpenDocument(entry);
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(entry.Authored
                            ? $"Authored: {entry.Display}\nDrag onto the element tree to import it into the open shape."
                            : $"{entry.Display}\nDrag onto the element tree to import it into the open shape.");
                    }
                    DrawModelBrowserEntryDragSource(entry, label);
                    DrawModelBrowserEntryContextMenu(entry, shown);
                }
                DrawModelBrowserFileActionPopups();
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

    private static string ModelBrowserEntryLabel(ModelShapeAssetEntry entry, bool showDomain)
    {
        string path = entry.AssetPath.Replace('\\', '/');
        if (path.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["shapes/".Length..];
        }

        string label = showDomain ? $"{entry.Domain}:{path}" : path;
        if (entry.MeshLib) label += "  M";
        return entry.Authored ? $"{label}  A" : label;
    }

    private static bool ModelShapeEntriesMatch(ModelShapeAssetEntry left, ModelShapeAssetEntry right)
    {
        return left.Authored == right.Authored &&
            string.Equals(left.Domain, right.Domain, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.AssetPath, right.AssetPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string ModelImportSourceLabel(ModelShapeAssetEntry entry)
    {
        return entry.Authored ? $"{entry.Display} [authored]" : entry.Display;
    }

    private void DrawModelBrowserEntryDragSource(ModelShapeAssetEntry entry, string label)
    {
        if (_modelDoc == null) return;

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 6f))
        {
            _modelDragDropShapeEntry ??= entry;
        }

        if (_modelDragDropShapeEntry != null && ModelShapeEntriesMatch(_modelDragDropShapeEntry, entry))
        {
            ImGui.SetTooltip($"Dragging {label}\nDrop on an element to import as a child, or at the bottom of the tree for root level.");
        }
    }

    private void DrawModelBrowserEntryContextMenu(ModelShapeAssetEntry entry, int shown)
    {
        if (!ImGui.BeginPopupContextItem($"##model-asset-menu-{shown}")) return;

        try
        {
            if (ImGui.MenuItem("Open"))
            {
                ModelRequestOpenDocument(entry);
            }
            if (ImGui.MenuItem("Create authored copy"))
            {
                ModelCreateAuthoredShapeCopy(entry);
            }
            if (_modelDoc == null) ImGui.BeginDisabled();
            if (ImGui.MenuItem("Import into open shape"))
            {
                ModelImportShapeIntoCurrent(entry, _modelSelectedElement);
            }
            if (_modelDoc == null) ImGui.EndDisabled();
            ImGui.Separator();
            if (!entry.Authored) ImGui.BeginDisabled();
            if (ImGui.MenuItem("Rename file..."))
            {
                _modelBrowserFileActionEntry = entry;
                _modelBrowserRenameName = Path.GetFileName(entry.AssetPath.Replace('\\', '/'));
                _modelBrowserPendingFilePopup = "rename";
            }
            if (ImGui.MenuItem("Change folder..."))
            {
                _modelBrowserFileActionEntry = entry;
                _modelBrowserMoveFolder = ModelBrowserFolderWithoutShapes(entry.AssetPath);
                _modelBrowserPendingFilePopup = "move";
            }
            if (ImGui.MenuItem("Delete file..."))
            {
                _modelBrowserFileActionEntry = entry;
                _modelBrowserPendingFilePopup = "delete";
            }
            if (!entry.Authored) ImGui.EndDisabled();
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void DrawModelBrowserFileActionPopups()
    {
        if (_modelBrowserPendingFilePopup.Length > 0)
        {
            string pending = _modelBrowserPendingFilePopup;
            _modelBrowserPendingFilePopup = "";
            switch (pending)
            {
                case "rename":
                    ImGui.OpenPopup("Rename authored shape##model-browser-rename-popup");
                    break;
                case "move":
                    ImGui.OpenPopup("Move authored shape##model-browser-move-popup");
                    break;
                case "delete":
                    ImGui.OpenPopup("Delete authored shape##model-browser-delete-popup");
                    break;
            }
        }

        DrawModelBrowserRenamePopup();
        DrawModelBrowserMovePopup();
        DrawModelBrowserDeletePopup();
    }

    private void DrawModelBrowserRenamePopup()
    {
        bool open = true;
        if (!ImGui.BeginPopupModal("Rename authored shape##model-browser-rename-popup", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;

        try
        {
            ModelShapeAssetEntry? entry = _modelBrowserFileActionEntry;
            ImGui.TextUnformatted(entry?.Display ?? "");
            ImGui.SetNextItemWidth(320f);
            ImGui.InputText("File name##model-browser-rename-name", ref _modelBrowserRenameName, 180);
            if (ImGui.Button("Rename##model-browser-rename-apply"))
            {
                if (entry != null)
                {
                    string folder = ModelAssetDirectory(entry.AssetPath);
                    string newPath = string.IsNullOrEmpty(folder)
                        ? ModelNormalizeShapeAssetPath(_modelBrowserRenameName)
                        : $"{folder}/{EnsureJsonFilePath(_modelBrowserRenameName.Trim().Replace('\\', '/').Trim('/'))}";
                    ModelMoveAuthoredShapeFile(entry, newPath, "Renamed authored shape");
                }
                _modelBrowserFileActionEntry = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##model-browser-rename-cancel"))
            {
                _modelBrowserFileActionEntry = null;
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void DrawModelBrowserMovePopup()
    {
        bool open = true;
        if (!ImGui.BeginPopupModal("Move authored shape##model-browser-move-popup", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;

        try
        {
            ModelShapeAssetEntry? entry = _modelBrowserFileActionEntry;
            ImGui.TextUnformatted(Path.GetFileName(entry?.AssetPath ?? ""));
            ImGui.SetNextItemWidth(360f);
            ImGui.InputText("Folder under shapes##model-browser-move-folder", ref _modelBrowserMoveFolder, 220);
            if (ImGui.Button("Move##model-browser-move-apply"))
            {
                if (entry != null)
                {
                    string fileName = Path.GetFileName(entry.AssetPath.Replace('\\', '/'));
                    string folder = ModelNormalizeShapeFolder(_modelBrowserMoveFolder);
                    string newPath = string.IsNullOrEmpty(folder) ? $"shapes/{fileName}" : $"shapes/{folder}/{fileName}";
                    ModelMoveAuthoredShapeFile(entry, newPath, "Moved authored shape");
                }
                _modelBrowserFileActionEntry = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##model-browser-move-cancel"))
            {
                _modelBrowserFileActionEntry = null;
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void DrawModelBrowserDeletePopup()
    {
        bool open = true;
        if (!ImGui.BeginPopupModal("Delete authored shape##model-browser-delete-popup", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;

        try
        {
            ModelShapeAssetEntry? entry = _modelBrowserFileActionEntry;
            ImGui.TextWrapped($"Delete authored file '{entry?.Display ?? ""}'?");
            if (ImGui.Button("Delete##model-browser-delete-apply"))
            {
                if (entry != null)
                {
                    ModelDeleteAuthoredShapeFile(entry);
                }
                _modelBrowserFileActionEntry = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##model-browser-delete-cancel"))
            {
                _modelBrowserFileActionEntry = null;
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private static string ModelBrowserFolderWithoutShapes(string assetPath)
    {
        string directory = ModelAssetDirectory(assetPath);
        return directory.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase)
            ? directory["shapes/".Length..].Trim('/')
            : directory.Trim('/');
    }

    private static string ModelAssetDirectory(string assetPath)
    {
        string normalized = assetPath.Replace('\\', '/').Trim().Trim('/');
        int slash = normalized.LastIndexOf('/');
        return slash > 0 ? normalized[..slash] : "";
    }

    private static string ModelNormalizeShapeFolder(string folder)
    {
        string normalized = folder.Replace('\\', '/').Trim().Trim('/');
        if (normalized.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["shapes/".Length..].Trim('/');
        }

        List<string> parts = [];
        foreach (string rawPart in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawPart is "." or "..") continue;
            string part = SanitizePathSegment(rawPart);
            if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
        }

        return string.Join("/", parts);
    }

    private static string ModelNormalizeShapeAssetPath(string assetPath)
    {
        string normalized = assetPath.Replace('\\', '/').Trim().Trim('/');
        if (normalized.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["shapes/".Length..].Trim('/');
        }
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "unnamed.json";
        }

        normalized = EnsureJsonFilePath(normalized);
        string[] rawParts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> parts = [];
        foreach (string rawPart in rawParts)
        {
            if (rawPart is "." or "..") continue;
            string part = SanitizePathSegment(rawPart);
            if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
        }

        if (parts.Count == 0) parts.Add("unnamed.json");
        string fileName = parts[^1];
        if (string.Equals(fileName, ".json", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(fileName)))
        {
            parts[^1] = "unnamed.json";
        }

        return "shapes/" + string.Join("/", parts);
    }

    private static string ModelAuthoredShapeFilePath(string domain, string assetPath)
    {
        string normalizedPath = ModelNormalizeShapeAssetPath(assetPath);
        string normalizedDomain = SanitizePathSegment(string.IsNullOrWhiteSpace(domain) ? "game" : domain.Trim().ToLowerInvariant());
        string relativePath = Path.Combine("assets", normalizedDomain, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
        return GetToolAuthoredAssetPath("models", relativePath);
    }

    private static string ModelCopyAssetPath(string assetPath)
    {
        string normalized = ModelNormalizeShapeAssetPath(assetPath);
        string directory = ModelAssetDirectory(normalized);
        string fileName = Path.GetFileName(normalized.Replace('\\', '/'));
        string name = Path.GetFileNameWithoutExtension(fileName);
        string copyName = $"{name}-copy.json";
        return string.IsNullOrEmpty(directory) ? copyName : $"{directory}/{copyName}";
    }

    private static string ModelUniqueAuthoredShapeAssetPath(string domain, string desiredAssetPath)
    {
        string normalized = ModelNormalizeShapeAssetPath(desiredAssetPath);
        if (!File.Exists(ModelAuthoredShapeFilePath(domain, normalized))) return normalized;

        string directory = ModelAssetDirectory(normalized);
        string fileName = Path.GetFileName(normalized.Replace('\\', '/'));
        string name = Path.GetFileNameWithoutExtension(fileName);
        for (int copy = 2; copy < 10_000; copy++)
        {
            string candidateName = $"{name}-{copy}.json";
            string candidate = string.IsNullOrEmpty(directory) ? candidateName : $"{directory}/{candidateName}";
            if (!File.Exists(ModelAuthoredShapeFilePath(domain, candidate))) return candidate;
        }

        return normalized;
    }

    private void ModelCreateAuthoredShapeCopy(ModelShapeAssetEntry entry)
    {
        try
        {
            string text = entry.Asset.ToText();
            string targetAssetPath = ModelUniqueAuthoredShapeAssetPath(entry.Domain, ModelCopyAssetPath(entry.AssetPath));
            string targetPath = ModelAuthoredShapeFilePath(entry.Domain, targetAssetPath);
            WriteAuthoredFile(targetPath, text);
            _modelShapeIndex = null;
            _modelStatus = $"Created authored copy {entry.Domain}:{targetAssetPath}.";
        }
        catch (Exception exception)
        {
            _modelDiagnostics.Exception($"Could not copy {entry.Display}", exception);
            _modelStatus = $"Could not copy {entry.Display}: {exception.Message}";
        }
    }

    private void ModelMoveAuthoredShapeFile(ModelShapeAssetEntry entry, string newAssetPath, string verb)
    {
        if (!entry.Authored)
        {
            _modelStatus = "Only authored shape files can be renamed or moved.";
            return;
        }

        try
        {
            string oldAssetPath = ModelNormalizeShapeAssetPath(entry.AssetPath);
            string targetAssetPath = ModelNormalizeShapeAssetPath(newAssetPath);
            if (string.Equals(oldAssetPath, targetAssetPath, StringComparison.OrdinalIgnoreCase))
            {
                _modelStatus = "Authored shape is already at that location.";
                return;
            }

            string oldPath = ModelAuthoredShapeFilePath(entry.Domain, oldAssetPath);
            string targetPath = ModelAuthoredShapeFilePath(entry.Domain, targetAssetPath);
            if (!File.Exists(oldPath))
            {
                _modelStatus = $"Authored file does not exist: {entry.Display}.";
                _modelShapeIndex = null;
                return;
            }
            if (File.Exists(targetPath))
            {
                _modelStatus = $"Target already exists: {entry.Domain}:{targetAssetPath}.";
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Move(oldPath, targetPath);
            ModelUpdateOpenAuthoredDocumentAfterMove(entry, targetAssetPath, targetPath);
            _modelShapeIndex = null;
            _modelStatus = $"{verb} to {entry.Domain}:{targetAssetPath}.";
        }
        catch (Exception exception)
        {
            _modelDiagnostics.Exception($"{verb} failed for {entry.Display}", exception);
            _modelStatus = $"{verb} failed for {entry.Display}: {exception.Message}";
        }
    }

    private void ModelUpdateOpenAuthoredDocumentAfterMove(ModelShapeAssetEntry entry, string newAssetPath, string newFilePath)
    {
        if (_modelDoc == null ||
            !_modelDoc.FromAuthoredFile ||
            !string.Equals(_modelDoc.Domain, entry.Domain, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_modelDoc.AssetPath, entry.AssetPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool wasDirty = _modelDoc.Dirty;
        _modelDoc.AssetPath = newAssetPath;
        _modelDoc.SourceText = File.Exists(newFilePath) ? File.ReadAllText(newFilePath) : _modelDoc.SourceText;
        _modelDoc.FromAuthoredFile = true;
        _modelDoc.Dirty = wasDirty;
        _modelJsonBufferStale = true;
    }

    private void ModelDeleteAuthoredShapeFile(ModelShapeAssetEntry entry)
    {
        if (!entry.Authored)
        {
            _modelStatus = "Only authored shape files can be deleted from this menu.";
            return;
        }

        try
        {
            string oldAssetPath = ModelNormalizeShapeAssetPath(entry.AssetPath);
            string oldPath = ModelAuthoredShapeFilePath(entry.Domain, oldAssetPath);
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }

            if (_modelDoc != null &&
                _modelDoc.FromAuthoredFile &&
                string.Equals(_modelDoc.Domain, entry.Domain, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_modelDoc.AssetPath, entry.AssetPath, StringComparison.OrdinalIgnoreCase))
            {
                _modelDoc.FromAuthoredFile = false;
                _modelDoc.IsNew = true;
                _modelDoc.Dirty = true;
                _modelJsonBufferStale = true;
            }

            _modelShapeIndex = null;
            _modelStatus = $"Deleted authored shape {entry.Display}.";
        }
        catch (Exception exception)
        {
            _modelDiagnostics.Exception($"Could not delete {entry.Display}", exception);
            _modelStatus = $"Could not delete {entry.Display}: {exception.Message}";
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
            if (ModelIsMeshLibMode)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Add mesh...##model-tree-add-mesh")) ImGui.OpenPopup("##model-add-mesh-root-popup");
                if (ImGui.BeginPopup("##model-add-mesh-root-popup"))
                {
                    if (ImGui.MenuItem("Mesh box")) ModelAddMeshElement(null, "Mesh");
                    if (ImGui.MenuItem("Triangle")) ModelAddMeshElement(null, "Triangle");
                    if (ImGui.MenuItem("Quad")) ModelAddMeshElement(null, "Quad");
                    ImGui.EndPopup();
                }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Add primitive##model-tree-add-primitive"))
            {
                SetModelGeneratorTool(1);
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

            ImGui.SetNextItemWidth(-float.Epsilon);
            ImGui.InputTextWithHint("##model-tree-filter", "Filter elements...", ref _modelTreeFilter, 128);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Show only elements whose name matches (ancestors stay visible). Supports loose subsequence matching.");
            }

            _modelTreeFilterMatches = BuildModelTreeFilterMatches();
            if (_modelTreeFilterMatches != null)
            {
                ImGui.TextDisabled($"{_modelTreeFilterMatches.Count} element(s) shown.");
            }

            ImGui.BeginChild("##model-tree-list", new NVector2(0f, 0f), false, ImGuiWindowFlags.HorizontalScrollbar);
            try
            {
                for (int index = 0; index < _modelDoc.Roots.Count; index++)
                {
                    DrawModelTreeNode(_modelDoc.Roots[index], index, depth: 0);
                }
                DrawModelTreeRootDropTarget();
            }
            finally
            {
                ImGui.EndChild();
            }
            ModelClearCompletedTreeDragDrop();
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private HashSet<ModelElementData>? BuildModelTreeFilterMatches()
    {
        string filter = _modelTreeFilter.Trim();
        if (filter.Length == 0 || _modelDoc == null) return null;

        HashSet<ModelElementData> visible = [];
        foreach (ModelElementData root in _modelDoc.Roots)
        {
            CollectModelTreeFilterMatches(root, filter, visible);
        }

        return visible;
    }

    private static bool CollectModelTreeFilterMatches(ModelElementData element, string filter, HashSet<ModelElementData> visible)
    {
        bool anyDescendant = false;
        foreach (ModelElementData child in element.Children)
        {
            anyDescendant |= CollectModelTreeFilterMatches(child, filter, visible);
        }

        if (anyDescendant || DevToolsFuzzyMatch.Matches(element.Name ?? "", filter))
        {
            visible.Add(element);
            return true;
        }

        return false;
    }

    private void DrawModelTreeNode(ModelElementData element, int index, int depth)
    {
        if (_modelTreeFilterMatches != null && !_modelTreeFilterMatches.Contains(element)) return;

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
            if (_modelTreeFilterMatches != null && element.Children.Any(child => _modelTreeFilterMatches.Contains(child)))
            {
                // Reveal matches while a filter is active.
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            }

            string name = string.IsNullOrWhiteSpace(element.Name) ? "(unnamed)" : element.Name;
            if (ReferenceEquals(element, _modelSelectedElement) && _modelSelectedElements.Count > 1)
            {
                name = "> " + name;
            }
            bool open = ImGui.TreeNodeEx($"{name}###model-node", flags);
            DrawModelTreeDragDrop(element);
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
                    ModelSelectElement(element, additive: IsDevToolsCtrlDown());
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
                if (ModelIsMeshLibMode && ImGui.BeginMenu("Add child mesh"))
                {
                    if (ImGui.MenuItem("Mesh box")) ModelAddMeshElement(element, "Mesh");
                    if (ImGui.MenuItem("Triangle")) ModelAddMeshElement(element, "Triangle");
                    if (ImGui.MenuItem("Quad")) ModelAddMeshElement(element, "Quad");
                    ImGui.EndMenu();
                }
                if (ModelIsMeshLibMode && element.NonCuboid == null && ModelElementHasRenderableBox(element) && ImGui.MenuItem("Convert cuboid to MeshLib mesh"))
                {
                    ModelConvertSelectedCuboidToMesh();
                }
                if (ImGui.MenuItem("Duplicate", "Ctrl+D"))
                {
                    ModelDuplicateSelectedElements();
                }
                if (ImGui.MenuItem("Cut with current pieces"))
                {
                    ModelCutSelectedElements(_modelCutPartsX, _modelCutPartsY, _modelCutPartsZ);
                }
                if (ImGui.BeginMenu("Quick cut"))
                {
                    if (ImGui.MenuItem("2 pieces on X")) ModelCutSelectedElements(2, 1, 1);
                    if (ImGui.MenuItem("2 pieces on Y")) ModelCutSelectedElements(1, 2, 1);
                    if (ImGui.MenuItem("2 pieces on Z")) ModelCutSelectedElements(1, 1, 2);
                    ImGui.EndMenu();
                }
                if (ImGui.MenuItem("Copy", "Ctrl+C"))
                {
                    ModelCopySelectedElementsToClipboard();
                }
                if (ImGui.MenuItem("Paste as child"))
                {
                    ModelPasteElementsFromClipboard(element);
                }
                if (ImGui.MenuItem("Delete", "Del"))
                {
                    ModelDeleteSelectedElements();
                }
                if (ImGui.MenuItem("Center pivot (keep position)"))
                {
                    ModelCenterPivotSelectedElements();
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

    private void DrawModelTreeDragDrop(ModelElementData element)
    {
        ModelShapeAssetEntry? draggedShape = _modelDragDropShapeEntry;
        if (draggedShape != null)
        {
            bool hoveredShapeTarget = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
            if (!hoveredShapeTarget) return;

            string shapeDropTargetName = string.IsNullOrWhiteSpace(element.Name) ? "(unnamed)" : element.Name;
            ImGui.SetTooltip($"Drop {ModelImportSourceLabel(draggedShape)} under {shapeDropTargetName}.");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                ModelImportShapeIntoCurrent(draggedShape, element);
                _modelDragDropShapeEntry = null;
            }
            return;
        }

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 6f))
        {
            _modelDragDropElement ??= element;
        }

        ModelElementData? dragged = _modelDragDropElement;
        if (dragged == null) return;

        bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        if (!hovered) return;

        string draggedName = string.IsNullOrWhiteSpace(dragged.Name) ? "(unnamed)" : dragged.Name;
        if (ReferenceEquals(dragged, element))
        {
            ImGui.SetTooltip($"Dragging {draggedName}");
            return;
        }

        if (dragged.EnumerateSubtree().Contains(element))
        {
            ImGui.SetTooltip("Cannot reparent an element into its own subtree.");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) _modelDragDropElement = null;
            return;
        }

        string targetName = string.IsNullOrWhiteSpace(element.Name) ? "(unnamed)" : element.Name;
        ImGui.SetTooltip($"Drop {draggedName} under {targetName}.");
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            ModelReparentElement(dragged, element);
            _modelDragDropElement = null;
        }
    }

    private void DrawModelTreeRootDropTarget()
    {
        ImGui.Dummy(new NVector2(Math.Max(1f, ImGui.GetContentRegionAvail().X), 24f));
        bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        if (_modelDragDropShapeEntry != null && hovered)
        {
            ImGui.SetTooltip($"Drop {ModelImportSourceLabel(_modelDragDropShapeEntry)} at root level.");
        }
        else if (hovered)
        {
            ImGui.SetTooltip("Drop here to move the element to the root.");
        }

        if (_modelDragDropShapeEntry != null && hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            ModelImportShapeIntoCurrent(_modelDragDropShapeEntry, null);
            _modelDragDropShapeEntry = null;
        }
        else if (_modelDragDropElement != null && hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            ModelReparentElement(_modelDragDropElement, null);
            _modelDragDropElement = null;
        }
    }

    private void ModelClearCompletedTreeDragDrop()
    {
        if (_modelDragDropElement != null && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _modelDragDropElement = null;
        }
        if (_modelDragDropShapeEntry != null && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _modelDragDropShapeEntry = null;
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
            if (_modelSelectedElement?.NonCuboid != null)
            {
                DrawModelMeshInspector(_modelDoc);
            }
            else
            {
                DrawModelFacesSection(_modelDoc);
            }
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

        ImGui.SameLine();
        if (ImGui.Button("Animate this shape##model-animate"))
        {
            ModelAnimateCurrentShape();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Save this shape to the authored models folder and open it in the animation editor's Shapes tab. If it has no animations yet, you can create the first one there.");
        }

        DrawModelRuntimeControls(doc);
        DrawModelExtraMetadataEditor("Document metadata", $"doc:{doc.DisplayPath}", doc.Extra, value => doc.Extra = value);
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

        if (element.NonCuboid != null)
        {
            DrawModelMeshElementBoundsControls(element);
        }
        else
        {
            DrawModelElementCutControls(element);
        }

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
            if (element.NonCuboid != null)
            {
                ModelAutoUvSelectedMeshFaces();
            }
            else
            {
                ModelBeginEdit();
                for (int face = 0; face < 6; face++)
                {
                    ModelAutoUvFace(element, face);
                }
                ModelMarkChanged();
                ModelEndEdit("Auto UV element");
            }
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

        DrawModelExtraMetadataEditor("Element metadata", $"element:{element.GetHashCode()}", element.Extra, value => element.Extra = value);

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

    private void DrawModelElementCutControls(ModelElementData element)
    {
        ImGui.SeparatorText("Cut");
        int cutX = _modelCutPartsX;
        int cutY = _modelCutPartsY;
        int cutZ = _modelCutPartsZ;
        bool partsChanged = false;

        ImGui.SetNextItemWidth(58f);
        partsChanged |= ImGui.InputInt("X##model-cut-x", ref cutX, 0);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(58f);
        partsChanged |= ImGui.InputInt("Y##model-cut-y", ref cutY, 0);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(58f);
        partsChanged |= ImGui.InputInt("Z##model-cut-z", ref cutZ, 0);
        if (partsChanged)
        {
            _modelCutPartsX = ModelNormalizeCutParts(cutX);
            _modelCutPartsY = ModelNormalizeCutParts(cutY);
            _modelCutPartsZ = ModelNormalizeCutParts(cutZ);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Pieces per axis. Max {ModelCutMaxPiecesPerElement} pieces per selected element.");
        }

        bool canCut = ModelCanCutSelection(_modelCutPartsX, _modelCutPartsY, _modelCutPartsZ, out string cutReason);
        if (!canCut) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Cut selected##model-cut-apply"))
        {
            ModelCutSelectedElements(_modelCutPartsX, _modelCutPartsY, _modelCutPartsZ);
        }
        if (!canCut) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Replaces selected cuboids with separate cuboid elements that share exact boundaries.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("X2##model-cut-x2")) ModelCutSelectedElements(2, 1, 1);
        ImGui.SameLine();
        if (ImGui.SmallButton("Y2##model-cut-y2")) ModelCutSelectedElements(1, 2, 1);
        ImGui.SameLine();
        if (ImGui.SmallButton("Z2##model-cut-z2")) ModelCutSelectedElements(1, 1, 2);

        if (!canCut)
        {
            ImGui.TextDisabled(cutReason);
        }
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
        string commonTexture = ModelCommonFaceTexture(element);
        string allFacesPreview = commonTexture.Length > 0 ? commonTexture : "(mixed)";
        ImGui.SetNextItemWidth(-1f);
        if (ModelFilteredCombo("All faces texture##model-face-all-texture", allFacesPreview, textureCodes, out string pickedAllTexture, allowCustom: true, filterHint: "filter texture codes"))
        {
            ModelBeginEdit();
            for (int faceIndex = 0; faceIndex < 6; faceIndex++)
            {
                ModelFaceData? face = element.Faces[faceIndex];
                if (face == null)
                {
                    face = new ModelFaceData();
                    element.Faces[faceIndex] = face;
                    ModelAutoUvFace(element, faceIndex);
                }
                face.Texture = pickedAllTexture;
            }
            ModelMarkChanged();
            ModelEndEdit("Set all face textures");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Set the texture code for all six faces of this element. Missing faces are created.");
        }
        ImGui.Spacing();

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

                DrawModelExtraMetadataEditor($"{ModelFaceNames[faceIndex]} metadata", $"face:{element.GetHashCode()}:{faceIndex}", face.Extra, value => face.Extra = value);
            }
            finally
            {
                ImGui.PopID();
            }
        }
    }

    private static string ModelCommonFaceTexture(ModelElementData element)
    {
        string? texture = null;
        bool any = false;
        foreach (ModelFaceData? face in element.Faces)
        {
            if (face == null) continue;
            any = true;
            texture ??= face.Texture;
            if (!string.Equals(texture, face.Texture, StringComparison.Ordinal))
            {
                return "";
            }
        }

        return any ? texture ?? "" : "";
    }

    private void DrawModelExtraMetadataEditor(string label, string bufferKey, JObject? extra, Action<JObject?> setExtra)
    {
        if (extra == null)
        {
            if (ImGui.SmallButton($"Add {label}##model-extra-add-{bufferKey}"))
            {
                ModelBeginEdit();
                setExtra(new JObject());
                _modelMetadataBuffers.Remove(bufferKey);
                ModelMarkChanged();
                ModelEndEdit($"Add {label}");
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Adds an object for model JSON fields that are preserved but not otherwise represented by the editor.");
            }
            return;
        }

        ImGuiTreeNodeFlags flags = extra.Count > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!ImGui.TreeNodeEx($"{label} ({extra.Count})##model-extra-node-{bufferKey}", flags)) return;

        if (!_modelMetadataBuffers.TryGetValue(bufferKey, out string? buffer))
        {
            buffer = extra.ToString(Formatting.Indented);
            _modelMetadataBuffers[bufferKey] = buffer;
        }

        ImGui.InputTextMultiline($"##model-extra-json-{bufferKey}", ref buffer, DevToolsImGuiTextBuffer.Capacity(buffer), new NVector2(-float.Epsilon, 96f), ImGuiInputTextFlags.AllowTabInput);
        _modelMetadataBuffers[bufferKey] = buffer;

        if (ImGui.Button($"Apply##model-extra-apply-{bufferKey}"))
        {
            if (DevToolsJson.TryParseObject(buffer, out JObject? parsed, out string error) && parsed != null)
            {
                ModelBeginEdit();
                setExtra(parsed.Count == 0 ? null : parsed);
                _modelMetadataBuffers[bufferKey] = parsed.ToString(Formatting.Indented);
                ModelMarkChanged();
                ModelEndEdit($"Edit {label}");
                _modelStatus = $"{label} updated.";
            }
            else
            {
                _modelStatus = $"{label} JSON parse failed: {error}";
            }
        }
        ImGui.SameLine();
        if (ImGui.Button($"Format##model-extra-format-{bufferKey}"))
        {
            if (DevToolsJsonTextTools.TryFormat(buffer, out string formatted, out string formatError))
            {
                _modelMetadataBuffers[bufferKey] = formatted;
            }
            else
            {
                _modelStatus = $"{label} format failed: {formatError}";
            }
        }
        ImGui.SameLine();
        if (ImGui.Button($"Remove##model-extra-remove-{bufferKey}"))
        {
            ModelBeginEdit();
            setExtra(null);
            _modelMetadataBuffers.Remove(bufferKey);
            ModelMarkChanged();
            ModelEndEdit($"Remove {label}");
        }

        ImGui.TreePop();
    }

    // Single bare letters and Ctrl+digit combos pass through to the game (E opens the
    // inventory, Q drops items, Ctrl+1..4 select backpack slots), so every shortcut here
    // uses a combination the vanilla hotkey table leaves unbound. Each one also has a
    // clickable equivalent; the toolbar Shortcuts button documents them.
    private void ModelHandleShortcuts()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        if (io.WantTextInput) return;

        bool ctrl = IsDevToolsCtrlDown();
        bool shift = IsDevToolsShiftDown();

        if (ctrl && !shift && IsDevToolsShortcutPressed(ImGuiKey.Z, GlKeys.Z))
        {
            ModelUndo();
        }
        else if (ctrl && !shift && IsDevToolsShortcutPressed(ImGuiKey.Y, GlKeys.Y))
        {
            ModelRedo();
        }
        else if (ctrl && !shift && IsDevToolsShortcutPressed(ImGuiKey.D, GlKeys.D) && _modelSelectedElement != null)
        {
            ModelDuplicateSelectedElements();
        }
        else if (ctrl && !shift && IsDevToolsShortcutPressed(ImGuiKey.C, GlKeys.C) && _modelSelectedElement != null)
        {
            ModelCopySelectedElementsToClipboard();
        }
        else if (ctrl && !shift && IsDevToolsShortcutPressed(ImGuiKey.V, GlKeys.V) && _modelDoc != null)
        {
            ModelPasteElementsFromClipboard(_modelSelectedElement?.Parent);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Delete) && _modelSelectedElement != null)
        {
            if (ModelMeshComponentsActive()) ModelDeleteSelectedMeshComponents();
            else ModelDeleteSelectedElements();
        }
        else if (ctrl && shift && IsDevToolsShortcutPressed(ImGuiKey._1, GlKeys.Number1))
        {
            ModelSetGizmoTool(ModelGizmoTool.None);
        }
        else if (ctrl && shift && IsDevToolsShortcutPressed(ImGuiKey._2, GlKeys.Number2))
        {
            ModelSetGizmoTool(ModelGizmoTool.Move);
        }
        else if (ctrl && shift && IsDevToolsShortcutPressed(ImGuiKey._3, GlKeys.Number3))
        {
            ModelSetGizmoTool(ModelGizmoTool.Resize);
        }
        else if (ctrl && shift && IsDevToolsShortcutPressed(ImGuiKey._4, GlKeys.Number4))
        {
            ModelSetGizmoTool(ModelGizmoTool.Rotate);
        }
        else if (ctrl && shift && IsDevToolsShortcutPressed(ImGuiKey._5, GlKeys.Number5))
        {
            ModelSetGizmoTool(ModelIsMeshLibMode ? ModelGizmoTool.Extrude : ModelGizmoTool.Cut);
        }
        else if (ctrl && shift && IsDevToolsShortcutPressed(ImGuiKey._6, GlKeys.Number6))
        {
            ModelSetGizmoTool(ModelIsMeshLibMode ? ModelGizmoTool.Inset : ModelGizmoTool.Chisel);
        }
        else if (ModelIsMeshLibMode && ctrl && shift && IsDevToolsShortcutPressed(ImGuiKey._7, GlKeys.Number7))
        {
            ModelSetGizmoTool(ModelGizmoTool.Subdivide);
        }
        else if (ModelHandleNudgeShortcuts())
        {
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Home))
        {
            ModelFocusCameraOnSelection();
        }
    }

    private bool ModelHandleNudgeShortcuts()
    {
        if (_modelDoc == null || ModelSelectedElementsInDocument().Count == 0) return false;

        double step = ModelNudgeStep();
        double[] delta = [0.0, 0.0, 0.0];
        (int horizontalAxis, int verticalAxis) = ModelArrowNudgeAxes();

        if (IsDevToolsShortcutPressed(ImGuiKey.LeftArrow, GlKeys.Left, repeat: true)) delta[horizontalAxis] -= step;
        if (IsDevToolsShortcutPressed(ImGuiKey.RightArrow, GlKeys.Right, repeat: true)) delta[horizontalAxis] += step;
        if (IsDevToolsShortcutPressed(ImGuiKey.UpArrow, GlKeys.Up, repeat: true)) delta[verticalAxis] += step;
        if (IsDevToolsShortcutPressed(ImGuiKey.DownArrow, GlKeys.Down, repeat: true)) delta[verticalAxis] -= step;

        return ModelMeshComponentsActive()
            ? ModelNudgeSelectedMeshComponents(delta[0], delta[1], delta[2])
            : ModelNudgeSelectedElements(delta[0], delta[1], delta[2]);
    }

    private (int HorizontalAxis, int VerticalAxis) ModelArrowNudgeAxes()
    {
        return Math.Clamp(_modelArrowNudgePlane, 0, ModelNudgePlaneLabels.Length - 1) switch
        {
            1 => (0, 2),
            2 => (1, 2),
            _ => (0, 1)
        };
    }

    private bool ModelNudgeSelectedElements(int axis, double amount)
    {
        double[] delta = [0.0, 0.0, 0.0];
        delta[Math.Clamp(axis, 0, 2)] = amount;
        return ModelNudgeSelectedElements(delta[0], delta[1], delta[2]);
    }

    private double ModelNudgeStep()
    {
        double step = _modelSnapEnabled ? Math.Max(0.0001f, _modelSnapMoveUnits) : 0.25;
        if (IsDevToolsShiftDown()) step *= 4.0;
        if (IsDevToolsAltDown()) step *= 0.25;
        return step;
    }

    private bool ModelNudgeSelectedElements(double dx, double dy, double dz)
    {
        if (_modelDoc == null) return false;
        if (Math.Abs(dx) < 0.000001 && Math.Abs(dy) < 0.000001 && Math.Abs(dz) < 0.000001) return false;

        List<ModelElementData> targets = ModelEffectiveSelectedRoots();
        if (targets.Count == 0) return false;

        ModelBeginEdit();
        foreach (ModelElementData element in targets)
        {
            ModelTranslateElement(element, dx, dy, dz);
        }
        ModelMarkChanged();
        ModelEndEdit("Nudge selected elements");
        _modelStatus = $"Nudged {targets.Count} selected element(s) {ModelFormatNudgeDelta(dx, dy, dz)}.";
        return true;
    }

    private static void ModelTranslateElement(ModelElementData element, double dx, double dy, double dz)
    {
        double[] delta = [dx, dy, dz];
        for (int axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(delta[axis]) < 0.000001) continue;
            element.From[axis] += delta[axis];
            element.To[axis] += delta[axis];
            if (element.RotationOrigin != null)
            {
                element.RotationOrigin[axis] += delta[axis];
            }
            if (element.NonCuboid?.Editable == true)
            {
                foreach (double[] vertex in element.NonCuboid.Vertices)
                {
                    if (vertex.Length > axis) vertex[axis] += delta[axis];
                }
            }
        }
    }

    private static string ModelFormatNudgeDelta(double dx, double dy, double dz)
    {
        List<string> parts = [];
        if (Math.Abs(dx) >= 0.000001) parts.Add($"{ModelSigned(dx)} X");
        if (Math.Abs(dy) >= 0.000001) parts.Add($"{ModelSigned(dy)} Y");
        if (Math.Abs(dz) >= 0.000001) parts.Add($"{ModelSigned(dz)} Z");
        return string.Join(", ", parts);
    }

    private static string ModelSigned(double value)
    {
        return value >= 0.0 ? $"+{value:0.###}" : $"{value:0.###}";
    }

    private static string ModelAxisLabel(int axis)
    {
        return ModelAxisLabels[Math.Clamp(axis, 0, ModelAxisLabels.Length - 1)];
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
            ModelClearMeshComponentSelection();
            _modelSelectedElement = null;
            _modelSelectedElements.Clear();
            _modelSelectionOrder.Clear();
            _modelSelectedFace = -1;
            return;
        }

        if (!additive)
        {
            bool changed = !ReferenceEquals(_modelSelectedElement, element) || _modelSelectedElements.Count != 1 || !_modelSelectedElements.Contains(element);
            if (changed) ModelClearMeshComponentSelection();
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
                index.Add(new ModelShapeAssetEntry(asset.Location.Domain, asset.Location.Path, asset, Authored: true, MeshLib: ModelAssetLooksMeshLib(asset)));
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

                index.Add(new ModelShapeAssetEntry(asset.Location.Domain, path, asset, MeshLib: ModelAssetLooksMeshLib(asset)));
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

    private void ModelImportShapeIntoCurrent(ModelShapeAssetEntry entry, ModelElementData? parent)
    {
        if (_modelDoc == null)
        {
            _modelStatus = "Open or create a shape before importing another model.";
            return;
        }

        if (!ModelTryLoadShapeEntryDocument(entry, out ModelDocumentData? sourceDoc, out string error) || sourceDoc == null)
        {
            _modelStatus = $"Could not import {entry.Display}: {error}";
            return;
        }

        if (sourceDoc.Roots.Count == 0)
        {
            _modelStatus = $"Could not import {entry.Display}: source shape has no elements.";
            return;
        }

        try
        {
            ModelBeginEdit();
            ModelElementData group = ModelBuildImportedShapeGroup(
                _modelDoc,
                sourceDoc,
                entry.AssetPath,
                out int importedElements,
                out int addedTextures,
                out int renamedTextures);

            group.Parent = parent;
            (parent?.Children ?? _modelDoc.Roots).Add(group);
            ModelSelectElement(group);
            if (string.IsNullOrWhiteSpace(_modelSelectedTextureCode))
            {
                _modelSelectedTextureCode = _modelDoc.Textures.FirstOrDefault()?.Code ?? "";
            }

            ModelMarkChanged();
            ModelEndEdit("Import shape");

            string target = parent == null
                ? "at root level"
                : $"under {parent.Name}";
            string textureSummary = addedTextures == 0
                ? "No texture slots were added."
                : $"{addedTextures} texture slot(s) were added{(renamedTextures > 0 ? $"; {renamedTextures} conflicting code(s) were renamed" : "")}.";
            _modelStatus = $"Imported {importedElements} element(s) from {entry.Display} as {group.Name} {target}. {textureSummary}";
        }
        catch (Exception exception)
        {
            ModelCancelEdit();
            _modelDiagnostics.Exception($"Could not import {entry.Display}", exception);
            _modelStatus = $"Could not import {entry.Display}: {exception.Message}";
        }
    }

    private bool ModelTryLoadShapeEntryDocument(ModelShapeAssetEntry entry, out ModelDocumentData? doc, out string error)
    {
        doc = null;
        error = "";
        try
        {
            string text = entry.Asset.ToText();
            if (!ModelTryParseDocument(text, entry.Domain, entry.AssetPath, isNew: false, out doc, out error) || doc == null)
            {
                if (string.IsNullOrWhiteSpace(error)) error = "invalid shape JSON.";
                return false;
            }

            doc.FromAuthoredFile = entry.Authored;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static ModelElementData ModelBuildImportedShapeGroup(
        ModelDocumentData targetDoc,
        ModelDocumentData sourceDoc,
        string sourceAssetPath,
        out int importedElements,
        out int addedTextures,
        out int renamedTextures)
    {
        Dictionary<string, string> textureMap = ModelMergeImportedTextures(targetDoc, sourceDoc, out addedTextures, out renamedTextures);
        HashSet<string> reservedNames = new(targetDoc.EnumerateElements().Select(element => element.Name), StringComparer.OrdinalIgnoreCase);
        string groupName = ModelReserveUniqueElementName(reservedNames, ModelImportGroupBaseName(sourceAssetPath));

        ModelElementData group = new()
        {
            Name = groupName,
            From = [0.0, 0.0, 0.0],
            To = [0.0, 0.0, 0.0],
            RotationOrigin = [0.0, 0.0, 0.0]
        };

        importedElements = 0;
        foreach (ModelElementData root in sourceDoc.Roots)
        {
            ModelElementData clone = root.CloneSubtree();
            clone.Parent = group;
            importedElements += ModelPrepareImportedElementSubtree(clone, textureMap, reservedNames);
            group.Children.Add(clone);
        }

        ModelCenterImportedGroupPivot(group);
        return group;
    }

    private static string ModelImportGroupBaseName(string sourceAssetPath)
    {
        string normalized = sourceAssetPath.Replace('\\', '/').Trim('/');
        if (normalized.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["shapes/".Length..].Trim('/');
        }

        string fileName = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "imported-model";
        string baseName = ModelSanitizeFileName(Path.GetFileNameWithoutExtension(fileName));
        return string.IsNullOrWhiteSpace(baseName) ? "imported-model" : baseName;
    }

    private static int ModelPrepareImportedElementSubtree(
        ModelElementData element,
        IReadOnlyDictionary<string, string> textureMap,
        HashSet<string> reservedNames)
    {
        int count = 0;
        foreach (ModelElementData node in element.EnumerateSubtree())
        {
            node.Name = ModelReserveUniqueElementName(reservedNames, node.Name);
            foreach (ModelFaceData? face in node.Faces)
            {
                if (face == null || string.IsNullOrWhiteSpace(face.Texture)) continue;
                if (textureMap.TryGetValue(face.Texture, out string? mappedTexture))
                {
                    face.Texture = mappedTexture;
                }
            }
            if (node.NonCuboid?.Editable == true)
            {
                foreach (ModelMeshFaceData face in node.NonCuboid.Faces)
                {
                    if (textureMap.TryGetValue(face.Texture, out string? mappedTexture)) face.Texture = mappedTexture;
                }
            }
            count++;
        }

        return count;
    }

    private static void ModelCenterImportedGroupPivot(ModelElementData group)
    {
        if (!ModelTryGetGroupLocalBounds(group, out DevToolsPreviewBounds bounds)) return;

        var center = bounds.Center;
        double[] centerUnits =
        [
            Math.Round(center.X * ModelUnitsPerBlock, 6),
            Math.Round(center.Y * ModelUnitsPerBlock, 6),
            Math.Round(center.Z * ModelUnitsPerBlock, 6)
        ];
        group.From = (double[])centerUnits.Clone();
        group.To = (double[])centerUnits.Clone();
        group.RotationOrigin = centerUnits;
    }

    private static Dictionary<string, string> ModelMergeImportedTextures(
        ModelDocumentData targetDoc,
        ModelDocumentData sourceDoc,
        out int addedTextures,
        out int renamedTextures)
    {
        addedTextures = 0;
        renamedTextures = 0;
        Dictionary<string, string> textureMap = new(StringComparer.Ordinal);
        HashSet<string> reservedCodes = ModelCollectUsedTextureCodes(targetDoc);

        foreach (string sourceCode in ModelSourceTextureCodes(sourceDoc))
        {
            string sourcePath = sourceDoc.Textures.FirstOrDefault(texture => string.Equals(texture.Code, sourceCode, StringComparison.Ordinal))?.Path ?? "";
            int[] sourceSize = ModelEffectiveTextureSize(sourceDoc, sourceCode);
            ModelTextureEntry? targetTexture = targetDoc.Textures.FirstOrDefault(texture => string.Equals(texture.Code, sourceCode, StringComparison.Ordinal));

            if (targetTexture != null && ModelImportTextureCompatible(targetDoc, targetTexture, sourcePath, sourceSize))
            {
                textureMap[sourceCode] = sourceCode;
                continue;
            }

            string targetCode;
            if (!reservedCodes.Contains(sourceCode))
            {
                targetCode = sourceCode;
                reservedCodes.Add(targetCode);
            }
            else
            {
                targetCode = ModelReserveUniqueTextureCode(reservedCodes, sourceCode);
                renamedTextures++;
            }

            targetDoc.Textures.Add(new ModelTextureEntry { Code = targetCode, Path = sourcePath });
            ModelApplyImportedTextureSize(targetDoc, targetCode, sourceSize);
            textureMap[sourceCode] = targetCode;
            addedTextures++;
        }

        return textureMap;
    }

    private static HashSet<string> ModelCollectUsedTextureCodes(ModelDocumentData doc)
    {
        HashSet<string> codes = new(StringComparer.Ordinal);
        foreach (ModelTextureEntry texture in doc.Textures)
        {
            if (!string.IsNullOrWhiteSpace(texture.Code)) codes.Add(texture.Code);
        }
        foreach (string code in doc.TextureSizes.Keys)
        {
            if (!string.IsNullOrWhiteSpace(code)) codes.Add(code);
        }
        foreach (ModelElementData element in doc.EnumerateElements())
        {
            foreach (ModelFaceData? face in element.Faces)
            {
                if (face != null && !string.IsNullOrWhiteSpace(face.Texture)) codes.Add(face.Texture);
            }
            if (element.NonCuboid?.Editable == true)
            {
                foreach (ModelMeshFaceData face in element.NonCuboid.Faces)
                {
                    if (!string.IsNullOrWhiteSpace(face.Texture)) codes.Add(face.Texture);
                }
            }
        }

        return codes;
    }

    private static IEnumerable<string> ModelSourceTextureCodes(ModelDocumentData doc)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ModelTextureEntry texture in doc.Textures)
        {
            if (!string.IsNullOrWhiteSpace(texture.Code) && seen.Add(texture.Code)) yield return texture.Code;
        }
        foreach (string code in doc.TextureSizes.Keys)
        {
            if (!string.IsNullOrWhiteSpace(code) && seen.Add(code)) yield return code;
        }
        foreach (ModelElementData element in doc.EnumerateElements())
        {
            if (element.NonCuboid?.Editable != true) continue;
            foreach (ModelMeshFaceData face in element.NonCuboid.Faces)
            {
                if (!string.IsNullOrWhiteSpace(face.Texture) && seen.Add(face.Texture)) yield return face.Texture;
            }
        }
    }

    private static bool ModelImportTextureCompatible(ModelDocumentData targetDoc, ModelTextureEntry targetTexture, string sourcePath, int[] sourceSize)
    {
        return string.Equals(targetTexture.Path.Trim(), sourcePath.Trim(), StringComparison.OrdinalIgnoreCase) &&
            ModelTextureSizeEquals(ModelEffectiveTextureSize(targetDoc, targetTexture.Code), sourceSize);
    }

    private static int[] ModelEffectiveTextureSize(ModelDocumentData doc, string code)
    {
        if (doc.TextureSizes.TryGetValue(code, out int[]? size) && size.Length >= 2 && size[0] > 0 && size[1] > 0)
        {
            return [size[0], size[1]];
        }

        return [Math.Max(1, doc.TextureWidth), Math.Max(1, doc.TextureHeight)];
    }

    private static void ModelApplyImportedTextureSize(ModelDocumentData targetDoc, string targetCode, int[] sourceSize)
    {
        int width = sourceSize.Length >= 1 ? Math.Max(1, sourceSize[0]) : Math.Max(1, targetDoc.TextureWidth);
        int height = sourceSize.Length >= 2 ? Math.Max(1, sourceSize[1]) : Math.Max(1, targetDoc.TextureHeight);
        if (width != Math.Max(1, targetDoc.TextureWidth) || height != Math.Max(1, targetDoc.TextureHeight))
        {
            targetDoc.TextureSizes[targetCode] = [width, height];
        }
    }

    private static bool ModelTextureSizeEquals(int[] left, int[] right)
    {
        return left.Length >= 2 && right.Length >= 2 && left[0] == right[0] && left[1] == right[1];
    }

    private static string ModelReserveUniqueTextureCode(HashSet<string> reservedCodes, string desired)
    {
        desired = string.IsNullOrWhiteSpace(desired) ? "texture" : desired.Trim();
        if (reservedCodes.Add(desired)) return desired;

        for (int counter = 2; counter < 10000; counter++)
        {
            string candidate = $"{desired}{counter}";
            if (reservedCodes.Add(candidate)) return candidate;
        }

        string fallback = $"{desired}_{Guid.NewGuid():N}"[..Math.Min(desired.Length + 9, desired.Length + 33)];
        reservedCodes.Add(fallback);
        return fallback;
    }

    private void ModelCreateNewDocument()
    {
        if (ModelIsMeshLibMode)
        {
            ModelDocumentData meshDoc = new()
            {
                IsNew = true,
                Domain = "game",
                AssetPath = "shapes/block/new-meshlib-shape.json",
                SourceText = ""
            };
            meshDoc.Textures.Add(new ModelTextureEntry { Code = "all", Path = "" });
            ModelElementData meshElement = new()
            {
                Name = "Mesh1",
                From = [0d, 0d, 0d],
                To = [16d, 16d, 16d],
                RotationOrigin = [8d, 8d, 8d],
                NonCuboid = ModelCreateBoxMesh([0d, 0d, 0d], [16d, 16d, 16d], "all")
            };
            meshDoc.Roots.Add(meshElement);
            ModelSetDocument(meshDoc);
            _modelStatus = "Created new MeshLib shape document.";
            return;
        }

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
        if (ModelDocumentContainsNonCuboid(doc)) _modelEditorMode = ModelEditorMode.MeshLib;
        ModelClearMeshComponentSelection();
        ModelSelectElement(doc.Roots.FirstOrDefault());
        _modelSelectedTextureCode = doc.Textures.FirstOrDefault()?.Code ?? "";
        _modelUndoStack.Clear();
        _modelRedoStack.Clear();
        _modelPendingEditSnapshot = null;
        _modelPreviewDirty = true;
        _modelJsonBufferStale = true;
        _modelReparentSource = null;
        ModelInvalidateGeneratorPreviews();
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

    private void ModelCutSelectedElements(int partsX, int partsY, int partsZ)
    {
        if (_modelDoc == null) return;

        partsX = ModelNormalizeCutParts(partsX);
        partsY = ModelNormalizeCutParts(partsY);
        partsZ = ModelNormalizeCutParts(partsZ);
        _modelCutPartsX = partsX;
        _modelCutPartsY = partsY;
        _modelCutPartsZ = partsZ;

        if (!ModelCanCutSelection(partsX, partsY, partsZ, out string reason))
        {
            _modelStatus = reason;
            return;
        }

        List<ModelElementData> targets = ModelEffectiveSelectedRoots();
        HashSet<ModelElementData> targetSet = new(targets);
        HashSet<string> reservedNames = new(
            _modelDoc.EnumerateElements()
                .Where(element => !targetSet.Contains(element))
                .Select(element => element.Name),
            StringComparer.OrdinalIgnoreCase);

        ModelBeginEdit();
        List<ModelElementData> created = [];
        foreach (ModelElementData target in targets)
        {
            List<ModelElementData> siblings = target.Parent?.Children ?? _modelDoc.Roots;
            int insertIndex = siblings.IndexOf(target);
            if (insertIndex < 0) continue;

            List<ModelElementData> pieces = ModelBuildCutPieces(
                target,
                partsX,
                partsY,
                partsZ,
                desired => ModelReserveUniqueElementName(reservedNames, desired));
            ModelAttachCutChildrenToPieces(target, pieces);
            foreach (ModelElementData piece in pieces)
            {
                piece.Parent = target.Parent;
            }

            siblings.RemoveAt(insertIndex);
            siblings.InsertRange(insertIndex, pieces);
            created.AddRange(pieces);
            if (ReferenceEquals(_modelReparentSource, target)) _modelReparentSource = null;
        }

        if (created.Count == 0)
        {
            ModelCancelEdit();
            return;
        }

        ModelSelectElements(created, created[0]);
        ModelMarkChanged();
        ModelEndEdit(created.Count == 1 ? "Cut element" : "Cut selected elements");
        _modelStatus = $"Cut {targets.Count} element(s) into {created.Count} separate element(s).";
    }

    private void ModelCutElementAtCoordinate(ModelElementData element, int axis, double coordinate)
    {
        if (_modelDoc == null) return;
        axis = Math.Clamp(axis, 0, 2);
        coordinate = Math.Round(coordinate, 6);

        if (!ModelCanCutElementAtCoordinate(element, axis, coordinate, out string reason))
        {
            _modelStatus = reason;
            return;
        }

        List<ModelElementData> siblings = element.Parent?.Children ?? _modelDoc.Roots;
        int insertIndex = siblings.IndexOf(element);
        if (insertIndex < 0)
        {
            _modelStatus = "Could not cut: element is no longer in the document.";
            return;
        }

        HashSet<string> reservedNames = new(
            _modelDoc.EnumerateElements()
                .Where(candidate => !ReferenceEquals(candidate, element))
                .Select(candidate => candidate.Name),
            StringComparer.OrdinalIgnoreCase);
        List<ModelElementData> pieces = ModelBuildCutPiecesAtCoordinate(
            element,
            axis,
            coordinate,
            desired => ModelReserveUniqueElementName(reservedNames, desired));
        if (pieces.Count != 2)
        {
            _modelStatus = "Could not cut: preview coordinate is outside the element.";
            return;
        }

        ModelBeginEdit();
        ModelAttachCutChildrenToPieces(element, pieces);
        foreach (ModelElementData piece in pieces)
        {
            piece.Parent = element.Parent;
        }
        siblings.RemoveAt(insertIndex);
        siblings.InsertRange(insertIndex, pieces);
        if (ReferenceEquals(_modelReparentSource, element)) _modelReparentSource = null;
        ModelSelectElements(pieces, pieces[0]);
        ModelMarkChanged();
        ModelEndEdit("Viewport cut element");
        _modelStatus = $"Cut {element.Name} on {ModelAxisName(axis)} at {coordinate:0.###}.";
    }

    private void ModelAddChiselMicroblock(ModelElementData element, double[] from, double[] to)
    {
        if (_modelDoc == null) return;

        if (ModelChiselWouldOverlap(element, from, to))
        {
            _modelStatus = "Chisel add skipped: that microblock space is already occupied.";
            return;
        }

        List<ModelElementData> siblings = element.Parent?.Children ?? _modelDoc.Roots;
        int insertIndex = siblings.IndexOf(element);
        if (insertIndex < 0)
        {
            _modelStatus = "Could not chisel: element is no longer in the document.";
            return;
        }

        HashSet<string> reservedNames = new(_modelDoc.EnumerateElements().Select(candidate => candidate.Name), StringComparer.OrdinalIgnoreCase);
        string texture = ModelResolveChiselTexture(element);
        ModelElementData microblock = ModelCreateChiselMicroblock(
            element,
            from,
            to,
            texture,
            desired => ModelReserveUniqueElementName(reservedNames, desired));

        ModelBeginEdit();
        siblings.Insert(insertIndex + 1, microblock);
        ModelElementData merged = ModelMergeChiselSiblings(siblings, microblock);
        ModelSelectElement(merged);
        ModelMarkChanged();
        ModelEndEdit("Chisel add microblock");
        _modelStatus = ReferenceEquals(merged, microblock)
            ? $"Added chisel microblock {microblock.Name} with texture '{texture}'."
            : $"Added and merged chisel microblock into {merged.Name} with texture '{texture}'.";
    }

    private void ModelRemoveChiselMicroblock(ModelElementData element, double[] removeFrom, double[] removeTo)
    {
        if (_modelDoc == null) return;

        if (element.Children.Count > 0)
        {
            _modelStatus = $"'{element.Name}' has children; unparent them before chiseling it.";
            return;
        }

        List<ModelElementData> siblings = element.Parent?.Children ?? _modelDoc.Roots;
        int insertIndex = siblings.IndexOf(element);
        if (insertIndex < 0)
        {
            _modelStatus = "Could not chisel: element is no longer in the document.";
            return;
        }

        HashSet<string> reservedNames = new(
            _modelDoc.EnumerateElements()
                .Where(candidate => !ReferenceEquals(candidate, element))
                .Select(candidate => candidate.Name),
            StringComparer.OrdinalIgnoreCase);
        string texture = ModelResolveChiselTexture(element);
        List<ModelElementData> pieces = ModelBuildChiselRemovalPieces(
            element,
            removeFrom,
            removeTo,
            texture,
            desired => ModelReserveUniqueElementName(reservedNames, desired));

        ModelBeginEdit();
        foreach (ModelElementData piece in pieces)
        {
            piece.Parent = element.Parent;
        }
        siblings.RemoveAt(insertIndex);
        if (pieces.Count > 0)
        {
            siblings.InsertRange(insertIndex, pieces);
            ModelSelectElements(pieces, pieces[0]);
        }
        else
        {
            ModelSelectElement(element.Parent ?? _modelDoc.Roots.FirstOrDefault());
        }
        if (ReferenceEquals(_modelReparentSource, element)) _modelReparentSource = null;
        ModelMarkChanged();
        ModelEndEdit("Chisel remove microblock");
        _modelStatus = pieces.Count == 0
            ? $"Removed {element.Name}."
            : $"Removed one chisel microblock from {element.Name}.";
    }

    private string ModelResolveChiselTexture(ModelElementData? source)
    {
        if (!string.IsNullOrWhiteSpace(_modelChiselTexture)) return _modelChiselTexture.Trim();

        string texture = source == null ? "" : ModelBestElementTexture(source);
        if (!string.IsNullOrWhiteSpace(texture)) return texture;
        return _modelDoc?.Textures.FirstOrDefault()?.Code ?? "";
    }

    private bool ModelChiselWouldOverlap(ModelElementData template, double[] from, double[] to)
    {
        if (_modelDoc == null) return false;

        foreach (ModelElementData candidate in _modelDoc.EnumerateElements())
        {
            if (!ModelElementHasRenderableBox(candidate)) continue;
            if (!ReferenceEquals(candidate.Parent, template.Parent)) continue;
            if (!ModelChiselTransformsMatch(candidate, template)) continue;
            if (ModelAxisAlignedBoxesOverlap(from, to, candidate.From, candidate.To)) return true;
        }

        return false;
    }

    private bool ModelCanCutSelection(int partsX, int partsY, int partsZ, out string reason)
    {
        reason = "";
        if (_modelDoc == null)
        {
            reason = "Open a shape first.";
            return false;
        }

        partsX = ModelNormalizeCutParts(partsX);
        partsY = ModelNormalizeCutParts(partsY);
        partsZ = ModelNormalizeCutParts(partsZ);
        long piecesPerElement = (long)partsX * partsY * partsZ;
        if (piecesPerElement <= 1)
        {
            reason = "Use at least 2 pieces.";
            return false;
        }
        if (piecesPerElement > ModelCutMaxPiecesPerElement)
        {
            reason = $"Cut is capped at {ModelCutMaxPiecesPerElement} pieces per element.";
            return false;
        }

        List<ModelElementData> targets = ModelEffectiveSelectedRoots();
        if (targets.Count == 0)
        {
            reason = "Select an element to cut.";
            return false;
        }

        foreach (ModelElementData target in targets)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                int parts = axis switch
                {
                    0 => partsX,
                    1 => partsY,
                    _ => partsZ
                };
                if (parts <= 1) continue;
                if (target.To[axis] - target.From[axis] <= 0.000001)
                {
                    reason = $"'{target.Name}' has no positive {ModelAxisName(axis)} size to cut.";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ModelCanCutElementAtCoordinate(ModelElementData element, int axis, double coordinate, out string reason)
    {
        reason = "";
        if (!ModelIsCutCoordinateInside(element, axis, coordinate))
        {
            reason = $"Cut line is too close to the {ModelAxisName(axis)} edge.";
            return false;
        }

        return true;
    }

    private static bool ModelIsCutCoordinateInside(ModelElementData element, int axis, double coordinate)
    {
        axis = Math.Clamp(axis, 0, 2);
        double size = element.To[axis] - element.From[axis];
        double margin = Math.Max(0.0001, Math.Min(0.01, Math.Abs(size) * 0.001));
        return coordinate > element.From[axis] + margin &&
            coordinate < element.To[axis] - margin;
    }

    private static int ModelNormalizeCutParts(int parts)
    {
        return Math.Clamp(parts, 1, 64);
    }

    private static List<ModelElementData> ModelBuildCutPieces(
        ModelElementData source,
        int partsX,
        int partsY,
        int partsZ,
        System.Func<string, string>? reserveName = null)
    {
        partsX = ModelNormalizeCutParts(partsX);
        partsY = ModelNormalizeCutParts(partsY);
        partsZ = ModelNormalizeCutParts(partsZ);

        double[][] bounds =
        [
            ModelBuildCutBounds(source.From[0], source.To[0], partsX),
            ModelBuildCutBounds(source.From[1], source.To[1], partsY),
            ModelBuildCutBounds(source.From[2], source.To[2], partsZ)
        ];

        List<ModelElementData> pieces = [];
        for (int x = 0; x < partsX; x++)
        {
            for (int y = 0; y < partsY; y++)
            {
                for (int z = 0; z < partsZ; z++)
                {
                    ModelElementData piece = source.CloneShallow();
                    piece.Name = reserveName?.Invoke(ModelCutPieceName(source.Name, x, y, z, partsX, partsY, partsZ))
                        ?? ModelCutPieceName(source.Name, x, y, z, partsX, partsY, partsZ);
                    piece.From = [bounds[0][x], bounds[1][y], bounds[2][z]];
                    piece.To = [bounds[0][x + 1], bounds[1][y + 1], bounds[2][z + 1]];
                    piece.Parent = null;
                    piece.Children.Clear();
                    pieces.Add(piece);
                }
            }
        }

        return pieces;
    }

    private static List<ModelElementData> ModelBuildCutPiecesAtCoordinate(
        ModelElementData source,
        int axis,
        double coordinate,
        System.Func<string, string>? reserveName = null)
    {
        axis = Math.Clamp(axis, 0, 2);
        coordinate = Math.Round(coordinate, 6);
        if (!ModelIsCutCoordinateInside(source, axis, coordinate)) return [];

        int[] partIndices = [0, 0, 0];
        int[] partCounts = [1, 1, 1];
        partCounts[axis] = 2;

        ModelElementData first = source.CloneShallow();
        partIndices[axis] = 0;
        first.Name = reserveName?.Invoke(ModelCutPieceName(source.Name, partIndices[0], partIndices[1], partIndices[2], partCounts[0], partCounts[1], partCounts[2]))
            ?? ModelCutPieceName(source.Name, partIndices[0], partIndices[1], partIndices[2], partCounts[0], partCounts[1], partCounts[2]);
        first.To[axis] = coordinate;
        first.Parent = null;
        first.Children.Clear();

        ModelElementData second = source.CloneShallow();
        partIndices[axis] = 1;
        second.Name = reserveName?.Invoke(ModelCutPieceName(source.Name, partIndices[0], partIndices[1], partIndices[2], partCounts[0], partCounts[1], partCounts[2]))
            ?? ModelCutPieceName(source.Name, partIndices[0], partIndices[1], partIndices[2], partCounts[0], partCounts[1], partCounts[2]);
        second.From[axis] = coordinate;
        second.Parent = null;
        second.Children.Clear();

        return [first, second];
    }

    private static void ModelAttachCutChildrenToPieces(ModelElementData source, IReadOnlyList<ModelElementData> pieces)
    {
        if (source.Children.Count == 0 || pieces.Count == 0) return;

        List<ModelElementData> children = [.. source.Children];
        source.Children.Clear();
        foreach (ModelElementData child in children)
        {
            ModelElementData parent = ModelFindCutPieceForChild(source, child, pieces);
            child.Parent = parent;
            parent.Children.Add(child);
        }
    }

    private static ModelElementData ModelFindCutPieceForChild(ModelElementData source, ModelElementData child, IReadOnlyList<ModelElementData> pieces)
    {
        double[] childCenter =
        [
            (child.From[0] + child.To[0]) * 0.5,
            (child.From[1] + child.To[1]) * 0.5,
            (child.From[2] + child.To[2]) * 0.5
        ];
        ModelElementData? piece = pieces.FirstOrDefault(candidate => ModelPointInsideBox(childCenter, candidate.From, candidate.To));
        if (piece != null) return piece;

        if (source.RotationOrigin != null)
        {
            piece = pieces.FirstOrDefault(candidate => ModelPointInsideBox(source.RotationOrigin, candidate.From, candidate.To));
            if (piece != null) return piece;
        }

        return pieces[0];
    }

    private static bool ModelPointInsideBox(double[] point, double[] from, double[] to)
    {
        const double epsilon = 0.000001;
        for (int axis = 0; axis < 3; axis++)
        {
            double min = Math.Min(from[axis], to[axis]) - epsilon;
            double max = Math.Max(from[axis], to[axis]) + epsilon;
            if (point[axis] < min || point[axis] > max) return false;
        }

        return true;
    }

    private static ModelElementData ModelCreateChiselMicroblock(
        ModelElementData template,
        double[] from,
        double[] to,
        string texture,
        System.Func<string, string>? reserveName = null)
    {
        ModelElementData element = new()
        {
            Name = reserveName?.Invoke(ModelChiselPieceName(template.Name, "add")) ?? ModelChiselPieceName(template.Name, "add"),
            From = ModelRoundVector(from),
            To = ModelRoundVector(to),
            RotationOrigin = (double[]?)template.RotationOrigin?.Clone(),
            RotationX = template.RotationX,
            RotationY = template.RotationY,
            RotationZ = template.RotationZ,
            Shade = template.Shade,
            StepParentName = template.StepParentName,
            Parent = template.Parent
        };
        ModelApplyContinuousUvToElement(element, texture, replaceTexture: true);
        return element;
    }

    private static List<ModelElementData> ModelBuildChiselRemovalPieces(
        ModelElementData source,
        double[] removeFrom,
        double[] removeTo,
        string texture,
        System.Func<string, string>? reserveName = null)
    {
        double[] sourceFrom = source.From;
        double[] sourceTo = source.To;
        double[] cutFrom =
        [
            Math.Clamp(Math.Min(removeFrom[0], removeTo[0]), sourceFrom[0], sourceTo[0]),
            Math.Clamp(Math.Min(removeFrom[1], removeTo[1]), sourceFrom[1], sourceTo[1]),
            Math.Clamp(Math.Min(removeFrom[2], removeTo[2]), sourceFrom[2], sourceTo[2])
        ];
        double[] cutTo =
        [
            Math.Clamp(Math.Max(removeFrom[0], removeTo[0]), sourceFrom[0], sourceTo[0]),
            Math.Clamp(Math.Max(removeFrom[1], removeTo[1]), sourceFrom[1], sourceTo[1]),
            Math.Clamp(Math.Max(removeFrom[2], removeTo[2]), sourceFrom[2], sourceTo[2])
        ];

        if (!ModelAxisAlignedBoxesOverlap(sourceFrom, sourceTo, cutFrom, cutTo)) return [];

        List<ModelElementData> pieces = [];
        void AddPiece(double x0, double y0, double z0, double x1, double y1, double z1, string suffix)
        {
            if (x1 - x0 <= 0.000001 || y1 - y0 <= 0.000001 || z1 - z0 <= 0.000001) return;

            ModelElementData piece = source.CloneShallow();
            piece.Name = reserveName?.Invoke(ModelChiselPieceName(source.Name, suffix)) ?? ModelChiselPieceName(source.Name, suffix);
            piece.From = ModelRoundVector([x0, y0, z0]);
            piece.To = ModelRoundVector([x1, y1, z1]);
            piece.Parent = null;
            piece.Children.Clear();
            ModelApplyContinuousUvToElement(piece, texture, replaceTexture: false);
            pieces.Add(piece);
        }

        double x0 = sourceFrom[0];
        double y0 = sourceFrom[1];
        double z0 = sourceFrom[2];
        double x1 = sourceTo[0];
        double y1 = sourceTo[1];
        double z1 = sourceTo[2];
        double cx0 = cutFrom[0];
        double cy0 = cutFrom[1];
        double cz0 = cutFrom[2];
        double cx1 = cutTo[0];
        double cy1 = cutTo[1];
        double cz1 = cutTo[2];

        AddPiece(x0, y0, z0, cx0, y1, z1, "x0");
        AddPiece(cx1, y0, z0, x1, y1, z1, "x1");
        AddPiece(cx0, y0, z0, cx1, cy0, z1, "y0");
        AddPiece(cx0, cy1, z0, cx1, y1, z1, "y1");
        AddPiece(cx0, cy0, z0, cx1, cy1, cz0, "z0");
        AddPiece(cx0, cy0, cz1, cx1, cy1, z1, "z1");
        return pieces;
    }

    private static ModelElementData ModelMergeChiselSiblings(List<ModelElementData> siblings, ModelElementData preferred)
    {
        return ModelMergeChiselElements(siblings, preferred) ?? preferred;
    }

    private static ModelElementData? ModelMergeChiselElements(System.Collections.IList siblings, ModelElementData? preferred)
    {
        if (siblings.Count < 2) return preferred;

        bool merged;
        do
        {
            merged = false;
            for (int leftIndex = 0; leftIndex < siblings.Count && !merged; leftIndex++)
            {
                if (siblings[leftIndex] is not ModelElementData left) continue;
                for (int rightIndex = leftIndex + 1; rightIndex < siblings.Count; rightIndex++)
                {
                    if (siblings[rightIndex] is not ModelElementData right) continue;
                    if (!ModelTryMergeChiselElements(left, right, out double[] mergedFrom, out double[] mergedTo, out string texture)) continue;

                    ModelElementData keep = ReferenceEquals(right, preferred) ? right : left;
                    ModelElementData drop = ReferenceEquals(keep, left) ? right : left;
                    keep.From = mergedFrom;
                    keep.To = mergedTo;
                    ModelApplyContinuousUvToElement(keep, texture, replaceTexture: true);
                    siblings.Remove(drop);
                    if (ReferenceEquals(drop, preferred)) preferred = keep;
                    merged = true;
                    break;
                }
            }
        }
        while (merged);

        return preferred;
    }

    private static bool ModelTryMergeChiselElements(
        ModelElementData left,
        ModelElementData right,
        out double[] mergedFrom,
        out double[] mergedTo,
        out string texture)
    {
        mergedFrom = new double[3];
        mergedTo = new double[3];
        texture = "";

        if (!ModelChiselMergeMetadataMatches(left, right)) return false;
        texture = ModelCommonFaceTexture(left);
        if (string.IsNullOrWhiteSpace(texture) || !string.Equals(texture, ModelCommonFaceTexture(right), StringComparison.Ordinal)) return false;
        if (!ModelChiselFacesMergeCompatible(left, right, texture)) return false;

        int mergeAxis = -1;
        for (int axis = 0; axis < 3; axis++)
        {
            bool sameSpan = Math.Abs(left.From[axis] - right.From[axis]) <= 0.000001 &&
                Math.Abs(left.To[axis] - right.To[axis]) <= 0.000001;
            bool touching = Math.Abs(left.To[axis] - right.From[axis]) <= 0.000001 ||
                Math.Abs(right.To[axis] - left.From[axis]) <= 0.000001;
            if (sameSpan) continue;
            if (!touching || mergeAxis >= 0) return false;
            mergeAxis = axis;
        }

        if (mergeAxis < 0) return false;
        for (int axis = 0; axis < 3; axis++)
        {
            if (axis != mergeAxis)
            {
                if (Math.Abs(left.From[axis] - right.From[axis]) > 0.000001 ||
                    Math.Abs(left.To[axis] - right.To[axis]) > 0.000001)
                {
                    return false;
                }
            }

            mergedFrom[axis] = ModelRoundForChisel(Math.Min(left.From[axis], right.From[axis]));
            mergedTo[axis] = ModelRoundForChisel(Math.Max(left.To[axis], right.To[axis]));
        }

        return mergedTo[0] - mergedFrom[0] > 0.000001 &&
            mergedTo[1] - mergedFrom[1] > 0.000001 &&
            mergedTo[2] - mergedFrom[2] > 0.000001;
    }

    private static bool ModelChiselMergeMetadataMatches(ModelElementData left, ModelElementData right)
    {
        return left.Children.Count == 0 &&
            right.Children.Count == 0 &&
            ReferenceEquals(left.Parent, right.Parent) &&
            ModelElementHasRenderableBox(left) &&
            ModelElementHasRenderableBox(right) &&
            ModelChiselTransformsMatch(left, right) &&
            left.Shade == right.Shade &&
            left.Visible == right.Visible &&
            string.Equals(left.StepParentName, right.StepParentName, StringComparison.Ordinal) &&
            JToken.DeepEquals(left.Extra, right.Extra);
    }

    private static bool ModelChiselFacesMergeCompatible(ModelElementData left, ModelElementData right, string texture)
    {
        for (int faceIndex = 0; faceIndex < 6; faceIndex++)
        {
            ModelFaceData? leftFace = left.Faces[faceIndex];
            ModelFaceData? rightFace = right.Faces[faceIndex];
            if (leftFace == null || rightFace == null) return false;
            if (!leftFace.Enabled || !rightFace.Enabled) return false;
            if (!string.Equals(leftFace.Texture, texture, StringComparison.Ordinal) ||
                !string.Equals(rightFace.Texture, texture, StringComparison.Ordinal))
            {
                return false;
            }
            if (Math.Abs(leftFace.Rotation - rightFace.Rotation) > 0.000001 ||
                leftFace.Glow != rightFace.Glow ||
                !JToken.DeepEquals(leftFace.Extra, rightFace.Extra))
            {
                return false;
            }
        }

        return true;
    }

    private static double[] ModelBuildCutBounds(double from, double to, int parts)
    {
        parts = ModelNormalizeCutParts(parts);
        double[] bounds = new double[parts + 1];
        bounds[0] = from;
        bounds[parts] = to;
        double span = to - from;
        for (int index = 1; index < parts; index++)
        {
            bounds[index] = Math.Round(from + span * index / parts, 6);
        }

        return bounds;
    }

    private static string ModelCutPieceName(string baseName, int x, int y, int z, int partsX, int partsY, int partsZ)
    {
        string name = string.IsNullOrWhiteSpace(baseName) ? "Element" : baseName.Trim();
        List<string> suffix = [];
        if (partsX > 1) suffix.Add($"x{x + 1}");
        if (partsY > 1) suffix.Add($"y{y + 1}");
        if (partsZ > 1) suffix.Add($"z{z + 1}");
        return suffix.Count == 0 ? name : $"{name}_{string.Join("_", suffix)}";
    }

    private static string ModelChiselPieceName(string baseName, string suffix)
    {
        string name = string.IsNullOrWhiteSpace(baseName) ? "Element" : baseName.Trim();
        return $"{name}_chisel_{suffix}";
    }

    private static string ModelReserveUniqueElementName(HashSet<string> reservedNames, string desired)
    {
        desired = string.IsNullOrWhiteSpace(desired) ? "Element" : desired.Trim();
        if (reservedNames.Add(desired)) return desired;

        for (int counter = 2; counter < 10000; counter++)
        {
            string candidate = $"{desired}{counter}";
            if (reservedNames.Add(candidate)) return candidate;
        }

        string fallback = $"{desired}_{Guid.NewGuid():N}"[..Math.Min(desired.Length + 9, desired.Length + 33)];
        reservedNames.Add(fallback);
        return fallback;
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

    private static bool ModelChiselTransformsMatch(ModelElementData left, ModelElementData right)
    {
        return Math.Abs(left.RotationX - right.RotationX) <= 0.000001 &&
            Math.Abs(left.RotationY - right.RotationY) <= 0.000001 &&
            Math.Abs(left.RotationZ - right.RotationZ) <= 0.000001 &&
            ModelVectorsEqual(left.RotationOrigin, right.RotationOrigin);
    }

    private static bool ModelVectorsEqual(double[]? left, double[]? right)
    {
        if (left == null || right == null) return left == null && right == null;
        return left.Length >= 3 && right.Length >= 3 &&
            Math.Abs(left[0] - right[0]) <= 0.000001 &&
            Math.Abs(left[1] - right[1]) <= 0.000001 &&
            Math.Abs(left[2] - right[2]) <= 0.000001;
    }

    private static bool ModelAxisAlignedBoxesOverlap(double[] leftFrom, double[] leftTo, double[] rightFrom, double[] rightTo)
    {
        return ModelAxisOverlap(leftFrom[0], leftTo[0], rightFrom[0], rightTo[0]) > 0.000001 &&
            ModelAxisOverlap(leftFrom[1], leftTo[1], rightFrom[1], rightTo[1]) > 0.000001 &&
            ModelAxisOverlap(leftFrom[2], leftTo[2], rightFrom[2], rightTo[2]) > 0.000001;
    }

    private static double ModelAxisOverlap(double leftFrom, double leftTo, double rightFrom, double rightTo)
    {
        return Math.Min(leftTo, rightTo) - Math.Max(leftFrom, rightFrom);
    }

    private static string ModelBestElementTexture(ModelElementData element)
    {
        return element.Faces
            .Where(face => face != null && !string.IsNullOrWhiteSpace(face.Texture))
            .GroupBy(face => face!.Texture, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault() ?? "";
    }

    private static void ModelApplyContinuousUvToElement(ModelElementData element, string texture, bool replaceTexture)
    {
        for (int faceIndex = 0; faceIndex < 6; faceIndex++)
        {
            ModelFaceData face = element.Faces[faceIndex] ?? new ModelFaceData();
            if (replaceTexture || string.IsNullOrWhiteSpace(face.Texture))
            {
                face.Texture = texture;
            }
            face.Enabled = true;
            float[] uv = ModelContinuousUvForFace(element, faceIndex);
            face.Uv[0] = uv[0];
            face.Uv[1] = uv[1];
            face.Uv[2] = uv[2];
            face.Uv[3] = uv[3];
            element.Faces[faceIndex] = face;
        }
    }

    private static float[] ModelContinuousUvForFace(ModelElementData element, int faceIndex)
    {
        return faceIndex switch
        {
            0 or 2 => [(float)element.From[0], (float)element.From[1], (float)element.To[0], (float)element.To[1]],
            1 or 3 => [(float)element.From[2], (float)element.From[1], (float)element.To[2], (float)element.To[1]],
            _ => [(float)element.From[0], (float)element.From[2], (float)element.To[0], (float)element.To[2]]
        };
    }

    private static double[] ModelRoundVector(double[] vector)
    {
        return [ModelRoundForChisel(vector[0]), ModelRoundForChisel(vector[1]), ModelRoundForChisel(vector[2])];
    }

    private static double ModelRoundForChisel(double value)
    {
        return Math.Abs(value) < 0.000001 ? 0.0 : Math.Round(value, 6);
    }

    private void ModelMirrorElementSubtree(ModelElementData element, int axis)
    {
        if (element.NonCuboid?.Editable == true)
        {
            foreach (double[] vertex in element.NonCuboid.Vertices)
            {
                if (vertex.Length > axis) vertex[axis] = -vertex[axis];
            }
            foreach (ModelMeshFaceData face in element.NonCuboid.Faces)
            {
                Array.Reverse(face.Vertices);
                face.Uv?.Reverse();
            }
        }
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
        bool compensated = ModelTryPreserveReparentTransform(element, newParent);
        List<ModelElementData> oldSiblings = element.Parent?.Children ?? _modelDoc.Roots;
        oldSiblings.Remove(element);
        element.Parent = newParent;
        (newParent?.Children ?? _modelDoc.Roots).Add(element);
        ModelMarkChanged();
        ModelEndEdit("Reparent element");
        _modelStatus = compensated
            ? $"Reparented {element.Name}; transform was adjusted to keep its position."
            : $"Reparented {element.Name}. Could not compensate transform; local values were kept as-is.";
    }

    private bool ModelTryPreserveReparentTransform(ModelElementData element, ModelElementData? newParent)
    {
        try
        {
            Matrixd oldWorld = ModelMatrixd(ModelComputeElementMatrix(element));
            Matrixd newParentWorld = newParent == null
                ? Matrixd.Create().Identity()
                : ModelMatrixd(ModelComputeElementMatrix(newParent));
            Matrixd inverseNewParent = newParentWorld.Clone().Invert();
            Matrixd newLocal = oldWorld.Clone().ReverseMul(inverseNewParent.Values);

            double[] oldOrigin = ModelEffectiveRotationOrigin(element);
            Vec3d oldOriginWorld = ModelTransformPoint(ModelComputeParentChainMatrix(element), oldOrigin);
            Vec3d newOriginLocal = ModelTransformPoint(inverseNewParent, oldOriginWorld);

            Vec3d oldLocalBoxOrigin = ModelTransformPoint(newLocal, new Vec3d(0, 0, 0));
            RigIkMatrix3 newRotation = RigIkMatrix3.FromMatrixd(newLocal).Orthonormalized();
            RigIkMatrix3 inverseRotation = newRotation.Inverted().Orthonormalized();
            Vec3d fromOffset = inverseRotation.TransformDirection(Sub(oldLocalBoxOrigin, newOriginLocal));
            Vec3d newFrom = Add(newOriginLocal, fromOffset);
            Vec3d euler = newRotation.ToEulerDegrees();

            double sizeX = element.SizeX;
            double sizeY = element.SizeY;
            double sizeZ = element.SizeZ;
            double[] previousFrom = (double[])element.From.Clone();
            bool hadOrigin = element.RotationOrigin != null;

            element.From[0] = ModelRoundForReparent(newFrom.X);
            element.From[1] = ModelRoundForReparent(newFrom.Y);
            element.From[2] = ModelRoundForReparent(newFrom.Z);
            element.To[0] = ModelRoundForReparent(newFrom.X + sizeX);
            element.To[1] = ModelRoundForReparent(newFrom.Y + sizeY);
            element.To[2] = ModelRoundForReparent(newFrom.Z + sizeZ);
            if (element.NonCuboid?.Editable == true)
            {
                double dx = element.From[0] - previousFrom[0];
                double dy = element.From[1] - previousFrom[1];
                double dz = element.From[2] - previousFrom[2];
                foreach (double[] vertex in element.NonCuboid.Vertices)
                {
                    if (vertex.Length < 3) continue;
                    vertex[0] = ModelRoundForReparent(vertex[0] + dx);
                    vertex[1] = ModelRoundForReparent(vertex[1] + dy);
                    vertex[2] = ModelRoundForReparent(vertex[2] + dz);
                }
            }
            element.RotationX = ModelWrapDegrees(euler.X);
            element.RotationY = ModelWrapDegrees(euler.Y);
            element.RotationZ = ModelWrapDegrees(euler.Z);
            bool hasRotation = Math.Abs(element.RotationX) > 0.0001 || Math.Abs(element.RotationY) > 0.0001 || Math.Abs(element.RotationZ) > 0.0001;
            element.RotationOrigin = hadOrigin || hasRotation
                ? [ModelRoundForReparent(newOriginLocal.X), ModelRoundForReparent(newOriginLocal.Y), ModelRoundForReparent(newOriginLocal.Z)]
                : null;
            return true;
        }
        catch (Exception exception)
        {
            _modelDiagnostics.Exception("Reparent transform compensation failed", exception);
            return false;
        }
    }

    private static Matrixd ModelMatrixd(Matrixf matrix)
    {
        return Matrixd.Create().Set(matrix.Values);
    }

    private static Vec3d ModelTransformPoint(Matrixd matrix, Vec3d point)
    {
        Vec4d transformed = matrix.TransformVector(new Vec4d(point.X / ModelUnitsPerBlock, point.Y / ModelUnitsPerBlock, point.Z / ModelUnitsPerBlock, 1.0));
        return new Vec3d(transformed.X * ModelUnitsPerBlock, transformed.Y * ModelUnitsPerBlock, transformed.Z * ModelUnitsPerBlock);
    }

    private static Vec3d ModelTransformPoint(Matrixf matrix, double[] pointUnits)
    {
        Vec4f transformed = matrix.TransformVector(new Vec4f(
            (float)(pointUnits[0] / ModelUnitsPerBlock),
            (float)(pointUnits[1] / ModelUnitsPerBlock),
            (float)(pointUnits[2] / ModelUnitsPerBlock),
            1f));
        return new Vec3d(transformed.X * ModelUnitsPerBlock, transformed.Y * ModelUnitsPerBlock, transformed.Z * ModelUnitsPerBlock);
    }

    private static double ModelRoundForReparent(double value)
    {
        return Math.Abs(value) < 0.000001 ? 0.0 : Math.Round(value, 6);
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
        // Clamp the UV span to the texture so large, scaled-up or overlapping elements stay fully textured.
        // Auto-UV maps 1 shape unit to 1 texel; without the clamp, anything bigger than the texture samples
        // outside the atlas and renders invisible. Small elements keep their 1:1 mapping (clamp is a no-op).
        (int texWidth, int texHeight) = _modelDoc?.GetTextureSize(face.Texture) ?? (16, 16);
        face.Uv[0] = 0f;
        face.Uv[1] = 0f;
        face.Uv[2] = (float)Math.Clamp(width, 0.0, texWidth);
        face.Uv[3] = (float)Math.Clamp(height, 0.0, texHeight);
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
                ModelPushHistory(_modelUndoStack, ModelCaptureHistoryEntry(label, _modelPendingEditSnapshot));
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

    private static void ModelPushHistory(List<ModelHistoryEntry> stack, ModelHistoryEntry entry)
    {
        stack.Add(entry);
        long retainedCharacters = stack.Sum(historyEntry => (long)historyEntry.Json.Length);
        while (stack.Count > 1 &&
               (stack.Count > ModelHistoryLimit || retainedCharacters > ModelHistoryCharacterBudget))
        {
            retainedCharacters -= stack[0].Json.Length;
            stack.RemoveAt(0);
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
            ModelPushHistory(_modelRedoStack, ModelCaptureHistoryEntry(entry.Label, current));
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
            ModelPushHistory(_modelUndoStack, ModelCaptureHistoryEntry(entry.Label, current));
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
        restored.RecoveryKey = ModelEnsureRecoveryKey(_modelDoc);
        _modelDoc = restored;
        ModelSelectElements(ModelResolveSelectionPaths(entry.SelectionPaths), ModelResolveSelectionPath(entry.SelectionPath));
        if (_modelSelectedElement == null)
        {
            ModelSelectElement(restored.Roots.FirstOrDefault());
        }
        _modelSelectedFace = entry.SelectedFace;
        _modelMeshSelectionMode = entry.MeshSelectionMode;
        ModelClearMeshComponentSelection();
        if (entry.MeshVertices != null)
        {
            foreach (int vertex in entry.MeshVertices) _modelMeshSelectedVertices.Add(vertex);
            _modelMeshVertexSelectionOrder.AddRange(entry.MeshVertices);
            _modelMeshActiveVertex = entry.MeshVertices.LastOrDefault(-1);
        }
        if (entry.MeshEdges != null)
        {
            foreach (int[] edge in entry.MeshEdges) if (edge.Length >= 2) _modelMeshSelectedEdges.Add(ModelMeshEdge.Create(edge[0], edge[1]));
            _modelMeshActiveEdge = _modelMeshSelectedEdges.Count > 0 ? _modelMeshSelectedEdges.Last() : null;
        }
        if (entry.MeshFaces != null)
        {
            foreach (int face in entry.MeshFaces) _modelMeshSelectedFaces.Add(face);
            _modelMeshActiveFace = entry.MeshFaces.LastOrDefault(-1);
        }
        _modelPreviewDirty = true;
        ModelInvalidateGeneratorPreviews();
        _modelJsonBufferStale = true;
        _modelLiveDirty = true;
        _modelLiveChangedAtMs = _api.World?.ElapsedMilliseconds ?? 0;
        _modelReparentSource = null;
    }

    private ModelHistoryEntry ModelCaptureHistoryEntry(string label, string json)
    {
        return new ModelHistoryEntry(
            label,
            json,
            ModelGetSelectionPath(),
            _modelSelectedFace,
            ModelGetSelectionPaths(),
            _modelMeshSelectionMode,
            [.. _modelMeshSelectedVertices.Order()],
            _modelMeshSelectedEdges.OrderBy(edge => edge.A).ThenBy(edge => edge.B).Select(edge => new[] { edge.A, edge.B }).ToArray(),
            [.. _modelMeshSelectedFaces.Order()]);
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
