using System.Collections;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
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
        string serialized = VanillaAnimationDocumentSerializer.Serialize(document);
        string status = _liveApplyManager.Apply(
            key,
            document.DisplayPath,
            () => CaptureVanillaLiveSnapshot(document),
            () => ApplyVanillaLiveDocument(document),
            () => BuildVanillaAppliedStatus(document));
        if (IsVanillaLiveApplyFailure(status))
        {
            _vanillaLiveAppliedHashes.Remove(key);
        }
        else
        {
            _vanillaLiveAppliedHashes[key] = serialized;
        }

        return status;
    }

    private static bool IsVanillaLiveApplyFailure(string status)
    {
        return status.StartsWith("Live apply failed", StringComparison.OrdinalIgnoreCase);
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
        List<VanillaEntityClientShapeSnapshot> clientSnapshots = GetVanillaRuntimeEntityClients(document)
            .Select(client => new VanillaEntityClientShapeSnapshot(
                client,
                client.LoadedShape,
                client.LoadedShapeForEntity,
                client.LoadedAlternateShapes?.ToArray()))
            .ToList();

        string backupPath = Path.Combine("assets", document.Domain, document.AssetPath.Replace('/', Path.DirectorySeparatorChar));
        return new(
            () =>
            {
                foreach (VanillaShapeRuntimeSnapshot snapshot in snapshots)
                {
                    snapshot.Shape.Animations = CloneVanillaAnimationArray(snapshot.Animations);
                    PrepareRuntimeShapeAnimationLookups(snapshot.Shape);
                }

                foreach (VanillaEntityClientShapeSnapshot snapshot in clientSnapshots)
                {
                    snapshot.Client.LoadedShape = snapshot.LoadedShape;
                    snapshot.Client.LoadedShapeForEntity = snapshot.LoadedShapeForEntity;
                    snapshot.Client.LoadedAlternateShapes = snapshot.LoadedAlternateShapes?.ToArray();
                }

                RefreshLoadedEntityAnimators(document);
            },
            backupPath,
            () => SerializeVanillaAnimationsBackup(snapshots.FirstOrDefault()?.Animations ?? []),
            "animations");
    }

    private DebugWindowManager.LivePatchSnapshot CaptureVanillaMetadataLiveSnapshot(VanillaAnimationDocument document)
    {
        List<VanillaMetadataRuntimeSnapshot> snapshots = GetVanillaRuntimeMetadataTargets(document)
            .Select(target => new VanillaMetadataRuntimeSnapshot(target.EntityType, target.Client, CloneAnimationMetaDataArray(target.Client.Animations)))
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
                }

                RefreshLoadedEntityAnimators(document);
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
            Dictionary<Shape, Shape> editedShapeCache = new(ReferenceEqualityComparer.Instance);
            bool shapeApplied = false;
            foreach (EntityClientProperties client in GetVanillaRuntimeEntityClients(document))
            {
                shapeApplied |= ApplyVanillaAnimationsToRuntimeClientShapes(client, edited, document.DisplayPath, editedShapeCache);
            }

            if (!shapeApplied && document.Shape != null)
            {
                ApplyVanillaAnimationsToRuntimeShape(document.Shape, edited);
            }

            return;
        }

        AnimationMetaData[] editedMetadata = document.MetadataEntries.Select(entry => CloneAnimationMetaData(entry.Metadata)).ToArray();
        bool applied = false;
        foreach (VanillaMetadataRuntimeTarget target in GetVanillaRuntimeMetadataTargets(document))
        {
            target.Client.Animations = CloneAnimationMetaDataArray(editedMetadata);
            RebuildRuntimeMetadataLookups(target.Client);
            applied = true;
        }

        if (!applied)
        {
            throw new InvalidOperationException("Entity has no client properties.");
        }
    }

    private string BuildVanillaAppliedStatus(VanillaAnimationDocument document)
    {
        VanillaRuntimeRefreshResult refresh = RefreshLoadedEntityAnimators(document);

        int targetCount = GetVanillaRuntimeEntityTargets(document).Count();
        string baseStatus = targetCount > 1
            ? $"Live applied {document.DisplayPath} to {targetCount} compatible group target(s){(document.RuntimeSkippedMembers > 0 ? $"; skipped {document.RuntimeSkippedMembers}" : "")}."
            : $"Live applied {document.DisplayPath}.";
        string failureText = refresh.Failures.Count == 0
            ? ""
            : $" Rebuild failures: {string.Join("; ", refresh.Failures.Take(3))}{(refresh.Failures.Count > 3 ? $"; +{refresh.Failures.Count - 3} more" : "")}.";
        if (refresh.Matched == 0) return $"{baseStatus} Applied to future starts only.{failureText}";
        if (refresh.Refreshed < refresh.Matched) return $"{baseStatus} Queued retessellation for {refresh.Refreshed}/{refresh.Matched} loaded entity renderer(s); some apply to future starts only.{failureText}";
        return $"{baseStatus} Queued retessellation for {refresh.Refreshed} loaded entity renderer instance(s).{failureText}";
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

            foreach (Entity entity in GetLoadedEntitiesForVanillaRuntimeTarget(entityType))
            {
                foreach (Shape? shape in new[]
                {
                    entity.Properties?.Client?.LoadedShape,
                    entity.Properties?.Client?.LoadedShapeForEntity
                })
                {
                    if (shape == null || !seen.Add(shape)) continue;
                    yield return shape;
                }
            }
        }
    }

    private IEnumerable<EntityClientProperties> GetVanillaRuntimeEntityClients(VanillaAnimationDocument document)
    {
        HashSet<EntityClientProperties> seen = [];
        foreach (EntityProperties entityType in GetVanillaRuntimeEntityTargets(document))
        {
            EntityClientProperties? client = entityType.Client;
            if (client != null && seen.Add(client))
            {
                yield return client;
            }

            foreach (Entity entity in GetLoadedEntitiesForVanillaRuntimeTarget(entityType))
            {
                client = entity.Properties?.Client;
                if (client != null && seen.Add(client))
                {
                    yield return client;
                }
            }
        }
    }

    private IEnumerable<VanillaMetadataRuntimeTarget> GetVanillaRuntimeMetadataTargets(VanillaAnimationDocument document)
    {
        HashSet<EntityClientProperties> seen = [];
        foreach (EntityProperties entityType in GetVanillaRuntimeEntityTargets(document))
        {
            if (entityType.Client != null && seen.Add(entityType.Client))
            {
                yield return new VanillaMetadataRuntimeTarget(entityType, entityType.Client);
            }

            foreach (Entity entity in GetLoadedEntitiesForVanillaRuntimeTarget(entityType))
            {
                EntityClientProperties? client = entity.Properties?.Client;
                if (client != null && seen.Add(client))
                {
                    yield return new VanillaMetadataRuntimeTarget(entityType, client);
                }
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
        shape.JointsById.Clear();
        shape.InitForAnimations(_api.World.Logger, label);
        PrepareRuntimeShapeAnimationLookups(shape);
    }

    private static void PrepareRuntimeShapeAnimationLookups(Shape shape)
    {
        foreach (VanillaAnimation animation in shape.Animations ?? [])
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
        }

        RebuildRuntimeShapeAnimationLookups(shape);
    }

    private void ApplyVanillaAnimationsToRuntimeShape(Shape target, VanillaAnimation[] edited)
    {
        target.Animations = CloneVanillaAnimationArray(edited);
        PrepareRuntimeShapeAnimationLookups(target);
    }

    private bool ApplyVanillaAnimationsToRuntimeClientShapes(EntityClientProperties client, VanillaAnimation[] edited, string label, Dictionary<Shape, Shape> editedShapeCache)
    {
        bool applied = false;
        if (client.LoadedShape != null)
        {
            client.LoadedShape = GetOrCreateEditedRuntimeShape(client.LoadedShape, edited, label, editedShapeCache);
            applied = true;
        }

        if (client.LoadedShapeForEntity != null)
        {
            client.LoadedShapeForEntity = GetOrCreateEditedRuntimeShape(client.LoadedShapeForEntity, edited, label, editedShapeCache);
            applied = true;
        }

        if (client.LoadedAlternateShapes != null)
        {
            Shape[] editedAlternates = new Shape[client.LoadedAlternateShapes.Length];
            for (int index = 0; index < client.LoadedAlternateShapes.Length; index++)
            {
                Shape alternate = client.LoadedAlternateShapes[index];
                editedAlternates[index] = GetOrCreateEditedRuntimeShape(alternate, edited, label, editedShapeCache);
            }

            client.LoadedAlternateShapes = editedAlternates;
            applied = true;
        }

        return applied;
    }

    private Shape GetOrCreateEditedRuntimeShape(Shape source, VanillaAnimation[] edited, string label, Dictionary<Shape, Shape> editedShapeCache)
    {
        if (editedShapeCache.TryGetValue(source, out Shape? existing))
        {
            return existing;
        }

        Shape clone = source.Clone() ?? throw new InvalidOperationException($"Could not clone runtime shape for {label}.");
        clone.Animations = CloneVanillaAnimationArray(edited);
        PrepareRuntimeShapeAnimations(clone, label);
        editedShapeCache[source] = clone;
        return clone;
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

    private VanillaRuntimeRefreshResult RefreshLoadedEntityAnimators(VanillaAnimationDocument document)
    {
        VanillaRuntimeRefreshResult total = new();
        foreach (EntityProperties entityType in GetVanillaRuntimeEntityTargets(document))
        {
            total.Add(RefreshLoadedEntityAnimators(entityType, document.DisplayPath));
        }

        return total;
    }

    private VanillaRuntimeRefreshResult RefreshLoadedEntityAnimators(EntityProperties? entityType, string label)
    {
        VanillaRuntimeRefreshResult result = new();
        if (entityType?.Code == null || _api.World is not IClientWorldAccessor clientWorld)
        {
            return result;
        }

        foreach (Entity entity in clientWorld.LoadedEntities.Values)
        {
            if (!string.Equals(entity.Properties?.Code?.ToString(), entityType.Code.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
            result.Matched++;
            if (entity.AnimManager == null)
            {
                result.Failures.Add($"{entity.Code}: missing animation manager");
                continue;
            }

            try
            {
                if (!TryResolveRuntimeEntityShape(entity, entityType, out Shape? entityShape, out string missingShapeReason) || entityShape == null)
                {
                    result.Failures.Add($"{entity.Code}: {missingShapeReason}");
                    continue;
                }

                if (entity.Properties?.Client?.Renderer is not EntityShapeRenderer renderer)
                {
                    result.Failures.Add($"{entity.Code}: renderer is not an entity shape renderer");
                    continue;
                }

                ClearRuntimeAnimationCache(entity);
                entity.AnimManager.AnimationsDirty = true;
                entity.MarkShapeModified();
                renderer.TesselateShape();
                result.Refreshed++;
            }
            catch (Exception exception)
            {
                result.Failures.Add($"{entity.Code}: {exception.Message}");
            }
        }

        return result;
    }

    private IEnumerable<Entity> GetLoadedEntitiesForVanillaRuntimeTarget(EntityProperties entityType)
    {
        if (entityType.Code == null || _api.World is not IClientWorldAccessor clientWorld) yield break;

        foreach (Entity entity in clientWorld.LoadedEntities.Values)
        {
            if (string.Equals(entity.Properties?.Code?.ToString(), entityType.Code.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                yield return entity;
            }
        }
    }

    private static bool TryResolveRuntimeEntityShape(Entity entity, EntityProperties entityType, out Shape? shape, out string failureReason)
    {
        EntityClientProperties? client = entity.Properties?.Client ?? entityType.Client;
        if (client == null)
        {
            shape = null;
            failureReason = "missing client properties";
            return false;
        }

        shape = client.LoadedShapeForEntity;
        if (shape != null)
        {
            failureReason = "";
            return true;
        }

        failureReason = client.LoadedShape != null
            ? "missing entity render shape; LoadedShape exists but renderer retessellates from LoadedShapeForEntity"
            : "missing loaded shape";
        return false;
    }

    private void ClearRuntimeAnimationCache(Entity entity)
    {
        try
        {
            AnimationCache.ClearCache(_api, entity);
        }
        catch
        {
            AnimationCache.ClearCache(_api);
        }
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
    private sealed record VanillaEntityClientShapeSnapshot(EntityClientProperties Client, Shape? LoadedShape, Shape? LoadedShapeForEntity, Shape[]? LoadedAlternateShapes);
    private sealed record VanillaMetadataRuntimeSnapshot(EntityProperties EntityType, EntityClientProperties Client, AnimationMetaData[] Animations);
    private sealed record VanillaMetadataRuntimeTarget(EntityProperties EntityType, EntityClientProperties Client);

    private sealed class VanillaRuntimeRefreshResult
    {
        public int Matched;
        public int Refreshed;
        public List<string> Failures { get; } = [];

        public void Add(VanillaRuntimeRefreshResult other)
        {
            Matched += other.Matched;
            Refreshed += other.Refreshed;
            Failures.AddRange(other.Failures);
        }
    }
}
