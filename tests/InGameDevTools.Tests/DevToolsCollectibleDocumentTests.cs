using InGameDevTools.Utils;
using Newtonsoft.Json.Linq;

namespace InGameDevTools.Tests;

public sealed class DevToolsCollectibleDocumentTests
{
    [Fact]
    public void BlockAnimationSetup_AddsEntityClassAndBehaviorWithoutDroppingFields()
    {
        JObject root = JObject.Parse(
            """
            {
              "code": "plain-block",
              "shape": { "base": "block/plain" },
              "textures": { "all": "block/stone/granite" },
              "entityBehaviors": [
                { "name": "KeepMe", "properties": { "value": 3 } }
              ]
            }
            """);

        DevToolsBlockAnimationSetupResult result = DevToolsBlockAnimationSetup.Apply(root, "idle");

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(DevToolsBlockAnimationSetup.EntityClassName, root["entityClass"]!.ToString());
        Assert.Equal("block/plain", root["shape"]!["base"]!.ToString());
        JArray behaviors = (JArray)root["entityBehaviors"]!;
        Assert.Equal(["KeepMe", DevToolsBlockAnimationSetup.BehaviorName], behaviors.Select(token => token!["name"]!.ToString()).ToArray());
        JObject setup = (JObject)behaviors[1]!;
        Assert.Equal("idle", setup["properties"]!["animation"]!.ToString());
        Assert.True(setup["properties"]!["loop"]!.Value<bool>());
        Assert.Equal(1f, setup["properties"]!["speed"]!.Value<float>());
    }

    [Fact]
    public void BlockAnimationSetup_UpdatesExistingBehaviorProperties()
    {
        JObject root = JObject.Parse(
            """
            {
              "code": "plain-block",
              "entityClass": "InGameDevToolsAnimatedBlock",
              "entityBehaviors": [
                {
                  "name": "InGameDevTools:AnimatedBlock",
                  "custom": true,
                  "properties": { "animation": "old", "speed": 0.5, "loop": false, "customProp": "keep" }
                }
              ]
            }
            """);

        DevToolsBlockAnimationSetupResult result = DevToolsBlockAnimationSetup.Apply(root, "new-idle");

        Assert.True(result.Success);
        Assert.True(result.Changed);
        JObject behavior = (JObject)((JArray)root["entityBehaviors"]!)[0]!;
        Assert.True(behavior["custom"]!.Value<bool>());
        Assert.Equal("new-idle", behavior["properties"]!["animation"]!.ToString());
        Assert.Equal("keep", behavior["properties"]!["customProp"]!.ToString());
    }

    [Fact]
    public void BlockAnimationSetup_DoesNotOverwriteForeignEntityClass()
    {
        JObject root = JObject.Parse(
            """
            {
              "code": "chest",
              "entityClass": "Chest",
              "entityBehaviors": [{ "name": "Inventory" }]
            }
            """);

        DevToolsBlockAnimationSetupResult result = DevToolsBlockAnimationSetup.Apply(root, "open");

        Assert.False(result.Success);
        Assert.False(result.Changed);
        Assert.Equal("Chest", root["entityClass"]!.ToString());
        Assert.DoesNotContain(((JArray)root["entityBehaviors"]!).Select(token => token!["name"]!.ToString()), name => name == DevToolsBlockAnimationSetup.BehaviorName);
    }

    [Fact]
    public void AnimatedBlockConfig_ParsesDefaultsAndClampsSpeed()
    {
        JObject root = JObject.Parse("""{ "animation": "idle", "speed": -2, "loop": false }""");

        DevToolsAnimatedBlockConfig config = DevToolsBlockAnimationSetup.ParseConfig(root);

        Assert.Equal("idle", config.AnimationCode);
        Assert.Equal(0.001f, config.Speed);
        Assert.False(config.Loop);
        Assert.True(config.IsValid);
    }

    [Fact]
    public void ImportExport_PreservesUnknownRootBehaviorsAttributesTexturesAndByType()
    {
        string source = """
        {
          "code": "gear-{metal}",
          "__comment": "keep root",
          "variantgroups": [
            { "code": "metal", "states": ["copper", "tin"], "custom": true }
          ],
          "behaviors": [
            { "name": "GroundStorable", "layout": "WallHalves", "customBehaviorField": 4 }
          ],
          "textures": {
            "all": { "base": "block/metal/plate", "customTextureField": "x" }
          },
          "attributes": {
            "handbook": { "group": "gear" },
            "customArray": [1, 2]
          },
          "byType": {
            "*-copper": { "durability": 20 }
          }
        }
        """;

        DevToolsCollectibleDocumentDraft document = DevToolsCollectibleDocumentDraft.FromJson(
            source,
            DevToolsCollectibleKind.Block,
            "testmod",
            "blocktypes/gear.json");

        JObject emitted = JObject.Parse(document.ToJson());
        Assert.Equal("keep root", emitted["__comment"]!.ToString());
        Assert.True(emitted["variantgroups"]![0]!["custom"]!.Value<bool>());
        Assert.Equal(4, emitted["behaviors"]![0]!["customBehaviorField"]!.Value<int>());
        Assert.Equal("x", emitted["textures"]!["all"]!["customTextureField"]!.ToString());
        Assert.Equal("gear", emitted["attributes"]!["handbook"]!["group"]!.ToString());
        Assert.Equal(20, emitted["byType"]!["*-copper"]!["durability"]!.Value<int>());
    }

