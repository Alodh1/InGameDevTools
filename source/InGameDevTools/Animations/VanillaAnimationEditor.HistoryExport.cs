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
    private sealed class VanillaAnimationDocumentSnapshot
    {
        private readonly int[] _animationIndexes;
        private readonly List<VanillaAnimation> _animations;
        private readonly List<JToken?> _animationSourceTokens;
        private readonly int[] _metadataIndexes;
        private readonly List<AnimationMetaData> _metadata;
        private readonly List<JToken?> _metadataSourceTokens;

        private VanillaAnimationDocumentSnapshot(
            string label,
            int[] animationIndexes,
            List<VanillaAnimation> animations,
            List<JToken?> animationSourceTokens,
            int[] metadataIndexes,
            List<AnimationMetaData> metadata,
            List<JToken?> metadataSourceTokens)
        {
            Label = label;
            _animationIndexes = animationIndexes;
            _animations = animations;
            _animationSourceTokens = animationSourceTokens;
            _metadataIndexes = metadataIndexes;
            _metadata = metadata;
            _metadataSourceTokens = metadataSourceTokens;
        }

        public string Label { get; }

        public static VanillaAnimationDocumentSnapshot FromDocument(VanillaAnimationDocument document, string label)
        {
            return FromIndexes(
                document,
                label,
                Enumerable.Range(0, document.ShapeAnimations.Count).ToArray(),
                Enumerable.Range(0, document.MetadataEntries.Count).ToArray());
        }

        public static VanillaAnimationDocumentSnapshot FromDocument(VanillaAnimationDocument document, string label, VanillaBrowserRow row)
        {
            List<int> animationIndexes = [];
            List<int> metadataIndexes = [];

            if (row.ShapeAnimation?.Document == document)
            {
                animationIndexes.Add(row.ShapeAnimation.Index);
            }

            if (row.MetadataEntry?.Document == document)
            {
                metadataIndexes.Add(row.MetadataEntry.Index);
            }

            if (animationIndexes.Count == 0 && metadataIndexes.Count == 0)
            {
                return FromDocument(document, label);
            }

            return FromIndexes(
                document,
                label,
                animationIndexes.Distinct().OrderBy(index => index).ToArray(),
                metadataIndexes.Distinct().OrderBy(index => index).ToArray());
        }

        private static VanillaAnimationDocumentSnapshot FromIndexes(VanillaAnimationDocument document, string label, int[] animationIndexes, int[] metadataIndexes)
        {
            return new(
                label,
                animationIndexes,
                animationIndexes
                    .Where(index => index >= 0 && index < document.ShapeAnimations.Count)
                    .Select(index => CloneVanillaAnimation(document.ShapeAnimations[index].Animation))
                    .ToList(),
                animationIndexes
                    .Where(index => index >= 0 && index < document.ShapeAnimations.Count)
                    .Select(index => document.ShapeAnimations[index].SourceToken?.DeepClone())
                    .ToList(),
                metadataIndexes,
                metadataIndexes
                    .Where(index => index >= 0 && index < document.MetadataEntries.Count)
                    .Select(index => CloneAnimationMetaData(document.MetadataEntries[index].Metadata))
                    .ToList(),
                metadataIndexes
                    .Where(index => index >= 0 && index < document.MetadataEntries.Count)
                    .Select(index => document.MetadataEntries[index].SourceToken?.DeepClone())
                    .ToList());
        }

        public bool Matches(VanillaAnimationDocument document)
        {
            int animationCount = Math.Min(_animationIndexes.Length, _animations.Count);
            for (int index = 0; index < animationCount; index++)
            {
                int animationIndex = _animationIndexes[index];
                if (animationIndex < 0 || animationIndex >= document.ShapeAnimations.Count) continue;

                VanillaShapeAnimationEntry entry = document.ShapeAnimations[animationIndex];
                if (!VanillaAnimationsStructurallyEqual(_animations[index], entry.Animation)) return false;
                if (!VanillaTokensEqual(_animationSourceTokens[index], entry.SourceToken)) return false;
            }

            int metadataCount = Math.Min(_metadataIndexes.Length, _metadata.Count);
            for (int index = 0; index < metadataCount; index++)
            {
                int metadataIndex = _metadataIndexes[index];
                if (metadataIndex < 0 || metadataIndex >= document.MetadataEntries.Count) continue;

                VanillaAnimationMetaEntry entry = document.MetadataEntries[metadataIndex];
                if (!VanillaMetadataStructurallyEqual(_metadata[index], entry.Metadata)) return false;
                if (!VanillaTokensEqual(_metadataSourceTokens[index], entry.SourceToken)) return false;
            }

            return true;
        }

        public bool SameContentAs(VanillaAnimationDocumentSnapshot other)
        {
            if (!_animationIndexes.SequenceEqual(other._animationIndexes)) return false;
            if (!_metadataIndexes.SequenceEqual(other._metadataIndexes)) return false;
            if (_animations.Count != other._animations.Count || _metadata.Count != other._metadata.Count) return false;

            for (int index = 0; index < _animations.Count; index++)
            {
                if (!VanillaAnimationsStructurallyEqual(_animations[index], other._animations[index])) return false;
                if (!VanillaTokensEqual(_animationSourceTokens[index], other._animationSourceTokens[index])) return false;
            }

            for (int index = 0; index < _metadata.Count; index++)
            {
                if (!VanillaMetadataStructurallyEqual(_metadata[index], other._metadata[index])) return false;
                if (!VanillaTokensEqual(_metadataSourceTokens[index], other._metadataSourceTokens[index])) return false;
            }

            return true;
        }

        private static bool VanillaTokensEqual(JToken? left, JToken? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return left == null && right == null;
            return JToken.DeepEquals(left, right);
        }

        private static bool VanillaAnimationsStructurallyEqual(VanillaAnimation left, VanillaAnimation right)
        {
            if (!string.Equals(left.Code, right.Code, StringComparison.Ordinal)) return false;
            if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal)) return false;
            if (left.QuantityFrames != right.QuantityFrames) return false;
            if (left.Version != right.Version) return false;
            if (left.EaseAnimationSpeed != right.EaseAnimationSpeed) return false;
            if (left.OnActivityStopped != right.OnActivityStopped) return false;
            if (left.OnAnimationEnd != right.OnAnimationEnd) return false;

            AnimationKeyFrame[] leftFrames = left.KeyFrames ?? [];
            AnimationKeyFrame[] rightFrames = right.KeyFrames ?? [];
            if (leftFrames.Length != rightFrames.Length) return false;

            for (int index = 0; index < leftFrames.Length; index++)
            {
                if (!VanillaKeyFramesStructurallyEqual(leftFrames[index], rightFrames[index])) return false;
            }

            return true;
        }

        private static bool VanillaKeyFramesStructurallyEqual(AnimationKeyFrame left, AnimationKeyFrame right)
        {
            if (left.Frame != right.Frame) return false;

            int leftCount = left.Elements?.Count ?? 0;
            int rightCount = right.Elements?.Count ?? 0;
            if (leftCount != rightCount) return false;
            if (leftCount == 0) return true;

            foreach ((string name, AnimationKeyFrameElement leftElement) in left.Elements!)
            {
                if (right.Elements == null ||
                    !right.Elements.TryGetValue(name, out AnimationKeyFrameElement? rightElement) ||
                    rightElement == null ||
                    !VanillaElementsStructurallyEqual(leftElement, rightElement))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool VanillaElementsStructurallyEqual(AnimationKeyFrameElement left, AnimationKeyFrameElement right)
        {
            return left.OffsetX == right.OffsetX &&
                left.OffsetY == right.OffsetY &&
                left.OffsetZ == right.OffsetZ &&
                left.StretchX == right.StretchX &&
                left.StretchY == right.StretchY &&
                left.StretchZ == right.StretchZ &&
                left.RotationX == right.RotationX &&
                left.RotationY == right.RotationY &&
                left.RotationZ == right.RotationZ &&
                left.OriginX == right.OriginX &&
                left.OriginY == right.OriginY &&
                left.OriginZ == right.OriginZ &&
                left.RotShortestDistanceX == right.RotShortestDistanceX &&
                left.RotShortestDistanceY == right.RotShortestDistanceY &&
                left.RotShortestDistanceZ == right.RotShortestDistanceZ;
        }

        private static bool VanillaMetadataStructurallyEqual(AnimationMetaData left, AnimationMetaData right)
        {
            if (!string.Equals(left.Code, right.Code, StringComparison.Ordinal)) return false;
            if (!string.Equals(left.Animation, right.Animation, StringComparison.Ordinal)) return false;
            if (left.Weight != right.Weight) return false;
            if (left.AnimationSpeed != right.AnimationSpeed) return false;
            if (left.MulWithWalkSpeed != right.MulWithWalkSpeed) return false;
            if (left.WeightCapFactor != right.WeightCapFactor) return false;
            if (left.EaseInSpeed != right.EaseInSpeed) return false;
            if (left.EaseOutSpeed != right.EaseOutSpeed) return false;
            if (left.BlendMode != right.BlendMode) return false;
            if (left.SupressDefaultAnimation != right.SupressDefaultAnimation) return false;
            if (left.HoldEyePosAfterEasein != right.HoldEyePosAfterEasein) return false;
            if (left.ClientSide != right.ClientSide) return false;
            if (left.WithFpVariant != right.WithFpVariant) return false;
            if (left.AdjustCollisionBox != right.AdjustCollisionBox) return false;
            if (!VanillaFloatDictionariesEqual(left.ElementWeight, right.ElementWeight)) return false;
            if (!VanillaBlendModeDictionariesEqual(left.ElementBlendMode, right.ElementBlendMode)) return false;

            AnimationSound[] leftSounds = left.AnimationSounds ?? [];
            AnimationSound[] rightSounds = right.AnimationSounds ?? [];
            if (leftSounds.Length != rightSounds.Length) return false;
            for (int index = 0; index < leftSounds.Length; index++)
            {
                AnimationSound leftSound = leftSounds[index];
                AnimationSound rightSound = rightSounds[index];
                if (leftSound.Frame != rightSound.Frame ||
                    leftSound.Chance != rightSound.Chance ||
                    leftSound.Looping != rightSound.Looping ||
                    leftSound.Attributes.Range != rightSound.Attributes.Range ||
                    !string.Equals(leftSound.Attributes.Location?.ToString(), rightSound.Attributes.Location?.ToString(), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool VanillaFloatDictionariesEqual(Dictionary<string, float>? left, Dictionary<string, float>? right)
        {
            int leftCount = left?.Count ?? 0;
            int rightCount = right?.Count ?? 0;
            if (leftCount != rightCount) return false;
            if (leftCount == 0) return true;

            foreach ((string key, float value) in left!)
            {
                if (right == null || !right.TryGetValue(key, out float other) || value != other) return false;
            }

            return true;
        }

        private static bool VanillaBlendModeDictionariesEqual(Dictionary<string, EnumAnimationBlendMode>? left, Dictionary<string, EnumAnimationBlendMode>? right)
        {
            int leftCount = left?.Count ?? 0;
            int rightCount = right?.Count ?? 0;
            if (leftCount != rightCount) return false;
            if (leftCount == 0) return true;

            foreach ((string key, EnumAnimationBlendMode value) in left!)
            {
                if (right == null || !right.TryGetValue(key, out EnumAnimationBlendMode other) || value != other) return false;
            }

            return true;
        }

        public VanillaAnimationDocumentSnapshot CaptureCurrent(VanillaAnimationDocument document, string label)
        {
            return FromIndexes(document, label, _animationIndexes, _metadataIndexes);
        }

        public void Restore(VanillaAnimationDocument document)
        {
            int animationCount = Math.Min(_animationIndexes.Length, _animations.Count);
            for (int index = 0; index < animationCount; index++)
            {
                int animationIndex = _animationIndexes[index];
                if (animationIndex < 0 || animationIndex >= document.ShapeAnimations.Count) continue;

                VanillaShapeAnimationEntry entry = document.ShapeAnimations[animationIndex];
                CopyVanillaAnimation(entry.Animation, _animations[index]);
                entry.SourceToken = _animationSourceTokens[index]?.DeepClone();
            }

            int metadataCount = Math.Min(_metadataIndexes.Length, _metadata.Count);
            for (int index = 0; index < metadataCount; index++)
            {
                int metadataIndex = _metadataIndexes[index];
                if (metadataIndex < 0 || metadataIndex >= document.MetadataEntries.Count) continue;
                CopyAnimationMetaData(document.MetadataEntries[metadataIndex].Metadata, _metadata[index]);
                document.MetadataEntries[metadataIndex].SourceToken = _metadataSourceTokens[index]?.DeepClone();
            }
        }

    }

    private sealed class VanillaAnimationEditorHistory
    {
        private const int MaxEntriesPerDocument = 100;

        private readonly Dictionary<string, List<VanillaAnimationDocumentSnapshot>> _undo = new();
        private readonly Dictionary<string, List<VanillaAnimationDocumentSnapshot>> _redo = new();
        private PendingVanillaEdit? _pendingEdit;

        public VanillaAnimationDocumentSnapshot Capture(VanillaAnimationDocument document, string label) => VanillaAnimationDocumentSnapshot.FromDocument(document, label);
        public VanillaAnimationDocumentSnapshot Capture(VanillaAnimationDocument document, string label, VanillaBrowserRow row) => VanillaAnimationDocumentSnapshot.FromDocument(document, label, row);
        public int UndoCount(VanillaAnimationDocument document) => GetStack(_undo, document.HistoryKey).Count;
        public int RedoCount(VanillaAnimationDocument document) => GetStack(_redo, document.HistoryKey).Count;
        public bool HasPendingEdit(VanillaAnimationDocument document) => _pendingEdit?.HistoryKey == document.HistoryKey;

        public bool TryGetPendingDocumentKey(out string? historyKey)
        {
            historyKey = _pendingEdit?.HistoryKey;
            return historyKey != null;
        }

        public void BeginEdit(VanillaAnimationDocument document, VanillaAnimationDocumentSnapshot before)
        {
            if (_pendingEdit?.HistoryKey == document.HistoryKey) return;
            if (_pendingEdit != null) CancelPendingEdit();
            _pendingEdit = new PendingVanillaEdit(document.HistoryKey, before);
        }

        public bool CommitEdit(VanillaAnimationDocument document)
        {
            if (_pendingEdit?.HistoryKey != document.HistoryKey) return false;

            VanillaAnimationDocumentSnapshot entry = _pendingEdit.Before;
            _pendingEdit = null;

            if (entry.Matches(document)) return false;

            Push(_undo, document.HistoryKey, entry);
            GetStack(_redo, document.HistoryKey).Clear();
            return true;
        }

        public void CancelPendingEdit()
        {
            _pendingEdit = null;
        }

        public bool RecordSnapshot(VanillaAnimationDocument document, VanillaAnimationDocumentSnapshot before)
        {
            if (before.Matches(document)) return false;

            List<VanillaAnimationDocumentSnapshot> undo = GetStack(_undo, document.HistoryKey);
            if (undo.Count > 0 && undo[^1].SameContentAs(before)) return false;

            Push(_undo, document.HistoryKey, before);
            GetStack(_redo, document.HistoryKey).Clear();
            return true;
        }

        public bool Undo(VanillaAnimationDocument document, out string status)
        {
            status = "";
            List<VanillaAnimationDocumentSnapshot> undo = GetStack(_undo, document.HistoryKey);
            if (undo.Count == 0)
            {
                status = "Nothing to undo.";
                return false;
            }

            VanillaAnimationDocumentSnapshot target = Pop(undo);
            Push(_redo, document.HistoryKey, target.CaptureCurrent(document, "Redo"));
            target.Restore(document);
            document.UpdateDirtyState();
            status = $"Undid {target.Label}.";
            return true;
        }

        public bool Redo(VanillaAnimationDocument document, out string status)
        {
            status = "";
            List<VanillaAnimationDocumentSnapshot> redo = GetStack(_redo, document.HistoryKey);
            if (redo.Count == 0)
            {
                status = "Nothing to redo.";
                return false;
            }

            VanillaAnimationDocumentSnapshot target = Pop(redo);
            Push(_undo, document.HistoryKey, target.CaptureCurrent(document, "Undo"));
            target.Restore(document);
            document.UpdateDirtyState();
            status = $"Redid {target.Label}.";
            return true;
        }

        public void Clear(VanillaAnimationDocument document)
        {
            GetStack(_undo, document.HistoryKey).Clear();
            GetStack(_redo, document.HistoryKey).Clear();
            if (_pendingEdit?.HistoryKey == document.HistoryKey) _pendingEdit = null;
        }

        public void ClearAll()
        {
            _undo.Clear();
            _redo.Clear();
            _pendingEdit = null;
        }

        private static void Push(Dictionary<string, List<VanillaAnimationDocumentSnapshot>> stacks, string historyKey, VanillaAnimationDocumentSnapshot entry)
        {
            List<VanillaAnimationDocumentSnapshot> stack = GetStack(stacks, historyKey);
            stack.Add(entry);
            if (stack.Count > MaxEntriesPerDocument)
            {
                stack.RemoveRange(0, stack.Count - MaxEntriesPerDocument);
            }
        }

        private static VanillaAnimationDocumentSnapshot Pop(List<VanillaAnimationDocumentSnapshot> stack)
        {
            int index = stack.Count - 1;
            VanillaAnimationDocumentSnapshot entry = stack[index];
            stack.RemoveAt(index);
            return entry;
        }

        private static List<VanillaAnimationDocumentSnapshot> GetStack(Dictionary<string, List<VanillaAnimationDocumentSnapshot>> stacks, string historyKey)
        {
            if (!stacks.TryGetValue(historyKey, out List<VanillaAnimationDocumentSnapshot>? stack))
            {
                stack = [];
                stacks[historyKey] = stack;
            }

            return stack;
        }

        private sealed record PendingVanillaEdit(string HistoryKey, VanillaAnimationDocumentSnapshot Before);
    }

    private sealed class VanillaAnimationExportService
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        public string Export(VanillaAnimationDocument document, bool overwrite)
        {
            try
            {
                string relativePath = Path.Combine("vanilla", "assets", document.Domain, document.AssetPath.Replace('/', Path.DirectorySeparatorChar));
                string outputPath = GetToolAuthoredAssetPath("animations", relativePath);

                if (File.Exists(outputPath) && !overwrite)
                {
                    return $"Export exists: {outputPath}. Enable overwrite exports to replace it.";
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                string json = BuildDocumentJson(document);

                string writeError = WriteAuthoredFile(outputPath, json);
                if (!string.IsNullOrEmpty(writeError))
                {
                    return $"Export failed for {document.DisplayPath}: {writeError}";
                }
                WriteManifest(outputPath, document);
                document.MarkClean();
                return $"Exported {document.DisplayPath} to {outputPath}.";
            }
            catch (Exception exception)
            {
                return $"Export failed for {document.DisplayPath}: {exception.Message}";
            }
        }

        public static string BuildDocumentJson(VanillaAnimationDocument document)
        {
            return document.Kind == VanillaDocumentKind.Shape
                ? BuildShapeExportJson(document)
                : BuildEntityMetadataExportJson(document);
        }

        private static string BuildShapeExportJson(VanillaAnimationDocument document)
        {
            JObject json = document.SourceJson?.DeepClone() as JObject ?? JObject.FromObject(document.Shape!, JsonSerializer.Create(JsonSettings));
            json["animations"] = new JArray(document.ShapeAnimations.Select(entry => ToVanillaAnimationToken(entry.Animation, entry.SourceToken)));
            return JsonConvert.SerializeObject(RemoveEditorPrivateProperties(json), Formatting.Indented, JsonSettings);
        }

        private static string BuildEntityMetadataExportJson(VanillaAnimationDocument document)
        {
            JObject json = document.SourceJson?.DeepClone() as JObject ?? new JObject
            {
                ["code"] = document.EntityCode ?? document.DisplayPath
            };

            JObject client = json["client"] as JObject ?? new JObject();
            json["client"] = client;
            client["animations"] = new JArray(document.MetadataEntries.Select(entry => ToAnimationMetaDataToken(entry.Metadata, entry.SourceToken)));

            return JsonConvert.SerializeObject(RemoveEditorPrivateProperties(json), Formatting.Indented, JsonSettings);
        }

        public static JToken ToVanillaAnimationToken(VanillaAnimation animation, JToken? sourceToken)
        {
            JObject token = sourceToken?.DeepClone() as JObject ?? new JObject();
            token.Remove("quantityFrames");
            token.Remove("quantityframes");
            token.Remove("keyFrames");
            token.Remove("keyframes");

            token["quantityframes"] = animation.QuantityFrames;
            if (!string.IsNullOrWhiteSpace(animation.Name)) token["name"] = animation.Name;
            token["code"] = animation.Code ?? animation.Name ?? "";
            token["version"] = animation.Version;
            token["easeAnimationSpeed"] = animation.EaseAnimationSpeed;
            token["onActivityStopped"] = animation.OnActivityStopped.ToString();
            token["onAnimationEnd"] = animation.OnAnimationEnd.ToString();
            JArray? sourceKeyFrames = GetSourceArray(sourceToken, "keyframes", "keyFrames");
            token["keyframes"] = new JArray((animation.KeyFrames ?? []).Select((keyFrame, index) =>
                ToVanillaKeyFrameToken(keyFrame, GetSourceTokenAt(sourceKeyFrames, index))));
            return token;
        }

        public static JToken ToVanillaKeyFrameToken(AnimationKeyFrame keyFrame, JToken? sourceToken = null)
        {
            JObject token = sourceToken?.DeepClone() as JObject ?? new JObject();
            token.Remove("frame");
            token.Remove("elements");
            token["frame"] = keyFrame.Frame;

            JObject elements = new();
            JObject? sourceElements = sourceToken?["elements"] as JObject;
            foreach ((string name, AnimationKeyFrameElement element) in (keyFrame.Elements ?? new()).OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                elements[name] = ToVanillaElementToken(element, sourceElements?.Property(name, StringComparison.OrdinalIgnoreCase)?.Value);
            }
            token["elements"] = elements;
            return token;
        }

        public static JToken ToVanillaElementToken(AnimationKeyFrameElement element, JToken? sourceToken = null)
        {
            JObject token = sourceToken?.DeepClone() as JObject ?? new JObject();
            RemoveKnownAnimationElementProperties(token);
            (double? offsetX, double? offsetY, double? offsetZ) = CompleteNullableGroup(element.OffsetX, element.OffsetY, element.OffsetZ, 0);
            (double? stretchX, double? stretchY, double? stretchZ) = CompleteNullableGroup(element.StretchX, element.StretchY, element.StretchZ, 1);
            (double? rotationX, double? rotationY, double? rotationZ) = CompleteNullableGroup(element.RotationX, element.RotationY, element.RotationZ, 0);
            (double? originX, double? originY, double? originZ) = CompleteNullableGroup(element.OriginX, element.OriginY, element.OriginZ, 0);

            AddNullable(token, "offsetX", offsetX);
            AddNullable(token, "offsetY", offsetY);
            AddNullable(token, "offsetZ", offsetZ);
            AddNullable(token, "stretchX", stretchX);
            AddNullable(token, "stretchY", stretchY);
            AddNullable(token, "stretchZ", stretchZ);
            AddNullable(token, "rotationX", rotationX);
            AddNullable(token, "rotationY", rotationY);
            AddNullable(token, "rotationZ", rotationZ);
            AddNullable(token, "originX", originX);
            AddNullable(token, "originY", originY);
            AddNullable(token, "originZ", originZ);
            if (element.RotShortestDistanceX) token["rotShortestDistanceX"] = true;
            if (element.RotShortestDistanceY) token["rotShortestDistanceY"] = true;
            if (element.RotShortestDistanceZ) token["rotShortestDistanceZ"] = true;
            return token;
        }

        public static JToken ToAnimationMetaDataToken(AnimationMetaData metadata, JToken? sourceToken)
        {
            JObject token = sourceToken?.DeepClone() as JObject ?? new JObject();
            token["code"] = metadata.Code ?? "";
            token["animation"] = metadata.Animation ?? "";
            token["weight"] = metadata.Weight;
            token["animationSpeed"] = metadata.AnimationSpeed;
            token["mulWithWalkSpeed"] = metadata.MulWithWalkSpeed;
            token["weightCapFactor"] = metadata.WeightCapFactor;
            token["easeInSpeed"] = metadata.EaseInSpeed;
            token["easeOutSpeed"] = metadata.EaseOutSpeed;
            token["blendMode"] = metadata.BlendMode.ToString();
            token["supressDefaultAnimation"] = metadata.SupressDefaultAnimation;
            token["holdEyePosAfterEasein"] = metadata.HoldEyePosAfterEasein;
            token["clientSide"] = metadata.ClientSide;
            token["withFpVariant"] = metadata.WithFpVariant;
            token["adjustCollisionBox"] = metadata.AdjustCollisionBox;

            token["elementWeight"] = JObject.FromObject(metadata.ElementWeight ?? new Dictionary<string, float>(), JsonSerializer.Create(JsonSettings));
            JObject blendModes = new();
            foreach ((string element, EnumAnimationBlendMode mode) in metadata.ElementBlendMode ?? new Dictionary<string, EnumAnimationBlendMode>())
            {
                blendModes[element] = mode.ToString();
            }
            token["elementBlendMode"] = blendModes;

            if (metadata.AnimationSounds != null && metadata.AnimationSounds.Length > 0)
            {
                JArray? sourceSounds = sourceToken?["animationSounds"] as JArray;
                JArray sounds = new();
                for (int index = 0; index < metadata.AnimationSounds.Length; index++)
                {
                    sounds.Add(ToAnimationSoundToken(metadata.AnimationSounds[index], sourceSounds != null && index < sourceSounds.Count ? sourceSounds[index] : null));
                }

                token["animationSounds"] = sounds;
            }
            else
            {
                token.Remove("animationSounds");
            }

            return token;
        }

        public static JToken ToAnimationSoundToken(AnimationSound sound, JToken? sourceToken)
        {
            JObject token = sourceToken?.DeepClone() as JObject ?? new JObject();
            token["frame"] = sound.Frame;
            token["chance"] = sound.Chance;
            token["looping"] = sound.Looping;
            token.Remove("path");
            if (sound.Attributes.Location != null) token["location"] = sound.Attributes.Location.ToString();
            token["range"] = sound.Attributes.Range;
            return token;
        }

        private static void AddNullable(JObject token, string property, double? value)
        {
            if (value.HasValue)
            {
                token[property] = value.Value;
            }
        }

        private static (double? X, double? Y, double? Z) CompleteNullableGroup(double? x, double? y, double? z, double fallback)
        {
            if (!x.HasValue && !y.HasValue && !z.HasValue)
            {
                return (null, null, null);
            }

            return (x ?? fallback, y ?? fallback, z ?? fallback);
        }

        private static JArray? GetSourceArray(JToken? sourceToken, params string[] names)
        {
            foreach (string name in names)
            {
                if (sourceToken?[name] is JArray array) return array;
            }

            return null;
        }

        private static JToken? GetSourceTokenAt(JArray? sourceArray, int index)
        {
            return sourceArray != null && index >= 0 && index < sourceArray.Count ? sourceArray[index] : null;
        }

        private static void RemoveKnownAnimationElementProperties(JObject token)
        {
            token.Remove("offsetX");
            token.Remove("offsetY");
            token.Remove("offsetZ");
            token.Remove("stretchX");
            token.Remove("stretchY");
            token.Remove("stretchZ");
            token.Remove("rotationX");
            token.Remove("rotationY");
            token.Remove("rotationZ");
            token.Remove("originX");
            token.Remove("originY");
            token.Remove("originZ");
            token.Remove("rotShortestDistanceX");
            token.Remove("rotShortestDistanceY");
            token.Remove("rotShortestDistanceZ");
        }

        private static JObject RemoveEditorPrivateProperties(JObject json)
        {
            json.Remove("_assetPath");
            return json;
        }

        private static void WriteManifest(string outputPath, VanillaAnimationDocument document)
        {
            JObject manifest = new()
            {
                ["exportedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["source"] = document.DisplayPath,
                ["kind"] = document.Kind.ToString(),
                ["export"] = outputPath
            };

            File.WriteAllText(outputPath + ".ingamedevtools-manifest.json", manifest.ToString(Formatting.Indented));
        }
    }
}
