using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InGameDevTools.Animations;

/// <summary>
/// Model editor workflow tools borrowed from desktop modelling software: clipboard copy/paste of
/// element subtrees (works across shapes and game restarts since it is plain shape JSON), center
/// pivot with position compensation, and batch find/replace renaming.
/// </summary>
public sealed partial class DebugWindowManager
{
    private const string ModelClipboardMarker = "ingamedevtoolsElements";

    private string _modelRenameFind = "";
    private string _modelRenameReplace = "";
    private bool _modelRenameSelectedOnly = true;

    private void ModelCopySelectedElementsToClipboard()
    {
        if (_modelDoc == null) return;

        List<ModelElementData> targets = ModelEffectiveSelectedRoots();
        if (targets.Count == 0)
        {
            _modelStatus = "Nothing selected to copy.";
            return;
        }

        JArray elements = [];
        foreach (ModelElementData element in targets)
        {
            elements.Add(ModelSerializeElement(element, includeInvisible: true));
        }

        JObject payload = new() { [ModelClipboardMarker] = elements };
        ImGui.SetClipboardText(payload.ToString(Formatting.Indented));
        _modelStatus = $"Copied {targets.Count} element(s) to clipboard.";
    }

    private void ModelPasteElementsFromClipboard(ModelElementData? parent)
    {
        if (_modelDoc == null) return;

        string clipboard = ImGui.GetClipboardText() ?? "";
        if (!DevToolsJson.TryParseToken(clipboard, out JToken? token, out string parseError) || token == null)
        {
            _modelStatus = $"Clipboard is not JSON: {parseError}";
            return;
        }

        JArray? elements = token switch
        {
            JObject payload when payload[ModelClipboardMarker] is JArray marked => marked,
            // A whole shape file: take its element list.
            JObject shape when shape["elements"] is JArray shapeElements => shapeElements,
            // A single bare element object.
            JObject single when single["from"] != null && single["to"] != null => [single],
            JArray array => array,
            _ => null
        };

        if (elements == null || elements.Count == 0)
        {
            _modelStatus = "Clipboard JSON does not contain shape elements.";
            return;
        }

        ModelBeginEdit();
        List<ModelElementData> pasted = [];
        try
        {
            foreach (JToken elementToken in elements)
            {
                if (elementToken is not JObject elementJson) continue;
                ModelElementData element = ModelParseElement(elementJson, parent);
                element.Name = ModelGenerateElementName(string.IsNullOrWhiteSpace(element.Name) ? "pasted" : element.Name);
                (parent?.Children ?? _modelDoc.Roots).Add(element);
                pasted.Add(element);
            }
        }
        catch (Exception exception)
        {
            ModelCancelEdit();
            _modelStatus = $"Paste failed: {exception.Message}";
            return;
        }

        if (pasted.Count == 0)
        {
            ModelCancelEdit();
            _modelStatus = "Clipboard JSON contained no element objects.";
            return;
        }

        ModelSelectElements(pasted, pasted[^1]);
        ModelMarkChanged();
        ModelEndEdit("Paste elements");
        _modelStatus = parent == null
            ? $"Pasted {pasted.Count} element(s) at root level."
            : $"Pasted {pasted.Count} element(s) under {parent.Name}.";
    }

    private void ModelCenterPivotSelectedElements()
    {
        if (_modelDoc == null) return;

        List<ModelElementData> targets = ModelSelectedElementsInDocument();
        if (targets.Count == 0)
        {
            _modelStatus = "Nothing selected to center pivots for.";
            return;
        }

        ModelBeginEdit();
        foreach (ModelElementData element in targets)
        {
            double[] oldOrigin = ModelEffectiveRotationOrigin(element);
            double[] newOrigin = ModelDefaultRotationOrigin(element);
            double[] rotation = DevToolsRotationMath.ComposeXyz(element.RotationX, element.RotationY, element.RotationZ);
            (double dx, double dy, double dz) = DevToolsRotationMath.PivotCompensation(
                rotation,
                oldOrigin[0], oldOrigin[1], oldOrigin[2],
                newOrigin[0], newOrigin[1], newOrigin[2]);

            element.From[0] += dx;
            element.From[1] += dy;
            element.From[2] += dz;
            element.To[0] += dx;
            element.To[1] += dy;
            element.To[2] += dz;
            if (element.NonCuboid?.Editable == true)
            {
                foreach (double[] vertex in element.NonCuboid.Vertices)
                {
                    if (vertex.Length < 3) continue;
                    vertex[0] += dx;
                    vertex[1] += dy;
                    vertex[2] += dz;
                }
            }
            // The pivot is the box center of the *moved* box.
            element.RotationOrigin =
            [
                newOrigin[0] + dx,
                newOrigin[1] + dy,
                newOrigin[2] + dz
            ];
        }

        ModelMarkChanged();
        ModelEndEdit("Center pivot");
        _modelStatus = $"Centered pivot for {targets.Count} element(s) without moving them.";
    }

