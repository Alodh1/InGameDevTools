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
}
