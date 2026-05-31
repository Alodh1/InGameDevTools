using ImGuiNET;
using InGameDevTools.Animations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace InGameDevTools.Utils;

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public struct ParticleEffectsPacket
{
    public string Code { get; set; }
    public double[] Position { get; set; }
    public float[] Velocity { get; set; }
    public float Intensity { get; set; }
}

public class ParticleEffectsManager
{
    public const string ParticleEffectsFileName = "particle-effects.json";

    public ParticleEffectsManager(ICoreAPI api)
    {
        _api = api;

        if (api is ICoreClientAPI clientApi)
        {
            _clientChannel = clientApi.Network.RegisterChannel(_networkChannelId)
                .RegisterMessageType<ParticleEffectsPacket>();
        }

        if (api is ICoreServerAPI serverApi)
        {
            serverApi.Network.RegisterChannel(_networkChannelId)
                .RegisterMessageType<ParticleEffectsPacket>()
                .SetMessageHandler<ParticleEffectsPacket>((player, packet) => SpawnParticleEffect(packet, player));
        }
    }

    public void LoadAssets()
    {
        _particleEffects.Clear();
#if DEBUG
        ClearPreviewReferenceModelCache();
#endif
        _scanDiagnostics = new ParticleAssetScanDiagnostics();
        LoadNamedParticleEffectAssets(_api);
        LoadRuntimeParticleAssets(_api);
        LoadEmbeddedParticleAssets(_api);

        if (_verboseParticleScanLog)
        {
            LogParticleScanDiagnostics(_api);
        }
    }

    private void LoadNamedParticleEffectAssets(ICoreAPI api)
    {
        List<IAsset> assets = api.Assets.GetManyInCategory("config", ParticleEffectsFileName);
        _scanDiagnostics.NamedConfigAssets = assets.Count;

        foreach (IAsset asset in assets)
        {
            try
            {
                string domain = asset.Location.Domain;
                JsonObject json = JsonObject.FromJson(asset.ToText());
                if (json.Token is not JObject token) continue;

                foreach ((string code, JToken? effect) in token)
                {
                    if (AddParticleEffect(api, $"{domain}:{code}", effect, asset.Location.ToString(), ParticleEffectSourceKind.NamedConfig))
                    {
                        _scanDiagnostics.NamedConfigEffectsAdded++;
                    }
                }
            }
            catch (Exception exception)
            {
                _scanDiagnostics.ParseFailures++;
                LoggerUtil.Error(api, this, $"Error on parsing particle effects for '{asset.Location.Domain}':\n{exception}");
            }
        }
    }

    private void LoadEmbeddedParticleAssets(ICoreAPI api)
    {
#if DEBUG
        ClearPreviewReferenceModelCache();
#endif

        Dictionary<string, IAsset> assetsByLocation = new(StringComparer.OrdinalIgnoreCase);
        foreach (IAsset asset in api.Assets.AllAssets.Values)
        {
            _scanDiagnostics.AllAssets++;
            string location = asset.Location?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            assetsByLocation.TryAdd(location, asset);
        }

        _scanDiagnostics.UniqueAssets = assetsByLocation.Count;

        foreach (IAsset asset in assetsByLocation.Values)
        {
            if (!LooksLikeJsonAsset(asset))
            {
                AddVerboseSample(_scanDiagnostics.SkippedSamples, $"skip non-json candidate: {asset.Location}");
                continue;
            }

            _scanDiagnostics.JsonCandidateAssets++;

            IAsset loadedAsset = asset;
            try
            {
                loadedAsset = api.Assets.TryGet(asset.Location, true) ?? asset;
            }
            catch (Exception exception)
            {
                _scanDiagnostics.TextReadFailures++;
                AddVerboseSample(_scanDiagnostics.SkippedSamples, $"load failed: {asset.Location} ({exception.Message})");
                continue;
            }

            string text;
            try
            {
                text = loadedAsset.ToText();
            }
            catch (Exception exception)
            {
                _scanDiagnostics.TextReadFailures++;
                AddVerboseSample(_scanDiagnostics.SkippedSamples, $"text read failed: {asset.Location} ({exception.Message})");
                continue;
            }

            if (!text.Contains("particleProperties", StringComparison.OrdinalIgnoreCase))
            {
                AddVerboseSample(_scanDiagnostics.SkippedSamples, $"no particleProperties: {asset.Location}");
                continue;
            }

            _scanDiagnostics.AssetsWithParticleProperties++;

            try
            {
                JsonObject json = JsonObject.FromJson(text);
                if (json.Token == null) continue;

                List<(string Path, JToken Token)> particleTokens = [];
                CollectParticlePropertyTokens(json.Token, "", particleTokens);
                foreach ((string particlePath, JToken token) in particleTokens)
                {
                    string key = $"{asset.Location.Domain}:{EnsureJsonPath(asset.Location.Path)}#{particlePath}";
                    if (AddParticleEffect(api, key, token, BuildParticleSource(asset.Location), ParticleEffectSourceKind.EmbeddedAsset))
                    {
                        _scanDiagnostics.EmbeddedEffectsAdded++;
                        AddVerboseSample(_scanDiagnostics.AddedSamples, key);
                    }
                }
            }
            catch (Exception exception)
            {
                _scanDiagnostics.ParseFailures++;
                LoggerUtil.Warn(api, this, $"Error on parsing embedded particle assets for '{asset.Location}':\n{exception}");
            }
        }
    }

    private void LoadRuntimeParticleAssets(ICoreAPI api)
    {
#if DEBUG
        ClearPreviewReferenceModelCache();
#endif

        foreach (Block block in api.World.Blocks)
        {
            _scanDiagnostics.RuntimeBlocksScanned++;
            AddRuntimeCollectibleParticleEffects(api, block, ParticleEffectSourceKind.RuntimeBlock);
        }

        foreach (Item item in api.World.Items)
        {
            _scanDiagnostics.RuntimeItemsScanned++;
            AddRuntimeCollectibleParticleEffects(api, item, ParticleEffectSourceKind.RuntimeItem);
        }
    }

    private void AddRuntimeCollectibleParticleEffects(ICoreAPI api, CollectibleObject collectible, ParticleEffectSourceKind sourceKind)
    {
        if (collectible?.Code == null || collectible.ParticleProperties == null || collectible.ParticleProperties.Length == 0)
        {
            return;
        }

        if (sourceKind == ParticleEffectSourceKind.RuntimeBlock)
        {
            _scanDiagnostics.RuntimeBlocksWithParticleProperties++;
        }
        else
        {
            _scanDiagnostics.RuntimeItemsWithParticleProperties++;
        }

        string typePrefix = sourceKind == ParticleEffectSourceKind.RuntimeBlock ? "block" : "item";
        for (int index = 0; index < collectible.ParticleProperties.Length; index++)
        {
            AdvancedParticleProperties? source = collectible.ParticleProperties[index];
            if (source == null)
            {
                continue;
            }

            try
            {
                AdvancedParticleProperties particleProperties = source.Clone();
                NormalizeParticleProperties(particleProperties);
                string key = $"{typePrefix}:{collectible.Code}#ParticleProperties[{index}]";
                string sourceName = $"{typePrefix}:{collectible.Code}";
                if (AddParticleEffect(api, key, particleProperties, sourceName, sourceKind))
                {
                    if (sourceKind == ParticleEffectSourceKind.RuntimeBlock)
                    {
                        _scanDiagnostics.RuntimeBlockEffectsAdded++;
                    }
                    else
                    {
                        _scanDiagnostics.RuntimeItemEffectsAdded++;
                    }

                    AddVerboseSample(_scanDiagnostics.AddedSamples, key);
                }
            }
            catch (Exception exception)
            {
                _scanDiagnostics.ParseFailures++;
                LoggerUtil.Warn(api, this, $"Error on cloning runtime particle effect '{collectible.Code}' from '{sourceKind}':\n{exception}");
            }
        }
    }

    private void LogParticleScanDiagnostics(ICoreAPI api)
    {
        LoggerUtil.Verbose(api, this, $"Particle scan: {_scanDiagnostics.Summary(_particleEffects.Count)}");
        foreach (string sample in _scanDiagnostics.AddedSamples)
        {
            LoggerUtil.Verbose(api, this, $"Particle scan added: {sample}");
        }

        foreach (string sample in _scanDiagnostics.SkippedSamples)
        {
            LoggerUtil.Verbose(api, this, $"Particle scan skipped: {sample}");
        }
    }

    private bool AddParticleEffect(ICoreAPI api, string key, JToken? token, string source, ParticleEffectSourceKind sourceKind)
    {
        if (token is not JObject)
        {
            return false;
        }

        try
        {
            JsonObject effectJson = new(token);
            AdvancedParticleProperties? particleProperties = effectJson.AsObject<AdvancedParticleProperties>();
            if (particleProperties == null) return false;
            NormalizeParticleProperties(particleProperties);

            string finalKey = key;
            int duplicate = 2;
            while (_particleEffects.ContainsKey(finalKey))
            {
                _scanDiagnostics.DuplicateKeys++;
                finalKey = $"{key}#{duplicate++}";
            }

            _particleEffects[finalKey] = new ParticleEffectEntry(finalKey, source, sourceKind, particleProperties);
            return true;
        }
        catch (Exception exception)
        {
            _scanDiagnostics.ParseFailures++;
            LoggerUtil.Warn(api, this, $"Error on parsing particle effect '{key}' from '{source}':\n{exception}");
            return false;
        }
    }

    private bool AddParticleEffect(ICoreAPI api, string key, AdvancedParticleProperties particleProperties, string source, ParticleEffectSourceKind sourceKind)
    {
        try
        {
            string finalKey = key;
            int duplicate = 2;
            while (_particleEffects.ContainsKey(finalKey))
            {
                _scanDiagnostics.DuplicateKeys++;
                finalKey = $"{key}#{duplicate++}";
            }

            _particleEffects[finalKey] = new ParticleEffectEntry(finalKey, source, sourceKind, particleProperties);
            return true;
        }
        catch (Exception exception)
        {
            _scanDiagnostics.ParseFailures++;
            LoggerUtil.Warn(api, this, $"Error on adding particle effect '{key}' from '{source}':\n{exception}");
            return false;
        }
    }

