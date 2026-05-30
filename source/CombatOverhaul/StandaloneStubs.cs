using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CombatOverhaul.Colliders
{
    public readonly struct LineSegmentCollider
    {
        public readonly Vector3d Position;
        public readonly Vector3d Direction;

        public LineSegmentCollider(Vector3d position, Vector3d direction)
        {
            Position = position;
            Direction = direction;
        }

        public LineSegmentCollider(Vector3 position, Vector3 direction)
        {
            Position = new Vector3d(position.X, position.Y, position.Z);
            Direction = new Vector3d(direction.X, direction.Y, direction.Z);
        }

        public LineSegmentCollider(params float[] positions)
        {
            Position = new Vector3d(positions[0], positions[1], positions[2]);
            Direction = new Vector3d(positions[3], positions[4], positions[5]) - Position;
        }
    }

    public sealed class CollidersEntityBehavior : EntityBehavior
    {
        public static bool RenderColliders { get; set; }

        public CollidersEntityBehavior(Entity entity) : base(entity)
        {
        }

        public override string PropertyName() => "CombatOverhaul:EntityColliders";

        public void Render(ICoreClientAPI? api, EntityAgent? entityAgent, EntityShapeRenderer renderer)
        {
        }
    }
}

namespace CombatOverhaul.MeleeSystems
{
    using CombatOverhaul.Colliders;

    public sealed class MeleeDamageType
    {
        public LineSegmentCollider RelativeCollider { get; set; }
    }
}

namespace CombatOverhaul.Animations
{
    public sealed class Animatable : CollectibleBehavior
    {
        public Animatable(CollectibleObject collObj) : base(collObj)
        {
        }

        public Shape? CurrentShape { get; set; }
    }

    public sealed class AnimatableAttachable : CollectibleBehavior
    {
        public AnimatableAttachable(CollectibleObject collObj) : base(collObj)
        {
        }

        public void SetSwitchModels(long entityId, bool switchModels)
        {
        }
    }

    public sealed class SoundsSynchronizerClient
    {
        public void Play(SoundFrame frame)
        {
        }

        public void Play(string code, bool randomizedPitch = false, float range = 32, float volume = 1, bool synchronize = true)
        {
        }
    }
}

namespace CombatOverhaul.Utils
{
    public abstract class GenericDisplayProto : BlockEntity
    {
        public virtual string AttributeTransformCode => "onDisplayTransform";
        public readonly Dictionary<int, ModelTransform> EditedTransforms = new();

        public virtual void RegenerateMeshes()
        {
            MarkDirty(true);
        }
    }
}
