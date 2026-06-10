using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private const long ModelLiveApplyDebounceMs = 1500;

    private string _modelJsonBuffer = "";
    private bool _modelJsonBufferStale = true;
    private bool _modelJsonEditing;
    private string _modelJsonError = "";
    private bool _modelLiveDirty;
    private long _modelLiveChangedAtMs;

    private static readonly HashSet<string> ModelKnownRootKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "textureWidth", "textureHeight", "textures", "textureSizes", "elements"
    };

    private static readonly HashSet<string> ModelKnownElementKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "from", "to", "rotationOrigin", "rotationX", "rotationY", "rotationZ",
        "shade", "stepParentName", "faces", "children"
    };

    private static readonly HashSet<string> ModelKnownFaceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "texture", "uv", "rotation", "glow", "enabled"
    };

    private bool ModelTryParseDocument(string text, string domain, string assetPath, bool isNew, out ModelDocumentData? doc, out string error)
    {
        doc = null;
        if (!DevToolsJson.TryParseObject(text, out JObject? root, out error) || root == null)
        {
            return false;
        }

        try
        {
            ModelDocumentData result = new()
            {
                Domain = domain,
                AssetPath = assetPath,
                IsNew = isNew,
                SourceText = text
            };

            JObject extra = new();
            foreach (JProperty property in root.Properties())
            {
                if (!ModelKnownRootKeys.Contains(property.Name))
                {
                    extra[property.Name] = property.Value.DeepClone();
                }
            }
            result.Extra = extra.Count > 0 ? extra : null;

            result.TextureWidth = ModelReadInt(root, "textureWidth", 16);
            result.TextureHeight = ModelReadInt(root, "textureHeight", 16);

            if (ModelFindProperty(root, "textures")?.Value is JObject textures)
            {
                foreach (JProperty texture in textures.Properties())
                {
                    result.Textures.Add(new ModelTextureEntry
                    {
                        Code = texture.Name,
                        Path = texture.Value.Type == JTokenType.String ? texture.Value.ToString() : ""
                    });
                }
            }

            if (ModelFindProperty(root, "textureSizes")?.Value is JObject textureSizes)
            {
                foreach (JProperty sizeProperty in textureSizes.Properties())
                {
                    if (sizeProperty.Value is JArray { Count: >= 2 } sizes)
                    {
                        result.TextureSizes[sizeProperty.Name] = [sizes[0].ToObject<int>(), sizes[1].ToObject<int>()];
                    }
                }
            }

            if (ModelFindProperty(root, "elements")?.Value is JArray elements)
            {
                foreach (JToken token in elements)
                {
                    if (token is not JObject elementJson) continue;
                    result.Roots.Add(ModelParseElement(elementJson, parent: null));
                }
            }

            doc = result;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static ModelElementData ModelParseElement(JObject json, ModelElementData? parent)
    {
        ModelElementData element = new()
        {
            Parent = parent,
            Name = ModelFindProperty(json, "name")?.Value.ToString() ?? "",
            From = ModelReadVector3(json, "from"),
            To = ModelReadVector3(json, "to"),
            RotationX = ModelReadDouble(json, "rotationX", 0),
            RotationY = ModelReadDouble(json, "rotationY", 0),
            RotationZ = ModelReadDouble(json, "rotationZ", 0),
            Shade = ModelReadBool(json, "shade", true),
            StepParentName = ModelFindProperty(json, "stepParentName")?.Value.ToString() ?? ""
        };

        if (ModelFindProperty(json, "rotationOrigin")?.Value is JArray { Count: >= 3 } origin)
        {
            element.RotationOrigin =
            [
                origin[0].ToObject<double>(),
                origin[1].ToObject<double>(),
                origin[2].ToObject<double>()
            ];
        }

        if (ModelFindProperty(json, "faces")?.Value is JObject faces)
        {
            foreach (JProperty faceProperty in faces.Properties())
            {
                int faceIndex = Array.FindIndex(ModelFaceNames, name => name.Equals(faceProperty.Name, StringComparison.OrdinalIgnoreCase));
                if (faceIndex < 0 || faceProperty.Value is not JObject faceJson) continue;
                element.Faces[faceIndex] = ModelParseFace(faceJson);
            }
        }

        if (ModelFindProperty(json, "children")?.Value is JArray children)
        {
            foreach (JToken token in children)
            {
                if (token is not JObject childJson) continue;
                element.Children.Add(ModelParseElement(childJson, element));
            }
        }

        JObject extra = new();
        foreach (JProperty property in json.Properties())
        {
            if (!ModelKnownElementKeys.Contains(property.Name))
            {
                extra[property.Name] = property.Value.DeepClone();
            }
        }
        element.Extra = extra.Count > 0 ? extra : null;

        return element;
    }

    private static ModelFaceData ModelParseFace(JObject json)
    {
        ModelFaceData face = new()
        {
            Rotation = (float)ModelReadDouble(json, "rotation", 0),
            Glow = ModelReadInt(json, "glow", 0),
            Enabled = ModelReadBool(json, "enabled", true)
        };

        string texture = ModelFindProperty(json, "texture")?.Value.ToString() ?? "";
        face.Texture = texture.StartsWith('#') ? texture[1..] : texture;

        if (ModelFindProperty(json, "uv")?.Value is JArray { Count: >= 4 } uv)
        {
            for (int index = 0; index < 4; index++)
            {
                face.Uv[index] = uv[index].ToObject<float>();
            }
        }

        JObject extra = new();
        foreach (JProperty property in json.Properties())
        {
            if (!ModelKnownFaceKeys.Contains(property.Name))
            {
                extra[property.Name] = property.Value.DeepClone();
            }
        }
        face.Extra = extra.Count > 0 ? extra : null;

        return face;
    }

    private static JProperty? ModelFindProperty(JObject json, string name)
    {
        return json.Property(name, StringComparison.OrdinalIgnoreCase);
    }

    private static int ModelReadInt(JObject json, string name, int fallback)
    {
        JToken? value = ModelFindProperty(json, name)?.Value;
        if (value == null) return fallback;
        try
        {
            return value.ToObject<int>();
        }
        catch
        {
            return fallback;
        }
    }

    private static double ModelReadDouble(JObject json, string name, double fallback)
    {
        JToken? value = ModelFindProperty(json, name)?.Value;
        if (value == null) return fallback;
        try
        {
            return value.ToObject<double>();
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ModelReadBool(JObject json, string name, bool fallback)
    {
        JToken? value = ModelFindProperty(json, name)?.Value;
        if (value == null) return fallback;
        try
        {
            return value.ToObject<bool>();
        }
        catch
        {
            return fallback;
        }
    }

    private static double[] ModelReadVector3(JObject json, string name)
    {
        if (ModelFindProperty(json, name)?.Value is JArray { Count: >= 3 } values)
        {
            try
            {
                return [values[0].ToObject<double>(), values[1].ToObject<double>(), values[2].ToObject<double>()];
            }
            catch
            {
            }
        }

        return new double[3];
    }

    private static string ModelSerializeDocument(ModelDocumentData doc, bool includeInvisible, bool indented)
    {
        JObject root = new()
        {
            ["textureWidth"] = doc.TextureWidth,
            ["textureHeight"] = doc.TextureHeight
        };

        JObject textures = new();
        foreach (ModelTextureEntry texture in doc.Textures)
        {
            if (string.IsNullOrWhiteSpace(texture.Code)) continue;
            textures[texture.Code] = texture.Path;
        }
        root["textures"] = textures;

        if (doc.TextureSizes.Count > 0)
        {
            JObject textureSizes = new();
            foreach ((string code, int[] size) in doc.TextureSizes)
            {
                if (size.Length < 2) continue;
                textureSizes[code] = new JArray(size[0], size[1]);
            }
            if (textureSizes.Count > 0) root["textureSizes"] = textureSizes;
        }

        JArray elements = [];
        foreach (ModelElementData element in doc.Roots)
        {
            if (!includeInvisible && !element.Visible) continue;
            elements.Add(ModelSerializeElement(element, includeInvisible));
        }
        root["elements"] = elements;

        if (doc.Extra != null)
        {
            foreach (JProperty property in doc.Extra.Properties())
            {
                root[property.Name] = property.Value.DeepClone();
            }
        }

        return root.ToString(indented ? Formatting.Indented : Formatting.None);
    }

    private static JObject ModelSerializeElement(ModelElementData element, bool includeInvisible)
    {
        JObject json = new()
        {
            ["name"] = element.Name,
            ["from"] = new JArray(element.From[0], element.From[1], element.From[2]),
            ["to"] = new JArray(element.To[0], element.To[1], element.To[2])
        };

        if (element.RotationOrigin != null)
        {
            json["rotationOrigin"] = new JArray(element.RotationOrigin[0], element.RotationOrigin[1], element.RotationOrigin[2]);
        }
        if (Math.Abs(element.RotationX) > 0.000001) json["rotationX"] = element.RotationX;
        if (Math.Abs(element.RotationY) > 0.000001) json["rotationY"] = element.RotationY;
        if (Math.Abs(element.RotationZ) > 0.000001) json["rotationZ"] = element.RotationZ;
        if (!element.Shade) json["shade"] = false;
        if (!string.IsNullOrWhiteSpace(element.StepParentName)) json["stepParentName"] = element.StepParentName;

        JObject faces = new();
        for (int faceIndex = 0; faceIndex < 6; faceIndex++)
        {
            ModelFaceData? face = element.Faces[faceIndex];
            if (face == null) continue;
            faces[ModelFaceNames[faceIndex]] = ModelSerializeFace(face);
        }
        if (faces.Count > 0) json["faces"] = faces;

        if (element.Children.Count > 0)
        {
            JArray children = [];
            foreach (ModelElementData child in element.Children)
            {
                if (!includeInvisible && !child.Visible) continue;
                children.Add(ModelSerializeElement(child, includeInvisible));
            }
            if (children.Count > 0) json["children"] = children;
        }

        if (element.Extra != null)
        {
            foreach (JProperty property in element.Extra.Properties())
            {
                json[property.Name] = property.Value.DeepClone();
            }
        }

        return json;
    }

    private static JObject ModelSerializeFace(ModelFaceData face)
    {
        JObject json = new()
        {
            ["texture"] = "#" + face.Texture,
            ["uv"] = new JArray(face.Uv[0], face.Uv[1], face.Uv[2], face.Uv[3])
        };

        if (Math.Abs(face.Rotation) > 0.000001) json["rotation"] = face.Rotation;
        if (face.Glow != 0) json["glow"] = face.Glow;
        if (!face.Enabled) json["enabled"] = false;

        if (face.Extra != null)
        {
            foreach (JProperty property in face.Extra.Properties())
            {
                json[property.Name] = property.Value.DeepClone();
            }
        }

        return json;
    }

    private void DrawModelJsonPanel()
    {
        if (_modelDoc == null)
        {
            ImGui.TextDisabled("Open a shape or create a new one.");
            return;
        }

        if (_modelJsonBufferStale && !_modelJsonEditing)
        {
            try
            {
                _modelJsonBuffer = ModelSerializeDocument(_modelDoc, includeInvisible: true, indented: true);
                _modelJsonError = "";
            }
            catch (Exception exception)
            {
                _modelJsonError = exception.Message;
            }
            _modelJsonBufferStale = false;
        }

        if (ImGui.Button("Apply JSON##model-json-apply"))
        {
            ModelApplyJsonBuffer();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Parse the JSON below and replace the document with it (undoable). Unknown keys like animations are preserved.");
        }
        ImGui.SameLine();
        if (ImGui.Button("Revert JSON##model-json-revert"))
        {
            _modelJsonBufferStale = true;
            _modelJsonEditing = false;
            _modelJsonError = "";
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy##model-json-copy"))
        {
            ImGui.SetClipboardText(_modelJsonBuffer);
            _modelStatus = "Shape JSON copied to clipboard.";
        }
        if (!string.IsNullOrWhiteSpace(_modelJsonError))
        {
            ImGui.SameLine();
            ImGui.TextColored(new NVector4(1f, 0.42f, 0.34f, 1f), _modelJsonError);
        }

        NVector2 available = ImGui.GetContentRegionAvail();
        ImGui.InputTextMultiline("##model-json-text", ref _modelJsonBuffer, 4_000_000, new NVector2(-1f, Math.Max(160f, available.Y - 4f)));
        _modelJsonEditing = ImGui.IsItemActive();
    }

    private void ModelApplyJsonBuffer()
    {
        if (_modelDoc == null) return;

        if (!ModelTryParseDocument(_modelJsonBuffer, _modelDoc.Domain, _modelDoc.AssetPath, _modelDoc.IsNew, out ModelDocumentData? parsed, out string error) || parsed == null)
        {
            _modelJsonError = $"Invalid JSON: {error}";
            return;
        }

        ModelBeginEdit();
        parsed.SourceText = _modelDoc.SourceText;
        parsed.Dirty = true;
        _modelDoc = parsed;
        _modelSelectedElement = parsed.Roots.FirstOrDefault();
        _modelSelectedFace = -1;
        _modelReparentSource = null;
        ModelMarkChanged();
        ModelEndEdit("Apply JSON");
        _modelJsonError = "";
        _modelStatus = "Applied JSON to the shape document.";
    }

    private SourceSaveResult TrySaveModelToSource()
    {
        if (_modelDoc == null)
        {
            return SourceSaveResult.Fail("No shape document open.");
        }

        try
        {
            string assetPath = _modelDoc.AssetPath.Trim().Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return SourceSaveResult.Fail("Model save failed: asset path is empty.");
            }
            if (!assetPath.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase))
            {
                assetPath = "shapes/" + assetPath.TrimStart('/');
            }
            assetPath = EnsureJsonFilePath(assetPath);
            _modelDoc.AssetPath = assetPath;

            ModelDocumentData doc = _modelDoc;
            string newText = ModelSerializeDocument(doc, includeInvisible: true, indented: true);
            string relativePath = Path.Combine("assets", doc.Domain, assetPath.Replace('/', Path.DirectorySeparatorChar));
            string outputPath = GetToolAuthoredAssetPath("models", relativePath);
            string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : doc.SourceText;
            SourceSaveRequest request = new(
                outputPath,
                oldText,
                newText,
                $"Saved authored shape to {outputPath}.",
                () =>
                {
                    string result = WriteAuthoredFile(outputPath, newText);
                    if (string.IsNullOrEmpty(result))
                    {
                        doc.Dirty = false;
                    }
                    return result;
                });
            return SourceSaveResult.Preview(request);
        }
        catch (Exception exception)
        {
            return SourceSaveResult.Fail($"Model save failed for {_modelDoc.DisplayPath}: {exception.Message}");
        }
    }

    private void DrawModelRuntimeControls(ModelDocumentData doc)
    {
        IAsset? runtimeAsset = ModelFindRuntimeAsset(doc);
        _liveApplyManager.DrawTargetControls(
            "model-live",
            ModelLiveApplyKey(doc),
            doc.DisplayPath,
            runtimeAsset != null,
            () => ApplyModelRuntime(force: true),
            RevertModelRuntime);
        if (runtimeAsset == null && doc.IsNew)
        {
            ImGui.TextDisabled("New shapes have no loaded runtime asset; save and reference them from a block/item/entity first.");
        }
        else if (runtimeAsset != null)
        {
            ImGui.TextDisabled("Applying reloads and re-tesselates all shapes; the game may pause briefly.");
        }
    }

    private string ApplyModelRuntime(bool force)
    {
        if (_modelDoc == null)
        {
            _liveApplyManager.LastStatus = "Models: no shape document open.";
            return _liveApplyManager.LastStatus;
        }

        ModelDocumentData doc = _modelDoc;
        IAsset? asset = ModelFindRuntimeAsset(doc);
        if (asset == null)
        {
            _liveApplyManager.LastStatus = $"Models: no loaded runtime asset for {doc.DisplayPath}.";
            return _liveApplyManager.LastStatus;
        }

        if (!force && !_modelLiveDirty)
        {
            return _liveApplyManager.LastStatus;
        }

        string newText = ModelSerializeDocument(doc, includeInvisible: true, indented: true);
        string status = _liveApplyManager.Apply(
            ModelLiveApplyKey(doc),
            doc.DisplayPath,
            captureOriginal: () =>
            {
                byte[] originalData = asset.Data;
                string originalText = doc.SourceText;
                return new LivePatchSnapshot(
                    () =>
                    {
                        asset.Data = originalData;
                        ModelTriggerShapeReload(doc);
                    },
                    Path.Combine("assets", doc.Domain, doc.AssetPath.Replace('/', Path.DirectorySeparatorChar)),
                    () => originalText,
                    "models");
            },
            applyCurrent: () =>
            {
                asset.Data = Encoding.UTF8.GetBytes(newText);
                ModelTriggerShapeReload(doc);
            },
            appliedStatus: () => $"Live applied {doc.DisplayPath}: shapes reloaded and re-tesselated.");
        _modelLiveDirty = false;
        return status;
    }

    private string RevertModelRuntime()
    {
        if (_modelDoc == null)
        {
            _liveApplyManager.LastStatus = "Models: no shape document open.";
            return _liveApplyManager.LastStatus;
        }

        return _liveApplyManager.Revert(ModelLiveApplyKey(_modelDoc));
    }

    private void ModelMaybeAutoApplyLive(bool force)
    {
        if (_modelDoc == null || !_liveApplyManager.AutoApply || !_modelLiveDirty) return;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;

        long now = _api.World?.ElapsedMilliseconds ?? 0;
        if (!force && now - _modelLiveChangedAtMs < ModelLiveApplyDebounceMs) return;

        if (ModelFindRuntimeAsset(_modelDoc) == null)
        {
            _modelLiveDirty = false;
            return;
        }

        ApplyModelRuntime(force: true);
    }

    private void ApplyModelRuntimeForActiveTab(bool force)
    {
        if (_modelDoc == null)
        {
            _liveApplyManager.LastStatus = "Models: no shape document open.";
            return;
        }

        if (ModelFindRuntimeAsset(_modelDoc) == null)
        {
            _liveApplyManager.LastStatus = $"Models: no loaded runtime asset for {_modelDoc.DisplayPath}.";
            return;
        }

        if (force || _modelLiveDirty)
        {
            ApplyModelRuntime(force: true);
        }
    }

    private void ClearModelLiveApplyState()
    {
        _modelLiveDirty = false;
    }

    private IAsset? ModelFindRuntimeAsset(ModelDocumentData doc)
    {
        if (string.IsNullOrWhiteSpace(doc.AssetPath)) return null;

        try
        {
            return _api.Assets.TryGet(new AssetLocation(doc.Domain, doc.AssetPath), loadAsset: true);
        }
        catch
        {
            return null;
        }
    }

    private static string ModelLiveApplyKey(ModelDocumentData doc)
    {
        return $"model:{doc.Domain}:{doc.AssetPath}".ToLowerInvariant();
    }

    private void ModelTriggerShapeReload(ModelDocumentData doc)
    {
        if (_api.World is not ClientMain client)
        {
            throw new InvalidOperationException("Shape reload requires a client world.");
        }

        client.eventManager?.TriggerReloadShapes();
        client.RedrawAllBlocks();

        AssetLocation shapeLocation = new(doc.Domain, doc.AssetPath);
        foreach (Entity entity in client.LoadedEntities.Values.ToList())
        {
            CompositeShape? compositeShape = entity.Properties?.Client?.ShapeForEntity ?? entity.Properties?.Client?.Shape;
            if (compositeShape?.Base == null) continue;

            AssetLocation entityShapeLocation = compositeShape.Base.Clone()
                .WithPathPrefixOnce("shapes/")
                .WithPathAppendixOnce(".json");
            if (!entityShapeLocation.Equals(shapeLocation)) continue;

            try
            {
                entity.MarkShapeModified();
            }
            catch (Exception exception)
            {
                LoggerUtil.Verbose(_api, this, $"Model live apply: could not refresh entity {entity.Code}: {exception.Message}");
            }
        }
    }
}