    private static bool LooksLikeJsonAsset(IAsset asset)
    {
        string name = asset.Name ?? "";
        string path = asset.Location?.Path ?? "";
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string firstPathPart = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return firstPathPart.Equals("blocktypes", StringComparison.OrdinalIgnoreCase) ||
            firstPathPart.Equals("itemtypes", StringComparison.OrdinalIgnoreCase) ||
            firstPathPart.Equals("entities", StringComparison.OrdinalIgnoreCase) ||
            firstPathPart.Equals("config", StringComparison.OrdinalIgnoreCase) ||
            firstPathPart.Equals("recipes", StringComparison.OrdinalIgnoreCase) ||
            firstPathPart.Equals("worldproperties", StringComparison.OrdinalIgnoreCase) ||
            firstPathPart.Equals("patches", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildParticleSource(AssetLocation location)
    {
        return $"{location.Domain}:{EnsureJsonPath(location.Path)}";
    }

    private static string EnsureJsonPath(string path)
    {
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : $"{path}.json";
    }

    private static void AddVerboseSample(List<string> samples, string text)
    {
        if (samples.Count < 20)
        {
            samples.Add(text);
        }
    }

    private static void CollectParticlePropertyTokens(JToken token, string path, List<(string Path, JToken Token)> results)
    {
        switch (token)
        {
            case JObject obj:
                foreach (JProperty property in obj.Properties())
                {
                    string childPath = string.IsNullOrWhiteSpace(path) ? property.Name : $"{path}/{property.Name}";
                    if (property.Name.Contains("particleProperties", StringComparison.OrdinalIgnoreCase))
                    {
                        CollectParticleTokens(property.Value, childPath, results);
                    }
                    else
                    {
                        CollectParticlePropertyTokens(property.Value, childPath, results);
                    }
                }
                break;
            case JArray array:
                for (int index = 0; index < array.Count; index++)
                {
                    CollectParticlePropertyTokens(array[index], $"{path}[{index}]", results);
                }
                break;
        }
    }

    private static void CollectParticleTokens(JToken token, string path, List<(string Path, JToken Token)> results)
    {
        switch (token)
        {
            case JObject obj when LooksLikeParticleProperties(obj):
                results.Add((path, obj));
                break;
            case JObject obj:
                foreach (JProperty property in obj.Properties())
                {
                    CollectParticleTokens(property.Value, $"{path}/{property.Name}", results);
                }
                break;
            case JArray array:
                for (int index = 0; index < array.Count; index++)
                {
                    CollectParticleTokens(array[index], $"{path}[{index}]", results);
                }
                break;
        }
    }

    private static bool LooksLikeParticleProperties(JObject obj)
    {
        return obj.Property("hsvaColor", StringComparison.OrdinalIgnoreCase) != null ||
            obj.Property("velocity", StringComparison.OrdinalIgnoreCase) != null ||
            obj.Property("posOffset", StringComparison.OrdinalIgnoreCase) != null ||
            obj.Property("quantity", StringComparison.OrdinalIgnoreCase) != null ||
            obj.Property("size", StringComparison.OrdinalIgnoreCase) != null ||
            obj.Property("lifeLength", StringComparison.OrdinalIgnoreCase) != null ||
            obj.Property("gravityEffect", StringComparison.OrdinalIgnoreCase) != null ||
            obj.Property("particleModel", StringComparison.OrdinalIgnoreCase) != null ||
            obj.Property("vertexFlags", StringComparison.OrdinalIgnoreCase) != null;
    }

    private static void NormalizeParticleProperties(AdvancedParticleProperties particleProperties)
    {
        // VS 1.22 can crash clients when VelocityEvolve is present on network-spawned particles.
        // Strip it at load time too, so old JSON assets cannot reintroduce the crash.
        particleProperties.VelocityEvolve = null;
        particleProperties.Velocity = EnsureNatFloatArray(particleProperties.Velocity, 3, 0f, 0f);
        particleProperties.PosOffset = EnsureNatFloatArray(particleProperties.PosOffset, 3, 0f, 0f);
        particleProperties.HsvaColor = EnsureNatFloatArray(particleProperties.HsvaColor, 4, 255f, 0f);
        particleProperties.Quantity ??= NewNatFloat(1f, 0f);
        particleProperties.LifeLength ??= NewNatFloat(1f, 0f);
        particleProperties.Size ??= NewNatFloat(0.1f, 0f);
        particleProperties.GravityEffect ??= NewNatFloat(0f, 0f);
    }

    private static NatFloat[] EnsureNatFloatArray(NatFloat[]? values, int length, float defaultAvg, float defaultVar)
    {
        NatFloat[] result = new NatFloat[length];
        for (int index = 0; index < length; index++)
        {
            result[index] = values != null && index < values.Length && values[index] != null
                ? values[index]
                : NewNatFloat(defaultAvg, defaultVar);
        }
        return result;
    }

    private static NatFloat NewNatFloat(float avg, float var) => new(var, avg, EnumDistribution.UNIFORM);

    public AdvancedParticleProperties Get(string code, string domain)
    {
        string key = $"{domain}:{code}";
        if (!_particleEffects.TryGetValue(key, out ParticleEffectEntry? entry)) throw new InvalidOperationException($"Particle effect '{key}' not found");
        return entry.Properties;
    }
    public AdvancedParticleProperties Get(string code)
    {
        if (!_particleEffects.TryGetValue(code, out ParticleEffectEntry? entry)) throw new InvalidOperationException($"Particle effect '{code}' not found");
        return entry.Properties;
    }
    public void Spawn(EntityPlayer player, string code, Vector3 position, Vector3 velocity, float intensity)
    {
        ParticleEffectsPacket packet = PreparePacket(code, player, position, velocity, intensity);
        if (_api.Side == EnumAppSide.Client)
        {
            _clientChannel?.SendPacket(packet);
            SpawnParticleEffect(packet, player.Player);
        }
        else
        {
            SpawnParticleEffect(packet, null);
        }
    }
    public void Spawn(string code, Vector3 position, Vector3 velocity, float intensity)
    {
        ParticleEffectsPacket packet = PreparePacket(code, position, velocity, intensity);
        SpawnParticleEffect(packet, null);
    }
    public void Spawn(string code, Vector3d position, Vector3 velocity, float intensity)
    {
        ParticleEffectsPacket packet = PreparePacket(code, position, velocity, intensity);
        SpawnParticleEffect(packet, null);
    }

#if DEBUG
    public void Draw(string id)
    {
        DrawEditor(id, 0, 1f);
    }

    public void DrawEditor(string id, float deltaSeconds)
    {
        DrawEditor(id, deltaSeconds, 1f);
    }

    public void DrawEditor(string id, float deltaSeconds, float uiScale, DebugWindowManager.DevToolsLiveApplyManager? liveApplyManager = null)
    {
        ParticleEffectEntry[] entries = _particleEffects.Values.OrderBy(entry => entry.DisplayKey, StringComparer.OrdinalIgnoreCase).ToArray();
        ParticleEffectEntry[] visibleEntries = entries
            .Where(entry => ImGuiLayoutHelper.MatchesDomain(_particleDomainFilter, entry.Domain))
            .Where(entry => MatchesParticleFilter(entry))
            .ToArray();

        if (visibleEntries.Length > 0 && (string.IsNullOrWhiteSpace(_selectedParticleEffectKey) || !visibleEntries.Any(entry => entry.Key.Equals(_selectedParticleEffectKey, StringComparison.OrdinalIgnoreCase))))
        {
            _selectedParticleEffectKey = visibleEntries[0].Key;
            ResetPreview(_selectedParticleEffectKey);
        }

        int visibleIndex = Math.Max(0, Array.FindIndex(visibleEntries, entry => string.Equals(entry.Key, _selectedParticleEffectKey, StringComparison.OrdinalIgnoreCase)));
        System.Numerics.Vector2 available = ImGui.GetContentRegionAvail();
        float scale = Math.Max(0.75f, uiScale);
        float splitterThickness = Math.Max(5f, 6f * scale);
        ImGuiLayoutHelper.CalculateThreePanelWidths(
            available.X,
            splitterThickness,
            _layout,
            240f * scale,
            520f * scale,
            320f * scale,
            360f * scale,
            860f * scale,
            out float panelAvailableWidth,
            out float leftWidth,
            out float centerWidth,
            out float rightWidth);

        ImGui.BeginChild($"##particles-browser-{id}", new System.Numerics.Vector2(leftWidth, available.Y), true);
        ImGui.SeparatorText("Particle effects");
        DrawParticleScanControls(id);
        ImGuiLayoutHelper.DrawDomainCombo($"Domain##particle-domain-filter-{id}", ref _particleDomainFilter, entries.Select(entry => entry.Domain));
        ImGui.InputTextWithHint($"##particle-filter-{id}", "filter effects", ref _particleFilter, 240);
        ImGui.BeginChild($"##particle-effects-list-{id}", new System.Numerics.Vector2(0, Math.Max(80f, ImGui.GetContentRegionAvail().Y * 0.55f)), true);
        for (int index = 0; index < visibleEntries.Length; index++)
        {
            ParticleEffectEntry particleEntry = visibleEntries[index];
            bool selected = particleEntry.Key.Equals(_selectedParticleEffectKey, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable($"{particleEntry.DisplayKey}##particle-effect-{particleEntry.Key}", selected))
            {
                _selectedParticleEffectKey = particleEntry.Key;
                visibleIndex = index;
                ResetPreview(_selectedParticleEffectKey);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(particleEntry.Key);
            }
        }
        ImGui.EndChild();
        ImGui.TextDisabled($"Showing {visibleEntries.Length} / {entries.Length}");
        if (!string.IsNullOrWhiteSpace(_selectedParticleEffectKey) && _particleEffects.TryGetValue(_selectedParticleEffectKey, out ParticleEffectEntry? selectedEntry))
        {
            ImGui.SeparatorText("Selected source");
            ImGui.TextWrapped(selectedEntry.SourceKindLabel);
            ImGui.TextWrapped(selectedEntry.Source);
        }
        ImGui.EndChild();

        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter($"##particles-left-splitter-{id}", available.Y, splitterThickness, panelAvailableWidth, ref _layout.LeftFraction, 240f * scale, Math.Max(240f * scale, panelAvailableWidth - rightWidth - 320f * scale));
        ImGui.SameLine(0, 0);

        if (entries.Length == 0 || string.IsNullOrWhiteSpace(_selectedParticleEffectKey) || !_particleEffects.TryGetValue(_selectedParticleEffectKey, out ParticleEffectEntry? entry))
        {
            ImGui.BeginChild($"##particles-empty-{id}", new System.Numerics.Vector2(centerWidth, available.Y), true);
            ImGui.TextDisabled("No particle effects loaded.");
            ImGui.TextWrapped("Use Reload assets after a world is loaded. If this remains empty, enable Verbose scan log and reload.");
            ImGui.EndChild();
            return;
        }

        AdvancedParticleProperties particleProperties = entry.Properties;
        ImGui.BeginChild($"##particles-preview-{id}", new System.Numerics.Vector2(centerWidth, available.Y), true);
        DrawPreviewPanel(id, _selectedParticleEffectKey, particleProperties, deltaSeconds);
        ImGui.EndChild();

        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter($"##particles-right-splitter-{id}", available.Y, splitterThickness, panelAvailableWidth, ref _layout.RightFraction, 360f * scale, Math.Max(360f * scale, panelAvailableWidth - leftWidth - 320f * scale), invertDrag: true);
        ImGui.SameLine(0, 0);

        ImGui.BeginChild($"##particles-properties-{id}", new System.Numerics.Vector2(rightWidth, available.Y), true, ImGuiWindowFlags.HorizontalScrollbar);
        ImGui.SeparatorText("Values");
        ImGui.TextWrapped($"{entry.SourceKindLabel}: {entry.Source}");
        DrawParticleLiveControls(id, entry, liveApplyManager);
        ParticleEditor.Draw($"{id}-{_selectedParticleEffectKey}", particleProperties);
        ImGui.EndChild();
    }

    private bool MatchesParticleFilter(ParticleEffectEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_particleFilter))
        {
            return true;
        }

        return entry.Key.Contains(_particleFilter, StringComparison.OrdinalIgnoreCase) ||
            entry.DisplayKey.Contains(_particleFilter, StringComparison.OrdinalIgnoreCase) ||
            entry.Source.Contains(_particleFilter, StringComparison.OrdinalIgnoreCase) ||
            entry.SourceKindLabel.Contains(_particleFilter, StringComparison.OrdinalIgnoreCase);
    }

    public void ResetLayout()
    {
        _layout.Reset();
    }

    private void ClearPreviewReferenceModelCache()
    {
        foreach (ParticlePreviewPlacement placement in _previewPlacementCache.Values)
        {
            placement.Dispose();
        }

        _previewPlacementCache.Clear();
    }

    private DevToolsPreview3DRenderer EnsurePreviewRenderer3D()
    {
        if (_previewRenderer3D != null) return _previewRenderer3D;
        if (_api is not ICoreClientAPI clientApi) throw new InvalidOperationException("3D preview requires client API.");
        return _previewRenderer3D = new DevToolsPreview3DRenderer(clientApi);
    }

    private void DrawParticleScanControls(string id)
    {
        if (ImGui.Button($"Reload assets##particle-reload-{id}"))
        {
            LoadAssets();
        }

        bool verboseChanged;
        ImGui.SameLine();
        verboseChanged = ImGui.Checkbox($"Verbose scan log##particle-verbose-{id}", ref _verboseParticleScanLog);
        if (verboseChanged && _verboseParticleScanLog)
        {
            LogParticleScanDiagnostics(_api);
        }

        ImGui.TextWrapped(_scanDiagnostics.Summary(_particleEffects.Count));
        ImGui.TextDisabled($"Added: {_scanDiagnostics.NamedConfigEffectsAdded} named, {_scanDiagnostics.RuntimeBlockEffectsAdded} block, {_scanDiagnostics.RuntimeItemEffectsAdded} item, {_scanDiagnostics.EmbeddedEffectsAdded} embedded.");
        ImGui.TextDisabled($"Failures: {_scanDiagnostics.ParseFailures} parse, {_scanDiagnostics.TextReadFailures} text.");
    }

    private void DrawParticleLiveControls(string id, ParticleEffectEntry entry, DebugWindowManager.DevToolsLiveApplyManager? liveApplyManager)
    {
        if (liveApplyManager == null) return;

        string targetKey = GetParticleLiveTargetKey(entry, out CollectibleObject? collectible, out int index);
        bool available = collectible != null && index >= 0;
        if (available && liveApplyManager.AutoApply)
        {
            ApplyParticleLive(entry, liveApplyManager);
        }

        liveApplyManager.DrawTargetControls(
            $"particle-live-{id}-{entry.Key}",
            string.IsNullOrWhiteSpace(targetKey) ? entry.Key : targetKey,
            entry.DisplayKey,
            available,
            () => ApplyParticleLive(entry, liveApplyManager),
            () => liveApplyManager.Revert(targetKey));
    }