    private void ModelIsolateSelectedElements()
    {
        if (_modelDoc == null) return;

        List<ModelElementData> roots = ModelEffectiveSelectedRoots();
        if (roots.Count == 0)
        {
            _modelStatus = "Nothing selected to isolate.";
            return;
        }

        HashSet<ModelElementData> keepVisible = [];
        foreach (ModelElementData root in roots)
        {
            foreach (ModelElementData element in root.EnumerateSubtree())
            {
                keepVisible.Add(element);
            }
            // Ancestors must stay visible so the selection itself renders.
            for (ModelElementData? ancestor = root.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                keepVisible.Add(ancestor);
            }
        }

        int hidden = 0;
        foreach (ModelElementData element in _modelDoc.EnumerateElements())
        {
            bool visible = keepVisible.Contains(element);
            if (element.Visible && !visible) hidden++;
            element.Visible = visible;
        }

        _modelPreviewDirty = true;
        _modelStatus = $"Isolated selection ({hidden} element(s) hidden). Viewport visibility only; hidden elements still save.";
    }

    private void ModelShowAllElements()
    {
        if (_modelDoc == null) return;

        int shown = 0;
        foreach (ModelElementData element in _modelDoc.EnumerateElements())
        {
            if (!element.Visible) shown++;
            element.Visible = true;
        }

        _modelPreviewDirty = true;
        _modelStatus = shown > 0 ? $"Restored visibility of {shown} element(s)." : "All elements were already visible.";
    }

    private void DrawModelBatchRenamePopup()
    {
        if (!ImGui.BeginPopup("##model-batch-rename-popup")) return;

        try
        {
            ImGui.SeparatorText("Batch rename");
            ImGui.SetNextItemWidth(220f);
            ImGui.InputTextWithHint("##model-rename-find", "Find in names...", ref _modelRenameFind, 128);
            ImGui.SetNextItemWidth(220f);
            ImGui.InputTextWithHint("##model-rename-replace", "Replace with...", ref _modelRenameReplace, 128);
            ImGui.Checkbox("Selected elements only##model-rename-selected", ref _modelRenameSelectedOnly);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Off renames matching elements in the whole shape. Selection includes the selected elements' subtrees.");
            }

            bool canRename = _modelRenameFind.Length > 0;
            if (!canRename) ImGui.BeginDisabled();
            if (ImGui.Button("Replace##model-rename-apply"))
            {
                ModelApplyBatchRename();
                ImGui.CloseCurrentPopup();
            }
            if (!canRename) ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled("Case-insensitive. One undo step.");
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void ModelApplyBatchRename()
    {
        if (_modelDoc == null || _modelRenameFind.Length == 0) return;

        IEnumerable<ModelElementData> scope = _modelRenameSelectedOnly
            ? ModelEffectiveSelectedRoots().SelectMany(element => element.EnumerateSubtree())
            : _modelDoc.EnumerateElements();

        ModelBeginEdit();
        int renamed = 0;
        foreach (ModelElementData element in scope)
        {
            if (!element.Name.Contains(_modelRenameFind, StringComparison.OrdinalIgnoreCase)) continue;
            element.Name = element.Name.Replace(_modelRenameFind, _modelRenameReplace, StringComparison.OrdinalIgnoreCase);
            renamed++;
        }

        if (renamed == 0)
        {
            ModelCancelEdit();
            _modelStatus = $"No element names contain '{_modelRenameFind}'.";
            return;
        }

        ModelMarkChanged();
        ModelEndEdit("Batch rename");
        _modelStatus = $"Renamed {renamed} element(s).";
    }
}
