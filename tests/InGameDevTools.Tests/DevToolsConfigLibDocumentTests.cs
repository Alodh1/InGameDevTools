using InGameDevTools.Utils;
using Newtonsoft.Json.Linq;

namespace InGameDevTools.Tests;

public sealed class DevToolsConfigLibDocumentTests
{
    [Fact]
    public void ImportExport_PreservesUnknownFields()
    {
        DevToolsConfigLibDocumentDraft document = DevToolsConfigLibDocumentDraft.FromPatchJson(
            """
            {
              "version": 3,
              "customRoot": "keep",
              "settings": [
                {
                  "code": "gameplay/speed",
                  "name": "speed",
                  "title": "Speed",
                  "comment": "Tune speed",
                  "type": "float",
                  "default": 1.5,
                  "range": { "min": 0, "max": 4, "step": 0.25, "snap": true },
                  "values": [ "slow", "fast" ],
                  "logarithmic": true,
                  "clientSide": true,
                  "weight": 10,
                  "customSetting": { "x": 1 }
                }
              ],
              "formatting": [
                { "type": "separator", "title": "Gameplay", "weight": 5, "customFormat": true }
              ]
            }
            """,
            "example",
            "config/custom.json");

        JObject emitted = JObject.Parse(document.ToPatchJson());

        Assert.Equal(3, emitted["version"]!.Value<int>());
        Assert.Equal("keep", emitted["customRoot"]!.ToString());
        JObject setting = (JObject)emitted["settings"]![0]!;
        Assert.Equal(1, setting["customSetting"]!["x"]!.Value<int>());
        Assert.True(setting["range"]!["snap"]!.Value<bool>());
        JObject formatting = (JObject)emitted["formatting"]![0]!;
        Assert.True(formatting["customFormat"]!.Value<bool>());
    }

    [Fact]
    public void ModConfigInference_CreatesScalarAndComplexSettings()
    {
        JToken root = JToken.Parse(
            """
            {
              "enabled": true,
              "count": 4,
              "speed": 1.25,
              "name": "ore",
              "nested": {
                "mode": "all",
                "limits": { "min": 1, "max": 2 }
              },
              "entries": [ { "code": "a" }, { "code": "b" } ]
            }
            """);

        DevToolsConfigLibDocumentDraft document = DevToolsConfigLibDocumentDraft.FromModConfig(root, "ExampleConfig.json", "Example Mod");

        Assert.Contains(document.Settings, setting => setting.Code == "enabled" && setting.Type == "boolean");
        Assert.Contains(document.Settings, setting => setting.Code == "count" && setting.Type == "integer");
        Assert.Contains(document.Settings, setting => setting.Code == "speed" && setting.Type == "float");
        Assert.Contains(document.Settings, setting => setting.Code == "name" && setting.Type == "string");
        Assert.Contains(document.Settings, setting => setting.Code == "nested/limits" && setting.Type == "object");
        Assert.Contains(document.Settings, setting => setting.Code == "nested/limits/min" && setting.Type == "integer");
        Assert.Contains(document.Settings, setting => setting.Code == "entries" && setting.Type == "array");
        Assert.Equal("example-mod", document.Domain);
    }

    [Fact]
    public void MergeFromModConfig_PreservesCustomizedMetadata()
    {
        DevToolsConfigLibDocumentDraft document = DevToolsConfigLibDocumentDraft.FromPatchJson(
            """
            {
              "version": 1,
              "settings": [
                {
                  "code": "speed",
                  "name": "custom-speed",
                  "title": "Custom Speed",
                  "comment": "Keep this",
                  "type": "float",
                  "default": 1.0,
                  "range": { "min": 0, "max": 8, "step": 0.5 },
                  "values": [ "a", "b" ],
                  "weight": 7,
                  "x-extra": true
                }
              ]
            }
            """,
            "example",
            "config/configlib-patches.json");

        document.MergeFromModConfig(JObject.Parse("""{ "speed": 2.5, "enabled": true }"""), "ServerConfig.json");

        DevToolsConfigLibSettingDraft speed = document.Settings.Single(setting => setting.Code == "speed");
        Assert.Equal("custom-speed", speed.Name);
        Assert.Equal("Custom Speed", speed.Title);
        Assert.Equal("Keep this", speed.Comment);
        Assert.Equal("0.5", speed.RangeStepJson);
        Assert.Equal(7, speed.Weight);
        Assert.True(speed.Extra["x-extra"]!.Value<bool>());
        Assert.Equal("2.5", JToken.Parse(speed.DefaultJson).ToString());
        Assert.Contains(document.Settings, setting => setting.Code == "enabled" && setting.Type == "boolean");
    }