    private string ApplyParticleLive(ParticleEffectEntry entry, DebugWindowManager.DevToolsLiveApplyManager liveApplyManager)
    {
        string targetKey = GetParticleLiveTargetKey(entry, out CollectibleObject? collectible, out int index);
        if (collectible == null || index < 0)
        {
            liveApplyManager.LastStatus = "Live target unavailable for this particle.";
            return liveApplyManager.LastStatus;
        }

        return liveApplyManager.Apply(
            targetKey,
            entry.DisplayKey,
            () => CaptureParticleLiveSnapshot(targetKey, collectible),
            () => ApplyParticlePropertiesToCollectible(collectible, index, entry.Properties),
            $"Live applied {entry.DisplayKey}.");
    }

    private DebugWindowManager.LivePatchSnapshot CaptureParticleLiveSnapshot(string targetKey, CollectibleObject collectible)
    {
        AdvancedParticleProperties[]? original = CloneParticleArray(collectible.ParticleProperties);
        string domain = collectible.Code?.Domain ?? "game";
        string path = collectible.Code?.Path ?? SanitizeBackupFileName(targetKey);
        string backupPath = Path.Combine("assets", domain, "runtime-particles", SanitizeBackupFileName(path) + ".json");
        return new(
            () => collectible.ParticleProperties = CloneParticleArray(original),
            backupPath,
            () => SerializeParticleBackup(original));
    }

    private static void ApplyParticlePropertiesToCollectible(CollectibleObject collectible, int index, AdvancedParticleProperties edited)
    {
        AdvancedParticleProperties[] particles = CloneParticleArray(collectible.ParticleProperties) ?? [];
        if (index >= particles.Length)
        {
            Array.Resize(ref particles, index + 1);
        }

        particles[index] = edited.Clone();
        collectible.ParticleProperties = particles;
    }

    private string GetParticleLiveTargetKey(ParticleEffectEntry entry, out CollectibleObject? collectible, out int index)
    {
        collectible = null;
        index = -1;
        if (TryGetRuntimeCollectibleCode(entry, "block", out string blockCode))
        {
            collectible = ResolveBlock(blockCode, null);
            index = GetParticleIndex(entry.Key);
            return $"particle:block:{blockCode}";
        }

        if (TryGetRuntimeCollectibleCode(entry, "item", out string itemCode))
        {
            collectible = ResolveItem(itemCode, null);
            index = GetParticleIndex(entry.Key);
            return $"particle:item:{itemCode}";
        }

        return "";
    }

    private static int GetParticleIndex(string key)
    {
        const string marker = "ParticleProperties[";
        int start = key.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return -1;
        start += marker.Length;
        int end = key.IndexOf(']', start);
        return end > start && int.TryParse(key[start..end], out int index) ? index : -1;
    }

    private static AdvancedParticleProperties[]? CloneParticleArray(AdvancedParticleProperties[]? particles)
    {
        return particles?.Select(particle => particle?.Clone()).Where(particle => particle != null).Cast<AdvancedParticleProperties>().ToArray();
    }

    private static string SerializeParticleBackup(AdvancedParticleProperties[]? particles)
    {
        JsonSerializerSettings settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        JsonSerializer serializer = JsonSerializer.Create(settings);
        JToken token = particles == null ? new JArray() : JToken.FromObject(particles, serializer);
        RemoveParticleBackupPropertyRecursive(token, "VelocityEvolve");
        RemoveParticleBackupPropertyRecursive(token, "velocityEvolve");
        return token.ToString(Formatting.Indented);
    }

    private static void RemoveParticleBackupPropertyRecursive(JToken token, string propertyName)
    {
        if (token is JObject obj)
        {
            obj.Property(propertyName, StringComparison.OrdinalIgnoreCase)?.Remove();
            foreach (JProperty property in obj.Properties().ToList())
            {
                RemoveParticleBackupPropertyRecursive(property.Value, propertyName);
            }
        }
        else if (token is JArray array)
        {
            foreach (JToken child in array)
            {
                RemoveParticleBackupPropertyRecursive(child, propertyName);
            }
        }
    }

    private static string SanitizeBackupFileName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return value.Replace(':', '_').Replace('/', '_').Replace('\\', '_').Replace('#', '_');
    }
#endif

    private string _selectedParticleEffectKey = "";
    private string _particleFilter = "";
    private bool _previewLoop = true;
    private float _previewEmitRate = 1f;
    private float _previewIntensity = 1f;
    private float _previewTimeScale = 1f;
    private float _previewWorldOffset = 2f;
    private float _previewVelocityX;
    private float _previewVelocityY;
    private float _previewVelocityZ;
    private float _previewYaw = -0.55f;
    private float _previewPitch = 0.35f;
    private float _previewDistance = 4.5f;
    private Vector3 _previewTarget = new(0f, 0.8f, 0f);
    private bool _previewWindEnabled;
    private float _previewWindX = 1f;
    private float _previewWindY;
    private float _previewWindZ = 0.25f;
    private float _previewWindStrength = 1f;
    private float _previewWindTurbulence = 0.25f;
    private float _previewWindPhase;
    private bool _previewShowReferenceModel = true;
    private bool _previewReferenceWireframe;
    private float _previewReferenceOpacity = 0.35f;
    private string _previewManualReferenceFilter = "";
    private string _previewManualReferenceKey = "";
    private int _previewManualReferenceIndex;
    private float _previewEmitAccumulator;
    private string _previewEffectKey = "";
    private readonly List<ParticlePreviewParticle> _previewParticles = [];
    private readonly Dictionary<string, ParticlePreviewPlacement> _previewPlacementCache = new(StringComparer.OrdinalIgnoreCase);
    private DevToolsPreview3DRenderer? _previewRenderer3D;
    private readonly Random _previewRandom = new(42);
    private readonly ICoreAPI _api;
    private readonly Dictionary<string, ParticleEffectEntry> _particleEffects = new(StringComparer.OrdinalIgnoreCase);
    private ParticleAssetScanDiagnostics _scanDiagnostics = new();
    private bool _verboseParticleScanLog;
    private readonly IClientNetworkChannel? _clientChannel;
    private const string _networkChannelId = "InGameDevTools:particle-effects";

#if DEBUG
    private string _particleDomainFilter = "";
    private readonly ImGuiThreePanelLayoutState _layout = new(0.22f, 0.42f);
#endif

    private enum ParticleEffectSourceKind
    {
        NamedConfig,
        EmbeddedAsset,
        RuntimeBlock,
        RuntimeItem
    }

    private sealed class ParticleEffectEntry
    {
        public ParticleEffectEntry(string key, string source, ParticleEffectSourceKind sourceKind, AdvancedParticleProperties properties)
        {
            Key = key;
            Source = source;
            SourceKind = sourceKind;
            Properties = properties;
#if DEBUG
            Domain = ImGuiLayoutHelper.GetDomainFromAssetCode(key);
            DisplayKey = ImGuiLayoutHelper.CompactAssetCode(key);
#endif
        }

        public string Key { get; }
        public string Source { get; }
#if DEBUG
        public string Domain { get; }
        public string DisplayKey { get; }
#endif
        public ParticleEffectSourceKind SourceKind { get; }
        public AdvancedParticleProperties Properties { get; }
        public string SourceKindLabel => SourceKind switch
        {
            ParticleEffectSourceKind.NamedConfig => "Named config",
            ParticleEffectSourceKind.RuntimeBlock => "Loaded block",
            ParticleEffectSourceKind.RuntimeItem => "Loaded item",
            _ => "Embedded asset"
        };
    }

    private sealed class ParticleAssetScanDiagnostics
    {
        public int AllAssets;
        public int UniqueAssets;
        public int JsonCandidateAssets;
        public int TextReadFailures;
        public int AssetsWithParticleProperties;
        public int RuntimeBlocksScanned;
        public int RuntimeItemsScanned;
        public int RuntimeBlocksWithParticleProperties;
        public int RuntimeItemsWithParticleProperties;
        public int RuntimeBlockEffectsAdded;
        public int RuntimeItemEffectsAdded;
        public int NamedConfigAssets;
        public int NamedConfigEffectsAdded;
        public int EmbeddedEffectsAdded;
        public int ParseFailures;
        public int DuplicateKeys;
        public List<string> AddedSamples { get; } = [];
        public List<string> SkippedSamples { get; } = [];

        public string Summary(int totalEffects)
        {
            return $"Loaded {totalEffects} effects. Registry: {RuntimeBlocksWithParticleProperties}/{RuntimeBlocksScanned} blocks, {RuntimeItemsWithParticleProperties}/{RuntimeItemsScanned} items. Assets: {AllAssets} scanned ({UniqueAssets} unique), {JsonCandidateAssets} JSON candidates, {AssetsWithParticleProperties} raw particle assets, {NamedConfigAssets} named configs, {DuplicateKeys} duplicate keys.";
        }
    }

