using InGameDevTools.Utils;

namespace InGameDevTools.Tests;

public sealed class ParticleAssetScanTests
{
    [Theory]
    [InlineData("blocktypes/wood/torch.json")]
    [InlineData("itemtypes/tool.json")]
    [InlineData("entities/player.json")]
    [InlineData("config/custom-particles.json")]
    [InlineData(@"blocktypes\wood\torch.JSON")]
    public void EmbeddedParticleScan_IncludesRuntimeDefinitionCategories(string path)
    {
        Assert.True(ParticleEffectsManager.IsEmbeddedParticleAssetPath(path));
    }

    [Theory]
    [InlineData("worldgen/schematics/story/huge.json")]
    [InlineData("shapes/block/clutter.json")]
    [InlineData("recipes/grid/foo.json")]
    [InlineData("lang/en.json")]
    [InlineData("blocktypes/not-json.txt")]
    public void EmbeddedParticleScan_ExcludesUnrelatedJsonCorpora(string path)
    {
        Assert.False(ParticleEffectsManager.IsEmbeddedParticleAssetPath(path));
    }
}
