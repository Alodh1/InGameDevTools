using InGameDevTools.Utils;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using VanillaAnimation = Vintagestory.API.Common.Animation;

namespace InGameDevTools.Animations;

public sealed class InGameDevToolsAnimatedBlockEntity : BlockEntity
{
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        return GetBehavior<InGameDevToolsAnimatedBlockBehavior>()?.SkipDefaultMesh == true ||
            base.OnTesselation(mesher, tessThreadTesselator);
    }
}

public sealed class InGameDevToolsAnimatedBlockBehavior : BlockEntityBehavior
{
    private readonly Dictionary<string, AnimationMetaData> _activeAnimations = new(StringComparer.OrdinalIgnoreCase);
    private ICoreClientAPI? _capi;
    private DevToolsAnimatedBlockConfig _config;
    private AnimatableRenderer? _renderer;
    private bool _skipDefaultMesh;
    private bool _unloaded;

    public InGameDevToolsAnimatedBlockBehavior(BlockEntity blockentity) : base(blockentity)
    {
    }

    public bool SkipDefaultMesh => _skipDefaultMesh;

    public override void Initialize(ICoreAPI api, JsonObject properties)
    {
        base.Initialize(api, properties);
        _config = DevToolsBlockAnimationSetup.ParseConfig(properties);
        if (api is not ICoreClientAPI capi || !_config.IsValid)
        {
            return;
        }

        _capi = capi;
        _unloaded = false;
        capi.Event.EnqueueMainThreadTask(InitializeRenderer, "ingamedevtools-animated-block-init");
    }

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        return _skipDefaultMesh || base.OnTesselation(mesher, tessThreadTesselator);
    }

    public override void OnBlockRemoved()
    {
        _unloaded = true;
        DisposeRenderer();
        _capi = null;
        base.OnBlockRemoved();
    }

    public override void OnBlockUnloaded()
    {
        _unloaded = true;
        DisposeRenderer();
        _capi = null;
        base.OnBlockUnloaded();
    }

    private void InitializeRenderer()
    {
        if (_unloaded || _capi == null || Block == null || Block.Shape?.Base == null || !_config.IsValid)
        {
            return;
        }

        try
        {
            DisposeRenderer();

            AssetLocation shapeLocation = Block.Shape.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
            Shape sourceShape = Shape.TryGet(_capi, shapeLocation)
                ?? throw new InvalidOperationException($"Could not load shape {shapeLocation}.");
            Shape shape = sourceShape.Clone() ?? sourceShape;
            VanillaAnimation animation = ResolveAnimation(shape, _config.AnimationCode);
            PrepareShapeAnimation(_capi, shape, animation, shapeLocation.ToString(), _config.Loop);

            ITexPositionSource texSource = _capi.Tesselator.GetTextureSource(Block, 0, false)
                ?? throw new InvalidOperationException($"Could not resolve block texture source for {Block.Code}.");
            CompositeShape compositeShape = Block.Shape;
            TesselationMetaData meta = new()
            {
                TexSource = texSource,
                WithJointIds = true,
                TypeForLogging = Block.Code?.ToString() ?? shapeLocation.ToString(),
                QuantityElements = compositeShape.QuantityElements,
                SelectiveElements = compositeShape.SelectiveElements,
                IgnoreElements = compositeShape.IgnoreElements,
                Rotation = new Vec3f(compositeShape.rotateX, compositeShape.rotateY, compositeShape.rotateZ)
            };

            _capi.Tesselator.TesselateShape(meta, shape, out MeshData mesh);
            if (mesh == null || mesh.VerticesCount <= 0 || mesh.IndicesCount <= 0)
            {
                throw new InvalidOperationException($"Tessellation for {shapeLocation} produced no renderable mesh.");
            }

            if (compositeShape.offsetX != 0 || compositeShape.offsetY != 0 || compositeShape.offsetZ != 0)
            {
                mesh.Translate(compositeShape.offsetX, compositeShape.offsetY, compositeShape.offsetZ);
            }

            ClientAnimator animator = new(() => 1, [animation], shape.Elements, shape.JointsById, null, null);
            AnimationMetaData metadata = new()
            {
                Code = _config.AnimationCode,
                Animation = _config.AnimationCode,
                AnimationSpeed = _config.Speed,
                Weight = 1f,
                BlendMode = EnumAnimationBlendMode.Average,
                EaseInSpeed = 10f,
                EaseOutSpeed = 10f,
                ClientSide = true
            };
            metadata.Init();

            _activeAnimations.Clear();
            _activeAnimations[_config.AnimationCode] = metadata;
            Vec3d pos = new(Pos.X, Pos.Y, Pos.Z);
            _renderer = new AnimatableRenderer(_capi, pos, new Vec3f(), animator, _activeAnimations, mesh, EnumRenderStage.Opaque);
            _skipDefaultMesh = true;
            Blockentity.MarkDirty(true);
        }
        catch (Exception exception)
        {
            DisposeRenderer();
            _skipDefaultMesh = false;
            _capi.Logger.Warning("InGameDevTools animated block setup failed for {0} at {1}: {2}", Block?.Code, Pos, exception);
        }
    }

    private void DisposeRenderer()
    {
        if (_renderer == null)
        {
            _skipDefaultMesh = false;
            return;
        }

        if (_renderer is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _renderer = null;
        _skipDefaultMesh = false;
    }

    private static VanillaAnimation ResolveAnimation(Shape shape, string animationCode)
    {
        VanillaAnimation? animation = (shape.Animations ?? []).FirstOrDefault(entry =>
            string.Equals(entry.Code ?? entry.Name, animationCode, StringComparison.OrdinalIgnoreCase));
        if (animation == null)
        {
            throw new InvalidOperationException($"Shape has no animation '{animationCode}'.");
        }

        return animation;
    }

    private static void PrepareShapeAnimation(ICoreClientAPI api, Shape shape, VanillaAnimation animation, string shapeName, bool loop)
    {
        shape.Animations = [animation];
        shape.JointsById ??= [];
        shape.JointsById.Clear();
        Dictionary<string, ShapeElement> elementsByName = shape.CollectAndResolveReferences(api.World.Logger, shapeName)
            ?? throw new InvalidOperationException($"Shape '{shapeName}' reference resolution returned no elements.");
        if (elementsByName.Count == 0 || shape.Elements == null || shape.Elements.Length == 0)
        {
            throw new InvalidOperationException($"Shape '{shapeName}' has no resolved elements.");
        }

        shape.CacheInvTransforms();
        shape.ResolveAndFindJoints(api.World.Logger, shapeName, elementsByName);

        if (string.IsNullOrWhiteSpace(animation.Code)) animation.Code = animation.Name;
        animation.OnAnimationEnd = loop ? EnumEntityAnimationEndHandling.Repeat : EnumEntityAnimationEndHandling.Hold;
        foreach (AnimationKeyFrame keyFrame in animation.KeyFrames ?? [])
        {
            if (keyFrame.Elements == null) continue;
            foreach (AnimationKeyFrameElement element in keyFrame.Elements.Values)
            {
                CompleteElementTransformGroups(element);
            }
        }

        animation.GenerateAllFrames(shape.Elements, shape.JointsById);
    }

    private static void CompleteElementTransformGroups(AnimationKeyFrameElement element)
    {
        if (element.PositionSet)
        {
            element.OffsetX ??= 0;
            element.OffsetY ??= 0;
            element.OffsetZ ??= 0;
        }

        if (element.RotationSet)
        {
            element.RotationX ??= 0;
            element.RotationY ??= 0;
            element.RotationZ ??= 0;
        }

        if (element.StretchSet)
        {
            element.StretchX ??= 1;
            element.StretchY ??= 1;
            element.StretchZ ??= 1;
        }
    }
}
