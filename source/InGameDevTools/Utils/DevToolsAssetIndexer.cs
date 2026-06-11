using Vintagestory.API.Common;

namespace InGameDevTools.Utils;

/// <summary>
/// Shared batched asset-index state machine (Idle -> Indexing -> Ready/Failed) used by the
/// source-backed editors. Owns the pending asset list, location dedupe set, batch position, and
/// state transitions; editors keep their own entry models, status text, and diagnostics.
/// </summary>
internal sealed class DevToolsAssetIndexer(int batchSize)
{
    public enum IndexState
    {
        Idle,
        Indexing,
        Ready,
        Failed
    }

    private readonly HashSet<string> _indexedLocations = new(StringComparer.OrdinalIgnoreCase);
    private int _position;

    public IndexState State { get; private set; } = IndexState.Idle;
    public List<IAsset> PendingAssets { get; } = [];
    public int Position => _position;

    public bool IsIdle => State == IndexState.Idle;
    public bool IsIndexing => State == IndexState.Indexing;
    public bool IsReady => State == IndexState.Ready;
    public bool IsFailed => State == IndexState.Failed;

    /// <summary>Standard per-frame entry point: start when idle, then advance one batch.</summary>
    public void EnsureIndexed(Action start, Action processBatch)
    {
        if (IsReady || IsFailed) return;
        if (IsIdle) start();
        processBatch();
    }

    /// <summary>Clears pending assets and dedupe state and enters Indexing.</summary>
    public void Begin()
    {
        State = IndexState.Indexing;
        _position = 0;
        PendingAssets.Clear();
        _indexedLocations.Clear();
    }

    public void AddAssets(IEnumerable<IAsset> assets, System.Func<IAsset?, bool> include)
    {
        DevToolsBatching.AddAssets(assets, PendingAssets, _indexedLocations, include);
    }

    public void AddSource(
        string label,
        System.Func<IEnumerable<IAsset>> getAssets,
        System.Func<IAsset?, bool> include,
        DevToolsEditorDiagnostics diagnostics)
    {
        DevToolsBatching.AddAssetSource(label, getAssets, PendingAssets, _indexedLocations, include, diagnostics);
    }

    public void SortPendingByLocation()
    {
        DevToolsBatching.SortAssetsByLocation(PendingAssets);
    }

    /// <summary>
    /// Advances one batch. Marks Ready before invoking <paramref name="complete"/>; any exception
    /// from indexing or completion marks the index Failed and is handed back for editor reporting.
    /// </summary>
    public bool TryProcessBatch(Action<IAsset> indexAsset, Action complete, Action progress, out Exception? error)
    {
        error = null;
        if (!IsIndexing) return true;

        try
        {
            DevToolsBatching.ProcessBatch(
                PendingAssets,
                ref _position,
                batchSize,
                indexAsset,
                () =>
                {
                    State = IndexState.Ready;
                    complete();
                },
                progress);
            return true;
        }
        catch (Exception exception)
        {
            State = IndexState.Failed;
            error = exception;
            return false;
        }
    }

    /// <summary>Marks the index failed (used by editor-level draw guards).</summary>
    public void Fail()
    {
        State = IndexState.Failed;
    }
}