#if DEBUG
    private void DrawPreviewPanel(string id, string key, AdvancedParticleProperties particleProperties, float deltaSeconds)
    {
        if (!string.Equals(_previewEffectKey, key, StringComparison.OrdinalIgnoreCase))
        {
            ResetPreview(key);
        }

        ImGui.SeparatorText("Preview");
        if (ImGui.Button($"Emit##particle-preview-{id}"))
        {
            EmitPreviewParticles(particleProperties, _previewIntensity, GetPreviewParticleOrigin(key));
        }

        ImGui.SameLine();
        if (ImGui.Button($"Reset##particle-preview-{id}"))
        {
            ResetPreview(key);
        }

        ImGui.SameLine();
        if (ImGui.Button($"Reset camera##particle-preview-{id}"))
        {
            ResetPreviewCamera(key);
        }

        ImGui.SameLine();
        ImGui.Checkbox($"Loop##particle-preview-{id}", ref _previewLoop);

        ImGui.Checkbox($"Reference model##particle-reference-{id}", ref _previewShowReferenceModel);
        ImGui.SameLine();
        ImGui.Checkbox($"Wireframe overlay##particle-reference-{id}", ref _previewReferenceWireframe);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.SliderFloat($"Opacity##particle-reference-{id}", ref _previewReferenceOpacity, 0.05f, 0.9f, "%.2f");

        DrawManualReferencePicker(id, key);

        ImGui.SetNextItemWidth(110);
        ImGui.SliderFloat($"Rate##particle-preview-{id}", ref _previewEmitRate, 0.05f, 20f, "%.2f/s");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.SliderFloat($"Intensity##particle-preview-{id}", ref _previewIntensity, 0.05f, 8f, "%.2f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.SliderFloat($"Speed##particle-preview-{id}", ref _previewTimeScale, 0.05f, 4f, "%.2fx");

        ImGui.SetNextItemWidth(110);
        ImGui.SliderFloat($"World offset##particle-preview-{id}", ref _previewWorldOffset, 0.25f, 12f, "%.2f");
        ImGui.SameLine();
        if (ImGui.Button($"Spawn in world##particle-preview-{id}"))
        {
            SpawnDebugParticles(key, _previewWorldOffset, new Vector3(_previewVelocityX, _previewVelocityY, _previewVelocityZ), _previewIntensity);
        }

        ImGui.SetNextItemWidth(100);
        ImGui.InputFloat($"Velocity X##particle-preview-{id}", ref _previewVelocityX, 0, 0, "%.2f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.InputFloat($"Y##particle-preview-{id}", ref _previewVelocityY, 0, 0, "%.2f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.InputFloat($"Z##particle-preview-{id}", ref _previewVelocityZ, 0, 0, "%.2f");

        if (ImGui.CollapsingHeader($"Wind preview##particle-preview-wind-{id}", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Checkbox($"Enable wind##particle-preview-{id}", ref _previewWindEnabled);
            ImGui.SameLine();
            ImGui.TextDisabled($"Effect wind affect: {particleProperties.WindAffectednes:0.00}");

            ImGui.SetNextItemWidth(100);
            ImGui.InputFloat($"Wind X##particle-preview-{id}", ref _previewWindX, 0, 0, "%.2f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.InputFloat($"Y##particle-preview-wind-{id}", ref _previewWindY, 0, 0, "%.2f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.InputFloat($"Z##particle-preview-wind-{id}", ref _previewWindZ, 0, 0, "%.2f");

            ImGui.SetNextItemWidth(130);
            ImGui.SliderFloat($"Strength##particle-preview-wind-{id}", ref _previewWindStrength, 0f, 8f, "%.2f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(130);
            ImGui.SliderFloat($"Turbulence##particle-preview-wind-{id}", ref _previewWindTurbulence, 0f, 2f, "%.2f");

            if (ImGui.Button($"Set east wind##particle-preview-wind-{id}"))
            {
                _previewWindX = 1f;
                _previewWindY = 0f;
                _previewWindZ = 0f;
            }

            ImGui.SameLine();
            if (ImGui.Button($"Set north wind##particle-preview-wind-{id}"))
            {
                _previewWindX = 0f;
                _previewWindY = 0f;
                _previewWindZ = 1f;
            }
        }

        float dt = Math.Clamp(deltaSeconds, 0f, 0.1f) * _previewTimeScale;
        if (_previewLoop && dt > 0)
        {
            Vector3 particleOrigin = GetPreviewParticleOrigin(key);
            _previewEmitAccumulator += dt * _previewEmitRate;
            while (_previewEmitAccumulator >= 1f)
            {
                EmitPreviewParticles(particleProperties, _previewIntensity, particleOrigin);
                _previewEmitAccumulator -= 1f;
            }
        }

        UpdatePreviewParticles(particleProperties, dt);
        DrawPreviewViewport(id, key, particleProperties);
    }

    private void DrawManualReferencePicker(string id, string key)
    {
        if (!_particleEffects.TryGetValue(key, out ParticleEffectEntry? entry)) return;
        if (!string.IsNullOrWhiteSpace(GetAutomaticReferenceCacheKey(entry))) return;

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint($"##particle-manual-reference-filter-{id}", "manual reference filter", ref _previewManualReferenceFilter, 200);
        string filter = _previewManualReferenceFilter.Trim();
        List<string> options = BuildManualReferenceOptions(filter).Take(250).ToList();
        if (options.Count == 0)
        {
            ImGui.TextDisabled("No block/item reference matches the filter.");
            return;
        }

        _previewManualReferenceIndex = Math.Clamp(_previewManualReferenceIndex, 0, options.Count - 1);
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo($"Manual reference##particle-manual-reference-{id}", ref _previewManualReferenceIndex, options.ToArray(), options.Count))
        {
            _previewManualReferenceKey = options[_previewManualReferenceIndex];
            ClearPreviewReferenceModelCache();
            ResetPreview(key);
        }
    }

    private IEnumerable<string> BuildManualReferenceOptions(string filter)
    {
        foreach (Block block in _api.World.Blocks)
        {
            if (block?.Code == null) continue;
            string key = $"block:{block.Code}";
            if (string.IsNullOrWhiteSpace(filter) || key.Contains(filter, StringComparison.OrdinalIgnoreCase)) yield return key;
        }

        foreach (Item item in _api.World.Items)
        {
            if (item?.Code == null) continue;
            string key = $"item:{item.Code}";
            if (string.IsNullOrWhiteSpace(filter) || key.Contains(filter, StringComparison.OrdinalIgnoreCase)) yield return key;
        }
    }

    private void DrawPreviewViewport(string id, string key, AdvancedParticleProperties particleProperties)
    {
        System.Numerics.Vector2 available = ImGui.GetContentRegionAvail();
        System.Numerics.Vector2 size = new(Math.Max(320f, available.X), Math.Max(260f, available.Y));
        ImGui.InvisibleButton($"##particle-preview-viewport-{id}", size);
        System.Numerics.Vector2 min = ImGui.GetItemRectMin();
        System.Numerics.Vector2 max = ImGui.GetItemRectMax();
        bool hovered = ImGui.IsItemHovered();

        if (hovered)
        {
            System.Numerics.Vector2 delta = ImGui.GetIO().MouseDelta;
            bool pan = ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
                (ImGui.IsMouseDragging(ImGuiMouseButton.Right) &&
                    (ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift)));

            if (pan)
            {
                DevToolsPreviewCamera camera = BuildPreviewCamera(min, max);
                float panScale = _previewDistance / Math.Max(120f, size.Y);
                _previewTarget -= camera.Right * delta.X * panScale;
                _previewTarget += camera.Up * delta.Y * panScale;
            }
            else if (ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                _previewYaw += delta.X * 0.01f;
                _previewPitch = Math.Clamp(_previewPitch + delta.Y * 0.01f, -1.45f, 1.45f);
            }

            float wheel = ImGui.GetIO().MouseWheel;
            if (Math.Abs(wheel) > 0.001f)
            {
                _previewDistance = Math.Clamp(_previewDistance * MathF.Pow(0.88f, wheel), 0.35f, 48f);
            }
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint background = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.035f, 0.036f, 0.032f, 1f));
        uint border = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.55f, 0.49f, 0.38f, 1f));
        uint grid = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.28f, 0.27f, 0.22f, 0.55f));
        uint axisX = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.85f, 0.25f, 0.16f, 0.9f));
        uint axisY = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.32f, 0.9f, 0.34f, 0.9f));
        uint axisZ = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.25f, 0.42f, 0.95f, 0.9f));
        uint windColor = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.4f, 0.82f, 1f, 0.92f));
        uint text = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.86f, 0.82f, 0.72f, 1f));

        drawList.AddRectFilled(min, max, background, 4f);
        drawList.AddRect(min, max, border, 4f);

        DevToolsPreviewCamera previewCamera = BuildPreviewCamera(min, max);
        ParticlePreviewPlacement placement = GetPreviewPlacement(key);
        ParticleReferenceModel? referenceModel = _previewShowReferenceModel ? placement.ReferenceModel : null;
        Vector3 previewOrigin = placement.ParticleOrigin;
        if (referenceModel?.Mesh != null)
        {
            Matrixf identity = new();
            identity.Identity();
            int textureId = EnsurePreviewRenderer3D().RenderToTexture(
                max.X - min.X,
                max.Y - min.Y,
                previewCamera,
                [new DevToolsPreviewMeshInstance(referenceModel.Mesh, identity)],
                out string? skipReason);
            if (textureId > 0)
            {
                drawList.AddImage(new IntPtr(textureId), min, max, new System.Numerics.Vector2(0f, 1f), new System.Numerics.Vector2(1f, 0f));
            }
            else if (!string.IsNullOrWhiteSpace(skipReason))
            {
                drawList.AddText(min + new System.Numerics.Vector2(12f, 90f), text, $"Reference render skipped: {skipReason}");
            }
        }

        drawList.PushClipRect(min, max, true);
        DrawPreviewGrid(drawList, previewCamera, grid, previewOrigin);
        DrawPreviewLine(drawList, previewCamera, previewOrigin, previewOrigin + new Vector3(1.5f, 0, 0), axisX, 2f);
        DrawPreviewLine(drawList, previewCamera, previewOrigin, previewOrigin + new Vector3(0, 1.5f, 0), axisY, 2f);
        DrawPreviewLine(drawList, previewCamera, previewOrigin, previewOrigin + new Vector3(0, 0, 1.5f), axisZ, 2f);
        DrawPreviewTextAt(drawList, previewCamera, previewOrigin + new Vector3(1.65f, 0, 0), axisX, "X");
        DrawPreviewTextAt(drawList, previewCamera, previewOrigin + new Vector3(0, 1.65f, 0), axisY, "Y");
        DrawPreviewTextAt(drawList, previewCamera, previewOrigin + new Vector3(0, 0, 1.65f), axisZ, "Z");

        if (referenceModel != null && _previewReferenceWireframe)
        {
            DrawReferenceModel(drawList, previewCamera, referenceModel);
        }

        if (_previewWindEnabled)
        {
            Vector3 wind = GetPreviewWindVector(particleProperties);
            if (wind.LengthSquared > 0.0001f)
            {
                Vector3 direction = Vector3.Normalize(wind);
                float length = Math.Clamp(wind.Length * 0.45f, 0.4f, 2.4f);
                Vector3 start = previewOrigin;
                Vector3 end = start + direction * length;
                DrawPreviewLine(drawList, previewCamera, start, end, windColor, 3f);
                DrawPreviewTextAt(drawList, previewCamera, end + direction * 0.08f, windColor, "wind");
            }
        }

        List<ParticlePreviewDrawParticle> drawParticles = [];
        foreach (ParticlePreviewParticle particle in _previewParticles)
        {
            if (!ProjectPreviewPoint(previewCamera, particle.Position, out System.Numerics.Vector2 point, out float depth)) continue;
            drawParticles.Add(new(particle, point, depth));
        }

        foreach (ParticlePreviewDrawParticle projected in drawParticles.OrderByDescending(particle => particle.Depth))
        {
            ParticlePreviewParticle particle = projected.Particle;
            float lifeT = Math.Clamp(particle.Age / Math.Max(0.001f, particle.Life), 0f, 1f);
            float alpha = Math.Clamp(1f - lifeT, 0f, 1f);
            float particleSize = Math.Clamp(particle.Size * previewCamera.FocalLength / Math.Max(0.05f, projected.Depth), 1.5f, 96f);
            System.Numerics.Vector2 point = projected.ScreenPosition;
            uint color = PreviewColor(particleProperties, alpha);

            if (particleProperties.ParticleModel == EnumParticleModel.Cube)
            {
                drawList.AddRectFilled(point - new System.Numerics.Vector2(particleSize, particleSize), point + new System.Numerics.Vector2(particleSize, particleSize), color, 1f);
            }
            else
            {
                drawList.AddCircleFilled(point, particleSize, color, 18);
            }
        }
        drawList.PopClipRect();

        drawList.AddText(min + new System.Numerics.Vector2(12f, 10f), text, $"{_previewParticles.Count} preview particles");
        drawList.AddText(min + new System.Numerics.Vector2(12f, 30f), text, "RMB orbits. MMB or Shift+RMB pans. Mouse wheel zooms.");
        string referenceText = referenceModel == null
            ? "Reference model: none"
            : $"Reference model: {referenceModel.Label}";
        drawList.AddText(min + new System.Numerics.Vector2(12f, 50f), text, referenceText);
        drawList.AddText(min + new System.Numerics.Vector2(12f, 70f), text, _previewWindEnabled ? "Wind preview enabled" : "Wind preview disabled");
    }

    private DevToolsPreviewCamera BuildPreviewCamera(System.Numerics.Vector2 min, System.Numerics.Vector2 max)
    {
        return DevToolsPreviewCamera.Orbit(min, max, _previewTarget, _previewYaw, _previewPitch, _previewDistance);
    }

    private static bool ProjectPreviewPoint(DevToolsPreviewCamera camera, Vector3 point, out System.Numerics.Vector2 screen, out float depth)
    {
        return camera.Project(point, out screen, out depth);
    }

    private static void DrawPreviewGrid(ImDrawListPtr drawList, DevToolsPreviewCamera camera, uint color, Vector3 origin)
    {
        const int extent = 8;
        for (int i = -extent; i <= extent; i++)
        {
            uint lineColor = i == 0 ? color | 0x55000000 : color;
            DrawPreviewLine(drawList, camera, new Vector3(origin.X - extent, origin.Y, origin.Z + i), new Vector3(origin.X + extent, origin.Y, origin.Z + i), lineColor, i == 0 ? 1.8f : 1f);
            DrawPreviewLine(drawList, camera, new Vector3(origin.X + i, origin.Y, origin.Z - extent), new Vector3(origin.X + i, origin.Y, origin.Z + extent), lineColor, i == 0 ? 1.8f : 1f);
        }
    }

    private static void DrawPreviewLine(ImDrawListPtr drawList, DevToolsPreviewCamera camera, Vector3 start, Vector3 end, uint color, float thickness)
    {
        if (!ProjectPreviewPoint(camera, start, out System.Numerics.Vector2 startScreen, out _) ||
            !ProjectPreviewPoint(camera, end, out System.Numerics.Vector2 endScreen, out _))
        {
            return;
        }

        drawList.AddLine(startScreen, endScreen, color, thickness);
    }

    private static void DrawPreviewTextAt(ImDrawListPtr drawList, DevToolsPreviewCamera camera, Vector3 position, uint color, string text)
    {
        if (ProjectPreviewPoint(camera, position, out System.Numerics.Vector2 screen, out _))
        {
            drawList.AddText(screen, color, text);
        }
    }

    private ParticleReferenceModel? GetPreviewReferenceModel(string key)
    {
        return GetPreviewPlacement(key).ReferenceModel;
    }

    private Vector3 GetPreviewParticleOrigin(string key)
    {
        return GetPreviewPlacement(key).ParticleOrigin;
    }

    private ParticlePreviewPlacement GetPreviewPlacement(string key)
    {
        if (!_particleEffects.TryGetValue(key, out ParticleEffectEntry? entry))
        {
            return ParticlePreviewPlacement.Unanchored;
        }

        string cacheKey = GetReferenceCacheKey(entry);
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return ParticlePreviewPlacement.Unanchored;
        }

        if (_previewPlacementCache.TryGetValue(cacheKey, out ParticlePreviewPlacement? cached))
        {
            return cached;
        }

        ParticlePreviewPlacement placement = BuildPreviewPlacement(entry);
        _previewPlacementCache[cacheKey] = placement;
        return placement;
    }

    private bool TryResolveManualReferenceCollectible(out CollectibleObject? collectible)
    {
        collectible = null;
        if (string.IsNullOrWhiteSpace(_previewManualReferenceKey)) return false;

        if (_previewManualReferenceKey.StartsWith("block:", StringComparison.OrdinalIgnoreCase))
        {
            collectible = ResolveBlock(_previewManualReferenceKey["block:".Length..], null);
            return collectible != null;
        }

        if (_previewManualReferenceKey.StartsWith("item:", StringComparison.OrdinalIgnoreCase))
        {
            collectible = ResolveItem(_previewManualReferenceKey["item:".Length..], null);
            return collectible != null;
        }

        return false;
    }

    private string GetReferenceCacheKey(ParticleEffectEntry entry)
    {
        string automatic = GetAutomaticReferenceCacheKey(entry);
        if (!string.IsNullOrWhiteSpace(automatic)) return automatic;
        return _previewManualReferenceKey;
    }

    private string GetAutomaticReferenceCacheKey(ParticleEffectEntry entry)
    {
        if (TryGetRuntimeCollectibleCode(entry, "block", out string blockCode))
        {
            return $"block:{blockCode}";
        }

        if (TryGetRuntimeCollectibleCode(entry, "item", out string itemCode))
        {
            return $"item:{itemCode}";
        }

        if (TryGetEmbeddedAssetLocation(entry, out AssetLocation? assetLocation))
        {
            return assetLocation.ToString();
        }

        return "";
    }

    private ParticlePreviewPlacement BuildPreviewPlacement(ParticleEffectEntry entry)
    {
        if (_api is not ICoreClientAPI clientApi)
        {
            return ParticlePreviewPlacement.Unanchored;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(GetAutomaticReferenceCacheKey(entry)) &&
                TryResolveManualReferenceCollectible(out CollectibleObject? manualReference) &&
                manualReference != null)
            {
                return BuildCollectiblePreviewPlacement(clientApi, manualReference);
            }

            if (TryResolveReferenceBlock(entry, out Block? block) && block?.Code != null)
            {
                return BuildCollectiblePreviewPlacement(clientApi, block);
            }

            if (TryResolveReferenceItem(entry, out Item? item) && item?.Code != null)
            {
                return BuildCollectiblePreviewPlacement(clientApi, item);
            }

            if (TryResolveReferenceEntity(entry, out EntityProperties? entityType) && entityType?.Client?.LoadedShapeForEntity != null)
            {
                Shape shape = entityType.Client.LoadedShapeForEntity;
                ShapeTextureSource textureSource = new(clientApi, shape, entityType.Code.ToString());
                CompositeShape? compositeShape = entityType.Client.ShapeForEntity ?? entityType.Client.Shape;
                TesselationMetaData meta = new()
                {
                    TexSource = textureSource,
                    WithJointIds = false,
                    WithDamageEffect = false,
                    TypeForLogging = entityType.Code.ToString(),
                    QuantityElements = compositeShape?.QuantityElements,
                    SelectiveElements = compositeShape?.SelectiveElements,
                    IgnoreElements = compositeShape?.IgnoreElements,
                    Rotation = compositeShape == null
                        ? null
                        : new Vec3f(compositeShape.rotateX, compositeShape.rotateY, compositeShape.rotateZ)
                };
                clientApi.Tesselator.TesselateShape(meta, shape, out MeshData mesh);
                if (compositeShape != null)
                {
                    mesh.Translate(compositeShape.offsetX, compositeShape.offsetY, compositeShape.offsetZ);
                }

                ParticleReferenceModel? entityModel = BuildReferenceModelFromMesh(clientApi, $"entity:{entityType.Code}", mesh, null);
                return new ParticlePreviewPlacement(entityModel, entityModel?.ParticleOrigin ?? Vector3.Zero);
            }
        }
        catch (Exception exception)
        {
            LoggerUtil.Verbose(_api, this, $"Particle reference model failed for '{entry.Key}': {exception.Message}");
        }

        return ParticlePreviewPlacement.Unanchored;
    }

    private ParticlePreviewPlacement BuildCollectiblePreviewPlacement(ICoreClientAPI clientApi, CollectibleObject collectible)
    {
        MeshData mesh;
        string label;
        if (collectible is Block block)
        {
            clientApi.Tesselator.TesselateBlock(block, out mesh);
            label = $"block:{block.Code}";
        }
        else if (collectible is Item item)
        {
            clientApi.Tesselator.TesselateItem(item, out mesh);
            label = $"item:{item.Code}";
        }
        else
        {
            return ParticlePreviewPlacement.Unanchored;
        }

        ParticleReferenceModel? model = BuildReferenceModelFromMesh(clientApi, label, mesh, collectible);
        if (model == null)
        {
            return new ParticlePreviewPlacement(null, GetCollectibleParticleOrigin(collectible, DevToolsPreviewBounds.Empty));
        }

        return new ParticlePreviewPlacement(model, model.ParticleOrigin);
    }

    private bool TryResolveReferenceBlock(ParticleEffectEntry entry, out Block? block)
    {
        block = null;
        if (TryGetRuntimeCollectibleCode(entry, "block", out string runtimeCode))
        {
            block = ResolveBlock(runtimeCode, null);
            return block != null;
        }

        if (!TryGetEmbeddedAssetLocation(entry, out AssetLocation? assetLocation) ||
            !assetLocation.Path.StartsWith("blocktypes/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? declaredCode = TryReadDeclaredAssetCode(assetLocation);
        block = !string.IsNullOrWhiteSpace(declaredCode)
            ? ResolveBlock(declaredCode, assetLocation.Domain)
            : ResolveBlock(LastPathSegmentWithoutExtension(assetLocation.Path), assetLocation.Domain);

        block ??= FindBlockByPathHint(assetLocation);
        return block != null;
    }

    private bool TryResolveReferenceItem(ParticleEffectEntry entry, out Item? item)
    {
        item = null;
        if (TryGetRuntimeCollectibleCode(entry, "item", out string runtimeCode))
        {
            item = ResolveItem(runtimeCode, null);
            return item != null;
        }

        if (!TryGetEmbeddedAssetLocation(entry, out AssetLocation? assetLocation) ||
            !assetLocation.Path.StartsWith("itemtypes/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? declaredCode = TryReadDeclaredAssetCode(assetLocation);
        item = !string.IsNullOrWhiteSpace(declaredCode)
            ? ResolveItem(declaredCode, assetLocation.Domain)
            : ResolveItem(LastPathSegmentWithoutExtension(assetLocation.Path), assetLocation.Domain);

        item ??= FindItemByPathHint(assetLocation);
        return item != null;
    }

    private bool TryResolveReferenceEntity(ParticleEffectEntry entry, out EntityProperties? entityType)
    {
        entityType = null;
        if (!TryGetEmbeddedAssetLocation(entry, out AssetLocation? assetLocation) ||
            !assetLocation.Path.StartsWith("entities/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? declaredCode = TryReadDeclaredAssetCode(assetLocation);
        if (!string.IsNullOrWhiteSpace(declaredCode))
        {
            entityType = _api.World.GetEntityType(AssetLocation.Create(declaredCode, assetLocation.Domain));
            if (entityType != null) return true;

            entityType = _api.World.GetEntityType(AssetLocation.Create(declaredCode, "game"));
            if (entityType != null) return true;
        }

        string hint = LastPathSegmentWithoutExtension(assetLocation.Path);
        entityType = _api.World.EntityTypes
            .Where(type => type?.Code != null)
            .FirstOrDefault(type => type.Code.Domain.Equals(assetLocation.Domain, StringComparison.OrdinalIgnoreCase) &&
                MatchesPathHint(type.Code.Path, hint))
            ?? _api.World.EntityTypes
                .Where(type => type?.Code != null)
                .FirstOrDefault(type => MatchesPathHint(type.Code.Path, hint));

        return entityType != null;
    }

    private Block? ResolveBlock(string code, string? defaultDomain)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        AssetLocation location = AssetLocation.Create(code, string.IsNullOrWhiteSpace(defaultDomain) ? "game" : defaultDomain);
        Block? block = _api.World.GetBlock(location);
        if (block != null) return block;

        if (!location.Domain.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            block = _api.World.GetBlock(new AssetLocation("game", location.Path));
        }

        return block;
    }

    private Item? ResolveItem(string code, string? defaultDomain)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        AssetLocation location = AssetLocation.Create(code, string.IsNullOrWhiteSpace(defaultDomain) ? "game" : defaultDomain);
        Item? item = _api.World.GetItem(location);
        if (item != null) return item;

        if (!location.Domain.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            item = _api.World.GetItem(new AssetLocation("game", location.Path));
        }

        return item;
    }

    private Block? FindBlockByPathHint(AssetLocation assetLocation)
    {
        string hint = LastPathSegmentWithoutExtension(assetLocation.Path);
        return _api.World.Blocks
            .Where(block => block?.Code != null)
            .FirstOrDefault(block => block.Code.Domain.Equals(assetLocation.Domain, StringComparison.OrdinalIgnoreCase) &&
                MatchesPathHint(block.Code.Path, hint))
            ?? _api.World.Blocks
                .Where(block => block?.Code != null)
                .FirstOrDefault(block => MatchesPathHint(block.Code.Path, hint));
    }

    private Item? FindItemByPathHint(AssetLocation assetLocation)
    {
        string hint = LastPathSegmentWithoutExtension(assetLocation.Path);
        return _api.World.Items
            .Where(item => item?.Code != null)
            .FirstOrDefault(item => item.Code.Domain.Equals(assetLocation.Domain, StringComparison.OrdinalIgnoreCase) &&
                MatchesPathHint(item.Code.Path, hint))
            ?? _api.World.Items
                .Where(item => item?.Code != null)
                .FirstOrDefault(item => MatchesPathHint(item.Code.Path, hint));
    }

    private string? TryReadDeclaredAssetCode(AssetLocation assetLocation)
    {
        try
        {
            IAsset? asset = _api.Assets.TryGet(assetLocation, true);
            if (asset == null) return null;

            JsonObject json = JsonObject.FromJson(asset.ToText());
            return json.Token is JObject obj ? obj["code"]?.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetRuntimeCollectibleCode(ParticleEffectEntry entry, string prefix, out string code)
    {
        code = "";
        string sourcePrefix = prefix + ":";
        string value = entry.Key.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)
            ? entry.Key
            : entry.Source.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)
                ? entry.Source
                : "";

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value[sourcePrefix.Length..];
        int hash = value.IndexOf('#');
        if (hash >= 0) value = value[..hash];
        code = value.Trim();
        return !string.IsNullOrWhiteSpace(code);
    }

    private static bool TryGetEmbeddedAssetLocation(ParticleEffectEntry entry, out AssetLocation assetLocation)
    {
        string source = entry.Source;
        int hash = source.IndexOf('#');
        if (hash >= 0) source = source[..hash];

        if (source.Contains(':') &&
            (source.Contains("blocktypes/", StringComparison.OrdinalIgnoreCase) ||
             source.Contains("itemtypes/", StringComparison.OrdinalIgnoreCase) ||
             source.Contains("entities/", StringComparison.OrdinalIgnoreCase)))
        {
            assetLocation = new AssetLocation(source);
            return true;
        }

        string key = entry.Key;
        hash = key.IndexOf('#');
        if (hash >= 0) key = key[..hash];
        if (key.Contains(':') &&
            (key.Contains("blocktypes/", StringComparison.OrdinalIgnoreCase) ||
             key.Contains("itemtypes/", StringComparison.OrdinalIgnoreCase) ||
             key.Contains("entities/", StringComparison.OrdinalIgnoreCase)))
        {
            assetLocation = new AssetLocation(key);
            return true;
        }

        assetLocation = new AssetLocation("game", "missing");
        return false;
    }

    private static string LastPathSegmentWithoutExtension(string path)
    {
        string normalized = path.Replace('\\', '/');
        string segment = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? normalized;
        return segment.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? segment[..^5] : segment;
    }

    private static bool MatchesPathHint(string path, string hint)
    {
        return path.Equals(hint, StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("-" + hint, StringComparison.OrdinalIgnoreCase) ||
            path.Contains(hint, StringComparison.OrdinalIgnoreCase);
    }

    private static ParticleReferenceModel? BuildReferenceModelFromMesh(ICoreClientAPI api, string label, MeshData mesh, CollectibleObject? collectible)
    {
        if (mesh.VerticesCount <= 0 || mesh.xyz == null || mesh.Indices == null || mesh.IndicesCount < 3)
        {
            return null;
        }

        EnsureReferenceVertexColor(mesh);
        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);
        for (int vertex = 0; vertex < mesh.VerticesCount; vertex++)
        {
            Vector3 position = ReadMeshVertex(mesh, vertex);
            min = Vector3.ComponentMin(min, position);
            max = Vector3.ComponentMax(max, position);
        }

        List<ParticleReferenceTriangle> triangles = [];
        int triangleCount = mesh.IndicesCount / 3;
        int stride = Math.Max(1, (int)Math.Ceiling(triangleCount / 5000f));
        for (int triangle = 0; triangle < triangleCount; triangle += stride)
        {
            int indexOffset = triangle * 3;
            int ia = mesh.Indices[indexOffset + 0];
            int ib = mesh.Indices[indexOffset + 1];
            int ic = mesh.Indices[indexOffset + 2];
            if (ia < 0 || ib < 0 || ic < 0 ||
                ia >= mesh.VerticesCount || ib >= mesh.VerticesCount || ic >= mesh.VerticesCount)
            {
                continue;
            }

            Vector3 a = ReadMeshVertex(mesh, ia);
            Vector3 b = ReadMeshVertex(mesh, ib);
            Vector3 c = ReadMeshVertex(mesh, ic);
            Vector3 normal = Vector3.Cross(b - a, c - a);
            if (normal.LengthSquared > 0.0001f)
            {
                normal = Vector3.Normalize(normal);
            }
            else
            {
                normal = Vector3.UnitY;
            }

            triangles.Add(new(a, b, c, normal, ReadTriangleColor(mesh, ia, ib, ic)));
        }

        if (triangles.Count == 0) return null;

        DevToolsPreviewBounds bounds = new(min, max);
        Vector3 particleOrigin = collectible == null ? Vector3.Zero : GetCollectibleParticleOrigin(collectible, bounds);
        DevToolsPreviewMesh previewMesh = new(label, api.Render.UploadMultiTextureMesh(mesh), bounds);
        return new ParticleReferenceModel(label, previewMesh, triangles, min, max, particleOrigin);
    }

    private static Vector3 GetCollectibleParticleOrigin(CollectibleObject collectible, DevToolsPreviewBounds meshBounds)
    {
        if (collectible is Block && TryGetCollectibleTopMiddle(collectible, out Vector3 blockOrigin))
        {
            return blockOrigin;
        }

        if (meshBounds.IsValid)
        {
            return DevToolsPreviewPlacement.TopCenter(meshBounds);
        }

        if (TryGetCollectibleTopMiddle(collectible, out Vector3 origin))
        {
            return origin;
        }

        return new Vector3(0.5f, 1f, 0.5f);
    }

    private static bool TryGetCollectibleTopMiddle(CollectibleObject collectible, out Vector3 origin)
    {
        origin = Vector3.Zero;
        FastVec3f? topMiddle = collectible.TopMiddlePos;
        if (!topMiddle.HasValue) return false;

        FastVec3f value = topMiddle.Value;
        origin = new Vector3(value.X, value.Y, value.Z);
        return true;
    }

    private static void EnsureReferenceVertexColor(MeshData mesh)
    {
        int requiredLength = mesh.VerticesCount * 4;
        if (requiredLength <= 0) return;

        if (mesh.Rgba == null || mesh.Rgba.Length < requiredLength)
        {
            mesh.Rgba = new byte[requiredLength];
            FillReferenceVertexColor(mesh.Rgba);
            return;
        }

        bool hasVisibleColor = false;
        for (int index = 0; index + 3 < requiredLength; index += 4)
        {
            if (mesh.Rgba[index + 3] == 0) continue;
            if ((mesh.Rgba[index] | mesh.Rgba[index + 1] | mesh.Rgba[index + 2]) == 0) continue;
            hasVisibleColor = true;
            break;
        }

        if (!hasVisibleColor)
        {
            FillReferenceVertexColor(mesh.Rgba);
            return;
        }

        for (int index = 3; index < requiredLength; index += 4)
        {
            if (mesh.Rgba[index] == 0) mesh.Rgba[index] = 255;
        }
    }

    private static void FillReferenceVertexColor(byte[] rgba)
    {
        for (int index = 0; index + 3 < rgba.Length; index += 4)
        {
            rgba[index] = 255;
            rgba[index + 1] = 255;
            rgba[index + 2] = 255;
            rgba[index + 3] = 255;
        }
    }

    private static Vector3 ReadMeshVertex(MeshData mesh, int vertex)
    {
        int offset = vertex * 3;
        return new Vector3(mesh.xyz[offset + 0], mesh.xyz[offset + 1], mesh.xyz[offset + 2]);
    }

    private static System.Numerics.Vector4 ReadTriangleColor(MeshData mesh, int ia, int ib, int ic)
    {
        if (mesh.Rgba == null || mesh.Rgba.Length < (Math.Max(ia, Math.Max(ib, ic)) + 1) * 4)
        {
            return new System.Numerics.Vector4(0.58f, 0.48f, 0.34f, 1f);
        }

        System.Numerics.Vector4 color = ReadVertexColor(mesh.Rgba, ia) + ReadVertexColor(mesh.Rgba, ib) + ReadVertexColor(mesh.Rgba, ic);
        color /= 3f;
        if (color.W <= 0.001f || (color.X + color.Y + color.Z) <= 0.001f)
        {
            return new System.Numerics.Vector4(0.58f, 0.48f, 0.34f, 1f);
        }

        return color;
    }

    private static System.Numerics.Vector4 ReadVertexColor(byte[] rgba, int vertex)
    {
        int offset = vertex * 4;
        return new System.Numerics.Vector4(
            rgba[offset + 0] / 255f,
            rgba[offset + 1] / 255f,
            rgba[offset + 2] / 255f,
            rgba[offset + 3] / 255f);
    }

    private void DrawReferenceModel(ImDrawListPtr drawList, DevToolsPreviewCamera camera, ParticleReferenceModel model)
    {
        List<ParticleReferenceProjectedTriangle> projectedTriangles = [];
        foreach (ParticleReferenceTriangle triangle in model.Triangles)
        {
            if (!ProjectPreviewPoint(camera, triangle.A, out System.Numerics.Vector2 a, out float depthA) ||
                !ProjectPreviewPoint(camera, triangle.B, out System.Numerics.Vector2 b, out float depthB) ||
                !ProjectPreviewPoint(camera, triangle.C, out System.Numerics.Vector2 c, out float depthC))
            {
                continue;
            }

            float depth = (depthA + depthB + depthC) / 3f;
            projectedTriangles.Add(new(a, b, c, depth, triangle.Normal, triangle.Color));
        }

        uint lineColor = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.92f, 0.82f, 0.58f, 0.72f));
        foreach (ParticleReferenceProjectedTriangle triangle in projectedTriangles.OrderByDescending(triangle => triangle.Depth))
        {
            float shade = 0.48f + Math.Abs(Vector3.Dot(triangle.Normal, Vector3.Normalize(new Vector3(-0.35f, 0.85f, -0.38f)))) * 0.52f;
            System.Numerics.Vector4 fill = new(
                Math.Clamp(triangle.Color.X * shade, 0f, 1f),
                Math.Clamp(triangle.Color.Y * shade, 0f, 1f),
                Math.Clamp(triangle.Color.Z * shade, 0f, 1f),
                Math.Clamp(_previewReferenceOpacity * triangle.Color.W, 0.02f, 0.95f));

            if (!_previewReferenceWireframe)
            {
                drawList.AddTriangleFilled(triangle.A, triangle.B, triangle.C, ImGui.ColorConvertFloat4ToU32(fill));
            }

            drawList.AddTriangle(triangle.A, triangle.B, triangle.C, lineColor, 1f);
        }
    }

    private void ResetPreviewCamera(string key)
    {
        _previewYaw = -0.55f;
        _previewPitch = 0.35f;
        ParticlePreviewPlacement placement = GetPreviewPlacement(key);
        if (!_previewShowReferenceModel)
        {
            _previewDistance = 4.5f;
            _previewTarget = placement.ParticleOrigin;
            return;
        }

        ParticleReferenceModel? model = placement.ReferenceModel;
        if (model == null)
        {
            _previewDistance = 4.5f;
            _previewTarget = placement.ParticleOrigin;
            return;
        }

        _previewTarget = model.Bounds.Center;
        _previewDistance = Math.Clamp(model.Bounds.Radius * 3.1f, 1.8f, 32f);
    }

    private void ResetPreview(string key)
    {
        _previewEffectKey = key;
        _previewParticles.Clear();
        _previewEmitAccumulator = 0;
        ResetPreviewCamera(key);
    }

    private void EmitPreviewParticles(AdvancedParticleProperties particleProperties, float intensity, Vector3 origin)
    {
        int count = Math.Clamp((int)MathF.Round(Math.Max(1f, SampleNatFloat(particleProperties.Quantity)) * Math.Max(0.05f, intensity)), 1, 320);
        for (int index = 0; index < count; index++)
        {
            _previewParticles.Add(new ParticlePreviewParticle(
                origin + new Vector3(
                    SampleNatFloat(GetNatFloat(particleProperties.PosOffset, 0)),
                    SampleNatFloat(GetNatFloat(particleProperties.PosOffset, 1)),
                    SampleNatFloat(GetNatFloat(particleProperties.PosOffset, 2))),
                new Vector3(
                    SampleNatFloat(GetNatFloat(particleProperties.Velocity, 0)) + _previewVelocityX,
                    SampleNatFloat(GetNatFloat(particleProperties.Velocity, 1)) + _previewVelocityY,
                    SampleNatFloat(GetNatFloat(particleProperties.Velocity, 2)) + _previewVelocityZ),
                Math.Max(0.05f, SampleNatFloat(particleProperties.LifeLength)),
                Math.Max(0.01f, SampleNatFloat(particleProperties.Size)),
                (float)_previewRandom.NextDouble()));
        }

        if (_previewParticles.Count > 2000)
        {
            _previewParticles.RemoveRange(0, _previewParticles.Count - 2000);
        }
    }

    private void UpdatePreviewParticles(AdvancedParticleProperties particleProperties, float deltaSeconds)
    {
        if (deltaSeconds <= 0) return;

        float gravity = SampleNatFloat(particleProperties.GravityEffect);
        Vector3 wind = GetPreviewWindVector(particleProperties);
        if (_previewWindEnabled)
        {
            _previewWindPhase += deltaSeconds;
        }

        for (int index = _previewParticles.Count - 1; index >= 0; index--)
        {
            ParticlePreviewParticle particle = _previewParticles[index];
            particle.Age += deltaSeconds;
            if (particle.Age >= particle.Life)
            {
                _previewParticles.RemoveAt(index);
                continue;
            }

            particle.Velocity.Y -= gravity * deltaSeconds;
            if (wind.LengthSquared > 0.0001f)
            {
                float turbulence = 1f;
                if (_previewWindTurbulence > 0f)
                {
                    turbulence += MathF.Sin(_previewWindPhase * 2.4f + particle.Seed * 12.9898f) * _previewWindTurbulence;
                    turbulence += MathF.Cos(_previewWindPhase * 1.7f + particle.Seed * 78.233f) * _previewWindTurbulence * 0.35f;
                    turbulence = Math.Max(0f, turbulence);
                }

                particle.Velocity += wind * turbulence * deltaSeconds;
            }

            particle.Position += particle.Velocity * deltaSeconds;
            _previewParticles[index] = particle;
        }
    }

    private Vector3 GetPreviewWindVector(AdvancedParticleProperties particleProperties)
    {
        if (!_previewWindEnabled) return Vector3.Zero;

        Vector3 direction = new(_previewWindX, _previewWindY, _previewWindZ);
        if (direction.LengthSquared <= 0.0001f) return Vector3.Zero;

        float windAffect = Math.Max(0f, particleProperties.WindAffectednes);
        return Vector3.Normalize(direction) * _previewWindStrength * windAffect;
    }

    private float SampleNatFloat(NatFloat? value)
    {
        if (value == null) return 0f;
        return value.avg + value.var * ((float)_previewRandom.NextDouble() * 2f - 1f);
    }

    private static NatFloat? GetNatFloat(NatFloat[]? values, int index)
    {
        return values != null && index >= 0 && index < values.Length ? values[index] : null;
    }

    private static uint PreviewColor(AdvancedParticleProperties particleProperties, float alphaMultiplier)
    {
        float hue = GetNatFloatAvg(particleProperties.HsvaColor, 0) / 255f;
        float saturation = GetNatFloatAvg(particleProperties.HsvaColor, 1) / 255f;
        float value = GetNatFloatAvg(particleProperties.HsvaColor, 2) / 255f;
        float alpha = Math.Clamp(GetNatFloatAvg(particleProperties.HsvaColor, 3) / 255f * alphaMultiplier, 0f, 1f);
        System.Numerics.Vector3 rgb = HsvToRgb(hue, saturation, value);
        return ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(rgb.X, rgb.Y, rgb.Z, alpha));
    }

    private static float GetNatFloatAvg(NatFloat[]? values, int index)
    {
        return values != null && index >= 0 && index < values.Length && values[index] != null ? values[index].avg : 255f;
    }

    private static System.Numerics.Vector3 HsvToRgb(float h, float s, float v)
    {
        h = ((h % 1f) + 1f) % 1f;
        float c = v * s;
        float x = c * (1f - Math.Abs(h * 6f % 2f - 1f));
        float m = v - c;
        return h switch
        {
            < 1f / 6f => new(c + m, x + m, m),
            < 2f / 6f => new(x + m, c + m, m),
            < 3f / 6f => new(m, c + m, x + m),
            < 4f / 6f => new(m, x + m, c + m),
            < 5f / 6f => new(x + m, m, c + m),
            _ => new(c + m, m, x + m)
        };
    }

    private readonly struct ParticlePreviewCamera(
        Vector3 position,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        System.Numerics.Vector2 center,
        float focalLength)
    {
        public readonly Vector3 Position = position;
        public readonly Vector3 Forward = forward;
        public readonly Vector3 Right = right;
        public readonly Vector3 Up = up;
        public readonly System.Numerics.Vector2 Center = center;
        public readonly float FocalLength = focalLength;
    }

    private readonly struct ParticlePreviewDrawParticle(
        ParticlePreviewParticle particle,
        System.Numerics.Vector2 screenPosition,
        float depth)
    {
        public readonly ParticlePreviewParticle Particle = particle;
        public readonly System.Numerics.Vector2 ScreenPosition = screenPosition;
        public readonly float Depth = depth;
    }

    private sealed class ParticlePreviewPlacement(ParticleReferenceModel? referenceModel, Vector3 particleOrigin) : IDisposable
    {
        public static ParticlePreviewPlacement Unanchored { get; } = new(null, Vector3.Zero);

        public ParticleReferenceModel? ReferenceModel { get; } = referenceModel;
        public Vector3 ParticleOrigin { get; } = particleOrigin;

        public void Dispose()
        {
            ReferenceModel?.Dispose();
        }
    }

    private sealed class ParticleReferenceModel(string label, DevToolsPreviewMesh mesh, List<ParticleReferenceTriangle> triangles, Vector3 min, Vector3 max, Vector3 particleOrigin) : IDisposable
    {
        public string Label { get; } = label;
        public DevToolsPreviewMesh Mesh { get; } = mesh;
        public List<ParticleReferenceTriangle> Triangles { get; } = triangles;
        public Vector3 Min { get; } = min;
        public Vector3 Max { get; } = max;
        public Vector3 ParticleOrigin { get; } = particleOrigin;
        public DevToolsPreviewBounds Bounds { get; } = new DevToolsPreviewBounds(min, max).Include(particleOrigin);

        public void Dispose()
        {
            Mesh.Dispose();
        }
    }

    private readonly struct ParticleReferenceTriangle(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 normal,
        System.Numerics.Vector4 color)
    {
        public readonly Vector3 A = a;
        public readonly Vector3 B = b;
        public readonly Vector3 C = c;
        public readonly Vector3 Normal = normal;
        public readonly System.Numerics.Vector4 Color = color;
    }

    private readonly struct ParticleReferenceProjectedTriangle(
        System.Numerics.Vector2 a,
        System.Numerics.Vector2 b,
        System.Numerics.Vector2 c,
        float depth,
        Vector3 normal,
        System.Numerics.Vector4 color)
    {
        public readonly System.Numerics.Vector2 A = a;
        public readonly System.Numerics.Vector2 B = b;
        public readonly System.Numerics.Vector2 C = c;
        public readonly float Depth = depth;
        public readonly Vector3 Normal = normal;
        public readonly System.Numerics.Vector4 Color = color;
    }

    private struct ParticlePreviewParticle(Vector3 position, Vector3 velocity, float life, float size, float seed)
    {
        public Vector3 Position = position;
        public Vector3 Velocity = velocity;
        public float Age;
        public float Life = life;
        public float Size = size;
        public float Seed = seed;
    }
#endif

    private void SpawnDebugParticles(string code, float offset, Vector3 velocity, float intensity)
    {
        EntityPlayer? playerEntity = (_api as ICoreClientAPI)?.World.Player.Entity;
        if (playerEntity == null) return;

        Vec3d pos = playerEntity.Pos.AheadCopy(offset).XYZ + playerEntity.LocalEyePos;
        Vector3d position = pos.ToOpenTK();
        Spawn(code, position, velocity, intensity);
    }
    private static ParticleEffectsPacket PreparePacket(string code, Entity player, Vector3 position, Vector3 velocity, float intensity)
    {
        Vector3 worldPosition = FromCameraReferenceFrame(player, position);
        Vector3 worldVelocity = FromCameraReferenceFrame(player, velocity);

        Vec3f vanillaPlayerPosition = player.SidedPos.AheadCopy(0).XYZFloat.Add(0, (float)player.LocalEyePos.Y, 0);

        Vector3 effectPosition = new Vector3(vanillaPlayerPosition.X, vanillaPlayerPosition.Y, vanillaPlayerPosition.Z) + worldPosition;

        return new ParticleEffectsPacket()
        {
            Code = code,
            Position = [effectPosition.X, effectPosition.Y, effectPosition.Z],
            Velocity = [worldVelocity.X, worldVelocity.Y, worldVelocity.Z],
            Intensity = intensity
        };
    }
    private static ParticleEffectsPacket PreparePacket(string code, Vector3 position, Vector3 velocity, float intensity)
    {
        return new ParticleEffectsPacket()
        {
            Code = code,
            Position = [position.X, position.Y, position.Z],
            Velocity = [velocity.X, velocity.Y, velocity.Z],
            Intensity = intensity
        };
    }
    private static ParticleEffectsPacket PreparePacket(string code, Vector3d position, Vector3 velocity, float intensity)
    {
        return new ParticleEffectsPacket()
        {
            Code = code,
            Position = [position.X, position.Y, position.Z],
            Velocity = [velocity.X, velocity.Y, velocity.Z],
            Intensity = intensity
        };
    }
    private void SpawnParticleEffect(ParticleEffectsPacket packet, IPlayer? player)
    {
        AdvancedParticleProperties effect;
        try
        {
            effect = Get(packet.Code);
        }
        catch (Exception exception)
        {
            LoggerUtil.Error(_api, this, $"Error on spawning particles: {exception}");
            return;
        }

        effect.basePos = new(packet.Position[0], packet.Position[1], packet.Position[2]);

        NatFloat velocityX = effect.Velocity[0].Clone();
        NatFloat velocityY = effect.Velocity[1].Clone();
        NatFloat velocityZ = effect.Velocity[2].Clone();
        effect.Velocity[0].avg += packet.Velocity[0];
        effect.Velocity[1].avg += packet.Velocity[1];
        effect.Velocity[2].avg += packet.Velocity[2];

        NatFloat quantity = effect.Quantity.Clone();
        effect.Quantity.avg *= packet.Intensity;
        effect.Quantity.var *= packet.Intensity;

        _api.World.SpawnParticles(effect, player);

        effect.Quantity = quantity;
        effect.Velocity[0] = velocityX;
        effect.Velocity[1] = velocityY;
        effect.Velocity[2] = velocityZ;
    }
    private static Vector3 FromCameraReferenceFrame(Entity player, Vector3 position)
    {
        Vec3f vanillaViewVector = player.SidedPos.GetViewVector().Normalize();
        Vector3 viewVector = new(vanillaViewVector.X, vanillaViewVector.Y, vanillaViewVector.Z);
        Vector3 vertical = new(0, 1, 0);
        Vector3 localZ = viewVector;
        Vector3 localX = Vector3.Normalize(Vector3.Cross(localZ, vertical));
        Vector3 localY = Vector3.Cross(localX, localZ);
        return localX * position.X + localY * position.Y + localZ * position.Z;
    }
}

