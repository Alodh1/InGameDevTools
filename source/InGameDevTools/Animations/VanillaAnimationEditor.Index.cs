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
    private sealed class VanillaAnimationIndexService
    {
        private readonly List<VanillaAnimationDocument> _documents = [];
        private readonly Dictionary<string, List<VanillaShapeAnimationEntry>> _shapeAnimationsByCode = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<VanillaEntityOption> _groupedEntityOptions = [];
        private readonly List<VanillaEntityOption> _groupedVisibleEntityOptions = [];
        private readonly List<VanillaEntityOption> _exactEntityOptions = [];
        private readonly List<VanillaEntityOption> _exactVisibleEntityOptions = [];
        private readonly List<VanillaBlockOption> _blockOptions = [];
        private readonly List<string> _allEntityDomains = [];
        private readonly List<string> _allBlockDomains = [];
        private bool _entityListReady;
        private bool _blockListReady;

        public IReadOnlyList<VanillaAnimationDocument> Documents => _documents;
        public IEnumerable<string> AllEntityDomains => _allEntityDomains;
        public IEnumerable<string> AllBlockDomains => _allBlockDomains;
        public VanillaEntityOption? SelectedEntityOption { get; private set; }
        public VanillaBlockOption? SelectedBlockOption { get; private set; }
        public int SelectedMemberIndex { get; private set; } = -1;
        public string? SelectedShapeKey { get; private set; }
        public string? SelectedShapeLabel { get; private set; }
        public string? SelectedEntityLabel => SelectedEntityOption?.Label;
        public string? SelectedBlockLabel => SelectedBlockOption?.Label;
        public bool HasSelectedEntity => SelectedEntityOption != null && SelectedMemberIndex >= 0;
        public bool HasSelectedBlock => SelectedBlockOption != null;
        public bool HasSelectedShape => SelectedShapeKey != null;

        public bool IsSelectedShape(string key)
        {
            return SelectedShapeKey != null && string.Equals(SelectedShapeKey, key, StringComparison.OrdinalIgnoreCase);
        }
        public string Status { get; private set; } = "Select an entity to index its vanilla animations.";

        public IReadOnlyList<VanillaEntityOption> GetEntityOptions(VanillaEntitySelectorMode mode, bool showHidden)
        {
            return mode switch
            {
                VanillaEntitySelectorMode.Exact => showHidden ? _exactEntityOptions : _exactVisibleEntityOptions,
                _ => showHidden ? _groupedEntityOptions : _groupedVisibleEntityOptions
            };
        }

        public bool IsSelectedEntityOption(VanillaEntityOption option)
        {
            return ReferenceEquals(option, SelectedEntityOption);
        }

        public IReadOnlyList<VanillaBlockOption> GetBlockOptions()
        {
            return _blockOptions;
        }

        public bool IsSelectedBlockOption(VanillaBlockOption option)
        {
            return ReferenceEquals(option, SelectedBlockOption);
        }

        public void EnsureEntityList(ICoreClientAPI api)
        {
            if (_entityListReady) return;

            _groupedEntityOptions.Clear();
            _groupedVisibleEntityOptions.Clear();
            _exactEntityOptions.Clear();
            _exactVisibleEntityOptions.Clear();
            _allEntityDomains.Clear();

            VanillaEntitySourceIndex sourceIndex = VanillaEntitySourceIndex.Build(api);
            List<VanillaEntityMember> members = [];
            foreach (EntityProperties entityType in api.World.EntityTypes ?? [])
            {
                string? code = entityType.Code?.ToString();
                if (string.IsNullOrWhiteSpace(code)) continue;
                string domain = entityType.Code?.Domain ?? "game";
                VanillaEntitySourceInfo? source = sourceIndex.Resolve(entityType);
                bool hidden = source?.Hidden == true;
                string hiddenReason = source?.HiddenReason ?? "";
                members.Add(new(
                    entityType,
                    ImGuiLayoutHelper.CompactAssetCode(code),
                    code,
                    domain,
                    source,
                    BuildMetadataCompatibilitySignature(entityType),
                    BuildShapeCompatibilitySignature(entityType),
                    hidden,
                    hiddenReason));
            }

            foreach (VanillaPlayerModelSource playerModel in VanillaPlayerModelSourceIndex.Build(api, members.Select(member => member.EntityType)))
            {
                VanillaEntitySourceInfo source = new(
                    playerModel.ConfigLocation,
                    playerModel.ConfigAssetPath,
                    playerModel.FullCode,
                    playerModel.ConfigSourceJson,
                    HasVariantGroups: false,
                    Hidden: false,
                    HiddenReason: "");
                string label = ImGuiLayoutHelper.CompactAssetCode(playerModel.FullCode);
                members.Add(new(
                    playerModel.AnimationEntityType,
                    label,
                    playerModel.FullCode,
                    playerModel.Domain,
                    source,
                    BuildMetadataCompatibilitySignature(playerModel.AnimationEntityType),
                    BuildPlayerModelShapeCompatibilitySignature(playerModel),
                    Hidden: false,
                    HiddenReason: "",
                    playerModel));
            }

            _allEntityDomains.AddRange(members.Select(member => member.Domain).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase));
            _exactEntityOptions.AddRange(members.Select(member => BuildEntityOption(
                [member],
                member.PlayerModel != null ? "playermodel" : "exact",
                member.PlayerModel != null ? "Playermodelib model" : "Exact runtime entity")));
            _groupedEntityOptions.AddRange(BuildGroupedEntityOptions(members));
            _exactEntityOptions.Sort(CompareEntityOptions);
            _groupedEntityOptions.Sort(CompareEntityOptions);
            _exactVisibleEntityOptions.AddRange(_exactEntityOptions.Where(option => option.Members.Any(member => !member.Hidden)));
            _groupedVisibleEntityOptions.AddRange(BuildVisibleEntityOptions(_groupedEntityOptions));
            _entityListReady = true;
            Status = $"Loaded {members.Count} entity types into {_groupedEntityOptions.Count} group(s). Select one to index its animations.";
        }

        public void EnsureBlockList(ICoreClientAPI api)
        {
            if (_blockListReady) return;

            _blockOptions.Clear();
            _allBlockDomains.Clear();

            foreach (Block block in api.World.Blocks ?? [])
            {
                if (block?.Code == null || block.Id == 0) continue;
                if (block.Shape?.Base == null) continue;

                string code = block.Code.ToString();
                string domain = block.Code.Domain ?? "game";
                VanillaBlockSourceInfo? source = BuildBlockSourceInfo(block);
                string label = ImGuiLayoutHelper.CompactAssetCode(code);
                string assetPath = source?.AssetPath ?? $"blocktypes/{EnsureJsonFilePath(block.Code.Path)}";
                string search = $"{label} {code} {domain} {assetPath} {block.Shape.Base}";
                _blockOptions.Add(new(block, label, code, domain, search, source));
            }

            _blockOptions.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));
            _allBlockDomains.AddRange(_blockOptions.Select(option => option.Domain).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase));
            _blockListReady = true;
        }

        public void SelectEntity(ICoreClientAPI api, IReadOnlyList<VanillaEntityOption> options, int index, int memberIndex, bool groupEdit)
        {
            EnsureEntityList(api);
            if (index < 0 || index >= options.Count)
            {
                ClearSelection();
                return;
            }

            SelectEntity(api, options[index], memberIndex, groupEdit);
        }

        public void SelectEntity(ICoreClientAPI api, VanillaEntityOption option, int memberIndex, bool groupEdit)
        {
            EnsureEntityList(api);
            if (option.Members.Count == 0)
            {
                ClearSelection();
                return;
            }

            SelectedEntityOption = option;
            SelectedMemberIndex = Math.Clamp(memberIndex, 0, option.Members.Count - 1);
            IndexSelectedEntity(api, option, SelectedMemberIndex, groupEdit);
        }

        public void SelectBlock(ICoreClientAPI api, IReadOnlyList<VanillaBlockOption> options, int index)
        {
            EnsureBlockList(api);
            if (index < 0 || index >= options.Count)
            {
                ClearSelection();
                return;
            }

            SelectBlock(api, options[index]);
        }

        public void SelectBlock(ICoreClientAPI api, VanillaBlockOption option)
        {
            EnsureBlockList(api);
            SelectedBlockOption = option;
            SelectedEntityOption = null;
            SelectedMemberIndex = -1;
            IndexSelectedBlock(api, option);
        }

        public bool SelectBlockByCode(ICoreClientAPI api, string code)
        {
            EnsureBlockList(api);
            VanillaBlockOption? option = _blockOptions.FirstOrDefault(entry =>
                string.Equals(entry.Code, code, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Block.Code?.Path, code, StringComparison.OrdinalIgnoreCase));
            if (option == null) return false;
            SelectBlock(api, option);
            return true;
        }

        public void ReloadSelectedEntity(ICoreClientAPI api, bool groupEdit)
        {
            if (!HasSelectedEntity) return;
            IndexSelectedEntity(api, SelectedEntityOption!, SelectedMemberIndex, groupEdit);
        }

        public void ReloadSelectedBlock(ICoreClientAPI api)
        {
            if (!HasSelectedBlock) return;
            IndexSelectedBlock(api, SelectedBlockOption!);
        }

        public void ClearSelection()
        {
            SelectedEntityOption = null;
            SelectedBlockOption = null;
            SelectedMemberIndex = -1;
            SelectedShapeKey = null;
            SelectedShapeLabel = null;
            _documents.Clear();
            _shapeAnimationsByCode.Clear();
            Status = "Select an entity or block to index its vanilla animations.";
        }

        /// <summary>
        /// Loads an already-parsed <see cref="Shape"/> (e.g. an authored model-editor shape or any indexed
        /// shape asset) as the editable shape document, mirroring <see cref="IndexSelectedBlock"/> but without a
        /// backing runtime block. New animations can be added even when the shape ships none.
        /// </summary>
        public void SetShapeDocument(ICoreClientAPI api, Shape shape, string domain, string assetPath, string label, JObject? sourceJson)
        {
            try
            {
                ClearSelection();

                shape.ResolveReferences(api.Logger, label);
                shape.Animations ??= [];
                string normalizedPath = EnsureJsonPath(assetPath);

                VanillaAnimationDocument shapeDocument = new()
                {
                    Kind = VanillaDocumentKind.Shape,
                    Domain = domain,
                    AssetPath = normalizedPath,
                    DisplayPath = label,
                    EntityCode = label,
                    Shape = shape,
                    SourceJson = sourceJson,
                    GroupLabel = label,
                    RuntimeGroupKind = "shape"
                };

                for (int index = 0; index < shape.Animations.Length; index++)
                {
                    VanillaAnimation animation = CloneVanillaAnimation(shape.Animations[index]);
                    if (string.IsNullOrWhiteSpace(animation.Code)) animation.Code = animation.Name;
                    VanillaShapeAnimationEntry entry = new(shapeDocument, index, animation, GetSourceArrayElement(sourceJson, "animations", index));
                    shapeDocument.ShapeAnimations.Add(entry);
                    RegisterShapeAnimation(entry);
                }

                _documents.Add(shapeDocument);
                shapeDocument.MarkClean();
                RebuildLinks();

                SelectedShapeKey = $"{domain}:{normalizedPath}";
                SelectedShapeLabel = label;
                Status = $"Loaded shape {label}: {shapeDocument.ShapeAnimations.Count} animation(s). New animations can be added even if the shape has none.";
            }
            catch (Exception exception)
            {
                _documents.Clear();
                _shapeAnimationsByCode.Clear();
                SelectedShapeKey = null;
                SelectedShapeLabel = null;
                Status = $"Could not load shape {label}: {exception.Message}";
                LoggerUtil.Warn(api, this, $"Could not load shape '{label}' for animation: {exception}");
            }
        }

        private static IEnumerable<VanillaEntityOption> BuildGroupedEntityOptions(IReadOnlyList<VanillaEntityMember> members)
        {
            List<VanillaEntityOption> options = [];
            HashSet<VanillaEntityMember> grouped = [];

            foreach (IGrouping<string, VanillaEntityMember> sourceGroup in members
                .Where(member => member.Source != null)
                .GroupBy(member => member.Source!.Key, StringComparer.OrdinalIgnoreCase))
            {
                List<VanillaEntityMember> groupMembers = sourceGroup.OrderBy(member => member.Label, StringComparer.OrdinalIgnoreCase).ToList();
                if (groupMembers.Count > 1 || groupMembers[0].Source?.HasVariantGroups == true)
                {
                    options.Add(BuildEntityOption(groupMembers, "source", "Source family"));
                    foreach (VanillaEntityMember member in groupMembers) grouped.Add(member);
                }
            }

            List<VanillaEntityMember> remaining = members.Where(member => !grouped.Contains(member)).ToList();
            foreach (IGrouping<string, VanillaEntityMember> signatureGroup in remaining
                .Where(member => !string.IsNullOrWhiteSpace(BuildCompatibleEntityGroupKey(member)))
                .GroupBy(BuildCompatibleEntityGroupKey, StringComparer.Ordinal))
            {
                List<VanillaEntityMember> groupMembers = signatureGroup.OrderBy(member => member.Label, StringComparer.OrdinalIgnoreCase).ToList();
                if (groupMembers.Count <= 1) continue;
                options.Add(BuildEntityOption(groupMembers, "compatible", "Compatible animation signature"));
                foreach (VanillaEntityMember member in groupMembers) grouped.Add(member);
            }

            foreach (VanillaEntityMember member in members.Where(member => !grouped.Contains(member)))
            {
                options.Add(BuildEntityOption([member], "single", "Single runtime entity"));
            }

            return options;
        }

        private static IEnumerable<VanillaEntityOption> BuildVisibleEntityOptions(IEnumerable<VanillaEntityOption> options)
        {
            foreach (VanillaEntityOption option in options)
            {
                List<VanillaEntityMember> visibleMembers = option.Members.Where(member => !member.Hidden).ToList();
                if (visibleMembers.Count == 0) continue;
                yield return visibleMembers.Count == option.Members.Count
                    ? option
                    : BuildEntityOption(visibleMembers, option.GroupKind, option.GroupKind, option.HiddenCount + option.Members.Count - visibleMembers.Count);
            }
        }

        private static VanillaEntityOption BuildEntityOption(IReadOnlyList<VanillaEntityMember> members, string groupKeyPrefix, string groupKind, int extraHiddenCount = 0)
        {
            List<VanillaEntityMember> sorted = members.OrderBy(member => member.Label, StringComparer.OrdinalIgnoreCase).ToList();
            int hiddenCount = sorted.Count(member => member.Hidden) + extraHiddenCount;
            string domain = BuildGroupDomain(sorted);
            string label = sorted.Count == 1
                ? sorted[0].Label
                : $"{BuildGroupBaseLabel(sorted)} ({sorted.Count})";
            string groupKey = $"{groupKeyPrefix}:{string.Join("|", sorted.Select(member => member.FullLabel))}";
            string tooltip = BuildEntityOptionTooltip(sorted, groupKind, hiddenCount);
            string search = $"{label} {tooltip} {string.Join(' ', sorted.Select(member => $"{member.Label} {member.FullLabel} {member.Source?.AssetPath} {member.Source?.SourceCode}"))}";
            return new(sorted, label, tooltip, domain, search, groupKey, groupKind, hiddenCount);
        }

        private static string BuildEntityOptionTooltip(IReadOnlyList<VanillaEntityMember> members, string groupKind, int hiddenCount)
        {
            StringBuilder builder = new();
            builder.Append(groupKind).AppendLine();
            builder.Append("Members: ").Append(members.Count);
            if (hiddenCount > 0) builder.Append(" (hidden/helper: ").Append(hiddenCount).Append(')');
            builder.AppendLine();

            string[] sourceAssets = members
                .Select(member => member.Source?.Key)
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray()!;
            if (sourceAssets.Length > 0)
            {
                builder.Append("Source: ").Append(string.Join(", ", sourceAssets)).AppendLine();
            }

            foreach (VanillaEntityMember member in members.Take(24))
            {
                builder.Append("- ").Append(member.FullLabel);
                if (member.Hidden && !string.IsNullOrWhiteSpace(member.HiddenReason))
                {
                    builder.Append(" (").Append(member.HiddenReason).Append(')');
                }
                builder.AppendLine();
            }

            if (members.Count > 24)
            {
                builder.Append("... ").Append(members.Count - 24).Append(" more");
            }

            return builder.ToString().TrimEnd();
        }

        private static string BuildGroupDomain(IReadOnlyList<VanillaEntityMember> members)
        {
            string[] domains = members.Select(member => member.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return domains.Length == 1 ? domains[0] : "";
        }

        private static string BuildGroupBaseLabel(IReadOnlyList<VanillaEntityMember> members)
        {
            VanillaEntitySourceInfo? commonSource = members[0].Source;
            if (commonSource != null && members.All(member => member.Source?.Key == commonSource.Key))
            {
                string sourceCode = commonSource.SourceCode.Contains(':', StringComparison.Ordinal)
                    ? commonSource.SourceCode
                    : $"{commonSource.Location.Domain}:{commonSource.SourceCode}";
                return ImGuiLayoutHelper.CompactAssetCode(sourceCode);
            }

            string prefix = GetCommonPrefix(members.Select(member => member.Label)).TrimEnd('-', '_', '/', ' ');
            return prefix.Length >= 3 ? prefix : members[0].Label;
        }

        private static string GetCommonPrefix(IEnumerable<string> values)
        {
            using IEnumerator<string> enumerator = values.GetEnumerator();
            if (!enumerator.MoveNext()) return "";
            string prefix = enumerator.Current;
            while (enumerator.MoveNext() && prefix.Length > 0)
            {
                string value = enumerator.Current;
                int length = Math.Min(prefix.Length, value.Length);
                int index = 0;
                while (index < length && char.ToUpperInvariant(prefix[index]) == char.ToUpperInvariant(value[index])) index++;
                prefix = prefix[..index];
            }

            return prefix;
        }

        private static string BuildCompatibleEntityGroupKey(VanillaEntityMember member)
        {
            return string.IsNullOrWhiteSpace(member.MetadataSignature) || string.IsNullOrWhiteSpace(member.ShapeSignature)
                ? ""
                : $"{member.MetadataSignature}\n--shape--\n{member.ShapeSignature}";
        }

        private static int CompareEntityOptions(VanillaEntityOption left, VanillaEntityOption right)
        {
            return string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildMetadataCompatibilitySignature(EntityProperties entityType)
        {
            AnimationMetaData[] animations = entityType.Client?.Animations ?? [];
            if (animations.Length == 0) return "";
            StringBuilder builder = new();
            for (int index = 0; index < animations.Length; index++)
            {
                AnimationMetaData animation = animations[index];
                builder.Append(index)
                    .Append(':')
                    .Append(animation.Code ?? "")
                    .Append("->")
                    .Append(animation.Animation ?? "")
                    .Append('|');
            }

            return builder.ToString();
        }

        private static string BuildShapeCompatibilitySignature(EntityProperties entityType)
        {
            Shape? shape = entityType.Client?.LoadedShapeForEntity ?? entityType.Client?.LoadedShape;
            VanillaAnimation[] animations = shape?.Animations ?? [];
            if (animations.Length == 0) return "";

            StringBuilder builder = new();
            for (int index = 0; index < animations.Length; index++)
            {
                VanillaAnimation animation = animations[index];
                builder.Append(index)
                    .Append(':')
                    .Append(animation.Code ?? animation.Name ?? "")
                    .Append(':')
                    .Append(animation.QuantityFrames)
                    .Append(':');

                foreach (AnimationKeyFrame keyFrame in animation.KeyFrames ?? [])
                {
                    builder.Append(keyFrame.Frame).Append('[');
                    if (keyFrame.Elements != null)
                    {
                        foreach (string elementName in keyFrame.Elements.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                        {
                            builder.Append(elementName).Append(',');
                        }
                    }
                    builder.Append(']');
                }

                builder.Append('|');
            }

            return builder.ToString();
        }

        private static string BuildPlayerModelShapeCompatibilitySignature(VanillaPlayerModelSource playerModel)
        {
            VanillaAnimation[] animations = playerModel.Shape.Animations ?? [];
            if (animations.Length == 0) return "";

            StringBuilder builder = new();
            builder.Append("playermodel:").Append(playerModel.AnimationSourceCode).Append(':').Append(playerModel.MatchedElementCount).Append('|');
            for (int index = 0; index < animations.Length; index++)
            {
                VanillaAnimation animation = animations[index];
                builder.Append(index)
                    .Append(':')
                    .Append(animation.Code ?? animation.Name ?? "")
                    .Append(':')
                    .Append(animation.QuantityFrames)
                    .Append(':');

                foreach (AnimationKeyFrame keyFrame in animation.KeyFrames ?? [])
                {
                    builder.Append(keyFrame.Frame).Append('[');
                    if (keyFrame.Elements != null)
                    {
                        foreach (string elementName in keyFrame.Elements.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                        {
                            builder.Append(elementName).Append(',');
                        }
                    }
                    builder.Append(']');
                }

                builder.Append('|');
            }

            return builder.ToString();
        }

        private sealed class VanillaPlayerModelSourceIndex
        {
            private static string _cachedConfigSignature = "";
            private static bool _cachedConfigInitialized;
            private static Dictionary<string, VanillaPlayerModelConfig> _cachedConfigs = new(StringComparer.OrdinalIgnoreCase);

            public static IReadOnlyList<VanillaPlayerModelSource> Build(ICoreClientAPI api, IEnumerable<EntityProperties> entityTypes)
            {
                Dictionary<string, VanillaPlayerModelConfig> configs = LoadConfigs(api);
                if (configs.Count == 0) return [];

                List<EntityProperties> animationCandidates = entityTypes
                    .Where(entityType => (entityType.Client?.LoadedShapeForEntity ?? entityType.Client?.LoadedShape)?.Animations?.Length > 0)
                    .Distinct()
                    .ToList();
                if (animationCandidates.Count == 0) return [];

                List<VanillaPlayerModelSource> result = [];
                HashSet<string> indexedShapes = new(StringComparer.OrdinalIgnoreCase);
                foreach (VanillaPlayerModelConfig config in configs.Values.OrderBy(config => config.FullCode, StringComparer.OrdinalIgnoreCase))
                {
                    if (!config.Enabled) continue;
                    AssetLocation? shapeLocation = ResolveShapeLocation(api, config.ShapePath, config.ConfigLocation.Domain);
                    if (shapeLocation == null) continue;

                    IAsset? shapeAsset = api.Assets.TryGet(shapeLocation, true);
                    Shape? modelShape = TryLoadShape(shapeAsset);
                    if (modelShape == null) continue;
                    NormalizePlayerModelShapeTextures(modelShape, shapeLocation.Domain);

                    Shape? scoreShape = modelShape;
                    IReadOnlyList<string> matchElements = config.KeyElements;
                    if (!string.IsNullOrWhiteSpace(config.BaseShapeCode) &&
                        configs.TryGetValue(NormalizeModelCode(config.BaseShapeCode, config.ConfigLocation.Domain), out VanillaPlayerModelConfig? baseConfig))
                    {
                        AssetLocation? baseShapeLocation = ResolveShapeLocation(api, baseConfig.ShapePath, baseConfig.ConfigLocation.Domain);
                        Shape? baseShape = TryLoadShape(api.Assets.TryGet(baseShapeLocation, true));
                        scoreShape = baseShape ?? scoreShape;
                        if (baseConfig.KeyElements.Count > 0)
                        {
                            matchElements = baseConfig.KeyElements;
                        }
                    }

                    JObject? shapeSourceJson = TryLoadJson(api, shapeLocation);
                    bool hasOwnAnimations = modelShape.Animations is { Length: > 0 };
                    bool hasBorrowedSource = TryFindBestAnimationSource(api, animationCandidates, scoreShape, matchElements, out EntityProperties? animationEntity, out Shape? animationShape, out int matchedElements) &&
                        animationEntity != null &&
                        animationShape?.Animations is { Length: > 0 };
                    if (!hasOwnAnimations && !hasBorrowedSource)
                    {
                        continue;
                    }

                    Shape editableShape = modelShape.Clone() ?? modelShape;
                    VanillaPlayerModelAnimationMode animationMode;
                    AssetLocation animationAssetLocation = shapeLocation;
                    JObject? animationSourceJson = shapeSourceJson;
                    EntityProperties runtimeEntityType;
                    string animationSourceCode;

                    if (hasOwnAnimations)
                    {
                        editableShape.Animations = (editableShape.Animations ?? []).Select(CloneVanillaAnimation).ToArray();
                        runtimeEntityType = animationEntity ?? ResolvePlayerModelRuntimeEntity(api, animationCandidates);
                        animationSourceCode = "own shape";
                        animationMode = VanillaPlayerModelAnimationMode.OwnShape;
                    }
                    else
                    {
                        editableShape.Animations = animationShape!.Animations.Select(CloneVanillaAnimation).ToArray();
                        runtimeEntityType = animationEntity!;
                        animationSourceCode = animationEntity!.Code?.ToString() ?? config.FullCode;
                        animationMode = VanillaPlayerModelAnimationMode.BorrowedFallback;

                        AssetLocation? borrowedAssetLocation = GetShapeAssetLocation(animationEntity);
                        if (borrowedAssetLocation != null)
                        {
                            animationAssetLocation = borrowedAssetLocation;
                            animationSourceJson = TryLoadJson(api, borrowedAssetLocation);
                        }
                    }

                    string shapeKey = $"{config.FullCode}|{shapeLocation}";
                    if (!indexedShapes.Add(shapeKey)) continue;

                    result.Add(new(
                        config.Code,
                        config.ConfigLocation,
                        config.ConfigAssetPath,
                        config.SourceJson,
                        shapeLocation,
                        shapeSourceJson,
                        editableShape,
                        animationAssetLocation,
                        animationSourceJson,
                        runtimeEntityType,
                        animationSourceCode,
                        hasBorrowedSource ? matchedElements : 0,
                        animationMode,
                        hasOwnAnimations ? CollectAnimationCodes(modelShape.Animations) : []));
                }

                return result;
            }

            private static Dictionary<string, VanillaPlayerModelConfig> LoadConfigs(ICoreClientAPI api)
            {
                List<IAsset> candidateAssets = EnumeratePlayerModelConfigAssets(api).ToList();
                string signature = BuildPlayerModelConfigAssetSignature(candidateAssets);
                if (_cachedConfigInitialized && string.Equals(signature, _cachedConfigSignature, StringComparison.Ordinal))
                {
                    return new Dictionary<string, VanillaPlayerModelConfig>(_cachedConfigs, StringComparer.OrdinalIgnoreCase);
                }

                Dictionary<string, VanillaPlayerModelConfig> configs = new(StringComparer.OrdinalIgnoreCase);
                foreach (IAsset asset in candidateAssets)
                {
                    string assetPath = asset.Location.Path.Replace('\\', '/');
                    JObject? source = TryParseJsonObject(ReadAssetText(asset));
                    if (source == null) continue;

                    foreach (JProperty property in source.Properties())
                    {
                        if (property.Value is not JObject entry) continue;
                        string? shapePath = entry["ShapePath"]?.ToString();
                        if (string.IsNullOrWhiteSpace(shapePath)) continue;

                        bool hasPlayerModelSignals =
                            entry["BaseShapeCode"] != null ||
                            entry["KeyElements"] is JArray ||
                            entry["SkinnableParts"] is JArray ||
                            assetPath.Contains("/customplayermodels/", StringComparison.OrdinalIgnoreCase) ||
                            assetPath.Contains("/baseshapes/", StringComparison.OrdinalIgnoreCase);
                        if (!hasPlayerModelSignals) continue;

                        bool enabled = entry["Enabled"]?.Type != JTokenType.Boolean || entry["Enabled"]?.Value<bool>() == true;
                        string code = NormalizeModelCode(property.Name, asset.Location.Domain);
                        configs[code] = new(
                            code,
                            new AssetLocation(asset.Location.Domain, assetPath),
                            assetPath,
                            source,
                            shapePath,
                            entry["BaseShapeCode"]?.ToString(),
                            CollectPlayerModelKeyElements(entry),
                            enabled);
                    }
                }

                _cachedConfigSignature = signature;
                _cachedConfigInitialized = true;
                _cachedConfigs = new Dictionary<string, VanillaPlayerModelConfig>(configs, StringComparer.OrdinalIgnoreCase);
                return new Dictionary<string, VanillaPlayerModelConfig>(configs, StringComparer.OrdinalIgnoreCase);
            }

            private static IEnumerable<IAsset> EnumeratePlayerModelConfigAssets(ICoreClientAPI api)
            {
                foreach (IAsset asset in api.Assets.AllAssets.Values)
                {
                    if (asset?.Location == null) continue;
                    string assetPath = asset.Location.Path.Replace('\\', '/');
                    if (!assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                        (!assetPath.StartsWith("config/", StringComparison.OrdinalIgnoreCase) &&
                         !assetPath.Contains("/config/", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    yield return asset;
                }
            }

            private static string BuildPlayerModelConfigAssetSignature(IEnumerable<IAsset> assets)
            {
                StringBuilder builder = new();
                foreach (IAsset asset in assets.OrderBy(asset => asset.Location?.ToString() ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    builder
                        .Append(asset.Location?.ToString() ?? "")
                        .Append('#')
                        .Append(asset.GetHashCode())
                        .Append('|');
                }

                return builder.ToString();
            }

            private static bool TryFindBestAnimationSource(
                ICoreClientAPI api,
                IReadOnlyList<EntityProperties> candidates,
                Shape modelShape,
                IReadOnlyList<string> keyElements,
                out EntityProperties? entityType,
                out Shape? animationShape,
                out int matchedElements)
            {
                HashSet<string> modelElements = keyElements.Count > 0
                    ? keyElements.Where(element => !string.IsNullOrWhiteSpace(element)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : CollectShapeElementNames(modelShape);
                EntityProperties? localPlayerType = api.World?.Player?.Entity?.Properties;
                entityType = null;
                animationShape = null;
                matchedElements = 0;
                if (modelElements.Count == 0) return false;

                foreach (EntityProperties candidate in candidates.OrderBy(candidate => candidate.Code?.ToString() ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    Shape? candidateShape = candidate.Client?.LoadedShapeForEntity ?? candidate.Client?.LoadedShape;
                    VanillaAnimation[] animations = candidateShape?.Animations ?? [];
                    if (animations.Length == 0) continue;

                    HashSet<string> animationElements = CollectAnimationElementNames(animations);
                    int score = animationElements.Count(modelElements.Contains);
                    if (score < matchedElements) continue;
                    if (score == matchedElements && !ShouldPreferAnimationCandidate(candidate, entityType, localPlayerType)) continue;

                    matchedElements = score;
                    entityType = candidate;
                    animationShape = candidateShape;
                }

                return matchedElements > 0;
            }

            private static bool ShouldPreferAnimationCandidate(EntityProperties candidate, EntityProperties? current, EntityProperties? localPlayerType)
            {
                if (current == null) return true;

                bool candidateIsLocal = IsSameEntityType(candidate, localPlayerType);
                bool currentIsLocal = IsSameEntityType(current, localPlayerType);
                if (candidateIsLocal != currentIsLocal) return candidateIsLocal;

                return string.Compare(candidate.Code?.ToString() ?? "", current.Code?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) < 0;
            }

            private static bool IsSameEntityType(EntityProperties? left, EntityProperties? right)
            {
                if (left == null || right == null) return false;
                return ReferenceEquals(left, right) ||
                    string.Equals(left.Code?.ToString(), right.Code?.ToString(), StringComparison.OrdinalIgnoreCase);
            }

            private static EntityProperties ResolvePlayerModelRuntimeEntity(ICoreClientAPI api, IReadOnlyList<EntityProperties> candidates)
            {
                EntityProperties? localPlayerType = api.World?.Player?.Entity?.Properties;
                if (localPlayerType != null)
                {
                    return localPlayerType;
                }

                EntityProperties? playerLikeCandidate = candidates.FirstOrDefault(candidate =>
                    candidate.Client?.LoadedShapeForEntity != null &&
                    candidate.Client?.Animations is { Length: > 0 });
                if (playerLikeCandidate != null)
                {
                    return playerLikeCandidate;
                }

                return candidates.First();
            }

            private static AssetLocation? ResolveShapeLocation(ICoreClientAPI api, string rawPath, string defaultDomain)
            {
                if (string.IsNullOrWhiteSpace(rawPath)) return null;
                AssetLocation raw = AssetLocation.Create(rawPath.Trim(), string.IsNullOrWhiteSpace(defaultDomain) ? "game" : defaultDomain);
                foreach (AssetLocation candidate in EnumerateShapeLocationCandidates(raw))
                {
                    if (api.Assets.TryGet(candidate, true) != null)
                    {
                        return candidate;
                    }
                }

                return null;
            }

            private static IEnumerable<AssetLocation> EnumerateShapeLocationCandidates(AssetLocation raw)
            {
                string path = raw.Path.Replace('\\', '/').TrimStart('/');
                string domain = raw.Domain;
                yield return new AssetLocation(domain, path);

                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new AssetLocation(domain, path + ".json");
                }

                if (!path.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new AssetLocation(domain, "shapes/" + path);
                    yield return new AssetLocation(domain, "shapes/" + path + ".json");
                }
            }

            private static Shape? TryLoadShape(IAsset? asset)
            {
                if (asset == null) return null;
                try
                {
                    return asset.ToObject<Shape>();
                }
                catch
                {
                    return null;
                }
            }

            private static void NormalizePlayerModelShapeTextures(Shape shape, string defaultDomain)
            {
                if (shape.Textures == null) return;
                string domain = string.IsNullOrWhiteSpace(defaultDomain) ? "game" : defaultDomain;
                foreach (AssetLocation? texturePath in shape.Textures.Values)
                {
                    if (texturePath == null) continue;
                    if (!texturePath.HasDomain())
                    {
                        texturePath.Domain = domain;
                    }
                }
            }

            private static HashSet<string> CollectShapeElementNames(Shape shape)
            {
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                if (shape.Elements == null) return names;
                foreach (ShapeElement element in shape.Elements)
                {
                    Collect(element, names);
                }

                return names;

                static void Collect(ShapeElement element, HashSet<string> names)
                {
                    if (!string.IsNullOrWhiteSpace(element.Name)) names.Add(element.Name);
                    if (element.Children == null) return;
                    foreach (ShapeElement child in element.Children)
                    {
                        Collect(child, names);
                    }
                }
            }

            private static HashSet<string> CollectAnimationElementNames(IEnumerable<VanillaAnimation> animations)
            {
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                foreach (VanillaAnimation animation in animations)
                {
                    foreach (AnimationKeyFrame keyFrame in animation.KeyFrames ?? [])
                    {
                        if (keyFrame.Elements == null) continue;
                        foreach (string elementName in keyFrame.Elements.Keys)
                        {
                            if (!string.IsNullOrWhiteSpace(elementName)) names.Add(elementName);
                        }
                    }
                }

                return names;
            }

            private static string[] CollectAnimationCodes(IEnumerable<VanillaAnimation>? animations)
            {
                return (animations ?? [])
                    .Select(animation => animation.Code ?? animation.Name ?? "")
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            private static IReadOnlyList<string> CollectPlayerModelKeyElements(JObject entry)
            {
                if (entry["KeyElements"] is not JArray keyElements) return [];
                return keyElements
                    .Select(token => token.Type == JTokenType.String ? token.ToString() : "")
                    .Where(element => !string.IsNullOrWhiteSpace(element))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            private static string NormalizeModelCode(string code, string defaultDomain)
            {
                AssetLocation location = AssetLocation.Create(code, string.IsNullOrWhiteSpace(defaultDomain) ? "game" : defaultDomain);
                return $"{location.Domain}:{location.Path}";
            }

            private sealed record VanillaPlayerModelConfig(
                string Code,
                AssetLocation ConfigLocation,
                string ConfigAssetPath,
                JObject SourceJson,
                string ShapePath,
                string? BaseShapeCode,
                IReadOnlyList<string> KeyElements,
                bool Enabled)
            {
                public string FullCode => Code;
            }
        }

        private sealed class VanillaEntitySourceIndex
        {
            private static readonly string[] TechnicalMetadataMarkers =
            [
                "bot",
                "debug",
                "dev",
                "helper",
                "hidden",
                "internal",
                "technical",
                "test"
            ];

            private readonly Dictionary<string, VanillaEntitySourceInfo> _sourcesByCode = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<VanillaEntitySourceInfo> _sources = [];

            public static VanillaEntitySourceIndex Build(ICoreClientAPI api)
            {
                VanillaEntitySourceIndex index = new();
                foreach (IAsset asset in api.Assets.AllAssets.Values)
                {
                    if (asset?.Location == null) continue;
                    string assetPath = asset.Location.Path.Replace('\\', '/');
                    if (!assetPath.StartsWith("entities/", StringComparison.OrdinalIgnoreCase) ||
                        !assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    JObject? json = TryParseJsonObject(ReadAssetText(asset));
                    string? sourceCode = json?["code"]?.ToString();
                    if (json == null || string.IsNullOrWhiteSpace(sourceCode)) continue;

                    bool hidden = TryGetHiddenReason(json, out string hiddenReason);
                    VanillaEntitySourceInfo source = new(
                        new AssetLocation(asset.Location.Domain, assetPath),
                        assetPath,
                        StripCodeDomain(sourceCode),
                        json,
                        json["variantgroups"] is JArray { Count: > 0 },
                        hidden,
                        hiddenReason);
                    index._sources.Add(source);

                    index.Register(source, source.SourceCode);
                    foreach (string entityCode in ExpandEntityCodes(api, source.Location.Domain, json, source.SourceCode))
                    {
                        index.Register(source, entityCode);
                    }
                }

                index._sources.Sort((left, right) => right.SourceCode.Length.CompareTo(left.SourceCode.Length));
                return index;
            }

            public VanillaEntitySourceInfo? Resolve(EntityProperties entityType)
            {
                if (entityType.Code == null) return null;
                string fullCode = NormalizeEntityCode(entityType.Code.Domain, entityType.Code.Path);
                if (_sourcesByCode.TryGetValue(fullCode, out VanillaEntitySourceInfo? exact))
                {
                    return exact;
                }

                string path = entityType.Code.Path;
                foreach (VanillaEntitySourceInfo source in _sources)
                {
                    if (string.Equals(path, source.SourceCode, StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith(source.SourceCode + "-", StringComparison.OrdinalIgnoreCase))
                    {
                        return source;
                    }
                }

                return null;
            }

            private void Register(VanillaEntitySourceInfo source, string code)
            {
                if (string.IsNullOrWhiteSpace(code)) return;
                _sourcesByCode[NormalizeEntityCode(source.Location.Domain, code)] = source;
            }

            private static IEnumerable<string> ExpandEntityCodes(ICoreClientAPI api, string domain, JObject sourceJson, string sourceCode)
            {
                if (sourceJson["variantgroups"] is not JArray groups || groups.Count == 0)
                {
                    yield return sourceCode;
                    yield break;
                }

                List<VanillaVariantGroup> variantGroups = [];
                foreach (JObject group in groups.OfType<JObject>())
                {
                    string? groupCode = group["code"]?.ToString();
                    if (string.IsNullOrWhiteSpace(groupCode)) continue;
                    List<string> states = ResolveVariantStates(api, domain, group).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (states.Count == 0) yield break;
                    variantGroups.Add(new(groupCode, states));
                }

                if (variantGroups.Count == 0)
                {
                    yield return sourceCode;
                    yield break;
                }

                foreach (Dictionary<string, string> combination in BuildVariantCombinations(variantGroups))
                {
                    yield return BuildVariantCode(sourceCode, variantGroups, combination);
                }
            }

            private static IEnumerable<string> ResolveVariantStates(ICoreClientAPI api, string domain, JObject group)
            {
                if (group["states"] is JArray states)
                {
                    foreach (JToken state in states)
                    {
                        string? value = state.ToString();
                        if (!string.IsNullOrWhiteSpace(value)) yield return value;
                    }
                }

                string? loadFromProperties = group["loadFromProperties"]?.ToString();
                if (!string.IsNullOrWhiteSpace(loadFromProperties))
                {
                    foreach (string value in LoadWorldPropertyStates(api, domain, loadFromProperties))
                    {
                        yield return value;
                    }
                }
            }

            private static IEnumerable<string> LoadWorldPropertyStates(ICoreClientAPI api, string domain, string loadFromProperties)
            {
                string path = EnsureJsonPath($"worldproperties/{loadFromProperties.Trim().TrimStart('/')}");
                foreach (string candidateDomain in new[] { domain, "game" }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    IAsset? asset = api.Assets.TryGet(new AssetLocation(candidateDomain, path), true);
                    JObject? json = TryParseJsonObject(ReadAssetText(asset));
                    if (json?["variants"] is not JArray variants) continue;

                    foreach (JToken variant in variants)
                    {
                        string? code = variant.Type == JTokenType.String
                            ? variant.ToString()
                            : variant["Code"]?.ToString() ?? variant["code"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(code)) yield return code;
                    }

                    yield break;
                }
            }

            private static IEnumerable<Dictionary<string, string>> BuildVariantCombinations(IReadOnlyList<VanillaVariantGroup> groups)
            {
                List<Dictionary<string, string>> combinations = [new(StringComparer.OrdinalIgnoreCase)];
                foreach (VanillaVariantGroup group in groups)
                {
                    List<Dictionary<string, string>> next = [];
                    foreach (Dictionary<string, string> combination in combinations)
                    {
                        foreach (string state in group.States)
                        {
                            Dictionary<string, string> copy = new(combination, StringComparer.OrdinalIgnoreCase)
                            {
                                [group.Code] = state
                            };
                            next.Add(copy);
                        }
                    }

                    combinations = next;
                }

                return combinations;
            }

            private static string BuildVariantCode(string sourceCode, IReadOnlyList<VanillaVariantGroup> groups, IReadOnlyDictionary<string, string> states)
            {
                string code = sourceCode;
                List<string> suffixes = [];
                foreach (VanillaVariantGroup group in groups)
                {
                    if (!states.TryGetValue(group.Code, out string? state)) continue;
                    string placeholder = "{" + group.Code + "}";
                    if (code.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
                    {
                        code = ReplaceInvariant(code, placeholder, state);
                    }
                    else
                    {
                        suffixes.Add(state);
                    }
                }

                return suffixes.Count == 0 ? code : $"{code}-{string.Join('-', suffixes)}";
            }

            private static bool TryGetHiddenReason(JObject source, out string reason)
            {
                List<string> evidence = [];
                if (source["tags"] is JArray tags)
                {
                    foreach (string tag in tags.Select(token => token.ToString()))
                    {
                        if (HasTechnicalMarker(tag)) evidence.Add($"tag:{tag}");
                    }
                }

                string? className = source["class"]?.ToString();
                if (!string.IsNullOrWhiteSpace(className) && HasTechnicalMarker(className))
                {
                    evidence.Add($"class:{className}");
                }

                if (source["attributes"] is JObject attributes)
                {
                    foreach (JProperty property in attributes.Properties())
                    {
                        if (HasTechnicalMarker(property.Name))
                        {
                            evidence.Add($"attribute:{property.Name}");
                        }
                    }
                }

                reason = string.Join(", ", evidence.Take(3));
                return evidence.Count > 0;
            }

            private static bool HasTechnicalMarker(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                return EnumerateMetadataTokens(value).Any(token => TechnicalMetadataMarkers.Contains(token, StringComparer.Ordinal));
            }

            private static IEnumerable<string> EnumerateMetadataTokens(string value)
            {
                StringBuilder token = new();
                char previous = '\0';
                foreach (char ch in value)
                {
                    if (!char.IsLetterOrDigit(ch))
                    {
                        if (token.Length > 0)
                        {
                            yield return token.ToString();
                            token.Clear();
                        }
                        previous = '\0';
                        continue;
                    }

                    if (char.IsUpper(ch) && token.Length > 0 && char.IsLower(previous))
                    {
                        yield return token.ToString();
                        token.Clear();
                    }

                    token.Append(char.ToLowerInvariant(ch));
                    previous = ch;
                }

                if (token.Length > 0)
                {
                    yield return token.ToString();
                }
            }

            private static string NormalizeEntityCode(string defaultDomain, string code)
            {
                string trimmed = StripCodeDomain(code);
                string domain = code.Contains(':', StringComparison.Ordinal) ? code[..code.IndexOf(':')] : defaultDomain;
                return $"{domain}:{trimmed}";
            }

            private static string StripCodeDomain(string code)
            {
                int separator = code.IndexOf(':');
                return separator >= 0 ? code[(separator + 1)..] : code;
            }

            private static string ReplaceInvariant(string value, string oldValue, string newValue)
            {
                int index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
                while (index >= 0)
                {
                    value = value[..index] + newValue + value[(index + oldValue.Length)..];
                    index = value.IndexOf(oldValue, index + newValue.Length, StringComparison.OrdinalIgnoreCase);
                }

                return value;
            }

            private sealed record VanillaVariantGroup(string Code, IReadOnlyList<string> States);
        }

        public VanillaShapeAnimationEntry? ResolveShapeAnimation(string animationCode)
        {
            if (string.IsNullOrWhiteSpace(animationCode)) return null;
            return _shapeAnimationsByCode.TryGetValue(animationCode, out List<VanillaShapeAnimationEntry>? entries)
                ? entries.FirstOrDefault()
                : null;
        }

        public void RebuildLinks()
        {
            _shapeAnimationsByCode.Clear();
            foreach (VanillaAnimationDocument document in _documents)
            {
                foreach (VanillaShapeAnimationEntry entry in document.ShapeAnimations)
                {
                    RegisterShapeAnimation(entry);
                }
            }

            foreach (VanillaAnimationDocument document in _documents)
            {
                foreach (VanillaAnimationMetaEntry entry in document.MetadataEntries)
                {
                    entry.LinkedShape = ResolveShapeAnimation(entry.Metadata.Animation);
                }
            }
        }

        private void IndexSelectedEntity(ICoreClientAPI api, VanillaEntityOption option, int memberIndex, bool groupEdit)
        {
            VanillaEntityMember selectedMember = option.Members[Math.Clamp(memberIndex, 0, option.Members.Count - 1)];
            EntityProperties entityType = selectedMember.EntityType;
            VanillaPlayerModelSource? playerModel = selectedMember.PlayerModel;
            try
            {
                _documents.Clear();
                _shapeAnimationsByCode.Clear();

                AnimationMetaData[]? metadata = entityType.Client?.Animations;
                Shape? shape = playerModel?.Shape ?? entityType.Client?.LoadedShapeForEntity ?? entityType.Client?.LoadedShape;
                string entityCode = playerModel?.FullCode ?? entityType.Code?.ToString() ?? $"entity-{entityType.Id}";
                string groupLabel = groupEdit && option.Members.Count > 1 ? option.Label : ImGuiLayoutHelper.CompactAssetCode(entityCode);

                JObject? entitySourceJson = playerModel?.ConfigSourceJson ?? selectedMember.Source?.SourceJson ?? TryLoadJson(api, GetEntityAssetLocation(entityType));
                AssetLocation? entityAssetLocation = playerModel?.ConfigLocation ?? selectedMember.Source?.Location ?? GetEntityAssetLocation(entityType);
                AssetLocation? shapeAssetLocation = playerModel?.AnimationAssetLocation ?? GetShapeAssetLocation(entityType);
                JObject? shapeSourceJson = playerModel?.AnimationSourceJson ?? TryLoadJson(api, shapeAssetLocation);
                IReadOnlyList<VanillaEntityMember> editMembers = groupEdit ? option.Members : [selectedMember];
                VanillaGroupTargets shapeTargets = playerModel != null
                    ? BuildPlayerModelShapeTargets(playerModel)
                    : BuildGroupTargets(editMembers, selectedMember, VanillaDocumentKind.Shape);
                VanillaGroupTargets metadataTargets = BuildGroupTargets(editMembers, selectedMember, VanillaDocumentKind.EntityMetadata);

                VanillaAnimationDocument? shapeDocument = null;
                if (shape?.Animations != null && shape.Animations.Length > 0)
                {
                    shapeDocument = new()
                    {
                        Kind = VanillaDocumentKind.Shape,
                        Domain = shapeAssetLocation?.Domain ?? entityType.Code?.Domain ?? "game",
                        AssetPath = shapeAssetLocation != null ? EnsureJsonPath(shapeAssetLocation.Path) : $"shapes/{entityType.Code?.Path ?? entityCode}.json",
                        DisplayPath = playerModel != null ? $"{entityCode} Playermodelib model" : $"{entityCode} shape",
                        EntityCode = entityCode,
                        EntityType = entityType,
                        Shape = shape,
                        SourceJson = shapeSourceJson,
                        GroupLabel = groupLabel,
                        RuntimeTargetEntities = shapeTargets.Targets,
                        RuntimeSkippedMembers = shapeTargets.Skipped,
                        RuntimeGroupKind = playerModel != null ? GetPlayerModelRuntimeGroupKind(playerModel) : option.GroupKind,
                        UseEntityTypeAsRuntimeFallback = playerModel?.UsesOwnAnimations != true,
                        PlayerModelSource = playerModel
                    };

                    for (int index = 0; index < shape.Animations.Length; index++)
                    {
                        VanillaAnimation animation = CloneVanillaAnimation(shape.Animations[index]);
                        if (string.IsNullOrWhiteSpace(animation.Code)) animation.Code = animation.Name;
                        VanillaShapeAnimationEntry entry = new(shapeDocument, index, animation, GetSourceArrayElement(shapeSourceJson, "animations", index));
                        shapeDocument.ShapeAnimations.Add(entry);
                        RegisterShapeAnimation(entry);
                    }

                    _documents.Add(shapeDocument);
                    shapeDocument.MarkClean();
                }

                if (playerModel == null)
                {
                    VanillaAnimationDocument metadataDocument = new()
                    {
                        Kind = VanillaDocumentKind.EntityMetadata,
                        Domain = entityAssetLocation?.Domain ?? entityType.Code?.Domain ?? "game",
                        AssetPath = entityAssetLocation?.Path ?? $"entities/{entityType.Code?.Path ?? entityCode}.json",
                        DisplayPath = entityCode,
                        EntityCode = entityCode,
                        EntityType = entityType,
                        Shape = shape,
                        SourceJson = entitySourceJson,
                        GroupLabel = groupLabel,
                        RuntimeTargetEntities = metadataTargets.Targets,
                        RuntimeSkippedMembers = metadataTargets.Skipped,
                        RuntimeGroupKind = option.GroupKind
                    };

                    if (metadata != null)
                    {
                        for (int index = 0; index < metadata.Length; index++)
                        {
                            AnimationMetaData editable = CloneAnimationMetaData(metadata[index]);
                            VanillaShapeAnimationEntry? linkedShape = ResolveShapeAnimation(editable.Animation);
                            metadataDocument.MetadataEntries.Add(new(metadataDocument, index, editable, linkedShape, GetNestedArrayElement(entitySourceJson, ["client", "animations"], index)));
                        }
                    }

                    _documents.Add(metadataDocument);
                    metadataDocument.MarkClean();
                }

                RebuildLinks();

                int shapeCount = shapeDocument?.ShapeAnimations.Count ?? 0;
                int metadataCount = _documents.Sum(document => document.MetadataEntries.Count);
                string targetStatus = groupEdit && option.Members.Count > 1
                    ? $" Group edit targets: metadata {metadataTargets.Targets.Count}/{editMembers.Count}, shape {shapeTargets.Targets.Count}/{editMembers.Count}."
                    : "";
                string playerModelStatus = playerModel == null
                    ? ""
                    : BuildPlayerModelIndexStatus(playerModel);
                Status = $"Indexed {entityCode}: {shapeCount} shape animations, {metadataCount} metadata entries.{targetStatus}{playerModelStatus}";
            }
            catch (Exception exception)
            {
                _documents.Clear();
                _shapeAnimationsByCode.Clear();
                Status = $"Could not index {entityType.Code}: {exception.Message}";
                LoggerUtil.Warn(api, this, $"Could not index vanilla entity animation '{entityType.Code}': {exception}");
            }
        }

        private void IndexSelectedBlock(ICoreClientAPI api, VanillaBlockOption option)
        {
            Block block = option.Block;
            try
            {
                _documents.Clear();
                _shapeAnimationsByCode.Clear();

                AssetLocation? shapeAssetLocation = GetBlockShapeAssetLocation(block);
                Shape? loadedShape = shapeAssetLocation == null ? null : Shape.TryGet(api, shapeAssetLocation);
                if (loadedShape == null)
                {
                    throw new InvalidOperationException($"Could not load block shape {shapeAssetLocation?.ToString() ?? "<none>"}.");
                }

                Shape editableShape = loadedShape.Clone() ?? loadedShape;
                editableShape.Animations ??= [];
                JObject? shapeSourceJson = TryLoadJson(api, shapeAssetLocation);
                string blockCode = block.Code?.ToString() ?? option.FullLabel;

                VanillaAnimationDocument shapeDocument = new()
                {
                    Kind = VanillaDocumentKind.Shape,
                    Domain = shapeAssetLocation?.Domain ?? block.Code?.Domain ?? "game",
                    AssetPath = shapeAssetLocation != null ? EnsureJsonPath(shapeAssetLocation.Path) : $"shapes/{block.Code?.Path ?? "unknown"}.json",
                    DisplayPath = $"{blockCode} block shape",
                    EntityCode = blockCode,
                    Block = block,
                    Shape = editableShape,
                    SourceJson = shapeSourceJson,
                    GroupLabel = option.Label,
                    RuntimeGroupKind = "block"
                };

                for (int index = 0; index < editableShape.Animations.Length; index++)
                {
                    VanillaAnimation animation = CloneVanillaAnimation(editableShape.Animations[index]);
                    if (string.IsNullOrWhiteSpace(animation.Code)) animation.Code = animation.Name;
                    VanillaShapeAnimationEntry entry = new(shapeDocument, index, animation, GetSourceArrayElement(shapeSourceJson, "animations", index));
                    shapeDocument.ShapeAnimations.Add(entry);
                    RegisterShapeAnimation(entry);
                }

                _documents.Add(shapeDocument);
                shapeDocument.MarkClean();
                RebuildLinks();

                Status = $"Indexed {blockCode}: {shapeDocument.ShapeAnimations.Count} shape animation(s). New animations can be added to blocks without an existing animations array.";
            }
            catch (Exception exception)
            {
                _documents.Clear();
                _shapeAnimationsByCode.Clear();
                Status = $"Could not index block {block.Code}: {exception.Message}";
                LoggerUtil.Warn(api, this, $"Could not index vanilla block animation '{block.Code}': {exception}");
            }
        }

        private static VanillaGroupTargets BuildPlayerModelShapeTargets(VanillaPlayerModelSource playerModel)
        {
            return playerModel.UsesOwnAnimations
                ? new VanillaGroupTargets([], 0)
                : new VanillaGroupTargets([playerModel.AnimationEntityType], 0);
        }

        private static string GetPlayerModelRuntimeGroupKind(VanillaPlayerModelSource playerModel)
        {
            return playerModel.UsesOwnAnimations
                ? "Playermodelib own shape"
                : "Playermodelib borrowed source";
        }

        private static string BuildPlayerModelIndexStatus(VanillaPlayerModelSource playerModel)
        {
            if (playerModel.UsesOwnAnimations)
            {
                return $" Playermodelib model uses its own shape animations from {playerModel.ShapeLocation}.";
            }

            return $" Playermodelib model preview borrows animations from {playerModel.AnimationSourceCode} ({playerModel.MatchedElementCount} matched elements); edits save to {playerModel.AnimationAssetLocation}.";
        }

        private static VanillaGroupTargets BuildGroupTargets(IReadOnlyList<VanillaEntityMember> members, VanillaEntityMember selected, VanillaDocumentKind kind)
        {
            string selectedSignature = kind == VanillaDocumentKind.Shape ? selected.ShapeSignature : selected.MetadataSignature;
            List<EntityProperties> targets = [];
            int skipped = 0;
            foreach (VanillaEntityMember member in members)
            {
                string signature = kind == VanillaDocumentKind.Shape ? member.ShapeSignature : member.MetadataSignature;
                if (!string.IsNullOrWhiteSpace(selectedSignature) &&
                    string.Equals(signature, selectedSignature, StringComparison.Ordinal))
                {
                    targets.Add(member.EntityType);
                }
                else
                {
                    skipped++;
                }
            }

            if (targets.Count == 0)
            {
                targets.Add(selected.EntityType);
                skipped = Math.Max(0, members.Count - 1);
            }

            return new(targets, skipped);
        }

        private void RegisterShapeAnimation(VanillaShapeAnimationEntry entry)
        {
            string? code = entry.Animation.Code;
            if (string.IsNullOrWhiteSpace(code)) return;

            if (!_shapeAnimationsByCode.TryGetValue(code, out List<VanillaShapeAnimationEntry>? entries))
            {
                _shapeAnimationsByCode[code] = entries = [];
            }

            entries.Add(entry);
        }

        private static AssetLocation? GetEntityAssetLocation(EntityProperties entityType)
        {
            return entityType.Code == null ? null : new AssetLocation(entityType.Code.Domain, $"entities/{entityType.Code.Path}.json");
        }

        private static AssetLocation? GetShapeAssetLocation(EntityProperties entityType)
        {
            CompositeShape? shape = entityType.Client?.ShapeForEntity ?? entityType.Client?.Shape;
            return shape?.Base?.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
        }

        private static AssetLocation? GetBlockShapeAssetLocation(Block block)
        {
            return block.Shape?.Base?.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
        }

        private static VanillaBlockSourceInfo? BuildBlockSourceInfo(Block block)
        {
            IAsset? asset = FindCollectibleSourceAsset(block);
            if (asset?.Location == null) return null;
            return new(
                asset.Location,
                asset.Location.Path.Replace('\\', '/'),
                TryParseJsonObject(ReadAssetText(asset)));
        }

        private static JObject? TryLoadJson(ICoreClientAPI api, AssetLocation? location)
        {
            if (location == null) return null;
            IAsset? asset = api.Assets.TryGet(location, true);
            return TryParseJsonObject(ReadAssetText(asset));
        }
    }
}
