#if DEBUG
using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private readonly List<TransformAssetEntry> _transformAssets = [];
    private readonly List<TransformAssetEntry> _visibleTransformAssets = [];
    private readonly Dictionary<string, ModelTransform> _transformDrafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _transformDirtyKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImGuiThreePanelLayoutState _transformsLayout = new(0.24f, 0.32f);
    private DevToolsPreview3DRenderer? _transformsPreviewRenderer;
    private DevToolsPreviewMesh? _transformPreviewMesh;
    private DevToolsPreviewMesh? _transformReferenceMesh;
    private string _transformPreviewCacheKey = "";
    private string _transformsFilter = "";
    private string _transformsDomainFilter = "";
    private int _transformsAssetIndex;
    private int _transformsTypeFilter;
    private bool _transformsDirtyOnly;
    private bool _transformUseTypedSlot;
    private int _transformDirectSlotIndex;
    private int _transformTypedMapIndex;
    private string _transformTypedKey = "";
    private string _transformReferenceFilter = "";
    private string _transformReferenceBlockCode = "";
    private int _transformReferenceBlockIndex;
    private float _transformPreviewYaw = -0.55f;
    private float _transformPreviewPitch = 0.35f;
    private float _transformPreviewDistance = 4.5f;
    private Vector3 _transformPreviewTarget = new(0.5f, 0.5f, 0.5f);
    private string _transformsStatus = "";
    private bool _transformsIndexed;

    private void TransformsEditorTab(float deltaSeconds)
    {
        ClearActiveTransformGizmo();
        EnsureTransformAssetsIndexed();

        NVector2 available = ImGui.GetContentRegionAvail();
        float scale = Math.Max(0.75f, _devToolsUiScale);
        float splitterThickness = Math.Max(5f, 6f * scale);
        ImGuiLayoutHelper.CalculateThreePanelWidths(
            available.X,
            splitterThickness,
            _transformsLayout,
            260f * scale,
            560f * scale,
            360f * scale,
            340f * scale,
            760f * scale,
            out float panelAvailableWidth,
            out float leftWidth,
            out float centerWidth,
            out float rightWidth);

        DrawTransformsBrowser(new NVector2(leftWidth, available.Y));
        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter("##transforms-left-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _transformsLayout.LeftFraction, 260f * scale, Math.Max(260f * scale, panelAvailableWidth - rightWidth - 360f * scale));
        ImGui.SameLine(0, 0);
        DrawTransformsViewport(new NVector2(centerWidth, available.Y));
        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter("##transforms-right-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _transformsLayout.RightFraction, 340f * scale, Math.Max(340f * scale, panelAvailableWidth - leftWidth - 360f * scale), invertDrag: true);
        ImGui.SameLine(0, 0);
        DrawTransformsInspector(new NVector2(rightWidth, available.Y));
    }

    private void ResetTransformsLayout()
    {
        _transformsLayout.Reset();
        _transformPreviewYaw = -0.55f;
        _transformPreviewPitch = 0.35f;
        _transformPreviewDistance = 4.5f;
        _transformPreviewTarget = new Vector3(0.5f, 0.5f, 0.5f);
    }

    private void EnsureTransformAssetsIndexed()
    {
        if (_transformsIndexed) return;
        _transformAssets.Clear();
        foreach (Block block in _api.World.Blocks)
        {
            if (block?.Code == null) continue;
            _transformAssets.Add(new(block, true));
        }

        foreach (Item item in _api.World.Items)
        {
            if (item?.Code == null) continue;
            _transformAssets.Add(new(item, false));
        }

        _transformAssets.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));
        RebuildVisibleTransformAssets();
        _transformsIndexed = true;
    }

    private void RebuildVisibleTransformAssets()
    {
        string filter = _transformsFilter.Trim();
        TransformAssetEntry? selected = SelectedTransformAsset;
        _visibleTransformAssets.Clear();
        foreach (TransformAssetEntry entry in _transformAssets)
        {
            if (!ImGuiLayoutHelper.MatchesDomain(_transformsDomainFilter, entry.Domain)) continue;
            if (_transformsTypeFilter == 1 && !entry.IsBlock) continue;
            if (_transformsTypeFilter == 2 && entry.IsBlock) continue;
            if (_transformsDirtyOnly && !_transformDirtyKeys.Any(key => key.StartsWith(entry.Key + "|", StringComparison.OrdinalIgnoreCase))) continue;
            if (!string.IsNullOrWhiteSpace(filter) && !entry.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            _visibleTransformAssets.Add(entry);
        }

        if (selected != null)
        {
            int selectedIndex = _visibleTransformAssets.FindIndex(entry => entry.Key == selected.Key);
            if (selectedIndex >= 0)
            {
                _transformsAssetIndex = selectedIndex;
                return;
            }
        }

        _transformsAssetIndex = Math.Clamp(_transformsAssetIndex, 0, Math.Max(0, _visibleTransformAssets.Count - 1));
    }

    private void DrawTransformsBrowser(NVector2 size)
    {
        ImGui.BeginChild("##transforms-browser", size, true);
        try
        {
            ImGui.SeparatorText("Transforms");
            if (ImGui.Button("Reload##transforms"))
            {
                _transformsIndexed = false;
                EnsureTransformAssetsIndexed();
            }

            if (ImGuiLayoutHelper.DrawDomainCombo("Domain##transforms-domain", ref _transformsDomainFilter, _transformAssets.Select(entry => entry.Domain)))
            {
                RebuildVisibleTransformAssets();
            }

            string[] typeLabels = ["All", "Blocks", "Items"];
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("Type##transforms-type", ref _transformsTypeFilter, typeLabels, typeLabels.Length))
            {
                RebuildVisibleTransformAssets();
            }

            if (ImGui.InputTextWithHint("##transforms-filter", "filter assets", ref _transformsFilter, 300))
            {
                RebuildVisibleTransformAssets();
            }

            if (ImGui.Checkbox("Dirty only##transforms-dirty", ref _transformsDirtyOnly))
            {
                RebuildVisibleTransformAssets();
            }

            ImGui.TextDisabled($"Showing {_visibleTransformAssets.Count} / {_transformAssets.Count} assets");
            ImGui.BeginChild("##transforms-asset-list", new NVector2(0, 0), false);
            for (int index = 0; index < _visibleTransformAssets.Count; index++)
            {
                TransformAssetEntry entry = _visibleTransformAssets[index];
                if (ImGui.Selectable($"{entry.Label}##transform-asset-{entry.Key}", index == _transformsAssetIndex))
                {
                    _transformsAssetIndex = index;
                    ResetTransformPreviewCameraToSelection();
                }
            }
            ImGui.EndChild();
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawTransformsViewport(NVector2 size)
    {
        ImGui.BeginChild("##transforms-viewport-panel", size, true);
        try
        {
            TransformAssetEntry? asset = SelectedTransformAsset;
            TransformSlotSelection? slot = GetSelectedTransformSlot(asset);
            if (asset == null || slot == null)
            {
                ImGui.TextDisabled("Select a block or item.");
                return;
            }

            ModelTransform transform = GetTransformDraft(asset, slot);
            DrawTransformPreviewSurface(asset, slot, transform);
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawTransformPreviewSurface(TransformAssetEntry asset, TransformSlotSelection slot, ModelTransform transform)
    {
        NVector2 available = ImGui.GetContentRegionAvail();
        NVector2 size = new(Math.Max(320f, available.X), Math.Max(280f, available.Y));
        ImGui.InvisibleButton($"##transform-preview-{asset.Key}-{slot.Key}", size);
        NVector2 min = ImGui.GetItemRectMin();
        NVector2 max = ImGui.GetItemRectMax();
        bool hovered = ImGui.IsItemHovered();

        if (hovered)
        {
            NVector2 delta = ImGui.GetIO().MouseDelta;
            bool pan = ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
                (ImGui.IsMouseDragging(ImGuiMouseButton.Right) && (ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift)));
            if (pan)
            {
                DevToolsPreviewCamera panCamera = BuildTransformPreviewCamera(min, max);
                float panScale = _transformPreviewDistance / Math.Max(120f, size.Y);
                _transformPreviewTarget -= panCamera.Right * delta.X * panScale;
                _transformPreviewTarget += panCamera.Up * delta.Y * panScale;
            }
            else if (ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                _transformPreviewYaw += delta.X * 0.01f;
                _transformPreviewPitch = Math.Clamp(_transformPreviewPitch + delta.Y * 0.01f, -1.45f, 1.45f);
            }

            float wheel = ImGui.GetIO().MouseWheel;
            if (Math.Abs(wheel) > 0.001f)
            {
                _transformPreviewDistance = Math.Clamp(_transformPreviewDistance * MathF.Pow(0.88f, wheel), 0.35f, 48f);
            }
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0.035f, 0.036f, 0.032f, 1f));
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 1f));
        uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.86f, 0.82f, 0.72f, 1f));
        drawList.AddRectFilled(min, max, background, 4f);

        BuildTransformPreviewMeshes(asset, slot, transform);
        List<DevToolsPreviewMeshInstance> instances = [];
        Matrixf identity = new();
        identity.Identity();
        if (_transformReferenceMesh != null) instances.Add(new(_transformReferenceMesh, identity));
        if (_transformPreviewMesh != null) instances.Add(new(_transformPreviewMesh, identity));

        DevToolsPreviewCamera camera = BuildTransformPreviewCamera(min, max);
        int textureId = EnsureTransformsPreviewRenderer().RenderToTexture(max.X - min.X, max.Y - min.Y, camera, instances, out string? skipReason);
        if (textureId > 0)
        {
            drawList.AddImage(new IntPtr(textureId), min, max, new NVector2(0f, 1f), new NVector2(1f, 0f));
        }
        else if (!string.IsNullOrWhiteSpace(skipReason))
        {
            drawList.AddText(min + new NVector2(12f, 54f), text, $"Preview skipped: {skipReason}");
        }

        drawList.PushClipRect(min, max, true);
        DrawTransformPreviewAxes(drawList, camera);
        drawList.PopClipRect();
        drawList.AddRect(min, max, border, 4f);
        drawList.AddText(min + new NVector2(12f, 10f), text, $"{asset.Label} / {slot.DisplayName}");
        drawList.AddText(min + new NVector2(12f, 30f), text, "RMB orbits. MMB or Shift+RMB pans. Mouse wheel zooms.");
    }

    private void DrawTransformsInspector(NVector2 size)
    {
        ImGui.BeginChild("##transforms-inspector", size, true, ImGuiWindowFlags.HorizontalScrollbar);
        try
        {
            TransformAssetEntry? asset = SelectedTransformAsset;
            if (asset == null)
            {
                ImGui.TextDisabled("Select a transform asset.");
                return;
            }

            ImGui.SeparatorText("Slot");
            ImGui.TextWrapped(asset.Collectible.Code.ToString());
            ImGui.Checkbox("Typed map##transform-use-typed", ref _transformUseTypedSlot);
            TransformSlotSelection? slot = DrawTransformSlotSelector(asset);
            if (slot == null) return;

            ModelTransform transform = GetTransformDraft(asset, slot);
            bool exists = TransformSlotExists(asset, slot);
            ImGui.TextDisabled(exists ? "Existing transform" : "Missing transform draft");

            if (!exists && ImGui.Button("Create slot##transform-create-slot"))
            {
                MarkTransformDirty(asset, slot);
            }

            DrawTransformReferenceSelector(slot);
            DrawTransformLiveControls(asset, slot, transform);

            ImGui.SeparatorText("Values");
            bool changed = false;
            float uniformScale = transform.ScaleXYZ.X;
            ImGui.SetNextItemWidth(120);
            if (ImGui.DragFloat("Uniform scale##transform-uniform-scale", ref uniformScale, 0.01f, 0.001f, 100f))
            {
                transform.Scale = Math.Max(0.001f, uniformScale);
                changed = true;
            }

            System.Numerics.Vector3 translation = new(transform.Translation.X, transform.Translation.Y, transform.Translation.Z);
            System.Numerics.Vector3 origin = new(transform.Origin.X, transform.Origin.Y, transform.Origin.Z);
            System.Numerics.Vector3 rotation = new(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z);
            System.Numerics.Vector3 scale = new(transform.ScaleXYZ.X, transform.ScaleXYZ.Y, transform.ScaleXYZ.Z);
            changed |= ImGui.DragFloat3("Translation##transform-values", ref translation, 0.01f);
            changed |= ImGui.DragFloat3("Rotation##transform-values", ref rotation, 0.25f);
            changed |= ImGui.DragFloat3("Origin##transform-values", ref origin, 0.01f);
            changed |= ImGui.DragFloat3("Scale XYZ##transform-values", ref scale, 0.01f, 0.001f, 100f);

            bool rotate = transform.Rotate;
            if (ImGui.Checkbox("Rotate in GUI##transform-values", ref rotate))
            {
                transform.Rotate = rotate;
                changed = true;
            }

            if (changed)
            {
                transform.Translation.Set(translation.X, translation.Y, translation.Z);
                transform.Rotation.Set(rotation.X, rotation.Y, rotation.Z);
                transform.Origin.Set(origin.X, origin.Y, origin.Z);
                transform.ScaleXYZ.Set(Math.Max(0.001f, scale.X), Math.Max(0.001f, scale.Y), Math.Max(0.001f, scale.Z));
                MarkTransformDirty(asset, slot);
            }

            DrawTransformGizmoControls(
                "transforms-tab",
                transform,
                GetGizmoContextForTransformCode(slot.AttributeCode),
                value =>
                {
                    MarkTransformDirty(asset, slot);
                    if (_liveApplyManager.AutoApply) ApplyTransformLive(asset, slot, value);
                });

            if (ImGui.Button("Reset default##transform-reset"))
            {
                _transformDrafts[slot.Key] = CreateDefaultTransformForSlot(asset, slot.AttributeCode);
                MarkTransformDirty(asset, slot);
            }
            ImGui.SameLine();
            if (ImGui.Button("Copy JSON##transform-copy"))
            {
                ImGui.SetClipboardText(JsonUtil.ToPrettyString(transform));
            }

            if (slot.CanSaveToSource)
            {
                ImGui.SameLine();
                if (ImGui.Button("Save to source##transform-source-save"))
                {
                    SourceSaveResult result = TrySaveTransformToSource(asset.Collectible, slot.AttributeCode, transform, slot.TypedKey);
                    if (result.Request != null)
                    {
                        QueueSourceSave(result, status => _transformsStatus = status);
                    }
                    else
                    {
                        _transformsStatus = result.Status;
                    }
                }
            }

            ImGui.SeparatorText("Raw JSON");
            string raw = JsonUtil.ToPrettyString(transform);
            ImGui.InputTextMultiline("##transform-raw", ref raw, (uint)Math.Max(raw.Length + 1, 1024), new NVector2(-1, 150), ImGuiInputTextFlags.ReadOnly);

            if (!string.IsNullOrWhiteSpace(_transformsStatus))
            {
                ImGui.SeparatorText("Status");
                ImGui.TextWrapped(_transformsStatus);
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private TransformSlotSelection? DrawTransformSlotSelector(TransformAssetEntry asset)
    {
        if (!_transformUseTypedSlot)
        {
            _transformDirectSlotIndex = Math.Clamp(_transformDirectSlotIndex, 0, DirectTransformAttributeCodes.Length - 1);
            ImGui.SetNextItemWidth(-1);
            ImGui.Combo("Transform##transform-direct-slot", ref _transformDirectSlotIndex, DirectTransformAttributeCodes, DirectTransformAttributeCodes.Length);
            string attributeCode = DirectTransformAttributeCodes[_transformDirectSlotIndex];
            return new(asset, attributeCode, null);
        }

        _transformTypedMapIndex = Math.Clamp(_transformTypedMapIndex, 0, TypedTransformAttributeCodes.Length - 1);
        ImGui.SetNextItemWidth(-1);
        ImGui.Combo("Transform map##transform-typed-map", ref _transformTypedMapIndex, TypedTransformAttributeCodes, TypedTransformAttributeCodes.Length);
        string attribute = TypedTransformAttributeCodes[_transformTypedMapIndex];
        string[] keys = GetTypedTransformKeys(asset, attribute).ToArray();
        if (keys.Length > 0)
        {
            int keyIndex = Math.Max(0, Array.IndexOf(keys, _transformTypedKey));
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("Existing key##transform-typed-key-combo", ref keyIndex, keys, keys.Length))
            {
                _transformTypedKey = keys[keyIndex];
            }
        }

        ImGui.InputTextWithHint("Key##transform-typed-key", "type key", ref _transformTypedKey, 120);
        if (string.IsNullOrWhiteSpace(_transformTypedKey))
        {
            ImGui.TextDisabled("Enter a typed transform key.");
            return null;
        }

        return new(asset, attribute, _transformTypedKey.Trim());
    }

    private void DrawTransformReferenceSelector(TransformSlotSelection slot)
    {
        ImGui.SeparatorText("Reference");
        string defaultReference = GetDefaultReferenceBlockCode(slot.AttributeCode);
        string effectiveReference = string.IsNullOrWhiteSpace(_transformReferenceBlockCode) ? defaultReference : _transformReferenceBlockCode;
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(effectiveReference) ? "No reference block" : $"Reference: {effectiveReference}");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##transform-reference-filter", "reference block filter", ref _transformReferenceFilter, 120);
        List<string> options = BuildReferenceBlockOptions(_transformReferenceFilter).Take(250).ToList();
        options.Insert(0, "");
        _transformReferenceBlockIndex = Math.Clamp(_transformReferenceBlockIndex, 0, options.Count - 1);
        if (ImGui.Combo("Override##transform-reference", ref _transformReferenceBlockIndex, options.Select(option => string.IsNullOrWhiteSpace(option) ? "<default>" : option).ToArray(), options.Count))
        {
            _transformReferenceBlockCode = options[_transformReferenceBlockIndex];
            _transformPreviewCacheKey = "";
        }
    }

    private void DrawTransformLiveControls(TransformAssetEntry asset, TransformSlotSelection slot, ModelTransform transform)
    {
        string liveKey = $"transform:{asset.Key}";
        _liveApplyManager.DrawTargetControls(
            $"transform-live-{slot.Key}",
            liveKey,
            asset.Label,
            true,
            () => ApplyTransformLive(asset, slot, transform),
            () => _liveApplyManager.Revert(liveKey));
    }

    private string ApplyTransformLive(TransformAssetEntry asset, TransformSlotSelection slot, ModelTransform transform)
    {
        string liveKey = $"transform:{asset.Key}";
        return _liveApplyManager.Apply(
            liveKey,
            asset.Label,
            () => CaptureTransformLiveSnapshot(asset),
            () =>
            {
                if (slot.TypedKey == null) ApplyDirectTransformAttribute(asset.Collectible, slot.AttributeCode, transform);
                else ApplyTypedTransformAttribute(asset.Collectible, slot.AttributeCode, slot.TypedKey, transform);
            },
            $"Live applied {slot.DisplayName} for {asset.Label}.");
    }

    private LivePatchSnapshot CaptureTransformLiveSnapshot(TransformAssetEntry asset)
    {
        JToken? original = asset.Collectible.Attributes?.Token?.DeepClone();
        return new(
            () => asset.Collectible.Attributes = original == null ? null : new JsonObject(original.DeepClone()),
            Path.Combine("assets", asset.Domain, "runtime-transforms", asset.Collectible.Code.Path.Replace('/', '_') + ".json"),
            () => (original ?? new JObject()).ToString(Newtonsoft.Json.Formatting.Indented));
    }

    private void BuildTransformPreviewMeshes(TransformAssetEntry asset, TransformSlotSelection slot, ModelTransform transform)
    {
        string referenceCode = string.IsNullOrWhiteSpace(_transformReferenceBlockCode)
            ? GetDefaultReferenceBlockCode(slot.AttributeCode)
            : _transformReferenceBlockCode;
        string cacheKey = $"{slot.Key}|{referenceCode}|{JsonUtil.ToString(transform)}";
        if (cacheKey == _transformPreviewCacheKey) return;

        _transformPreviewCacheKey = cacheKey;
        _transformPreviewMesh?.Dispose();
        _transformReferenceMesh?.Dispose();
        _transformPreviewMesh = null;
        _transformReferenceMesh = null;

        try
        {
            MeshData mesh;
            if (asset.Collectible is Block block)
            {
                _api.Tesselator.TesselateBlock(block, out mesh);
            }
            else if (asset.Collectible is Item item)
            {
                _api.Tesselator.TesselateItem(item, out mesh);
            }
            else
            {
                return;
            }

            mesh.ModelTransform(transform);
            _transformPreviewMesh = DevToolsPreviewMeshFactory.FromMesh(_api, asset.Label, mesh);

            if (!string.IsNullOrWhiteSpace(referenceCode))
            {
                Block? reference = _api.World.GetBlock(AssetLocation.Create(referenceCode, "game"));
                if (reference != null)
                {
                    _api.Tesselator.TesselateBlock(reference, out MeshData referenceMesh);
                    _transformReferenceMesh = DevToolsPreviewMeshFactory.FromMesh(_api, $"ref:{reference.Code}", referenceMesh);
                }
            }

            ResetTransformPreviewCameraToBounds();
        }
        catch (Exception exception)
        {
            _transformsStatus = $"Transform preview failed: {exception.Message}";
        }
    }

    private DevToolsPreviewCamera BuildTransformPreviewCamera(NVector2 min, NVector2 max)
    {
        return DevToolsPreviewCamera.Orbit(min, max, _transformPreviewTarget, _transformPreviewYaw, _transformPreviewPitch, _transformPreviewDistance);
    }

    private void DrawTransformPreviewAxes(ImDrawListPtr drawList, DevToolsPreviewCamera camera)
    {
        uint axisX = ImGui.ColorConvertFloat4ToU32(new NVector4(0.85f, 0.25f, 0.16f, 0.9f));
        uint axisY = ImGui.ColorConvertFloat4ToU32(new NVector4(0.32f, 0.9f, 0.34f, 0.9f));
        uint axisZ = ImGui.ColorConvertFloat4ToU32(new NVector4(0.25f, 0.42f, 0.95f, 0.9f));
        DrawTransformPreviewLine(drawList, camera, Vector3.Zero, new Vector3(1.5f, 0, 0), axisX, 2f);
        DrawTransformPreviewLine(drawList, camera, Vector3.Zero, new Vector3(0, 1.5f, 0), axisY, 2f);
        DrawTransformPreviewLine(drawList, camera, Vector3.Zero, new Vector3(0, 0, 1.5f), axisZ, 2f);
    }

    private static void DrawTransformPreviewLine(ImDrawListPtr drawList, DevToolsPreviewCamera camera, Vector3 start, Vector3 end, uint color, float thickness)
    {
        if (!camera.Project(start, out NVector2 a, out _) || !camera.Project(end, out NVector2 b, out _)) return;
        drawList.AddLine(a, b, color, thickness);
    }

    private void ResetTransformPreviewCameraToSelection()
    {
        _transformPreviewCacheKey = "";
        ResetTransformPreviewCameraToBounds();
    }

    private void ResetTransformPreviewCameraToBounds()
    {
        DevToolsPreviewBounds bounds = DevToolsPreviewBounds.Empty;
        if (_transformReferenceMesh != null) bounds = bounds.Include(_transformReferenceMesh.Bounds);
        if (_transformPreviewMesh != null) bounds = bounds.Include(_transformPreviewMesh.Bounds);
        if (!bounds.IsValid)
        {
            _transformPreviewTarget = new Vector3(0.5f, 0.5f, 0.5f);
            _transformPreviewDistance = 4.5f;
            return;
        }

        _transformPreviewTarget = bounds.Center;
        _transformPreviewDistance = Math.Clamp(bounds.Radius * 3.1f, 1.4f, 36f);
    }

    private DevToolsPreview3DRenderer EnsureTransformsPreviewRenderer()
    {
        return _transformsPreviewRenderer ??= new DevToolsPreview3DRenderer(_api);
    }

    private ModelTransform GetTransformDraft(TransformAssetEntry asset, TransformSlotSelection slot)
    {
        if (_transformDrafts.TryGetValue(slot.Key, out ModelTransform? draft)) return draft;
        ModelTransform transform = ReadTransform(asset, slot) ?? CreateDefaultTransformForSlot(asset, slot.AttributeCode);
        transform.EnsureDefaultValues();
        _transformDrafts[slot.Key] = transform;
        return transform;
    }

    private ModelTransform? ReadTransform(TransformAssetEntry asset, TransformSlotSelection slot)
    {
        if (slot.TypedKey == null)
        {
            return asset.Collectible.Attributes?[slot.AttributeCode].AsObject<ModelTransform>()?.Clone();
        }

        if (asset.Collectible.Attributes?[slot.AttributeCode].Token is not JObject map) return null;
        return map[slot.TypedKey] == null ? null : new JsonObject(map[slot.TypedKey]).AsObject<ModelTransform>()?.Clone();
    }

    private bool TransformSlotExists(TransformAssetEntry asset, TransformSlotSelection slot)
    {
        if (slot.TypedKey == null) return asset.Collectible.Attributes?[slot.AttributeCode].Exists == true;
        return asset.Collectible.Attributes?[slot.AttributeCode].Token is JObject map && map[slot.TypedKey] != null;
    }

    private void MarkTransformDirty(TransformAssetEntry asset, TransformSlotSelection slot)
    {
        _transformDirtyKeys.Add(slot.Key);
        _transformPreviewCacheKey = "";
        RebuildVisibleTransformAssets();
        if (_liveApplyManager.AutoApply)
        {
            ApplyTransformLive(asset, slot, GetTransformDraft(asset, slot));
        }
    }

    private TransformSlotSelection? GetSelectedTransformSlot(TransformAssetEntry? asset)
    {
        if (asset == null) return null;
        if (!_transformUseTypedSlot)
        {
            _transformDirectSlotIndex = Math.Clamp(_transformDirectSlotIndex, 0, DirectTransformAttributeCodes.Length - 1);
            return new(asset, DirectTransformAttributeCodes[_transformDirectSlotIndex], null);
        }

        _transformTypedMapIndex = Math.Clamp(_transformTypedMapIndex, 0, TypedTransformAttributeCodes.Length - 1);
        return string.IsNullOrWhiteSpace(_transformTypedKey)
            ? null
            : new TransformSlotSelection(asset, TypedTransformAttributeCodes[_transformTypedMapIndex], _transformTypedKey.Trim());
    }

    private IEnumerable<string> GetTypedTransformKeys(TransformAssetEntry asset, string attributeCode)
    {
        if (asset.Collectible.Attributes?[attributeCode].Token is not JObject map) yield break;
        foreach (JProperty property in map.Properties())
        {
            yield return property.Name;
        }
    }

    private IEnumerable<string> BuildReferenceBlockOptions(string filter)
    {
        foreach (Block block in _api.World.Blocks)
        {
            if (block?.Code == null) continue;
            string code = block.Code.ToString();
            if (string.IsNullOrWhiteSpace(filter) || code.Contains(filter, StringComparison.OrdinalIgnoreCase)) yield return code;
        }
    }

    private static string GetDefaultReferenceBlockCode(string attributeCode)
    {
        if (attributeCode.Contains("forge", StringComparison.OrdinalIgnoreCase)) return "game:forge";
        if (attributeCode.Contains("firepit", StringComparison.OrdinalIgnoreCase)) return "game:firepit";
        if (attributeCode.Contains("trap", StringComparison.OrdinalIgnoreCase)) return "game:baskettrap";
        if (attributeCode.Contains("shelf", StringComparison.OrdinalIgnoreCase)) return "game:shelf";
        if (attributeCode.Contains("toolrack", StringComparison.OrdinalIgnoreCase)) return "game:toolrack";
        if (attributeCode.Contains("display", StringComparison.OrdinalIgnoreCase)) return "game:displaycase";
        if (attributeCode.Contains("groundStorage", StringComparison.OrdinalIgnoreCase)) return "game:groundstorage";
        return "";
    }

    private static ModelTransform CreateDefaultTransformForSlot(TransformAssetEntry asset, string attributeCode)
    {
        bool block = asset.IsBlock;
        ModelTransform transform = attributeCode switch
        {
            "guiTransform" => block ? ModelTransform.BlockDefaultGui() : ModelTransform.ItemDefaultGui(),
            "groundTransform" or "groundStorageTransform" => block ? ModelTransform.BlockDefaultGround() : ModelTransform.ItemDefaultGround(),
            "tpHandTransform" or "tpOffHandTransform" => block ? ModelTransform.BlockDefaultTp() : ModelTransform.ItemDefaultTp(),
            _ => CreateDefaultTransform()
        };
        transform.EnsureDefaultValues();
        return transform;
    }

    private TransformAssetEntry? SelectedTransformAsset => _visibleTransformAssets.Count == 0 ? null : _visibleTransformAssets[Math.Clamp(_transformsAssetIndex, 0, _visibleTransformAssets.Count - 1)];

    private sealed record TransformAssetEntry(CollectibleObject Collectible, bool IsBlock)
    {
        public string Key => $"{(IsBlock ? "block" : "item")}:{Collectible.Code}";
        public string Domain => Collectible.Code?.Domain ?? "game";
        public string Label => $"{(IsBlock ? "Block" : "Item")} | {ImGuiLayoutHelper.CompactAssetCode(Collectible.Code?.ToString() ?? "unknown")}";
        public string SearchText => $"{Key} {Collectible.Code} {Label}";
    }

    private sealed record TransformSlotSelection(TransformAssetEntry Asset, string AttributeCode, string? TypedKey)
    {
        public string Key => $"{Asset.Key}|{AttributeCode}|{TypedKey ?? ""}";
        public string DisplayName => TypedKey == null ? AttributeCode : $"{AttributeCode} / {TypedKey}";
        public bool CanSaveToSource => true;
    }
}
#endif
