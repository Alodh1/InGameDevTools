using InGameDevTools.Utils;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace InGameDevTools.Tests;

public sealed class DevToolsAssetIndexerTests
{
    private sealed class FakeAsset(string domain, string path) : IAsset
    {
        public string Name => System.IO.Path.GetFileName(path);
        public AssetLocation Location { get; } = new(domain, path);
        public IAssetOrigin Origin { get; set; } = null!;
        public byte[] Data { get; set; } = [];
        public bool IsPatched { get; set; }

        public T ToObject<T>(JsonSerializerSettings? settings = null) => throw new NotSupportedException();
        public string ToText() => "";
        public BitmapRef ToBitmap(ICoreClientAPI capi) => throw new NotSupportedException();
        public bool IsLoaded() => true;
    }

    private static FakeAsset Asset(string path) => new("game", path);

    [Fact]
    public void EnsureIndexed_StartsOnceWhenIdleAndProcessesBatches()
    {
        DevToolsAssetIndexer indexer = new(batchSize: 2);
        int started = 0;

        indexer.EnsureIndexed(
            () =>
            {
                started++;
                indexer.Begin();
                indexer.AddAssets([Asset("a.json"), Asset("b.json"), Asset("c.json")], _ => true);
            },
            () => indexer.TryProcessBatch(_ => { }, () => { }, () => { }, out _));

        Assert.Equal(1, started);
        Assert.True(indexer.IsIndexing);
        Assert.Equal(2, indexer.Position);

        indexer.EnsureIndexed(
            () => started++,
            () => indexer.TryProcessBatch(_ => { }, () => { }, () => { }, out _));

        Assert.Equal(1, started);
        Assert.True(indexer.IsReady);

        // Ready: neither start nor processBatch runs again.
        indexer.EnsureIndexed(() => started++, () => started += 10);
        Assert.Equal(1, started);
    }

    [Fact]
    public void Begin_ResetsPendingAssetsPositionAndDedupe()
    {
        DevToolsAssetIndexer indexer = new(batchSize: 10);
        indexer.Begin();
        indexer.AddAssets([Asset("a.json")], _ => true);
        indexer.TryProcessBatch(_ => { }, () => { }, () => { }, out _);
        Assert.True(indexer.IsReady);
        Assert.Equal(1, indexer.Position);

        indexer.Begin();

        Assert.True(indexer.IsIndexing);
        Assert.Empty(indexer.PendingAssets);
        Assert.Equal(0, indexer.Position);

        // The same location can be added again after Begin cleared the dedupe set.
        indexer.AddAssets([Asset("a.json")], _ => true);
        Assert.Single(indexer.PendingAssets);
    }

    [Fact]
    public void AddAssets_DeduplicatesByLocationAcrossSources()
    {
        DevToolsAssetIndexer indexer = new(batchSize: 10);
        indexer.Begin();

        indexer.AddAssets([Asset("worldgen/a.json"), Asset("worldgen/b.json")], _ => true);
        indexer.AddAssets([Asset("worldgen/A.JSON"), Asset("worldgen/c.json")], _ => true);

        Assert.Equal(3, indexer.PendingAssets.Count);
    }

    [Fact]
    public void AddAssets_HonorsIncludeFilter()
    {
        DevToolsAssetIndexer indexer = new(batchSize: 10);
        indexer.Begin();

        indexer.AddAssets(
            [Asset("a.json"), Asset("b.txt")],
            asset => asset!.Location.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

        Assert.Single(indexer.PendingAssets);
        Assert.Equal("a.json", indexer.PendingAssets[0].Location.Path);
    }

    [Fact]
    public void TryProcessBatch_ReportsProgressThenCompletes()
    {
        DevToolsAssetIndexer indexer = new(batchSize: 2);
        indexer.Begin();
        indexer.AddAssets([Asset("a.json"), Asset("b.json"), Asset("c.json")], _ => true);

        List<string> indexed = [];
        int completed = 0;
        int progressed = 0;

        Assert.True(indexer.TryProcessBatch(asset => indexed.Add(asset.Location.Path), () => completed++, () => progressed++, out _));
        Assert.Equal(["a.json", "b.json"], indexed);
        Assert.Equal(0, completed);
        Assert.Equal(1, progressed);
        Assert.True(indexer.IsIndexing);

        Assert.True(indexer.TryProcessBatch(asset => indexed.Add(asset.Location.Path), () => completed++, () => progressed++, out _));
        Assert.Equal(["a.json", "b.json", "c.json"], indexed);
        Assert.Equal(1, completed);
        Assert.Equal(1, progressed);
        Assert.True(indexer.IsReady);
        Assert.Empty(indexer.PendingAssets);
    }

    [Fact]
    public void TryProcessBatch_MarksReadyBeforeCompleteRuns()
    {
        DevToolsAssetIndexer indexer = new(batchSize: 5);
        indexer.Begin();
        indexer.AddAssets([Asset("a.json")], _ => true);

        bool readyDuringComplete = false;
        indexer.TryProcessBatch(_ => { }, () => readyDuringComplete = indexer.IsReady, () => { }, out _);

        Assert.True(readyDuringComplete);
    }

    [Fact]
    public void TryProcessBatch_FailsAndReturnsErrorWhenIndexingThrows()
    {
        DevToolsAssetIndexer indexer = new(batchSize: 5);
        indexer.Begin();
        indexer.AddAssets([Asset("a.json")], _ => true);

        bool ok = indexer.TryProcessBatch(
            _ => throw new InvalidOperationException("boom"),
            () => { },
            () => { },
            out Exception? error);

        Assert.False(ok);
        Assert.True(indexer.IsFailed);
        Assert.IsType<InvalidOperationException>(error);

        // Failed indexers stay failed: EnsureIndexed must not restart or process.
        int calls = 0;
        indexer.EnsureIndexed(() => calls++, () => calls++);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void TryProcessBatch_IsNoOpWhenNotIndexing()
    {
        DevToolsAssetIndexer indexer = new(batchSize: 5);

        bool ok = indexer.TryProcessBatch(_ => throw new InvalidOperationException(), () => { }, () => { }, out Exception? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(indexer.IsIdle);
    }

    [Fact]
    public void Fail_MarksIndexerFailed()
    {
        DevToolsAssetIndexer indexer = new(batchSize: 5);
        indexer.Begin();
        indexer.AddAssets([Asset("a.json")], _ => true);

        indexer.Fail();

        Assert.True(indexer.IsFailed);
        Assert.Empty(indexer.PendingAssets);
    }
}
