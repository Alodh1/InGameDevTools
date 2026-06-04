using Vintagestory.API.Common;

namespace InGameDevTools.Utils;

internal static class DevToolsBatching
{
    public static void AddAssets(
        IEnumerable<IAsset> assets,
        ICollection<IAsset> destination,
        System.Func<IAsset?, bool> include)
    {
        foreach (IAsset? asset in assets)
        {
            if (asset?.Location == null || !include(asset)) continue;
            destination.Add(asset);
        }
    }

    public static void AddAssets(
        IEnumerable<IAsset> assets,
        ICollection<IAsset> destination,
        ISet<string> indexedLocations,
        System.Func<IAsset?, bool> include)
    {
        foreach (IAsset? asset in assets)
        {
            if (asset?.Location == null || !include(asset)) continue;
            string key = asset.Location.ToString();
            if (indexedLocations.Add(key))
            {
                destination.Add(asset);
            }
        }
    }

    public static void AddAssetSource(
        string label,
        System.Func<IEnumerable<IAsset>> getAssets,
        ICollection<IAsset> destination,
        ISet<string> indexedLocations,
        System.Func<IAsset?, bool> include,
        DevToolsEditorDiagnostics diagnostics)
    {
        try
        {
            AddAssets(getAssets(), destination, indexedLocations, include);
        }
        catch (Exception exception)
        {
            diagnostics.Warning($"Skipped {label}: {exception.Message}", exception.ToString());
        }
    }

    public static void SortAssetsByLocation(List<IAsset> assets)
    {
        assets.Sort((left, right) => string.Compare(left.Location.ToString(), right.Location.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    public static void ProcessBatch<T>(
        IReadOnlyList<T> items,
        ref int index,
        int batchSize,
        Action<T> processItem,
        Action complete,
        Action progress)
    {
        int processed = 0;
        while (processed < batchSize && index < items.Count)
        {
            processItem(items[index++]);
            processed++;
        }

        if (index >= items.Count)
        {
            complete();
        }
        else
        {
            progress();
        }
    }
}