#if DEBUG
public static class ParticleEditor
{
    public static void Draw(string id, AdvancedParticleProperties particleProperties)
    {
        JsonOutput(id, particleProperties);
        ParticleModelEditor(id, particleProperties);
        ColorEditor(id, particleProperties);
        ColorEvolveEditor(id, particleProperties);
        BehaviorEditor(id, particleProperties);
        SizeEditor(id, particleProperties);
        VelocityEditor(id, particleProperties);
        BooleansEditor(id, particleProperties);
        FlagsEditor(id, particleProperties);

        ImGui.BeginDisabled();
        ImGui.CollapsingHeader($"Secondary particles:##{id}");
        ImGui.CollapsingHeader($"Death particles:##{id}");
        ImGui.EndDisabled();
    }


    // MAIN EDITORS
    private static void JsonOutput(string id, AdvancedParticleProperties particleProperties)
    {
        if (!ImGui.CollapsingHeader($"JSON:##{id}")) return;
        ImGui.Indent();

        JsonSerializerSettings settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        JsonSerializer serializer = JsonSerializer.Create(settings);
        JToken json = JToken.FromObject(particleProperties, serializer);

        // VS 1.22 does not like this generated field in exported particle JSON.
        // Keep the in-editor value usable, but never print/copy it into the JSON output.
        RemovePropertyRecursive(json, "VelocityEvolve");
        RemovePropertyRecursive(json, "velocityEvolve");

        string output = json.ToString(Formatting.Indented);

        ImGui.InputTextMultiline($"##{id}", ref output, (uint)output.Length, new(500, 300), ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AutoSelectAll);
        if (ImGui.Button($"Copy to clipboard##{id}"))
        {
            ImGui.SetClipboardText(output);
        }
        ImGui.Unindent();
    }

