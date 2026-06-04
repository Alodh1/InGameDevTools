using ImGuiNET;
using InGameDevTools.Animations;
using InGameDevTools.Integration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
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
        ClearPreviewReferenceModelCache();
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
                string text = asset.ToText();
                if (!DevToolsJson.TryParseObject(text, out JObject? token, out string error) || token == null)
                {
                    _scanDiagnostics.ParseFailures++;
                    _diagnostics.Warning($"Named particle asset parse failed for {asset.Location}: {error}", text);
                    continue;
                }

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
                _diagnostics.Exception($"Named particle asset parse failed for {asset.Location}", exception);
                LoggerUtil.Error(api, this, $"Error on parsing particle effects for '{asset.Location.Domain}':\n{exception}");
            }
        }
    }

    private void LoadEmbeddedParticleAssets(ICoreAPI api)
    {
        ClearPreviewReferenceModelCache();

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
                _diagnostics.Warning($"Particle asset load failed: {asset.Location}", exception.ToString());
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
                _diagnostics.Warning($"Particle asset text read failed: {asset.Location}", exception.ToString());
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
                if (!DevToolsJson.TryParseToken(text, out JToken? root, out string error) || root == null)
                {
                    _scanDiagnostics.ParseFailures++;
                    _diagnostics.Warning($"Particle asset parse failed: {asset.Location}: {error}", text);
                    AddVerboseSample(_scanDiagnostics.SkippedSamples, $"parse failed: {asset.Location} ({error})");
                    continue;
                }

                List<(string Path, JToken Token)> particleTokens = [];
                CollectParticlePropertyTokens(root, "", particleTokens);
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
                _diagnostics.Exception($"Embedded particle asset parse failed for {asset.Location}", exception);
                LoggerUtil.Warn(api, this, $"Error on parsing embedded particle assets for '{asset.Location}':\n{exception}");
            }
        }
    }

    private void LoadRuntimeParticleAssets(ICoreAPI api)
    {
        ClearPreviewReferenceModelCache();

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
                _diagnostics.Exception($"Runtime particle clone failed for {collectible.Code}", exception);
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

    private static void NormalizeParticleProperties(AdvancedParticleProperties particleProperties, bool stripVelocityEvolve = false)
    {
        if (stripVelocityEvolve)
        {
            // VS 1.22 can crash clients when VelocityEvolve is present on network-spawned particles.
            // Keep it in editor/preview data, but remove it from the runtime spawn copy.
            particleProperties.VelocityEvolve = null;
        }

        particleProperties.Velocity = EnsureNatFloatArray(particleProperties.Velocity, 3, 0f, 0.5f);
        particleProperties.PosOffset = EnsureNatFloatArray(particleProperties.PosOffset, 3, 0f, 0f);
        particleProperties.HsvaColor = EnsureHsvaNatFloatArray(particleProperties.HsvaColor);
        particleProperties.Quantity ??= NewNatFloat(1f, 0f);
        particleProperties.LifeLength ??= NewNatFloat(1f, 0f);
        particleProperties.Size ??= NewNatFloat(1f, 0f);
        particleProperties.GravityEffect ??= NewNatFloat(1f, 0f);
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

    private static NatFloat[] EnsureHsvaNatFloatArray(NatFloat[]? values)
    {
        NatFloat[] result = new NatFloat[4];
        float[] defaultAvg = [128f, 128f, 128f, 255f];
        float[] defaultVar = [128f, 128f, 128f, 0f];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = values != null && index < values.Length && values[index] != null
                ? values[index]
                : NewNatFloat(defaultAvg[index], defaultVar[index]);
        }
        return result;
    }

    private static NatFloat NewNatFloat(float avg, float var) => new(avg, var, EnumDistribution.UNIFORM);

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

    public void Draw(string id)
    {
        DrawEditor(id, 0, 1f);
    }

    public void DrawEditor(string id, float deltaSeconds)
    {
        DrawEditor(id, deltaSeconds, 1f);
    }

    public void DrawEditor(string id, float deltaSeconds, float uiScale, DebugWindowManager.DevToolsLiveApplyManager? liveApplyManager = null, bool showDiagnostics = false)
    {
        List<ParticleEffectFamily> families = BuildParticleFamilies();
        ParticleEffectFamily[] visibleFamilies = families
            .Where(family => ImGuiLayoutHelper.MatchesDomain(_particleDomainFilter, family.Domain))
            .Where(MatchesParticleFilter)
            .ToArray();

        if (visibleFamilies.Length > 0 && (string.IsNullOrWhiteSpace(_selectedParticleFamilyKey) || !visibleFamilies.Any(family => family.Key.Equals(_selectedParticleFamilyKey, StringComparison.OrdinalIgnoreCase))))
        {
            SelectParticleFamily(visibleFamilies[0]);
        }

        ParticleEffectFamily? selectedFamily = visibleFamilies.FirstOrDefault(family => family.Key.Equals(_selectedParticleFamilyKey, StringComparison.OrdinalIgnoreCase));
        ParticleEffectVariant? selectedVariant = selectedFamily?.Variants.FirstOrDefault(variant => variant.Key.Equals(_selectedParticleVariantKey, StringComparison.OrdinalIgnoreCase)) ?? selectedFamily?.Variants.FirstOrDefault();
        if (selectedFamily != null && selectedVariant != null && !_selectedParticleVariantKey.Equals(selectedVariant.Key, StringComparison.OrdinalIgnoreCase))
        {
            _selectedParticleVariantKey = selectedVariant.Key;
            ResetPreview(selectedVariant.ReferenceKey);
        }

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
        ImGui.SeparatorText("Particle sources");
        DrawParticleScanControls(id);
        ImGuiLayoutHelper.DrawDomainCombo($"Domain##particle-domain-filter-{id}", ref _particleDomainFilter, families.Select(family => family.Domain));
        ImGui.InputTextWithHint($"##particle-filter-{id}", "filter blocks/items", ref _particleFilter, 240);
        ImGui.BeginChild($"##particle-effects-list-{id}", new System.Numerics.Vector2(0, Math.Max(80f, ImGui.GetContentRegionAvail().Y * 0.55f)), true);
        foreach (ParticleEffectFamily family in visibleFamilies)
        {
            bool selected = family.Key.Equals(_selectedParticleFamilyKey, StringComparison.OrdinalIgnoreCase);
            string familyLabel = family.IsModified ? $"{family.DisplayKey} *" : family.DisplayKey;
            if (ImGui.Selectable($"{familyLabel}##particle-family-{family.Key}", selected))
            {
                SelectParticleFamily(family);
                selectedFamily = family;
                selectedVariant = family.Variants.FirstOrDefault();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(family.Tooltip);
            }
        }
        ImGui.EndChild();
        ImGui.TextDisabled($"Showing {visibleFamilies.Length} / {families.Count}");
        if (selectedFamily != null)
        {
            DrawParticleVariantAndEmitterSelectors(id, selectedFamily, ref selectedVariant);
        }
        ImGui.EndChild();

        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter($"##particles-left-splitter-{id}", available.Y, splitterThickness, panelAvailableWidth, ref _layout.LeftFraction, 240f * scale, Math.Max(240f * scale, panelAvailableWidth - rightWidth - 320f * scale));
        ImGui.SameLine(0, 0);

        if (families.Count == 0 || selectedFamily == null || selectedVariant == null)
        {
            ImGui.BeginChild($"##particles-empty-{id}", new System.Numerics.Vector2(centerWidth, available.Y), true);
            ImGui.TextDisabled("No particle effects loaded.");
            ImGui.TextWrapped("Use Reload assets after a world is loaded. If this remains empty, enable Verbose scan log and reload.");
            ImGui.EndChild();
            return;
        }

        IReadOnlyList<ParticleEffectEntry> previewEmitters = selectedVariant.EmittersWithParticles;
        ParticleEffectEntry? selectedEmitter = selectedVariant.GetEmitter(_selectedParticleEmitterIndex);
        ImGui.BeginChild($"##particles-preview-{id}", new System.Numerics.Vector2(centerWidth, available.Y), true);
        DrawPreviewPanel(id, selectedVariant, previewEmitters, selectedEmitter?.Properties ?? previewEmitters.FirstOrDefault()?.Properties, deltaSeconds);
        ImGui.EndChild();

        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter($"##particles-right-splitter-{id}", available.Y, splitterThickness, panelAvailableWidth, ref _layout.RightFraction, 360f * scale, Math.Max(360f * scale, panelAvailableWidth - leftWidth - 320f * scale), invertDrag: true);
        ImGui.SameLine(0, 0);

        ImGui.BeginChild($"##particles-properties-{id}", new System.Numerics.Vector2(rightWidth, available.Y), true, ImGuiWindowFlags.HorizontalScrollbar);
        ImGui.SeparatorText("Values");
        DrawParticleEditScopeControls(id, selectedFamily, selectedVariant);
        DrawParticleResetControls(id, selectedFamily, selectedVariant, _selectedParticleEmitterIndex, liveApplyManager);
        DrawParticleLiveControls(id, selectedFamily, selectedVariant, _selectedParticleEmitterIndex, liveApplyManager);
        if (selectedEmitter == null)
        {
            ImGui.TextWrapped($"The selected variant has no ParticleProperties[{_selectedParticleEmitterIndex}] emitter.");
        }
        else
        {
            AdvancedParticleProperties particleProperties = selectedEmitter.Properties;
            JToken before = ParticlePropertiesToToken(particleProperties);
            ParticleEditor.Draw($"{id}-{selectedFamily.Key}-{_selectedParticleEmitterIndex}", particleProperties);
            JToken after = ParticlePropertiesToToken(particleProperties);
            if (!JToken.DeepEquals(before, after))
            {
                ApplyParticleEditorChange(selectedFamily, selectedVariant, _selectedParticleEmitterIndex, before, after, liveApplyManager);
            }
        }

        if (!string.IsNullOrWhiteSpace(_particleStatus))
        {
            ImGui.SeparatorText("Status");
            ImGui.TextWrapped(_particleStatus);
        }
        _diagnostics.Draw($"{id}-diagnostics", showDiagnostics);
        ImGui.EndChild();
    }

    private bool MatchesParticleFilter(ParticleEffectFamily family)
    {
        if (string.IsNullOrWhiteSpace(_particleFilter))
        {
            return true;
        }

        return family.SearchText.Contains(_particleFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectParticleFamily(ParticleEffectFamily family)
    {
        _selectedParticleFamilyKey = family.Key;
        ParticleEffectVariant? variant = family.Variants.FirstOrDefault();
        _selectedParticleVariantKey = variant?.Key ?? "";
        _selectedParticleEmitterIndex = 0;
        if (variant != null)
        {
            ResetPreview(variant.ReferenceKey);
        }
    }

    private void DrawParticleVariantAndEmitterSelectors(string id, ParticleEffectFamily family, ref ParticleEffectVariant? selectedVariant)
    {
        ImGui.SeparatorText("Selected source");
        ImGui.TextWrapped(family.IsModified ? $"{family.DisplayKey} *" : family.DisplayKey);
        ImGui.TextDisabled($"{family.Variants.Count} variant(s), {family.MaxEmitterCount} emitter slot(s), {family.ModifiedEmitterCount} modified emitter(s)");

        string[] variantLabels = family.Variants
            .Select(variant => variant.IsModified ? $"{variant.DisplayKey} *" : variant.DisplayKey)
            .ToArray();
        int variantIndex = Math.Max(0, family.Variants.FindIndex(variant => variant.Key.Equals(_selectedParticleVariantKey, StringComparison.OrdinalIgnoreCase)));
        ImGui.SetNextItemWidth(-1);
        if (variantLabels.Length > 0 && ImGui.Combo($"Variant##particle-variant-{id}", ref variantIndex, variantLabels, variantLabels.Length))
        {
            selectedVariant = family.Variants[variantIndex];
            _selectedParticleVariantKey = selectedVariant.Key;
            ResetPreview(selectedVariant.ReferenceKey);
        }

        int maxEmitter = Math.Max(0, family.MaxEmitterCount - 1);
        _selectedParticleEmitterIndex = Math.Clamp(_selectedParticleEmitterIndex, 0, maxEmitter);
        ImGui.SeparatorText("Emitters");
        for (int emitterIndex = 0; emitterIndex < family.MaxEmitterCount; emitterIndex++)
        {
            ParticleEffectEntry? emitter = selectedVariant?.GetEmitter(emitterIndex);
            bool hasEmitter = emitter != null;
            string modifiedMarker = emitter?.IsModified == true ? " *" : "";
            string label = hasEmitter ? $"ParticleProperties[{emitterIndex}]{modifiedMarker}" : $"ParticleProperties[{emitterIndex}] missing";
            if (ImGui.Selectable($"{label}##particle-emitter-{id}-{emitterIndex}", emitterIndex == _selectedParticleEmitterIndex))
            {
                _selectedParticleEmitterIndex = emitterIndex;
            }
        }
    }

    private void DrawParticleEditScopeControls(string id, ParticleEffectFamily family, ParticleEffectVariant variant)
    {
        ImGui.TextWrapped($"{variant.SourceKindLabel}: {variant.Source}");
        bool groupEdit = !_particleVariantOnlyEdit;
        if (ImGui.Checkbox($"Group edit##particle-group-edit-{id}", ref groupEdit))
        {
            _particleVariantOnlyEdit = !groupEdit;
        }

        ImGui.SameLine();
        ImGui.TextDisabled(groupEdit
            ? $"Editing all {family.Variants.Count} variants"
            : $"Editing only {variant.DisplayKey}");
    }

    private void DrawParticleResetControls(string id, ParticleEffectFamily family, ParticleEffectVariant variant, int emitterIndex, DebugWindowManager.DevToolsLiveApplyManager? liveApplyManager)
    {
        ImGui.SeparatorText("Original");
        ImGui.TextDisabled($"{family.ModifiedEmitterCount} modified emitter(s) in family; {variant.ModifiedEmitterCount} in selected variant.");

        bool emitterCanReset = GetParticleResetEmitterTargets(family, variant, emitterIndex, out _).Any(entry => entry.IsModified);
        bool variantCanReset = variant.IsModified;
        bool familyCanReset = family.IsModified;

        if (!emitterCanReset) ImGui.BeginDisabled();
        if (ImGui.Button($"Reset emitter##particle-reset-emitter-{id}"))
        {
            ResetParticleEmitterToOriginal(family, variant, emitterIndex, liveApplyManager);
        }
        if (!emitterCanReset) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!variantCanReset) ImGui.BeginDisabled();
        if (ImGui.Button($"Reset variant##particle-reset-variant-{id}"))
        {
            ResetParticleVariantToOriginal(family, variant, liveApplyManager);
        }
        if (!variantCanReset) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!familyCanReset) ImGui.BeginDisabled();
        if (ImGui.Button($"Reset family##particle-reset-family-{id}"))
        {
            ResetParticleFamilyToOriginal(family, variant, liveApplyManager);
        }
        if (!familyCanReset) ImGui.EndDisabled();
    }

    private List<ParticleEffectEntry> GetParticleResetEmitterTargets(ParticleEffectFamily family, ParticleEffectVariant variant, int emitterIndex, out int skipped)
    {
        skipped = 0;
        List<ParticleEffectEntry> targets = [];
        if (_particleVariantOnlyEdit)
        {
            ParticleEffectEntry? entry = variant.GetEmitter(emitterIndex);
            if (entry != null)
            {
                targets.Add(entry);
            }
            else
            {
                skipped++;
            }

            return targets;
        }

        foreach (ParticleEffectVariant targetVariant in family.Variants)
        {
            ParticleEffectEntry? entry = targetVariant.GetEmitter(emitterIndex);
            if (entry != null)
            {
                targets.Add(entry);
            }
            else
            {
                skipped++;
            }
        }

        return targets;
    }

    private void ResetParticleEmitterToOriginal(ParticleEffectFamily family, ParticleEffectVariant variant, int emitterIndex, DebugWindowManager.DevToolsLiveApplyManager? liveApplyManager)
    {
        List<ParticleEffectEntry> targets = GetParticleResetEmitterTargets(family, variant, emitterIndex, out int skipped);
        int reset = ResetParticleEntriesToOriginal(targets);
        if (reset == 0)
        {
            _particleStatus = targets.Count == 0
                ? $"No ParticleProperties[{emitterIndex}] emitter exists in the selected reset scope."
                : $"ParticleProperties[{emitterIndex}] is already at its original value.";
            return;
        }

        ClearParticleLiveHashesForEmitter(family, variant, emitterIndex);
        ResetPreview(variant.ReferenceKey);
        string runtimeStatus = ApplyParticleResetLiveEmitter(family, variant, emitterIndex, liveApplyManager);
        _particleStatus = _particleVariantOnlyEdit
            ? $"Reset ParticleProperties[{emitterIndex}] for {variant.DisplayKey}; {runtimeStatus}"
            : $"Reset ParticleProperties[{emitterIndex}] for {reset} {family.DisplayKey} variant(s){(skipped > 0 ? $"; skipped {skipped} missing variant(s)" : "")}; {runtimeStatus}";
    }

    private void ResetParticleVariantToOriginal(ParticleEffectFamily family, ParticleEffectVariant variant, DebugWindowManager.DevToolsLiveApplyManager? liveApplyManager)
    {
        List<ParticleEffectEntry> targets = variant.EmittersWithParticles.ToList();
        int reset = ResetParticleEntriesToOriginal(targets);
        if (reset == 0)
        {
            _particleStatus = $"{variant.DisplayKey} is already at its original particle values.";
            return;
        }

        ClearParticleLiveHashesForVariant(family, variant);
        ResetPreview(variant.ReferenceKey);
        string runtimeStatus = ApplyParticleResetLiveVariant(variant, liveApplyManager);
        _particleStatus = $"Reset {reset} emitter(s) for {variant.DisplayKey}; {runtimeStatus}";
    }

    private void ResetParticleFamilyToOriginal(ParticleEffectFamily family, ParticleEffectVariant selectedVariant, DebugWindowManager.DevToolsLiveApplyManager? liveApplyManager)
    {
        List<ParticleEffectEntry> targets = family.Variants.SelectMany(variant => variant.EmittersWithParticles).ToList();
        int reset = ResetParticleEntriesToOriginal(targets);
        if (reset == 0)
        {
            _particleStatus = $"{family.DisplayKey} is already at its original particle values.";
            return;
        }

        ClearParticleLiveHashesForFamily(family);
        ResetPreview(selectedVariant.ReferenceKey);
        string runtimeStatus = ApplyParticleResetLiveFamily(family, liveApplyManager);
        _particleStatus = $"Reset {reset} emitter(s) for {family.DisplayKey}; {runtimeStatus}";
    }

    private static int ResetParticleEntriesToOriginal(IEnumerable<ParticleEffectEntry> entries)
    {
        int reset = 0;
        foreach (ParticleEffectEntry entry in entries.Distinct())
        {
            if (!entry.IsModified) continue;

            entry.ResetToOriginal();
            reset++;
        }

        return reset;
    }

    private List<ParticleEffectFamily> BuildParticleFamilies()
    {
        Dictionary<string, ParticleEffectFamilyBuilder> builders = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParticleEffectEntry entry in _particleEffects.Values)
        {
            ParticleEffectMember member = BuildParticleEffectMember(entry);
            if (!builders.TryGetValue(member.FamilyKey, out ParticleEffectFamilyBuilder? builder))
            {
                builders[member.FamilyKey] = builder = new ParticleEffectFamilyBuilder(member.FamilyKey, member.FamilyDisplayKey, member.Domain, member.SearchText, member.FamilyTooltip);
            }

            builder.Add(member);
        }

        return builders.Values
            .Select(builder => builder.Build())
            .OrderBy(family => family.DisplayKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ParticleEffectMember BuildParticleEffectMember(ParticleEffectEntry entry)
    {
        string typePrefix = "";
        string collectibleCode = "";
        CollectibleObject? collectible = null;
        bool runtime = false;
        if (TryGetRuntimeCollectibleCode(entry, "block", out string blockCode))
        {
            typePrefix = "block";
            collectibleCode = blockCode;
            collectible = ResolveBlock(blockCode, null);
            runtime = true;
        }
        else if (TryGetRuntimeCollectibleCode(entry, "item", out string itemCode))
        {
            typePrefix = "item";
            collectibleCode = itemCode;
            collectible = ResolveItem(itemCode, null);
            runtime = true;
        }

        if (!runtime || collectible?.Code == null)
        {
            return new ParticleEffectMember(
                $"effect:{entry.Key}",
                entry.DisplayKey,
                entry.Domain,
                entry.Key,
                entry.DisplayKey,
                entry.SourceKindLabel,
                entry.Source,
                entry.SearchText,
                entry.Key,
                entry.Key,
                GetParticleIndex(entry.Key),
                entry,
                null,
                false,
                $"{entry.Key}\n{entry.Source}");
        }

        string familyPath = GetParticleFamilyPath(collectible);
        string familyKey = $"{typePrefix}:{collectible.Code.Domain}:{familyPath}";
        string variantKey = $"{typePrefix}:{collectible.Code}";
        string familyDisplay = $"{(typePrefix == "block" ? "Block" : "Item")} | {ImGuiLayoutHelper.CompactAssetCode($"{collectible.Code.Domain}:{familyPath}")}";
        string variantDisplay = ImGuiLayoutHelper.CompactAssetCode(collectible.Code.ToString());
        string search = $"{familyKey} {familyDisplay} {variantKey} {variantDisplay} {entry.Key} {entry.Source}";
        return new ParticleEffectMember(
            familyKey,
            familyDisplay,
            collectible.Code.Domain,
            variantKey,
            variantDisplay,
            entry.SourceKindLabel,
            entry.Source,
            search,
            entry.Key,
            variantKey,
            GetParticleIndex(entry.Key),
            entry,
            collectible,
            true,
            $"{familyDisplay}\n{collectible.Code}\n{entry.Source}");
    }

    private static string GetParticleFamilyPath(CollectibleObject collectible)
    {
        if (collectible.Attributes?.Token is JObject attributes &&
            attributes["handbook"] is JObject handbook &&
            handbook["groupBy"] is JArray groupBy)
        {
            string? pattern = groupBy.Values<string>().FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                return NormalizeParticleFamilyPattern(pattern);
            }
        }

        string path = collectible.Code?.Path ?? "unknown";
        int dash = path.IndexOf('-');
        return dash > 0 ? path[..dash] : path;
    }

    private static string NormalizeParticleFamilyPattern(string pattern)
    {
        string value = pattern.Trim();
        int colon = value.IndexOf(':');
        if (colon >= 0) value = value[(colon + 1)..];
        int star = value.IndexOf('*');
        if (star >= 0) value = value[..star];
        return value.TrimEnd('-', '/', '_');
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

    private void DrawParticleLiveControls(string id, ParticleEffectFamily family, ParticleEffectVariant variant, int emitterIndex, DebugWindowManager.DevToolsLiveApplyManager? liveApplyManager)
    {
        if (liveApplyManager == null) return;

        List<ParticleEffectEntry> targets = GetParticleLiveTargets(family, variant, emitterIndex).ToList();
        bool available = targets.Count > 0 && targets.All(entry => GetParticleLiveTargetKey(entry, out CollectibleObject? collectible, out int index).Length > 0 && collectible != null && index >= 0);
        string scopeKey = GetParticleLiveScopeKey(family, variant, emitterIndex);
        if (available && liveApplyManager.AutoApply)
        {
            ApplySelectedParticleLive(family, variant, emitterIndex, liveApplyManager);
        }

        ImGui.SeparatorText("Runtime");
        ImGui.TextDisabled(available ? (liveApplyManager.AutoApply ? "Runtime apply enabled" : "Runtime apply disabled") : "Live target unavailable");
        if (available && liveApplyManager.CanRevert(scopeKey))
        {
            if (ImGui.Button($"Revert selected##particle-live-revert-{id}-{scopeKey}"))
            {
                liveApplyManager.LastStatus = liveApplyManager.Revert(scopeKey);
                _particleLiveAppliedHashes.Remove(scopeKey);
                ClearParticleRuntimeOverrides(scopeKey);
            }
        }

        if (!available)
        {
            ImGui.TextWrapped($"{family.DisplayKey}: no live runtime target.");
        }
        else if (!string.IsNullOrWhiteSpace(liveApplyManager.LastStatus))
        {
            ImGui.TextWrapped(liveApplyManager.LastStatus);
        }
    }

    public void ApplyDirtyParticleLive(DebugWindowManager.DevToolsLiveApplyManager liveApplyManager, bool force = false)
    {
        ParticleEffectFamily? family = BuildParticleFamilies().FirstOrDefault(candidate => candidate.Key.Equals(_selectedParticleFamilyKey, StringComparison.OrdinalIgnoreCase));
        ParticleEffectVariant? variant = family?.Variants.FirstOrDefault(candidate => candidate.Key.Equals(_selectedParticleVariantKey, StringComparison.OrdinalIgnoreCase));
        if (family == null || variant == null)
        {
            liveApplyManager.LastStatus = "No selected particle family to apply.";
            return;
        }

        ApplySelectedParticleLive(family, variant, _selectedParticleEmitterIndex, liveApplyManager, force);
    }

    public void ClearParticleLiveApplyState()
    {
        _particleLiveAppliedHashes.Clear();
        _particleLiveOverrideKeysByScope.Clear();
        ParticleRuntimePatches.ClearOverrides();
    }

    private string ApplySelectedParticleLive(ParticleEffectFamily family, ParticleEffectVariant variant, int emitterIndex, DebugWindowManager.DevToolsLiveApplyManager liveApplyManager, bool force = false)
    {
        List<ParticleEffectEntry> targets = GetParticleLiveTargets(family, variant, emitterIndex).ToList();
        string scopeKey = GetParticleLiveScopeKey(family, variant, emitterIndex);
        return ApplyParticleLiveEntries(
            scopeKey,
            family.DisplayKey,
            emitterIndex,
            targets,
            liveApplyManager,
            force,
            $"No runtime ParticleProperties[{emitterIndex}] target for {family.DisplayKey}.");
    }

    private string ApplyParticleLiveEntries(
        string scopeKey,
        string displayName,
        int emitterIndex,
        IReadOnlyCollection<ParticleEffectEntry> targets,
        DebugWindowManager.DevToolsLiveApplyManager liveApplyManager,
        bool force,
        string noTargetsMessage)
    {
        if (targets.Count == 0)
        {
            liveApplyManager.LastStatus = noTargetsMessage;
            return liveApplyManager.LastStatus;
        }

        Dictionary<string, ParticleRuntimeTarget> runtimeTargets = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParticleEffectEntry entry in targets)
        {
            string targetKey = GetParticleLiveTargetKey(entry, out CollectibleObject? collectible, out int index);
            if (collectible == null || index < 0 || string.IsNullOrWhiteSpace(targetKey)) continue;
            runtimeTargets[targetKey] = new(targetKey, collectible, index, entry);
        }

        if (runtimeTargets.Count == 0)
        {
            liveApplyManager.LastStatus = "Live target unavailable for selected particle group.";
            return liveApplyManager.LastStatus;
        }

        string desiredHash = BuildParticleDesiredRuntimeHash(runtimeTargets.Values);
        string currentRuntimeHash = BuildParticleCurrentRuntimeHash(runtimeTargets.Values);
        if (!force &&
            _particleLiveAppliedHashes.TryGetValue(scopeKey, out string? appliedHash) &&
            string.Equals(appliedHash, desiredHash, StringComparison.Ordinal) &&
            string.Equals(currentRuntimeHash, desiredHash, StringComparison.Ordinal))
        {
            return liveApplyManager.LastStatus;
        }

        string status = liveApplyManager.Apply(
            scopeKey,
            displayName,
            () => CaptureParticleGroupLiveSnapshot(scopeKey, runtimeTargets.Values.Select(target => target.Collectible)),
            () =>
            {
                foreach (ParticleRuntimeTarget target in runtimeTargets.Values)
                {
                    ApplyParticlePropertiesToCollectible(target.Collectible, target.Index, target.Entry.Properties);
                    TrackParticleRuntimeOverride(scopeKey, target);
                }
            },
            $"Applied ParticleProperties[{emitterIndex}] to {runtimeTargets.Count} {displayName} variant(s).");
        _particleLiveAppliedHashes[scopeKey] = desiredHash;
        return status;
    }

    private void TrackParticleRuntimeOverride(string scopeKey, ParticleRuntimeTarget target)
    {
        string? overrideKey = ParticleRuntimePatches.SetOverride(target.Collectible, target.Index, target.Entry.Properties);
        if (string.IsNullOrWhiteSpace(overrideKey)) return;

        foreach (HashSet<string> keys in _particleLiveOverrideKeysByScope.Values)
        {
            keys.Remove(overrideKey);
        }

        if (!_particleLiveOverrideKeysByScope.TryGetValue(scopeKey, out HashSet<string>? scopeKeys))
        {
            _particleLiveOverrideKeysByScope[scopeKey] = scopeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        scopeKeys.Add(overrideKey);
    }

    private void ClearParticleRuntimeOverrides(string scopeKey)
    {
        if (!_particleLiveOverrideKeysByScope.TryGetValue(scopeKey, out HashSet<string>? overrideKeys)) return;

        foreach (string overrideKey in overrideKeys)
        {
            ParticleRuntimePatches.RemoveOverride(overrideKey);
        }

        _particleLiveOverrideKeysByScope.Remove(scopeKey);
    }

    private static string BuildParticleDesiredRuntimeHash(IEnumerable<ParticleRuntimeTarget> targets)
    {
        return string.Join("\n---\n", targets
            .OrderBy(target => target.Key, StringComparer.OrdinalIgnoreCase)
            .Select(target => $"{target.Key}[{target.Index}]\n{ParticlePropertiesToToken(target.Entry.Properties)}"));
    }

    private static string BuildParticleCurrentRuntimeHash(IEnumerable<ParticleRuntimeTarget> targets)
    {
        return string.Join("\n---\n", targets
            .OrderBy(target => target.Key, StringComparer.OrdinalIgnoreCase)
            .Select(target => $"{target.Key}[{target.Index}]\n{ParticleRuntimePropertyToken(target.Collectible, target.Index)}"));
    }

    private static JToken ParticleRuntimePropertyToken(CollectibleObject collectible, int index)
    {
        if (collectible.ParticleProperties == null ||
            index < 0 ||
            index >= collectible.ParticleProperties.Length ||
            collectible.ParticleProperties[index] == null)
        {
            return JValue.CreateNull();
        }

        AdvancedParticleProperties clone = collectible.ParticleProperties[index].Clone();
        NormalizeParticleProperties(clone);
        return ParticlePropertiesToToken(clone);
    }

    private IEnumerable<ParticleEffectEntry> GetParticleLiveTargets(ParticleEffectFamily family, ParticleEffectVariant variant, int emitterIndex)
    {
        if (_particleVariantOnlyEdit)
        {
            ParticleEffectEntry? entry = variant.GetEmitter(emitterIndex);
            if (entry != null) yield return entry;
            yield break;
        }

        foreach (ParticleEffectVariant memberVariant in family.Variants)
        {
            ParticleEffectEntry? entry = memberVariant.GetEmitter(emitterIndex);
            if (entry != null) yield return entry;
        }
    }

    private string GetParticleLiveScopeKey(ParticleEffectFamily family, ParticleEffectVariant variant, int emitterIndex)
    {
        return _particleVariantOnlyEdit
            ? GetParticleVariantLiveScopeKey(variant, emitterIndex)
            : GetParticleFamilyLiveScopeKey(family, emitterIndex);
    }

    private static string GetParticleVariantLiveScopeKey(ParticleEffectVariant variant, int emitterIndex)
    {
        return $"particle:{variant.Key}:ParticleProperties[{emitterIndex}]";
    }

    private static string GetParticleFamilyLiveScopeKey(ParticleEffectFamily family, int emitterIndex)
    {
        return $"particle-family:{family.Key}:ParticleProperties[{emitterIndex}]";
    }

    private void ClearParticleLiveHashesForEmitter(ParticleEffectFamily family, ParticleEffectVariant variant, int emitterIndex)
    {
        _particleLiveAppliedHashes.Remove(GetParticleFamilyLiveScopeKey(family, emitterIndex));
        foreach (ParticleEffectVariant memberVariant in family.Variants)
        {
            _particleLiveAppliedHashes.Remove(GetParticleVariantLiveScopeKey(memberVariant, emitterIndex));
        }
    }

    private void ClearParticleLiveHashesForVariant(ParticleEffectFamily family, ParticleEffectVariant variant)
    {
        for (int emitterIndex = 0; emitterIndex < variant.EmitterCount; emitterIndex++)
        {
            _particleLiveAppliedHashes.Remove(GetParticleFamilyLiveScopeKey(family, emitterIndex));
            _particleLiveAppliedHashes.Remove(GetParticleVariantLiveScopeKey(variant, emitterIndex));
        }
    }

    private void ClearParticleLiveHashesForFamily(ParticleEffectFamily family)
    {
        for (int emitterIndex = 0; emitterIndex < family.MaxEmitterCount; emitterIndex++)
        {
            _particleLiveAppliedHashes.Remove(GetParticleFamilyLiveScopeKey(family, emitterIndex));
            foreach (ParticleEffectVariant variant in family.Variants)
            {
                _particleLiveAppliedHashes.Remove(GetParticleVariantLiveScopeKey(variant, emitterIndex));
            }
        }
    }

    private string ApplyParticleResetLiveEmitter(ParticleEffectFamily family, ParticleEffectVariant variant, int emitterIndex, DebugWindowManager.DevToolsLiveApplyManager? liveApplyManager)
    {
        if (liveApplyManager == null) return "runtime apply unavailable; runtime not updated.";
        if (!liveApplyManager.AutoApply) return "runtime apply disabled; runtime not updated.";

        List<ParticleEffectEntry> targets = GetParticleResetEmitterTargets(family, variant, emitterIndex, out _);
        string scopeKey = _particleVariantOnlyEdit
            ? GetParticleVariantLiveScopeKey(variant, emitterIndex)
            : GetParticleFamilyLiveScopeKey(family, emitterIndex);
        string displayName = _particleVariantOnlyEdit ? variant.DisplayKey : family.DisplayKey;
        return ApplyParticleLiveEntries(
            scopeKey,
            displayName,
            emitterIndex,
            targets,
            liveApplyManager,
            force: true,
            $"No runtime ParticleProperties[{emitterIndex}] target for {displayName}.");
    }

    private string ApplyParticleResetLiveVariant(ParticleEffectVariant variant, DebugWindowManager.DevToolsLiveApplyManager? liveApplyManager)
    {
        if (liveApplyManager == null) return "runtime apply unavailable; runtime not updated.";
        if (!liveApplyManager.AutoApply) return "runtime apply disabled; runtime not updated.";

        int applied = 0;
        for (int emitterIndex = 0; emitterIndex < variant.EmitterCount; emitterIndex++)
        {
            ParticleEffectEntry? entry = variant.GetEmitter(emitterIndex);
            if (entry == null) continue;

            ApplyParticleLiveEntries(
                GetParticleVariantLiveScopeKey(variant, emitterIndex),
                variant.DisplayKey,
                emitterIndex,
                [entry],
                liveApplyManager,
                force: true,
                $"No runtime ParticleProperties[{emitterIndex}] target for {variant.DisplayKey}.");
            applied++;
        }

        return applied == 0 ? "runtime apply had no emitters to update." : $"runtime apply processed {applied} emitter slot(s).";
    }

    private string ApplyParticleResetLiveFamily(ParticleEffectFamily family, DebugWindowManager.DevToolsLiveApplyManager? liveApplyManager)
    {
        if (liveApplyManager == null) return "runtime apply unavailable; runtime not updated.";
        if (!liveApplyManager.AutoApply) return "runtime apply disabled; runtime not updated.";

        int applied = 0;
        for (int emitterIndex = 0; emitterIndex < family.MaxEmitterCount; emitterIndex++)
        {
            List<ParticleEffectEntry> entries = family.Variants
                .Select(variant => variant.GetEmitter(emitterIndex))
                .Where(entry => entry != null)
                .Cast<ParticleEffectEntry>()
                .ToList();
            if (entries.Count == 0) continue;

            ApplyParticleLiveEntries(
                GetParticleFamilyLiveScopeKey(family, emitterIndex),
                family.DisplayKey,
                emitterIndex,
                entries,
                liveApplyManager,
                force: true,
                $"No runtime ParticleProperties[{emitterIndex}] target for {family.DisplayKey}.");
            applied++;
        }

        return applied == 0 ? "runtime apply had no emitters to update." : $"runtime apply processed {applied} emitter slot(s).";
    }

    private DebugWindowManager.LivePatchSnapshot CaptureParticleGroupLiveSnapshot(string targetKey, IEnumerable<CollectibleObject> collectibles)
    {
        List<ParticleCollectibleSnapshot> originals = collectibles
            .Distinct()
            .Select(collectible => new ParticleCollectibleSnapshot(collectible, CloneParticleArray(collectible.ParticleProperties)))
            .ToList();

        string backupPath = Path.Combine("assets", "runtime-particles", SanitizeBackupFileName(targetKey) + ".json");
        return new(
            () =>
            {
                foreach (ParticleCollectibleSnapshot snapshot in originals)
                {
                    snapshot.Collectible.ParticleProperties = CloneParticleArray(snapshot.Particles);
                }
            },
            backupPath,
            () => SerializeParticleBackup(originals.Count > 0 ? originals[0].Particles : null),
            "particles");
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
            () => SerializeParticleBackup(original),
            "particles");
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

    private void ApplyParticleEditorChange(
        ParticleEffectFamily family,
        ParticleEffectVariant variant,
        int emitterIndex,
        JToken before,
        JToken after,
        DebugWindowManager.DevToolsLiveApplyManager? liveApplyManager)
    {
        int changed = 0;
        int skipped = 0;
        IEnumerable<ParticleEffectVariant> targetVariants = _particleVariantOnlyEdit ? [variant] : family.Variants;
        foreach (ParticleEffectVariant targetVariant in targetVariants)
        {
            ParticleEffectEntry? target = targetVariant.GetEmitter(emitterIndex);
            if (target == null)
            {
                skipped++;
                continue;
            }

            if (!ReferenceEquals(target, variant.GetEmitter(emitterIndex)))
            {
                JToken targetToken = ParticlePropertiesToToken(target.Properties);
                ApplyParticleJsonDiff(targetToken, before, after);
                AdvancedParticleProperties? patched = new JsonObject(targetToken).AsObject<AdvancedParticleProperties>();
                if (patched != null)
                {
                    NormalizeParticleProperties(patched);
                    target.ReplaceProperties(patched);
                }
            }

            changed++;
        }

        _particleStatus = _particleVariantOnlyEdit
            ? $"Edited ParticleProperties[{emitterIndex}] for {variant.DisplayKey}."
            : $"Edited ParticleProperties[{emitterIndex}] for {changed} {family.DisplayKey} variant(s){(skipped > 0 ? $"; skipped {skipped} missing variant(s)" : "")}.";

        if (liveApplyManager?.AutoApply == true)
        {
            ApplySelectedParticleLive(family, variant, emitterIndex, liveApplyManager);
        }
    }

    private static JToken ParticlePropertiesToToken(AdvancedParticleProperties particleProperties)
    {
        JsonSerializerSettings settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        return JToken.FromObject(particleProperties, JsonSerializer.Create(settings));
    }

    private static AdvancedParticleProperties CloneNormalizedParticleProperties(AdvancedParticleProperties particleProperties)
    {
        AdvancedParticleProperties clone = particleProperties.Clone();
        NormalizeParticleProperties(clone);
        return clone;
    }

    private static JToken NormalizedParticlePropertiesToToken(AdvancedParticleProperties particleProperties)
    {
        return ParticlePropertiesToToken(CloneNormalizedParticleProperties(particleProperties));
    }

    private static bool ParticlePropertiesMatch(AdvancedParticleProperties left, AdvancedParticleProperties right)
    {
        return JToken.DeepEquals(NormalizedParticlePropertiesToToken(left), NormalizedParticlePropertiesToToken(right));
    }

    private static void ApplyParticleJsonDiff(JToken target, JToken before, JToken after)
    {
        if (JToken.DeepEquals(before, after)) return;

        if (target is JObject targetObject && before is JObject beforeObject && after is JObject afterObject)
        {
            foreach (JProperty afterProperty in afterObject.Properties())
            {
                JToken? beforeValue = beforeObject[afterProperty.Name];
                if (beforeValue == null)
                {
                    targetObject[afterProperty.Name] = afterProperty.Value.DeepClone();
                    continue;
                }

                if (targetObject[afterProperty.Name] == null || !JToken.DeepEquals(beforeValue, afterProperty.Value))
                {
                    if (targetObject[afterProperty.Name] is JObject && beforeValue is JObject && afterProperty.Value is JObject)
                    {
                        ApplyParticleJsonDiff(targetObject[afterProperty.Name]!, beforeValue, afterProperty.Value);
                    }
                    else
                    {
                        targetObject[afterProperty.Name] = afterProperty.Value.DeepClone();
                    }
                }
            }

            foreach (JProperty beforeProperty in beforeObject.Properties())
            {
                if (afterObject[beforeProperty.Name] == null)
                {
                    targetObject.Property(beforeProperty.Name)?.Remove();
                }
            }

            return;
        }

        target.Replace(after.DeepClone());
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
        if (particles == null) return null;

        AdvancedParticleProperties[] clones = new AdvancedParticleProperties[particles.Length];
        for (int index = 0; index < particles.Length; index++)
        {
            if (particles[index] != null)
            {
                clones[index] = particles[index].Clone();
            }
        }

        return clones;
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

    private const float RuntimePreviewTickIntervalSeconds = 0.033f;

    private string _particleFilter = "";
    private bool _previewLoop = true;
    private ParticlePreviewMode _previewMode = ParticlePreviewMode.RuntimeBlockTick;
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
    private bool _previewShowReferenceModel = true;
    private bool _previewReferenceWireframe;
    private float _previewReferenceOpacity = 0.35f;
    private string _previewManualReferenceFilter = "";
    private string _previewManualReferenceKey = "";
    private int _previewManualReferenceIndex;
    private float _previewEmitAccumulator;
    private float _previewRuntimeSeconds;
    private string _previewEffectKey = "";
    private string _previewStatus = "";
    private string _selectedParticleFamilyKey = "";
    private string _selectedParticleVariantKey = "";
    private int _selectedParticleEmitterIndex;
    private bool _particleVariantOnlyEdit;
    private string _particleStatus = "";
    private readonly DevToolsEditorDiagnostics _diagnostics = new("Particles");
    private readonly Dictionary<string, ParticlePreviewPlacement> _previewPlacementCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _particleLiveAppliedHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _particleLiveOverrideKeysByScope = new(StringComparer.OrdinalIgnoreCase);
    private DevToolsPreview3DRenderer? _previewRenderer3D;
    private readonly ICoreAPI _api;
    private readonly Dictionary<string, ParticleEffectEntry> _particleEffects = new(StringComparer.OrdinalIgnoreCase);
    private ParticleAssetScanDiagnostics _scanDiagnostics = new();
    private bool _verboseParticleScanLog;
    private readonly IClientNetworkChannel? _clientChannel;
    private const string _networkChannelId = "InGameDevTools:particle-effects";

    private string _particleDomainFilter = "";
    private readonly ImGuiThreePanelLayoutState _layout = new(0.22f, 0.42f);

    private enum ParticleEffectSourceKind
    {
        NamedConfig,
        EmbeddedAsset,
        RuntimeBlock,
        RuntimeItem
    }

    private enum ParticlePreviewMode
    {
        RuntimeBlockTick,
        EmitterList
    }

    private sealed class ParticleEffectEntry
    {
        public ParticleEffectEntry(string key, string source, ParticleEffectSourceKind sourceKind, AdvancedParticleProperties properties)
        {
            Key = key;
            Source = source;
            SourceKind = sourceKind;
            Properties = CloneNormalizedParticleProperties(properties);
            OriginalProperties = CloneNormalizedParticleProperties(Properties);
            Domain = ImGuiLayoutHelper.GetDomainFromAssetCode(key);
            DisplayKey = ImGuiLayoutHelper.CompactAssetCode(key);
            SearchText = $"{key} {source} {SourceKindLabel}";
        }

        public string Key { get; }
        public string Source { get; }
        public string Domain { get; }
        public string DisplayKey { get; }
        public string SearchText { get; }
        public ParticleEffectSourceKind SourceKind { get; }
        public AdvancedParticleProperties Properties { get; private set; }
        public AdvancedParticleProperties OriginalProperties { get; }
        public bool IsModified => !ParticlePropertiesMatch(Properties, OriginalProperties);
        public string SourceKindLabel => SourceKind switch
        {
            ParticleEffectSourceKind.NamedConfig => "Named config",
            ParticleEffectSourceKind.RuntimeBlock => "Loaded block",
            ParticleEffectSourceKind.RuntimeItem => "Loaded item",
            _ => "Embedded asset"
        };

        public void ReplaceProperties(AdvancedParticleProperties properties)
        {
            Properties = CloneNormalizedParticleProperties(properties);
        }

        public void ResetToOriginal()
        {
            Properties = CloneNormalizedParticleProperties(OriginalProperties);
        }
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

    private void DrawPreviewPanel(string id, ParticleEffectVariant selectedVariant, IReadOnlyList<ParticleEffectEntry> emitters, AdvancedParticleProperties? selectedParticleProperties, float deltaSeconds)
    {
        string key = selectedVariant.ReferenceKey;
        if (!string.Equals(_previewEffectKey, key, StringComparison.OrdinalIgnoreCase))
        {
            ResetPreview(key);
        }

        ImGui.SeparatorText("Preview");
        bool runtimePreviewAvailable = TryResolveRuntimePreviewBlock(selectedVariant, out Block? runtimePreviewBlock, out string runtimePreviewSource);
        int previewModeIndex = (int)_previewMode;
        string[] previewModes = ["Runtime block tick", "Emitter list"];
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo($"Mode##particle-preview-mode-{id}", ref previewModeIndex, previewModes, previewModes.Length))
        {
            _previewMode = (ParticlePreviewMode)Math.Clamp(previewModeIndex, 0, previewModes.Length - 1);
            ResetPreview(key);
        }

        bool useRuntimePreview = _previewMode == ParticlePreviewMode.RuntimeBlockTick && runtimePreviewAvailable && runtimePreviewBlock != null;
        ImGui.SameLine();
        ImGui.TextDisabled(useRuntimePreview
            ? $"Runtime block: {runtimePreviewSource}"
            : _previewMode == ParticlePreviewMode.RuntimeBlockTick
                ? $"Runtime block unavailable: {runtimePreviewSource}"
                : "Emitter-list preview");

        if (ImGui.Button($"Emit##particle-preview-{id}"))
        {
            EmitPreviewTickOrParticles(selectedVariant, emitters, _previewIntensity, useRuntimePreview, runtimePreviewBlock, runtimePreviewSource);
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

        bool runtimeRateControlsDisabled = useRuntimePreview;
        ImGui.SetNextItemWidth(110);
        if (runtimeRateControlsDisabled) ImGui.BeginDisabled();
        ImGui.SliderFloat($"Rate##particle-preview-{id}", ref _previewEmitRate, 0.05f, 20f, "%.2f/s");
        if (runtimeRateControlsDisabled) ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        if (runtimeRateControlsDisabled) ImGui.BeginDisabled();
        ImGui.SliderFloat($"Intensity##particle-preview-{id}", ref _previewIntensity, 0.05f, 8f, "%.2f");
        if (runtimeRateControlsDisabled) ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        if (runtimeRateControlsDisabled) ImGui.BeginDisabled();
        ImGui.SliderFloat($"Speed##particle-preview-{id}", ref _previewTimeScale, 0.05f, 4f, "%.2fx");
        if (runtimeRateControlsDisabled) ImGui.EndDisabled();
        if (useRuntimePreview)
        {
            ImGui.TextDisabled($"Runtime block tick uses the engine {RuntimePreviewTickIntervalSeconds:0.000}s async cadence; rate, intensity, and speed are ignored.");
        }

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
            ImGui.Checkbox($"Use game wind##particle-preview-{id}", ref _previewWindEnabled);
            ImGui.SameLine();
            ImGui.TextDisabled($"Selected wind affectedness: {selectedParticleProperties?.WindAffectednes ?? 0f:0.00}");
            Vec3f wind = GlobalConstants.CurrentWindSpeedClient;
            ImGui.TextDisabled($"Current game wind: {wind.X:0.00}, {wind.Y:0.00}, {wind.Z:0.00}");
        }

        if (ImGui.CollapsingHeader($"Preview environment##particle-preview-environment-{id}"))
        {
            ImGui.TextDisabled("Uses Vintage Story ParticlePhysics against the current client world.");
        }

        float dt = Math.Clamp(deltaSeconds, 0f, 0.1f) * (useRuntimePreview ? 1f : _previewTimeScale);
        if (_previewLoop && dt > 0)
        {
            if (useRuntimePreview && runtimePreviewBlock != null)
            {
                _previewEmitAccumulator += dt;
                int ticks = 0;
                while (_previewEmitAccumulator >= RuntimePreviewTickIntervalSeconds && ticks < 8)
                {
                    EmitRuntimeBlockPreviewTick(selectedVariant, emitters, runtimePreviewBlock, _previewIntensity, runtimePreviewSource);
                    _previewRuntimeSeconds += RuntimePreviewTickIntervalSeconds;
                    _previewEmitAccumulator -= RuntimePreviewTickIntervalSeconds;
                    ticks++;
                }
            }
            else
            {
                Vector3 particleOrigin = GetPreviewParticleOrigin(key);
                _previewEmitAccumulator += dt * _previewEmitRate;
                while (_previewEmitAccumulator >= 1f)
                {
                    EmitPreviewParticles(emitters, _previewIntensity, particleOrigin);
                    _previewEmitAccumulator -= 1f;
                }
            }
        }

        DrawPreviewViewport(id, key, emitters, selectedParticleProperties, dt);
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

    private void DrawPreviewViewport(string id, string key, IReadOnlyList<ParticleEffectEntry> emitters, AdvancedParticleProperties? selectedParticleProperties, float deltaSeconds)
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
        List<DevToolsPreviewMeshInstance> meshInstances = [];
        if (referenceModel?.Mesh != null)
        {
            Matrixf identity = new();
            identity.Identity();
            meshInstances.Add(new DevToolsPreviewMeshInstance(referenceModel.Mesh, identity));
        }

        DevToolsPreview3DRenderer previewRenderer = EnsurePreviewRenderer3D();
        int textureId = previewRenderer.RenderToTexture(
            max.X - min.X,
            max.Y - min.Y,
            previewCamera,
            meshInstances,
            deltaSeconds,
            out string? skipReason);
        if (textureId > 0)
        {
            drawList.AddImage(new IntPtr(textureId), min, max, new System.Numerics.Vector2(0f, 1f), new System.Numerics.Vector2(1f, 0f));
        }
        else if (!string.IsNullOrWhiteSpace(skipReason))
        {
            drawList.AddText(min + new System.Numerics.Vector2(12f, 90f), text, $"Preview render skipped: {skipReason}");
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
            Vector3 wind = selectedParticleProperties == null ? Vector3.Zero : GetPreviewGameWindVector(selectedParticleProperties);
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

        drawList.PopClipRect();

        drawList.AddText(min + new System.Numerics.Vector2(12f, 10f), text, $"{previewRenderer.ParticleCount} engine preview particles");
        string referenceText = referenceModel == null
            ? "Reference model: none"
            : $"Reference model: {referenceModel.Label}";
        drawList.AddText(min + new System.Numerics.Vector2(12f, 30f), text, referenceText);
        drawList.AddText(min + new System.Numerics.Vector2(12f, 50f), text, _previewWindEnabled ? "Game wind enabled" : "Game wind disabled");
        string previewStatus = string.IsNullOrWhiteSpace(_previewStatus)
            ? "Environment: current client world ParticlePhysics"
            : _previewStatus;
        drawList.AddText(min + new System.Numerics.Vector2(12f, 70f), text, previewStatus);
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
            _diagnostics.Warning($"Particle reference model failed for {entry.Key}", exception.ToString());
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

            return DevToolsJson.TryParseObject(asset.ToText())?["code"]?.ToString();
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
        _previewRenderer3D?.ResetParticles();
        _previewEmitAccumulator = 0;
        _previewRuntimeSeconds = 0;
        _previewStatus = "";
        ResetPreviewCamera(key);
    }

    private int EmitPreviewTickOrParticles(
        ParticleEffectVariant selectedVariant,
        IReadOnlyList<ParticleEffectEntry> emitters,
        float intensity,
        bool useRuntimePreview,
        Block? runtimePreviewBlock,
        string runtimePreviewSource)
    {
        if (useRuntimePreview && runtimePreviewBlock != null)
        {
            int spawned = EmitRuntimeBlockPreviewTick(selectedVariant, emitters, runtimePreviewBlock, intensity, runtimePreviewSource);
            _previewRuntimeSeconds += RuntimePreviewTickIntervalSeconds;
            return spawned;
        }

        int emitterSpawned = EmitPreviewParticles(emitters, intensity, GetPreviewParticleOrigin(selectedVariant.ReferenceKey));
        _previewStatus = $"Emitter-list preview: spawned {emitterSpawned} particle(s) from {emitters.Count} emitter(s).";
        return emitterSpawned;
    }

    private bool TryResolveRuntimePreviewBlock(ParticleEffectVariant selectedVariant, out Block? block, out string source)
    {
        if (selectedVariant.Collectible is Block variantBlock && variantBlock.Code != null)
        {
            block = variantBlock;
            source = variantBlock.Code.ToString();
            return true;
        }

        foreach (ParticleEffectEntry entry in selectedVariant.EmittersWithParticles)
        {
            if (TryResolveReferenceBlock(entry, out Block? resolvedBlock) && resolvedBlock?.Code != null)
            {
                block = resolvedBlock;
                source = resolvedBlock.Code.ToString();
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(_previewManualReferenceKey) &&
            _previewManualReferenceKey.StartsWith("block:", StringComparison.OrdinalIgnoreCase))
        {
            block = ResolveBlock(_previewManualReferenceKey["block:".Length..], null);
            if (block?.Code != null)
            {
                source = $"{block.Code} (manual reference)";
                return true;
            }
        }

        block = null;
        source = selectedVariant.Collectible is Item
            ? "selected source is an item"
            : "no block reference is resolved";
        return false;
    }

    private int EmitRuntimeBlockPreviewTick(
        ParticleEffectVariant selectedVariant,
        IReadOnlyList<ParticleEffectEntry> emitters,
        Block block,
        float intensity,
        string runtimePreviewSource)
    {
        if (_api is not ICoreClientAPI clientApi)
        {
            _previewStatus = "Runtime block tick preview requires the client API.";
            return EmitPreviewParticles(emitters, intensity, GetPreviewParticleOrigin(selectedVariant.ReferenceKey));
        }

        DevToolsPreview3DRenderer previewRenderer = EnsurePreviewRenderer3D();
        BlockPos tickPos = GetPreviewRuntimeTickPos(clientApi);
        Vec3d worldToPreviewOffset = new(-tickPos.X, -tickPos.InternalY, -tickPos.Z);
        PreviewAsyncParticleManager previewManager = new(
            clientApi.World.BlockAccessor,
            previewRenderer,
            _previewTarget,
            worldToPreviewOffset,
            new Vec3f(_previewVelocityX, _previewVelocityY, _previewVelocityZ),
            _previewWindEnabled);

        AdvancedParticleProperties[]? original = block.ParticleProperties;
        AdvancedParticleProperties[] patched = BuildRuntimePreviewParticleProperties(block, selectedVariant);
        try
        {
            block.ParticleProperties = patched;
            block.OnAsyncClientParticleTick(
                previewManager,
                tickPos,
                GetRuntimePreviewWindAffectedness(selectedVariant, patched),
                _previewRuntimeSeconds);

            int spawned = previewManager.Flush();
            _previewStatus = $"Runtime tick preview ({RuntimePreviewTickIntervalSeconds:0.000}s cadence): {runtimePreviewSource}, captured {previewManager.CapturedCount} provider(s), spawned {spawned} particle(s).";
            return spawned;
        }
        catch (Exception exception)
        {
            _previewStatus = $"Runtime tick preview failed for {runtimePreviewSource}: {exception.Message}; using emitter-list fallback.";
            _diagnostics.Exception($"Runtime tick preview failed for {runtimePreviewSource}", exception);
            LoggerUtil.Verbose(_api, this, _previewStatus);
            return EmitPreviewParticles(emitters, intensity, GetPreviewParticleOrigin(selectedVariant.ReferenceKey));
        }
        finally
        {
            block.ParticleProperties = original;
        }
    }

    private static BlockPos GetPreviewRuntimeTickPos(ICoreClientAPI clientApi)
    {
        return clientApi.World.Player?.Entity?.Pos?.AsBlockPos ?? new BlockPos(0, 0, 0);
    }

    private AdvancedParticleProperties[] BuildRuntimePreviewParticleProperties(Block block, ParticleEffectVariant selectedVariant)
    {
        AdvancedParticleProperties[]? original = block.ParticleProperties;
        int length = Math.Max(original?.Length ?? 0, selectedVariant.Emitters.Count);
        AdvancedParticleProperties[] patched = new AdvancedParticleProperties[length];
        for (int index = 0; index < length; index++)
        {
            AdvancedParticleProperties? source = null;
            if (index < selectedVariant.Emitters.Count && selectedVariant.Emitters[index] != null)
            {
                source = selectedVariant.Emitters[index]!.Properties;
            }
            else if (original != null && index < original.Length && original[index] != null)
            {
                source = original[index];
            }

            patched[index] = source == null
                ? CreateDisabledParticleProperties()
                : CloneNormalizedParticleProperties(source);
        }

        return patched;
    }

    private static AdvancedParticleProperties CreateDisabledParticleProperties()
    {
        AdvancedParticleProperties properties = new();
        properties.Quantity = NatFloat.createUniform(0f, 0f);
        return properties;
    }

    private float GetRuntimePreviewWindAffectedness(ParticleEffectVariant selectedVariant, AdvancedParticleProperties[] patched)
    {
        if (!_previewWindEnabled) return 0f;

        IEnumerable<float> configured = selectedVariant.EmittersWithParticles
            .Select(entry => Math.Max(entry.Properties.WindAffectednesAtPos, entry.Properties.WindAffectednes))
            .Concat(patched.Select(properties => Math.Max(properties.WindAffectednesAtPos, properties.WindAffectednes)));
        float windAffectedness = configured.DefaultIfEmpty(0f).Max();
        return Math.Max(1f, windAffectedness);
    }

    private int EmitPreviewParticles(IReadOnlyList<ParticleEffectEntry> emitters, float intensity, Vector3 origin)
    {
        List<AdvancedParticleProperties> prepared = new(emitters.Count);
        for (int emitterIndex = 0; emitterIndex < emitters.Count; emitterIndex++)
        {
            prepared.Add(PrepareEnginePreviewParticle(emitters[emitterIndex].Properties, intensity, origin));
        }

        return EnsurePreviewRenderer3D().SpawnParticles(prepared, _previewTarget);
    }

    private AdvancedParticleProperties PrepareEnginePreviewParticle(AdvancedParticleProperties particleProperties, float intensity, Vector3 origin)
    {
        AdvancedParticleProperties clone = particleProperties.Clone();
        NormalizeParticleTree(clone);

        clone.basePos = new Vec3d(origin.X, origin.Y, origin.Z);
        clone.baseVelocity = new Vec3f(_previewVelocityX, _previewVelocityY, _previewVelocityZ);
        clone.WindAffectednesAtPos = _previewWindEnabled ? Math.Max(1f, clone.WindAffectednesAtPos) : 0f;

        ScaleNatFloat(clone.Quantity, Math.Max(0.05f, intensity));
        return clone;
    }

    private static void NormalizeParticleTree(AdvancedParticleProperties particleProperties)
    {
        NormalizeParticleProperties(particleProperties);
        if (particleProperties.SecondaryParticles != null)
        {
            foreach (AdvancedParticleProperties secondary in particleProperties.SecondaryParticles)
            {
                if (secondary != null) NormalizeParticleTree(secondary);
            }
        }

        if (particleProperties.DeathParticles != null)
        {
            foreach (AdvancedParticleProperties death in particleProperties.DeathParticles)
            {
                if (death != null) NormalizeParticleTree(death);
            }
        }
    }

    private static void ScaleNatFloat(NatFloat? value, float multiplier)
    {
        if (value == null || Math.Abs(multiplier - 1f) <= 0.0001f) return;

        value.avg *= multiplier;
        value.var *= multiplier;
        value.offset *= multiplier;
    }

    private static void ApplyRuntimePreviewParticleModifiers(
        AdvancedParticleProperties particleProperties,
        Vec3d worldToPreviewOffset,
        Vec3f extraVelocity,
        bool windEnabled)
    {
        NormalizeParticleTree(particleProperties);
        particleProperties.basePos.X += worldToPreviewOffset.X;
        particleProperties.basePos.Y += worldToPreviewOffset.Y;
        particleProperties.basePos.Z += worldToPreviewOffset.Z;
        particleProperties.baseVelocity.X += extraVelocity.X;
        particleProperties.baseVelocity.Y += extraVelocity.Y;
        particleProperties.baseVelocity.Z += extraVelocity.Z;
        if (!windEnabled)
        {
            particleProperties.WindAffectednesAtPos = 0f;
        }
    }

    private Vector3 GetPreviewGameWindVector(AdvancedParticleProperties particleProperties)
    {
        if (!_previewWindEnabled) return Vector3.Zero;

        Vec3f wind = GlobalConstants.CurrentWindSpeedClient;
        return new Vector3(wind.X, wind.Y, wind.Z) * Math.Max(0f, particleProperties.WindAffectednes);
    }

    private sealed class ParticleEffectFamilyBuilder(string key, string displayKey, string domain, string searchText, string tooltip)
    {
        private readonly Dictionary<string, ParticleEffectVariantBuilder> _variants = new(StringComparer.OrdinalIgnoreCase);

        public void Add(ParticleEffectMember member)
        {
            if (!_variants.TryGetValue(member.VariantKey, out ParticleEffectVariantBuilder? variant))
            {
                _variants[member.VariantKey] = variant = new ParticleEffectVariantBuilder(member);
            }

            variant.Add(member);
        }

        public ParticleEffectFamily Build()
        {
            List<ParticleEffectVariant> variants = _variants.Values
                .Select(variant => variant.Build())
                .OrderBy(variant => variant.DisplayKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
            int maxEmitterCount = variants.Count == 0 ? 0 : variants.Max(variant => variant.EmitterCount);
            return new(key, displayKey, domain, $"{searchText} {string.Join(' ', variants.Select(variant => variant.SearchText))}", tooltip, variants, maxEmitterCount);
        }
    }

    private sealed class ParticleEffectVariantBuilder(ParticleEffectMember first)
    {
        private readonly List<ParticleEffectEntry?> _emitters = [];
        private readonly ParticleEffectMember _first = first;

        public void Add(ParticleEffectMember member)
        {
            int index = Math.Max(0, member.EmitterIndex);
            while (_emitters.Count <= index)
            {
                _emitters.Add(null);
            }

            _emitters[index] = member.Entry;
        }

        public ParticleEffectVariant Build()
        {
            return new(
                _first.VariantKey,
                _first.VariantDisplayKey,
                _first.SourceKindLabel,
                _first.Source,
                _first.ReferenceKey,
                _first.Collectible,
                _first.Runtime,
                _emitters,
                _first.SearchText);
        }
    }

    private sealed class ParticleEffectFamily(
        string key,
        string displayKey,
        string domain,
        string searchText,
        string tooltip,
        List<ParticleEffectVariant> variants,
        int maxEmitterCount)
    {
        public string Key { get; } = key;
        public string DisplayKey { get; } = displayKey;
        public string Domain { get; } = domain;
        public string SearchText { get; } = searchText;
        public string Tooltip { get; } = tooltip;
        public List<ParticleEffectVariant> Variants { get; } = variants;
        public int MaxEmitterCount { get; } = maxEmitterCount;
        public int ModifiedEmitterCount => Variants.Sum(variant => variant.ModifiedEmitterCount);
        public bool IsModified => ModifiedEmitterCount > 0;
    }

    private sealed class ParticleEffectVariant(
        string key,
        string displayKey,
        string sourceKindLabel,
        string source,
        string referenceKey,
        CollectibleObject? collectible,
        bool runtime,
        List<ParticleEffectEntry?> emitters,
        string searchText)
    {
        public string Key { get; } = key;
        public string DisplayKey { get; } = displayKey;
        public string SourceKindLabel { get; } = sourceKindLabel;
        public string Source { get; } = source;
        public string ReferenceKey { get; } = referenceKey;
        public CollectibleObject? Collectible { get; } = collectible;
        public bool Runtime { get; } = runtime;
        public List<ParticleEffectEntry?> Emitters { get; } = emitters;
        public string SearchText { get; } = searchText;
        public int EmitterCount => Emitters.Count;
        public IReadOnlyList<ParticleEffectEntry> EmittersWithParticles => Emitters.Where(entry => entry != null).Cast<ParticleEffectEntry>().ToList();
        public int ModifiedEmitterCount => Emitters.Count(entry => entry?.IsModified == true);
        public bool IsModified => ModifiedEmitterCount > 0;

        public ParticleEffectEntry? GetEmitter(int index)
        {
            return index >= 0 && index < Emitters.Count ? Emitters[index] : null;
        }
    }

    private readonly record struct ParticleEffectMember(
        string FamilyKey,
        string FamilyDisplayKey,
        string Domain,
        string VariantKey,
        string VariantDisplayKey,
        string SourceKindLabel,
        string Source,
        string SearchText,
        string ReferenceKey,
        string VariantReferenceKey,
        int EmitterIndex,
        ParticleEffectEntry Entry,
        CollectibleObject? Collectible,
        bool Runtime,
        string FamilyTooltip);

    private readonly record struct ParticleRuntimeTarget(string Key, CollectibleObject Collectible, int Index, ParticleEffectEntry Entry);

    private readonly record struct ParticleCollectibleSnapshot(CollectibleObject Collectible, AdvancedParticleProperties[]? Particles);

    private sealed class PreviewAsyncParticleManager(
        IBlockAccessor blockAccess,
        DevToolsPreview3DRenderer previewRenderer,
        Vector3 cameraPosition,
        Vec3d worldToPreviewOffset,
        Vec3f extraVelocity,
        bool windEnabled) : IAsyncParticleManager
    {
        private readonly List<IParticlePropertiesProvider> _captured = [];

        public IBlockAccessor BlockAccess { get; } = blockAccess;
        public int CapturedCount => _captured.Count;

        public int Spawn(IParticlePropertiesProvider particleProperties)
        {
            if (particleProperties == null) return 0;

            _captured.Add(CreatePreviewProvider(particleProperties));
            return 1;
        }

        public int ParticlesAlive(EnumParticleModel model)
        {
            return previewRenderer.ParticleCountFor(model);
        }

        public int Flush()
        {
            return previewRenderer.SpawnParticleProviders(_captured, cameraPosition);
        }

        private IParticlePropertiesProvider CreatePreviewProvider(IParticlePropertiesProvider particleProperties)
        {
            if (particleProperties is AdvancedParticleProperties advancedParticleProperties)
            {
                AdvancedParticleProperties clone = advancedParticleProperties.Clone();
                ApplyRuntimePreviewParticleModifiers(clone, worldToPreviewOffset, extraVelocity, windEnabled);
                return clone;
            }

            return new OffsetParticlePropertiesProvider(particleProperties, worldToPreviewOffset);
        }
    }

    private sealed class OffsetParticlePropertiesProvider(IParticlePropertiesProvider inner, Vec3d worldToPreviewOffset) : IParticlePropertiesProvider
    {
        public bool IgnoreUserConfig => inner.IgnoreUserConfig;
        public bool Async => inner.Async;
        public float ParentVelocityWeight => inner.ParentVelocityWeight;
        public bool DieInLiquid => inner.DieInLiquid;
        public bool SwimOnLiquid => inner.SwimOnLiquid;
        public float Bounciness => inner.Bounciness;
        public bool DieInAir => inner.DieInAir;
        public bool DieOnRainHeightmap => inner.DieOnRainHeightmap;
        public float Quantity => inner.Quantity;
        public Vec3d Pos => Offset(inner.Pos, worldToPreviewOffset);
        public Vec3f ParentVelocity => inner.ParentVelocity;
        public int LightEmission => inner.LightEmission;
        public EvolvingNatFloat OpacityEvolve => inner.OpacityEvolve;
        public EvolvingNatFloat RedEvolve => inner.RedEvolve;
        public EvolvingNatFloat GreenEvolve => inner.GreenEvolve;
        public EvolvingNatFloat BlueEvolve => inner.BlueEvolve;
        public EnumParticleModel ParticleModel => inner.ParticleModel;
        public float Size => inner.Size;
        public EvolvingNatFloat SizeEvolve => inner.SizeEvolve;
        public EvolvingNatFloat[] VelocityEvolve => inner.VelocityEvolve;
        public float GravityEffect => inner.GravityEffect;
        public float LifeLength => inner.LifeLength;
        public int VertexFlags => inner.VertexFlags;
        public bool SelfPropelled => inner.SelfPropelled;
        public bool TerrainCollision => inner.TerrainCollision;
        public float SecondarySpawnInterval => inner.SecondarySpawnInterval;
        public IParticlePropertiesProvider[] SecondaryParticles => inner.SecondaryParticles;
        public IParticlePropertiesProvider[] DeathParticles => inner.DeathParticles;
        public bool RandomVelocityChange => inner.RandomVelocityChange;

        public void Init(ICoreAPI api) => inner.Init(api);

        public void BeginParticle() => inner.BeginParticle();

        public Vec3f GetVelocity(Vec3d pos)
        {
            Vec3d worldPos = Offset(pos, new Vec3d(-worldToPreviewOffset.X, -worldToPreviewOffset.Y, -worldToPreviewOffset.Z));
            return inner.GetVelocity(worldPos);
        }

        public int GetRgbaColor(ICoreClientAPI capi) => inner.GetRgbaColor(capi);

        public void ToBytes(BinaryWriter writer) => inner.ToBytes(writer);

        public void FromBytes(BinaryReader reader, IWorldAccessor resolver) => inner.FromBytes(reader, resolver);

        public void PrepareForSecondarySpawn(ParticleBase particleInstance) => inner.PrepareForSecondarySpawn(particleInstance);

        private static Vec3d Offset(Vec3d value, Vec3d offset)
        {
            return new Vec3d(value.X + offset.X, value.Y + offset.Y, value.Z + offset.Z);
        }
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

        Vec3f vanillaPlayerPosition = player.Pos.AheadCopy(0).XYZFloat.Add(0, (float)player.LocalEyePos.Y, 0);

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
            effect = Get(packet.Code).Clone();
            NormalizeParticleProperties(effect, stripVelocityEvolve: true);
        }
        catch (Exception exception)
        {
            _diagnostics.Exception($"Spawn particles failed for {packet.Code}", exception);
            LoggerUtil.Error(_api, this, $"Error on spawning particles: {exception}");
            return;
        }

        effect.basePos = new(packet.Position[0], packet.Position[1], packet.Position[2]);

        effect.Velocity[0].avg += packet.Velocity[0];
        effect.Velocity[1].avg += packet.Velocity[1];
        effect.Velocity[2].avg += packet.Velocity[2];

        effect.Quantity.avg *= packet.Intensity;
        effect.Quantity.var *= packet.Intensity;

        _api.World.SpawnParticles(effect, player);
    }
    private static Vector3 FromCameraReferenceFrame(Entity player, Vector3 position)
    {
        Vec3f vanillaViewVector = player.Pos.GetViewVector().Normalize();
        Vector3 viewVector = new(vanillaViewVector.X, vanillaViewVector.Y, vanillaViewVector.Z);
        Vector3 vertical = new(0, 1, 0);
        Vector3 localZ = viewVector;
        Vector3 localX = Vector3.Normalize(Vector3.Cross(localZ, vertical));
        Vector3 localY = Vector3.Cross(localX, localZ);
        return localX * position.X + localY * position.Y + localZ * position.Z;
    }
}

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
        HsvaNatFloatDetailsEditor(id, particleProperties);

        bool ColorByBlock = particleProperties.ColorByBlock;
        ImGui.Checkbox($"Color by block##{id}", ref ColorByBlock);
        particleProperties.ColorByBlock = ColorByBlock;

        ImGui.Unindent();
    }
    private static void ColorEvolveEditor(string id, AdvancedParticleProperties particleProperties)
    {
        if (!ImGui.CollapsingHeader($"Color evolve:##{id}")) return;
        ImGui.Indent();

        EvolvingNatFloat opacity = particleProperties.OpacityEvolve;
        EvolvingNatFloatEditorOptional(id, "Opacity (direct alpha)", ref opacity);
        particleProperties.OpacityEvolve = opacity;

        EvolvingNatFloat red = particleProperties.RedEvolve;
        EvolvingNatFloatEditorOptional(id, "Red (additive)", ref red);
        particleProperties.RedEvolve = red;

        EvolvingNatFloat green = particleProperties.GreenEvolve;
        EvolvingNatFloatEditorOptional(id, "Green (additive)", ref green);
        particleProperties.GreenEvolve = green;

        EvolvingNatFloat blue = particleProperties.BlueEvolve;
        EvolvingNatFloatEditorOptional(id, "Blue (additive)", ref blue);
        particleProperties.BlueEvolve = blue;

        ImGui.Unindent();
    }
    private static void NatFloatEditor(string id, string name, ref NatFloat value)
    {
        value ??= new NatFloat(0f, 0f, EnumDistribution.UNIFORM);
        ImGui.PushID($"{name}{id}");
        ImGui.Text($"{name}:");

        ImGui.SetNextItemWidth(82);
        ImGui.InputFloat("Offset", ref value.offset, 0, 0, "%.3f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(82);
        ImGui.InputFloat("Avg", ref value.avg, 0, 0, "%.3f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(82);
        ImGui.InputFloat("Var", ref value.var, 0, 0, "%.3f");
        ImGui.SameLine();
        NatFloatDistributionEditor("Dist", ref value.dist);
        ImGui.PopID();
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
        NatFloatEditor(id, "Secondary spawn interval", ref SecondarySpawnInterval);
        particleProperties.SecondarySpawnInterval = SecondarySpawnInterval;

        float windAffectedness = particleProperties.WindAffectednes;
        ImGui.InputFloat($"Wind affectedness##{id}", ref windAffectedness);
        particleProperties.WindAffectednes = windAffectedness;

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

        EvolvingNatFloat sizeEvolve = particleProperties.SizeEvolve;
        EvolvingNatFloatEditorOptional(id, "Size evolve (direct size)", ref sizeEvolve, defaultWhenEnabled: EvolvingNatFloat.createIdentical(0f));
        particleProperties.SizeEvolve = sizeEvolve;

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
        ImGui.SliderInt($"Z offset##{id}", ref ZOffset, 0, 7);
        flags.ZOffset = (byte)ZOffset;

        bool Lod0 = flags.Lod0;
        ImGui.Checkbox("Lod0", ref Lod0);
        flags.Lod0 = Lod0;

        EnumWindBitMode WindMode = flags.WindMode;
        WindBitModeEditor(id, "Wind mode", ref WindMode);
        flags.WindMode = WindMode;

        int WindData = flags.WindData;
        ImGui.SliderInt($"Wind data##{id}", ref WindData, 0, 7);
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
    private static void HsvaNatFloatDetailsEditor(string id, AdvancedParticleProperties particleProperties)
    {
        if (!ImGui.TreeNode($"HSVA randomization##{id}")) return;
        NatFloatEditor(id, "Hue", ref particleProperties.HsvaColor[0]);
        NatFloatEditor(id, "Saturation", ref particleProperties.HsvaColor[1]);
        NatFloatEditor(id, "Value", ref particleProperties.HsvaColor[2]);
        NatFloatEditor(id, "Alpha", ref particleProperties.HsvaColor[3]);
        ImGui.TreePop();
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
    private static void EvolvingNatFloatEditorOptional(string id, string label, ref EvolvingNatFloat value, EvolvingNatFloat? defaultWhenEnabled = null)
    {
        bool enabled = value != EvolvingNatFloat.NoValueSet && value.Transform != EnumTransformFunction.UNSPECIFIED;
        ImGui.Checkbox($"{label}##{id}", ref enabled);
        if (!enabled)
        {
            value = EvolvingNatFloat.NoValueSet;
            return;
        }

        if (value == EvolvingNatFloat.NoValueSet || value.Transform == EnumTransformFunction.UNSPECIFIED)
        {
            value = defaultWhenEnabled ?? new EvolvingNatFloat(EnumTransformFunction.LINEAR, 0f);
        }

        EvolvingNatFloatEditor(id, label, ref value);
    }

    private static void EvolvingNatFloatEditor(string id, string label, ref EvolvingNatFloat value)
    {
        int currentModel = Math.Clamp((int)value.Transform, 0, _transformFunction.Length - 1);
        float currentFactor = value.Factor;
        ImGui.SetNextItemWidth(170);
        ImGui.Combo($"##combo{label}{id}", ref currentModel, _transformFunction, _transformFunction.Length, _transformFunction.Length);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        ImGui.DragFloat($"Factor##factor{label}{id}", ref currentFactor);

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

    private static readonly EnumDistribution[] _natFloatDistributions = Enum.GetValues<EnumDistribution>();
    private static readonly string[] _natFloatDistributionNames = _natFloatDistributions.Select(value => value.ToString()).ToArray();

    private static void NatFloatDistributionEditor(string label, ref EnumDistribution value)
    {
        int index = Array.IndexOf(_natFloatDistributions, value);
        if (index < 0) index = Array.IndexOf(_natFloatDistributions, EnumDistribution.UNIFORM);
        if (index < 0) index = 0;

        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo(label, ref index, _natFloatDistributionNames, _natFloatDistributionNames.Length))
        {
            value = _natFloatDistributions[Math.Clamp(index, 0, _natFloatDistributions.Length - 1)];
        }
    }

    private static void NatFloatVecEditor(string id, string name, ref NatFloat[] vector)
    {
        ImGui.Text($"{name}");
        NatFloatEditor(id, $"{name}.X", ref vector[0]);
        NatFloatEditor(id, $"{name}.Y", ref vector[1]);
        NatFloatEditor(id, $"{name}.Z", ref vector[2]);
    }
}
