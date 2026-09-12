namespace Heartbeat.Collector.Desktop.Mac;

internal sealed class PendingForegroundRecords
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, ForegroundRecord> _records = [];

    public void Stage(ForegroundRecord record)
    {
        lock (_gate)
        {
            _records[record.Id] = record;
        }
    }

    public IReadOnlyList<ForegroundRecord> ReadBatch()
    {
        lock (_gate)
        {
            return [.. _records.Values];
        }
    }

    public void Confirm(ForegroundRecord record)
    {
        lock (_gate)
        {
            // A request only confirms its snapshot, not progress staged while it was in flight.
            if (_records.TryGetValue(record.Id, out var pending) && pending == record)
            {
                _records.Remove(record.Id);
            }
        }
    }
}