    private static void RemovePropertyRecursive(JToken token, string propertyName)
    {
        if (token is JObject obj)
        {
            obj.Property(propertyName, StringComparison.OrdinalIgnoreCase)?.Remove();

            foreach (JProperty property in obj.Properties().ToList())
            {
                RemovePropertyRecursive(property.Value, propertyName);
            }
        }
        else if (token is JArray array)
        {
            foreach (JToken child in array)
            {
                RemovePropertyRecursive(child, propertyName);
            }
        }
    }
    private static void ColorEditor(string id, AdvancedParticleProperties particleProperties)
    {
        if (!ImGui.CollapsingHeader($"Color:##{id}")) return;
        ImGui.Indent();

        HsvaEditor(id, particleProperties);
        HsvaVarianceEditor(id, particleProperties);

        bool ColorByBlock = particleProperties.ColorByBlock;
        ImGui.Checkbox($"Color by block##{id}", ref ColorByBlock);
        particleProperties.ColorByBlock = ColorByBlock;

        ImGui.Unindent();
    }
    private static void ColorEvolveEditor(string id, AdvancedParticleProperties particleProperties)
    {
        if (!ImGui.CollapsingHeader($"Color evolve:##{id}")) return;
        ImGui.Indent();

        EvolvingNatFloat? opacity = particleProperties.OpacityEvolve;
        EvolvingNatFloatEditorNullable(id, "Opacity", ref opacity);
        particleProperties.OpacityEvolve = opacity.GetValueOrDefault();

        EvolvingNatFloat? red = particleProperties.RedEvolve;
        EvolvingNatFloatEditorNullable(id, "Red", ref red);
        particleProperties.RedEvolve = red.GetValueOrDefault();

        EvolvingNatFloat? green = particleProperties.GreenEvolve;
        EvolvingNatFloatEditorNullable(id, "Green", ref green);
        particleProperties.GreenEvolve = green.GetValueOrDefault();

        EvolvingNatFloat? blue = particleProperties.BlueEvolve;
        EvolvingNatFloatEditorNullable(id, "Blue", ref blue);
        particleProperties.BlueEvolve = blue.GetValueOrDefault();

        ImGui.Unindent();
    }
    private static void NatFloatEditor(string id, string name, ref NatFloat value, int nameSize = 150)
    {
        ImGui.PushItemWidth(80);
        ImGui.Text($"{name}: "); ImGui.SameLine(nameSize);
        ImGui.Text("Avg ="); ImGui.SameLine(nameSize + 50);
        ImGui.InputFloat($"##avg{name}{id}", ref value.avg); ImGui.SameLine(nameSize + 150);
        ImGui.Text("Var ="); ImGui.SameLine(nameSize + 200);
        ImGui.InputFloat($"##var{name}{id}", ref value.var);
        ImGui.PopItemWidth();
    }
    private static void BehaviorEditor(string id, AdvancedParticleProperties particleProperties)
    {
        if (!ImGui.CollapsingHeader($"Behavior:##{id}")) return;

        ImGui.Indent();

        NatFloat gravity = particleProperties.GravityEffect;
        NatFloatEditor(id, "Gravity", ref gravity);
        particleProperties.GravityEffect = gravity;

        NatFloat lifeLength = particleProperties.LifeLength;
        NatFloatEditor(id, "Life length", ref lifeLength);
        particleProperties.LifeLength = lifeLength;

        NatFloat quantity = particleProperties.Quantity;
        NatFloatEditor(id, "Quantity", ref quantity);
        particleProperties.Quantity = quantity;

        NatFloat SecondarySpawnInterval = particleProperties.SecondarySpawnInterval;
        NatFloatEditor(id, "Secondary spawn interval", ref SecondarySpawnInterval, 250);
        particleProperties.SecondarySpawnInterval = SecondarySpawnInterval;

        float WindAffectednes = particleProperties.WindAffectednes;
        ImGui.InputFloat($"Wind affectednes##{id}", ref WindAffectednes);
        particleProperties.WindAffectednes = WindAffectednes;

        float Bounciness = particleProperties.Bounciness;
        ImGui.InputFloat($"Bounciness##{id}", ref Bounciness);
        particleProperties.Bounciness = Bounciness;

        NatFloat[] PosOffset = particleProperties.PosOffset;
        NatFloatVecEditor(id, "Position offset", ref PosOffset);
        particleProperties.PosOffset = PosOffset;

        ImGui.Unindent();
    }
    private static void SizeEditor(string id, AdvancedParticleProperties particleProperties)
    {
        if (!ImGui.CollapsingHeader($"Size:##{id}")) return;

        ImGui.Indent();

        NatFloat size = particleProperties.Size;
        NatFloatEditor(id, "Size", ref size);
        particleProperties.Size = size;

        EvolvingNatFloat? sizeEvolve = particleProperties.SizeEvolve;
        EvolvingNatFloatEditorNullable(id, "Size evolve", ref sizeEvolve);
        particleProperties.SizeEvolve = sizeEvolve.GetValueOrDefault();

        ImGui.Unindent();
    }
    private static void VelocityEditor(string id, AdvancedParticleProperties particleProperties)
    {
        if (!ImGui.CollapsingHeader($"Velocity:##{id}")) return;

        ImGui.Indent();

        NatFloat velocityX = particleProperties.Velocity[0];
        NatFloatEditor(id, "Velocity.X", ref velocityX);
        particleProperties.Velocity[0] = velocityX;

        NatFloat velocityY = particleProperties.Velocity[1];
        NatFloatEditor(id, "Velocity.Y", ref velocityY);
        particleProperties.Velocity[1] = velocityY;

        NatFloat velocityZ = particleProperties.Velocity[2];
        NatFloatEditor(id, "Velocity.Z", ref velocityZ);
        particleProperties.Velocity[2] = velocityZ;

        // Disabled intentionally: VS 1.22 can crash clients when VelocityEvolve is present
        // on network-spawned AdvancedParticleProperties.
        particleProperties.VelocityEvolve = null;

        ImGui.Unindent();
    }
    private static void BooleansEditor(string id, AdvancedParticleProperties particleProperties)
    {
        if (!ImGui.CollapsingHeader($"Bool properties:##{id}")) return;

        ImGui.Indent();

        bool DieOnRainHeightmap = particleProperties.DieOnRainHeightmap;
        ImGui.Checkbox($"Die on rain height map##{id}", ref DieOnRainHeightmap);
        particleProperties.DieOnRainHeightmap = DieOnRainHeightmap;

        bool RandomVelocityChange = particleProperties.RandomVelocityChange;
        ImGui.Checkbox($"Random velocity change##{id}", ref RandomVelocityChange);
        particleProperties.RandomVelocityChange = RandomVelocityChange;

        bool DieInAir = particleProperties.DieInAir;
        ImGui.Checkbox($"Die in air##{id}", ref DieInAir);
        particleProperties.DieInAir = DieInAir;

        bool DieInLiquid = particleProperties.DieInLiquid;
        ImGui.Checkbox($"Die in liquid##{id}", ref DieInLiquid);
        particleProperties.DieInLiquid = DieInLiquid;

        bool SwimOnLiquid = particleProperties.SwimOnLiquid;
        ImGui.Checkbox($"Swim on liquid##{id}", ref SwimOnLiquid);
        particleProperties.SwimOnLiquid = SwimOnLiquid;

        bool SelfPropelled = particleProperties.SelfPropelled;
        ImGui.Checkbox($"Self-propelled##{id}", ref SelfPropelled);
        particleProperties.SelfPropelled = SelfPropelled;

        bool TerrainCollision = particleProperties.TerrainCollision;
        ImGui.Checkbox($"Terrain collision##{id}", ref TerrainCollision);
        particleProperties.TerrainCollision = TerrainCollision;

        ImGui.Unindent();
    }
    private static void FlagsEditor(string id, AdvancedParticleProperties particleProperties)
    {
        if (!ImGui.CollapsingHeader($"Vertex flags:##{id}")) return;

        ImGui.Indent();

        VertexFlags flags = new(particleProperties.VertexFlags);

        int glowLevel = flags.GlowLevel;
        ImGui.SliderInt($"Glow level##{id}", ref glowLevel, 0, 255);
        flags.GlowLevel = (byte)glowLevel;

        bool Reflective = flags.Reflective;
        ImGui.Checkbox("Reflective", ref Reflective);
        flags.Reflective = Reflective;

        int ZOffset = flags.ZOffset;
        ImGui.SliderInt($"Z offset##{id}", ref ZOffset, 0, 255);
        flags.ZOffset = (byte)ZOffset;

        bool Lod0 = flags.Lod0;
        ImGui.Checkbox("Lod0", ref Lod0);
        flags.Lod0 = Lod0;

        EnumWindBitMode WindMode = flags.WindMode;
        WindBitModeEditor(id, "Wind mode", ref WindMode);
        flags.WindMode = WindMode;

        int WindData = flags.WindData;
        ImGui.SliderInt($"Wind data##{id}", ref WindData, 0, 255);
        flags.WindData = (byte)WindData;

        int Normal = flags.Normal;
        ImGui.InputInt($"Normal##{id}", ref Normal);
        flags.Normal = (short)GameMath.Clamp(Normal, short.MinValue, short.MaxValue);

        particleProperties.VertexFlags = flags.All;

        ImGui.Unindent();
    }

