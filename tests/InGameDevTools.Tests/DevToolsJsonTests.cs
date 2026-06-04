using InGameDevTools.Utils;
using Newtonsoft.Json.Linq;

namespace InGameDevTools.Tests;

public sealed class DevToolsJsonTests
{
    [Fact]
    public void TryParseToken_ParsesValidJson()
    {
        bool parsed = DevToolsJson.TryParseToken("{\"code\":\"test\"}", out JToken? token, out string error);

        Assert.True(parsed);
        Assert.Empty(error);
        Assert.IsType<JObject>(token);
        Assert.Equal("test", token?["code"]?.ToString());
    }

    [Fact]
    public void TryParseObject_RejectsArrayRoot()
    {
        bool parsed = DevToolsJson.TryParseObject("[1,2,3]", out JObject? json, out string error);

        Assert.False(parsed);
        Assert.Null(json);
        Assert.Equal("JSON root is not an object", error);
    }

    [Fact]
    public void TryParseToken_ReportsInvalidJson()
    {
        bool parsed = DevToolsJson.TryParseToken("{", out JToken? token, out string error, useVintageStoryFallback: false);

        Assert.False(parsed);
        Assert.Null(token);
        Assert.NotEmpty(error);
    }
}
