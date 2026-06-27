using InGameDevTools.Utils;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using System.Runtime.InteropServices;
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

    private enum ModelTexturePaintTool
    {
        Brush,
        Eraser,
        Fill,
        Picker
    }

    private static readonly float[] ModelUvSnapSteps = [0f, 1f, 0.5f, 0.25f];
    private static readonly string[] ModelUvSnapLabels = ["Off", "1", "0.5", "0.25"];
    private static readonly string[] ModelTexturePaintToolLabels = ["Brush", "Eraser", "Fill", "Picker"];

    private readonly Dictionary<string, (int TextureId, int Width, int Height)> _modelUvTextureCache = new(StringComparer.Ordinal);
    private float _modelUvZoom = 18f;
    private NVector2 _modelUvPan = NVector2.Zero;
    private bool _modelUvFitPending = true;
    private int _modelUvSnapIndex = 2;
    private ModelUvDragMode _modelUvDragMode = ModelUvDragMode.None;
    private ModelFaceData? _modelUvDragFace;
    private NVector2 _modelUvDragStartMouse;
    private float[] _modelUvDragStartUv = new float[4];
    private readonly Random _modelUvRandom = new();
    private readonly Dictionary<string, NVector4> _modelUvTextureTintOverrides = new(StringComparer.Ordinal);
    private bool _modelTexturePaintMode;
    private ModelTexturePaintTool _modelTexturePaintTool;
    private NVector4 _modelTexturePaintColor = new(0.35f, 0.75f, 0.28f, 1f);
    private int _modelTexturePaintBrushSize = 1;
    private int _modelTexturePaintNewWidth = 16;
    private int _modelTexturePaintNewHeight = 16;
    private string _modelTexturePaintSavePath = "";
    private string _modelTexturePaintSavePathKey = "";
    private string _modelTexturePaintKey = "";
    private string _modelTexturePaintSourceLabel = "";
    private DevToolsTexturePaintCanvas? _modelTexturePaintCanvas;
    private int _modelTexturePaintTextureId;
    private int _modelTexturePaintTextureWidth;
    private int _modelTexturePaintTextureHeight;
    private bool _modelTexturePaintTextureDirty = true;

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
                _modelUvTextureTintOverrides.Remove(_modelSelectedTextureCode);
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
                if (_modelUvTextureTintOverrides.Remove(oldCode, out NVector4 movedTint))
                {
                    _modelUvTextureTintOverrides[selectedTexture.Code] = movedTint;
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
                ModelResetTexturePaintForTexture(doc, selectedTexture);
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

            bool tintEnabled = _modelUvTextureTintOverrides.TryGetValue(selectedTexture.Code, out NVector4 tint);
            if (!tintEnabled) tint = NVector4.One;
            if (ImGui.Checkbox("Preview color override##model-texture-preview-tint-enabled", ref tintEnabled))
            {
                if (tintEnabled)
                {
                    _modelUvTextureTintOverrides[selectedTexture.Code] = tint;
                }
                else
                {
                    _modelUvTextureTintOverrides.Remove(selectedTexture.Code);
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Tint this texture only in the UV editor preview. Useful for dynamically colored textures such as leaves.");
            }
            if (tintEnabled)
            {
                ImGui.SetNextItemWidth(-58f);
                if (ImGui.ColorEdit4("Color##model-texture-preview-tint", ref tint, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                {
                    tint.X = Math.Clamp(tint.X, 0f, 1f);
                    tint.Y = Math.Clamp(tint.Y, 0f, 1f);
                    tint.Z = Math.Clamp(tint.Z, 0f, 1f);
                    tint.W = Math.Clamp(tint.W, 0f, 1f);
                    _modelUvTextureTintOverrides[selectedTexture.Code] = tint;
                }
                if (ImGui.SmallButton("Reset preview color##model-texture-preview-tint-reset"))
                {
                    _modelUvTextureTintOverrides[selectedTexture.Code] = NVector4.One;
                }
            }

            DrawModelTexturePainterControls(doc, selectedTexture);
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

    private NVector4 ModelUvTextureTint(string textureCode)
    {
        return _modelUvTextureTintOverrides.TryGetValue(textureCode, out NVector4 tint) ? tint : NVector4.One;
    }

    private void DrawModelTexturePainterControls(ModelDocumentData doc, ModelTextureEntry texture)
    {
        string key = ModelTexturePaintKey(doc, texture);
        if (!string.Equals(_modelTexturePaintSavePathKey, key, StringComparison.Ordinal))
        {
            _modelTexturePaintSavePathKey = key;
            _modelTexturePaintSavePath = ModelDefaultTexturePaintPath(doc, texture);
        }

        DevToolsTexturePaintCanvas? canvas = ModelTexturePaintCanvasFor(doc, texture);

        ImGui.SeparatorText("Texture painter");
        bool paintMode = _modelTexturePaintMode;
        if (ImGui.Checkbox("Paint on UV canvas##model-texture-paint-toggle-left", ref paintMode))
        {
            _modelTexturePaintMode = paintMode;
            if (_modelTexturePaintMode && canvas == null && !string.IsNullOrWhiteSpace(texture.Path))
            {
                ModelLoadSelectedTextureForPainting(doc, texture);
                canvas = ModelTexturePaintCanvasFor(doc, texture);
            }
        }
        int tool = (int)_modelTexturePaintTool;
        ImGui.SetNextItemWidth(-58f);
        if (ImGui.Combo("Tool##model-texture-paint-tool", ref tool, ModelTexturePaintToolLabels, ModelTexturePaintToolLabels.Length))
        {
            _modelTexturePaintTool = (ModelTexturePaintTool)Math.Clamp(tool, 0, ModelTexturePaintToolLabels.Length - 1);
        }
        ImGui.SetNextItemWidth(-58f);
        ImGui.DragInt("Brush px##model-texture-paint-brush", ref _modelTexturePaintBrushSize, 0.1f, 1, 64);
        _modelTexturePaintBrushSize = Math.Clamp(_modelTexturePaintBrushSize, 1, 64);
        ImGui.SetNextItemWidth(-58f);
        ImGui.ColorEdit4("Paint color##model-texture-paint-color", ref _modelTexturePaintColor, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf);

        ImGui.SetNextItemWidth(76f);
        ImGui.InputInt("W##model-texture-paint-new-width", ref _modelTexturePaintNewWidth, 0);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(76f);
        ImGui.InputInt("H##model-texture-paint-new-height", ref _modelTexturePaintNewHeight, 0);
        _modelTexturePaintNewWidth = Math.Clamp(_modelTexturePaintNewWidth, 1, 4096);
        _modelTexturePaintNewHeight = Math.Clamp(_modelTexturePaintNewHeight, 1, 4096);

        if (ImGui.SmallButton("New blank##model-texture-paint-create"))
        {
            ModelCreateBlankPaintTexture(doc, texture);
            canvas = ModelTexturePaintCanvasFor(doc, texture);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Create a new editable image for this texture code and point the shape at the save path.");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clone selected##model-texture-paint-load"))
        {
            ModelLoadSelectedTextureForPainting(doc, texture);
            canvas = ModelTexturePaintCanvasFor(doc, texture);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Copy the selected game/authored texture PNG into the painter without overwriting the original.");
        }

        ImGui.SetNextItemWidth(-58f);
        ImGui.InputTextWithHint("Save path##model-texture-paint-save-path", "e.g. devtools/myshape/leaf", ref _modelTexturePaintSavePath, 260);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Texture path inside this shape domain. Saved under assets/<domain>/textures/<path>.png.");
        }

        bool canSave = canvas != null;
        if (!canSave) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Save and use##model-texture-paint-save"))
        {
            ModelSavePaintTexture(doc, texture);
            canvas = ModelTexturePaintCanvasFor(doc, texture);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear canvas##model-texture-paint-clear"))
        {
            canvas?.Clear(new DevToolsTexturePaintColor(255, 255, 255, 255));
            _modelTexturePaintTextureDirty = true;
        }
        if (!canSave) ImGui.EndDisabled();

        if (canvas == null)
        {
            ImGui.TextDisabled("No editable texture loaded.");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(_modelTexturePaintSourceLabel))
            {
                ImGui.TextDisabled($"Source: {_modelTexturePaintSourceLabel}");
            }
            string outputPath = ModelNormalizeTexturePaintPath(_modelTexturePaintSavePath, out _);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                ImGui.TextDisabled($"Output: textures/{outputPath}.png");
            }
            ImGui.TextDisabled($"{canvas.Width}x{canvas.Height} px{(canvas.Dirty ? " (unsaved)" : "")}");
        }
    }

    private void ModelCreateBlankPaintTexture(ModelDocumentData doc, ModelTextureEntry texture)
    {
        string path = ModelNormalizeTexturePaintPath(_modelTexturePaintSavePath, out string error);
        if (string.IsNullOrWhiteSpace(path))
        {
            _modelStatus = $"Texture painter: {error}";
            return;
        }

        DevToolsTexturePaintCanvas canvas = new(_modelTexturePaintNewWidth, _modelTexturePaintNewHeight, new DevToolsTexturePaintColor(255, 255, 255, 255));
        canvas.Dirty = true;
        ModelSetTexturePaintCanvas(doc, texture, canvas);
        _modelTexturePaintSourceLabel = "blank";
        _modelTexturePaintMode = true;

        ModelBeginEdit();
        texture.Path = path;
        doc.TextureSizes[texture.Code] = [canvas.Width, canvas.Height];
        ModelMarkChanged();
        ModelEndEdit("Create texture");
        _modelStatus = $"Created editable texture {canvas.Width}x{canvas.Height}. Save PNG to write it.";
    }

    private void ModelLoadSelectedTextureForPainting(ModelDocumentData doc, ModelTextureEntry texture)
    {
        if (!ModelTryReadTexturePngBytes(doc, texture, out byte[] data, out string source, out string error))
        {
            _modelStatus = $"Texture painter load failed: {error}";
            return;
        }

        if (!DevToolsTexturePaintCanvas.TryLoadPng(data, out DevToolsTexturePaintCanvas? canvas, out error) || canvas == null)
        {
            _modelStatus = $"Texture painter load failed: {error}";
            return;
        }

        ModelSetTexturePaintCanvas(doc, texture, canvas);
        canvas.Dirty = true;
        _modelTexturePaintNewWidth = canvas.Width;
        _modelTexturePaintNewHeight = canvas.Height;
        _modelTexturePaintSourceLabel = source;
        _modelTexturePaintMode = true;
        _modelStatus = $"Cloned {canvas.Width}x{canvas.Height} texture from {source}. Save and use to write an authored PNG copy.";
    }

    private bool ModelTryReadTexturePngBytes(ModelDocumentData doc, ModelTextureEntry texture, out byte[] data, out string source, out string error)
    {
        data = [];
        source = "";
        error = "";

        string rawTexturePath = (texture.Path ?? "").Trim();
        string path = ModelNormalizeTexturePaintPath(rawTexturePath, out _);
        bool explicitDomain = rawTexturePath.Contains(':', StringComparison.Ordinal);
        if (!explicitDomain && !string.IsNullOrWhiteSpace(path))
        {
            string authoredPath = ModelTexturePaintOutputPath(doc.Domain, path);
            if (File.Exists(authoredPath))
            {
                data = File.ReadAllBytes(authoredPath);
                source = authoredPath;
                return true;
            }
        }

        try
        {
            if (string.IsNullOrWhiteSpace(rawTexturePath))
            {
                error = "selected texture has no path.";
                return false;
            }

            AssetLocation location = AssetLocation.Create(rawTexturePath, doc.Domain)
                .WithPathPrefixOnce("textures/")
                .WithPathAppendixOnce(".png");
            IAsset? asset = _api.Assets.TryGet(location, true);
            if (asset?.Data is { Length: > 0 } bytes)
            {
                data = bytes;
                source = location.ToString();
                return true;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        error = "selected texture PNG was not found.";
        return false;
    }

    private void ModelSavePaintTexture(ModelDocumentData doc, ModelTextureEntry texture)
    {
        DevToolsTexturePaintCanvas? canvas = ModelTexturePaintCanvasFor(doc, texture);
        if (canvas == null)
        {
            _modelStatus = "Texture painter: no editable texture loaded.";
            return;
        }

        string path = ModelNormalizeTexturePaintPath(_modelTexturePaintSavePath, out string error);
        if (string.IsNullOrWhiteSpace(path))
        {
            _modelStatus = $"Texture painter save failed: {error}";
            return;
        }

        try
        {
            string outputPath = ModelTexturePaintOutputPath(doc.Domain, path);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            string writeError = WriteAuthoredBytes(outputPath, canvas.EncodePng());
            if (!string.IsNullOrEmpty(writeError))
            {
                _modelStatus = $"Texture painter save failed: {writeError}";
                return;
            }
            canvas.Dirty = false;
            _modelTexturePaintSourceLabel = outputPath;
            _modelUvTextureCache.Clear();
            _modelTexturePaintTextureDirty = true;

            bool changed = !string.Equals(texture.Path, path, StringComparison.Ordinal) ||
                !doc.TextureSizes.TryGetValue(texture.Code, out int[]? size) ||
                size.Length < 2 ||
                size[0] != canvas.Width ||
                size[1] != canvas.Height;
            if (changed)
            {
                ModelBeginEdit();
                texture.Path = path;
                doc.TextureSizes[texture.Code] = [canvas.Width, canvas.Height];
                ModelMarkChanged();
                ModelEndEdit("Save texture");
            }

            _modelStatus = $"Saved texture PNG to {outputPath}.";
        }
        catch (Exception exception)
        {
            _modelStatus = $"Texture painter save failed: {exception.Message}";
        }
    }

    private DevToolsTexturePaintCanvas? ModelTexturePaintCanvasFor(ModelDocumentData doc, ModelTextureEntry texture)
    {
        return string.Equals(_modelTexturePaintKey, ModelTexturePaintKey(doc, texture), StringComparison.Ordinal) ? _modelTexturePaintCanvas : null;
    }

    private void ModelSetTexturePaintCanvas(ModelDocumentData doc, ModelTextureEntry texture, DevToolsTexturePaintCanvas canvas)
    {
        _modelTexturePaintKey = ModelTexturePaintKey(doc, texture);
        _modelTexturePaintCanvas = canvas;
        _modelTexturePaintTextureDirty = true;
    }

    private void ModelResetTexturePaintForTexture(ModelDocumentData doc, ModelTextureEntry texture)
    {
        _modelTexturePaintSavePathKey = "";
        if (!string.Equals(_modelTexturePaintKey, ModelTexturePaintKey(doc, texture), StringComparison.Ordinal)) return;

        _modelTexturePaintKey = "";
        _modelTexturePaintCanvas = null;
        _modelTexturePaintSourceLabel = "";
        ModelDisposeTexturePaintTexture();
    }

    private static string ModelTexturePaintKey(ModelDocumentData doc, ModelTextureEntry texture)
    {
        return $"{doc.Domain}|{texture.Code}";
    }

    private string ModelTexturePaintOutputPath(string domain, string texturePath)
    {
        string relative = Path.Combine("assets", domain, "textures", texturePath.Replace('/', Path.DirectorySeparatorChar) + ".png");
        return GetToolAuthoredAssetPath("models", relative);
    }

    private static string ModelNormalizeTexturePaintPath(string path, out string error)
    {
        error = "";
        string normalized = (path ?? "").Trim().Replace('\\', '/');
        if (normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["textures/".Length..];
        }
        if (normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }
        normalized = normalized.Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "texture path is empty.";
            return "";
        }
        if (Path.IsPathRooted(normalized) || normalized.Contains("..", StringComparison.Ordinal))
        {
            error = "texture path cannot be absolute or contain '..'.";
            return "";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (string segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".." || segment.IndexOfAny(invalid) >= 0)
            {
                error = $"invalid texture path segment '{segment}'.";
                return "";
            }
        }

        return normalized;
    }

    private static string ModelDefaultTexturePaintPath(ModelDocumentData doc, ModelTextureEntry texture)
    {
        string assetPath = doc.AssetPath.Replace('\\', '/');
        string shapeName = Path.GetFileNameWithoutExtension(assetPath);
        return $"devtools/{ModelSanitizeTexturePaintSegment(shapeName)}/{ModelSanitizeTexturePaintSegment(texture.Code)}";
    }

    private static string ModelSanitizeTexturePaintSegment(string value)
    {
        Span<char> buffer = stackalloc char[Math.Min(Math.Max(value.Length, 1), 96)];
        int count = 0;
        foreach (char c in value)
        {
            if (count >= buffer.Length) break;
            buffer[count++] = char.IsLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '-';
        }

        string sanitized = new string(buffer[..count]).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "texture" : sanitized;
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
            ImGui.SameLine();
            ImGui.Checkbox("Paint texture##model-texture-paint-mode", ref _modelTexturePaintMode);
            if (_modelTexturePaintMode)
            {
                _modelUvDragMode = ModelUvDragMode.None;
                _modelUvDragFace = null;
            }

            ModelTextureEntry? texture = doc.Textures.FirstOrDefault(entry => string.Equals(entry.Code, _modelSelectedTextureCode, StringComparison.Ordinal));
            if (texture == null)
            {
                ImGui.TextDisabled("Select a texture on the left to edit UVs on it.");
                return;
            }

            (int textureId, int imageWidth, int imageHeight) = ModelResolveTexture(doc, texture);
            (int uvWidth, int uvHeight) = doc.GetTextureSize(texture.Code);
            DevToolsTexturePaintCanvas? paintCanvas = ModelTexturePaintCanvasFor(doc, texture);
            string paintTextureError = "";
            if (_modelTexturePaintMode && paintCanvas != null)
            {
                if (TryEnsureModelTexturePaintTexture(paintCanvas, out int paintTextureId, out paintTextureError))
                {
                    textureId = paintTextureId;
                    imageWidth = paintCanvas.Width;
                    imageHeight = paintCanvas.Height;
                }
                else if (!string.IsNullOrWhiteSpace(paintTextureError))
                {
                    _modelStatus = $"Texture painter preview failed: {paintTextureError}";
                }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Randomize texture UVs##model-uv-randomize-texture"))
            {
                float snapStep = ModelUvSnapSteps[Math.Clamp(_modelUvSnapIndex, 0, ModelUvSnapSteps.Length - 1)];
                int changed = ModelRandomizeTextureUvFaces(doc, texture.Code, uvWidth, uvHeight, snapStep);
                _modelStatus = changed == 0
                    ? $"No visible faces use texture '{texture.Code}'."
                    : $"Randomized {changed} UV face(s) using texture '{texture.Code}'.";
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Move every visible face using this texture to a random in-bounds UV offset while preserving each face rectangle size.");
            }

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
                    uint tint = ImGui.ColorConvertFloat4ToU32(ModelUvTextureTint(texture.Code));
                    drawList.AddImage(new IntPtr(textureId), origin, origin + textureExtent, NVector2.Zero, NVector2.One, tint);
                }
                else
                {
                    uint missing = ImGui.ColorConvertFloat4ToU32(new NVector4(0.35f, 0.12f, 0.3f, 1f));
                    drawList.AddRectFilled(origin, origin + textureExtent, missing);
                    drawList.AddText(origin + new NVector2(8f, 8f), ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.8f, 0.9f, 1f)), "texture not found");
                }

                DrawModelUvGrid(drawList, origin, uvWidth, uvHeight);
                if (_modelTexturePaintMode)
                {
                    DrawModelUvFaceRects(doc, drawList, origin, texture.Code, false, min, max);
                    DrawModelTexturePaintCursorAndInput(drawList, origin, textureExtent, hovered, paintCanvas, uvWidth, uvHeight);
                }
                else
                {
                    DrawModelUvFaceRects(doc, drawList, origin, texture.Code, hovered, min, max);
                }

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

    private void DrawModelTexturePaintCursorAndInput(
        ImDrawListPtr drawList,
        NVector2 origin,
        NVector2 textureExtent,
        bool hovered,
        DevToolsTexturePaintCanvas? canvas,
        int uvWidth,
        int uvHeight)
    {
        if (canvas == null)
        {
            if (hovered)
            {
                drawList.AddText(origin + new NVector2(8f, 8f), ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.8f, 0.55f, 1f)), "Create or load a texture to paint.");
            }
            return;
        }

        if (!hovered || textureExtent.X <= 0.001f || textureExtent.Y <= 0.001f || uvWidth <= 0 || uvHeight <= 0) return;

        NVector2 mouse = ImGui.GetMousePos();
        if (!ModelTryTexturePaintPixelAt(mouse, origin, textureExtent, canvas, out int pixelX, out int pixelY, out NVector2 pixelCenter))
        {
            return;
        }

        float pixelScale = MathF.Max(textureExtent.X / Math.Max(1, canvas.Width), textureExtent.Y / Math.Max(1, canvas.Height));
        float radius = MathF.Max(4f, _modelTexturePaintBrushSize * pixelScale);
        uint cursor = ImGui.ColorConvertFloat4ToU32(_modelTexturePaintTool == ModelTexturePaintTool.Eraser
            ? new NVector4(1f, 0.3f, 0.3f, 0.95f)
            : new NVector4(1f, 1f, 1f, 0.95f));
        drawList.AddCircle(pixelCenter, radius, cursor, 32, 1.4f);
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (_modelTexturePaintTool is ModelTexturePaintTool.Brush or ModelTexturePaintTool.Eraser)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                DevToolsTexturePaintColor color = _modelTexturePaintTool == ModelTexturePaintTool.Eraser
                    ? new DevToolsTexturePaintColor(0, 0, 0, 0)
                    : ModelTexturePaintColorFromVector(_modelTexturePaintColor);
                int changed = canvas.PaintCircle(pixelX, pixelY, _modelTexturePaintBrushSize, color);
                if (changed > 0)
                {
                    _modelTexturePaintTextureDirty = true;
                    _modelStatus = $"Painted {changed} pixel(s).";
                }
            }
        }
        else if (_modelTexturePaintTool == ModelTexturePaintTool.Fill)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                int changed = canvas.FloodFill(pixelX, pixelY, ModelTexturePaintColorFromVector(_modelTexturePaintColor));
                if (changed > 0)
                {
                    _modelTexturePaintTextureDirty = true;
                    _modelStatus = $"Filled {changed} pixel(s).";
                }
            }
        }
        else if (_modelTexturePaintTool == ModelTexturePaintTool.Picker && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _modelTexturePaintColor = ModelTexturePaintColorToVector(canvas.GetPixel(pixelX, pixelY));
            _modelTexturePaintTool = ModelTexturePaintTool.Brush;
            _modelStatus = $"Picked color at {pixelX}, {pixelY}.";
        }
    }

    private static bool ModelTryTexturePaintPixelAt(
        NVector2 mouse,
        NVector2 origin,
        NVector2 textureExtent,
        DevToolsTexturePaintCanvas canvas,
        out int pixelX,
        out int pixelY,
        out NVector2 pixelCenter)
    {
        pixelX = 0;
        pixelY = 0;
        pixelCenter = NVector2.Zero;
        float localX = mouse.X - origin.X;
        float localY = mouse.Y - origin.Y;
        if (localX < 0f || localY < 0f || localX > textureExtent.X || localY > textureExtent.Y)
        {
            return false;
        }

        float u = Math.Clamp(localX / Math.Max(0.0001f, textureExtent.X), 0f, 0.999999f);
        float v = Math.Clamp(localY / Math.Max(0.0001f, textureExtent.Y), 0f, 0.999999f);
        pixelX = Math.Clamp((int)MathF.Floor(u * canvas.Width), 0, canvas.Width - 1);
        pixelY = Math.Clamp((int)MathF.Floor(v * canvas.Height), 0, canvas.Height - 1);
        pixelCenter = origin + new NVector2(
            (pixelX + 0.5f) * textureExtent.X / Math.Max(1, canvas.Width),
            (pixelY + 0.5f) * textureExtent.Y / Math.Max(1, canvas.Height));
        return true;
    }

    private bool TryEnsureModelTexturePaintTexture(DevToolsTexturePaintCanvas canvas, out int textureId, out string error)
    {
        textureId = 0;
        error = "";

        if (_modelTexturePaintTextureId > 0 &&
            !_modelTexturePaintTextureDirty &&
            _modelTexturePaintTextureWidth == canvas.Width &&
            _modelTexturePaintTextureHeight == canvas.Height)
        {
            textureId = _modelTexturePaintTextureId;
            return true;
        }

        int restoreActiveTexture = 0;
        int restoreTexture2D = 0;
        int restoreUnpackAlignment = 4;
        GCHandle pinned = default;

        try
        {
            GL.GetInteger(GetPName.ActiveTexture, out restoreActiveTexture);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.GetInteger(GetPName.TextureBinding2D, out restoreTexture2D);
            GL.GetInteger(GetPName.UnpackAlignment, out restoreUnpackAlignment);

            if (_modelTexturePaintTextureId <= 0)
            {
                GL.GenTextures(1, out _modelTexturePaintTextureId);
                GL.BindTexture(TextureTarget.Texture2D, _modelTexturePaintTextureId);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            }
            else
            {
                GL.BindTexture(TextureTarget.Texture2D, _modelTexturePaintTextureId);
            }

            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            pinned = GCHandle.Alloc(canvas.Rgba, GCHandleType.Pinned);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba8,
                canvas.Width,
                canvas.Height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                pinned.AddrOfPinnedObject());

            _modelTexturePaintTextureWidth = canvas.Width;
            _modelTexturePaintTextureHeight = canvas.Height;
            _modelTexturePaintTextureDirty = false;
            textureId = _modelTexturePaintTextureId;
            return textureId > 0;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            if (pinned.IsAllocated)
            {
                pinned.Free();
            }

            try
            {
                GL.PixelStore(PixelStoreParameter.UnpackAlignment, restoreUnpackAlignment);
                GL.BindTexture(TextureTarget.Texture2D, restoreTexture2D);
                GL.ActiveTexture((TextureUnit)restoreActiveTexture);
            }
            catch
            {
                // The normal resolved texture can still draw this frame if GL state restore fails.
            }
        }
    }

    private void ModelDisposeTexturePaintTexture()
    {
        if (_modelTexturePaintTextureId <= 0) return;

        try
        {
            GL.DeleteTexture(_modelTexturePaintTextureId);
        }
        catch
        {
            // The GL context may already be gone during game shutdown.
        }

        _modelTexturePaintTextureId = 0;
        _modelTexturePaintTextureWidth = 0;
        _modelTexturePaintTextureHeight = 0;
        _modelTexturePaintTextureDirty = true;
    }

    private static DevToolsTexturePaintColor ModelTexturePaintColorFromVector(NVector4 color)
    {
        return new DevToolsTexturePaintColor(
            (byte)Math.Clamp(MathF.Round(color.X * 255f), 0f, 255f),
            (byte)Math.Clamp(MathF.Round(color.Y * 255f), 0f, 255f),
            (byte)Math.Clamp(MathF.Round(color.Z * 255f), 0f, 255f),
            (byte)Math.Clamp(MathF.Round(color.W * 255f), 0f, 255f));
    }

    private static NVector4 ModelTexturePaintColorToVector(DevToolsTexturePaintColor color)
    {
        return new NVector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    }

    private static float ModelSnapUv(float value, float snapStep, bool bypass)
    {
        if (snapStep <= 0f || bypass) return value;
        return MathF.Round(value / snapStep) * snapStep;
    }

    private int ModelRandomizeTextureUvFaces(ModelDocumentData doc, string textureCode, int uvWidth, int uvHeight, float snapStep)
    {
        List<ModelFaceData> faces = [];
        foreach (ModelElementData element in doc.EnumerateElements())
        {
            if (!element.Visible) continue;
            foreach (ModelFaceData? face in element.Faces)
            {
                if (face == null || !string.Equals(face.Texture, textureCode, StringComparison.Ordinal)) continue;
                faces.Add(face);
            }
        }

        if (faces.Count == 0) return 0;

        ModelBeginEdit();
        int changed = 0;
        foreach (ModelFaceData face in faces)
        {
            if (ModelRandomizeUvFace(face, uvWidth, uvHeight, _modelUvRandom, snapStep))
            {
                changed++;
            }
        }

        if (changed > 0)
        {
            ModelMarkChanged();
            ModelEndEdit("Randomize texture UVs");
        }
        else
        {
            ModelCancelEdit();
        }

        return changed;
    }

    private static bool ModelRandomizeUvFace(ModelFaceData face, int uvWidth, int uvHeight, Random random, float snapStep = 1f)
    {
        float width = face.Uv[2] - face.Uv[0];
        float height = face.Uv[3] - face.Uv[1];
        float spanU = MathF.Abs(width);
        float spanV = MathF.Abs(height);
        if (spanU <= 0.0001f || spanV <= 0.0001f) return false;

        float startU = ModelRandomUvStart(Math.Max(0, uvWidth - spanU), random, snapStep);
        float startV = ModelRandomUvStart(Math.Max(0, uvHeight - spanV), random, snapStep);
        if (width >= 0)
        {
            face.Uv[0] = startU;
            face.Uv[2] = startU + spanU;
        }
        else
        {
            face.Uv[0] = startU + spanU;
            face.Uv[2] = startU;
        }

        if (height >= 0)
        {
            face.Uv[1] = startV;
            face.Uv[3] = startV + spanV;
        }
        else
        {
            face.Uv[1] = startV + spanV;
            face.Uv[3] = startV;
        }

        return true;
    }

    private static float ModelRandomUvStart(float maxStart, Random random, float snapStep)
    {
        if (maxStart <= 0.0001f) return 0f;
        float step = snapStep > 0f ? snapStep : 1f;
        int slots = Math.Max(1, (int)MathF.Floor(maxStart / step));
        return Math.Min(maxStart, random.Next(slots + 1) * step);
    }
}