    // SUPPLEMENTARY EDITORS
    private static void HsvaEditor(string id, AdvancedParticleProperties particleProperties)
    {
        float hue = particleProperties.HsvaColor[0].avg;
        float saturation = particleProperties.HsvaColor[1].avg;
        float value = particleProperties.HsvaColor[2].avg;
        float alpha = particleProperties.HsvaColor[3].avg;

        System.Numerics.Vector4 color = new(hue / 255, saturation / 255, value / 255, alpha / 255);
        ImGui.ColorPicker4($"Color##{id}", ref color, ImGuiColorEditFlags.InputHSV | ImGuiColorEditFlags.DisplayHSV);

        particleProperties.HsvaColor[0].avg = color.X * 255f;
        particleProperties.HsvaColor[1].avg = color.Y * 255f;
        particleProperties.HsvaColor[2].avg = color.Z * 255f;
        particleProperties.HsvaColor[3].avg = color.W * 255f;
    }
    private static void HsvaVarianceEditor(string id, AdvancedParticleProperties particleProperties)
    {
        float hue = particleProperties.HsvaColor[0].var;
        float saturation = particleProperties.HsvaColor[1].var;
        float value = particleProperties.HsvaColor[2].var;
        float alpha = particleProperties.HsvaColor[3].var;

        System.Numerics.Vector4 color = new(hue, saturation, value, alpha);

        ImGui.InputFloat4($"Variance HSVA##{id}", ref color, "%.0f");

        particleProperties.HsvaColor[0].var = color.X;
        particleProperties.HsvaColor[1].var = color.Y;
        particleProperties.HsvaColor[2].var = color.Z;
        particleProperties.HsvaColor[3].var = color.W;
    }

