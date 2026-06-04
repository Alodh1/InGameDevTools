namespace InGameDevTools.Utils;

internal static class DevToolsBatching
{
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
