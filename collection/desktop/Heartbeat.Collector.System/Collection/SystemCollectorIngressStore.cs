using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Collector.System.Collection;

internal sealed record PendingSystemSegmentIngress(Guid EntryId, ForegroundSegmentSnapshot Snapshot);
internal sealed record PendingSystemInputIngress(Guid EntryId, InputEventItem Item);
internal sealed record SystemInputIngressGap(
    Guid GapId,
    DateTimeOffset Start,
    DateTimeOffset End,
    int EstimatedFactsLost);
internal sealed record PendingSystemInputGapIngress(Guid EntryId, SystemInputIngressGap Gap);

internal enum SystemInputIngressStageResult
{
    EventStaged,
    GapStaged
}

/// <summary>
/// Append-only durable first stage for observations produced by native System callbacks. Entries
/// are compacted away only after the Collector Protocol outbox accepts the same Fact identity and
/// revision. A crash between those commits replays an idempotent duplicate.
/// </summary>
internal sealed class SystemCollectorIngressStore
{
    private const string SegmentKind = "segment";
    private const string InputEventKind = "input_event";
    private const string InputGapKind = "input_gap";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly int _inputCapacity;
    private readonly List<PendingSystemSegmentIngress> _segments;
    private readonly List<PendingSystemInputIngress> _inputEvents;
    private readonly List<PendingSystemInputGapIngress> _inputGaps;

    private SystemCollectorIngressStore(
        string path,
        int inputCapacity,
        List<PendingSystemSegmentIngress> segments,
        List<PendingSystemInputIngress> inputEvents,
        List<PendingSystemInputGapIngress> inputGaps)
    {
        _path = path;
        _inputCapacity = inputCapacity;
        _segments = segments;
        _inputEvents = inputEvents;
        _inputGaps = inputGaps;
    }

