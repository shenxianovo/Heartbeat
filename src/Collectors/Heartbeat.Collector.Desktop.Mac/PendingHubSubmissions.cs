using System.Text.Json;
using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop.Mac;

internal sealed record SubmissionRoute(CollectorDeclaration Collector, TrackDeclaration Track);

internal sealed record PendingSubmissionBatch(
    SubmissionRoute Route,
    IReadOnlyList<RecordSnapshot> Records)
{
    public HubSubmission ToSubmission() => new(Route.Collector, Route.Track, Records);
}

/// <summary>
/// Hub 接管前的进程内缓冲。这里没有持久暂存，也没有积压上限后的行为，是当前实现有意的边界：
/// Collector 侧如何保护未交接数据、积压到多少之后怎么办，都还是未决问题，见
/// docs/recording-open-questions.md 的「Collector 到 Hub 的未交接数据」。要在这里加落盘或丢弃策略，先过那一节。
/// </summary>
internal sealed class PendingHubSubmissions
{
    private const int MaximumBatchSize = 500;
    private const int MaximumBatchBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, (SubmissionRoute Route, RecordSnapshot Record)> _records = [];

    public void Stage(SubmissionRoute route, RecordSnapshot record)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(record.Id, out var existing) && existing.Route != route)
            {
                throw new InvalidOperationException("A pending Record cannot move to another Track.");
            }

            _records[record.Id] = (route, record);
        }
    }

    public IReadOnlyList<PendingSubmissionBatch> ReadBatches()
    {
        (SubmissionRoute Route, RecordSnapshot Record)[] snapshot;
        lock (_gate)
        {
            snapshot = _records.Values.ToArray();
        }

        var result = new List<PendingSubmissionBatch>();
        foreach (var group in snapshot.GroupBy(item => item.Route))
        {
            var current = new List<RecordSnapshot>();
            // Empty records already contributes both brackets. Each element contributes its
            // encoded bytes, plus one comma after the first element.
            var envelopeBytes = EncodedSize(group.Key, []);
            var currentBytes = envelopeBytes;
            foreach (var item in group)
            {
                var recordBytes = JsonSerializer.SerializeToUtf8Bytes(item.Record, JsonOptions).Length;
                if ((long)envelopeBytes + recordBytes > MaximumBatchBytes)
                    throw new InvalidOperationException($"Record {item.Record.Id} exceeds the Hub submission limit.");

                var separatorBytes = current.Count == 0 ? 0 : 1;
                if (current.Count == MaximumBatchSize ||
                    currentBytes + recordBytes + separatorBytes > MaximumBatchBytes)
                {
                    result.Add(new PendingSubmissionBatch(group.Key, current.ToArray()));
                    current.Clear();
                    currentBytes = envelopeBytes;
                    separatorBytes = 0;
                }
                current.Add(item.Record);
                currentBytes += recordBytes + separatorBytes;
            }
            if (current.Count != 0)
                result.Add(new PendingSubmissionBatch(group.Key, current.ToArray()));
        }
        return result;
    }

    public void Confirm(PendingSubmissionBatch batch)
    {
        lock (_gate)
        {
            foreach (var sent in batch.Records)
            {
                if (_records.TryGetValue(sent.Id, out var current) &&
                    current.Route == batch.Route && SameSnapshot(current.Record, sent))
                {
                    _records.Remove(sent.Id);
                }
            }
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    private static int EncodedSize(SubmissionRoute route, IReadOnlyList<RecordSnapshot> records) =>
        JsonSerializer.SerializeToUtf8Bytes(new HubSubmission(route.Collector, route.Track, records), JsonOptions).Length;

    private static bool SameSnapshot(RecordSnapshot left, RecordSnapshot right) =>
        left.Id == right.Id &&
        left.StartedAt == right.StartedAt &&
        left.EndedAt == right.EndedAt &&
        left.ObservedAt == right.ObservedAt &&
        JsonElement.DeepEquals(left.Value, right.Value);
}
