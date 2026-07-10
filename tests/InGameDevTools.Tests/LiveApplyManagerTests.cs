using InGameDevTools.Animations;

namespace InGameDevTools.Tests;

public sealed class LiveApplyManagerTests
{
    [Fact]
    public void Revert_ReleasesSnapshotSoNextApplyRecaptures()
    {
        DebugWindowManager.DevToolsLiveApplyManager manager = new();
        int captures = 0;
        int runtimeValue = 0;

        DebugWindowManager.LivePatchSnapshot Capture()
        {
            captures++;
            int original = runtimeValue;
            return new(() => runtimeValue = original, null, null);
        }

        manager.Apply("target", "Target", Capture, () => runtimeValue = 10, "applied");
        Assert.Equal(1, captures);
        Assert.Equal(10, runtimeValue);

        manager.Revert("target");
        Assert.Equal(0, runtimeValue);

        manager.Apply("target", "Target", Capture, () => runtimeValue = 20, "applied");
        Assert.Equal(2, captures);
        Assert.Equal(20, runtimeValue);
    }

    [Fact]
    public void RevertAll_ReleasesTrackedButUnappliedSnapshots()
    {
        DebugWindowManager.DevToolsLiveApplyManager manager = new();
        int captures = 0;
        Func<DebugWindowManager.LivePatchSnapshot> capture = () =>
        {
            captures++;
            return new(() => { }, null, null);
        };

        manager.TrackOriginal("target", "Target", capture);
        manager.RevertAll();
        manager.TrackOriginal("target", "Target", capture);

        Assert.Equal(2, captures);
    }
}