    public static SystemCollectorIngressStore Open(string path, int inputCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (inputCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputCapacity));
        var fullPath = Path.GetFullPath(path);
        var segments = new List<PendingSystemSegmentIngress>();
        var inputEvents = new List<PendingSystemInputIngress>();
        var inputGaps = new List<PendingSystemInputGapIngress>();
        if (File.Exists(fullPath))
        {
            var lines = File.ReadAllLines(fullPath, Encoding.UTF8);
            for (var index = 0; index < lines.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(lines[index]))
                    continue;
                StoredIngressEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<StoredIngressEntry>(lines[index], JsonOptions);
                }
                catch (JsonException) when (index == lines.Length - 1)
                {
                    // A crash may leave only the final append incomplete. It was never confirmed
                    // durable to its producer and is not part of the restartable prefix.
                    break;
                }
                if (entry is null || entry.EntryId == Guid.Empty)
                    throw new InvalidDataException("System Collector ingress entry is invalid.");
                switch (entry.Kind)
                {
                    case SegmentKind when entry.Segment is not null && entry.InputEvent is null:
                        segments.Add(new PendingSystemSegmentIngress(entry.EntryId, entry.Segment));
                        break;
                    case InputEventKind when entry.InputEvent is not null && entry.Segment is null:
                        inputEvents.Add(new PendingSystemInputIngress(entry.EntryId, entry.InputEvent));
                        break;
                    case InputGapKind when entry.InputGap is not null &&
                                                entry.Segment is null &&
                                                entry.InputEvent is null &&
                                                entry.InputGap.GapId != Guid.Empty &&
                                                entry.InputGap.End > entry.InputGap.Start &&
                                                entry.InputGap.EstimatedFactsLost > 0:
                        inputGaps.Add(new PendingSystemInputGapIngress(entry.EntryId, entry.InputGap));
                        break;
                    default:
                        throw new InvalidDataException("System Collector ingress entry kind is invalid.");
                }
            }
        }
        return new SystemCollectorIngressStore(fullPath, inputCapacity, segments, inputEvents, inputGaps);
    }

    public void Enqueue(ForegroundSegmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            var pending = new PendingSystemSegmentIngress(Guid.CreateVersion7(), snapshot);
            Append(new StoredIngressEntry(pending.EntryId, SegmentKind, snapshot, null, null));
            _segments.Add(pending);
        }
    }

    public SystemInputIngressStageResult StageInputEvent(InputEventItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            var copy = new InputEventItem
            {
                Id = item.Id,
                EventType = item.EventType,
                CodeSet = item.CodeSet,
                Code = item.Code,
                Timestamp = item.Timestamp
            };
            if (_inputEvents.Count >= _inputCapacity)
            {
                var gap = new SystemInputIngressGap(
                    Guid.CreateVersion7(),
                    copy.Timestamp,
                    copy.Timestamp.AddTicks(1),
                    1);
                var pendingGap = new PendingSystemInputGapIngress(Guid.CreateVersion7(), gap);
                Append(new StoredIngressEntry(
                    pendingGap.EntryId,
                    InputGapKind,
                    null,
                    null,
                    gap));
                _inputGaps.Add(pendingGap);
                return SystemInputIngressStageResult.GapStaged;
            }
            var pending = new PendingSystemInputIngress(Guid.CreateVersion7(), copy);
            Append(new StoredIngressEntry(pending.EntryId, InputEventKind, null, copy, null));
            _inputEvents.Add(pending);
            return SystemInputIngressStageResult.EventStaged;
        }
    }

    public bool TryEnqueue(InputEventItem item) =>
        StageInputEvent(item) == SystemInputIngressStageResult.EventStaged;

    public IReadOnlyList<PendingSystemSegmentIngress> PeekSegments(int limit)
    {
        lock (_gate)
            return _segments.Take(limit).ToArray();
    }

    public IReadOnlyList<PendingSystemInputIngress> PeekInputEvents(int limit)
    {
        lock (_gate)
            return _inputEvents.Take(limit).ToArray();
    }

    public IReadOnlyList<PendingSystemInputGapIngress> PeekInputGaps(int limit)
    {
        lock (_gate)
            return _inputGaps.Take(limit).ToArray();
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
                return _segments.Count != 0 || _inputEvents.Count != 0 || _inputGaps.Count != 0;
        }
    }

    public int PendingInputGapCount
    {
        get
        {
            lock (_gate)
                return _inputGaps.Sum(item => item.Gap.EstimatedFactsLost);
        }
    }

    internal IReadOnlySet<Guid> PendingFactIds
    {
        get
        {
            lock (_gate)
                return _segments.Select(item => item.Snapshot.FactId)
                    .Concat(_inputEvents.Select(item => item.Item.Id))
                    .ToHashSet();
        }
    }

    public void AcknowledgeSegments(IReadOnlyList<PendingSystemSegmentIngress> entries)
    {
        lock (_gate)
        {
            EnsurePrefix(_segments.Select(item => item.EntryId), entries.Select(item => item.EntryId));
            Rewrite(_segments.Skip(entries.Count), _inputEvents, _inputGaps);
            _segments.RemoveRange(0, entries.Count);
        }
    }

    public void AcknowledgeInputEvents(IReadOnlyList<PendingSystemInputIngress> entries)
    {
        lock (_gate)
        {
            EnsurePrefix(_inputEvents.Select(item => item.EntryId), entries.Select(item => item.EntryId));
            Rewrite(_segments, _inputEvents.Skip(entries.Count), _inputGaps);
            _inputEvents.RemoveRange(0, entries.Count);
        }
    }

    public void AcknowledgeInputGaps(IReadOnlyList<PendingSystemInputGapIngress> entries)
    {
        lock (_gate)
        {
            EnsurePrefix(_inputGaps.Select(item => item.EntryId), entries.Select(item => item.EntryId));
            Rewrite(_segments, _inputEvents, _inputGaps.Skip(entries.Count));
            _inputGaps.RemoveRange(0, entries.Count);
        }
    }

    private static void EnsurePrefix(IEnumerable<Guid> pending, IEnumerable<Guid> acknowledged)
    {
        var expected = acknowledged.ToArray();
        if (!pending.Take(expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException("System Collector ingress must be acknowledged in durable order.");
    }

    private void Append(StoredIngressEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("System Collector ingress path has no directory."));
        using var stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private void Rewrite(
        IEnumerable<PendingSystemSegmentIngress> segments,
        IEnumerable<PendingSystemInputIngress> inputEvents,
        IEnumerable<PendingSystemInputGapIngress> inputGaps)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("System Collector ingress path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                foreach (var entry in segments.Select(item =>
                             new StoredIngressEntry(item.EntryId, SegmentKind, item.Snapshot, null, null))
                         .Concat(inputEvents.Select(item =>
                             new StoredIngressEntry(item.EntryId, InputEventKind, null, item.Item, null)))
                         .Concat(inputGaps.Select(item =>
                             new StoredIngressEntry(item.EntryId, InputGapKind, null, null, item.Gap))))
                    writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed record StoredIngressEntry(
        Guid EntryId,
        string Kind,
        ForegroundSegmentSnapshot? Segment,
        InputEventItem? InputEvent,
        SystemInputIngressGap? InputGap);
}