    private static readonly string[] _particleModels = new[] { "Quad", "Cube" };
    private static void ParticleModelEditor(string id, AdvancedParticleProperties particleProperties)
    {
        int currentModel = (int)particleProperties.ParticleModel;

        ImGui.Combo($"Model##{id}", ref currentModel, _particleModels, 2, 2);

        particleProperties.ParticleModel = (EnumParticleModel)currentModel;
    }

    private static readonly string[] _transformFunction = new[]
    {
        "IDENTICAL",
        "LINEAR",
        "LINEARNULLIFY",
        "LINEARREDUCE",
        "LINEARINCREASE",
        "QUADRATIC",
        "INVERSELINEAR",
        "ROOT",
        "SINUS",
        "CLAMPEDPOSITIVESINUS",
        "COSINUS",
        "SMOOTHSTEP"
    };
    private static void EvolvingNatFloatEditorNullable(string id, string label, ref EvolvingNatFloat? value)
    {
        bool enabled = value != null;

        ImGui.Checkbox($"{label}##{id}", ref enabled);

        if (!enabled)
        {
            value = null;
            return;
        }

        value ??= new(EnumTransformFunction.LINEAR, 0);

        int currentModel = (int)value.Value.Transform;
        float currentFactor = value.Value.Factor;
        ImGui.Combo($"##combo{label}{id}", ref currentModel, _transformFunction, 12, 12);
        ImGui.DragFloat($"##drag{label}{id}", ref currentFactor);

        EnumTransformFunction newTransform = (EnumTransformFunction)currentModel;

        value = new(newTransform, currentFactor);
    }
    private static void EvolvingNatFloatEditor(string id, string label, ref EvolvingNatFloat value)
    {
        ImGui.Text($"{label}: ");

        int currentModel = (int)value.Transform;
        float currentFactor = value.Factor;
        ImGui.Combo($"##avg{label}{id}", ref currentModel, _transformFunction, 12, 12);
        ImGui.DragFloat($"##var{label}{id}", ref currentFactor);

        EnumTransformFunction newTransform = (EnumTransformFunction)currentModel;

        value = new(newTransform, currentFactor);
    }

    private static readonly string[] _windBitMode = new[]
    {
        "NoWind",
        "WeakWind",
        "NormalWind",
        "Leaves",
        "Bend",
        "TallBend",
        "Water",
        "ExtraWeakWind",
        "Fruit",
        "WeakWindNoBend",
        "WeakWindInverseBend",
        "WaterPlant"
    };
    private static void WindBitModeEditor(string id, string name, ref EnumWindBitMode value)
    {
        int intValue = (int)value;
        ImGui.Combo($"{name}##{id}", ref intValue, _windBitMode, _windBitMode.Length);
        value = (EnumWindBitMode)intValue;
    }

    private static void NatFloatVecEditor(string id, string name, ref NatFloat[] vector)
    {
        System.Numerics.Vector3 average = new(vector[0].avg, vector[1].avg, vector[2].avg);
        System.Numerics.Vector3 variance = new(vector[0].var, vector[1].var, vector[2].var);
        ImGui.Text($"{name}");
        ImGui.Text("average:  "); ImGui.SameLine();
        ImGui.InputFloat3($"##average{name}{id}", ref average, "%.2f");
        ImGui.Text("variance: "); ImGui.SameLine();
        ImGui.InputFloat3($"##variance{name}{id}", ref variance);
        vector[0].avg = average.X;
        vector[1].avg = average.Y;
        vector[2].avg = average.Z;
        vector[0].var = variance.X;
        vector[1].var = variance.Y;
        vector[2].var = variance.Z;
    }
}
#endif
