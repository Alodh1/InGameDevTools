using InGameDevTools.Utils;

namespace InGameDevTools.Tests;

public sealed class DevToolsBatchingTests
{
    [Fact]
    public void ProcessBatch_ProcessesOnlyBatchSizeAndReportsProgress()
    {
        int index = 0;
        List<int> processed = [];
        int completed = 0;
        int progressed = 0;

        DevToolsBatching.ProcessBatch(
            [1, 2, 3],
            ref index,
            2,
            processed.Add,
            () => completed++,
            () => progressed++);

        Assert.Equal([1, 2], processed);
        Assert.Equal(2, index);
        Assert.Equal(0, completed);
        Assert.Equal(1, progressed);
    }

    [Fact]
    public void ProcessBatch_CompletesWhenLastItemIsProcessed()
    {
        int index = 2;
        List<int> processed = [];
        int completed = 0;
        int progressed = 0;

        DevToolsBatching.ProcessBatch(
            [1, 2, 3],
            ref index,
            2,
            processed.Add,
            () => completed++,
            () => progressed++);

        Assert.Equal([3], processed);
        Assert.Equal(3, index);
        Assert.Equal(1, completed);
        Assert.Equal(0, progressed);
    }
}
