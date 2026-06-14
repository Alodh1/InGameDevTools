using InGameDevTools.Animations;

namespace InGameDevTools.Tests;

public sealed class VanillaAnimatorFirstPersonPreviewTests
{
    [Fact]
    public void GetVanillaPreviewJointIdForVertex_ReadsPerVertexJointIds()
    {
        int[] jointIds = [11, 12, 13, 14, 15];

        Assert.Equal(13, DebugWindowManager.GetVanillaPreviewJointIdForVertex(jointIds, verticesCount: 5, vertexIndex: 2));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(8)]
    public void GetVanillaPreviewJointIdForVertex_RejectsOutOfRangeVertices(int vertexIndex)
    {
        int[] jointIds = [21, 22, 23];

        Assert.Equal(0, DebugWindowManager.GetVanillaPreviewJointIdForVertex(jointIds, verticesCount: 3, vertexIndex));
    }
}
