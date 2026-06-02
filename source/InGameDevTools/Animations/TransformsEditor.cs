using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private readonly List<TransformAssetEntry> _transformAssets = [];
    private readonly List<TransformAssetEntry> _visibleTransformAssets = [];
    private readonly Dictionary<string, ModelTransform> _transformDrafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _transformDirtyKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _transformFamilyKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _transformFamilyDisplayKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _transformFamilyCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TransformApplicabilityResult> _transformApplicabilityCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JObject?> _transformSourceJsonCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImGuiThreePanelLayoutState _transformsLayout = new(0.24f, 0.32f);
    private DevToolsPreview3DRenderer? _transformsPreviewRenderer;
    private DevToolsPreviewMesh? _transformPreviewMesh;
    private DevToolsPreviewMesh? _transformReferenceMesh;
    private Matrixf _transformPreviewModelMatrix = CreateIdentityMatrix();
    private Matrixf _transformReferenceModelMatrix = CreateIdentityMatrix();
    private string _transformPreviewCacheKey = "";
    private string _transformPreviewPlacementStatus = "";
    private string _transformsFilter = "";
    private string _transformsDomainFilter = "";
    private int _transformsAssetIndex;
    private int _transformsTypeFilter;
    private bool _transformsDirtyOnly;
    private bool _transformsOnlyApplicable = true;
    private bool _transformsShowUncertain;
    private bool _transformGroupEdit;
    private bool _transformUseTypedSlot;
    private int _transformDirectSlotIndex;
    private int _transformTypedMapIndex;
    private string _transformTypedKey = "";
    private string _transformReferenceFilter = "";
    private string _transformReferenceBlockCode = "";
    private int _transformReferenceBlockIndex;
    private readonly Dictionary<string, string> _transformLiveAppliedHashes = new(StringComparer.OrdinalIgnoreCase);
    private float _transformPreviewYaw = -0.55f;
    private float _transformPreviewPitch = 0.35f;
    private float _transformPreviewDistance = 4.5f;
    private Vector3 _transformPreviewTarget = new(0.5f, 0.5f, 0.5f);
    private Vector3 _transformPreviewAnchor = Vector3.Zero;
    private bool _transformViewportGizmoAtAnchor;
    private TransformGizmoAxis _transformViewportGizmoDragAxis = TransformGizmoAxis.None;
    private TransformGizmoMode _transformViewportGizmoDragMode = TransformGizmoMode.None;
    private NVector2 _transformViewportGizmoDragMouseStart;
    private NVector2 _transformViewportGizmoDragVector = new(1f, 0f);
    private NVector2 _transformViewportGizmoDragCenter;
    private double _transformViewportGizmoDragLastAngleRadians;
    private double _transformViewportGizmoDragAccumulatedDegrees;
    private double _transformViewportGizmoDragRingScreenSign = -1.0;
    private float _transformViewportGizmoDragStartValue;
    private string _transformViewportGizmoDragSlotKey = "";
    private string _transformsStatus = "";
    private bool _transformsIndexed;

    private void TransformsEditorTab(float deltaSeconds)
    {
        ClearActiveTransformGizmo();
        EnsureTransformAssetsIndexed();

        NVector2 available = ImGui.GetContentRegionAvail();
        float scale = Math.Max(0.75f, _devToolsUiScale);
        float splitterThickness = Math.Max(5f, 6f * scale);
        ImGuiLayoutHelper.CalculateThreePanelWidths(
            available.X,
            splitterThickness,
            _transformsLayout,
            260f * scale,
            560f * scale,
            360f * scale,
            340f * scale,
            760f * scale,
            out float panelAvailableWidth,
            out float leftWidth,
            out float centerWidth,
            out float rightWidth);

        DrawTransformsBrowser(new NVector2(leftWidth, available.Y));
        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter("##transforms-left-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _transformsLayout.LeftFraction, 260f * scale, Math.Max(260f * scale, panelAvailableWidth - rightWidth - 360f * scale));
        ImGui.SameLine(0, 0);
        DrawTransformsViewport(new NVector2(centerWidth, available.Y));
        ImGui.SameLine(0, 0);
        ImGuiLayoutHelper.DrawVerticalSplitter("##transforms-right-splitter", available.Y, splitterThickness, panelAvailableWidth, ref _transformsLayout.RightFraction, 340f * scale, Math.Max(340f * scale, panelAvailableWidth - leftWidth - 360f * scale), invertDrag: true);
        ImGui.SameLine(0, 0);
        DrawTransformsInspector(new NVector2(rightWidth, available.Y));
    }

    private void ResetTransformsLayout()
    {
        _transformsLayout.Reset();
        _transformPreviewYaw = -0.55f;
        _transformPreviewPitch = 0.35f;
        _transformPreviewDistance = 4.5f;
        _transformPreviewTarget = new Vector3(0.5f, 0.5f, 0.5f);
        _transformPreviewAnchor = Vector3.Zero;
    }

    private void EnsureTransformAssetsIndexed()
    {
        if (_transformsIndexed) return;
        _transformAssets.Clear();
        _transformApplicabilityCache.Clear();
        _transformSourceJsonCache.Clear();
        foreach (Block block in _api.World.Blocks)
        {
            if (block?.Code == null) continue;
            _transformAssets.Add(new(block, true));
        }

        foreach (Item item in _api.World.Items)
        {
            if (item?.Code == null) continue;
            _transformAssets.Add(new(item, false));
        }

        _transformAssets.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));
        RebuildTransformFamilyIndex();
        RebuildVisibleTransformAssets();
        _transformsIndexed = true;
    }

    private void RebuildVisibleTransformAssets()
    {
        string filter = _transformsFilter.Trim();
        string attributeCode = GetSelectedTransformAttributeCode();
        TransformAssetEntry? selected = SelectedTransformAsset;
        _visibleTransformAssets.Clear();
        foreach (TransformAssetEntry entry in _transformAssets)
        {
            if (!ImGuiLayoutHelper.MatchesDomain(_transformsDomainFilter, entry.Domain)) continue;
            if (_transformsTypeFilter == 1 && !entry.IsBlock) continue;
            if (_transformsTypeFilter == 2 && entry.IsBlock) continue;
            if (_transformsDirtyOnly && !_transformDirtyKeys.Any(key => key.StartsWith(entry.Key + "|", StringComparison.OrdinalIgnoreCase))) continue;
            if (!string.IsNullOrWhiteSpace(filter) && !entry.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            TransformApplicabilityResult applicability = GetTransformApplicability(entry, attributeCode);
            if (_transformsOnlyApplicable)
            {
                if (applicability.Kind == TransformApplicabilityKind.NotApplicable) continue;
                if (!_transformsShowUncertain && applicability.Kind == TransformApplicabilityKind.Uncertain) continue;
            }
            _visibleTransformAssets.Add(entry);
        }

        if (selected != null)
        {
            int selectedIndex = _visibleTransformAssets.FindIndex(entry => entry.Key == selected.Key);
            if (selectedIndex >= 0)
            {
                _transformsAssetIndex = selectedIndex;
                return;
            }
        }

        _transformsAssetIndex = Math.Clamp(_transformsAssetIndex, 0, Math.Max(0, _visibleTransformAssets.Count - 1));
    }

    private void RebuildTransformFamilyIndex()
    {
        _transformFamilyKeys.Clear();
        _transformFamilyDisplayKeys.Clear();
        _transformFamilyCounts.Clear();

        Dictionary<string, int> candidateCounts = new(StringComparer.OrdinalIgnoreCase);
        foreach (TransformAssetEntry entry in _transformAssets)
        {
            foreach ((string key, _) in GetTransformFallbackFamilyCandidates(entry))
            {
                candidateCounts[key] = candidateCounts.TryGetValue(key, out int count) ? count + 1 : 1;
            }
        }

        foreach (TransformAssetEntry entry in _transformAssets)
        {
            string familyKey;
            string displayPath;
            if (TryGetTransformMetadataFamily(entry, out familyKey, out displayPath))
            {
                // Metadata-declared grouping is authoritative.
            }
            else
            {
                (familyKey, displayPath) = GetTransformFallbackFamilyCandidates(entry)
                    .FirstOrDefault(candidate => candidateCounts.TryGetValue(candidate.Key, out int count) && count > 1);
                if (string.IsNullOrWhiteSpace(familyKey))
                {
                    displayPath = entry.Collectible.Code?.Path ?? "unknown";
                    familyKey = BuildTransformFamilyKey(entry, displayPath);
                }
            }

            _transformFamilyKeys[entry.Key] = familyKey;
            _transformFamilyDisplayKeys[entry.Key] = $"{(entry.IsBlock ? "Block" : "Item")} | {ImGuiLayoutHelper.CompactAssetCode($"{entry.Domain}:{displayPath}")}";
            _transformFamilyCounts[familyKey] = _transformFamilyCounts.TryGetValue(familyKey, out int existing) ? existing + 1 : 1;
        }
    }

    private static bool TryGetTransformMetadataFamily(TransformAssetEntry entry, out string familyKey, out string displayPath)
    {
        familyKey = "";
        displayPath = "";
        if (entry.Collectible.Attributes?.Token is not JObject attributes ||
            attributes["handbook"] is not JObject handbook ||
            handbook["groupBy"] is not JArray groupBy)
        {
            return false;
        }

        string? pattern = groupBy.Values<string>().FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        displayPath = NormalizeTransformFamilyPattern(pattern);
        if (string.IsNullOrWhiteSpace(displayPath)) return false;

        familyKey = BuildTransformFamilyKey(entry, displayPath);
        return true;
    }

    private static IEnumerable<(string Key, string DisplayPath)> GetTransformFallbackFamilyCandidates(TransformAssetEntry entry)
    {
        string path = entry.Collectible.Code?.Path ?? "unknown";
        for (int index = path.Length - 1; index > 0; index--)
        {
            char character = path[index];
            if (character != '-' && character != '_' && character != '/') continue;

            string prefix = path[..index].TrimEnd('-', '_', '/');
            if (string.IsNullOrWhiteSpace(prefix)) continue;
            yield return (BuildTransformFamilyKey(entry, prefix), prefix);
        }
    }

    private static string BuildTransformFamilyKey(TransformAssetEntry entry, string familyPath)
    {
        return $"{(entry.IsBlock ? "block" : "item")}:{entry.Domain}:{familyPath}";
    }

    private static string NormalizeTransformFamilyPattern(string pattern)
    {
        string value = pattern.Trim();
        int colon = value.IndexOf(':');
        if (colon >= 0) value = value[(colon + 1)..];
        int star = value.IndexOf('*');
        if (star >= 0) value = value[..star];
        return value.TrimEnd('-', '/', '_');
    }

    private TransformApplicabilityResult GetTransformApplicability(TransformAssetEntry entry, string attributeCode)
    {
        string baseAttribute = GetTransformBaseAttributeCode(attributeCode);
        string cacheKey = $"{entry.Key}|{baseAttribute}";
        if (_transformApplicabilityCache.TryGetValue(cacheKey, out TransformApplicabilityResult? cached)) return cached;

        TransformApplicabilityResult result = ComputeTransformApplicability(entry, baseAttribute);
        _transformApplicabilityCache[cacheKey] = result;
        return result;
    }

    private TransformApplicabilityResult ComputeTransformApplicability(TransformAssetEntry entry, string baseAttribute)
    {
        if (IsGeneralTransformContext(baseAttribute))
        {
            return TransformApplicabilityResult.Applicable("General held/inventory transform context.");
        }

        JObject? sourceJson = GetTransformSourceJson(entry);
        TransformContextRule rule = GetTransformContextRule(baseAttribute);

        if (TryFindNegativeApplicability(entry, sourceJson, rule, out string negativeReason))
        {
            return TransformApplicabilityResult.NotApplicable(negativeReason);
        }

        if (TryFindTransformMetadataMatch(entry, sourceJson, baseAttribute, out string transformReason))
        {
            return TransformApplicabilityResult.Applicable(transformReason);
        }

        if (TryFindPositiveCapability(entry, sourceJson, rule, out string capabilityReason))
        {
            return TransformApplicabilityResult.Applicable(capabilityReason);
        }

        if (rule.CheckCombustibleProps && TryFindRuntimeInterfaceMatch(entry, ["IInFirepitMeshSupplier", "IInFirepitRendererSupplier"], out string interfaceReason))
        {
            return TransformApplicabilityResult.Applicable(interfaceReason);
        }

        if (TryFindDisplayableMatch(entry, sourceJson, rule, out string displayableReason))
        {
            return TransformApplicabilityResult.Applicable(displayableReason);
        }

        if (TryFindBehaviorMatch(entry, sourceJson, rule, out string behaviorReason))
        {
            return TransformApplicabilityResult.Applicable(behaviorReason);
        }

        return rule.UnmatchedIsUncertain
            ? TransformApplicabilityResult.Uncertain($"No metadata proves {baseAttribute}; disable Only applicable or enable Show uncertain to author it manually.")
            : TransformApplicabilityResult.NotApplicable($"No {rule.DisplayName} metadata matched.");
    }

    private JObject? GetTransformSourceJson(TransformAssetEntry entry)
    {
        if (_transformSourceJsonCache.TryGetValue(entry.Key, out JObject? cached)) return cached;

        IAsset? sourceAsset = FindCollectibleSourceAsset(entry.Collectible);
        JObject? json = sourceAsset == null ? null : TryParseJsonObject(ReadAssetText(sourceAsset));
        _transformSourceJsonCache[entry.Key] = json;
        return json;
    }

    private static bool IsGeneralTransformContext(string baseAttribute)
    {
        return baseAttribute.Equals("guiTransform", StringComparison.OrdinalIgnoreCase) ||
               baseAttribute.Equals("groundTransform", StringComparison.OrdinalIgnoreCase) ||
               baseAttribute.Equals("tpHandTransform", StringComparison.OrdinalIgnoreCase) ||
               baseAttribute.Equals("tpOffHandTransform", StringComparison.OrdinalIgnoreCase);
    }

    private static TransformContextRule GetTransformContextRule(string baseAttribute)
    {
        string code = baseAttribute.ToLowerInvariant();

        if (code.Contains("forge", StringComparison.Ordinal))
        {
            return new("forge", ["forgable", "workableTemperature"], ["forgable"], [], [], true);
        }

        if (code.Contains("tong", StringComparison.Ordinal))
        {
            return new("tongs", ["forgable", "workableTemperature"], ["forgable"], [], ["tong", "tongs"], true);
        }

        if (code.Contains("firepit", StringComparison.Ordinal))
        {
            return new("firepit", ["combustibleProps"], [], [], ["firepit"], true, CheckCombustibleProps: true);
        }

        if (code.Contains("groundstorage", StringComparison.Ordinal))
        {
            return new("ground storage", [], [], ["GroundStorable"], ["groundstorage", "groundStorage"], true);
        }

        if (code.Contains("display", StringComparison.Ordinal))
        {
            return new("display", ["displaycaseable"], ["displaycaseable"], [], ["display", "displaycase", "genericdisplay", "genericDisplay"], true);
        }

        if (code.Contains("shelf", StringComparison.Ordinal))
        {
            return new("shelf", ["shelvable"], ["shelvable"], [], ["shelf"], true);
        }

        if (code.Contains("toolrack", StringComparison.Ordinal))
        {
            return new("tool rack", ["rackable"], ["rackable"], [], ["toolrack", "rack"], true);
        }

        if (code.Contains("moldrack", StringComparison.Ordinal))
        {
            return new("mold rack", ["moldrackable"], ["moldrackable"], [], ["moldrack"], true);
        }

        if (code.Contains("scrollrack", StringComparison.Ordinal))
        {
            return new("scroll rack", ["scrollrackable"], ["scrollrackable"], [], ["scrollrack"], true);
        }

        if (code.Contains("trap", StringComparison.Ordinal))
        {
            return new("trap", [], [], [], ["trap"], true);
        }

        if (code.Contains("omok", StringComparison.Ordinal))
        {
            return new("omok", [], [], [], ["omok"], true);
        }

        if (code.Contains("weaponrack", StringComparison.Ordinal))
        {
            return new("weapon rack", ["rackable", "weaponrackable"], ["weaponrackable"], [], ["weaponrack", "rack"], true);
        }

        if (code.Contains("wallmount", StringComparison.Ordinal))
        {
            return new("wall mount", ["wallmountable"], ["wallmountable"], [], ["wallmount"], true);
        }

        if (code.Contains("pistolstand", StringComparison.Ordinal))
        {
            return new("pistol stand", ["pistolstandable"], ["pistolstandable"], [], ["pistolstand"], true);
        }

        if (code.Contains("vice", StringComparison.Ordinal))
        {
            return new("vice", ["viceable"], ["viceable"], [], ["vice"], true);
        }

        if (code.Contains("crossbowwallmount", StringComparison.Ordinal))
        {
            return new("crossbow wall mount", ["crossbowwallmountable", "wallmountable"], ["crossbowwallmountable"], [], ["crossbowwallmount", "wallmount"], true);
        }

        if (code.Contains("antlermount", StringComparison.Ordinal))
        {
            return new("antler mount", ["antlermountable", "wallmountable"], ["antlermountable"], [], ["antlermount", "wallmount"], true);
        }

        return new(baseAttribute, [], [], [], [], true);
    }

    private bool TryFindNegativeApplicability(TransformAssetEntry entry, JObject? sourceJson, TransformContextRule rule, out string reason)
    {
        foreach (string capability in rule.NegativeCapabilityKeys)
        {
            if (TryFindMetadataToken(entry, sourceJson, capability, out JToken? token, out string matchReason) &&
                IsExplicitFalse(token))
            {
                reason = $"{matchReason} is false.";
                return true;
            }
        }

        if (rule.CheckCombustibleProps &&
            TryFindMetadataToken(entry, sourceJson, "combustibleProps", out JToken? combustibleToken, out string combustibleReason) &&
            CombustibleRequiresContainer(combustibleToken))
        {
            reason = $"{combustibleReason} requires a container.";
            return true;
        }

        reason = "";
        return false;
    }

    private bool TryFindTransformMetadataMatch(TransformAssetEntry entry, JObject? sourceJson, string baseAttribute, out string reason)
    {
        foreach (JObject container in GetTransformMetadataContainers(entry, sourceJson, includeSourceRoot: false))
        {
            if (TryGetProperty(container, baseAttribute, out JToken? direct) && direct.Type != JTokenType.Null)
            {
                reason = $"Existing {baseAttribute}.";
                return true;
            }

            if (TryGetByTypeMatch(entry, container, $"{baseAttribute}ByType", out JToken? byType, out string pattern) &&
                byType.Type != JTokenType.Null)
            {
                reason = $"{baseAttribute}ByType matched {pattern}.";
                return true;
            }
        }

        reason = "";
        return false;
    }

    private bool TryFindPositiveCapability(TransformAssetEntry entry, JObject? sourceJson, TransformContextRule rule, out string reason)
    {
        foreach (string capability in rule.CapabilityKeys)
        {
            if (!TryFindMetadataToken(entry, sourceJson, capability, out JToken? token, out string matchReason)) continue;
            if (rule.CheckCombustibleProps)
            {
                if (IsCombustibleUsableInFirepit(token))
                {
                    reason = $"{matchReason} is usable without a container.";
                    return true;
                }

                continue;
            }

            if (IsCapabilityPositive(token))
            {
                reason = matchReason;
                return true;
            }
        }

        reason = "";
        return false;
    }

    private bool TryFindDisplayableMatch(TransformAssetEntry entry, JObject? sourceJson, TransformContextRule rule, out string reason)
    {
        if (rule.DisplayableKeys.Length == 0)
        {
            reason = "";
            return false;
        }

        foreach (JObject container in GetTransformMetadataContainers(entry, sourceJson, includeSourceRoot: false))
        {
            if (TryGetProperty(container, "displayable", out JToken? displayable) &&
                TryMatchDisplayableContext(displayable, rule.DisplayableKeys, out string key))
            {
                reason = $"displayable.{key}.";
                return true;
            }

            if (TryGetByTypeMatch(entry, container, "displayableByType", out JToken? byTypeDisplayable, out string pattern) &&
                TryMatchDisplayableContext(byTypeDisplayable, rule.DisplayableKeys, out key))
            {
                reason = $"displayableByType matched {pattern} / {key}.";
                return true;
            }
        }

        reason = "";
        return false;
    }

    private bool TryFindBehaviorMatch(TransformAssetEntry entry, JObject? sourceJson, TransformContextRule rule, out string reason)
    {
        if (sourceJson == null || rule.BehaviorNames.Length == 0)
        {
            reason = "";
            return false;
        }

        if (TryMatchBehaviorArray(sourceJson["behaviors"], rule.BehaviorNames, out string behavior))
        {
            reason = $"behavior {behavior}.";
            return true;
        }

        if (sourceJson["behaviorsByType"] is JObject byType)
        {
            foreach (JProperty property in byType.Properties())
            {
                if (!TransformPatternMatches(entry, property.Name)) continue;
                if (!TryMatchBehaviorArray(property.Value, rule.BehaviorNames, out behavior)) continue;
                reason = $"behaviorsByType matched {property.Name} / {behavior}.";
                return true;
            }
        }

        reason = "";
        return false;
    }

    private static bool TryFindRuntimeInterfaceMatch(TransformAssetEntry entry, string[] interfaceNames, out string reason)
    {
        foreach (Type interfaceType in entry.Collectible.GetType().GetInterfaces())
        {
            if (!interfaceNames.Any(name => interfaceType.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            reason = $"runtime interface {interfaceType.Name}.";
            return true;
        }

        reason = "";
        return false;
    }

    private bool TryFindMetadataToken(TransformAssetEntry entry, JObject? sourceJson, string metadataKey, out JToken token, out string reason)
    {
        foreach (JObject container in GetTransformMetadataContainers(entry, sourceJson, includeSourceRoot: true))
        {
            if (TryGetProperty(container, metadataKey, out JToken? direct))
            {
                token = direct;
                reason = metadataKey;
                return true;
            }

            string byTypeKey = metadataKey.EndsWith("ByType", StringComparison.OrdinalIgnoreCase)
                ? metadataKey
                : $"{metadataKey}ByType";
            if (TryGetByTypeMatch(entry, container, byTypeKey, out JToken? byTypeToken, out string pattern))
            {
                token = byTypeToken;
                reason = $"{byTypeKey} matched {pattern}";
                return true;
            }
        }

        token = JValue.CreateNull();
        reason = "";
        return false;
    }

    private IEnumerable<JObject> GetTransformMetadataContainers(TransformAssetEntry entry, JObject? sourceJson, bool includeSourceRoot)
    {
        if (entry.Collectible.Attributes?.Token is JObject runtimeAttributes) yield return runtimeAttributes;

        if (sourceJson?["attributes"] is JObject sourceAttributes) yield return sourceAttributes;
        if (sourceJson?["attributesByType"] is JObject attributesByType)
        {
            foreach (JProperty property in attributesByType.Properties())
            {
                if (TransformPatternMatches(entry, property.Name) && property.Value is JObject typedAttributes)
                {
                    yield return typedAttributes;
                }
            }
        }

        if (includeSourceRoot && sourceJson != null) yield return sourceJson;
    }

    private static bool TryGetProperty(JObject container, string key, out JToken token)
    {
        return container.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out token!);
    }

    private static bool TryGetByTypeMatch(TransformAssetEntry entry, JObject container, string byTypeKey, out JToken token, out string pattern)
    {
        if (TryGetProperty(container, byTypeKey, out JToken? mapToken) && mapToken is JObject map)
        {
            foreach (JProperty property in map.Properties())
            {
                if (!TransformPatternMatches(entry, property.Name)) continue;
                token = property.Value;
                pattern = property.Name;
                return true;
            }
        }

        token = JValue.CreateNull();
        pattern = "";
        return false;
    }

    private static bool TransformPatternMatches(TransformAssetEntry entry, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        string path = entry.Collectible.Code?.Path ?? "";
        string fullCode = entry.Collectible.Code?.ToString() ?? path;

        return PatternMatchesCode(path, pattern) || PatternMatchesCode(fullCode, pattern);
    }

    private static bool PatternMatchesCode(string code, string pattern)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        if (pattern.StartsWith('@'))
        {
            try
            {
                return Regex.IsMatch(code, pattern[1..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch
            {
                return false;
            }
        }

        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(code, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return string.Equals(code, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplicitFalse(JToken token)
    {
        return token.Type switch
        {
            JTokenType.Boolean => token.Value<bool>() == false,
            JTokenType.Integer or JTokenType.Float => Math.Abs(token.Value<double>()) < 0.000001,
            JTokenType.String => token.Value<string>() is { } text &&
                                 (text.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                                  text.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                                  text.Equals("none", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private static bool IsCapabilityPositive(JToken token)
    {
        if (token.Type == JTokenType.Null) return false;
        if (token.Type is JTokenType.Integer or JTokenType.Float) return true;
        if (IsExplicitFalse(token)) return false;
        return true;
    }

    private static bool CombustibleRequiresContainer(JToken token)
    {
        return token is JObject obj &&
               TryGetProperty(obj, "requiresContainer", out JToken? requiresContainer) &&
               requiresContainer.Type == JTokenType.Boolean &&
               requiresContainer.Value<bool>();
    }

    private static bool IsCombustibleUsableInFirepit(JToken token)
    {
        return token.Type != JTokenType.Null && !CombustibleRequiresContainer(token) && !IsExplicitFalse(token);
    }

    private static bool TryMatchDisplayableContext(JToken token, string[] contextKeys, out string matchedKey)
    {
        if (token is JObject displayable)
        {
            foreach (string contextKey in contextKeys)
            {
                foreach (JProperty property in displayable.Properties())
                {
                    if (!property.Name.Equals(contextKey, StringComparison.OrdinalIgnoreCase)) continue;
                    matchedKey = property.Name;
                    return true;
                }
            }
        }

        matchedKey = "";
        return false;
    }

    private static bool TryMatchBehaviorArray(JToken? token, string[] behaviorNames, out string behavior)
    {
        if (token is JArray behaviors)
        {
            foreach (JToken behaviorToken in behaviors)
            {
                string? name = behaviorToken is JObject obj && TryGetProperty(obj, "name", out JToken? nameToken)
                    ? nameToken.Value<string>()
                    : behaviorToken.Value<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!behaviorNames.Any(expected => expected.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                behavior = name;
                return true;
            }
        }

        behavior = "";
        return false;
    }

    private static string GetTransformBaseAttributeCode(string attributeCode)
    {
        return attributeCode.EndsWith("ByType", StringComparison.OrdinalIgnoreCase)
            ? attributeCode[..^"ByType".Length]
            : attributeCode;
    }

    private void DrawTransformsBrowser(NVector2 size)
    {
        ImGui.BeginChild("##transforms-browser", size, true);
        try
        {
            ImGui.SeparatorText("Transforms");
            if (ImGui.Button("Reload##transforms"))
            {
                _transformsIndexed = false;
                EnsureTransformAssetsIndexed();
            }

            bool useTypedSlot = _transformUseTypedSlot;
            if (ImGui.Checkbox("Typed map##transform-use-typed-global", ref useTypedSlot))
            {
                _transformUseTypedSlot = useTypedSlot;
                _transformPreviewCacheKey = "";
                RebuildVisibleTransformAssets();
            }

            if (DrawTransformContextSelector())
            {
                _transformPreviewCacheKey = "";
                ResetTransformPreviewCameraToSelection();
                RebuildVisibleTransformAssets();
            }

            string[] typeLabels = ["All", "Blocks", "Items"];
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("Type##transforms-type", ref _transformsTypeFilter, typeLabels, typeLabels.Length))
            {
                RebuildVisibleTransformAssets();
            }

            if (ImGuiLayoutHelper.DrawDomainCombo("Domain##transforms-domain", ref _transformsDomainFilter, _transformAssets.Select(entry => entry.Domain)))
            {
                RebuildVisibleTransformAssets();
            }

            if (ImGui.InputTextWithHint("##transforms-filter", "filter assets", ref _transformsFilter, 300))
            {
                RebuildVisibleTransformAssets();
            }

            if (ImGui.Checkbox("Dirty only##transforms-dirty", ref _transformsDirtyOnly))
            {
                RebuildVisibleTransformAssets();
            }

            bool onlyApplicable = _transformsOnlyApplicable;
            if (ImGui.Checkbox("Only applicable##transforms-only-applicable", ref onlyApplicable))
            {
                _transformsOnlyApplicable = onlyApplicable;
                RebuildVisibleTransformAssets();
            }

            if (_transformsOnlyApplicable)
            {
                bool showUncertain = _transformsShowUncertain;
                if (ImGui.Checkbox("Show uncertain##transforms-show-uncertain", ref showUncertain))
                {
                    _transformsShowUncertain = showUncertain;
                    RebuildVisibleTransformAssets();
                }
            }

            ImGui.TextDisabled($"Showing {_visibleTransformAssets.Count} / {_transformAssets.Count} assets");
            ImGui.BeginChild("##transforms-asset-list", new NVector2(0, 0), false);
            string attributeCode = GetSelectedTransformAttributeCode();
            for (int index = 0; index < _visibleTransformAssets.Count; index++)
            {
                TransformAssetEntry entry = _visibleTransformAssets[index];
                TransformApplicabilityResult applicability = GetTransformApplicability(entry, attributeCode);
                string marker = applicability.Kind switch
                {
                    TransformApplicabilityKind.Applicable => "",
                    TransformApplicabilityKind.Uncertain => " ?",
                    _ => " !"
                };
                if (ImGui.Selectable($"{entry.Label}{marker}##transform-asset-{entry.Key}", index == _transformsAssetIndex))
                {
                    _transformsAssetIndex = index;
                    ResetTransformPreviewCameraToSelection();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{applicability.Kind}: {applicability.Reason}");
                }
            }
            ImGui.EndChild();
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private bool DrawTransformContextSelector()
    {
        bool changed = false;
        if (!_transformUseTypedSlot)
        {
            _transformDirectSlotIndex = Math.Clamp(_transformDirectSlotIndex, 0, DirectTransformAttributeCodes.Length - 1);
            ImGui.SetNextItemWidth(-1);
            changed |= ImGui.Combo("Context##transform-direct-slot-global", ref _transformDirectSlotIndex, DirectTransformAttributeCodes, DirectTransformAttributeCodes.Length);
            return changed;
        }

        _transformTypedMapIndex = Math.Clamp(_transformTypedMapIndex, 0, TypedTransformAttributeCodes.Length - 1);
        ImGui.SetNextItemWidth(-1);
        changed |= ImGui.Combo("Context map##transform-typed-map-global", ref _transformTypedMapIndex, TypedTransformAttributeCodes, TypedTransformAttributeCodes.Length);
        ImGui.SetNextItemWidth(-1);
        changed |= ImGui.InputTextWithHint("Key##transform-typed-key-global", "type key", ref _transformTypedKey, 120);
        return changed;
    }

    private void DrawTransformsViewport(NVector2 size)
    {
        ImGui.BeginChild("##transforms-viewport-panel", size, true);
        try
        {
            TransformAssetEntry? asset = SelectedTransformAsset;
            TransformSlotSelection? slot = GetSelectedTransformSlot(asset);
            if (asset == null || slot == null)
            {
                ImGui.TextDisabled("Select a block or item.");
                return;
            }

            ModelTransform transform = GetTransformDraft(asset, slot);
            DrawTransformPreviewSurface(asset, slot, transform);
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawTransformPreviewSurface(TransformAssetEntry asset, TransformSlotSelection slot, ModelTransform transform)
    {
        NVector2 available = ImGui.GetContentRegionAvail();
        NVector2 size = new(Math.Max(320f, available.X), Math.Max(280f, available.Y));
        ImGui.InvisibleButton($"##transform-preview-{asset.Key}-{slot.Key}", size);
        NVector2 min = ImGui.GetItemRectMin();
        NVector2 max = ImGui.GetItemRectMax();
        bool hovered = ImGui.IsItemHovered();

        if (hovered)
        {
            NVector2 delta = ImGui.GetIO().MouseDelta;
            bool pan = ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
                (ImGui.IsMouseDragging(ImGuiMouseButton.Right) && (ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift)));
            if (pan)
            {
                DevToolsPreviewCamera panCamera = BuildTransformPreviewCamera(min, max);
                float panScale = _transformPreviewDistance / Math.Max(120f, size.Y);
                _transformPreviewTarget -= panCamera.Right * delta.X * panScale;
                _transformPreviewTarget += panCamera.Up * delta.Y * panScale;
            }
            else if (ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                _transformPreviewYaw += delta.X * 0.01f;
                _transformPreviewPitch = Math.Clamp(_transformPreviewPitch + delta.Y * 0.01f, -1.45f, 1.45f);
            }

            float wheel = ImGui.GetIO().MouseWheel;
            if (Math.Abs(wheel) > 0.001f)
            {
                _transformPreviewDistance = Math.Clamp(_transformPreviewDistance * MathF.Pow(0.88f, wheel), 0.35f, 48f);
            }
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        uint background = ImGui.ColorConvertFloat4ToU32(new NVector4(0.035f, 0.036f, 0.032f, 1f));
        uint border = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.49f, 0.38f, 1f));
        uint grid = ImGui.ColorConvertFloat4ToU32(new NVector4(0.28f, 0.27f, 0.22f, 0.42f));
        uint gridMajor = ImGui.ColorConvertFloat4ToU32(new NVector4(0.45f, 0.42f, 0.33f, 0.72f));
        uint text = ImGui.ColorConvertFloat4ToU32(new NVector4(0.86f, 0.82f, 0.72f, 1f));
        drawList.AddRectFilled(min, max, background, 4f);

        BuildTransformPreviewMeshes(asset, slot, transform);
        List<DevToolsPreviewMeshInstance> instances = [];
        if (_transformReferenceMesh != null) instances.Add(new(_transformReferenceMesh, _transformReferenceModelMatrix));
        if (_transformPreviewMesh != null) instances.Add(new(_transformPreviewMesh, _transformPreviewModelMatrix));

        DevToolsPreviewCamera camera = BuildTransformPreviewCamera(min, max);
        int textureId = EnsureTransformsPreviewRenderer().RenderToTexture(max.X - min.X, max.Y - min.Y, camera, instances, out string? skipReason);
        if (textureId > 0)
        {
            drawList.AddImage(new IntPtr(textureId), min, max, new NVector2(0f, 1f), new NVector2(1f, 0f));
        }
        else if (!string.IsNullOrWhiteSpace(skipReason))
        {
            drawList.AddText(min + new NVector2(12f, 54f), text, $"Preview skipped: {skipReason}");
        }

        drawList.PushClipRect(min, max, true);
        DrawTransformPreviewGrid(drawList, camera, GetTransformPreviewGridExtent(), grid, gridMajor);
        if (!DrawTransformViewportGizmo(asset, slot, transform, drawList, camera, min, max, hovered))
        {
            DrawTransformPreviewAxes(drawList, camera);
        }
        drawList.PopClipRect();
        drawList.AddRect(min, max, border, 4f);
        drawList.AddText(min + new NVector2(12f, 10f), text, $"{asset.Label} / {slot.DisplayName}");
        drawList.AddText(min + new NVector2(12f, 30f), text, "RMB orbits. MMB or Shift+RMB pans. Mouse wheel zooms.");
        if (!string.IsNullOrWhiteSpace(_transformPreviewPlacementStatus))
        {
            drawList.AddText(min + new NVector2(12f, 50f), text, _transformPreviewPlacementStatus);
        }
    }

    private void DrawTransformsInspector(NVector2 size)
    {
        ImGui.BeginChild("##transforms-inspector", size, true, ImGuiWindowFlags.HorizontalScrollbar);
        try
        {
            TransformAssetEntry? asset = SelectedTransformAsset;
            if (asset == null)
            {
                ImGui.TextDisabled("Select a transform asset.");
                return;
            }

            ImGui.SeparatorText("Slot");
            ImGui.TextWrapped(asset.Collectible.Code.ToString());
            TransformSlotSelection? slot = GetSelectedTransformSlot(asset);
            if (slot == null)
            {
                ImGui.TextDisabled("Enter a typed transform key in the browser.");
                return;
            }
            ImGui.TextDisabled(slot.DisplayName);
            TransformApplicabilityResult applicability = GetTransformApplicability(asset, slot.AttributeCode);
            ImGui.TextWrapped($"{applicability.Kind}: {applicability.Reason}");

            ModelTransform transform = GetTransformDraft(asset, slot);
            bool exists = TransformSlotExists(asset, slot);
            ImGui.TextDisabled(exists ? "Existing transform" : "Missing transform draft");

            if (!exists && ImGui.Button("Create slot##transform-create-slot"))
            {
                ApplyTransformDraftEdit(asset, slot, transform);
            }

            DrawTransformReferenceSelector(slot);
            DrawTransformScopeControls(asset, slot);
            DrawTransformLiveControls(asset, slot, transform);

            ImGui.SeparatorText("Values");
            bool changed = false;
            float uniformScale = transform.ScaleXYZ.X;
            ImGui.SetNextItemWidth(120);
            if (ImGui.DragFloat("Uniform scale##transform-uniform-scale", ref uniformScale, 0.01f, 0.001f, 100f))
            {
                transform.Scale = Math.Max(0.001f, uniformScale);
                changed = true;
            }

            System.Numerics.Vector3 translation = new(transform.Translation.X, transform.Translation.Y, transform.Translation.Z);
            System.Numerics.Vector3 origin = new(transform.Origin.X, transform.Origin.Y, transform.Origin.Z);
            System.Numerics.Vector3 rotation = new(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z);
            System.Numerics.Vector3 scale = new(transform.ScaleXYZ.X, transform.ScaleXYZ.Y, transform.ScaleXYZ.Z);
            changed |= ImGui.DragFloat3("Translation##transform-values", ref translation, 0.01f);
            changed |= ImGui.DragFloat3("Rotation##transform-values", ref rotation, 0.25f);
            changed |= ImGui.DragFloat3("Origin##transform-values", ref origin, 0.01f);
            changed |= ImGui.DragFloat3("Scale XYZ##transform-values", ref scale, 0.01f, 0.001f, 100f);

            bool rotate = transform.Rotate;
            if (ImGui.Checkbox("Rotate in GUI##transform-values", ref rotate))
            {
                transform.Rotate = rotate;
                changed = true;
            }

            if (changed)
            {
                transform.Translation.Set(translation.X, translation.Y, translation.Z);
                transform.Rotation.Set(rotation.X, rotation.Y, rotation.Z);
                transform.Origin.Set(origin.X, origin.Y, origin.Z);
                transform.ScaleXYZ.Set(Math.Max(0.001f, scale.X), Math.Max(0.001f, scale.Y), Math.Max(0.001f, scale.Z));
                ApplyTransformDraftEdit(asset, slot, transform);
            }

            DrawTransformGizmoControls(
                "transforms-tab",
                transform,
                GetGizmoContextForTransformCode(slot.AttributeCode),
                value =>
                {
                    ApplyTransformDraftEdit(asset, slot, value);
                },
                registerActive: false);

            if (ImGui.Button("Reset default##transform-reset"))
            {
                ResetTransformDraftToDefault(asset, slot);
            }
            ImGui.SameLine();
            if (ImGui.Button("Copy JSON##transform-copy"))
            {
                ImGui.SetClipboardText(JsonUtil.ToPrettyString(transform));
            }

            if (slot.CanSaveToSource)
            {
                ImGui.SameLine();
                string saveLabel = _transformGroupEdit && GetTransformFamilyCount(asset) > 1
                    ? "Save group authored files##transform-source-save"
                    : "Save authored file##transform-source-save";
                if (ImGui.Button(saveLabel))
                {
                    SourceSaveResult result = TrySaveSelectedTransformToSource(asset, slot);
                    if (result.Request != null)
                    {
                        QueueSourceSave(result, status => _transformsStatus = status);
                    }
                    else
                    {
                        _transformsStatus = result.Status;
                    }
                }
            }

            ImGui.SeparatorText("Raw JSON");
            string raw = JsonUtil.ToPrettyString(transform);
            ImGui.InputTextMultiline("##transform-raw", ref raw, (uint)Math.Max(raw.Length + 1, 1024), new NVector2(-1, 150), ImGuiInputTextFlags.ReadOnly);

            if (!string.IsNullOrWhiteSpace(_transformsStatus))
            {
                ImGui.SeparatorText("Status");
                ImGui.TextWrapped(_transformsStatus);
            }
            _transformDiagnostics.Draw("transforms-inspector", _showEditorDiagnostics);
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private TransformSlotSelection? DrawTransformSlotSelector(TransformAssetEntry asset)
    {
        if (!_transformUseTypedSlot)
        {
            _transformDirectSlotIndex = Math.Clamp(_transformDirectSlotIndex, 0, DirectTransformAttributeCodes.Length - 1);
            ImGui.SetNextItemWidth(-1);
            ImGui.Combo("Transform##transform-direct-slot", ref _transformDirectSlotIndex, DirectTransformAttributeCodes, DirectTransformAttributeCodes.Length);
            string attributeCode = DirectTransformAttributeCodes[_transformDirectSlotIndex];
            return new(asset, attributeCode, null);
        }

        _transformTypedMapIndex = Math.Clamp(_transformTypedMapIndex, 0, TypedTransformAttributeCodes.Length - 1);
        ImGui.SetNextItemWidth(-1);
        ImGui.Combo("Transform map##transform-typed-map", ref _transformTypedMapIndex, TypedTransformAttributeCodes, TypedTransformAttributeCodes.Length);
        string attribute = TypedTransformAttributeCodes[_transformTypedMapIndex];
        string[] keys = GetTypedTransformKeys(asset, attribute).ToArray();
        if (keys.Length > 0)
        {
            int keyIndex = Math.Max(0, Array.IndexOf(keys, _transformTypedKey));
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("Existing key##transform-typed-key-combo", ref keyIndex, keys, keys.Length))
            {
                _transformTypedKey = keys[keyIndex];
            }
        }

        ImGui.InputTextWithHint("Key##transform-typed-key", "type key", ref _transformTypedKey, 120);
        if (string.IsNullOrWhiteSpace(_transformTypedKey))
        {
            ImGui.TextDisabled("Enter a typed transform key.");
            return null;
        }

        return new(asset, attribute, _transformTypedKey.Trim());
    }

    private void DrawTransformReferenceSelector(TransformSlotSelection slot)
    {
        ImGui.SeparatorText("Reference");
        TransformReferenceResolution resolution = ResolveTransformReference(slot.Asset, slot);
        ImGui.TextDisabled(resolution.Block == null ? "No reference block" : $"Reference: {resolution.Code}");
        if (!string.IsNullOrWhiteSpace(resolution.Reason))
        {
            ImGui.TextWrapped(resolution.Reason);
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##transform-reference-filter", "reference block filter", ref _transformReferenceFilter, 120);
        List<string> options = BuildReferenceBlockOptions(_transformReferenceFilter).Take(250).ToList();
        options.Insert(0, "");
        _transformReferenceBlockIndex = Math.Clamp(_transformReferenceBlockIndex, 0, options.Count - 1);
        if (ImGui.Combo("Override##transform-reference", ref _transformReferenceBlockIndex, options.Select(option => string.IsNullOrWhiteSpace(option) ? "<default>" : option).ToArray(), options.Count))
        {
            _transformReferenceBlockCode = options[_transformReferenceBlockIndex];
            _transformPreviewCacheKey = "";
        }
    }

    private void DrawTransformScopeControls(TransformAssetEntry asset, TransformSlotSelection slot)
    {
        ImGui.SeparatorText("Edit scope");
        int familyCount = GetTransformFamilyCount(asset);
        string familyDisplay = GetTransformFamilyDisplayKey(asset);
        ImGui.TextWrapped(familyDisplay);
        ImGui.TextDisabled($"{familyCount} compatible asset(s) for {slot.DisplayName}");

        bool groupEdit = _transformGroupEdit;
        if (familyCount <= 1) ImGui.BeginDisabled();
        if (ImGui.Checkbox("Group edit##transform-group-edit", ref groupEdit))
        {
            _transformGroupEdit = groupEdit;
            _transformsStatus = _transformGroupEdit
                ? $"Group edit enabled for {familyDisplay}."
                : "Transform edits apply only to the selected asset.";
        }
        if (familyCount <= 1) ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Groups use attributes.handbook.groupBy when present; otherwise they use the longest shared code prefix with matching assets.");
        }

        ImGui.TextDisabled(_transformGroupEdit && familyCount > 1
            ? $"Editing all {familyCount} family members."
            : "Editing selected asset only.");
    }

    private void DrawTransformLiveControls(TransformAssetEntry asset, TransformSlotSelection slot, ModelTransform transform)
    {
        string liveKey = $"transform:{asset.Key}";
        _liveApplyManager.DrawRuntimeStatus(
            $"transform-live-{slot.Key}",
            liveKey,
            asset.Label,
            true,
            () =>
            {
                ClearTransformAppliedHashesForAsset(asset);
                return _liveApplyManager.Revert(liveKey);
            });
    }

    private string ApplyTransformLive(TransformAssetEntry asset, TransformSlotSelection slot, ModelTransform transform, bool force = false)
    {
        string liveKey = $"transform:{asset.Key}";
        string hashKey = $"transform:{slot.Key}";
        string hash = JsonUtil.ToString(transform);
        if (!force &&
            _transformLiveAppliedHashes.TryGetValue(hashKey, out string? appliedHash) &&
            string.Equals(appliedHash, hash, StringComparison.Ordinal))
        {
            return _liveApplyManager.LastStatus;
        }

        string status = _liveApplyManager.Apply(
            liveKey,
            asset.Label,
            () => CaptureTransformLiveSnapshot(asset),
            () =>
            {
                if (slot.TypedKey == null) ApplyDirectTransformAttribute(asset.Collectible, slot.AttributeCode, transform);
                else ApplyTypedTransformAttribute(asset.Collectible, slot.AttributeCode, slot.TypedKey, transform);
            },
            $"Live applied {slot.DisplayName} for {asset.Label}.");
        _transformLiveAppliedHashes[hashKey] = hash;
        return status;
    }

    private void ApplySelectedTransformLive(bool force = false)
    {
        TransformAssetEntry? asset = SelectedTransformAsset;
        TransformSlotSelection? slot = GetSelectedTransformSlot(asset);
        if (asset == null || slot == null)
        {
            _liveApplyManager.LastStatus = "No selected transform to apply.";
            return;
        }

        List<(TransformAssetEntry Asset, TransformSlotSelection Slot)> targets = GetTransformEditTargets(asset, slot, out int skipped);
        foreach ((TransformAssetEntry targetAsset, TransformSlotSelection targetSlot) in targets)
        {
            ApplyTransformLive(targetAsset, targetSlot, GetTransformDraft(targetAsset, targetSlot), force);
        }

        if (targets.Count > 1 || skipped > 0)
        {
            _liveApplyManager.LastStatus = $"Live applied {slot.DisplayName} to {targets.Count} {GetTransformFamilyDisplayKey(asset)} asset(s){FormatTransformSkippedSuffix(skipped)}.";
        }
    }

    private void ClearTransformLiveApplyState()
    {
        _transformLiveAppliedHashes.Clear();
    }

    private void ClearTransformAppliedHashesForAsset(TransformAssetEntry asset)
    {
        string prefix = $"transform:{asset.Key}|";
        foreach (string key in _transformLiveAppliedHashes.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _transformLiveAppliedHashes.Remove(key);
        }
    }

    private LivePatchSnapshot CaptureTransformLiveSnapshot(TransformAssetEntry asset)
    {
        JToken? original = asset.Collectible.Attributes?.Token?.DeepClone();
        return new(
            () => asset.Collectible.Attributes = original == null ? null : new JsonObject(original.DeepClone()),
            Path.Combine("assets", asset.Domain, "runtime-transforms", asset.Collectible.Code.Path.Replace('/', '_') + ".json"),
            () => (original ?? new JObject()).ToString(Newtonsoft.Json.Formatting.Indented),
            "transforms");
    }

    private void BuildTransformPreviewMeshes(TransformAssetEntry asset, TransformSlotSelection slot, ModelTransform transform)
    {
        TransformReferenceResolution referenceResolution = ResolveTransformReference(asset, slot);
        string cacheKey = $"{slot.Key}|{referenceResolution.Code}|{referenceResolution.Reason}|{JsonUtil.ToString(transform)}";
        if (cacheKey == _transformPreviewCacheKey) return;

        _transformPreviewCacheKey = cacheKey;
        _transformPreviewMesh?.Dispose();
        _transformReferenceMesh?.Dispose();
        _transformPreviewMesh = null;
        _transformReferenceMesh = null;
        _transformPreviewModelMatrix = CreateIdentityMatrix();
        _transformReferenceModelMatrix = CreateIdentityMatrix();
        _transformPreviewAnchor = Vector3.Zero;
        _transformViewportGizmoAtAnchor = false;
        _transformPreviewPlacementStatus = "";

        try
        {
            TransformGizmoContext context = GetGizmoContextForTransformCode(slot.AttributeCode);
            Block? reference = referenceResolution.Block;

            if (reference != null)
            {
                _api.Tesselator.TesselateBlock(reference, out MeshData referenceMesh);
                _transformReferenceMesh = DevToolsPreviewMeshFactory.FromMesh(_api, $"ref:{reference.Code}", referenceMesh);
            }

            MeshData mesh;
            if (asset.Collectible is Block block)
            {
                _api.Tesselator.TesselateBlock(block, out mesh);
            }
            else if (asset.Collectible is Item item)
            {
                _api.Tesselator.TesselateItem(item, out mesh);
            }
            else
            {
                return;
            }

            mesh.ModelTransform(transform);
            TransformPreviewPlacement placement = BuildTransformPreviewPlacement(slot.AttributeCode, reference, context, _transformReferenceMesh?.Bounds, mesh);
            _transformPreviewModelMatrix = placement.ItemMatrix;
            _transformPreviewAnchor = placement.Anchor;
            _transformViewportGizmoAtAnchor = placement.GizmoAtAnchor;
            _transformPreviewPlacementStatus = string.IsNullOrWhiteSpace(referenceResolution.Reason)
                ? placement.Status
                : $"{referenceResolution.Reason}; {placement.Status}";

            _transformPreviewMesh = DevToolsPreviewMeshFactory.FromMesh(_api, asset.Label, mesh);

            ResetTransformPreviewCameraToBounds();
        }
        catch (Exception exception)
        {
            _transformsStatus = $"Transform preview failed: {exception.Message}";
            _transformDiagnostics.Exception("Transform preview failed", exception);
        }
    }

    private DevToolsPreviewCamera BuildTransformPreviewCamera(NVector2 min, NVector2 max)
    {
        return DevToolsPreviewCamera.Orbit(min, max, _transformPreviewTarget, _transformPreviewYaw, _transformPreviewPitch, _transformPreviewDistance);
    }

    private void DrawTransformPreviewAxes(ImDrawListPtr drawList, DevToolsPreviewCamera camera)
    {
        uint axisX = ImGui.ColorConvertFloat4ToU32(new NVector4(0.85f, 0.25f, 0.16f, 0.9f));
        uint axisY = ImGui.ColorConvertFloat4ToU32(new NVector4(0.32f, 0.9f, 0.34f, 0.9f));
        uint axisZ = ImGui.ColorConvertFloat4ToU32(new NVector4(0.25f, 0.42f, 0.95f, 0.9f));
        DrawTransformPreviewLine(drawList, camera, _transformPreviewAnchor, _transformPreviewAnchor + new Vector3(1.5f, 0, 0), axisX, 2f);
        DrawTransformPreviewLine(drawList, camera, _transformPreviewAnchor, _transformPreviewAnchor + new Vector3(0, 1.5f, 0), axisY, 2f);
        DrawTransformPreviewLine(drawList, camera, _transformPreviewAnchor, _transformPreviewAnchor + new Vector3(0, 0, 1.5f), axisZ, 2f);
    }

    private int GetTransformPreviewGridExtent()
    {
        DevToolsPreviewBounds bounds = DevToolsPreviewBounds.Empty;
        if (_transformReferenceMesh != null) bounds = bounds.Include(_transformReferenceMesh.Bounds);
        if (_transformPreviewMesh != null) bounds = bounds.Include(_transformPreviewMesh.Bounds);
        if (bounds.IsValid) bounds = bounds.Include(_transformPreviewAnchor).Include(Vector3.Zero);
        if (!bounds.IsValid) return 4;

        float coordinateExtent = Math.Max(
            Math.Max(Math.Max(Math.Abs(bounds.Min.X), Math.Abs(bounds.Max.X)), Math.Max(Math.Abs(bounds.Min.Y), Math.Abs(bounds.Max.Y))),
            Math.Max(Math.Abs(bounds.Min.Z), Math.Abs(bounds.Max.Z)));
        return Math.Clamp((int)Math.Ceiling(Math.Max(bounds.Radius * 1.5f, coordinateExtent + 1f)), 4, 16);
    }

    private bool DrawTransformViewportGizmo(TransformAssetEntry asset, TransformSlotSelection slot, ModelTransform transform, ImDrawListPtr drawList, DevToolsPreviewCamera camera, NVector2 min, NVector2 max, bool hovered)
    {
        if (GizmoMode == TransformGizmoMode.None)
        {
            ClearTransformViewportGizmoDrag();
            return false;
        }

        Vector3 center = GetTransformViewportGizmoCenter();
        float axisLength = GetTransformViewportGizmoAxisLength();
        GetTransformViewportGizmoAxes(transform, out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ);
        if (!camera.Project(center, out NVector2 centerScreen, out _)) return false;

        NVector2 xScreen = ProjectTransformViewportAxis(camera, center, axisX, axisLength);
        NVector2 yScreen = ProjectTransformViewportAxis(camera, center, axisY, axisLength);
        NVector2 zScreen = ProjectTransformViewportAxis(camera, center, axisZ, axisLength);
        TransformGizmoAxis hoveredAxis = hovered ? PickTransformViewportGizmoAxis(camera, center, axisX, axisY, axisZ, axisLength) : TransformGizmoAxis.None;
        if (hoveredAxis != TransformGizmoAxis.None) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (hovered && hoveredAxis != TransformGizmoAxis.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _transformViewportGizmoDragAxis = hoveredAxis;
            _transformViewportGizmoDragMode = GizmoMode;
            _transformViewportGizmoDragMouseStart = ImGui.GetMousePos();
            _transformViewportGizmoDragVector = GetTransformViewportGizmoDragVector(camera, center, hoveredAxis, axisX, axisY, axisZ, axisLength, centerScreen);
            _transformViewportGizmoDragCenter = centerScreen;
            _transformViewportGizmoDragLastAngleRadians = GetTransformViewportMouseAngle(centerScreen, _transformViewportGizmoDragMouseStart);
            _transformViewportGizmoDragAccumulatedDegrees = 0;
            _transformViewportGizmoDragRingScreenSign = GizmoMode == TransformGizmoMode.Rotate
                ? GetTransformViewportRingScreenSign(camera, center, hoveredAxis, axisX, axisY, axisZ, axisLength)
                : -1.0;
            _transformViewportGizmoDragStartValue = GetTransformGizmoAxisValue(transform, GizmoMode, hoveredAxis);
            _transformViewportGizmoDragSlotKey = slot.Key;
        }

        if (_transformViewportGizmoDragAxis != TransformGizmoAxis.None)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || !string.Equals(_transformViewportGizmoDragSlotKey, slot.Key, StringComparison.OrdinalIgnoreCase))
            {
                ClearTransformViewportGizmoDrag();
            }
            else
            {
                ApplyTransformViewportGizmoDrag(asset, slot, transform, camera);
                hoveredAxis = _transformViewportGizmoDragAxis;
            }
        }

        uint red = ImGui.ColorConvertFloat4ToU32(new NVector4(0.85f, 0.25f, 0.16f, 0.95f));
        uint green = ImGui.ColorConvertFloat4ToU32(new NVector4(0.32f, 0.9f, 0.34f, 0.95f));
        uint blue = ImGui.ColorConvertFloat4ToU32(new NVector4(0.25f, 0.42f, 0.95f, 0.95f));
        uint white = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.96f, 0.78f, 1f));
        uint xColor = hoveredAxis == TransformGizmoAxis.X ? white : red;
        uint yColor = hoveredAxis == TransformGizmoAxis.Y ? white : green;
        uint zColor = hoveredAxis == TransformGizmoAxis.Z ? white : blue;
        drawList.AddCircleFilled(centerScreen, 4.5f, white, 16);

        if (GizmoMode == TransformGizmoMode.Rotate)
        {
            DrawTransformViewportGizmoRing(drawList, camera, center, axisY, axisZ, axisLength, xColor);
            DrawTransformViewportGizmoRing(drawList, camera, center, axisX, axisZ, axisLength, yColor);
            DrawTransformViewportGizmoRing(drawList, camera, center, axisX, axisY, axisLength, zColor);
            return true;
        }

        DrawTransformViewportGizmoAxis(drawList, centerScreen, xScreen, xColor);
        DrawTransformViewportGizmoAxis(drawList, centerScreen, yScreen, yColor);
        DrawTransformViewportGizmoAxis(drawList, centerScreen, zScreen, zColor);
        if (GizmoMode == TransformGizmoMode.Scale)
        {
            DrawTransformViewportGizmoCube(drawList, centerScreen + xScreen, xColor);
            DrawTransformViewportGizmoCube(drawList, centerScreen + yScreen, yColor);
            DrawTransformViewportGizmoCube(drawList, centerScreen + zScreen, zColor);
        }
        else
        {
            DrawTransformViewportGizmoArrow(drawList, centerScreen, xScreen, xColor);
            DrawTransformViewportGizmoArrow(drawList, centerScreen, yScreen, yColor);
            DrawTransformViewportGizmoArrow(drawList, centerScreen, zScreen, zColor);
        }

        return true;
    }

    private Vector3 GetTransformViewportGizmoCenter()
    {
        if (_transformViewportGizmoAtAnchor) return _transformPreviewAnchor;
        return _transformPreviewMesh?.Bounds.Center ?? _transformPreviewAnchor;
    }

    private float GetTransformViewportGizmoAxisLength()
    {
        DevToolsPreviewBounds bounds = _transformPreviewMesh?.Bounds ?? _transformReferenceMesh?.Bounds ?? DevToolsPreviewBounds.Empty;
        return bounds.IsValid ? Math.Clamp(bounds.Radius * 0.75f, 0.25f, 1.2f) : 0.7f;
    }

    private void GetTransformViewportGizmoAxes(ModelTransform transform, out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ)
    {
        axisX = Vector3.UnitX;
        axisY = Vector3.UnitY;
        axisZ = Vector3.UnitZ;
        if (GizmoSpace == TransformGizmoSpace.World) return;

        Matrixf rotation = new();
        rotation.Identity();
        rotation.Rotate(transform.Rotation.X * GameMath.DEG2RAD, transform.Rotation.Y * GameMath.DEG2RAD, transform.Rotation.Z * GameMath.DEG2RAD);
        axisX = NormalizeOrDefault(TransformDirection(rotation, Vector3.UnitX), Vector3.UnitX);
        axisY = NormalizeOrDefault(TransformDirection(rotation, Vector3.UnitY), Vector3.UnitY);
        axisZ = NormalizeOrDefault(TransformDirection(rotation, Vector3.UnitZ), Vector3.UnitZ);
    }

    private static Vector3 TransformDirection(Matrixf matrix, Vector3 direction)
    {
        Vec4f transformed = matrix.TransformVector(new Vec4f(direction.X, direction.Y, direction.Z, 0f));
        return new Vector3(transformed.X, transformed.Y, transformed.Z);
    }

    private static Vector3 NormalizeOrDefault(Vector3 value, Vector3 fallback)
    {
        return value.LengthSquared < 0.000001f ? fallback : Vector3.Normalize(value);
    }

    private static NVector2 ProjectTransformViewportAxis(DevToolsPreviewCamera camera, Vector3 center, Vector3 axis, float axisLength)
    {
        return camera.Project(center + axis * axisLength, out NVector2 end, out _) &&
               camera.Project(center, out NVector2 start, out _)
            ? end - start
            : new NVector2(1f, 0f);
    }

    private TransformGizmoAxis PickTransformViewportGizmoAxis(DevToolsPreviewCamera camera, Vector3 center, Vector3 axisX, Vector3 axisY, Vector3 axisZ, float axisLength)
    {
        NVector2 mouse = ImGui.GetMousePos();
        if (!camera.Project(center, out NVector2 centerScreen, out _)) return TransformGizmoAxis.None;
        if (GizmoMode == TransformGizmoMode.Rotate)
        {
            float dx = DistanceToTransformViewportRing(camera, center, axisY, axisZ, axisLength, mouse);
            float dy = DistanceToTransformViewportRing(camera, center, axisX, axisZ, axisLength, mouse);
            float dz = DistanceToTransformViewportRing(camera, center, axisX, axisY, axisLength, mouse);
            float min = Math.Min(dx, Math.Min(dy, dz));
            if (min > 14f) return TransformGizmoAxis.None;
            if (min == dx) return TransformGizmoAxis.X;
            if (min == dy) return TransformGizmoAxis.Y;
            return TransformGizmoAxis.Z;
        }

        TransformGizmoAxis picked = TransformGizmoAxis.None;
        float best = 14f;
        Test(TransformGizmoAxis.X, axisX);
        Test(TransformGizmoAxis.Y, axisY);
        Test(TransformGizmoAxis.Z, axisZ);
        return picked;

        void Test(TransformGizmoAxis axis, Vector3 direction)
        {
            NVector2 screenAxis = ProjectTransformViewportAxis(camera, center, direction, axisLength);
            float distance = DistancePointToTransformViewportSegment(mouse, centerScreen, centerScreen + screenAxis);
            if (distance >= best) return;
            best = distance;
            picked = axis;
        }
    }

    private static float DistanceToTransformViewportRing(DevToolsPreviewCamera camera, Vector3 center, Vector3 axisA, Vector3 axisB, float radius, NVector2 mouse)
    {
        const int segments = 72;
        if (!camera.Project(center + axisA * radius, out NVector2 previous, out _)) return float.MaxValue;
        float best = float.MaxValue;
        for (int i = 1; i <= segments; i++)
        {
            float angle = (float)(i / (double)segments * Math.PI * 2.0);
            Vector3 point = center + axisA * ((float)Math.Cos(angle) * radius) + axisB * ((float)Math.Sin(angle) * radius);
            if (!camera.Project(point, out NVector2 projected, out _)) continue;
            best = Math.Min(best, DistancePointToTransformViewportSegment(mouse, previous, projected));
            previous = projected;
        }

        return best;
    }

    private NVector2 GetTransformViewportGizmoDragVector(DevToolsPreviewCamera camera, Vector3 center, TransformGizmoAxis axis, Vector3 axisX, Vector3 axisY, Vector3 axisZ, float axisLength, NVector2 centerScreen)
    {
        if (GizmoMode == TransformGizmoMode.Rotate)
        {
            NVector2 radial = ImGui.GetMousePos() - centerScreen;
            NVector2 tangent = new(-radial.Y, radial.X);
            return NormalizeTransformScreenVector(tangent, GetProjectedTransformViewportAxis(camera, center, axis, axisX, axisY, axisZ, axisLength));
        }

        return NormalizeTransformScreenVector(GetProjectedTransformViewportAxis(camera, center, axis, axisX, axisY, axisZ, axisLength), new NVector2(1f, 0f));
    }

    private static NVector2 GetProjectedTransformViewportAxis(DevToolsPreviewCamera camera, Vector3 center, TransformGizmoAxis axis, Vector3 axisX, Vector3 axisY, Vector3 axisZ, float axisLength)
    {
        return axis switch
        {
            TransformGizmoAxis.X => ProjectTransformViewportAxis(camera, center, axisX, axisLength),
            TransformGizmoAxis.Y => ProjectTransformViewportAxis(camera, center, axisY, axisLength),
            TransformGizmoAxis.Z => ProjectTransformViewportAxis(camera, center, axisZ, axisLength),
            _ => ProjectTransformViewportAxis(camera, center, axisX, axisLength)
        };
    }

    private static NVector2 NormalizeTransformScreenVector(NVector2 value, NVector2 fallback)
    {
        float length = value.Length();
        return length < 0.001f || !float.IsFinite(length) ? fallback : value / length;
    }

    private static double GetTransformViewportMouseAngle(NVector2 center, NVector2 mouse)
    {
        NVector2 radial = mouse - center;
        return Math.Atan2(radial.Y, radial.X);
    }

    private double UpdateTransformViewportGizmoRingDrag()
    {
        NVector2 radial = ImGui.GetMousePos() - _transformViewportGizmoDragCenter;
        if (radial.LengthSquared() < 16f) return _transformViewportGizmoDragAccumulatedDegrees;

        double angle = GetTransformViewportMouseAngle(_transformViewportGizmoDragCenter, ImGui.GetMousePos());
        double delta = NormalizeTransformViewportRadians(angle - _transformViewportGizmoDragLastAngleRadians);
        _transformViewportGizmoDragLastAngleRadians = angle;
        double sign = Math.Abs(_transformViewportGizmoDragRingScreenSign) < 0.001 ? -1.0 : _transformViewportGizmoDragRingScreenSign;
        _transformViewportGizmoDragAccumulatedDegrees += delta * 180.0 / Math.PI / sign;
        return _transformViewportGizmoDragAccumulatedDegrees;
    }

    private static double NormalizeTransformViewportRadians(double radians)
    {
        while (radians > Math.PI) radians -= Math.PI * 2.0;
        while (radians < -Math.PI) radians += Math.PI * 2.0;
        return radians;
    }

    private static double GetTransformViewportRingScreenSign(DevToolsPreviewCamera camera, Vector3 center, TransformGizmoAxis axis, Vector3 axisX, Vector3 axisY, Vector3 axisZ, float axisLength)
    {
        Vector3 axisA = axis == TransformGizmoAxis.X ? axisY : axisX;
        Vector3 axisB = axis == TransformGizmoAxis.Z ? axisY : axisZ;
        if (axis == TransformGizmoAxis.Y) axisB = axisZ;
        if (!camera.Project(center, out NVector2 centerScreen, out _)) return -1.0;
        NVector2 previous = default;
        bool hasPrevious = false;
        for (int i = 0; i <= 72; i++)
        {
            float angle = (float)(i / 72.0 * Math.PI * 2.0);
            Vector3 point = center + axisA * ((float)Math.Cos(angle) * axisLength) + axisB * ((float)Math.Sin(angle) * axisLength);
            if (!camera.Project(point, out NVector2 projected, out _)) continue;
            if (hasPrevious)
            {
                NVector2 from = previous - centerScreen;
                NVector2 to = projected - centerScreen;
                float cross = from.X * to.Y - from.Y * to.X;
                if (Math.Abs(cross) > 0.001f) return Math.Sign(cross);
            }

            previous = projected;
            hasPrevious = true;
        }

        return -1.0;
    }

    private void ApplyTransformViewportGizmoDrag(TransformAssetEntry asset, TransformSlotSelection slot, ModelTransform transform, DevToolsPreviewCamera camera)
    {
        NVector2 direction = NormalizeTransformScreenVector(_transformViewportGizmoDragVector, new NVector2(1f, 0f));
        NVector2 mouseDelta = ImGui.GetMousePos() - _transformViewportGizmoDragMouseStart;
        double projected = NVector2.Dot(mouseDelta, direction);
        float value = _transformViewportGizmoDragStartValue;

        switch (_transformViewportGizmoDragMode)
        {
            case TransformGizmoMode.Move:
                value += (float)(projected / Math.Max(1f, camera.FocalLength) * _transformPreviewDistance);
                value = (float)SnapTransformGizmoValue(value, Math.Max(0.0001, TransformGizmoIncrement));
                break;
            case TransformGizmoMode.Scale:
                value = Math.Clamp(value + (float)(projected * 0.01f), 0.001f, 100f);
                value = (float)SnapTransformGizmoValue(value, Math.Max(0.0001, TransformGizmoIncrement));
                break;
            case TransformGizmoMode.Rotate:
                value = NormalizeTransformDegrees(value + (float)UpdateTransformViewportGizmoRingDrag());
                value = NormalizeTransformDegrees((float)SnapTransformGizmoValue(value, Math.Max(0.0001, TransformGizmoIncrement)));
                break;
            default:
                return;
        }

        if (Math.Abs(value - GetTransformGizmoAxisValue(transform, _transformViewportGizmoDragMode, _transformViewportGizmoDragAxis)) < 0.0001f) return;
        SetTransformGizmoAxisValue(transform, _transformViewportGizmoDragMode, _transformViewportGizmoDragAxis, value);
        ApplyTransformDraftEdit(asset, slot, transform);
    }

    private static float GetTransformGizmoAxisValue(ModelTransform transform, TransformGizmoMode mode, TransformGizmoAxis axis)
    {
        return mode switch
        {
            TransformGizmoMode.Move => axis switch
            {
                TransformGizmoAxis.X => transform.Translation.X,
                TransformGizmoAxis.Y => transform.Translation.Y,
                TransformGizmoAxis.Z => transform.Translation.Z,
                _ => 0
            },
            TransformGizmoMode.Rotate => axis switch
            {
                TransformGizmoAxis.X => transform.Rotation.X,
                TransformGizmoAxis.Y => transform.Rotation.Y,
                TransformGizmoAxis.Z => transform.Rotation.Z,
                _ => 0
            },
            TransformGizmoMode.Scale => axis switch
            {
                TransformGizmoAxis.X => transform.ScaleXYZ.X,
                TransformGizmoAxis.Y => transform.ScaleXYZ.Y,
                TransformGizmoAxis.Z => transform.ScaleXYZ.Z,
                _ => 1
            },
            _ => 0
        };
    }

    private static void SetTransformGizmoAxisValue(ModelTransform transform, TransformGizmoMode mode, TransformGizmoAxis axis, float value)
    {
        switch (mode)
        {
            case TransformGizmoMode.Move:
                if (axis == TransformGizmoAxis.X) transform.Translation.X = value;
                if (axis == TransformGizmoAxis.Y) transform.Translation.Y = value;
                if (axis == TransformGizmoAxis.Z) transform.Translation.Z = value;
                break;
            case TransformGizmoMode.Rotate:
                if (axis == TransformGizmoAxis.X) transform.Rotation.X = value;
                if (axis == TransformGizmoAxis.Y) transform.Rotation.Y = value;
                if (axis == TransformGizmoAxis.Z) transform.Rotation.Z = value;
                break;
            case TransformGizmoMode.Scale:
                if (axis == TransformGizmoAxis.X) transform.ScaleXYZ.X = Math.Max(0.001f, value);
                if (axis == TransformGizmoAxis.Y) transform.ScaleXYZ.Y = Math.Max(0.001f, value);
                if (axis == TransformGizmoAxis.Z) transform.ScaleXYZ.Z = Math.Max(0.001f, value);
                break;
        }
    }

    private double SnapTransformGizmoValue(double value, double step)
    {
        return IncludeGizmoInIncrement ? Math.Round(value / step) * step : value;
    }

    private static float NormalizeTransformDegrees(float value)
    {
        while (value > 180f) value -= 360f;
        while (value < -180f) value += 360f;
        return value;
    }

    private void ClearTransformViewportGizmoDrag()
    {
        _transformViewportGizmoDragAxis = TransformGizmoAxis.None;
        _transformViewportGizmoDragMode = TransformGizmoMode.None;
        _transformViewportGizmoDragVector = new NVector2(1f, 0f);
        _transformViewportGizmoDragCenter = NVector2.Zero;
        _transformViewportGizmoDragLastAngleRadians = 0;
        _transformViewportGizmoDragAccumulatedDegrees = 0;
        _transformViewportGizmoDragRingScreenSign = -1.0;
        _transformViewportGizmoDragSlotKey = "";
    }

    private static void DrawTransformViewportGizmoAxis(ImDrawListPtr drawList, NVector2 center, NVector2 axis, uint color)
    {
        DrawTransformViewportLine(drawList, center, center + axis, color, 2.4f);
    }

    private static void DrawTransformViewportGizmoArrow(ImDrawListPtr drawList, NVector2 center, NVector2 axis, uint color)
    {
        NVector2 tip = center + axis;
        NVector2 dir = NormalizeTransformScreenVector(axis, new NVector2(1f, 0f));
        NVector2 normal = new(-dir.Y, dir.X);
        drawList.AddTriangleFilled(tip, tip - dir * 13f + normal * 5.5f, tip - dir * 13f - normal * 5.5f, color);
    }

    private static void DrawTransformViewportGizmoCube(ImDrawListPtr drawList, NVector2 center, uint color)
    {
        NVector2 half = new(5.5f, 5.5f);
        drawList.AddRectFilled(center - half, center + half, color, 1.5f);
    }

    private static void DrawTransformViewportGizmoRing(ImDrawListPtr drawList, DevToolsPreviewCamera camera, Vector3 center, Vector3 axisA, Vector3 axisB, float radius, uint color)
    {
        const int segments = 72;
        if (!camera.Project(center + axisA * radius, out NVector2 previous, out _)) return;
        for (int i = 1; i <= segments; i++)
        {
            float angle = (float)(i / (double)segments * Math.PI * 2.0);
            Vector3 point = center + axisA * ((float)Math.Cos(angle) * radius) + axisB * ((float)Math.Sin(angle) * radius);
            if (camera.Project(point, out NVector2 projected, out _))
            {
                DrawTransformViewportLine(drawList, previous, projected, color, 2.4f);
                previous = projected;
            }
        }
    }

    private static void DrawTransformViewportLine(ImDrawListPtr drawList, NVector2 start, NVector2 end, uint color, float thickness)
    {
        if (!float.IsFinite(start.X) || !float.IsFinite(start.Y) || !float.IsFinite(end.X) || !float.IsFinite(end.Y)) return;
        drawList.AddLine(start, end, color, thickness);
    }

    private static float DistancePointToTransformViewportSegment(NVector2 point, NVector2 a, NVector2 b)
    {
        NVector2 ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq <= 0.0001f) return (point - a).Length();
        float t = Math.Clamp(NVector2.Dot(point - a, ab) / lengthSq, 0f, 1f);
        return (point - (a + ab * t)).Length();
    }

    private static void DrawTransformPreviewLine(ImDrawListPtr drawList, DevToolsPreviewCamera camera, Vector3 start, Vector3 end, uint color, float thickness)
    {
        if (!camera.Project(start, out NVector2 a, out _) || !camera.Project(end, out NVector2 b, out _)) return;
        drawList.AddLine(a, b, color, thickness);
    }

    private static void DrawTransformPreviewGrid(ImDrawListPtr drawList, DevToolsPreviewCamera camera, int extent, uint color, uint majorColor)
    {
        DrawTransformPreviewGridPlane(drawList, camera, Vector3.UnitX, Vector3.UnitZ, extent, color, majorColor);
        DrawTransformPreviewGridPlane(drawList, camera, Vector3.UnitX, Vector3.UnitY, extent, color, majorColor);
        DrawTransformPreviewGridPlane(drawList, camera, Vector3.UnitZ, Vector3.UnitY, extent, color, majorColor);
    }

    private static void DrawTransformPreviewGridPlane(ImDrawListPtr drawList, DevToolsPreviewCamera camera, Vector3 axisA, Vector3 axisB, int extent, uint color, uint majorColor)
    {
        for (int i = -extent; i <= extent; i++)
        {
            uint lineColor = i == 0 ? majorColor : color;
            float thickness = i == 0 ? 1.8f : 1f;
            DrawTransformPreviewGridLine(drawList, camera, axisA * -extent + axisB * i, axisA * extent + axisB * i, lineColor, thickness);
            DrawTransformPreviewGridLine(drawList, camera, axisA * i + axisB * -extent, axisA * i + axisB * extent, lineColor, thickness);
        }
    }

    private static void DrawTransformPreviewGridLine(ImDrawListPtr drawList, DevToolsPreviewCamera camera, Vector3 start, Vector3 end, uint color, float thickness)
    {
        int segments = Math.Max(1, (int)Math.Ceiling((end - start).Length));
        Vector3 step = (end - start) / segments;
        bool previousVisible = camera.Project(start, out NVector2 previousScreen, out _);

        for (int segment = 1; segment <= segments; segment++)
        {
            Vector3 point = start + step * segment;
            bool visible = camera.Project(point, out NVector2 screen, out _);
            if (previousVisible && visible)
            {
                DrawTransformViewportLine(drawList, previousScreen, screen, color, thickness);
            }

            previousScreen = screen;
            previousVisible = visible;
        }
    }

    private static Vector3 GetTransformReferenceAnchor(TransformGizmoContext context, DevToolsPreviewBounds referenceBounds)
    {
        if (!referenceBounds.IsValid) return Vector3.Zero;

        return context switch
        {
            TransformGizmoContext.Ground => DevToolsPreviewPlacement.TopCenter(referenceBounds),
            TransformGizmoContext.Display => referenceBounds.Center,
            _ => Vector3.Zero
        };
    }

    private static Vector3 GetTransformMeshAnchor(TransformGizmoContext context, DevToolsPreviewBounds meshBounds)
    {
        if (!meshBounds.IsValid) return Vector3.Zero;

        return context switch
        {
            TransformGizmoContext.Ground => DevToolsPreviewPlacement.BottomCenter(meshBounds),
            TransformGizmoContext.Display => meshBounds.Center,
            _ => Vector3.Zero
        };
    }

    private TransformPreviewPlacement BuildTransformPreviewPlacement(
        string attributeCode,
        Block? reference,
        TransformGizmoContext context,
        DevToolsPreviewBounds? referenceBounds,
        MeshData mesh)
    {
        if (TryBuildForgeTransformPlacement(attributeCode, out TransformPreviewPlacement forgePlacement))
        {
            return forgePlacement;
        }

        if (reference != null &&
            ReferenceInventoryTransformMatches(reference, attributeCode, out string configuredAttribute) &&
            TryBuildInventoryTransformPlacement(reference, configuredAttribute, out TransformPreviewPlacement inventoryPlacement))
        {
            return inventoryPlacement;
        }

        Matrixf identity = CreateIdentityMatrix();
        DevToolsPreviewBounds transformedBounds = DevToolsPreviewMeshFactory.CalculateBounds(mesh);
        if (referenceBounds is { IsValid: true } validReferenceBounds && transformedBounds.IsValid)
        {
            Vector3 referenceAnchor = GetTransformReferenceAnchor(context, validReferenceBounds);
            Vector3 meshAnchor = GetTransformMeshAnchor(context, transformedBounds);
            Vector3 offset = referenceAnchor - meshAnchor;
            if (offset.LengthSquared > 0.000001f)
            {
                mesh.Translate(offset.X, offset.Y, offset.Z);
                transformedBounds = DevToolsPreviewMeshFactory.CalculateBounds(mesh);
            }

            string status = context is TransformGizmoContext.Display or TransformGizmoContext.Ground
                ? "placement: bounds fallback"
                : "placement: no metadata provider; item left in transform-local space";
            return new(identity, referenceAnchor, context is TransformGizmoContext.Display or TransformGizmoContext.Ground, status);
        }

        Vector3 anchor = transformedBounds.IsValid ? transformedBounds.Center : Vector3.Zero;
        return new(identity, anchor, false, "placement: no reference provider");
    }

    private static bool TryBuildForgeTransformPlacement(string attributeCode, out TransformPreviewPlacement placement)
    {
        if (GetTransformBaseAttributeCode(attributeCode).Contains("forge", StringComparison.OrdinalIgnoreCase))
        {
            Matrixf matrix = CreateIdentityMatrix();
            matrix.Translate(0f, 0.6875f, 0f);
            placement = new(matrix, new Vector3(0.5f, 0.6875f, 0.5f), true, "placement: forge runtime anchor");
            return true;
        }

        placement = TransformPreviewPlacement.Empty;
        return false;
    }

    private static bool TryBuildInventoryTransformPlacement(Block reference, string configuredAttribute, out TransformPreviewPlacement placement)
    {
        if (!TryGetFirstReferenceSlotCenter(reference, out Vector3 center))
        {
            placement = TransformPreviewPlacement.Empty;
            return false;
        }

        Vector3 slotRotation = ReadRotationDegrees(GetFirstRotationByIndex(reference, 0));
        Vector3 blockRotation = ReadRotationDegrees(reference.Attributes?["rotate"]);
        Matrixf matrix = CreateIdentityMatrix();
        matrix.Translate(center.X, center.Y, center.Z)
            .RotateX(slotRotation.X * GameMath.DEG2RAD)
            .RotateY(slotRotation.Y * GameMath.DEG2RAD)
            .RotateZ(slotRotation.Z * GameMath.DEG2RAD)
            .RotateX(blockRotation.X * GameMath.DEG2RAD)
            .RotateY(blockRotation.Y * GameMath.DEG2RAD)
            .RotateZ(blockRotation.Z * GameMath.DEG2RAD);

        placement = new(matrix, center, true, $"placement: {configuredAttribute} slot 0");
        return true;
    }

    private static bool TryGetFirstReferenceSlotCenter(Block reference, out Vector3 center)
    {
        Cuboidf[]? boxes = reference.SelectionBoxes;
        if (boxes == null || boxes.Length == 0) boxes = reference.CollisionBoxes;
        if (boxes == null || boxes.Length == 0)
        {
            center = new Vector3(0.5f, 0.5f, 0.5f);
            return true;
        }

        Cuboidf box = boxes[0];
        center = new Vector3(
            box.X1 + (box.X2 - box.X1) * 0.5f,
            box.Y1 + (box.Y2 - box.Y1) * 0.5f,
            box.Z1 + (box.Z2 - box.Z1) * 0.5f);
        return true;
    }

    private static JsonObject? GetFirstRotationByIndex(Block reference, int index)
    {
        if (reference.Attributes == null || !reference.Attributes.KeyExists("rotations")) return null;
        JsonObject[]? rotations = reference.Attributes["rotations"].AsArray();
        if (rotations == null) return null;
        return rotations.Length > index ? rotations[index] : null;
    }

    private static Vector3 ReadRotationDegrees(JsonObject? json)
    {
        if (json == null) return Vector3.Zero;
        return new Vector3(
            json["x"].AsFloat(0f),
            json["y"].AsFloat(0f),
            json["z"].AsFloat(0f));
    }

    private static Matrixf CreateIdentityMatrix()
    {
        Matrixf matrix = new();
        matrix.Identity();
        return matrix;
    }

    private void ResetTransformPreviewCameraToSelection()
    {
        _transformPreviewCacheKey = "";
        ResetTransformPreviewCameraToBounds();
    }

    private void ResetTransformPreviewCameraToBounds()
    {
        if (_transformReferenceMesh?.Bounds.IsValid == true)
        {
            DevToolsPreviewBounds referenceBounds = _transformReferenceMesh.Bounds.Include(_transformPreviewAnchor);
            _transformPreviewTarget = referenceBounds.Center;
            _transformPreviewDistance = Math.Clamp(referenceBounds.Radius * 3.1f, 1.4f, 36f);
            return;
        }

        DevToolsPreviewBounds bounds = DevToolsPreviewBounds.Empty;
        if (_transformPreviewMesh != null) bounds = bounds.Include(_transformPreviewMesh.Bounds);
        if (bounds.IsValid) bounds = bounds.Include(_transformPreviewAnchor);
        if (!bounds.IsValid)
        {
            _transformPreviewTarget = new Vector3(0.5f, 0.5f, 0.5f);
            _transformPreviewDistance = 4.5f;
            return;
        }

        _transformPreviewTarget = bounds.Center;
        _transformPreviewDistance = Math.Clamp(bounds.Radius * 3.1f, 1.4f, 36f);
    }

    private int GetTransformFamilyCount(TransformAssetEntry asset)
    {
        return _transformFamilyCounts.TryGetValue(GetTransformFamilyKey(asset), out int count) ? count : 1;
    }

    private DevToolsPreview3DRenderer EnsureTransformsPreviewRenderer()
    {
        return _transformsPreviewRenderer ??= new DevToolsPreview3DRenderer(_api);
    }

    private ModelTransform GetTransformDraft(TransformAssetEntry asset, TransformSlotSelection slot)
    {
        if (_transformDrafts.TryGetValue(slot.Key, out ModelTransform? draft)) return draft;
        ModelTransform transform = ReadTransform(asset, slot) ?? CreateDefaultTransformForSlot(asset, slot.AttributeCode);
        transform.EnsureDefaultValues();
        _transformDrafts[slot.Key] = transform;
        return transform;
    }

    private ModelTransform? ReadTransform(TransformAssetEntry asset, TransformSlotSelection slot)
    {
        if (slot.TypedKey == null)
        {
            return asset.Collectible.Attributes?[slot.AttributeCode].AsObject<ModelTransform>()?.Clone();
        }

        if (asset.Collectible.Attributes?[slot.AttributeCode].Token is not JObject map) return null;
        return map[slot.TypedKey] == null ? null : new JsonObject(map[slot.TypedKey]).AsObject<ModelTransform>()?.Clone();
    }

    private bool TransformSlotExists(TransformAssetEntry asset, TransformSlotSelection slot)
    {
        if (slot.TypedKey == null) return asset.Collectible.Attributes?[slot.AttributeCode].Exists == true;
        return asset.Collectible.Attributes?[slot.AttributeCode].Token is JObject map && map[slot.TypedKey] != null;
    }

    private void MarkTransformDirty(TransformAssetEntry asset, TransformSlotSelection slot)
    {
        _transformDirtyKeys.Add(slot.Key);
        _transformPreviewCacheKey = "";
        RebuildVisibleTransformAssets();
        if (_liveApplyManager.AutoApply)
        {
            ApplyTransformLive(asset, slot, GetTransformDraft(asset, slot));
        }
    }

    private void ApplyTransformDraftEdit(TransformAssetEntry asset, TransformSlotSelection slot, ModelTransform transform)
    {
        List<(TransformAssetEntry Asset, TransformSlotSelection Slot)> targets = GetTransformEditTargets(asset, slot, out int skipped);
        foreach ((TransformAssetEntry targetAsset, TransformSlotSelection targetSlot) in targets)
        {
            _transformDrafts[targetSlot.Key] = ReferenceEquals(targetSlot, slot) || targetSlot.Key.Equals(slot.Key, StringComparison.OrdinalIgnoreCase)
                ? transform
                : transform.Clone();
            _transformDirtyKeys.Add(targetSlot.Key);
        }

        _transformApplicabilityCache.Clear();
        _transformPreviewCacheKey = "";
        RebuildVisibleTransformAssets();
        if (_liveApplyManager.AutoApply)
        {
            foreach ((TransformAssetEntry targetAsset, TransformSlotSelection targetSlot) in targets)
            {
                ApplyTransformLive(targetAsset, targetSlot, GetTransformDraft(targetAsset, targetSlot));
            }
        }

        string skippedSuffix = FormatTransformSkippedSuffix(skipped);
        _transformsStatus = targets.Count > 1
            ? $"Edited {slot.DisplayName} for {targets.Count} {GetTransformFamilyDisplayKey(asset)} asset(s){skippedSuffix}."
            : $"Edited {slot.DisplayName} for {asset.Label}{skippedSuffix}.";
    }

    private void ResetTransformDraftToDefault(TransformAssetEntry asset, TransformSlotSelection slot)
    {
        List<(TransformAssetEntry Asset, TransformSlotSelection Slot)> targets = GetTransformEditTargets(asset, slot, out int skipped);
        foreach ((TransformAssetEntry targetAsset, TransformSlotSelection targetSlot) in targets)
        {
            _transformDrafts[targetSlot.Key] = CreateDefaultTransformForSlot(targetAsset, targetSlot.AttributeCode);
            _transformDirtyKeys.Add(targetSlot.Key);
        }

        _transformApplicabilityCache.Clear();
        _transformPreviewCacheKey = "";
        RebuildVisibleTransformAssets();
        if (_liveApplyManager.AutoApply)
        {
            foreach ((TransformAssetEntry targetAsset, TransformSlotSelection targetSlot) in targets)
            {
                ApplyTransformLive(targetAsset, targetSlot, GetTransformDraft(targetAsset, targetSlot));
            }
        }

        string skippedSuffix = FormatTransformSkippedSuffix(skipped);
        _transformsStatus = targets.Count > 1
            ? $"Reset {slot.DisplayName} defaults for {targets.Count} {GetTransformFamilyDisplayKey(asset)} asset(s){skippedSuffix}."
            : $"Reset {slot.DisplayName} default for {asset.Label}{skippedSuffix}.";
    }

    private SourceSaveResult TrySaveSelectedTransformToSource(TransformAssetEntry asset, TransformSlotSelection slot)
    {
        if (!_transformGroupEdit)
        {
            return TrySaveTransformToSource(asset.Collectible, slot.AttributeCode, GetTransformDraft(asset, slot), slot.TypedKey);
        }

        List<(TransformAssetEntry Asset, TransformSlotSelection Slot)> targets = GetTransformEditTargets(asset, slot, out int skipped);
        return TrySaveTransformTargetsToSource(asset, slot, targets, skipped);
    }

    private SourceSaveResult TrySaveTransformTargetsToSource(
        TransformAssetEntry sourceAsset,
        TransformSlotSelection sourceSlot,
        IReadOnlyList<(TransformAssetEntry Asset, TransformSlotSelection Slot)> targets,
        int skipped)
    {
        try
        {
            if (targets.Count == 0)
            {
                return SourceSaveResult.Fail("No transform targets to save.");
            }

            Dictionary<string, TransformSourceSaveFile> files = new(StringComparer.OrdinalIgnoreCase);
            foreach ((TransformAssetEntry targetAsset, TransformSlotSelection targetSlot) in targets)
            {
                TransformSourceSaveFile file = GetOrCreateTransformSourceSaveFile(files, targetAsset);
                ApplyTransformToSourceDocument(file.Json, targetSlot.AttributeCode, GetTransformDraft(targetAsset, targetSlot), targetSlot.TypedKey);
            }

            if (files.Count == 0)
            {
                return SourceSaveResult.Fail("No transform source files could be resolved.");
            }

            List<TransformSourceSaveFile> fileList = files.Values
                .OrderBy(file => file.OutputPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (TransformSourceSaveFile file in fileList)
            {
                file.NewText = JsonUtil.ToPrettyString(file.Json);
            }

            string skippedSuffix = FormatTransformSkippedSuffix(skipped);
            string status = $"Saved authored {sourceSlot.DisplayName} for {targets.Count} {GetTransformFamilyDisplayKey(sourceAsset)} asset(s) to {fileList.Count} file(s){skippedSuffix}.";

            if (fileList.Count == 1)
            {
                TransformSourceSaveFile file = fileList[0];
                SourceSaveRequest request = new(
                    file.OutputPath,
                    file.OldText,
                    file.NewText,
                    status,
                    () => WriteAuthoredFile(file.OutputPath, file.NewText));
                return SourceSaveResult.Preview(request);
            }

            SourceSaveRequest groupRequest = new(
                $"{fileList.Count} authored transform files",
                BuildTransformGroupSavePreview(fileList, oldText: true),
                BuildTransformGroupSavePreview(fileList, oldText: false),
                status,
                () =>
                {
                    foreach (TransformSourceSaveFile file in fileList)
                    {
                        WriteAuthoredFile(file.OutputPath, file.NewText);
                    }

                    return "";
                });
            return SourceSaveResult.Preview(groupRequest);
        }
        catch (Exception exception)
        {
            return SourceSaveResult.Fail($"Group save failed for {sourceAsset.Collectible.Code}: {exception.Message}");
        }
    }

    private static TransformSourceSaveFile GetOrCreateTransformSourceSaveFile(Dictionary<string, TransformSourceSaveFile> files, TransformAssetEntry asset)
    {
        IAsset? sourceAsset = FindCollectibleSourceAsset(asset.Collectible);
        string domain = sourceAsset?.Location.Domain ?? asset.Collectible.Code?.Domain ?? "game";
        string kind = asset.Collectible is Block ? "blocktypes" : "itemtypes";
        string assetPath = sourceAsset?.Location.Path ?? $"{kind}/{EnsureJsonFilePath(asset.Collectible.Code?.Path ?? "unknown")}";
        string outputPath = GetToolAuthoredAssetPath("transforms", Path.Combine("assets", domain, assetPath.Replace('/', Path.DirectorySeparatorChar)));

        if (files.TryGetValue(outputPath, out TransformSourceSaveFile? existing))
        {
            return existing;
        }

        string sourceText = ReadAssetText(sourceAsset);
        string oldText = File.Exists(outputPath) ? File.ReadAllText(outputPath) : sourceText;
        JObject json = TryParseJsonObject(oldText) ?? TryParseJsonObject(sourceText) ?? CreateCollectibleAuthoringDocument(asset.Collectible);
        TransformSourceSaveFile file = new(outputPath, oldText, json);
        files[outputPath] = file;
        return file;
    }

    private static void ApplyTransformToSourceDocument(JObject json, string attributeCode, ModelTransform transform, string? typedKey)
    {
        JObject attributes = json["attributes"] as JObject ?? new JObject();
        if (typedKey == null)
        {
            attributes[attributeCode] = TransformToToken(transform);
        }
        else
        {
            JObject transformsByType = attributes[attributeCode] as JObject ?? new JObject();
            transformsByType[typedKey] = TransformToToken(transform);
            attributes[attributeCode] = transformsByType;
        }

        json["attributes"] = attributes;
    }

    private static string BuildTransformGroupSavePreview(IEnumerable<TransformSourceSaveFile> files, bool oldText)
    {
        return string.Join(
            Environment.NewLine + Environment.NewLine,
            files.Select(file =>
                $"// {file.OutputPath}" + Environment.NewLine +
                (oldText ? file.OldText : file.NewText)));
    }

    private List<(TransformAssetEntry Asset, TransformSlotSelection Slot)> GetTransformEditTargets(TransformAssetEntry asset, TransformSlotSelection slot, out int skipped)
    {
        skipped = 0;
        List<(TransformAssetEntry Asset, TransformSlotSelection Slot)> targets = [];
        if (!_transformGroupEdit)
        {
            targets.Add((asset, slot));
            return targets;
        }

        string familyKey = GetTransformFamilyKey(asset);
        foreach (TransformAssetEntry target in _transformAssets)
        {
            if (!string.Equals(GetTransformFamilyKey(target), familyKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(target.Key, asset.Key, StringComparison.OrdinalIgnoreCase) &&
                GetTransformApplicability(target, slot.AttributeCode).Kind != TransformApplicabilityKind.Applicable)
            {
                skipped++;
                continue;
            }

            targets.Add((target, new TransformSlotSelection(target, slot.AttributeCode, slot.TypedKey)));
        }

        return targets.Count == 0 ? [(asset, slot)] : targets;
    }

    private static string FormatTransformSkippedSuffix(int skipped)
    {
        return skipped > 0 ? $"; skipped {skipped} not applicable" : "";
    }

    private string GetTransformFamilyKey(TransformAssetEntry asset)
    {
        return _transformFamilyKeys.TryGetValue(asset.Key, out string? key) ? key : BuildTransformFamilyKey(asset, asset.Collectible.Code?.Path ?? "unknown");
    }

    private string GetTransformFamilyDisplayKey(TransformAssetEntry asset)
    {
        return _transformFamilyDisplayKeys.TryGetValue(asset.Key, out string? display) ? display : asset.Label;
    }

    private TransformSlotSelection? GetSelectedTransformSlot(TransformAssetEntry? asset)
    {
        if (asset == null) return null;
        string attributeCode = GetSelectedTransformAttributeCode();
        string? typedKey = GetSelectedTransformTypedKey();
        return _transformUseTypedSlot && string.IsNullOrWhiteSpace(typedKey)
            ? null
            : new TransformSlotSelection(asset, attributeCode, typedKey);
    }

    private string GetSelectedTransformAttributeCode()
    {
        if (!_transformUseTypedSlot)
        {
            _transformDirectSlotIndex = Math.Clamp(_transformDirectSlotIndex, 0, DirectTransformAttributeCodes.Length - 1);
            return DirectTransformAttributeCodes[_transformDirectSlotIndex];
        }

        _transformTypedMapIndex = Math.Clamp(_transformTypedMapIndex, 0, TypedTransformAttributeCodes.Length - 1);
        return TypedTransformAttributeCodes[_transformTypedMapIndex];
    }

    private string? GetSelectedTransformTypedKey()
    {
        return _transformUseTypedSlot && !string.IsNullOrWhiteSpace(_transformTypedKey) ? _transformTypedKey.Trim() : null;
    }

    private IEnumerable<string> GetTypedTransformKeys(TransformAssetEntry asset, string attributeCode)
    {
        if (asset.Collectible.Attributes?[attributeCode].Token is not JObject map) yield break;
        foreach (JProperty property in map.Properties())
        {
            yield return property.Name;
        }
    }

    private IEnumerable<string> BuildReferenceBlockOptions(string filter)
    {
        foreach (Block block in _api.World.Blocks)
        {
            if (block?.Code == null) continue;
            string code = block.Code.ToString();
            if (string.IsNullOrWhiteSpace(filter) || code.Contains(filter, StringComparison.OrdinalIgnoreCase)) yield return code;
        }
    }

    private TransformReferenceResolution ResolveTransformReference(TransformAssetEntry asset, TransformSlotSelection slot)
    {
        if (!string.IsNullOrWhiteSpace(_transformReferenceBlockCode))
        {
            Block? manualBlock = ResolveReferenceBlock(_transformReferenceBlockCode);
            return manualBlock == null
                ? new(null, _transformReferenceBlockCode, $"Manual reference not found: {_transformReferenceBlockCode}", true)
                : new(manualBlock, manualBlock.Code.ToString(), $"Manual reference: {manualBlock.Code}", true);
        }

        TransformReferenceCandidate? best = null;
        foreach (Block block in _api.World.Blocks)
        {
            if (block?.Code == null) continue;
            if (!ReferenceInventoryTransformMatches(block, slot.AttributeCode, out string configuredAttribute)) continue;

            int score = 1000;
            if (block.Code.Domain.Equals(asset.Domain, StringComparison.OrdinalIgnoreCase)) score += 100;
            score += GetReferenceOrientationScore(block.Code.Path);
            if (HasNonEmptyEnumerableProperty(block, "CreativeInventoryTabs") ||
                HasNonEmptyEnumerableProperty(block, "CreativeInventoryStacks"))
            {
                score += 10;
            }

            TransformReferenceCandidate candidate = new(block, score, $"Default reference from {configuredAttribute}: {block.Code}");
            if (best == null ||
                candidate.Score > best.Score ||
                (candidate.Score == best.Score && string.Compare(candidate.Block.Code.ToString(), best.Block.Code.ToString(), StringComparison.OrdinalIgnoreCase) < 0))
            {
                best = candidate;
            }
        }

        if (best != null)
        {
            return new(best.Block, best.Block.Code.ToString(), best.Reason, false);
        }

        string fallbackCode = GetFallbackDefaultReferenceBlockCode(slot.AttributeCode);
        if (!string.IsNullOrWhiteSpace(fallbackCode))
        {
            Block? fallbackBlock = ResolveReferenceBlock(fallbackCode);
            if (fallbackBlock != null)
            {
                return new(fallbackBlock, fallbackBlock.Code.ToString(), $"Default reference fallback: {fallbackBlock.Code}", false);
            }
        }

        return new(null, "", "No metadata-backed reference block found.", false);
    }

    private Block? ResolveReferenceBlock(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return _api.World.GetBlock(AssetLocation.Create(code, "game"));
    }

    private static bool ReferenceInventoryTransformMatches(Block block, string attributeCode, out string configuredAttribute)
    {
        configuredAttribute = block.Attributes?["inventoryTransformAttribute"].AsString("") ?? "";
        if (string.IsNullOrWhiteSpace(configuredAttribute)) return false;

        string baseAttribute = GetTransformBaseAttributeCode(attributeCode);
        return configuredAttribute.Equals(attributeCode, StringComparison.OrdinalIgnoreCase) ||
               configuredAttribute.Equals(baseAttribute, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetReferenceOrientationScore(string path)
    {
        if (path.Contains("-east", StringComparison.OrdinalIgnoreCase)) return 8;
        if (path.Contains("-north", StringComparison.OrdinalIgnoreCase)) return 7;
        if (path.Contains("-south", StringComparison.OrdinalIgnoreCase)) return 6;
        if (path.Contains("-west", StringComparison.OrdinalIgnoreCase)) return 5;
        if (path.Contains("-up", StringComparison.OrdinalIgnoreCase)) return 4;
        return 0;
    }

    private static bool HasNonEmptyEnumerableProperty(object instance, string propertyName)
    {
        try
        {
            object? value = instance.GetType().GetProperty(propertyName)?.GetValue(instance);
            return value switch
            {
                Array array => array.Length > 0,
                System.Collections.IEnumerable enumerable => enumerable.GetEnumerator().MoveNext(),
                _ => value != null
            };
        }
        catch
        {
            return false;
        }
    }

    private static string GetFallbackDefaultReferenceBlockCode(string attributeCode)
    {
        string baseAttribute = GetTransformBaseAttributeCode(attributeCode);
        if (baseAttribute.Contains("forge", StringComparison.OrdinalIgnoreCase)) return "game:forge";
        if (baseAttribute.Contains("firepit", StringComparison.OrdinalIgnoreCase)) return "game:firepit";
        if (baseAttribute.Contains("trap", StringComparison.OrdinalIgnoreCase)) return "game:baskettrap";
        if (baseAttribute.Contains("shelf", StringComparison.OrdinalIgnoreCase)) return "game:shelf";
        if (baseAttribute.Contains("toolrack", StringComparison.OrdinalIgnoreCase) ||
            baseAttribute.Contains("rack", StringComparison.OrdinalIgnoreCase)) return "game:toolrack";
        if (baseAttribute.Contains("display", StringComparison.OrdinalIgnoreCase)) return "game:displaycase";
        if (baseAttribute.Contains("groundStorage", StringComparison.OrdinalIgnoreCase)) return "game:groundstorage";
        return "";
    }

    private static ModelTransform CreateDefaultTransformForSlot(TransformAssetEntry asset, string attributeCode)
    {
        bool block = asset.IsBlock;
        ModelTransform transform = attributeCode switch
        {
            "guiTransform" => block ? ModelTransform.BlockDefaultGui() : ModelTransform.ItemDefaultGui(),
            "groundTransform" or "groundStorageTransform" => block ? ModelTransform.BlockDefaultGround() : ModelTransform.ItemDefaultGround(),
            "tpHandTransform" or "tpOffHandTransform" => block ? ModelTransform.BlockDefaultTp() : ModelTransform.ItemDefaultTp(),
            _ => CreateDefaultTransform()
        };
        transform.EnsureDefaultValues();
        return transform;
    }

    private TransformAssetEntry? SelectedTransformAsset => _visibleTransformAssets.Count == 0 ? null : _visibleTransformAssets[Math.Clamp(_transformsAssetIndex, 0, _visibleTransformAssets.Count - 1)];

    private sealed record TransformAssetEntry(CollectibleObject Collectible, bool IsBlock)
    {
        public string Key => $"{(IsBlock ? "block" : "item")}:{Collectible.Code}";
        public string Domain => Collectible.Code?.Domain ?? "game";
        public string Label => $"{(IsBlock ? "Block" : "Item")} | {ImGuiLayoutHelper.CompactAssetCode(Collectible.Code?.ToString() ?? "unknown")}";
        public string SearchText => $"{Key} {Collectible.Code} {Label}";
    }

    private sealed record TransformSlotSelection(TransformAssetEntry Asset, string AttributeCode, string? TypedKey)
    {
        public string Key => $"{Asset.Key}|{AttributeCode}|{TypedKey ?? ""}";
        public string DisplayName => TypedKey == null ? AttributeCode : $"{AttributeCode} / {TypedKey}";
        public bool CanSaveToSource => true;
    }

    private sealed record TransformReferenceResolution(Block? Block, string Code, string Reason, bool IsManual);

    private sealed record TransformReferenceCandidate(Block Block, int Score, string Reason);

    private readonly record struct TransformPreviewPlacement(Matrixf ItemMatrix, Vector3 Anchor, bool GizmoAtAnchor, string Status)
    {
        public static TransformPreviewPlacement Empty => new(CreateIdentityMatrix(), Vector3.Zero, false, "");
    }

    private sealed class TransformSourceSaveFile
    {
        public TransformSourceSaveFile(string outputPath, string oldText, JObject json)
        {
            OutputPath = outputPath;
            OldText = oldText;
            Json = json;
            NewText = oldText;
        }

        public string OutputPath { get; }
        public string OldText { get; }
        public JObject Json { get; }
        public string NewText { get; set; }
    }

    private enum TransformApplicabilityKind
    {
        Applicable,
        Uncertain,
        NotApplicable
    }

    private sealed record TransformApplicabilityResult(TransformApplicabilityKind Kind, string Reason)
    {
        public static TransformApplicabilityResult Applicable(string reason) => new(TransformApplicabilityKind.Applicable, reason);
        public static TransformApplicabilityResult Uncertain(string reason) => new(TransformApplicabilityKind.Uncertain, reason);
        public static TransformApplicabilityResult NotApplicable(string reason) => new(TransformApplicabilityKind.NotApplicable, reason);
    }

    private sealed record TransformContextRule(
        string DisplayName,
        string[] CapabilityKeys,
        string[] NegativeCapabilityKeys,
        string[] BehaviorNames,
        string[] DisplayableKeys,
        bool UnmatchedIsUncertain,
        bool CheckCombustibleProps = false);
}
