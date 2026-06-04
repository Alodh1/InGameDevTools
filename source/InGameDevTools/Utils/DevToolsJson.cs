using Newtonsoft.Json.Linq;
using Vintagestory.API.Datastructures;

namespace InGameDevTools.Utils;

internal static class DevToolsJson
{
    public static JToken? TryParseToken(string text, bool useVintageStoryFallback = true)
    {
        return TryParseToken(text, out JToken? token, out _, useVintageStoryFallback) ? token : null;
    }

    public static bool TryParseToken(string text, out JToken? token, out string error, bool useVintageStoryFallback = true)
    {
        token = null;
        error = "";

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty JSON";
            return false;
        }

        try
        {
            token = JToken.Parse(text);
            return true;
        }
        catch (Exception first)
        {
            if (!useVintageStoryFallback)
            {
                error = first.Message;
                return false;
            }

            try
            {
                token = JsonObject.FromJson(text).Token;
                if (token != null) return true;

                error = "JSON parser returned no token";
                return false;
            }
            catch (Exception second)
            {
                error = MergeErrors(first, second);
                return false;
            }
        }
    }

    public static JObject? TryParseObject(string text, bool useVintageStoryFallback = true)
    {
        return TryParseObject(text, out JObject? json, out _, useVintageStoryFallback) ? json : null;
    }

    public static bool TryParseObject(string text, out JObject? json, out string error, bool useVintageStoryFallback = true)
    {
        json = null;

        if (!TryParseToken(text, out JToken? token, out error, useVintageStoryFallback))
        {
            return false;
        }

        if (token is JObject obj)
        {
            json = obj;
            return true;
        }

        error = "JSON root is not an object";
        return false;
    }

    private static string MergeErrors(Exception first, Exception second)
    {
        string error = second.Message;
        if (!string.IsNullOrWhiteSpace(first.Message) && !first.Message.Equals(second.Message, StringComparison.Ordinal))
        {
            error = $"{first.Message}; {second.Message}";
        }

        return error;
    }
}
