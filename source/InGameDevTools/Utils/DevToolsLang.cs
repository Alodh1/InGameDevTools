using System.Globalization;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace InGameDevTools.Utils;

internal static class DevToolsLang
{
    public const string Domain = "ingamedevtools";
    public const string AutoLanguageCode = "";

    private static readonly Dictionary<string, string> Entries = new(StringComparer.Ordinal);
    private static readonly DevToolsLanguageOption[] SupportedLanguageOptions =
    [
        new(AutoLanguageCode, "Auto (game language)"),
        new("en", "English"),
        new("de", "Deutsch"),
        new("es-es", "Español"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("nl", "Nederlands"),
        new("pl", "Polski"),
        new("pt-br", "Português (Brasil)"),
        new("ru", "Русский"),
        new("tr", "Türkçe"),
        new("uk", "Українська")
    ];

    public static string ActiveLanguageCode { get; private set; } = "en";
    public static IReadOnlyList<DevToolsLanguageOption> LanguageOptions => SupportedLanguageOptions;

    public static void Load(ICoreAPI api, string? configuredLanguageCode = null)
    {
        Entries.Clear();
        LoadAsset(api, "en");

        string languageCode = string.IsNullOrWhiteSpace(configuredLanguageCode)
            ? NormalizeLanguageCode(ReadClientLanguageCode())
            : NormalizeLanguageCode(configuredLanguageCode);
        ActiveLanguageCode = languageCode;
        if (string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase)) return;

        LoadAsset(api, languageCode);
    }

    private static void LoadAsset(ICoreAPI api, string languageCode)
    {
        try
        {
            IAsset? asset = api.Assets.TryGet(new AssetLocation(Domain, $"lang/{languageCode}.json"), true);
            if (asset == null) return;
            LoadFromJson(asset.ToText(), clearExisting: false);
        }
        catch
        {
            // Keep already loaded fallback strings.
        }
    }

    internal static void LoadFromJson(string json)
    {
        LoadFromJson(json, clearExisting: true);
    }

    private static void LoadFromJson(string json, bool clearExisting)
    {
        if (clearExisting) Entries.Clear();
        JObject root = JObject.Parse(json);
        foreach (JProperty property in root.Properties())
        {
            if (property.Value.Type == JTokenType.String)
            {
                Entries[property.Name] = property.Value.ToString();
            }
        }
    }

    internal static void ClearForTests()
    {
        Entries.Clear();
        ActiveLanguageCode = "en";
    }

    public static string Get(string key, string fallback)
    {
        return Entries.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value) ? value : fallback;
    }

    public static string Get(string key, string fallback, params object?[] args)
    {
        string value = Get(key, fallback);
        if (args.Length == 0) return value;

        try
        {
            return string.Format(CultureInfo.CurrentCulture, value, args);
        }
        catch
        {
            return string.Format(CultureInfo.CurrentCulture, fallback, args);
        }
    }

    public static string Label(string key, string fallback, string id)
    {
        return $"{Get(key, fallback)}##{id}";
    }

    private static string ReadClientLanguageCode()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VintagestoryData",
                "clientsettings.json");
            if (!File.Exists(path)) return "en";
            JObject root = JObject.Parse(File.ReadAllText(path));
            return root["language"]?.ToString() ?? "en";
        }
        catch
        {
            return "en";
        }
    }

    internal static string NormalizeLanguageCode(string code)
    {
        string mapped = MapLanguageCode(code);
        return IsSupportedLanguageCode(mapped) ? mapped : "en";
    }

    internal static string NormalizeConfiguredLanguageCode(string? code)
    {
        string normalized = (code ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        if (normalized is "" or "auto" or "game" or "default") return AutoLanguageCode;

        string mapped = MapLanguageCode(normalized);
        return IsSupportedLanguageCode(mapped)
            ? mapped
            : AutoLanguageCode;
    }

    internal static bool UsesCyrillicGlyphs(string? code)
    {
        return MapLanguageCode(code) is "ru" or "uk";
    }

    internal static bool ActiveLanguageUsesCyrillic => UsesCyrillicGlyphs(ActiveLanguageCode);

    private static string MapLanguageCode(string? code)
    {
        string normalized = (code ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "" => "en",
            "es" or "es-419" => "es-es",
            "pt" or "pt-pt" => "pt-br",
            _ => normalized
        };
    }

    private static bool IsSupportedLanguageCode(string code)
    {
        return SupportedLanguageOptions.Any(option =>
            option.Code.Length > 0 && option.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
    }
}

internal readonly record struct DevToolsLanguageOption(string Code, string DisplayName);
