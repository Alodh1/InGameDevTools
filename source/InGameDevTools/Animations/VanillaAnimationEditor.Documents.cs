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
    private sealed class VanillaAnimationDocument
    {
        public VanillaDocumentKind Kind { get; init; }
        public string Domain { get; init; } = "game";
        public string AssetPath { get; init; } = "";
        public string DisplayPath { get; init; } = "";
        public string? EntityCode { get; init; }
        public EntityProperties? EntityType { get; init; }
        public Shape? Shape { get; init; }
        public JObject? SourceJson { get; init; }
        public VanillaPlayerModelSource? PlayerModelSource { get; init; }
        public string GroupLabel { get; init; } = "";
        public IReadOnlyList<EntityProperties> RuntimeTargetEntities { get; init; } = [];
        public bool UseEntityTypeAsRuntimeFallback { get; init; } = true;
        public int RuntimeSkippedMembers { get; init; }
        public string RuntimeGroupKind { get; init; } = "";
        public List<VanillaShapeAnimationEntry> ShapeAnimations { get; } = [];
        public List<VanillaAnimationMetaEntry> MetadataEntries { get; } = [];
        public string HistoryKey => $"{Kind}:{Domain}:{AssetPath}:{EntityCode}";
        public bool Dirty { get; private set; }
        private string _cleanSerialized = "";

        public void MarkClean()
        {
            _cleanSerialized = VanillaAnimationDocumentSerializer.Serialize(this);
            Dirty = false;
        }

        public void MarkDirty()
        {
            Dirty = true;
        }

        public void UpdateDirtyState()
        {
            Dirty = !string.Equals(_cleanSerialized, VanillaAnimationDocumentSerializer.Serialize(this), StringComparison.Ordinal);
        }
    }

    private sealed class VanillaShapeAnimationEntry
    {
        public VanillaShapeAnimationEntry(VanillaAnimationDocument document, int index, VanillaAnimation animation, JToken? sourceToken)
        {
            Document = document;
            Index = index;
            Animation = animation;
            SourceToken = sourceToken;
        }

        public VanillaAnimationDocument Document { get; }
        public int Index { get; }
        public VanillaAnimation Animation { get; }
        public JToken? SourceToken { get; set; }
    }

    private sealed class VanillaAnimationMetaEntry
    {
        public VanillaAnimationMetaEntry(VanillaAnimationDocument document, int index, AnimationMetaData metadata, VanillaShapeAnimationEntry? linkedShape, JToken? sourceToken)
        {
            Document = document;
            Index = index;
            Metadata = metadata;
            LinkedShape = linkedShape;
            SourceToken = sourceToken;
        }

        public VanillaAnimationDocument Document { get; }
        public int Index { get; }
        public AnimationMetaData Metadata { get; }
        public VanillaShapeAnimationEntry? LinkedShape { get; set; }
        public JToken? SourceToken { get; set; }

        public VanillaShapeAnimationEntry? ResolveCurrentShape()
        {
            if (LinkedShape != null)
            {
                string linkedCode = LinkedShape.Animation.Code ?? LinkedShape.Animation.Name ?? "";
                if (string.Equals(linkedCode, Metadata.Animation, StringComparison.OrdinalIgnoreCase))
                {
                    return LinkedShape;
                }
            }

            if (Document.Shape?.Animations != null)
            {
                for (int index = 0; index < Document.Shape.Animations.Length; index++)
                {
                    VanillaAnimation animation = Document.Shape.Animations[index];
                    string code = animation.Code ?? animation.Name ?? "";
                    if (string.Equals(code, Metadata.Animation, StringComparison.OrdinalIgnoreCase))
                    {
                        return new VanillaShapeAnimationEntry(Document, index, animation, null);
                    }
                }
            }

            return LinkedShape;
        }
    }

    private enum VanillaDocumentKind
    {
        Shape,
        EntityMetadata
    }
}
