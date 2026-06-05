using System.Diagnostics;
using System.Text;
using InGameDevTools.Utils;
using InGameDevTools.Integration.Transpilers;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VanillaAnimation = Vintagestory.API.Common.Animation;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private sealed class VanillaAnimationPreviewScene : IDisposable
    {
        private readonly ICoreClientAPI _api;
        private readonly Dictionary<string, AnimationMetaData> _activeAnimationsByAnimCode = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AnimationMetaData> _ghostAnimationsByAnimCode = new(StringComparer.OrdinalIgnoreCase);
        private Shape _shape;
        private AnimationMetaData _metadata;
        private AnimationMetaData _ghostMetadata;
        private VanillaAnimation _animation;
        private string _activeAnimationCode;
        private ClientAnimator _animator;
        private ClientAnimator _ghostAnimator;
        private readonly MeshData _previewMeshData;
        private readonly MultiTextureMeshRef _meshRef;
        private MultiTextureMeshRef? _firstPersonMeshRef;
        private MultiTextureMeshRef? _immersiveFirstPersonMeshRef;
        private bool _classicFirstPersonBuildAttempted;
        private bool _immersiveFirstPersonBuildAttempted;
        private readonly bool _classicFirstPersonSupported;
        private readonly bool _immersiveFirstPersonSupported;
        private VanillaPreviewMode _previewMode = VanillaPreviewMode.Orbit;
        private long _renderRevision;
        private bool _disposed;

        private VanillaAnimationPreviewScene(
            ICoreClientAPI api,
            string key,
            string displayName,
            Shape shape,
            VanillaAnimation animation,
            AnimationMetaData metadata,
            ClientAnimator animator,
            VanillaPreviewMeshSet meshes,
            int textureId,
            VanillaModelBounds bounds,
            VanillaGuiTransform guiTransform,
            string status)
        {
            _api = api;
            Key = key;
            DisplayName = displayName;
            _shape = shape;
            _animation = animation;
            _metadata = metadata;
            _activeAnimationCode = GetAnimationCode(animation, metadata);
            _metadata.Animation = _activeAnimationCode;
            _animator = animator;
            _ghostMetadata = CloneAnimationMetaData(metadata);
            _ghostMetadata.Animation = _activeAnimationCode;
            _ghostAnimator = CreatePreviewAnimator(shape, animation, key);
            _previewMeshData = meshes.PreviewMeshData;
            _meshRef = meshes.Orbit;
            _firstPersonMeshRef = meshes.FirstPerson;
            _immersiveFirstPersonMeshRef = meshes.ImmersiveFirstPerson;
            _classicFirstPersonSupported = meshes.ClassicFirstPersonSupported;
            _immersiveFirstPersonSupported = meshes.ImmersiveFirstPersonSupported;
            MeshVerticesCount = meshes.VerticesCount;
            MeshIndicesCount = meshes.IndicesCount;
            TextureId = textureId;
            Status = status;
            GuiEntitySize = guiTransform.EntitySize;
            EntityEyeHeight = guiTransform.EyeHeight > 0 ? guiTransform.EyeHeight : Math.Max(0.05f, bounds.Height * guiTransform.EntitySize * 0.85f);
            GuiShapeRotateX = guiTransform.RotateX;
            GuiShapeRotateY = guiTransform.RotateY;
            GuiShapeRotateZ = guiTransform.RotateZ;
            FirstPersonFovDegrees = Math.Clamp(api.Settings.Int["fpHandsFoV"] > 0 ? api.Settings.Int["fpHandsFoV"] : 75, 25, 130);
            MainFovDegrees = Math.Clamp(api.Settings.Int["fieldOfView"] > 0 ? api.Settings.Int["fieldOfView"] : 70, 25, 130);
            FirstPersonYOffset = api.Settings.Float["fpHandsYOffset"];
            QuantityFrames = Math.Max(1, animation.QuantityFrames);
            ApplyBounds(bounds);
            _activeAnimationsByAnimCode[_activeAnimationCode] = _metadata;
            _ghostAnimationsByAnimCode[_activeAnimationCode] = _ghostMetadata;
            ForceEvaluatePose(0);
        }

        public string Key { get; }
        public string DisplayName { get; }
        public ICoreClientAPI Api => _api;
        public MultiTextureMeshRef MeshRef => _meshRef;
        public int MeshVerticesCount { get; private set; }
        public int MeshIndicesCount { get; private set; }
        public int TextureId { get; }
        public string Status { get; private set; }
        public int QuantityFrames { get; private set; }
        public float CurrentFrame { get; private set; }
        public bool Playing { get; set; }
        public ClientAnimator Animator => _animator;
        public ClientAnimator GhostAnimator => _ghostAnimator;
        public float ModelCenterX { get; private set; }
        public float ModelCenterY { get; private set; }
        public float ModelCenterZ { get; private set; }
        public float ModelWidth { get; private set; } = 1f;
        public float ModelHeight { get; private set; } = 2f;
        public float ModelDepth { get; private set; } = 1f;
        public Shape Shape => _shape;
        public float GuiEntitySize { get; private set; } = 1f;
        public float EntityEyeHeight { get; private set; } = 1.6f;
        public float GuiShapeRotateX { get; private set; }
        public float GuiShapeRotateY { get; private set; }
        public float GuiShapeRotateZ { get; private set; }
        public float FirstPersonFovDegrees { get; private set; } = 75f;
        public float MainFovDegrees { get; private set; } = 70f;
        public float FirstPersonYOffset { get; private set; }
        public bool ClassicFirstPersonAvailable => IsUsableMesh(_firstPersonMeshRef) || (_classicFirstPersonSupported && !_classicFirstPersonBuildAttempted);
        public bool ImmersiveFirstPersonAvailable => IsUsableMesh(_immersiveFirstPersonMeshRef) || (_immersiveFirstPersonSupported && !_immersiveFirstPersonBuildAttempted);
        public bool FirstPersonAvailable => ClassicFirstPersonAvailable || ImmersiveFirstPersonAvailable;
        public VanillaPreviewMode PreviewMode => _previewMode;
        public long RenderRevision => _renderRevision;

        public MultiTextureMeshRef GetMeshRef(VanillaPreviewMode mode)
        {
            return mode switch
            {
                VanillaPreviewMode.FirstPerson when IsUsableMesh(_firstPersonMeshRef) => _firstPersonMeshRef!,
                VanillaPreviewMode.ImmersiveFirstPerson when IsUsableMesh(_immersiveFirstPersonMeshRef) => _immersiveFirstPersonMeshRef!,
                _ => _meshRef
            };
        }

        public bool HasAttachmentPoint(string code)
        {
            return _animator.GetAttachmentPointPose(code) != null;
        }

        public static VanillaAnimationPreviewScene Create(ICoreClientAPI api, VanillaBrowserRow row)
        {
            Shape sourceShape = GetSourceShape(row) ?? throw new InvalidOperationException("Selected vanilla row has no loaded shape.");
            Shape shape = PrepareShapeForPreview(api, sourceShape, row.Key);
            ApplyEditedAnimationsToPreviewShape(row, shape);
            ResolvePreviewShapeAnimationReferences(api, shape, row.Key);
            VanillaAnimation animation = ResolvePreviewAnimation(row, shape, VanillaPreviewMode.Orbit) ?? throw new InvalidOperationException("Selected vanilla row has no matching animation in its preview shape.");
            PrepareAnimationFrames(shape, animation);
            AnimationMetaData metadata = BuildPreviewMetadata(row, animation, VanillaPreviewMode.Orbit);
            ClientAnimator animator = CreatePreviewAnimator(shape, animation, row.Key);
            VanillaPreviewMeshSet meshes = BuildPreviewMeshes(api, row, shape, animator, out int textureId);
            VanillaModelBounds bounds = CalculateModelBounds(shape);
            VanillaGuiTransform guiTransform = GetGuiTransform(row);
            string status = $"Loaded {row.Label}. Mesh parts: {meshes.Orbit.meshrefs?.Length ?? 0}. First-person: {(meshes.ClassicFirstPersonSupported ? "classic lazy" : "not available")}, {(meshes.ImmersiveFirstPersonSupported ? "immersive lazy" : "no immersive mesh")}. Bounds: {bounds.Width:0.00} x {bounds.Height:0.00} x {bounds.Depth:0.00}.";
            return new(api, row.Key, row.Label, shape, animation, metadata, animator, meshes, textureId, bounds, guiTransform, status);
        }

        public void ReloadAnimator(VanillaBrowserRow row)
        {
            Shape sourceShape = GetSourceShape(row) ?? throw new InvalidOperationException("Selected vanilla row has no loaded shape.");
            Shape shape = PrepareShapeForPreview(_api, sourceShape, row.Key);
            ApplyEditedAnimationsToPreviewShape(row, shape);
            ResolvePreviewShapeAnimationReferences(_api, shape, row.Key);
            VanillaAnimation animation = ResolvePreviewAnimation(row, shape, _previewMode) ?? throw new InvalidOperationException("Selected vanilla row has no matching animation in its preview shape.");
            PrepareAnimationFrames(shape, animation);
            AnimationMetaData metadata = BuildPreviewMetadata(row, animation, _previewMode);

            _shape = shape;
            _animation = animation;
            _metadata = metadata;
            _ghostMetadata = CloneAnimationMetaData(metadata);
            _activeAnimationCode = GetAnimationCode(animation, metadata);
            _metadata.Animation = _activeAnimationCode;
            _ghostMetadata.Animation = _activeAnimationCode;
            _animator = CreatePreviewAnimator(shape, animation, row.Key);
            _ghostAnimator = CreatePreviewAnimator(shape, animation, row.Key);
            ApplyBounds(CalculateModelBounds(shape));
            ApplyGuiTransform(GetGuiTransform(row));
            FirstPersonFovDegrees = Math.Clamp(_api.Settings.Int["fpHandsFoV"] > 0 ? _api.Settings.Int["fpHandsFoV"] : 75, 25, 130);
            MainFovDegrees = Math.Clamp(_api.Settings.Int["fieldOfView"] > 0 ? _api.Settings.Int["fieldOfView"] : 70, 25, 130);
            FirstPersonYOffset = _api.Settings.Float["fpHandsYOffset"];
            _activeAnimationsByAnimCode.Clear();
            _activeAnimationsByAnimCode[_activeAnimationCode] = _metadata;
            _ghostAnimationsByAnimCode.Clear();
            _ghostAnimationsByAnimCode[_activeAnimationCode] = _ghostMetadata;
            QuantityFrames = Math.Max(1, animation.QuantityFrames);
            CurrentFrame = Math.Clamp(CurrentFrame, 0, Math.Max(0, QuantityFrames - 1));
            ForceEvaluatePose(CurrentFrame);
        }

        public void SetPreviewMode(VanillaBrowserRow row, VanillaPreviewMode mode)
        {
            if ((mode == VanillaPreviewMode.FirstPerson && !EnsureFirstPersonMesh(immersive: false)) ||
                (mode == VanillaPreviewMode.ImmersiveFirstPerson && !EnsureFirstPersonMesh(immersive: true)))
            {
                mode = VanillaPreviewMode.Orbit;
            }

            if (_previewMode == mode) return;

            VanillaAnimation animation = ResolvePreviewAnimation(row, _shape, mode) ?? _animation;
            PrepareAnimationFrames(_shape, animation);
            AnimationMetaData metadata = BuildPreviewMetadata(row, animation, mode);

            _previewMode = mode;
            _animation = animation;
            _metadata = metadata;
            _ghostMetadata = CloneAnimationMetaData(metadata);
            _activeAnimationCode = GetAnimationCode(animation, metadata);
            _metadata.Animation = _activeAnimationCode;
            _ghostMetadata.Animation = _activeAnimationCode;
            _animator = CreatePreviewAnimator(_shape, animation, row.Key);
            _ghostAnimator = CreatePreviewAnimator(_shape, animation, row.Key);
            _activeAnimationsByAnimCode.Clear();
            _activeAnimationsByAnimCode[_activeAnimationCode] = _metadata;
            _ghostAnimationsByAnimCode.Clear();
            _ghostAnimationsByAnimCode[_activeAnimationCode] = _ghostMetadata;
            QuantityFrames = Math.Max(1, animation.QuantityFrames);
            CurrentFrame = Math.Clamp(CurrentFrame, 0, Math.Max(0, QuantityFrames - 1));
            ForceEvaluatePose(CurrentFrame);
        }

        public void Play()
        {
            if (_disposed) return;
            if (CurrentFrame >= QuantityFrames - 1)
            {
                Scrub(0);
            }
            else
            {
                EnsureActive();
            }

            Playing = true;
        }

        public void Tick(float deltaSeconds)
        {
            if (_disposed) return;
            EnsureActive();
            _animator.OnFrame(_activeAnimationsByAnimCode, deltaSeconds);
            RunningAnimation? state = _animator.GetAnimationState(_activeAnimationCode);
            if (state != null)
            {
                CurrentFrame = Math.Clamp(state.CurrentFrame, 0, Math.Max(0, QuantityFrames - 1));
            }

            MarkRenderDirty();
        }

        public void Scrub(float frame)
        {
            ForceEvaluatePose(frame);
        }

        private void ForceEvaluatePose(float frame)
        {
            if (_disposed) return;
            bool wasPlaying = Playing;
            CurrentFrame = Math.Clamp(frame, 0, Math.Max(0, QuantityFrames - 1));
            EnsureActive();
            _metadata.StartFrameOnce = CurrentFrame;
            _animator.OnFrame(_activeAnimationsByAnimCode, 0.001f);

            RunningAnimation? state = _animator.GetAnimationState(_activeAnimationCode);
            if (state != null)
            {
                state.meta = _metadata;
                state.EasingFactor = 1f;
                state.CurrentFrame = CurrentFrame;
                state.Iterations = CurrentFrame >= QuantityFrames - 1 ? 1 : 0;
            }

            _metadata.StartFrameOnce = CurrentFrame;
            _animator.OnFrame(_activeAnimationsByAnimCode, 0f);
            state = _animator.GetAnimationState(_activeAnimationCode);
            if (state != null)
            {
                state.meta = _metadata;
                state.EasingFactor = 1f;
                state.CurrentFrame = CurrentFrame;
                state.Iterations = CurrentFrame >= QuantityFrames - 1 ? 1 : 0;
            }
            Playing = wasPlaying;
            MarkRenderDirty();
        }

        public bool TryEvaluateGhostPose(float frame)
        {
            if (_disposed) return false;

            float ghostFrame = Math.Clamp(frame, 0, Math.Max(0, QuantityFrames - 1));
            _ghostAnimationsByAnimCode[_activeAnimationCode] = _ghostMetadata;
            _ghostMetadata.StartFrameOnce = ghostFrame;
            _ghostAnimator.OnFrame(_ghostAnimationsByAnimCode, 0.001f);

            RunningAnimation? state = _ghostAnimator.GetAnimationState(_activeAnimationCode);
            if (state != null)
            {
                state.meta = _ghostMetadata;
                state.EasingFactor = 1f;
                state.CurrentFrame = ghostFrame;
                state.Iterations = ghostFrame >= QuantityFrames - 1 ? 1 : 0;
            }

            _ghostMetadata.StartFrameOnce = ghostFrame;
            _ghostAnimator.OnFrame(_ghostAnimationsByAnimCode, 0f);
            state = _ghostAnimator.GetAnimationState(_activeAnimationCode);
            if (state != null)
            {
                state.meta = _ghostMetadata;
                state.EasingFactor = 1f;
                state.CurrentFrame = ghostFrame;
                state.Iterations = ghostFrame >= QuantityFrames - 1 ? 1 : 0;
            }

            return _ghostAnimator.Matrices != null;
        }

        private void MarkRenderDirty()
        {
            unchecked
            {
                _renderRevision++;
            }
        }

        private bool EnsureFirstPersonMesh(bool immersive)
        {
            if (immersive)
            {
                if (IsUsableMesh(_immersiveFirstPersonMeshRef)) return true;
                if (!_immersiveFirstPersonSupported || _immersiveFirstPersonBuildAttempted) return false;

                _immersiveFirstPersonBuildAttempted = true;
                _immersiveFirstPersonMeshRef = TryBuildPlayerFirstPersonMesh(_api, _previewMeshData, _animator, immersive: true);
                if (IsUsableMesh(_immersiveFirstPersonMeshRef))
                {
                    MarkRenderDirty();
                    return true;
                }

                Status = $"{Status} Immersive first-person mesh could not be built.";
                return false;
            }

            if (IsUsableMesh(_firstPersonMeshRef)) return true;
            if (!_classicFirstPersonSupported || _classicFirstPersonBuildAttempted) return false;

            _classicFirstPersonBuildAttempted = true;
            _firstPersonMeshRef = TryBuildPlayerFirstPersonMesh(_api, _previewMeshData, _animator, immersive: false);
            if (IsUsableMesh(_firstPersonMeshRef))
            {
                MarkRenderDirty();
                return true;
            }

            Status = $"{Status} Classic first-person mesh could not be built.";
            return false;
        }

        private static bool IsUsableMesh(MultiTextureMeshRef? meshRef)
        {
            return meshRef is { Disposed: false, Initialized: true };
        }

        public static bool TryBuildFirstPersonItemStack(ICoreClientAPI api, string code, out ItemStack? stack, out string normalizedCode)
        {
            stack = null;
            normalizedCode = "";
            string requested = code.Trim();
            if (requested.Length == 0) return false;

            bool itemOnly = requested.StartsWith("item:", StringComparison.OrdinalIgnoreCase);
            bool blockOnly = requested.StartsWith("block:", StringComparison.OrdinalIgnoreCase);
            if (itemOnly || blockOnly)
            {
                requested = requested[(requested.IndexOf(':') + 1)..];
            }

            static bool Matches(CollectibleObject collectible, string target)
            {
                string full = collectible.Code?.ToString() ?? "";
                string shortCode = collectible.Code?.Path ?? "";
                return string.Equals(full, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(shortCode, target, StringComparison.OrdinalIgnoreCase);
            }

            if (!blockOnly)
            {
                foreach (Item item in api.World.Items)
                {
                    if (item?.Code == null || !Matches(item, requested)) continue;
                    stack = new ItemStack(item, 1);
                    normalizedCode = item.Code.ToString();
                    return true;
                }
            }

            if (!itemOnly)
            {
                foreach (Block block in api.World.Blocks)
                {
                    if (block?.Code == null || !Matches(block, requested)) continue;
                    stack = new ItemStack(block, 1);
                    normalizedCode = block.Code.ToString();
                    return true;
                }
            }

            return false;
        }

        private void EnsureActive()
        {
            _activeAnimationsByAnimCode[_activeAnimationCode] = _metadata;
        }

        private void ApplyBounds(VanillaModelBounds bounds)
        {
            ModelCenterX = bounds.CenterX;
            ModelCenterY = bounds.CenterY;
            ModelCenterZ = bounds.CenterZ;
            ModelWidth = Math.Max(0.1f, bounds.Width);
            ModelHeight = Math.Max(0.1f, bounds.Height);
            ModelDepth = Math.Max(0.1f, bounds.Depth);
        }

        private void ApplyGuiTransform(VanillaGuiTransform transform)
        {
            GuiEntitySize = transform.EntitySize;
            EntityEyeHeight = transform.EyeHeight > 0 ? transform.EyeHeight : Math.Max(0.05f, ModelHeight * GuiEntitySize * 0.85f);
            GuiShapeRotateX = transform.RotateX;
            GuiShapeRotateY = transform.RotateY;
            GuiShapeRotateZ = transform.RotateZ;
        }

        private static string GetAnimationCode(VanillaAnimation animation, AnimationMetaData metadata)
        {
            return animation.Code ?? animation.Name ?? metadata.Animation ?? metadata.Code ?? "preview";
        }

        public void Dispose()
        {
            _disposed = true;
            _meshRef.Dispose();
            _firstPersonMeshRef?.Dispose();
            _immersiveFirstPersonMeshRef?.Dispose();
        }

        private static Shape? GetSourceShape(VanillaBrowserRow row)
        {
            return row.Document.Shape ?? row.ShapeAnimation?.Document.Shape ?? row.MetadataEntry?.LinkedShape?.Document.Shape;
        }

        private static void ApplyEditedAnimationsToPreviewShape(VanillaBrowserRow row, Shape shape)
        {
            VanillaAnimationDocument? document = row.ShapeAnimation?.Document
                ?? row.MetadataEntry?.ResolveCurrentShape()?.Document
                ?? (row.Document.Kind == VanillaDocumentKind.Shape ? row.Document : null);
            if (document == null || document.ShapeAnimations.Count == 0) return;

            List<VanillaAnimation> animations = (shape.Animations ?? []).Select(CloneVanillaAnimation).ToList();
            foreach (VanillaShapeAnimationEntry entry in document.ShapeAnimations)
            {
                VanillaAnimation clone = CloneVanillaAnimation(entry.Animation);
                string code = clone.Code ?? clone.Name ?? "";
                int targetIndex = !string.IsNullOrWhiteSpace(code)
                    ? animations.FindIndex(animation => string.Equals(animation.Code ?? animation.Name, code, StringComparison.OrdinalIgnoreCase))
                    : -1;
                if (targetIndex < 0 && entry.Index >= 0 && entry.Index < animations.Count)
                {
                    targetIndex = entry.Index;
                }

                if (targetIndex >= 0)
                {
                    animations[targetIndex] = clone;
                }
                else
                {
                    animations.Add(clone);
                }
            }

            shape.Animations = animations.ToArray();
        }

        private static VanillaGuiTransform GetGuiTransform(VanillaBrowserRow row)
        {
            EntityClientProperties? client = row.Document.EntityType?.Client
                ?? row.ShapeAnimation?.Document.EntityType?.Client
                ?? row.MetadataEntry?.Document.EntityType?.Client
                ?? row.MetadataEntry?.LinkedShape?.Document.EntityType?.Client;
            CompositeShape? shape = client?.ShapeForEntity ?? client?.Shape;
            EntityProperties? entityType = row.Document.EntityType
                ?? row.ShapeAnimation?.Document.EntityType
                ?? row.MetadataEntry?.Document.EntityType
                ?? row.MetadataEntry?.LinkedShape?.Document.EntityType;
            return new(
                Math.Max(0.001f, client?.Size ?? 1f),
                Math.Max(0f, (float)(entityType?.EyeHeight ?? 0)),
                shape?.rotateX ?? 0f,
                shape?.rotateY ?? 0f,
                shape?.rotateZ ?? 0f);
        }

        private static Shape PrepareShapeForPreview(ICoreClientAPI api, Shape sourceShape, string shapeName)
        {
            Shape shape = sourceShape.Clone() ?? throw new InvalidOperationException($"Preview shape '{shapeName}' could not be cloned.");
            if (shape.Elements == null || shape.Elements.Length == 0)
            {
                throw new InvalidOperationException($"Preview shape '{shapeName}' has no elements.");
            }

            shape.Textures ??= new();
            ResolvePreviewShapeAnimationReferences(api, shape, shapeName);

            return shape;
        }

        private static void ResolvePreviewShapeAnimationReferences(ICoreClientAPI api, Shape shape, string shapeName)
        {
            shape.AnimationsByCrc32 ??= new();
            shape.AnimationsByCrc32.Clear();
            shape.JointsById ??= new();
            shape.JointsById.Clear();

            Dictionary<string, ShapeElement> elementsByName = shape.CollectAndResolveReferences(api.World.Logger, shapeName)
                ?? throw new InvalidOperationException($"Preview shape '{shapeName}' reference resolution returned no elements.");
            if (elementsByName.Count == 0)
            {
                throw new InvalidOperationException($"Preview shape '{shapeName}' has no resolved elements.");
            }

            shape.CacheInvTransforms();
            shape.ResolveAndFindJoints(api.World.Logger, shapeName, elementsByName);
        }

        private static void PrepareAnimationFrames(Shape shape, VanillaAnimation animation)
        {
            if (string.IsNullOrWhiteSpace(animation.Code)) animation.Code = animation.Name;
            CompleteVanillaAnimationTransformGroups(animation);
            if (shape.Elements == null || shape.Elements.Length == 0)
            {
                throw new InvalidOperationException($"Preview animation '{animation.Code ?? animation.Name ?? "unknown"}' has no shape elements to animate.");
            }

            animation.GenerateAllFrames(shape.Elements, shape.JointsById);
        }

        private static ClientAnimator CreatePreviewAnimator(Shape shape, VanillaAnimation animation, string shapeName)
        {
            if (shape.Elements == null || shape.Elements.Length == 0)
            {
                throw new InvalidOperationException($"Preview shape '{shapeName}' has no elements for its animator.");
            }

            if (animation == null)
            {
                throw new InvalidOperationException($"Preview shape '{shapeName}' has no selected animation for its animator.");
            }

            return new ClientAnimator(() => 1, [animation], shape.Elements, shape.JointsById, null, null);
        }

        private static void CompleteVanillaAnimationTransformGroups(VanillaAnimation animation)
        {
            if (animation.KeyFrames == null) return;
            foreach (AnimationKeyFrame keyFrame in animation.KeyFrames)
            {
                if (keyFrame.Elements == null) continue;
                foreach (AnimationKeyFrameElement element in keyFrame.Elements.Values)
                {
                    CompleteVanillaElementTransformGroups(element);
                }
            }
        }

        private static VanillaModelBounds CalculateModelBounds(Shape shape)
        {
            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;
            float maxZ = float.NegativeInfinity;
            bool hasBounds = false;

            void Include(ShapeElement element)
            {
                if (element.From is { Length: >= 3 } from && element.To is { Length: >= 3 } to)
                {
                    float fromX = (float)from[0] / 16f;
                    float fromY = (float)from[1] / 16f;
                    float fromZ = (float)from[2] / 16f;
                    float toX = (float)to[0] / 16f;
                    float toY = (float)to[1] / 16f;
                    float toZ = (float)to[2] / 16f;

                    minX = Math.Min(minX, Math.Min(fromX, toX));
                    minY = Math.Min(minY, Math.Min(fromY, toY));
                    minZ = Math.Min(minZ, Math.Min(fromZ, toZ));
                    maxX = Math.Max(maxX, Math.Max(fromX, toX));
                    maxY = Math.Max(maxY, Math.Max(fromY, toY));
                    maxZ = Math.Max(maxZ, Math.Max(fromZ, toZ));
                    hasBounds = true;
                }

                if (element.Children == null) return;
                foreach (ShapeElement child in element.Children)
                {
                    Include(child);
                }
            }

            if (shape.Elements != null)
            {
                foreach (ShapeElement element in shape.Elements)
                {
                    Include(element);
                }
            }

            return hasBounds
                ? new(minX, minY, minZ, maxX, maxY, maxZ)
                : new(0f, 0f, 0f, 1f, 2f, 1f);
        }

        private static VanillaAnimation? ResolvePreviewAnimation(VanillaBrowserRow row, Shape previewShape, VanillaPreviewMode mode)
        {
            string? code = ResolvePreviewAnimationCode(row, previewShape, mode);
            if (string.IsNullOrWhiteSpace(code)) return previewShape.Animations?.FirstOrDefault();
            return previewShape.Animations?.FirstOrDefault(animation =>
                string.Equals(animation.Code ?? animation.Name, code, StringComparison.OrdinalIgnoreCase));
        }

        private static string? ResolvePreviewAnimationCode(VanillaBrowserRow row, Shape previewShape, VanillaPreviewMode mode)
        {
            if (row.MetadataEntry != null)
            {
                return ResolvePreviewMetadata(row, mode)?.Animation ?? row.MetadataEntry.Metadata.Animation;
            }

            string? code = row.ShapeAnimation?.Animation.Code ?? row.ShapeAnimation?.Animation.Name;
            if (mode == VanillaPreviewMode.Orbit || string.IsNullOrWhiteSpace(code)) return code;

            string suffix = GetPreviewModeAnimationSuffix(mode);
            if (HasFirstPersonSuffix(code)) return code;

            string variantCode = code + suffix;
            return previewShape.Animations?.Any(animation => string.Equals(animation.Code ?? animation.Name, variantCode, StringComparison.OrdinalIgnoreCase)) == true
                ? variantCode
                : code;
        }

        private static AnimationMetaData BuildPreviewMetadata(VanillaBrowserRow row, VanillaAnimation animation, VanillaPreviewMode mode)
        {
            AnimationMetaData metadata = row.MetadataEntry != null
                ? ResolvePreviewMetadata(row, mode) ?? CloneAnimationMetaData(row.MetadataEntry.Metadata)
                : new AnimationMetaData
                {
                    Code = animation.Code ?? animation.Name ?? "preview",
                    Animation = animation.Code ?? animation.Name ?? "preview",
                    AnimationSpeed = 1f,
                    Weight = 1f,
                    BlendMode = EnumAnimationBlendMode.Add,
                    EaseInSpeed = 10f,
                    EaseOutSpeed = 10f,
                    ClientSide = true
                };

            metadata.Code = string.IsNullOrWhiteSpace(metadata.Code) ? metadata.Animation : metadata.Code;
            metadata.Animation = string.IsNullOrWhiteSpace(metadata.Animation) ? animation.Code ?? animation.Name ?? metadata.Code : metadata.Animation;
            metadata.Init();
            return metadata;
        }

        private static AnimationMetaData? ResolvePreviewMetadata(VanillaBrowserRow row, VanillaPreviewMode mode)
        {
            if (row.MetadataEntry == null) return null;

            AnimationMetaData source = row.MetadataEntry.Metadata;
            if (mode == VanillaPreviewMode.Orbit || HasFirstPersonSuffix(source.Code))
            {
                return CloneAnimationMetaData(source);
            }

            if (mode == VanillaPreviewMode.FirstPerson)
            {
                AnimationMetaData sourceClone = CloneAnimationMetaData(source);
                sourceClone.Init();
                if (sourceClone.WithFpVariant && sourceClone.FpVariant != null)
                {
                    return CloneAnimationMetaData(sourceClone.FpVariant);
                }
            }

            string suffix = GetPreviewModeAnimationSuffix(mode);
            if (!string.IsNullOrWhiteSpace(source.Code) &&
                row.MetadataEntry.Document.EntityType?.Client?.AnimationsByMetaCode?.TryGetValue(source.Code + suffix, out AnimationMetaData? variant) == true)
            {
                return CloneAnimationMetaData(variant);
            }

            return CloneAnimationMetaData(source);
        }

        private static string GetPreviewModeAnimationSuffix(VanillaPreviewMode mode)
        {
            return mode == VanillaPreviewMode.ImmersiveFirstPerson ? "-ifp" : "-fp";
        }

        private static bool HasFirstPersonSuffix(string? code)
        {
            return !string.IsNullOrWhiteSpace(code) &&
                (code.EndsWith("-fp", StringComparison.OrdinalIgnoreCase) || code.EndsWith("-ifp", StringComparison.OrdinalIgnoreCase));
        }

        private static VanillaPreviewMeshSet BuildPreviewMeshes(ICoreClientAPI api, VanillaBrowserRow row, Shape shape, ClientAnimator animator, out int textureId)
        {
            if (api.Tesselator == null)
            {
                throw new InvalidOperationException("Preview tessellator is not available.");
            }

            if (api.Render == null)
            {
                throw new InvalidOperationException("Preview renderer is not available.");
            }

            if (api.EntityTextureAtlas == null)
            {
                throw new InvalidOperationException("Entity texture atlas is not available for preview tessellation.");
            }

            ITexPositionSource texSource = CreateTextureSource(api, row, shape);
            CompositeShape? compositeShape = GetCompositeShape(row);
            TesselationMetaData meta = new()
            {
                TexSource = texSource,
                WithJointIds = true,
                WithDamageEffect = true,
                TypeForLogging = row.Key,
                QuantityElements = compositeShape?.QuantityElements,
                SelectiveElements = compositeShape?.SelectiveElements,
                IgnoreElements = compositeShape?.IgnoreElements,
                Rotation = compositeShape == null
                    ? null
                    : new Vec3f(compositeShape.rotateX, compositeShape.rotateY, compositeShape.rotateZ)
            };

            api.Tesselator.TesselateShape(meta, shape, out MeshData mesh);
            if (mesh == null)
            {
                throw new InvalidOperationException($"Preview tessellation for {row.Label} returned no mesh.");
            }

            if (mesh.VerticesCount <= 0 || mesh.IndicesCount <= 0)
            {
                throw new InvalidOperationException($"Preview tessellation for {row.Label} produced an empty mesh.");
            }

            if (compositeShape != null)
            {
                mesh.Translate(compositeShape.offsetX, compositeShape.offsetY, compositeShape.offsetZ);
            }

            EnsurePreviewVertexColor(mesh);
            textureId = mesh.TextureIds is { Length: > 0 }
                ? mesh.TextureIds[0]
                : GetFallbackEntityTextureId(api);

            MultiTextureMeshRef orbit = api.Render.UploadMultiTextureMesh(mesh)
                ?? throw new InvalidOperationException($"Preview mesh upload for {row.Label} returned no mesh reference.");
            if (orbit.Disposed)
            {
                throw new InvalidOperationException($"Preview mesh upload for {row.Label} returned a disposed mesh reference.");
            }

            return new(
                orbit,
                null,
                null,
                mesh,
                HasPlayerFirstPersonMeshJoints(animator, immersive: false),
                HasPlayerFirstPersonMeshJoints(animator, immersive: true),
                mesh.VerticesCount,
                mesh.IndicesCount);
        }

        private static int GetFallbackEntityTextureId(ICoreClientAPI api)
        {
            if (api.EntityTextureAtlas.AtlasTextures is { Count: > 0 } atlasTextures && atlasTextures[0] != null)
            {
                return atlasTextures[0].TextureId;
            }

            TextureAtlasPosition? unknown = api.EntityTextureAtlas.UnknownTexturePosition;
            if (unknown?.atlasTextureId > 0)
            {
                return unknown.atlasTextureId;
            }

            throw new InvalidOperationException("Preview mesh has no texture ids and the entity texture atlas has no fallback texture.");
        }

        private static MultiTextureMeshRef? TryBuildPlayerFirstPersonMesh(ICoreClientAPI api, MeshData mesh, ClientAnimator animator, bool immersive)
        {
            try
            {
                return BuildPlayerFirstPersonMesh(api, mesh, animator, immersive);
            }
            catch (Exception exception)
            {
                api.Logger.VerboseDebug("[InGameDevTools] First-person vanilla preview mesh skipped: immersive={0}, reason={1}", immersive, exception.Message);
                return null;
            }
        }

        private static bool HasPlayerFirstPersonMeshJoints(ClientAnimator animator, bool immersive)
        {
            HashSet<int> jointIds = [];
            if (immersive)
            {
                LoadJointIdsRecursive(animator.GetPosebyName("Neck", StringComparison.InvariantCultureIgnoreCase), jointIds);
                return jointIds.Count > 0;
            }

            LoadJointIdsRecursive(animator.GetPosebyName("UpperArmR", StringComparison.InvariantCultureIgnoreCase), jointIds);
            LoadJointIdsRecursive(animator.GetPosebyName("UpperArmL", StringComparison.InvariantCultureIgnoreCase), jointIds);
            return jointIds.Count > 0;
        }

        private static MultiTextureMeshRef? BuildPlayerFirstPersonMesh(ICoreClientAPI api, MeshData mesh, ClientAnimator animator, bool immersive)
        {
            if (mesh.CustomInts == null || mesh.CustomInts.Values == null || mesh.VerticesCount <= 0) return null;

            HashSet<int> jointIds = [];
            if (immersive)
            {
                LoadJointIdsRecursive(animator.GetPosebyName("Neck", StringComparison.InvariantCultureIgnoreCase), jointIds);
                if (jointIds.Count == 0) return null;
            }
            else
            {
                LoadJointIdsRecursive(animator.GetPosebyName("UpperArmR", StringComparison.InvariantCultureIgnoreCase), jointIds);
                LoadJointIdsRecursive(animator.GetPosebyName("UpperArmL", StringComparison.InvariantCultureIgnoreCase), jointIds);
                if (jointIds.Count == 0) return null;
            }

            MeshData filtered = mesh.EmptyClone() ?? throw new InvalidOperationException("Could not create first-person preview mesh clone.");
            filtered.AddMeshData(mesh, vertexIndex =>
            {
                if (vertexIndex < 0 || vertexIndex >= mesh.VerticesCount) return false;
                int jointValueIndex = vertexIndex * 4;
                if (jointValueIndex < 0 || jointValueIndex >= mesh.CustomInts.Values.Length) return false;
                bool inSet = jointIds.Contains(mesh.CustomInts.Values[jointValueIndex]);
                return immersive ? !inSet : inSet;
            });

            return filtered.VerticesCount > 0 ? api.Render.UploadMultiTextureMesh(filtered) : null;
        }

        private static void LoadJointIdsRecursive(ElementPose? pose, HashSet<int> jointIds)
        {
            if (pose?.ForElement == null) return;

            if (pose.ForElement.JointId > 0)
            {
                jointIds.Add(pose.ForElement.JointId);
            }

            if (pose.ChildElementPoses == null) return;
            foreach (ElementPose child in pose.ChildElementPoses)
            {
                LoadJointIdsRecursive(child, jointIds);
            }
        }

        private static void EnsurePreviewVertexColor(MeshData mesh)
        {
            int requiredLength = mesh.VerticesCount * 4;
            if (requiredLength <= 0) return;

            if (mesh.Rgba == null || mesh.Rgba.Length < requiredLength)
            {
                mesh.Rgba = new byte[requiredLength];
                FillPreviewVertexColor(mesh.Rgba);
                return;
            }

            bool hasVisibleColor = false;
            for (int index = 0; index + 3 < requiredLength; index += 4)
            {
                if (mesh.Rgba[index + 3] == 0) continue;
                if ((mesh.Rgba[index + 0] | mesh.Rgba[index + 1] | mesh.Rgba[index + 2]) == 0) continue;
                hasVisibleColor = true;
                break;
            }

            if (!hasVisibleColor)
            {
                FillPreviewVertexColor(mesh.Rgba);
                return;
            }

            for (int index = 3; index < requiredLength; index += 4)
            {
                if (mesh.Rgba[index] == 0)
                {
                    mesh.Rgba[index] = 255;
                }
            }
        }

        private static void FillPreviewVertexColor(byte[] rgba)
        {
            for (int index = 0; index + 3 < rgba.Length; index += 4)
            {
                rgba[index + 0] = 255;
                rgba[index + 1] = 255;
                rgba[index + 2] = 255;
                rgba[index + 3] = 255;
            }
        }

        private static CompositeShape? GetCompositeShape(VanillaBrowserRow row)
        {
            if (GetPlayerModelSource(row) != null)
            {
                return null;
            }

            EntityClientProperties? client = row.Document.EntityType?.Client
                ?? row.ShapeAnimation?.Document.EntityType?.Client
                ?? row.MetadataEntry?.Document.EntityType?.Client
                ?? row.MetadataEntry?.LinkedShape?.Document.EntityType?.Client;
            return client?.ShapeForEntity ?? client?.Shape;
        }

        private static ITexPositionSource CreateTextureSource(ICoreClientAPI api, VanillaBrowserRow row, Shape shape)
        {
            if (GetPlayerModelSource(row) != null && shape.Textures is { Count: > 0 })
            {
                return new VanillaEntityTextureSource(api, shape, row.Key, new Dictionary<string, CompositeTexture>());
            }

            IDictionary<string, CompositeTexture>? textures = row.Document.EntityType?.Client?.Textures;
            if (textures != null && textures.Count > 0)
            {
                return new VanillaEntityTextureSource(api, shape, row.Key, textures);
            }

            return new ShapeTextureSource(api, shape, row.Key);
        }

        private static VanillaPlayerModelSource? GetPlayerModelSource(VanillaBrowserRow row)
        {
            return row.Document.PlayerModelSource
                ?? row.ShapeAnimation?.Document.PlayerModelSource
                ?? row.MetadataEntry?.Document.PlayerModelSource
                ?? row.MetadataEntry?.LinkedShape?.Document.PlayerModelSource;
        }
    }

    private sealed class VanillaEntityTextureSource : ITexPositionSource
    {
        private readonly ICoreClientAPI _api;
        private readonly Shape _shape;
        private readonly string _filenameForLogging;
        private readonly IDictionary<string, CompositeTexture> _textures;
        private readonly HashSet<string> _missingTextures = new(StringComparer.OrdinalIgnoreCase);

        public VanillaEntityTextureSource(ICoreClientAPI api, Shape shape, string filenameForLogging, IDictionary<string, CompositeTexture> textures)
        {
            _api = api;
            _shape = shape;
            _filenameForLogging = filenameForLogging;
            _textures = textures;
        }

        public Size2i AtlasSize => _api.EntityTextureAtlas.Size;

        public TextureAtlasPosition this[string textureCode]
        {
            get
            {
                if (string.IsNullOrWhiteSpace(textureCode))
                {
                    return _api.EntityTextureAtlas.UnknownTexturePosition;
                }

                if (_textures.TryGetValue(textureCode, out CompositeTexture? texture) && texture != null)
                {
                    return GetEntityTexturePosition(textureCode, texture);
                }

                if (_shape.Textures != null && _shape.Textures.TryGetValue(textureCode, out AssetLocation? texturePath) && texturePath != null)
                {
                    if (_api.EntityTextureAtlas.GetOrInsertTexture(texturePath, out _, out TextureAtlasPosition texPos))
                    {
                        return texPos;
                    }

                    return _api.EntityTextureAtlas.UnknownTexturePosition;
                }

                if (_textures.TryGetValue("all", out CompositeTexture? fallbackTexture) && fallbackTexture != null)
                {
                    return GetEntityTexturePosition("all", fallbackTexture);
                }

                if (_missingTextures.Add(textureCode))
                {
                    _api.Logger.Warning("Shape {0} has an element using texture code {1}, but no entity texture mapping exists", _filenameForLogging, textureCode);
                }

                return _api.EntityTextureAtlas.UnknownTexturePosition;
            }
        }

        private TextureAtlasPosition GetEntityTexturePosition(string textureCode, CompositeTexture texture)
        {
            if (texture.Baked == null)
            {
                texture.Bake(_api.Assets);
            }

            BakedCompositeTexture? baked = GetDefaultBakedTexture(texture);
            TextureAtlasPosition? bakedPosition = GetEntityAtlasPosition(baked);
            if (bakedPosition != null && (baked?.TextureSubId > 0 || IsUnknownTexture(baked)))
            {
                return bakedPosition;
            }

            if (baked?.BakedName != null &&
                _api.EntityTextureAtlas.GetOrInsertTexture(baked.BakedName, out int textureSubId, out TextureAtlasPosition insertedPosition))
            {
                baked.TextureSubId = textureSubId;
                if (ReferenceEquals(baked, texture.Baked))
                {
                    texture.Baked.TextureSubId = textureSubId;
                }

                return insertedPosition;
            }

            if (_missingTextures.Add(textureCode))
            {
                _api.Logger.Warning("Could not resolve entity texture code {0} while tessellating {1}", textureCode, _filenameForLogging);
            }

            return _api.EntityTextureAtlas.UnknownTexturePosition;
        }

        private TextureAtlasPosition? GetEntityAtlasPosition(BakedCompositeTexture? baked)
        {
            if (baked == null) return null;

            int textureSubId = baked.TextureSubId;
            TextureAtlasPosition[]? positions = _api.EntityTextureAtlas.Positions;
            if (positions == null) return null;

            return textureSubId >= 0 && textureSubId < positions.Length
                ? positions[textureSubId]
                : null;
        }

        private static BakedCompositeTexture? GetDefaultBakedTexture(CompositeTexture texture)
        {
            BakedCompositeTexture? baked = texture.Baked;
            return baked?.BakedVariants is { Length: > 0 } variants
                ? variants[0] ?? baked
                : baked;
        }

        private static bool IsUnknownTexture(BakedCompositeTexture? baked)
        {
            return baked?.BakedName?.Path == "unknown";
        }
    }

    private readonly struct VanillaModelBounds
    {
        public VanillaModelBounds(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            MinX = minX;
            MinY = minY;
            MinZ = minZ;
            MaxX = maxX;
            MaxY = maxY;
            MaxZ = maxZ;
        }

        public float MinX { get; }
        public float MinY { get; }
        public float MinZ { get; }
        public float MaxX { get; }
        public float MaxY { get; }
        public float MaxZ { get; }
        public float Width => Math.Max(0.1f, MaxX - MinX);
        public float Height => Math.Max(0.1f, MaxY - MinY);
        public float Depth => Math.Max(0.1f, MaxZ - MinZ);
        public float CenterX => (MinX + MaxX) * 0.5f;
        public float CenterY => (MinY + MaxY) * 0.5f;
        public float CenterZ => (MinZ + MaxZ) * 0.5f;
    }

    private readonly record struct VanillaGizmoProjection(
        NVector2 Center,
        float Scale,
        NVector2 AxisX,
        NVector2 AxisY,
        NVector2 AxisZ,
        NVector2[] RingX,
        NVector2[] RingY,
        NVector2[] RingZ,
        NVector2[] BoundsCorners,
        bool HasVisualCenter,
        NVector2 VisualCenter,
        VanillaGizmoTranslationBasis TranslationBasis,
        NVector3 AxisXModel,
        NVector3 AxisYModel,
        NVector3 AxisZModel,
        RigIkMatrix3 RotationParentBasis,
        Vec3d BaseRotationDegrees);

    private readonly struct VanillaGizmoTranslationBasis
    {
        public static VanillaGizmoTranslationBasis Identity { get; } = new(NVector3.UnitX, NVector3.UnitY, NVector3.UnitZ);

        private readonly float determinant;

        public VanillaGizmoTranslationBasis(NVector3 axisX, NVector3 axisY, NVector3 axisZ)
        {
            AxisX = axisX;
            AxisY = axisY;
            AxisZ = axisZ;
            determinant = axisX.X * (axisY.Y * axisZ.Z - axisY.Z * axisZ.Y) -
                axisY.X * (axisX.Y * axisZ.Z - axisX.Z * axisZ.Y) +
                axisZ.X * (axisX.Y * axisY.Z - axisX.Z * axisY.Y);
        }

        public NVector3 AxisX { get; }
        public NVector3 AxisY { get; }
        public NVector3 AxisZ { get; }

        public NVector3 ModelToOffsetDelta(NVector3 modelDelta)
        {
            if (Math.Abs(determinant) < 0.000001f)
            {
                return new NVector3(
                    ProjectOntoBasis(modelDelta, AxisX),
                    ProjectOntoBasis(modelDelta, AxisY),
                    ProjectOntoBasis(modelDelta, AxisZ));
            }

            float tx = modelDelta.X * (AxisY.Y * AxisZ.Z - AxisY.Z * AxisZ.Y) -
                AxisY.X * (modelDelta.Y * AxisZ.Z - modelDelta.Z * AxisZ.Y) +
                AxisZ.X * (modelDelta.Y * AxisY.Z - modelDelta.Z * AxisY.Y);
            float ty = AxisX.X * (modelDelta.Y * AxisZ.Z - modelDelta.Z * AxisZ.Y) -
                modelDelta.X * (AxisX.Y * AxisZ.Z - AxisX.Z * AxisZ.Y) +
                AxisZ.X * (AxisX.Y * modelDelta.Z - AxisX.Z * modelDelta.Y);
            float tz = AxisX.X * (AxisY.Y * modelDelta.Z - AxisY.Z * modelDelta.Y) -
                AxisY.X * (AxisX.Y * modelDelta.Z - AxisX.Z * modelDelta.Y) +
                modelDelta.X * (AxisX.Y * AxisY.Z - AxisX.Z * AxisY.Y);

            return new NVector3(tx / determinant, ty / determinant, tz / determinant);
        }

        private static float ProjectOntoBasis(NVector3 modelDelta, NVector3 basis)
        {
            float lengthSquared = basis.LengthSquared();
            return lengthSquared < 0.000001f ? 0f : NVector3.Dot(modelDelta, basis) / lengthSquared;
        }
    }

    private readonly record struct VanillaViewportElementHit(
        string ElementName,
        NVector2[] BoundsCorners,
        NVector2 Center,
        double Distance,
        float ScreenArea,
        int HierarchyDepth);

    private sealed record VanillaPreviewMeshSet(
        MultiTextureMeshRef Orbit,
        MultiTextureMeshRef? FirstPerson,
        MultiTextureMeshRef? ImmersiveFirstPerson,
        MeshData PreviewMeshData,
        bool ClassicFirstPersonSupported,
        bool ImmersiveFirstPersonSupported,
        int VerticesCount,
        int IndicesCount);

    private readonly record struct VanillaGuiTransform(float EntitySize, float EyeHeight, float RotateX, float RotateY, float RotateZ);

    private readonly record struct VanillaPreviewCameraState(
        Matrixf Projection,
        Matrixf View,
        Matrixf ProjectionView,
        Matrixf Model,
        NVector3 Eye,
        NVector3 Target,
        float Distance);

    private readonly record struct VanillaPreviewGhost(bool Enabled, float Frame, float Opacity, float Red, float Green, float Blue, string Label)
    {
        public static VanillaPreviewGhost Disabled { get; } = new(false, 0f, 0f, 0f, 0f, 0f, "");
    }

    private readonly record struct VanillaPreviewRenderKey(
        string SceneKey,
        long RenderRevision,
        int Width,
        int Height,
        float Yaw,
        float Pitch,
        float Zoom,
        float PanX,
        float PanY,
        VanillaPreviewMode Mode,
        bool FirstPersonInspectCamera,
        float FirstPersonLookPitchDegrees,
        string FirstPersonRightHandItemCode,
        string FirstPersonLeftHandItemCode,
        bool WorldLighting,
        string GhostKey);

    private sealed class VanillaAnimationViewport3DRenderer : IDisposable
    {
        private readonly ICoreClientAPI _api;
        private FrameBufferRef? _frameBuffer;
        private VanillaPreviewRenderKey? _lastRenderKey;
        private int _lastTextureId;
        private string _lastSceneLogKey = "";
        private string _lastFrameLogKey = "";
        private string _lastSkipLogKey = "";
        private long _lastSkipLogAtMs;

        public VanillaAnimationViewport3DRenderer(ICoreClientAPI api)
        {
            _api = api;
        }

        public void SetVisible(bool visible)
        {
            if (visible) return;
            ClearRenderCache();
            _lastFrameLogKey = "";
            _lastSkipLogKey = "";
        }

        private static string BuildGhostRenderKey(IReadOnlyList<VanillaPreviewGhost> ghosts)
        {
            if (ghosts.Count == 0) return "";
            return string.Join(
                "|",
                ghosts
                    .Where(ghost => ghost.Enabled)
                    .Select(ghost => $"{ghost.Frame:0.###}:{ghost.Opacity:0.###}:{ghost.Red:0.###}:{ghost.Green:0.###}:{ghost.Blue:0.###}:{ghost.Label}"));
        }

        public int RenderToTexture(
            VanillaAnimationPreviewScene scene,
            float width,
            float height,
            float yaw,
            float pitch,
            float zoom,
            float panX,
            float panY,
            VanillaPreviewMode mode,
            bool firstPersonInspectCamera,
            float firstPersonLookPitchDegrees,
            string firstPersonRightHandItemCode,
            string firstPersonLeftHandItemCode,
            bool worldLighting,
            IReadOnlyList<VanillaPreviewGhost> ghosts,
            bool verboseLogs,
            out string? skipReason)
        {
            skipReason = null;
            if (width <= 32 || height <= 32) return Skip(scene, mode, width, height, "viewport too small", verboseLogs, out skipReason);

            MultiTextureMeshRef meshRef = scene.GetMeshRef(mode);
            if (meshRef.Disposed) return Skip(scene, mode, width, height, "mesh disposed", verboseLogs, out skipReason);
            if (!meshRef.Initialized) return Skip(scene, mode, width, height, "mesh not initialized", verboseLogs, out skipReason);

            int framebufferWidth = Math.Max(1, (int)Math.Ceiling(width));
            int framebufferHeight = Math.Max(1, (int)Math.Ceiling(height));
            VanillaPreviewRenderKey renderKey = new(
                scene.Key,
                scene.RenderRevision,
                framebufferWidth,
                framebufferHeight,
                yaw,
                pitch,
                zoom,
                panX,
                panY,
                mode,
                firstPersonInspectCamera,
                firstPersonLookPitchDegrees,
                firstPersonRightHandItemCode.Trim(),
                firstPersonLeftHandItemCode.Trim(),
                worldLighting,
                BuildGhostRenderKey(ghosts));
            if (_lastTextureId > 0 &&
                _lastRenderKey == renderKey &&
                _frameBuffer is { Disposed: false, ColorTextureIds.Length: > 0 })
            {
                return _lastTextureId;
            }

            FrameBufferRef frameBuffer = EnsureFrameBuffer(framebufferWidth, framebufferHeight);
            if (frameBuffer == null || frameBuffer.Disposed)
            {
                return Skip(scene, mode, width, height, "preview framebuffer unavailable", verboseLogs, out skipReason);
            }

            if (frameBuffer.ColorTextureIds == null || frameBuffer.ColorTextureIds.Length == 0)
            {
                return Skip(scene, mode, width, height, "preview framebuffer has no color texture", verboseLogs, out skipReason);
            }

            VanillaPreviewCameraState camera = BuildVanillaPreviewCamera(scene, framebufferWidth, framebufferHeight, yaw, pitch, zoom, panX, panY, mode, firstPersonInspectCamera, firstPersonLookPitchDegrees, firstPersonRightHandItemCode);
            IRenderAPI render = _api.Render;
            if (render == null)
            {
                return Skip(scene, mode, width, height, "render API unavailable", verboseLogs, out skipReason);
            }

            FrameBufferRef? restoreFrameBuffer = render.CurrentFrameBuffer;
            IShaderProgram? previous = render.CurrentActiveShader;
            int[] restoreViewport = new int[4];
            GL.GetInteger(GetPName.Viewport, restoreViewport);
            bool restoreDepthTest = GL.IsEnabled(EnableCap.DepthTest);
            GL.GetInteger(GetPName.DepthFunc, out int restoreDepthFunc);
            GL.GetBoolean(GetPName.DepthWritemask, out bool restoreDepthMask);
            GL.GetDouble(GetPName.DepthClearValue, out double restoreDepthClearValue);
            bool restoreCullFace = GL.IsEnabled(EnableCap.CullFace);
            GL.GetInteger(GetPName.FrontFace, out int restoreFrontFace);
            GL.GetInteger(GetPName.CullFaceMode, out int restoreCullFaceMode);
            bool restoreBlend = GL.IsEnabled(EnableCap.Blend);
            bool[] restoreColorMask = new bool[4];
            GL.GetBoolean(GetPName.ColorWritemask, restoreColorMask);
            float[] restoreClearColor = new float[4];
            GL.GetFloat(GetPName.ColorClearValue, restoreClearColor);
            IShaderProgram? shader = null;
            string shaderName = "";
            FramebufferErrorCode frameBufferStatus = FramebufferErrorCode.FramebufferComplete;
            ErrorCode glError = ErrorCode.NoError;

            try
            {
                render.CurrentFrameBuffer = frameBuffer;
                frameBufferStatus = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                if (frameBufferStatus != FramebufferErrorCode.FramebufferComplete)
                {
                    return Skip(scene, mode, width, height, $"framebuffer incomplete: {frameBufferStatus}", verboseLogs, out skipReason);
                }

                render.GlViewport(0, 0, framebufferWidth, framebufferHeight);
                render.GLEnableDepthTest();
                GL.DepthFunc(DepthFunction.Lequal);
                render.GLDepthMask(true);
                render.GlDisableCullFace();
                render.GlToggleBlend(false);
                GL.ClearColor(0.055f, 0.052f, 0.045f, 1f);
                GL.ClearDepth(1.0);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                GL.ColorMask(true, true, true, false);

                ModSystemFpHands? fpHands = _api.ModLoader.GetModSystem<ModSystemFpHands>(true);
                bool classicFirstPerson = mode == VanillaPreviewMode.FirstPerson && fpHands?.fpModeHandShader != null;
                shader = classicFirstPerson ? fpHands!.fpModeHandShader : render.GetEngineShader(EnumShaderProgram.Entityanimated);
                shaderName = classicFirstPerson ? "fpModeHandShader" : "Entityanimated";
                if (shader == null)
                {
                    return Skip(scene, mode, width, height, $"preview shader unavailable: {shaderName}", verboseLogs, out skipReason);
                }

                previous?.Stop();
                shader.Use();

                SetUniform(shader, "extraGlow", 0);
                SetUniform(shader, "rgbaAmbientIn", worldLighting ? render.AmbientColor : new Vec3f(1f, 1f, 1f));
                SetUniform(shader, "rgbaLightIn", GetPreviewLight(render, worldLighting));
                SetUniform(shader, "rgbaFogIn", worldLighting ? render.FogColor : new Vec4f(0f, 0f, 0f, 0f));
                SetUniform(shader, "fogMinIn", worldLighting ? render.FogMin : 0f);
                SetUniform(shader, "fogDensityIn", worldLighting ? render.FogDensity : 0f);
                SetUniform(shader, "renderColor", ColorUtil.WhiteArgbVec);
                SetUniform(shader, "alphaTest", 0.01f);
                SetUniform(shader, "depthOffset", GetFirstPersonDepthOffset(mode, fpHands));
                Vec3f lightPosition = render.ShaderUniforms?.LightPosition3D ?? new Vec3f(0.7071068f, -0.7071068f, 0f);
                SetUniform(shader, "lightPosition", lightPosition);
                SetUniformMatrix(shader, "projectionMatrix", camera.Projection.Values);
                SetUniformMatrix(shader, "viewMatrix", camera.View.Values);
                SetUniformMatrix(shader, "modelMatrix", camera.Model.Values);
                SetUniform(shader, "viewDistance", 1024f);
                SetUniform(shader, "addRenderFlags", 0);
                SetUniform(shader, "windWaveIntensity", 0f);
                SetUniform(shader, "entityId", 0);
                SetUniform(shader, "glitchFlicker", 0);
                SetUniform(shader, "frostAlpha", 0f);
                SetUniform(shader, "globalWarpIntensity", 0f);
                SetUniform(shader, "glitchWaviness", 0f);
                SetUniform(shader, "waterWaveCounter", render.ShaderUniforms?.WaterWaveCounter ?? 0f);
                SetUniform(shader, "glitchEffectStrength", 0f);
                if (shader.UBOs != null && shader.UBOs.TryGetValue("Animation", out UBORef animationUbo))
                {
                    if (scene.Animator.Matrices == null)
                    {
                        return Skip(scene, mode, width, height, "preview animator has no matrices", verboseLogs, out skipReason);
                    }

                    animationUbo.Update(scene.Animator.Matrices, 0, scene.Animator.MaxJointId * 16 * 4);
                }

                LogVerboseScene(scene, mode, meshRef, verboseLogs);
                render.RenderMultiTextureMesh(meshRef, "entityTex", 0);
                foreach (VanillaPreviewGhost ghost in ghosts)
                {
                    if (!ghost.Enabled || !scene.TryEvaluateGhostPose(ghost.Frame)) continue;

                    render.GlToggleBlend(true, EnumBlendMode.Standard);
                    render.GLDepthMask(false);
                    SetUniform(shader, "renderColor", new Vec4f(ghost.Red, ghost.Green, ghost.Blue, Math.Clamp(ghost.Opacity, 0.05f, 0.8f)));
                    if (shader.UBOs != null && shader.UBOs.TryGetValue("Animation", out UBORef ghostAnimationUbo) && scene.GhostAnimator.Matrices != null)
                    {
                        ghostAnimationUbo.Update(scene.GhostAnimator.Matrices, 0, scene.GhostAnimator.MaxJointId * 16 * 4);
                    }

                    render.RenderMultiTextureMesh(meshRef, "entityTex", 0);
                    render.GLDepthMask(true);
                    render.GlToggleBlend(false);
                    SetUniform(shader, "renderColor", ColorUtil.WhiteArgbVec);
                }
                shader.Stop();
                shader = null;

                RenderFirstPersonHeldItem(scene, camera, mode, fpHands, worldLighting, firstPersonRightHandItemCode, rightHand: true);
                if (!string.IsNullOrWhiteSpace(firstPersonLeftHandItemCode))
                {
                    RenderFirstPersonHeldItem(scene, camera, mode, fpHands, worldLighting, firstPersonLeftHandItemCode, rightHand: false);
                }

                glError = GL.GetError();
                previous?.Use();
                LogVerboseFrame(scene, mode, meshRef, frameBuffer, framebufferWidth, framebufferHeight, shaderName, frameBufferStatus, glError, verboseLogs);
                _lastRenderKey = renderKey;
                _lastTextureId = frameBuffer.ColorTextureIds[0];
                return _lastTextureId;
            }
            catch (Exception exception)
            {
                return Skip(scene, mode, width, height, exception.Message, verboseLogs, out skipReason);
            }
            finally
            {
                shader?.Stop();
                render.CurrentFrameBuffer = restoreFrameBuffer;
                render.GlViewport(restoreViewport[0], restoreViewport[1], restoreViewport[2], restoreViewport[3]);
                previous?.Use();
                GL.FrontFace((FrontFaceDirection)restoreFrontFace);
                GL.CullFace((TriangleFace)restoreCullFaceMode);
                if (restoreCullFace) render.GlEnableCullFace();
                else render.GlDisableCullFace();
                GL.ClearDepth(restoreDepthClearValue);
                GL.ClearColor(restoreClearColor[0], restoreClearColor[1], restoreClearColor[2], restoreClearColor[3]);
                GL.ColorMask(restoreColorMask[0], restoreColorMask[1], restoreColorMask[2], restoreColorMask[3]);
                GL.DepthFunc((DepthFunction)restoreDepthFunc);
                render.GLDepthMask(restoreDepthMask);
                if (restoreBlend) render.GlToggleBlend(true, EnumBlendMode.Standard);
                else render.GlToggleBlend(false);
                if (restoreDepthTest) render.GLEnableDepthTest();
                else GL.Disable(EnableCap.DepthTest);
            }
        }

        private void RenderFirstPersonHeldItem(
            VanillaAnimationPreviewScene scene,
            VanillaPreviewCameraState camera,
            VanillaPreviewMode mode,
            ModSystemFpHands? fpHands,
            bool worldLighting,
            string itemCode,
            bool rightHand)
        {
            if (mode != VanillaPreviewMode.FirstPerson && mode != VanillaPreviewMode.ImmersiveFirstPerson) return;
            if (string.IsNullOrWhiteSpace(itemCode)) return;
            if (!VanillaAnimationPreviewScene.TryBuildFirstPersonItemStack(_api, itemCode, out ItemStack? stack, out _) || stack == null) return;

            AttachmentPointAndPose? apap = scene.Animator.GetAttachmentPointPose(rightHand ? "RightHand" : "LeftHand");
            AttachmentPoint? attachPoint = apap?.AttachPoint;
            if (apap == null || attachPoint == null) return;

            DummySlot slot = new(stack);
            ItemRenderInfo renderInfo = _api.Render.GetItemStackRenderInfo(slot, (EnumItemRenderTarget)(rightHand ? 2 : 3), 0f);
            if (renderInfo?.ModelRef == null || renderInfo.Transform == null) return;

            ModelTransform transform = renderInfo.Transform.EnsureDefaultValues();
            Matrixf itemModel = new();
            itemModel.Set(camera.Model.Values)
                .Mul(apap.AnimModelMatrix)
                .Translate(transform.Origin.X, transform.Origin.Y, transform.Origin.Z)
                .Scale(transform.ScaleXYZ.X, transform.ScaleXYZ.Y, transform.ScaleXYZ.Z)
                .Translate(
                    attachPoint.PosX / 16.0 + transform.Translation.X,
                    attachPoint.PosY / 16.0 + transform.Translation.Y,
                    attachPoint.PosZ / 16.0 + transform.Translation.Z)
                .Rotate(
                    (float)((attachPoint.RotationX + transform.Rotation.X) * GameMath.DEG2RAD),
                    (float)((attachPoint.RotationY + transform.Rotation.Y) * GameMath.DEG2RAD),
                    (float)((attachPoint.RotationZ + transform.Rotation.Z) * GameMath.DEG2RAD))
                .Translate(-transform.Origin.X, -transform.Origin.Y, -transform.Origin.Z);

            IRenderAPI render = _api.Render;
            IShaderProgram? previous = render.CurrentActiveShader;
            IShaderProgram? itemShader = mode == VanillaPreviewMode.FirstPerson && fpHands?.fpModeItemShader != null
                ? fpHands.fpModeItemShader
                : render.GetEngineShader(EnumShaderProgram.Standard);
            if (itemShader == null) return;

            try
            {
                previous?.Stop();
                itemShader.Use();
                SetUniform(itemShader, "depthOffset", mode == VanillaPreviewMode.FirstPerson ? GetFirstPersonDepthOffset(mode, fpHands) : 0f);
                SetUniform(itemShader, "ssaoAttn", 1f);
                SetUniform(itemShader, "dontWarpVertices", 2);
                SetUniform(itemShader, "addRenderFlags", 0);
                SetUniform(itemShader, "normalShaded", renderInfo.NormalShaded ? 1 : 0);
                SetUniform(itemShader, "tempGlowMode", stack.ItemAttributes?["tempGlowMode"].AsInt(0) ?? 0);
                SetUniform(itemShader, "rgbaTint", ColorUtil.WhiteArgbVec);
                SetUniform(itemShader, "renderColor", ColorUtil.WhiteArgbVec);
                SetUniform(itemShader, "alphaTest", renderInfo.AlphaTest);
                SetUniform(itemShader, "damageEffect", renderInfo.DamageEffect);
                SetUniform(itemShader, "overlayOpacity", renderInfo.OverlayOpacity);
                SetUniform(itemShader, "rgbaAmbientIn", worldLighting ? render.AmbientColor : new Vec3f(1f, 1f, 1f));
                SetUniform(itemShader, "rgbaLightIn", GetPreviewLight(render, worldLighting));
                SetUniform(itemShader, "rgbaFogIn", worldLighting ? render.FogColor : new Vec4f(0f, 0f, 0f, 0f));
                SetUniform(itemShader, "fogMinIn", worldLighting ? render.FogMin : 0f);
                SetUniform(itemShader, "fogDensityIn", worldLighting ? render.FogDensity : 0f);
                SetUniform(itemShader, "rgbaGlowIn", 1f, 1f, 1f, 0f);
                SetUniform(itemShader, "extraGlow", 0);
                SetUniform(itemShader, "averageColor", 1f, 1f, 1f, 1f);
                SetUniformMatrix(itemShader, "projectionMatrix", camera.Projection.Values);
                SetUniformMatrix(itemShader, "viewMatrix", camera.View.Values);
                SetUniformMatrix(itemShader, "modelMatrix", itemModel.Values);

                bool restoreCull = GL.IsEnabled(EnableCap.CullFace);
                if (renderInfo.CullFaces)
                {
                    render.GlEnableCullFace();
                }
                else
                {
                    render.GlDisableCullFace();
                }

                render.RenderMultiTextureMesh(renderInfo.ModelRef, "tex", 0);

                if (restoreCull)
                {
                    render.GlEnableCullFace();
                }
                else
                {
                    render.GlDisableCullFace();
                }

                SetUniform(itemShader, "damageEffect", 0f);
                SetUniform(itemShader, "tempGlowMode", 0);
            }
            finally
            {
                itemShader.Stop();
                previous?.Use();
            }
        }

        private Vec4f GetPreviewLight(IRenderAPI render, bool worldLighting)
        {
            if (!worldLighting)
            {
                return new Vec4f(1f, 1f, 1f, 1f);
            }

            BlockPos lightPos = _api.World.Player?.Entity?.Pos?.AsBlockPos ?? new BlockPos(0, 0, 0);
            return _api.World.BlockAccessor.GetLightRGBs(lightPos.X, lightPos.Y, lightPos.Z);
        }

        private float GetFirstPersonDepthOffset(VanillaPreviewMode mode, ModSystemFpHands? fpHands)
        {
            if (mode != VanillaPreviewMode.FirstPerson) return 0f;

            if (fpHands != null)
            {
                return PlayerRenderingPatches.GetNetOffset();
            }

            int fieldOfView = Math.Max(1, _api.Settings.Int["fieldOfView"]);
            return PlayerRenderingPatches.FpHandsOffset - GameMath.Max(0f, fieldOfView / 90f - 1f) / 2f;
        }

        private FrameBufferRef EnsureFrameBuffer(int width, int height)
        {
            if (_frameBuffer != null && !_frameBuffer.Disposed && _frameBuffer.Width == width && _frameBuffer.Height == height)
            {
                return _frameBuffer;
            }

            DestroyFrameBuffer();
            FramebufferAttrs attrs = new("ingamedevtools-vanilla-preview", width, height)
            {
                Attachments =
                [
                    new FramebufferAttrsAttachment
                    {
                        AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                        Texture = new RawTexture
                        {
                            Width = width,
                            Height = height,
                            PixelFormat = EnumTexturePixelFormat.Rgba,
                            PixelInternalFormat = EnumTextureInternalFormat.Rgba8
                        }
                    },
                    new FramebufferAttrsAttachment
                    {
                        AttachmentType = EnumFramebufferAttachment.DepthAttachment,
                        Texture = new RawTexture
                        {
                            Width = width,
                            Height = height,
                            PixelFormat = EnumTexturePixelFormat.DepthComponent,
                            PixelInternalFormat = EnumTextureInternalFormat.DepthComponent32
                        }
                    }
                ]
            };
            _frameBuffer = _api.Render.CreateFrameBuffer(attrs);
            return _frameBuffer;
        }

        private void DestroyFrameBuffer()
        {
            if (_frameBuffer != null && !_frameBuffer.Disposed)
            {
                _api.Render.DestroyFrameBuffer(_frameBuffer);
            }

            _frameBuffer = null;
            ClearRenderCache();
        }

        private void ClearRenderCache()
        {
            _lastRenderKey = null;
            _lastTextureId = 0;
        }

        private int Skip(VanillaAnimationPreviewScene scene, VanillaPreviewMode mode, float width, float height, string reason, bool verboseLogs, out string skipReason)
        {
            skipReason = reason;
            if (!verboseLogs) return 0;

            long now = _api.World.ElapsedMilliseconds;
            string key = $"{scene.Key}|{mode}|{(int)width}x{(int)height}|{reason}";
            if (key == _lastSkipLogKey && now - _lastSkipLogAtMs < 1000) return 0;

            _lastSkipLogKey = key;
            _lastSkipLogAtMs = now;
            _api.Logger.VerboseDebug("[InGameDevTools] Vanilla preview skipped: scene={0}, mode={1}, size={2:0}x{3:0}, reason={4}", scene.Key, mode, width, height, reason);
            return 0;
        }

        private void LogVerboseScene(VanillaAnimationPreviewScene scene, VanillaPreviewMode mode, MultiTextureMeshRef meshRef, bool verboseLogs)
        {
            if (!verboseLogs) return;

            string key = $"{scene.Key}|{mode}|scene";
            if (key == _lastSceneLogKey) return;

            _lastSceneLogKey = key;
            _api.Logger.VerboseDebug(
                "[InGameDevTools] Vanilla preview scene: scene={0}, display={1}, mode={2}, status='{3}', meshParts={4}, vertices={5}, indices={6}, textureIds=[{7}], animatorMaxJoint={8}, matrixFloats={9}",
                scene.Key,
                scene.DisplayName,
                mode,
                scene.Status,
                meshRef.meshrefs?.Length ?? 0,
                scene.MeshVerticesCount,
                scene.MeshIndicesCount,
                TextureIdsForLog(meshRef),
                scene.Animator.MaxJointId,
                scene.Animator.Matrices?.Length ?? 0);
        }

        private void LogVerboseFrame(
            VanillaAnimationPreviewScene scene,
            VanillaPreviewMode mode,
            MultiTextureMeshRef meshRef,
            FrameBufferRef frameBuffer,
            int width,
            int height,
            string shaderName,
            FramebufferErrorCode frameBufferStatus,
            ErrorCode glError,
            bool verboseLogs)
        {
            if (!verboseLogs) return;

            string key = $"{scene.Key}|{mode}|{width}x{height}|{shaderName}|frame";
            if (key == _lastFrameLogKey) return;

            _lastFrameLogKey = key;
            _api.Logger.VerboseDebug(
                "[InGameDevTools] Vanilla preview frame: scene={0}, mode={1}, size={2}x{3}, framebuffer={4}, colorTextures=[{5}], depthTexture={6}, shader={7}, meshParts={8}, textureIds=[{9}], animatorMaxJoint={10}, matrixFloats={11}, glError={12}",
                scene.Key,
                mode,
                width,
                height,
                frameBufferStatus,
                frameBuffer.ColorTextureIds == null ? "" : string.Join(",", frameBuffer.ColorTextureIds),
                frameBuffer.DepthTextureId,
                shaderName,
                meshRef.meshrefs?.Length ?? 0,
                TextureIdsForLog(meshRef),
                scene.Animator.MaxJointId,
                scene.Animator.Matrices?.Length ?? 0,
                glError);
        }

        private static string TextureIdsForLog(MultiTextureMeshRef meshRef)
        {
            int[]? textureIds = meshRef.textureids;
            if (textureIds == null || textureIds.Length == 0) return "<none>";

            const int max = 10;
            string result = string.Join(",", textureIds.Take(max));
            return textureIds.Length > max ? $"{result},+{textureIds.Length - max}" : result;
        }

        public void Dispose()
        {
            DestroyFrameBuffer();
        }

        private static void SetTexture(IShaderProgram shader, string name, int textureId, int textureNumber)
        {
            if (shader.HasUniform(name))
            {
                shader.BindTexture2D(name, textureId, textureNumber);
            }
        }

        private static void SetUniform(IShaderProgram shader, string name, int value)
        {
            if (shader.HasUniform(name))
            {
                shader.Uniform(name, value);
            }
        }

        private static void SetUniform(IShaderProgram shader, string name, float value)
        {
            if (shader.HasUniform(name))
            {
                shader.Uniform(name, value);
            }
        }

        private static void SetUniform(IShaderProgram shader, string name, Vec3f value)
        {
            if (shader.HasUniform(name))
            {
                shader.Uniform(name, value);
            }
        }

        private static void SetUniform(IShaderProgram shader, string name, Vec4f value)
        {
            if (shader.HasUniform(name))
            {
                shader.Uniform(name, value);
            }
        }

        private static void SetUniform(IShaderProgram shader, string name, float valueX, float valueY, float valueZ)
        {
            if (shader.HasUniform(name))
            {
                shader.Uniform(name, valueX, valueY, valueZ);
            }
        }

        private static void SetUniform(IShaderProgram shader, string name, float valueX, float valueY, float valueZ, float valueW)
        {
            if (shader.HasUniform(name))
            {
                shader.Uniform(name, valueX, valueY, valueZ, valueW);
            }
        }

        private static void SetUniformMatrix(IShaderProgram shader, string name, float[] matrix)
        {
            if (shader.HasUniform(name))
            {
                shader.UniformMatrix(name, matrix);
            }
        }
    }

    private static class VanillaAnimationDocumentSerializer
    {
        public static string Serialize(VanillaAnimationDocument document)
        {
            JObject token = new()
            {
                ["kind"] = document.Kind.ToString(),
                ["domain"] = document.Domain,
                ["assetPath"] = document.AssetPath
            };

            if (document.ShapeAnimations.Count > 0)
            {
                token["animations"] = new JArray(document.ShapeAnimations.Select(entry =>
                    VanillaAnimationExportService.ToVanillaAnimationToken(entry.Animation, null)));
            }

            if (document.MetadataEntries.Count > 0)
            {
                token["metadata"] = new JArray(document.MetadataEntries.Select(entry =>
                    VanillaAnimationExportService.ToAnimationMetaDataToken(entry.Metadata, null)));
            }

            return JsonConvert.SerializeObject(token, Formatting.None);
        }
    }
}