    [Fact]
    public void VariantExpansionAndByTypeMatching_HandleWildcards()
    {
        DevToolsCollectibleDocumentDraft document = DevToolsCollectibleDocumentDraft.FromJson(
            """
            {
              "code": "gear-{metal}-{size}",
              "variantgroups": [
                { "code": "metal", "states": ["copper", "tin"] },
                { "code": "size", "states": ["small", "large"] }
              ],
              "byType": {
                "gear-copper-*": { "value": 1 },
                "gear-tin-large": { "value": 2 }
              }
            }
            """,
            DevToolsCollectibleKind.Item,
            "testmod",
            "itemtypes/gear.json");

        Assert.Equal(
            ["gear-copper-small", "gear-copper-large", "gear-tin-small", "gear-tin-large"],
            document.ExpandVariantCodes());
        Assert.Equal(["gear-copper-*"], document.FindByTypeMatches("testmod:gear-copper-small"));
        Assert.Equal(["gear-tin-large"], document.FindByTypeMatches("gear-tin-large"));
    }

    [Fact]
    public void BehaviorMutations_EmitExactOrder()
    {
        DevToolsCollectibleDocumentDraft document = DevToolsCollectibleDocumentDraft.FromJson(
            """{ "code": "thing", "behaviors": [{ "name": "A" }, { "name": "B" }] }""",
            DevToolsCollectibleKind.Block,
            "testmod",
            "blocktypes/thing.json");

        List<DevToolsCollectibleBehaviorDraft> behaviors = document.GetBehaviors().Select(behavior => behavior.Clone()).ToList();
        behaviors.Add(new DevToolsCollectibleBehaviorDraft { Name = "C" });
        DevToolsCollectibleBehaviorDraft duplicate = behaviors[0].Clone();
        duplicate.Name = "A-copy";
        behaviors.Insert(1, duplicate);
        DevToolsCollectibleBehaviorDraft moved = behaviors[3];
        behaviors.RemoveAt(3);
        behaviors.Insert(0, moved);
        behaviors.RemoveAt(1);
        document.SetBehaviors(behaviors);

        JArray emitted = (JArray)JObject.Parse(document.ToJson())["behaviors"]!;
        Assert.Equal(["C", "A-copy", "B"], emitted.Select(row => row!["name"]!.ToString()).ToArray());
    }

    [Fact]
    public void StructuredAttributeEdits_ProduceNestedJson()
    {
        DevToolsCollectibleDocumentDraft document = DevToolsCollectibleDocumentDraft.Empty(
            DevToolsCollectibleKind.Item,
            "testmod",
            "tool");

        document.SetAttribute(new[] { "handbook", "group" }, "tools");
        document.SetAttribute(new[] { "combat", "damage" }, JToken.FromObject(4));

        JObject emitted = JObject.Parse(document.ToJson());
        Assert.True(document.TryGetAttribute(["handbook", "group"], out JToken? group), document.ToJson());
        Assert.True(document.TryGetAttribute(["combat", "damage"], out JToken? damage), document.ToJson());
        Assert.Equal("tools", group!.ToString());
        Assert.Equal(4, damage!.Value<int>());
        Assert.Equal("tools", emitted["attributes"]!["handbook"]!["group"]!.ToString());
        Assert.Equal(4, emitted["attributes"]!["combat"]!["damage"]!.Value<int>());
    }

    [Fact]
    public void Validation_CatchesCommonAuthoringErrors()
    {
        DevToolsCollectibleDocumentDraft document = DevToolsCollectibleDocumentDraft.FromJson(
            """
            {
              "shape": { "base": "block/missing-shape" },
              "textures": { "all": "block/missing-texture" },
              "variantgroups": [
                { "code": "kind", "states": ["a", "a"] },
                { "code": "kind", "states": ["b"] }
              ],
              "behaviors": [{ "layout": "missing-name" }],
              "drops": [{ "type": "item" }],
              "attributes": []
            }
            """,
            DevToolsCollectibleKind.Block,
            "testmod",
            "blocktypes/../bad.json");

        IReadOnlyList<DevToolsCollectibleValidationIssue> issues = document.Validate(
            shapeExists: _ => false,
            textureExists: _ => false,
            stackExists: _ => false);

        Assert.Contains(issues, issue => issue.Message.Contains("Missing required code", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Message.Contains("save path", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Message.Contains("shape.base", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Message.Contains("Texture", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Message.Contains("Duplicate variantgroup", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Message.Contains("duplicate state", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Message.Contains("behavior", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Message.Contains("drop", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Message.Contains("attributes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AuthoredOutputPath_NormalizesBlockAndItemRoots()
    {
        DevToolsCollectibleDocumentDraft block = DevToolsCollectibleDocumentDraft.Empty(
            DevToolsCollectibleKind.Block,
            "TestMod",
            "stone/path");
        DevToolsCollectibleDocumentDraft item = DevToolsCollectibleDocumentDraft.Empty(
            DevToolsCollectibleKind.Item,
            "TestMod",
            "gear");

        Assert.Equal(Path.Combine("assets", "testmod", "blocktypes", "stone-path.json"), block.BuildAssetRelativePath());
        Assert.Equal(Path.Combine("assets", "testmod", "itemtypes", "gear.json"), item.BuildAssetRelativePath());
        Assert.Equal("itemtypes/custom/folder.json", DevToolsCollectibleDocumentDraft.NormalizeAssetPath(DevToolsCollectibleKind.Item, "custom/folder", "gear"));
        Assert.Throws<InvalidOperationException>(() => DevToolsCollectibleDocumentDraft.NormalizeAssetPath(DevToolsCollectibleKind.Block, "../bad", "bad"));
    }
}
