using System.Collections;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using VanillaAnimation = Vintagestory.API.Common.Animation;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private readonly Dictionary<string, string> _vanillaLiveAppliedHashes = new(StringComparer.Ordinal);

    private void TrackVanillaLiveOriginals()
    {
        foreach (VanillaAnimationDocument document in _vanillaIndex.Documents)
        {
            string key = GetVanillaLiveKey(document);
            _liveApplyManager.TrackOriginal(key, document.DisplayPath, () => CaptureVanillaLiveSnapshot(document));
        }
    }

    private void DrawVanillaLiveControls(VanillaAnimationDocument document, VanillaBrowserRow row)
    {
        string key = GetVanillaLiveKey(document);
        bool available = IsVanillaLiveTargetAvailable(document);
        _liveApplyManager.DrawRuntimeStatus(
            $"vanilla-live-{document.HistoryKey}",
            key,
            document.DisplayPath,
            available,
            () =>
            {
                string status = _liveApplyManager.Revert(key);
                _vanillaLiveAppliedHashes.Remove(key);
                RefreshVanillaPreviewAfterEdit(row);
                return status;
            });
        if (!available)
        {
            return;
        }

        if (_liveApplyManager.AutoApply)
        {
            ImGui.TextWrapped("Runtime apply is enabled in the top toolbar. Edits are applied automatically.");
        }
        else
        {
            ImGui.TextWrapped("Enable Runtime apply in the top toolbar to apply edits automatically.");
        }

        int targetCount = GetVanillaRuntimeEntityTargets(document).Count();
        if (targetCount > 1 || document.RuntimeSkippedMembers > 0)
        {
            ImGui.TextDisabled($"Group targets: {targetCount}{(document.RuntimeSkippedMembers > 0 ? $"; skipped incompatible: {document.RuntimeSkippedMembers}" : "")}");
        }
    }

    private void ApplyAllDirtyVanillaLive(bool force = false)
    {
        List<VanillaAnimationDocument> dirty = _vanillaIndex.Documents.Where(document => document.Dirty).ToList();
        if (dirty.Count == 0)
        {
            if (force)
            {
                _vanillaStatus = "Runtime apply enabled. Future edits will apply automatically.";
            }
            else
            {
                _vanillaStatus = "No dirty vanilla documents to apply live.";
            }
            return;
        }

        List<string> statuses = [];
        foreach (VanillaAnimationDocument document in dirty)
        {
            statuses.Add(ApplyVanillaLive(document, force));
        }

        _vanillaStatus = string.Join(Environment.NewLine, statuses);
        if (FindVanillaBrowserRow(_vanillaSelection.RowKey) is { } row)
        {
            RefreshVanillaPreviewAfterEdit(row);
        }
    }

    private void AutoApplyVanillaDocument(VanillaAnimationDocument document, bool force = false)
    {
        if (!_liveApplyManager.AutoApply || !IsVanillaLiveTargetAvailable(document)) return;

        string key = GetVanillaLiveKey(document);
        string serialized = VanillaAnimationDocumentSerializer.Serialize(document);
        if (!force &&
            _vanillaLiveAppliedHashes.TryGetValue(key, out string? lastApplied) &&
            string.Equals(lastApplied, serialized, StringComparison.Ordinal))
        {
            return;
        }

        _vanillaStatus = ApplyVanillaLive(document, force);
    }

    private string ApplyVanillaLive(VanillaAnimationDocument document, bool force = false)
    {
        if (!IsVanillaLiveTargetAvailable(document))
        {
            _liveApplyManager.LastStatus = "Live target unavailable for this vanilla document.";
            return _liveApplyManager.LastStatus;
        }

        string key = GetVanillaLiveKey(document);
        string status = _liveApplyManager.Apply(
            key,
            document.DisplayPath,
            () => CaptureVanillaLiveSnapshot(document),
            () => ApplyVanillaLiveDocument(document),
            () => BuildVanillaAppliedStatus(document));
        _vanillaLiveAppliedHashes[key] = VanillaAnimationDocumentSerializer.Serialize(document);
        return status;
    }

    private void ClearVanillaLiveApplyState()
    {
        _vanillaLiveAppliedHashes.Clear();
    }

    private static string GetVanillaLiveKey(VanillaAnimationDocument document)
    {
        return document.Kind == VanillaDocumentKind.Shape
            ? $"vanilla:shape:{document.HistoryKey}"
            : $"vanilla:metadata:{document.HistoryKey}";
    }

    private static bool IsVanillaLiveTargetAvailable(VanillaAnimationDocument document)
    {
        return document.Kind switch
        {
            VanillaDocumentKind.Shape => document.Shape != null && document.ShapeAnimations.Count > 0,
            VanillaDocumentKind.EntityMetadata => document.EntityType?.Client != null,
            _ => false
        };
    }

    private DebugWindowManager.LivePatchSnapshot CaptureVanillaLiveSnapshot(VanillaAnimationDocument document)
    {
        return document.Kind == VanillaDocumentKind.Shape
            ? CaptureVanillaShapeLiveSnapshot(document)
            : CaptureVanillaMetadataLiveSnapshot(document);
    }

    private DebugWindowManager.LivePatchSnapshot CaptureVanillaShapeLiveSnapshot(VanillaAnimationDocument document)
    {
        Shape[] targets = GetVanillaRuntimeShapes(document).ToArray();
        List<VanillaShapeRuntimeSnapshot> snapshots = targets
            .Select(shape => new VanillaShapeRuntimeSnapshot(shape, CloneVanillaAnimationArray(shape.Animations)))
            .ToList();

        string backupPath = Path.Combine("assets", document.Domain, document.AssetPath.Replace('/', Path.DirectorySeparatorChar));
        return new(
            () =>
            {
                foreach (VanillaShapeRuntimeSnapshot snapshot in snapshots)
                {
                    snapshot.Shape.Animations = CloneVanillaAnimationArray(snapshot.Animations);
                    PrepareRuntimeShapeAnimations(snapshot.Shape, document.DisplayPath);
                }
            },
            backupPath,
            () => SerializeVanillaAnimationsBackup(snapshots.FirstOrDefault()?.Animations ?? []),
            "animations");
    }

    private DebugWindowManager.LivePatchSnapshot CaptureVanillaMetadataLiveSnapshot(VanillaAnimationDocument document)
    {
        List<VanillaMetadataRuntimeSnapshot> snapshots = GetVanillaRuntimeEntityTargets(document)
            .Where(entityType => entityType.Client != null)
            .Select(entityType => new VanillaMetadataRuntimeSnapshot(entityType, entityType.Client!, CloneAnimationMetaDataArray(entityType.Client!.Animations)))
            .ToList();
        if (snapshots.Count == 0)
        {
            throw new InvalidOperationException("Entity has no client properties.");
        }

        string backupPath = Path.Combine("assets", document.Domain, document.AssetPath.Replace('/', Path.DirectorySeparatorChar));
        return new(
            () =>
            {
                foreach (VanillaMetadataRuntimeSnapshot snapshot in snapshots)
                {
                    snapshot.Client.Animations = CloneAnimationMetaDataArray(snapshot.Animations);
                    RebuildRuntimeMetadataLookups(snapshot.Client);
                    RefreshLoadedEntityAnimators(snapshot.EntityType, out _);
                }
            },
            backupPath,
            () => SerializeVanillaMetadataBackup(snapshots.FirstOrDefault()?.Animations ?? []),
            "animations");
    }

    private void ApplyVanillaLiveDocument(VanillaAnimationDocument document)
    {
        if (document.Kind == VanillaDocumentKind.Shape)
        {
            VanillaAnimation[] edited = document.ShapeAnimations.Select(entry => CloneVanillaAnimation(entry.Animation)).ToArray();
            foreach (Shape target in GetVanillaRuntimeShapes(document))
            {
                target.Animations = CloneVanillaAnimationArray(edited);
                PrepareRuntimeShapeAnimations(target, document.DisplayPath);
            }

            return;
        }

        AnimationMetaData[] editedMetadata = document.MetadataEntries.Select(entry => CloneAnimationMetaData(entry.Metadata)).ToArray();
        bool applied = false;
        foreach (EntityProperties entityType in GetVanillaRuntimeEntityTargets(document))
        {
            EntityClientProperties? client = entityType.Client;
            if (client == null) continue;
            client.Animations = CloneAnimationMetaDataArray(editedMetadata);
            RebuildRuntimeMetadataLookups(client);
            applied = true;
        }

        if (!applied)
        {
            throw new InvalidOperationException("Entity has no client properties.");
        }
    }

    private string BuildVanillaAppliedStatus(VanillaAnimationDocument document)
    {
        int refreshed = 0;
        int matched = 0;
        foreach (EntityProperties entityType in GetVanillaRuntimeEntityTargets(document))
        {
            refreshed += RefreshLoadedEntityAnimators(entityType, out int targetMatched);
            matched += targetMatched;
        }

        int targetCount = GetVanillaRuntimeEntityTargets(document).Count();
        string baseStatus = targetCount > 1
            ? $"Live applied {document.DisplayPath} to {targetCount} compatible group target(s){(document.RuntimeSkippedMembers > 0 ? $"; skipped {document.RuntimeSkippedMembers}" : "")}."
            : $"Live applied {document.DisplayPath}.";
        if (matched == 0) return $"{baseStatus} Applied to future starts only.";
        if (refreshed < matched) return $"{baseStatus} Refreshed {refreshed}/{matched} loaded entities; some apply to future starts only.";
        return $"{baseStatus} Refreshed {refreshed} loaded entity instance(s).";
    }

    private IEnumerable<Shape> GetVanillaRuntimeShapes(VanillaAnimationDocument document)
    {
        HashSet<Shape> seen = [];
        foreach (Shape? shape in new[]
        {
            document.Shape
        })
        {
            if (shape == null || !seen.Add(shape)) continue;
            yield return shape;
        }

        foreach (EntityProperties entityType in GetVanillaRuntimeEntityTargets(document))
        {
            foreach (Shape? shape in new[]
            {
                entityType.Client?.LoadedShape,
                entityType.Client?.LoadedShapeForEntity
            })
            {
                if (shape == null || !seen.Add(shape)) continue;
                yield return shape;
            }
        }
    }

    private static IEnumerable<EntityProperties> GetVanillaRuntimeEntityTargets(VanillaAnimationDocument document)
    {
        if (document.RuntimeTargetEntities.Count > 0)
        {
            return document.RuntimeTargetEntities;
        }

        return document.EntityType != null ? [document.EntityType] : [];
    }

    private void PrepareRuntimeShapeAnimations(Shape shape, string label)
    {
        try
        {
            Dictionary<string, ShapeElement> elementsByName = shape.CollectAndResolveReferences(_api.World.Logger, label);
            shape.CacheInvTransforms();
            shape.ResolveAndFindJoints(_api.World.Logger, label, elementsByName);
        }
        catch
        {
            // Loaded runtime shapes are usually already resolved. Frame generation below is the critical step.
        }

        foreach (VanillaAnimation animation in shape.Animations ?? [])
        {
            PrepareLiveAnimationFrames(shape, animation);
        }

        RebuildRuntimeShapeAnimationLookups(shape);
    }

    private static void RebuildRuntimeShapeAnimationLookups(Shape shape)
    {
        Dictionary<string, VanillaAnimation> byCode = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<uint, VanillaAnimation> byCrc = [];
        foreach (VanillaAnimation animation in shape.Animations ?? [])
        {
            string code = animation.Code ?? animation.Name ?? "";
            if (string.IsNullOrWhiteSpace(code)) continue;
            byCode[code] = animation;
            byCrc[ToAnimationCrc(code)] = animation;
        }

        AssignDictionaryMember(shape, "AnimationsByCode", byCode);
        AssignDictionaryMember(shape, "AnimationsByCrc32", byCrc);
    }

    private static void PrepareLiveAnimationFrames(Shape shape, VanillaAnimation animation)
    {
        if (string.IsNullOrWhiteSpace(animation.Code)) animation.Code = animation.Name;
        if (animation.KeyFrames != null)
        {
            foreach (AnimationKeyFrame keyFrame in animation.KeyFrames)
            {
                if (keyFrame.Elements == null) continue;
                foreach (AnimationKeyFrameElement element in keyFrame.Elements.Values)
                {
                    CompleteVanillaElementTransformGroups(element);
                }
            }
        }

        animation.GenerateAllFrames(shape.Elements, shape.JointsById);
    }

    private static void RebuildRuntimeMetadataLookups(EntityClientProperties client)
    {
        foreach (AnimationMetaData metadata in client.Animations ?? [])
        {
            metadata.Init();
        }

        Dictionary<string, AnimationMetaData> byCode = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<uint, AnimationMetaData> byCrc = [];
        foreach (AnimationMetaData metadata in client.Animations ?? [])
        {
            string code = metadata.Code ?? metadata.Animation ?? "";
            if (string.IsNullOrWhiteSpace(code)) continue;
            byCode[code] = metadata;
            byCrc[ToAnimationCrc(code)] = metadata;
        }

        AssignDictionaryMember(client, "AnimationsByMetaCode", byCode);
        AssignDictionaryMember(client, "AnimationsByCrc32", byCrc);
    }

    private int RefreshLoadedEntityAnimators(EntityProperties? entityType, out int matched)
    {
        matched = 0;
        if (entityType?.Code == null || _api.World is not IClientWorldAccessor clientWorld)
        {
            return 0;
        }

        int refreshed = 0;
        foreach (Entity entity in clientWorld.LoadedEntities.Values)
        {
            if (!string.Equals(entity.Properties?.Code?.ToString(), entityType.Code.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
            matched++;
            if (entity.AnimManager == null) continue;

            try
            {
                entity.AnimManager.AnimationsDirty = true;
                entity.AnimManager.Init(_api, entity);
                refreshed++;
            }
            catch
            {
                try
                {
                    entity.AnimManager.AnimationsDirty = true;
                }
                catch
                {
                    // Best-effort refresh only. Future animation starts still see patched entity type data.
                }
            }
        }

        return refreshed;
    }

    private static void AssignDictionaryMember(object target, string memberName, IDictionary source)
    {
        System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;
        System.Reflection.MemberInfo? member = target.GetType().GetMember(memberName, flags).FirstOrDefault();
        object? dictionary = member switch
        {
            System.Reflection.FieldInfo field => field.GetValue(target),
            System.Reflection.PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(target),
            _ => null
        };

        if (dictionary is IDictionary existing)
        {
            existing.Clear();
            foreach (DictionaryEntry entry in source)
            {
                existing[entry.Key] = entry.Value;
            }
            return;
        }

        if (member is System.Reflection.FieldInfo settableField)
        {
            settableField.SetValue(target, source);
        }
        else if (member is System.Reflection.PropertyInfo { CanWrite: true } settableProperty)
        {
            settableProperty.SetValue(target, source);
        }
    }

    private static uint ToAnimationCrc(string code)
    {
        return GameMath.Crc32(code.ToLowerInvariant()) & int.MaxValue;
    }

    private static VanillaAnimation[] CloneVanillaAnimationArray(VanillaAnimation[]? animations)
    {
        return animations?.Select(CloneVanillaAnimation).ToArray() ?? [];
    }

    private static AnimationMetaData[] CloneAnimationMetaDataArray(AnimationMetaData[]? animations)
    {
        return animations?.Select(CloneAnimationMetaData).ToArray() ?? [];
    }

    private static string SerializeVanillaAnimationsBackup(VanillaAnimation[] animations)
    {
        JArray array = new(animations.Select(animation => VanillaAnimationExportService.ToVanillaAnimationToken(animation, null)));
        return new JObject { ["animations"] = array }.ToString(Formatting.Indented);
    }

    private static string SerializeVanillaMetadataBackup(AnimationMetaData[] animations)
    {
        JArray array = new(animations.Select(metadata => VanillaAnimationExportService.ToAnimationMetaDataToken(metadata, null)));
        return new JObject { ["client"] = new JObject { ["animations"] = array } }.ToString(Formatting.Indented);
    }

    private sealed record VanillaShapeRuntimeSnapshot(Shape Shape, VanillaAnimation[] Animations);
    private sealed record VanillaMetadataRuntimeSnapshot(EntityProperties EntityType, EntityClientProperties Client, AnimationMetaData[] Animations);
}
