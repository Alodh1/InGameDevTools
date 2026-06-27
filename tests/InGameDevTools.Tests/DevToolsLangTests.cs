using InGameDevTools.Utils;
using Newtonsoft.Json.Linq;

namespace InGameDevTools.Tests;

public sealed class DevToolsLangTests
{
    [Fact]
    public void Get_ReturnsLoadedValueAndFormatsArguments()
    {
        try
        {
            DevToolsLang.LoadFromJson("""{ "ui.test": "Value {0}" }""");

            Assert.Equal("Value 42", DevToolsLang.Get("ui.test", "Fallback {0}", 42));
        }
        finally
        {
            DevToolsLang.ClearForTests();
        }
    }

    [Fact]
    public void Get_FallsBackWhenKeyMissing()
    {
        try
        {
            DevToolsLang.LoadFromJson("{}");

            Assert.Equal("Fallback text", DevToolsLang.Get("missing.key", "Fallback text"));
        }
        finally
        {
            DevToolsLang.ClearForTests();
        }
    }

    [Fact]
    public void EnglishLangFile_IsFlatStringMap()
    {
        string path = FindRepoFile(Path.Combine("Resources", "assets", "ingamedevtools", "lang", "en.json"));
        JObject root = JObject.Parse(File.ReadAllText(path));

        Assert.NotEmpty(root.Properties());
        AssertFlatStringMap(root);
    }

    [Fact]
    public void AllLangFiles_AreFlatStringMapsWithKnownKeys()
    {
        string langDir = Path.GetDirectoryName(FindRepoFile(Path.Combine("Resources", "assets", "ingamedevtools", "lang", "en.json")))
            ?? throw new DirectoryNotFoundException("Could not resolve lang directory.");
        JObject english = JObject.Parse(File.ReadAllText(Path.Combine(langDir, "en.json")));
        HashSet<string> englishKeys = english.Properties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        foreach (string path in Directory.GetFiles(langDir, "*.json").Order(StringComparer.Ordinal))
        {
            JObject localized = JObject.Parse(File.ReadAllText(path));
            AssertFlatStringMap(localized);
            foreach (JProperty property in localized.Properties())
            {
                Assert.Contains(property.Name, englishKeys);
            }
        }
    }

    [Fact]
    public void LanguageOptions_ExposeOnlyShippedRenderableLanguages()
    {
        string langDir = Path.GetDirectoryName(FindRepoFile(Path.Combine("Resources", "assets", "ingamedevtools", "lang", "en.json")))
            ?? throw new DirectoryNotFoundException("Could not resolve lang directory.");
        string[] expectedCodes =
        [
            DevToolsLang.AutoLanguageCode,
            "en",
            "de",
            "es-es",
            "fr",
            "it",
            "nl",
            "pl",
            "pt-br",
            "ru",
            "tr",
            "uk"
        ];

        string[] actualCodes = DevToolsLang.LanguageOptions.Select(option => option.Code).ToArray();

        Assert.Equal(expectedCodes, actualCodes);
        foreach (string code in actualCodes.Where(code => code.Length > 0))
        {
            Assert.True(File.Exists(Path.Combine(langDir, $"{code}.json")), $"Missing lang asset for visible language '{code}'.");
        }
    }

    [Fact]
    public void ShippedNonEnglishLangFiles_DoNotContainSuspiciousQuestionMarkReplacements()
    {
        string langDir = Path.GetDirectoryName(FindRepoFile(Path.Combine("Resources", "assets", "ingamedevtools", "lang", "en.json")))
            ?? throw new DirectoryNotFoundException("Could not resolve lang directory.");

        foreach (string path in Directory.GetFiles(langDir, "*.json").Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(path).Equals("en.json", StringComparison.OrdinalIgnoreCase)) continue;

            JObject localized = JObject.Parse(File.ReadAllText(path));
            foreach (JProperty property in localized.Properties())
            {
                string value = property.Value.ToString();
                int lastContentIndex = value.TrimEnd().Length - 1;
                for (int index = 0; index < value.Length; index++)
                {
                    if (value[index] == '?' && index != lastContentIndex)
                    {
                        Assert.Fail($"{Path.GetFileName(path)}:{property.Name} contains a suspicious replacement question mark: {value}");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("es-419", "es-es")]
    [InlineData("pt-pt", "pt-br")]
    [InlineData("zh-tw", "en")]
    [InlineData("zh_Hans", "en")]
    [InlineData("ru", "ru")]
    [InlineData("uk", "uk")]
    [InlineData("ja", "en")]
    public void NormalizeLanguageCode_MapsSupportedAliasesAndFallsBackToEnglish(string input, string expected)
    {
        Assert.Equal(expected, DevToolsLang.NormalizeLanguageCode(input));
    }

    [Theory]
    [InlineData("tr", "tr")]
    [InlineData("pt-pt", "pt-br")]
    [InlineData("ru", "ru")]
    [InlineData("uk", "uk")]
    [InlineData("zh-cn", DevToolsLang.AutoLanguageCode)]
    [InlineData("not-a-language", DevToolsLang.AutoLanguageCode)]
    public void NormalizeConfiguredLanguageCode_KeepsOnlyVisibleLanguages(string input, string expected)
    {
        Assert.Equal(expected, DevToolsLang.NormalizeConfiguredLanguageCode(input));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("uk")]
    public void UsesCyrillicGlyphs_ReturnsTrueForCyrillicLanguages(string input)
    {
        Assert.True(DevToolsLang.UsesCyrillicGlyphs(input));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    [InlineData("zh-cn")]
    public void UsesCyrillicGlyphs_ReturnsFalseForOtherLanguages(string input)
    {
        Assert.False(DevToolsLang.UsesCyrillicGlyphs(input));
    }

    private static string FindRepoFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }

    private static void AssertFlatStringMap(JObject root)
    {
        foreach (JProperty property in root.Properties())
        {
            Assert.Equal(JTokenType.String, property.Value.Type);
            Assert.False(string.IsNullOrWhiteSpace(property.Name));
            Assert.False(string.IsNullOrWhiteSpace(property.Value.ToString()));
        }
    }
}
