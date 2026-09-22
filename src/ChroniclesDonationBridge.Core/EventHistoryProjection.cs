namespace ChroniclesDonationBridge.Core;

public static class EventHistoryProjection
{
    public static IReadOnlyList<EventHistoryEntry> LatestPerCommand(
        IEnumerable<EventHistoryEntry> entries,
        int maximum = 500)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (maximum <= 0) return [];

        var latest = new Dictionary<string, IndexedEntry>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in entries)
        {
            var key = LogicalKey(entry);
            if (!latest.TryGetValue(key, out var current) || entry.Timestamp > current.Entry.Timestamp)
            {
                latest[key] = new IndexedEntry(entry, index);
            }
            index++;
        }

        return latest.Values
            .OrderByDescending(item => SortTime(item.Entry))
            .ThenByDescending(item => item.Entry.Timestamp)
            .ThenBy(item => item.Index)
            .Take(maximum)
            .Select(item => item.Entry)
            .ToList();
    }

    public static bool IsSameLogicalEvent(EventHistoryEntry? left, EventHistoryEntry? right)
    {
        if (left is null || right is null) return false;
        return string.Equals(LogicalKey(left), LogicalKey(right), StringComparison.Ordinal);
    }

    public static DateTimeOffset SortTime(EventHistoryEntry entry) =>
        entry.DonationCreatedAt == default ? entry.Timestamp : entry.DonationCreatedAt;

    private static string LogicalKey(EventHistoryEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.DonationId) && !string.IsNullOrWhiteSpace(entry.ActionId)
            ? "donation:" + entry.DonationId + "\0action:" + entry.ActionId
            : string.IsNullOrWhiteSpace(entry.CommandId)
                ? "event:" + entry.EventId
                : "command:" + entry.CommandId;

    private sealed record IndexedEntry(EventHistoryEntry Entry, int Index);
}
