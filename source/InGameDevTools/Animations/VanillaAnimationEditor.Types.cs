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
    private sealed class VanillaAnimationSelection
    {
        public string RowKey = "";
        public int KeyFrameIndex;
        public string ElementName = "";
        public int AddElementIndex;
        public int LoopStartFrame;
        public int LoopEndFrame;
        public string NewElementWeightKey = "";
        public float NewElementWeightValue = 1f;
        public string NewElementBlendKey = "";
        public EnumAnimationBlendMode NewElementBlendMode = EnumAnimationBlendMode.Add;

        public void Clear()
        {
            RowKey = "";
            KeyFrameIndex = 0;
            ElementName = "";
            AddElementIndex = 0;
            LoopStartFrame = 0;
            LoopEndFrame = 0;
        }
    }

    private sealed record VanillaBrowserRow(
        string Key,
        string Label,
        string FullLabel,
        VanillaAnimationDocument Document,
        VanillaShapeAnimationEntry? ShapeAnimation,
        VanillaAnimationMetaEntry? MetadataEntry,
        VanillaBrowserRowKind Kind,
        string SearchText,
        bool IsUnresolved);

    private enum VanillaBrowserRowKind
    {
        Metadata,
        Shape
    }

    private enum VanillaBrowserQuickFilter
    {
        All,
        Metadata,
        Shape,
        Dirty,
        Unresolved
    }

    private enum VanillaPreviewMode
    {
        Orbit,
        FirstPerson,
        ImmersiveFirstPerson
    }

    private enum VanillaSymmetrySide
    {
        Unknown,
        Left,
        Right
    }

    private enum VanillaLiveSymmetryMode
    {
        InPlace,
        HalfCycle
    }

    private enum VanillaLiveSymmetryDriver
    {
        SelectedElement,
        LeftDrivesRight,
        RightDrivesLeft
    }

    private enum VanillaIkChainMode
    {
        AutoConservative,
        AutoExtended,
        ManualOverride
    }

    private enum VanillaIkSolverKind
    {
        Ccd,
        Fabrik
    }

    private enum VanillaIkEffectorMode
    {
        DistalEndpoint,
        GizmoHandle
    }

    private enum VanillaPlayerModelAnimationMode
    {
        OwnShape,
        BorrowedFallback
    }

    private readonly record struct VanillaSymmetryPairCandidate(string ElementName, VanillaSymmetrySide SourceSide);
    private readonly record struct VanillaSymmetryResult(bool Applied, int Written, int CreatedKeyFrames, int OverwrittenElements, string Message, IReadOnlyList<string>? WrittenElements = null);
    private readonly record struct VanillaCutApplyResult(string OriginalName, string NewName, int Axis, double Coordinate, double CutRatio, int RegisteredChannels, bool InsertedManualIk);
    private readonly record struct VanillaIkManualChain(IReadOnlyList<string> ElementNames, string EndElementName, string DisplayName);
    private readonly record struct VanillaIkChainNode(string ElementName, string ParentElementName, int Depth);
    private readonly record struct VanillaIkPoseInfo(ElementPose Pose, Vec3d Origin, RigIkMatrix3 WorldRotation, RigIkMatrix3 ParentWorldRotation, Vec3d BaseRotationDegrees);

    private sealed record VanillaIkCcdCache(
        VanillaIkManualChain Chain,
        IReadOnlyList<VanillaIkPoseInfo> BoneInfos,
        Vec3d[] JointPositions,
        Vec3d EndOrigin,
        TransformGizmoAxes SelectedAxes,
        AnimationKeyFrameElement SelectedStartElement,
        IReadOnlyList<AnimationKeyFrameElement> StartElements);

    private enum VanillaEntitySelectorMode
    {
        Grouped,
        Exact
    }

    private enum VanillaAnimationSourceMode
    {
        Entities,
        Blocks,
        OverhaulLib
    }

    private sealed record VanillaEntityOption(
        IReadOnlyList<VanillaEntityMember> Members,
        string Label,
        string Tooltip,
        string Domain,
        string SearchText,
        string GroupKey,
        string GroupKind,
        int HiddenCount)
    {
        public VanillaEntityMember Representative => Members.Count > 0 ? Members[0] : throw new InvalidOperationException("Entity option has no members.");
    }

    private sealed record VanillaEntityMember(
        EntityProperties EntityType,
        string Label,
        string FullLabel,
        string Domain,
        VanillaEntitySourceInfo? Source,
        string MetadataSignature,
        string ShapeSignature,
        bool Hidden,
        string HiddenReason,
        VanillaPlayerModelSource? PlayerModel = null)
    {
        public string Code => PlayerModel?.FullCode ?? EntityType.Code?.ToString() ?? FullLabel;
    }

    private sealed record VanillaEntitySourceInfo(
        AssetLocation Location,
        string AssetPath,
        string SourceCode,
        JObject? SourceJson,
        bool HasVariantGroups,
        bool Hidden,
        string HiddenReason)
    {
        public string Key => $"{Location.Domain}:{AssetPath}";
    }

    private sealed record VanillaPlayerModelSource(
        string Code,
        AssetLocation ConfigLocation,
        string ConfigAssetPath,
        JObject ConfigSourceJson,
        AssetLocation ShapeLocation,
        JObject? ShapeSourceJson,
        Shape Shape,
        AssetLocation AnimationAssetLocation,
        JObject? AnimationSourceJson,
        EntityProperties AnimationEntityType,
        string AnimationSourceCode,
        int MatchedElementCount,
        VanillaPlayerModelAnimationMode AnimationMode,
        IReadOnlyList<string> OwnAnimationCodes)
    {
        public string Domain => ConfigLocation.Domain;
        public string FullCode => Code.Contains(':', StringComparison.Ordinal) ? Code : $"{Domain}:{Code}";
        public bool UsesOwnAnimations => AnimationMode == VanillaPlayerModelAnimationMode.OwnShape;
    }

    private sealed record VanillaBlockOption(
        Block Block,
        string Label,
        string FullLabel,
        string Domain,
        string SearchText,
        VanillaBlockSourceInfo? Source)
    {
        public string Code => Block.Code?.ToString() ?? FullLabel;
    }

    private sealed record VanillaBlockSourceInfo(
        AssetLocation Location,
        string AssetPath,
        JObject? SourceJson)
    {
        public string Key => $"{Location.Domain}:{AssetPath}";
    }

    private sealed record VanillaGroupTargets(IReadOnlyList<EntityProperties> Targets, int Skipped);
}