    [Fact]
    public void Reorder_EmitsSettingsInListOrder()
    {
        DevToolsConfigLibDocumentDraft document = DevToolsConfigLibDocumentDraft.Empty("example");
        document.Settings.Add(DevToolsConfigLibSettingDraft.FromInferred("first", "integer", new JValue(1)));
        document.Settings.Add(DevToolsConfigLibSettingDraft.FromInferred("second", "integer", new JValue(2)));

        DevToolsConfigLibSettingDraft moved = document.Settings[1];
        document.Settings.RemoveAt(1);
        document.Settings.Insert(0, moved);

        JObject emitted = JObject.Parse(document.ToPatchJson());
        Assert.Equal("second", emitted["settings"]![0]!["code"]!.ToString());
        Assert.Equal("first", emitted["settings"]![1]!["code"]!.ToString());
    }

    [Fact]
    public void Validation_CatchesCommonBlockingIssues()
    {
        DevToolsConfigLibDocumentDraft document = DevToolsConfigLibDocumentDraft.Empty("Bad Domain!");
        document.Version = -1;
        document.RelativePath = "../bad";
        document.ModConfigRelativePath = "../bad";
        document.Settings.Add(new DevToolsConfigLibSettingDraft
        {
            Code = "speed",
            Name = "duplicate",
            Type = "float",
            DefaultJson = "{",
            HasRange = true,
            RangeMinJson = "10",
            RangeMaxJson = "1",
            RangeStepJson = "0"
        });
        document.Settings.Add(new DevToolsConfigLibSettingDraft
        {
            Code = "speed",
            Name = "duplicate",
            Type = "string",
            DefaultJson = "\"missing\"",
            ValuesJson = "[\"present\"]"
        });

        List<DevToolsConfigLibValidationIssue> issues = document.Validate(modConfigIncludedOnly: true);

        Assert.Contains(issues, issue => issue.Severity == DevToolsConfigLibIssueSeverity.Error && issue.Message.Contains("Version", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Severity == DevToolsConfigLibIssueSeverity.Error && issue.Message.Contains("path traversal", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Severity == DevToolsConfigLibIssueSeverity.Error && issue.Message.Contains("invalid default JSON", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Severity == DevToolsConfigLibIssueSeverity.Error && issue.Message.Contains("min is greater", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Severity == DevToolsConfigLibIssueSeverity.Error && issue.Message.Contains("Duplicate setting code", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Severity == DevToolsConfigLibIssueSeverity.Warning && issue.Message.Contains("not present in dropdown values", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NestedOutputPath_NormalizesUnderAssetsDomain()
    {
        DevToolsConfigLibDocumentDraft document = DevToolsConfigLibDocumentDraft.Empty("Example Mod");
        document.RelativePath = "/compatibility/../compatibility/meteoricsteel/configlib";

        Assert.Equal(
            Path.Combine("assets", "example-mod", "compatibility", "meteoricsteel", "configlib.json"),
            document.BuildPatchAssetRelativePath());
    }

    [Fact]
    public void ScratchDocument_GeneratesPatchModConfigAndCSharpLoader()
    {
        DevToolsConfigLibDocumentDraft document = DevToolsConfigLibDocumentDraft.Scratch("Example Mod");
        document.Settings.Clear();
        document.Settings.Add(DevToolsConfigLibSettingDraft.FromInferred("enabled", "boolean", new JValue(true)));
        document.Settings.Add(DevToolsConfigLibSettingDraft.FromInferred("combat/damageMultiplier", "float", new JValue(1.5)));
        document.Settings.Add(DevToolsConfigLibSettingDraft.FromInferred("display/name", "string", new JValue("Longsword")));

        JObject patch = JObject.Parse(document.ToPatchJson());
        JObject modConfig = JObject.Parse(document.ToModConfigJson(includedOnly: true));
        string csharp = document.ToCSharpLoaderCode();

        Assert.Equal("enabled", patch["settings"]![0]!["code"]!.ToString());
        Assert.True(modConfig["enabled"]!.Value<bool>());
        Assert.Equal(1.5, modConfig["combat"]!["damageMultiplier"]!.Value<double>());
        Assert.Contains("namespace ExampleMod;", csharp, StringComparison.Ordinal);
        Assert.Contains("public sealed class ExampleModConfig", csharp, StringComparison.Ordinal);
        Assert.Contains("public CombatConfig Combat { get; set; } = new();", csharp, StringComparison.Ordinal);
        Assert.Contains("public float DamageMultiplier { get; set; } = 1.5f;", csharp, StringComparison.Ordinal);
        Assert.Contains("public const string FileName = \"example-mod.json\";", csharp, StringComparison.Ordinal);
        Assert.Contains("api.LoadModConfig<ExampleModConfig>(FileName)", csharp, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpLoader_GeneratesJsonLinqTypesForObjectAndArraySettings()
    {
        DevToolsConfigLibDocumentDraft document = DevToolsConfigLibDocumentDraft.Empty("complex");
        document.Settings.Add(DevToolsConfigLibSettingDraft.FromInferred("advanced/options", "object", JObject.Parse("""{ "speed": 2 }""")));
        document.Settings.Add(DevToolsConfigLibSettingDraft.FromInferred("entries", "array", JArray.Parse("""[ "a", "b" ]""")));

        string csharp = document.ToCSharpLoaderCode();

        Assert.Contains("using Newtonsoft.Json.Linq;", csharp, StringComparison.Ordinal);
        Assert.Contains("public JObject Options { get; set; } = JObject.Parse(\"{\\\"speed\\\":2}\");", csharp, StringComparison.Ordinal);
        Assert.Contains("public JArray Entries { get; set; } = JArray.Parse(\"[\\\"a\\\",\\\"b\\\"]\");", csharp, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpValidation_CatchesBadNamesAndDuplicateGeneratedProperties()
    {
        DevToolsConfigLibDocumentDraft document = DevToolsConfigLibDocumentDraft.Empty("example");
        document.CSharpNamespace = "bad namespace";
        document.LoaderClassName = "";
        document.Settings.Add(DevToolsConfigLibSettingDraft.FromInferred("foo-bar", "boolean", new JValue(true)));
        document.Settings.Add(DevToolsConfigLibSettingDraft.FromInferred("foo_bar", "boolean", new JValue(false)));

        List<DevToolsConfigLibValidationIssue> issues = document.Validate(modConfigIncludedOnly: false);

        Assert.Contains(issues, issue => issue.Severity == DevToolsConfigLibIssueSeverity.Warning && issue.Message.Contains("namespace", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Severity == DevToolsConfigLibIssueSeverity.Error && issue.Message.Contains("Loader class name", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Severity == DevToolsConfigLibIssueSeverity.Error && issue.Message.Contains("property name conflict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CSharpOutputPath_NormalizesUnderAuthoredConfigLibSourceFolder()
    {
        DevToolsConfigLibDocumentDraft document = DevToolsConfigLibDocumentDraft.Empty("example");
        document.CSharpNamespace = "Example.Mod.Config";
        document.LoaderClassName = "ExampleConfigLoader";

        Assert.Equal(
            Path.Combine("src", "Example", "Mod", "Config", "ExampleConfigLoader.cs"),
            document.BuildCSharpRelativePath());
    }
}
