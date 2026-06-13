using InGameDevTools.Utils;
using Newtonsoft.Json.Linq;

namespace InGameDevTools.Tests;

public sealed class DevToolsPatchDocumentTests
{
    [Fact]
    public void VanillaImport_EmitsConditionDependsOnAndUnknownFields()
    {
        string source = """
        [
          {
            "op": "add",
            "file": "game:blocktypes/stone/granite.json",
            "path": "/attributes/foo",
            "value": 3,
            "condition": { "when": "feature", "isValue": true },
            "dependsOn": [{ "modid": "othermod" }],
            "__comment": "keep me"
          }
        ]
        """;

        DevToolsPatchDocumentDraft document = DevToolsPatchDocumentDraft.FromJson(
            source,
            DevToolsPatchOutputFormat.Vanilla,
            "testmod",
            "compat/stone.json");

        JObject operation = (JObject)JArray.Parse(document.ToJson())[0]!;
        Assert.Equal("/attributes/foo", operation["path"]!.ToString());
        Assert.Equal("othermod", operation["dependsOn"]![0]!["modid"]!.ToString());
        Assert.Equal("keep me", operation["__comment"]!.ToString());
        Assert.True(operation["condition"]!["isValue"]!.Value<bool>());
    }

    [Fact]
    public void JsonPatchesImport_EmitsExtendedFieldsAndFromPath()
    {
        string source = """
        [
          {
            "enabled": false,
            "side": "server",
            "priority": 7,
            "op": "copy",
            "file": "@@game:itemtypes/.+.json",
            "frompath": "a/0",
            "path": "b/-",
            "dependsOn": [{ "modid": "jsonpatcheslib", "version": "1.5.2" }]
          }
        ]
        """;

        DevToolsPatchDocumentDraft document = DevToolsPatchDocumentDraft.FromJson(
            source,
            DevToolsPatchOutputFormat.JsonPatchesLib,
            "testmod",
            "generated.json");

        JObject operation = (JObject)JArray.Parse(document.ToJson())[0]!;
        Assert.False(operation["enabled"]!.Value<bool>());
        Assert.Equal("server", operation["side"]!.ToString());
        Assert.Equal(7, operation["priority"]!.Value<int>());
        Assert.Equal("a/0", operation["frompath"]!.ToString());
        Assert.Equal("@@game:itemtypes/.+.json", operation["file"]!.ToString());
        Assert.Equal("1.5.2", operation["dependsOn"]![0]!["version"]!.ToString());
    }

    [Fact]
    public void RawOperation_PreservesCustomKeys()
    {
        JObject raw = JObject.Parse("""
        {
          "target": "bear-adult",
          "op": "add",
          "file": "game:entities/animal/mammal/bear-adult",
          "path": "/client/behaviors/-",
          "value": { "code": "example" }
        }
        """);

        DevToolsPatchOperationDraft operation = DevToolsPatchOperationDraft.FromJson(raw, DevToolsPatchOutputFormat.Vanilla);

        JObject emitted = operation.ToJson(DevToolsPatchOutputFormat.Vanilla);
        Assert.Equal("bear-adult", emitted["target"]!.ToString());
        Assert.Equal("example", emitted["value"]!["code"]!.ToString());
    }

    [Fact]
    public void OperationReorder_ChangesSerializedOrderExactly()
    {
        DevToolsPatchDocumentDraft document = new()
        {
            Format = DevToolsPatchOutputFormat.JsonPatchesLib
        };
        document.Operations.Add(new DevToolsPatchOperationDraft { Op = "add", File = "game:a.json", Path = "first", ValueJson = "1" });
        document.Operations.Add(new DevToolsPatchOperationDraft { Op = "add", File = "game:a.json", Path = "second", ValueJson = "2" });

        DevToolsPatchOperationDraft moved = document.Operations[1];
        document.Operations.RemoveAt(1);
        document.Operations.Insert(0, moved);

        JArray emitted = JArray.Parse(document.ToJson());
        Assert.Equal("second", emitted[0]!["path"]!.ToString());
        Assert.Equal("first", emitted[1]!["path"]!.ToString());
    }

    [Fact]
    public void Preview_ArrayAddInsertsAtNumericIndexAndAppendsAtDash()
    {
        JObject source = JObject.Parse("""{ "values": ["a", "c"] }""");
        DevToolsPatchOperationDraft insert = new()
        {
            Op = "add",
            File = "game:test.json",
            Path = "values/1",
            ValueJson = "\"b\""
        };
        DevToolsPatchOperationDraft append = new()
        {
            Op = "add",
            File = "game:test.json",
            Path = "values/-",
            ValueJson = "\"d\""
        };

        DevToolsPatchPreviewResult result = DevToolsPatchPreview.Apply(
            source,
            [insert, append],
            "game",
            "test.json",
            new DevToolsPatchPreviewOptions(DevToolsPatchOutputFormat.JsonPatchesLib, "Server", []));

        Assert.True(result.Success, result.Status);
        JArray values = (JArray)JObject.Parse(result.PreviewText)["values"]!;
        Assert.Equal(["a", "b", "c", "d"], values.Select(value => value!.ToString()).ToArray());
    }

    [Fact]
    public void Preview_SkipsDisabledAndSideMismatchedOperations()
    {
        JObject source = JObject.Parse("""{ "value": 1 }""");
        DevToolsPatchOperationDraft disabled = new()
        {
            Enabled = false,
            Op = "replace",
            File = "game:test.json",
            Path = "value",
            ValueJson = "2",
            Side = "server"
        };
        DevToolsPatchOperationDraft client = new()
        {
            Op = "replace",
            File = "game:test.json",
            Path = "value",
            ValueJson = "3",
            Side = "client"
        };

        DevToolsPatchPreviewResult result = DevToolsPatchPreview.Apply(
            source,
            [disabled, client],
            "game",
            "test.json",
            new DevToolsPatchPreviewOptions(DevToolsPatchOutputFormat.JsonPatchesLib, "Server", []));

        Assert.True(result.Success, result.Status);
        Assert.Equal(1, JObject.Parse(result.PreviewText)["value"]!.Value<int>());
        Assert.Contains(result.Warnings, warning => warning.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("side", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Preview_EvaluatesVanillaConditionFromSettings()
    {
        JObject source = JObject.Parse("""{ "value": 1 }""");
        DevToolsPatchOperationDraft operation = new()
        {
            Op = "replace",
            File = "game:test.json",
            Path = "value",
            ValueJson = "2",
            ConditionJson = """{ "when": "feature", "isValue": true }"""
        };

        DevToolsPatchPreviewResult result = DevToolsPatchPreview.Apply(
            source,
            [operation],
            "game",
            "test.json",
            new DevToolsPatchPreviewOptions(DevToolsPatchOutputFormat.Vanilla, "Server", JObject.Parse("""{ "feature": true }""")));

        Assert.True(result.Success, result.Status);
        Assert.Equal(2, JObject.Parse(result.PreviewText)["value"]!.Value<int>());
    }

    [Fact]
    public void NestedSavePath_NormalizesToFormatFolder()
    {
        Assert.Equal(
            "jsonpatches/compatibility/meteoricsteel/fixes.json",
            DevToolsPatchDocumentDraft.BuildAssetPath(DevToolsPatchOutputFormat.JsonPatchesLib, "compatibility/meteoricsteel/fixes"));
        Assert.Equal(
            "patches/compatibility/meteoricsteel/fixes.json",
            DevToolsPatchDocumentDraft.BuildAssetPath(DevToolsPatchOutputFormat.Vanilla, "/compatibility/../compatibility/meteoricsteel/fixes.json"));
    }
}
