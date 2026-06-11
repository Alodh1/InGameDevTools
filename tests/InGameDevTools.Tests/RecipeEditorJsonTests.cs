using System.Reflection;
using InGameDevTools.Animations;
using Newtonsoft.Json.Linq;

namespace InGameDevTools.Tests;

public sealed class RecipeEditorJsonTests
{
    private static readonly Type StateType = typeof(DebugWindowManager).GetNestedType("RecipeEditorState", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("RecipeEditorState not found.");

    [Fact]
    public void GetGridRows_SplitsVanillaTabSeparatedPattern()
    {
        JObject recipe = new()
        {
            ["ingredientPattern"] = "R\tL",
            ["width"] = 1,
            ["height"] = 2
        };

        List<string> rows = InvokeGetGridRows(recipe, out int width, out int height);

        Assert.Equal(1, width);
        Assert.Equal(2, height);
        Assert.Equal(["R", "L"], rows);
    }

    [Fact]
    public void CreateDefaultRecipe_CookingUsesVanillaCookingShape()
    {
        object kind = Enum.Parse(StateType.DeclaringType!.GetNestedType("RecipeEditorKind", BindingFlags.NonPublic)!, "Cooking");
        MethodInfo method = StateType.GetMethod("CreateDefaultRecipe", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CreateDefaultRecipe not found.");

        JObject recipe = Assert.IsType<JObject>(method.Invoke(null, [kind, "new-recipe"]));

        Assert.NotNull(recipe["cooksInto"]);
        Assert.Null(recipe["output"]);
        Assert.IsType<JArray>(recipe["ingredients"]);
        Assert.IsType<JArray>(recipe["ingredients"]?[0]?["validStacks"]);
    }

    [Fact]
    public void DescribeOutput_UsesCooksIntoForCookingRecipes()
    {
        MethodInfo method = StateType.GetMethod("DescribeOutput", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DescribeOutput not found.");
        JObject recipe = new()
        {
            ["cooksInto"] = new JObject
            {
                ["type"] = "item",
                ["code"] = "game:candle"
            }
        };

        string output = Assert.IsType<string>(method.Invoke(null, [recipe]));

        Assert.Equal("game:candle", output);
    }

    [Fact]
    public void ParseRecipeJson_AcceptsVintageStoryJsonDialect()
    {
        MethodInfo method = StateType.GetMethod("ParseRecipeJson", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ParseRecipeJson not found.");
        const string text = "{ code: \"test\", ingredients: [ { type: \"item\", code: \"stick\", }, ], }";

        JToken token = Assert.IsAssignableFrom<JToken>(method.Invoke(null, [text]));

        Assert.Equal("test", token["code"]?.ToString());
        Assert.Equal("stick", token["ingredients"]?[0]?["code"]?.ToString());
    }

    [Fact]
    public void CollectVariantPlaceholderNames_FindsSubstitutionAndVariantArrays()
    {
        MethodInfo method = StateType.GetMethod("CollectVariantPlaceholderNames", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CollectVariantPlaceholderNames not found.");
        JObject recipe = new()
        {
            ["output"] = new JObject
            {
                ["type"] = "item",
                ["code"] = "game:nugget-{ore}"
            },
            ["metal"] = new JArray("copper", "iron")
        };

        IEnumerable<string> names = Assert.IsAssignableFrom<IEnumerable<string>>(method.Invoke(null, [recipe]));

        Assert.Contains("ore", names);
        Assert.Contains("metal", names);
    }

    private static List<string> InvokeGetGridRows(JObject recipe, out int width, out int height)
    {
        MethodInfo method = StateType.GetMethod("GetGridRows", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GetGridRows not found.");
        object?[] parameters = [recipe, 0, 0];

        object? result = method.Invoke(null, parameters);

        width = Assert.IsType<int>(parameters[1]);
        height = Assert.IsType<int>(parameters[2]);
        return Assert.IsType<List<string>>(result);
    }
}
