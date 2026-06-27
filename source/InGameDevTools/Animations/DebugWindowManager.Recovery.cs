using InGameDevTools.Utils;
using ImGuiNET;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using NVector2 = System.Numerics.Vector2;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private readonly HashSet<string> _legacyModelRecoveryPrunedForKeys = new(StringComparer.Ordinal);
    private bool _legacyModelRecoveryPrefixPruned;

    private void DrawRecoveryBanner()
    {
        PruneLegacyModelPartialPathSnapshotsOnce();
        IReadOnlyList<DevToolsRecoverySnapshot> snapshots = _recoveryManager.ListSnapshots();
        if (snapshots.Count == 0) return;

        ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.86f, 0.30f, 1f));
        ImGui.TextUnformatted(DevToolsLang.Get("ui.recovery.banner", "Unsaved DevTools recovery available."));
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.SmallButton(DevToolsLang.Label("ui.recovery.review", "Review", "recovery-banner-review")))
        {
            _openRecoveryReviewPopup = true;
        }
    }

    private void DrawRecoveryReviewPopup()
    {
        PruneLegacyModelPartialPathSnapshotsOnce();
        string popupId = DevToolsLang.Label("ui.recovery.window", "DevTools recovery", "recovery-review");
        if (_openRecoveryReviewPopup)
        {
            ImGui.OpenPopup(popupId);
            _openRecoveryReviewPopup = false;
        }

        bool open = true;
        ImGui.SetNextWindowSize(new NVector2(920f, 520f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(popupId, ref open))
        {
            return;
        }

        IReadOnlyList<DevToolsRecoverySnapshot> snapshots = _recoveryManager.ListSnapshots();
        if (snapshots.Count == 0)
        {
            ImGui.TextWrapped(DevToolsLang.Get("ui.recovery.empty", "No recovery snapshots are pending."));
        }
        else
        {
            ImGui.TextWrapped(DevToolsLang.Get("ui.recovery.explain", "Restore loads the recovered draft back into its editor when possible. Discard deletes only the recovery snapshot."));
            ImGui.Separator();

            if (ImGui.BeginTable("##recovery-table", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, new NVector2(-float.Epsilon, 380f)))
            {
                ImGui.TableSetupColumn(DevToolsLang.Get("ui.recovery.editor", "Editor"), ImGuiTableColumnFlags.WidthFixed, 130f);
                ImGui.TableSetupColumn(DevToolsLang.Get("ui.recovery.document", "Document"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(DevToolsLang.Get("ui.recovery.target", "Target"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(DevToolsLang.Get("ui.recovery.updated", "Updated"), ImGuiTableColumnFlags.WidthFixed, 150f);
                ImGui.TableSetupColumn(DevToolsLang.Get("ui.recovery.actions", "Actions"), ImGuiTableColumnFlags.WidthFixed, 190f);
                ImGui.TableHeadersRow();

                foreach (DevToolsRecoverySnapshot snapshot in snapshots)
                {
                    ImGui.PushID(snapshot.RecoveryKey);
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextWrapped(snapshot.Editor);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextWrapped(snapshot.DocumentLabel);
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextWrapped(snapshot.TargetPath);
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(snapshot.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                    ImGui.TableSetColumnIndex(4);

                    if (ImGui.SmallButton(DevToolsLang.Get("ui.recovery.restore", "Restore")))
                    {
                        if (TryRestoreRecoverySnapshot(snapshot, out string status))
                        {
                            _settingsStatus = status;
                            _recoveryManager.Discard(snapshot.RecoveryKey);
                        }
                        else
                        {
                            _settingsStatus = status;
                        }
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton(DevToolsLang.Get("ui.recovery.discard", "Discard")))
                    {
                        _recoveryManager.Discard(snapshot.RecoveryKey);
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton(DevToolsLang.Get("ui.recovery.copy", "Copy")))
                    {
                        ImGui.SetClipboardText(snapshot.Text ?? snapshot.BinaryBase64 ?? "");
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
        }

        if (ImGui.Button(DevToolsLang.Label("ui.common.close", "Close", "recovery-close")))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(DevToolsLang.Label("ui.recovery.openFolder", "Open recovery folder", "recovery-open-folder")))
        {
            OpenDevToolsFolder(_recoveryManager.Root);
        }

        if (!open) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void UpdateDevToolsRecoveryAutosaves()
    {
        if (!_devToolsConfig.EnableRecoveryAutosave) return;

        TimeSpan delay = TimeSpan.FromSeconds(_devToolsConfig.RecoveryAutosaveDelaySeconds);
        TrackModelRecovery(delay);
        TrackVanillaAnimationRecovery(delay);
        TrackBlockItemJsonRecovery(delay);
        TrackWorldgenRecovery(delay);
        TrackAiBehaviorRecovery(delay);
        TrackPatchCreatorRecovery(delay);
        TrackConfigLibRecovery(delay);
        TrackLootDropRecovery(delay);
        TrackTransformRecovery(delay);
        TrackTexturePaintRecovery(delay);
        _recipeEditor.TrackRecovery(_recoveryManager, delay);
    }

    private void TrackModelRecovery(TimeSpan delay)
    {
        if (_modelDoc == null) return;

        string targetPath = "";
        try
        {
            string path = EnsureJsonFilePath((_modelDoc.AssetPath ?? "").Trim().Replace('\\', '/'));
            if (!path.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase)) path = "shapes/" + path.TrimStart('/');
            targetPath = GetToolAuthoredAssetPath("models", Path.Combine("assets", _modelDoc.Domain, path.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            targetPath = _modelDoc.DisplayPath;
        }

        string recoveryDocumentKey = ModelEnsureRecoveryKey(_modelDoc);
        PruneLegacyModelRecoverySnapshots(_modelDoc, recoveryDocumentKey);

        _recoveryManager.TrackText(
            "Models",
            recoveryDocumentKey,
            _modelDoc.DisplayPath,
            targetPath,
            ModelSerializeDocument(_modelDoc, includeInvisible: true, indented: true),
            _modelDoc.Dirty,
            delay);
    }

    private static string ModelEnsureRecoveryKey(ModelDocumentData doc)
    {
        if (string.IsNullOrWhiteSpace(doc.RecoveryKey))
        {
            doc.RecoveryKey = $"model-session:{Guid.NewGuid():N}";
        }

        return doc.RecoveryKey;
    }

    private void PruneLegacyModelRecoverySnapshots(ModelDocumentData doc, string currentDocumentKey)
    {
        if (!_legacyModelRecoveryPrunedForKeys.Add(currentDocumentKey)) return;

        string currentRecoveryKey = DevToolsRecoveryManager.BuildRecoveryKey("Models", currentDocumentKey);
        string currentDisplay = NormalizeRecoveryModelPath(doc.DisplayPath);
        if (string.IsNullOrWhiteSpace(currentDisplay)) return;

        _recoveryManager.DiscardWhere(snapshot =>
        {
            if (!string.Equals(snapshot.Editor, "Models", StringComparison.Ordinal)) return false;
            if (string.Equals(snapshot.RecoveryKey, currentRecoveryKey, StringComparison.Ordinal)) return false;

            string oldDocumentKey = snapshot.RecoveryKey.Split("::", 2, StringSplitOptions.None).ElementAtOrDefault(1) ?? "";
            if (oldDocumentKey.StartsWith("model-session:", StringComparison.OrdinalIgnoreCase)) return false;

            string oldDisplay = NormalizeRecoveryModelPath(!string.IsNullOrWhiteSpace(snapshot.DocumentLabel) ? snapshot.DocumentLabel : oldDocumentKey);
            return oldDisplay.Length > 0 && (currentDisplay.StartsWith(oldDisplay, StringComparison.OrdinalIgnoreCase) || oldDisplay.StartsWith(currentDisplay, StringComparison.OrdinalIgnoreCase));
        });
    }

    private void PruneLegacyModelPartialPathSnapshotsOnce()
    {
        if (_legacyModelRecoveryPrefixPruned) return;
        _legacyModelRecoveryPrefixPruned = true;

        List<(DevToolsRecoverySnapshot Snapshot, string Display)> legacy = _recoveryManager.ListSnapshots()
            .Select(snapshot => (Snapshot: snapshot, Display: LegacyModelRecoveryDisplay(snapshot)))
            .Where(item => item.Display.Length > 0)
            .ToList();

        HashSet<string> discard = new(StringComparer.Ordinal);
        for (int left = 0; left < legacy.Count; left++)
        {
            for (int right = left + 1; right < legacy.Count; right++)
            {
                DevToolsRecoverySnapshot a = legacy[left].Snapshot;
                DevToolsRecoverySnapshot b = legacy[right].Snapshot;
                if (!string.Equals(a.ContentSha256, b.ContentSha256, StringComparison.OrdinalIgnoreCase)) continue;

                string aDisplay = legacy[left].Display;
                string bDisplay = legacy[right].Display;
                if (!aDisplay.StartsWith(bDisplay, StringComparison.OrdinalIgnoreCase) &&
                    !bDisplay.StartsWith(aDisplay, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DevToolsRecoverySnapshot keep = aDisplay.Length != bDisplay.Length
                    ? (aDisplay.Length > bDisplay.Length ? a : b)
                    : (a.UpdatedUtc >= b.UpdatedUtc ? a : b);
                DevToolsRecoverySnapshot remove = ReferenceEquals(keep, a) ? b : a;
                discard.Add(remove.RecoveryKey);
            }
        }

        foreach (string recoveryKey in discard)
        {
            _recoveryManager.Discard(recoveryKey);
        }
    }

    private static string LegacyModelRecoveryDisplay(DevToolsRecoverySnapshot snapshot)
    {
        if (!string.Equals(snapshot.Editor, "Models", StringComparison.Ordinal)) return "";

        string oldDocumentKey = snapshot.RecoveryKey.Split("::", 2, StringSplitOptions.None).ElementAtOrDefault(1) ?? "";
        if (oldDocumentKey.StartsWith("model-session:", StringComparison.OrdinalIgnoreCase)) return "";

        string display = NormalizeRecoveryModelPath(!string.IsNullOrWhiteSpace(snapshot.DocumentLabel) ? snapshot.DocumentLabel : oldDocumentKey);
        return display.Contains(":shapes/", StringComparison.OrdinalIgnoreCase) ? display : "";
    }

    private static string NormalizeRecoveryModelPath(string value)
    {
        string normalized = (value ?? "").Trim().Replace('\\', '/');
        return normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^5]
            : normalized;
    }

    private void TrackVanillaAnimationRecovery(TimeSpan delay)
    {
        foreach (VanillaAnimationDocument document in _vanillaIndex.Documents)
        {
            string targetPath = "";
            try
            {
                string relativePath = Path.Combine("vanilla", "assets", document.Domain, document.AssetPath.Replace('/', Path.DirectorySeparatorChar));
                targetPath = GetToolAuthoredAssetPath("animations", relativePath);
            }
            catch
            {
                targetPath = document.DisplayPath;
            }

            string text;
            try
            {
                text = VanillaAnimationExportService.BuildDocumentJson(document);
            }
            catch
            {
                text = document.SourceJson?.ToString(Newtonsoft.Json.Formatting.Indented) ?? "{}";
            }

            _recoveryManager.TrackText(
                "Animations",
                document.HistoryKey,
                document.DisplayPath,
                targetPath,
                text,
                document.Dirty,
                delay);
        }
    }

    private void TrackTexturePaintRecovery(TimeSpan delay)
    {
        if (_modelDoc == null || _modelTexturePaintCanvas == null || string.IsNullOrWhiteSpace(_modelTexturePaintKey)) return;

        string targetPath = _modelTexturePaintSavePath;
        try
        {
            string path = ModelNormalizeTexturePaintPath(_modelTexturePaintSavePath, out _);
            if (!string.IsNullOrWhiteSpace(path)) targetPath = ModelTexturePaintOutputPath(_modelDoc.Domain, path);
        }
        catch
        {
            // Use display fallback below.
        }

        _recoveryManager.TrackBinary(
            "Texture Painter",
            _modelTexturePaintKey,
            _modelTexturePaintKey,
            targetPath,
            _modelTexturePaintCanvas.EncodePng(),
            _modelTexturePaintCanvas.Dirty,
            delay);
    }

    private void TrackBlockItemJsonRecovery(TimeSpan delay)
    {
        if (string.IsNullOrWhiteSpace(_blockItemJsonLoadedKey)) return;

        string domain = string.IsNullOrWhiteSpace(_blockItemJsonOutputDomain) ? _blockItemJsonLoadedDomain : _blockItemJsonOutputDomain;
        string assetPath = string.IsNullOrWhiteSpace(_blockItemJsonOutputPath) ? _blockItemJsonLoadedAssetPath : _blockItemJsonOutputPath;
        string targetPath = GetToolAuthoredAssetPath("block-item-json", Path.Combine("assets", domain, EnsureJsonFilePath(assetPath).Replace('/', Path.DirectorySeparatorChar)));
        _recoveryManager.TrackText("Block/Item JSON", _blockItemJsonLoadedKey, _blockItemJsonLoadedLabel, targetPath, _blockItemJsonText, IsBlockItemJsonDirty, delay);
    }

    private void TrackWorldgenRecovery(TimeSpan delay)
    {
        if (string.IsNullOrWhiteSpace(_worldgenLoadedKey)) return;
        WorldgenAssetEntry? entry = _worldgenEntries.FirstOrDefault(candidate => candidate.Key.Equals(_worldgenLoadedKey, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;

        string targetPath = GetToolAuthoredAssetPath("worldgen", Path.Combine("assets", entry.Domain, entry.AssetPath.Replace('/', Path.DirectorySeparatorChar)));
        _recoveryManager.TrackText("Worldgen", entry.Key, $"{entry.Domain}:{entry.AssetPath}", targetPath, _worldgenCurrentText, IsWorldgenEntryDirty(entry), delay);
    }

    private void TrackAiBehaviorRecovery(TimeSpan delay)
    {
        if (string.IsNullOrWhiteSpace(_aiBehaviorLoadedKey)) return;
        AiBehaviorEntry? entry = _aiBehaviorEntries.FirstOrDefault(candidate => candidate.Key.Equals(_aiBehaviorLoadedKey, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;

        string relativePath = Path.Combine("assets", entry.Domain, entry.AssetPath.Replace('/', Path.DirectorySeparatorChar));
        string targetPath = GetToolAuthoredAssetPath("entity-ai", relativePath);
        bool dirty = IsAiBehaviorTextDirty(_aiBehaviorCurrentText, _aiBehaviorOriginalText);
        _recoveryManager.TrackText("Entity AI", entry.Key, entry.DisplayCode, targetPath, _aiBehaviorCurrentText, dirty, delay);
    }

    private void TrackPatchCreatorRecovery(TimeSpan delay)
    {
        string domain = SanitizePathSegment(string.IsNullOrWhiteSpace(_patchCreatorOutputDomain) ? "ingamedevtools" : _patchCreatorOutputDomain.Trim());
        string assetPath = DevToolsPatchDocumentDraft.BuildAssetPath(CurrentPatchCreatorOutputFormat, _patchCreatorPatchName);
        string targetPath = GetToolAuthoredAssetPath("patches", Path.Combine("assets", domain, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        string documentKey = string.IsNullOrWhiteSpace(_patchCreatorLoadedPatchKey) ? $"{domain}:{assetPath}" : _patchCreatorLoadedPatchKey;
        string text = BuildPatchCreatorPatchJson(CurrentPatchCreatorOutputFormat);
        _recoveryManager.TrackText("Patches", documentKey, $"{domain}:{assetPath}", targetPath, text, _patchCreatorDocumentDirty, delay);
    }

    private void TrackConfigLibRecovery(TimeSpan delay)
    {
        string targetPath = GetToolAuthoredAssetPath("configlib", _configLibDocument.BuildPatchAssetRelativePath());
        string documentKey = string.IsNullOrWhiteSpace(_configLibLoadedDocumentKey)
            ? $"{_configLibDocument.Domain}:{_configLibDocument.RelativePath}"
            : _configLibLoadedDocumentKey;
        _recoveryManager.TrackText("ConfigLib", documentKey, $"{_configLibDocument.Domain}:{_configLibDocument.RelativePath}", targetPath, _configLibDocument.ToPatchJson(), _configLibDocumentDirty, delay);
    }

    private void TrackLootDropRecovery(TimeSpan delay)
    {
        LootDropEntry? entry = SelectedLootDropEntry;
        if (entry == null || string.IsNullOrWhiteSpace(_lootDropLoadedKey)) return;

        RefreshLootDropCachedState(entry);
        string domain = entry.SourceAsset?.Location.Domain ?? entry.Domain;
        string assetPath = entry.SourceAsset?.Location.Path ?? BuildLootDropFallbackAssetPath(entry);
        string targetPath = GetToolAuthoredAssetPath("loot-drops", Path.Combine("assets", domain, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        _recoveryManager.TrackText("Loot/Drops", entry.Key, entry.Label, targetPath, CurrentLootDropJson(), IsLootDropDirty, delay);
    }

    private void TrackTransformRecovery(TimeSpan delay)
    {
        TransformAssetEntry? asset = SelectedTransformAsset;
        TransformSlotSelection? slot = GetSelectedTransformSlot(asset);
        if (asset == null || slot == null) return;

        ModelTransform transform = GetTransformDraft(asset, slot);
        IAsset? sourceAsset = FindCollectibleSourceAsset(asset.Collectible);
        string domain = sourceAsset?.Location.Domain ?? asset.Collectible.Code?.Domain ?? "game";
        string kind = asset.Collectible is Block ? "blocktypes" : "itemtypes";
        string assetPath = sourceAsset?.Location.Path ?? $"{kind}/{EnsureJsonFilePath(asset.Collectible.Code?.Path ?? "unknown")}";
        string targetPath = GetToolAuthoredAssetPath("transforms", Path.Combine("assets", domain, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        _recoveryManager.TrackText("Transforms", slot.Key, $"{asset.Label} / {slot.DisplayName}", targetPath, TransformToToken(transform).ToString(Newtonsoft.Json.Formatting.Indented), _transformDirtyKeys.Contains(slot.Key), delay);
    }

    private bool TryRestoreRecoverySnapshot(DevToolsRecoverySnapshot snapshot, out string status)
    {
        status = "";
        if (snapshot.PayloadKind == "binary-base64")
        {
            return TryRestoreBinaryRecoverySnapshot(snapshot, out status);
        }

        if (string.IsNullOrWhiteSpace(snapshot.Text))
        {
            status = "Recovery snapshot has no text payload.";
            return false;
        }

        return snapshot.Editor switch
        {
            "Models" => TryRestoreModelRecovery(snapshot, out status),
            "Animations" => TryRestoreVanillaAnimationRecovery(snapshot, out status),
            "Block/Item JSON" => TryRestoreBlockItemJsonRecovery(snapshot, out status),
            "Worldgen" => TryRestoreWorldgenRecovery(snapshot, out status),
            "Entity AI" => TryRestoreAiBehaviorRecovery(snapshot, out status),
            "Patches" => TryRestorePatchCreatorRecovery(snapshot, out status),
            "ConfigLib" => TryRestoreConfigLibRecovery(snapshot, out status),
            "Loot/Drops" => TryRestoreLootDropRecovery(snapshot, out status),
            "Transforms" => TryRestoreTransformRecovery(snapshot, out status),
            "Recipes" => _recipeEditor.TryRestoreRecovery(snapshot, out status),
            _ => RecoveryUnsupported(snapshot, out status)
        };
    }

    private bool TryRestoreModelRecovery(DevToolsRecoverySnapshot snapshot, out string status)
    {
        string document = !string.IsNullOrWhiteSpace(snapshot.DocumentLabel)
            ? snapshot.DocumentLabel
            : snapshot.RecoveryKey.Split("::", 2, StringSplitOptions.None).ElementAtOrDefault(1) ?? "";
        string domain = "game";
        string assetPath = "shapes/recovered.json";
        int separator = document.IndexOf(':');
        if (separator > 0)
        {
            domain = document[..separator];
            assetPath = document[(separator + 1)..];
        }

        if (!ModelTryParseDocument(snapshot.Text ?? "", domain, assetPath, isNew: false, out ModelDocumentData? parsed, out string error) || parsed == null)
        {
            status = $"Model recovery failed: {error}";
            return false;
        }

        parsed.Dirty = true;
        _modelDoc = parsed;
        ModelSelectElement(parsed.Roots.FirstOrDefault());
        ModelMarkChanged();
        _activeDevToolsTab = DevToolsTab.Models;
        status = $"Restored recovered model draft {parsed.DisplayPath}.";
        return true;
    }

    private bool TryRestoreVanillaAnimationRecovery(DevToolsRecoverySnapshot snapshot, out string status)
    {
        string documentKey = snapshot.RecoveryKey.Split("::", 2, StringSplitOptions.None).ElementAtOrDefault(1) ?? "";
        string[] parts = documentKey.Split(':', 4);
        if (parts.Length < 3 || !string.Equals(parts[0], nameof(VanillaDocumentKind.Shape), StringComparison.OrdinalIgnoreCase))
        {
            status = "Animation recovery can restore shape animation JSON directly. Use Copy for recovered entity metadata animation payloads.";
            return false;
        }

        string domain = parts[1];
        string assetPath = parts[2];
        JObject? json = TryParseJsonObject(snapshot.Text ?? "");
        if (json == null)
        {
            status = "Animation recovery failed: invalid JSON.";
            return false;
        }

        try
        {
            Shape? shape = JsonUtil.ToObject<Shape>(snapshot.Text ?? "{}", domain);
            if (shape == null)
            {
                status = "Animation recovery failed: recovered JSON did not contain a shape document.";
                return false;
            }

            _vanillaIndex.SetShapeDocument(_api, shape, domain, assetPath, snapshot.DocumentLabel, json);
            VanillaAnimationDocument? restored = _vanillaIndex.Documents.FirstOrDefault(document =>
                document.Kind == VanillaDocumentKind.Shape &&
                string.Equals(document.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(document.AssetPath, EnsureJsonPath(assetPath), StringComparison.OrdinalIgnoreCase));
            restored?.MarkDirty();
            _selectVanillaAnimationsTabOnNextDraw = true;
            _activeDevToolsTab = DevToolsTab.Animations;
            status = $"Restored recovered animation shape document {snapshot.DocumentLabel}.";
            return true;
        }
        catch (Exception exception)
        {
            status = $"Animation recovery failed: {exception.Message}";
            return false;
        }
    }

    private bool TryRestoreBlockItemJsonRecovery(DevToolsRecoverySnapshot snapshot, out string status)
    {
        string text = snapshot.Text ?? "";
        JObject? json = TryParseJsonObject(text);
        if (json == null)
        {
            status = "Block/Item recovery failed: invalid JSON.";
            return false;
        }

        _blockItemJsonLoadedKey = $"recovery:{Guid.NewGuid():N}";
        _blockItemJsonLoadedLabel = snapshot.DocumentLabel;
        _blockItemJsonLoadedSourceText = "{}";
        _blockItemJsonOriginalText = "{}";
        _blockItemJsonText = json.ToString(Newtonsoft.Json.Formatting.Indented);
        _blockItemJsonLoadedIsAuthored = true;
        _blockItemJsonLoadedIsRuntime = false;
        _blockItemJsonTextHistory.Reset(_blockItemJsonText);
        _activeDevToolsTab = DevToolsTab.BlockItemJson;
        status = $"Restored recovered Block/Item JSON draft {snapshot.DocumentLabel}.";
        return true;
    }

    private bool TryRestoreWorldgenRecovery(DevToolsRecoverySnapshot snapshot, out string status)
    {
        string key = snapshot.RecoveryKey.Split("::", 2, StringSplitOptions.None).ElementAtOrDefault(1) ?? "";
        WorldgenAssetEntry? entry = _worldgenEntries.FirstOrDefault(candidate => candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            status = "Worldgen recovery failed: original asset is not indexed yet. Open Worldgen once, then retry.";
            return false;
        }

        LoadWorldgenEntry(entry);
        _worldgenCurrentText = snapshot.Text ?? "";
        ValidateWorldgenCurrentText();
        RememberWorldgenDraft();
        _activeDevToolsTab = DevToolsTab.Worldgen;
        status = $"Restored recovered worldgen draft {entry.Domain}:{entry.AssetPath}.";
        return true;
    }

    private bool TryRestoreAiBehaviorRecovery(DevToolsRecoverySnapshot snapshot, out string status)
    {
        string key = snapshot.RecoveryKey.Split("::", 2, StringSplitOptions.None).ElementAtOrDefault(1) ?? "";
        AiBehaviorEntry? entry = _aiBehaviorEntries.FirstOrDefault(candidate => candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            status = "Entity AI recovery failed: original asset is not indexed yet. Open Entity AI once, then retry.";
            return false;
        }

        LoadAiBehaviorEntry(entry, keepDirty: false);
        _aiBehaviorCurrentText = snapshot.Text ?? "";
        ValidateAiBehaviorCurrentText();
        RememberAiBehaviorDraft();
        _activeDevToolsTab = DevToolsTab.EntityAi;
        status = $"Restored recovered Entity AI draft {entry.DisplayCode}.";
        return true;
    }

    private bool TryRestorePatchCreatorRecovery(DevToolsRecoverySnapshot snapshot, out string status)
    {
        try
        {
            DevToolsPatchDocumentDraft document = DevToolsPatchDocumentDraft.FromJson(
                snapshot.Text ?? "[]",
                CurrentPatchCreatorOutputFormat,
                _patchCreatorOutputDomain,
                _patchCreatorPatchName);
            _patchCreatorOperations.Clear();
            _patchCreatorOperations.AddRange(document.Operations);
            _patchCreatorOperationIndex = _patchCreatorOperations.Count > 0 ? 0 : -1;
            _patchCreatorDocumentDirty = true;
            if (_patchCreatorOperationIndex >= 0) LoadPatchCreatorOperation(_patchCreatorOperations[_patchCreatorOperationIndex]);
            _activeDevToolsTab = DevToolsTab.Patches;
            status = $"Restored recovered patch draft {snapshot.DocumentLabel}.";
            return true;
        }
        catch (Exception exception)
        {
            status = $"Patch recovery failed: {exception.Message}";
            return false;
        }
    }

    private bool TryRestoreConfigLibRecovery(DevToolsRecoverySnapshot snapshot, out string status)
    {
        try
        {
            _configLibDocument = DevToolsConfigLibDocumentDraft.FromPatchJson(snapshot.Text ?? "{}", _configLibDocument.Domain, _configLibDocument.RelativePath);
            _configLibDocumentDirty = true;
            _activeDevToolsTab = DevToolsTab.ConfigLib;
            status = $"Restored recovered ConfigLib draft {snapshot.DocumentLabel}.";
            return true;
        }
        catch (Exception exception)
        {
            status = $"ConfigLib recovery failed: {exception.Message}";
            return false;
        }
    }

    private bool TryRestoreLootDropRecovery(DevToolsRecoverySnapshot snapshot, out string status)
    {
        string key = snapshot.RecoveryKey.Split("::", 2, StringSplitOptions.None).ElementAtOrDefault(1) ?? "";
        LootDropEntry? entry = _lootDropEntries.FirstOrDefault(candidate => candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            status = "Loot/drop recovery failed: original entry is not indexed yet. Open Loot/Drops once, then retry.";
            return false;
        }

        try
        {
            LoadLootDropEntry(entry, keepDirty: false);
            if (entry.Kind == LootDropKind.TradeTable)
            {
                JToken trade = JToken.Parse(snapshot.Text ?? "null");
                _lootDropTradeJson = trade.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            else
            {
                JArray drops = JArray.Parse(snapshot.Text ?? "[]");
                _lootDropDrafts.Clear();
                foreach (JToken drop in drops)
                {
                    _lootDropDrafts.Add(LootDropDraft.FromToken(drop));
                }
                _lootDropSelectedDraftIndex = Math.Clamp(_lootDropSelectedDraftIndex, 0, Math.Max(0, _lootDropDrafts.Count - 1));
            }

            RefreshLootDropCachedState(entry);
            SaveCurrentLootDropDraftState();
            _activeDevToolsTab = DevToolsTab.LootDrops;
            status = $"Restored recovered loot/drop draft {entry.Label}.";
            return true;
        }
        catch (Exception exception)
        {
            status = $"Loot/drop recovery failed: {exception.Message}";
            return false;
        }
    }

    private bool TryRestoreTransformRecovery(DevToolsRecoverySnapshot snapshot, out string status)
    {
        string slotKey = snapshot.RecoveryKey.Split("::", 2, StringSplitOptions.None).ElementAtOrDefault(1) ?? "";
        TransformAssetEntry? asset = _transformAssets.FirstOrDefault(candidate => slotKey.StartsWith(candidate.Key + "|", StringComparison.OrdinalIgnoreCase));
        if (asset == null)
        {
            status = "Transform recovery failed: original asset is not indexed yet. Open Transforms once, then retry.";
            return false;
        }

        TransformSlotSelection? slot = GetRecoverableTransformSlot(asset, slotKey);
        if (slot == null)
        {
            status = "Transform recovery failed: original transform slot could not be resolved.";
            return false;
        }

        try
        {
            ModelTransform? transform = JToken.Parse(snapshot.Text ?? "{}").ToObject<ModelTransform>();
            if (transform == null)
            {
                status = "Transform recovery failed: transform payload was empty.";
                return false;
            }

            _transformDrafts[slot.Key] = transform.EnsureDefaultValues();
            _transformDirtyKeys.Add(slot.Key);
            RebuildVisibleTransformAssets();
            _activeDevToolsTab = DevToolsTab.Transforms;
            status = $"Restored recovered transform draft {slot.DisplayName} for {asset.Label}.";
            return true;
        }
        catch (Exception exception)
        {
            status = $"Transform recovery failed: {exception.Message}";
            return false;
        }
    }

    private TransformSlotSelection? GetRecoverableTransformSlot(TransformAssetEntry asset, string slotKey)
    {
        string[] parts = slotKey.Split('|');
        if (parts.Length < 2) return null;

        string attributeCode = parts[1];
        string? typedKey = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : null;
        return new TransformSlotSelection(asset, attributeCode, typedKey);
    }

    private bool TryRestoreBinaryRecoverySnapshot(DevToolsRecoverySnapshot snapshot, out string status)
    {
        if (snapshot.Editor != "Texture Painter" || string.IsNullOrWhiteSpace(snapshot.BinaryBase64))
        {
            return RecoveryUnsupported(snapshot, out status);
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(snapshot.BinaryBase64);
            if (!DevToolsTexturePaintCanvas.TryLoadPng(bytes, out DevToolsTexturePaintCanvas? canvas, out string error) || canvas == null)
            {
                status = $"Texture recovery failed: {error}";
                return false;
            }

            _modelTexturePaintCanvas = canvas;
            _modelTexturePaintCanvas.Dirty = true;
            _modelTexturePaintTextureDirty = true;
            _modelTexturePaintMode = true;
            _activeDevToolsTab = DevToolsTab.Models;
            status = $"Restored recovered texture painter canvas {snapshot.DocumentLabel}.";
            return true;
        }
        catch (Exception exception)
        {
            status = $"Texture recovery failed: {exception.Message}";
            return false;
        }
    }

    private static bool RecoveryUnsupported(DevToolsRecoverySnapshot snapshot, out string status)
    {
        status = $"Recovery for {snapshot.Editor} cannot be loaded directly yet. Use Copy to recover the payload manually.";
        return false;
    }

    private static void OpenDevToolsFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening a folder is convenience-only.
        }
    }
}
