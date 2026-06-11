using InGameDevTools.Utils;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private enum ModelUvDragMode
    {
        None,
        Move,
        Resize
    }

    private static readonly float[] ModelUvSnapSteps = [0f, 1f, 0.5f, 0.25f];
    private static readonly string[] ModelUvSnapLabels = ["Off", "1", "0.5", "0.25"];

    private readonly Dictionary<string, (int TextureId, int Width, int Height)> _modelUvTextureCache = new(StringComparer.Ordinal);
    private float _modelUvZoom = 18f;
    private NVector2 _modelUvPan = NVector2.Zero;
    private bool _modelUvFitPending = true;
    private int _modelUvSnapIndex = 2;
    private ModelUvDragMode _modelUvDragMode = ModelUvDragMode.None;
    private ModelFaceData? _modelUvDragFace;
    private NVector2 _modelUvDragStartMouse;
    private float[] _modelUvDragStartUv = new float[4];

    private void DrawModelUvPanel()
    {
        if (_modelDoc == null)
        {
            ImGui.TextDisabled("Open a shape or create a new one.");
            return;
        }

        NVector2 available = ImGui.GetContentRegionAvail();
        float listWidth = Math.Clamp(available.X * 0.28f, 220f, 330f);
        DrawModelTextureListPanel(_modelDoc, new NVector2(listWidth, Math.Max(200f, available.Y)));
        ImGui.SameLine();
        DrawModelUvCanvasPanel(_modelDoc, new NVector2(Math.Max(280f, available.X - listWidth - 8f), Math.Max(200f, available.Y)));
    }

    private void DrawModelTextureListPanel(ModelDocumentData doc, NVector2 size)
    {
        ImGui.BeginChild("##model-texture-list", size, true);
        try
        {
            ImGui.SeparatorText("Textures");
            if (ImGui.SmallButton("Add##model-texture-add"))
            {
                ModelBeginEdit();
                string code = ModelGenerateTextureCode(doc);
                doc.Textures.Add(new ModelTextureEntry { Code = code, Path = "" });
                _modelSelectedTextureCode = code;
                ModelMarkChanged();
                ModelEndEdit("Add texture");
            }
            ImGui.SameLine();
            bool hasSelection = doc.Textures.Any(texture => string.Equals(texture.Code, _modelSelectedTextureCode, StringComparison.Ordinal));
            if (!hasSelection) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Remove##model-texture-remove"))
            {
                ModelBeginEdit();
                doc.Textures.RemoveAll(texture => string.Equals(texture.Code, _modelSelectedTextureCode, StringComparison.Ordinal));
                doc.TextureSizes.Remove(_modelSelectedTextureCode);
                _modelSelectedTextureCode = doc.Textures.FirstOrDefault()?.Code ?? "";
                ModelMarkChanged();
                ModelEndEdit("Remove texture");
            }
            if (!hasSelection) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton("Reload##model-texture-reload"))
            {
                _modelUvTextureCache.Clear();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Drop the texture lookup cache so changed texture paths resolve again.");
            }

            ImGui.BeginChild("##model-texture-rows", new NVector2(0f, Math.Max(80f, size.Y * 0.35f)), true);
            try
            {
                foreach (ModelTextureEntry texture in doc.Textures)
                {
                    bool selected = string.Equals(texture.Code, _modelSelectedTextureCode, StringComparison.Ordinal);
                    string label = string.IsNullOrWhiteSpace(texture.Path) ? $"{texture.Code} (no path)" : $"{texture.Code} = {texture.Path}";
                    if (ImGui.Selectable($"{label}##model-texture-{texture.Code}", selected))
                    {
                        _modelSelectedTextureCode = texture.Code;
                        _modelUvFitPending = true;
                    }
                }
                if (doc.Textures.Count == 0)
                {
                    ImGui.TextDisabled("No textures. Faces will render as 'unknown'.");
                }
            }
            finally
            {
                ImGui.EndChild();
            }

            ModelTextureEntry? selectedTexture = doc.Textures.FirstOrDefault(texture => string.Equals(texture.Code, _modelSelectedTextureCode, StringComparison.Ordinal));
            if (selectedTexture == null)
            {
                ImGui.TextDisabled("Select a texture to edit it.");
                return;
            }

            string codeEdit = selectedTexture.Code;
            ImGui.SetNextItemWidth(-58f);
            ImGui.InputTextWithHint("Code##model-texture-code", "texture code", ref codeEdit, 80);
            bool codeCommitted = ImGui.IsItemDeactivatedAfterEdit();
            if (codeCommitted && !string.Equals(codeEdit, selectedTexture.Code, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(codeEdit))
            {
                ModelBeginEdit();
                string oldCode = selectedTexture.Code;
                selectedTexture.Code = codeEdit.Trim();
                if (doc.TextureSizes.Remove(oldCode, out int[]? movedSize))
                {
                    doc.TextureSizes[selectedTexture.Code] = movedSize;
                }
                foreach (ModelElementData element in doc.EnumerateElements())
                {
                    foreach (ModelFaceData? face in element.Faces)
                    {
                        if (face != null && string.Equals(face.Texture, oldCode, StringComparison.Ordinal))
                        {
                            face.Texture = selectedTexture.Code;
                        }
                    }
                }
                _modelSelectedTextureCode = selectedTexture.Code;
                ModelMarkChanged();
                ModelEndEdit("Rename texture");
            }

            ImGui.SetNextItemWidth(-58f);
            if (ModelFilteredCombo("Path##model-texture-path", selectedTexture.Path, EnsureModelTextureAssetIndex(), out string pickedPath, allowCustom: true, filterHint: "filter texture assets"))
            {
                ModelBeginEdit();
                selectedTexture.Path = pickedPath.Trim();
                _modelUvTextureCache.Clear();
                ModelMarkChanged();
                ModelEndEdit("Edit texture path");
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Texture asset this code resolves to. Options list every loaded texture PNG; type in the filter for paths that only exist at runtime.");
            }

            (int sizeWidth, int sizeHeight) = doc.GetTextureSize(selectedTexture.Code);
            bool hasOverride = doc.TextureSizes.ContainsKey(selectedTexture.Code);
            if (ImGui.Checkbox("Size override##model-texture-size-override", ref hasOverride))
            {
                ModelBeginEdit();
                if (hasOverride)
                {
                    doc.TextureSizes[selectedTexture.Code] = [sizeWidth, sizeHeight];
                }
                else
                {
                    doc.TextureSizes.Remove(selectedTexture.Code);
                }
                ModelMarkChanged();
                ModelEndEdit("Toggle texture size override");
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Per-texture UV space size (textureSizes). Without an override the shape texture size is used.");
            }
            if (doc.TextureSizes.TryGetValue(selectedTexture.Code, out int[]? overrideSize) && overrideSize.Length >= 2)
            {
                int width = overrideSize[0];
                int height = overrideSize[1];
                ImGui.SetNextItemWidth(80f);
                bool overrideChanged = ImGui.InputInt("##model-texture-size-w", ref width, 0);
                if (ImGui.IsItemActivated()) ModelBeginEdit();
                bool overrideCommitted = ImGui.IsItemDeactivatedAfterEdit();
                ImGui.SameLine();
                ImGui.TextUnformatted("x");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80f);
                overrideChanged |= ImGui.InputInt("##model-texture-size-h", ref height, 0);
                if (ImGui.IsItemActivated()) ModelBeginEdit();
                overrideCommitted |= ImGui.IsItemDeactivatedAfterEdit();
                if (overrideChanged)
                {
                    overrideSize[0] = Math.Clamp(width, 1, 4096);
                    overrideSize[1] = Math.Clamp(height, 1, 4096);
                    ModelMarkChanged();
                }
                if (overrideCommitted) ModelEndEdit("Edit texture size override");
            }

            (int textureId, int imageWidth, int imageHeight) = ModelResolveTexture(doc, selectedTexture);
            ImGui.TextDisabled(textureId > 0
                ? $"Image {imageWidth}x{imageHeight} px, UV space {sizeWidth}x{sizeHeight}"
                : "Texture image not found.");
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private string ModelGenerateTextureCode(ModelDocumentData doc)
    {
        HashSet<string> codes = new(doc.Textures.Select(texture => texture.Code), StringComparer.Ordinal);
        if (!codes.Contains("texture")) return "texture";
        for (int counter = 2; counter < 1000; counter++)
        {
            string candidate = $"texture{counter}";
            if (!codes.Contains(candidate)) return candidate;
        }
        return "texture" + Guid.NewGuid().ToString("N")[..6];
    }

    private (int TextureId, int Width, int Height) ModelResolveTexture(ModelDocumentData doc, ModelTextureEntry texture)
    {
        if (string.IsNullOrWhiteSpace(texture.Path)) return (0, 0, 0);

        string cacheKey = $"{doc.Domain}|{texture.Path}";
        if (_modelUvTextureCache.TryGetValue(cacheKey, out (int TextureId, int Width, int Height) cached))
        {
            return cached;
        }

        (int, int, int) resolved = (0, 0, 0);
        try
        {
            AssetLocation location = AssetLocation.Create(texture.Path, doc.Domain)
                .WithPathPrefixOnce("textures/")
                .WithPathAppendixOnce(".png");
            LoadedTexture loadedTexture = new(_api);
            _api.Render.GetOrLoadTexture(location, ref loadedTexture);
            if (loadedTexture.TextureId > 0)
            {
                resolved = (loadedTexture.TextureId, loadedTexture.Width, loadedTexture.Height);
            }
        }
        catch
        {
            resolved = (0, 0, 0);
        }

        _modelUvTextureCache[cacheKey] = resolved;
        return resolved;
    }

    private void DrawModelUvCanvasPanel(ModelDocumentData doc, NVector2 size)
    {
        ImGui.BeginChild("##model-uv-canvas-panel", size, true);
        try
        {
            ImGui.TextDisabled("LMB drag UV / corner to resize, RMB or MMB pan, wheel zoom");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80f);
            ImGui.Combo("Snap##model-uv-snap", ref _modelUvSnapIndex, ModelUvSnapLabels, ModelUvSnapLabels.Length);
            ImGui.SameLine();
            if (ImGui.SmallButton("Fit##model-uv-fit"))
            {
                _modelUvFitPending = true;
            }

            ModelTextureEntry? texture = doc.Textures.FirstOrDefault(entry => string.Equals(entry.Code, _modelSelectedTextureCode, StringComparison.Ordinal));
            if (texture == null)
            {
                ImGui.TextDisabled("Select a texture on the left to edit UVs on it.");
                return;
            }

            (int textureId, int imageWidth, int imageHeight) = ModelResolveTexture(doc, texture);
            (int uvWidth, int uvHeight) = doc.GetTextureSize(texture.Code);

            NVector2 available = ImGui.GetContentRegionAvail();
            NVector2 canvasSize = new(Math.Max(240f, available.X), Math.Max(200f, available.Y));
            ImGui.InvisibleButton("##model-uv-canvas", canvasSize);
            NVector2 min = ImGui.GetItemRectMin();
            NVector2 max = ImGui.GetItemRectMax();
            bool hovered = ImGui.IsItemHovered();

            if (_modelUvFitPending)
            {
                _modelUvFitPending = false;
                float zoomX = (canvasSize.X - 40f) / Math.Max(1, uvWidth);
                float zoomY = (canvasSize.Y - 40f) / Math.Max(1, uvHeight);
                _modelUvZoom = Math.Clamp(Math.Min(zoomX, zoomY), 2f, 128f);
                _modelUvPan = new NVector2(
                    (canvasSize.X - uvWidth * _modelUvZoom) * 0.5f,
                    (canvasSize.Y - uvHeight * _modelUvZoom) * 0.5f);
            }

            if (hovered)
            {
                float wheel = ImGui.GetIO().MouseWheel;
                if (Math.Abs(wheel) > 0.001f)
                {
                    NVector2 mouseLocal = ImGui.GetMousePos() - min;
                    NVector2 uvAtMouse = (mouseLocal - _modelUvPan) / _modelUvZoom;
                    _modelUvZoom = Math.Clamp(_modelUvZoom * MathF.Pow(1.15f, wheel), 1f, 256f);
                    _modelUvPan = mouseLocal - uvAtMouse * _modelUvZoom;
                }

                if (ImGui.IsMouseDragging(ImGuiMouseButton.Right) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
                {
                    _modelUvPan += ImGui.GetIO().MouseDelta;
                }
            }

            ImDrawListPtr drawList = ImGui.GetWindowDrawList();
            uint background = ImGui.ColorConvertFloat4ToU32(DevToolsViewportBackground.FillColor);
            uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 1f));
            drawList.AddRectFilled(min, max, background, 4f);
            drawList.PushClipRect(min, max, true);
            try
            {
                NVector2 origin = min + _modelUvPan;
                NVector2 textureExtent = new(uvWidth * _modelUvZoom, uvHeight * _modelUvZoom);

                if (textureId > 0)
                {
                    drawList.AddImage(new IntPtr(textureId), origin, origin + textureExtent);
                }
                else
                {
                    uint missing = ImGui.ColorConvertFloat4ToU32(new NVector4(0.35f, 0.12f, 0.3f, 1f));
                    drawList.AddRectFilled(origin, origin + textureExtent, missing);
                    drawList.AddText(origin + new NVector2(8f, 8f), ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.8f, 0.9f, 1f)), "texture not found");
                }

                DrawModelUvGrid(drawList, origin, uvWidth, uvHeight);
                DrawModelUvFaceRects(doc, drawList, origin, texture.Code, hovered, min, max);

                _ = imageWidth;
                _ = imageHeight;
            }
            finally
            {
                drawList.PopClipRect();
            }
            drawList.AddRect(min, max, border, 4f);
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawModelUvGrid(ImDrawListPtr drawList, NVector2 origin, int uvWidth, int uvHeight)
    {
        uint minor = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 1f, 1f, 0.07f));
        uint major = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 1f, 1f, 0.22f));

        if (_modelUvZoom >= 6f)
        {
            for (int x = 0; x <= uvWidth; x++)
            {
                drawList.AddLine(
                    origin + new NVector2(x * _modelUvZoom, 0f),
                    origin + new NVector2(x * _modelUvZoom, uvHeight * _modelUvZoom),
                    x % 4 == 0 ? major : minor,
                    1f);
            }
            for (int y = 0; y <= uvHeight; y++)
            {
                drawList.AddLine(
                    origin + new NVector2(0f, y * _modelUvZoom),
                    origin + new NVector2(uvWidth * _modelUvZoom, y * _modelUvZoom),
                    y % 4 == 0 ? major : minor,
                    1f);
            }
        }
        else
        {
            drawList.AddRect(origin, origin + new NVector2(uvWidth * _modelUvZoom, uvHeight * _modelUvZoom), major);
        }
    }

    private void DrawModelUvFaceRects(ModelDocumentData doc, ImDrawListPtr drawList, NVector2 origin, string textureCode, bool hovered, NVector2 clipMin, NVector2 clipMax)
    {
        _ = clipMin;
        _ = clipMax;
        NVector2 mouse = ImGui.GetMousePos();
        float snapStep = ModelUvSnapSteps[Math.Clamp(_modelUvSnapIndex, 0, ModelUvSnapSteps.Length - 1)];
        bool bypassSnap = ImGui.IsKeyDown(ImGuiKey.LeftAlt) || ImGui.IsKeyDown(ImGuiKey.RightAlt);

        if (_modelUvDragMode != ModelUvDragMode.None)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || _modelUvDragFace == null)
            {
                _modelUvDragMode = ModelUvDragMode.None;
                _modelUvDragFace = null;
                ModelEndEdit("Edit UV");
            }
            else
            {
                NVector2 deltaUv = (mouse - _modelUvDragStartMouse) / _modelUvZoom;
                float deltaU = ModelSnapUv(deltaUv.X, snapStep, bypassSnap);
                float deltaV = ModelSnapUv(deltaUv.Y, snapStep, bypassSnap);
                ModelFaceData face = _modelUvDragFace;
                if (_modelUvDragMode == ModelUvDragMode.Move)
                {
                    face.Uv[0] = _modelUvDragStartUv[0] + deltaU;
                    face.Uv[1] = _modelUvDragStartUv[1] + deltaV;
                    face.Uv[2] = _modelUvDragStartUv[2] + deltaU;
                    face.Uv[3] = _modelUvDragStartUv[3] + deltaV;
                }
                else
                {
                    face.Uv[2] = _modelUvDragStartUv[2] + deltaU;
                    face.Uv[3] = _modelUvDragStartUv[3] + deltaV;
                }
                ModelMarkChanged();
            }
        }

        ModelFaceData? hoveredFace = null;
        ModelElementData? hoveredElement = null;
        int hoveredFaceIndex = -1;
        bool hoveredCorner = false;

        foreach (ModelElementData element in doc.EnumerateElements())
        {
            if (!element.Visible) continue;

            for (int faceIndex = 0; faceIndex < 6; faceIndex++)
            {
                ModelFaceData? face = element.Faces[faceIndex];
                if (face == null || !string.Equals(face.Texture, textureCode, StringComparison.Ordinal)) continue;

                NVector2 cornerA = origin + new NVector2(face.Uv[0] * _modelUvZoom, face.Uv[1] * _modelUvZoom);
                NVector2 cornerB = origin + new NVector2(face.Uv[2] * _modelUvZoom, face.Uv[3] * _modelUvZoom);
                NVector2 rectMin = new(Math.Min(cornerA.X, cornerB.X), Math.Min(cornerA.Y, cornerB.Y));
                NVector2 rectMax = new(Math.Max(cornerA.X, cornerB.X), Math.Max(cornerA.Y, cornerB.Y));

                bool isSelected = ReferenceEquals(element, _modelSelectedElement) && faceIndex == _modelSelectedFace;
                bool mouseInside = hovered && mouse.X >= rectMin.X && mouse.X <= rectMax.X && mouse.Y >= rectMin.Y && mouse.Y <= rectMax.Y;
                bool mouseOnCorner = hovered && NVector2.Distance(mouse, cornerB) <= 7f;
                if ((mouseInside || mouseOnCorner) && hoveredFace == null && _modelUvDragMode == ModelUvDragMode.None)
                {
                    hoveredFace = face;
                    hoveredElement = element;
                    hoveredFaceIndex = faceIndex;
                    hoveredCorner = mouseOnCorner;
                }

                NVector4 baseColor = isSelected
                    ? new NVector4(1f, 0.82f, 0.3f, 0.95f)
                    : face.Enabled
                        ? new NVector4(0.45f, 0.75f, 0.95f, 0.7f)
                        : new NVector4(0.6f, 0.6f, 0.6f, 0.45f);
                uint color = ImGui.ColorConvertFloat4ToU32(baseColor);
                drawList.AddRect(rectMin, rectMax, color, 0f, ImDrawFlags.None, isSelected ? 2.4f : 1.2f);
                if (isSelected)
                {
                    uint fill = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.82f, 0.3f, 0.1f));
                    drawList.AddRectFilled(rectMin, rectMax, fill);
                    drawList.AddRectFilled(cornerB - new NVector2(4f, 4f), cornerB + new NVector2(4f, 4f), color);
                }

                if (_modelUvZoom * Math.Abs(face.Uv[2] - face.Uv[0]) > 42f)
                {
                    drawList.AddText(rectMin + new NVector2(3f, 2f), color, $"{element.Name}.{ModelFaceNames[faceIndex]}");
                }
            }
        }

        if (hoveredFace != null)
        {
            ImGui.SetMouseCursor(hoveredCorner ? ImGuiMouseCursor.ResizeNWSE : ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                ModelSelectElement(hoveredElement);
                _modelSelectedFace = hoveredFaceIndex;
                ModelBeginEdit();
                _modelUvDragMode = hoveredCorner ? ModelUvDragMode.Resize : ModelUvDragMode.Move;
                _modelUvDragFace = hoveredFace;
                _modelUvDragStartMouse = mouse;
                _modelUvDragStartUv = (float[])hoveredFace.Uv.Clone();
            }
        }
    }

    private static float ModelSnapUv(float value, float snapStep, bool bypass)
    {
        if (snapStep <= 0f || bypass) return value;
        return MathF.Round(value / snapStep) * snapStep;
    }
}
