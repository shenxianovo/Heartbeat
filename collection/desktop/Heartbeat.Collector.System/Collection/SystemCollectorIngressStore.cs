using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Collector.System.Collection;

internal sealed record PendingSystemSegmentIngress(
    Guid EntryId,
    IReadOnlyList<ForegroundSegmentSnapshot> Snapshots);
internal sealed record PendingSystemInputDelivery(
    Guid EntryId,
    InputEventItem? Item = null,
    SystemInputIngressGap? Gap = null);
internal sealed record SystemSegmentIngressGap(
    Guid GapId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Reason);
internal sealed record PendingSystemSegmentGapIngress(Guid EntryId, SystemSegmentIngressGap Gap);
internal sealed record SystemInputIngressGap(
    Guid GapId,
    DateTimeOffset Start,
    DateTimeOffset End,
    int EstimatedFactsLost);

internal enum SystemInputIngressStageResult
{
    EventStaged,
    GapStaged
}

/// <summary>
/// Append-only durable first stage for System observations. A segment batch and its active
/// checkpoint are one journal mutation; Fact ACK compaction never removes the checkpoint. Input
/// capacity likewise stages either the Event or its Gap in one mutation.
/// </summary>
internal sealed class SystemCollectorIngressStore
{
    private const string SegmentKind = "segment";
    private const string SegmentBatchKind = "segment_batch";
    private const string SegmentRecoveryKind = "segment_recovery";
    private const string SegmentGapKind = "segment_gap";
    private const string SegmentCheckpointKind = "segment_checkpoint";
    private const string InputEventKind = "input_event";
    private const string InputGapKind = "input_gap";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly int _inputCapacity;
    private readonly List<PendingSystemSegmentIngress> _segmentBatches;
    private readonly List<PendingSystemSegmentGapIngress> _segmentGaps;
    private readonly List<PendingSystemInputDelivery> _inputDeliveries;
    private ForegroundSegmentSnapshot? _activeSegmentCheckpoint;

    private SystemCollectorIngressStore(
        string path,
        int inputCapacity,
        List<PendingSystemSegmentIngress> segmentBatches,
        List<PendingSystemSegmentGapIngress> segmentGaps,
        List<PendingSystemInputDelivery> inputDeliveries,
        ForegroundSegmentSnapshot? activeSegmentCheckpoint)
    {
        _path = path;
        _inputCapacity = inputCapacity;
        _segmentBatches = segmentBatches;
        _segmentGaps = segmentGaps;
        _inputDeliveries = inputDeliveries;
        _activeSegmentCheckpoint = activeSegmentCheckpoint;
    }

    public static SystemCollectorIngressStore Open(string path, int inputCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (inputCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputCapacity));
        var fullPath = Path.GetFullPath(path);
        var segmentBatches = new List<PendingSystemSegmentIngress>();
        var segmentGaps = new List<PendingSystemSegmentGapIngress>();
        var inputDeliveries = new List<PendingSystemInputDelivery>();
        ForegroundSegmentSnapshot? activeSegmentCheckpoint = null;
        if (File.Exists(fullPath))
        {
            var bytes = File.ReadAllBytes(fullPath);
            var lineStart = 0;
            while (lineStart < bytes.Length)
            {
                var newline = Array.IndexOf(bytes, (byte)'\n', lineStart);
                var lineEnd = newline >= 0 ? newline : bytes.Length;
                var nextLineStart = newline >= 0 ? newline + 1 : bytes.Length;
                var line = Encoding.UTF8.GetString(bytes, lineStart, lineEnd - lineStart).TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    lineStart = nextLineStart;
                    continue;
                }
                StoredIngressEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<StoredIngressEntry>(line, JsonOptions);
                }
                catch (JsonException) when (nextLineStart == bytes.Length)
                {
                    RepairMalformedTail(fullPath, lineStart);
                    break;
                }
                if (entry is null || entry.EntryId == Guid.Empty)
                    throw new InvalidDataException("System Collector ingress entry is invalid.");
                switch (entry.Kind)
                {
                    case SegmentKind when entry.Segment is not null &&
                                                entry.SegmentBatch is null &&
                                                entry.InputEvent is null:
                        segmentBatches.Add(new PendingSystemSegmentIngress(
                            entry.EntryId,
                            [entry.Segment]));
                        break;
                    case SegmentBatchKind when ValidSegmentBatch(entry.SegmentBatch):
                        segmentBatches.Add(new PendingSystemSegmentIngress(
                            entry.EntryId,
                            entry.SegmentBatch!));
                        break;
                    case SegmentRecoveryKind when ValidSegmentBatch(entry.SegmentBatch) &&
                                                    ValidSegmentGapOrNull(entry.SegmentGap):
                        segmentBatches.Add(new PendingSystemSegmentIngress(
                            entry.EntryId,
                            entry.SegmentBatch!));
                        if (entry.SegmentGap is not null)
                            segmentGaps.Add(new PendingSystemSegmentGapIngress(
                                entry.EntryId,
                                entry.SegmentGap));
                        break;
                    case SegmentGapKind when ValidSegmentGap(entry.SegmentGap):
                        segmentGaps.Add(new PendingSystemSegmentGapIngress(entry.EntryId, entry.SegmentGap!));
                        break;
                    case SegmentCheckpointKind when entry.MutatesCheckpoint:
                        break;
                    case InputEventKind when entry.InputEvent is not null && entry.Segment is null:
                        inputDeliveries.Add(new PendingSystemInputDelivery(
                            entry.EntryId,
                            Item: entry.InputEvent));
                        break;
                    case InputGapKind when ValidInputGap(entry.InputGap) &&
                                                entry.Segment is null &&
                                                entry.InputEvent is null:
                        inputDeliveries.Add(new PendingSystemInputDelivery(
                            entry.EntryId,
                            Gap: entry.InputGap));
                        break;
                    default:
                        throw new InvalidDataException("System Collector ingress entry kind is invalid.");
                }
                if (entry.MutatesCheckpoint)
                {
                    if (entry.Checkpoint is { IsFinal: true })
                        throw new InvalidDataException("System Collector active checkpoint cannot be final.");
                    activeSegmentCheckpoint = entry.Checkpoint;
                }
                if (newline < 0)
                    RepairMissingTailNewline(fullPath);
                lineStart = nextLineStart;
            }
        }
        return new SystemCollectorIngressStore(
            fullPath,
            inputCapacity,
            segmentBatches,
            segmentGaps,
            inputDeliveries,
            activeSegmentCheckpoint);
    }

    public void Enqueue(ForegroundSegmentSnapshot snapshot) => StageSegmentBatch([snapshot]);

    public void StageSegmentBatch(IReadOnlyList<ForegroundSegmentSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
            return;
        var copy = snapshots.ToArray();
        lock (_gate)
        {
            var pending = new PendingSystemSegmentIngress(Guid.CreateVersion7(), copy);
            var checkpoint = copy[^1].IsFinal ? null : copy[^1];
            var mutatesCheckpoint = checkpoint is not null ||
                _activeSegmentCheckpoint?.FactId == copy[^1].FactId;
            Append(new StoredIngressEntry(
                pending.EntryId,
                SegmentBatchKind,
                SegmentBatch: copy,
                MutatesCheckpoint: mutatesCheckpoint,
                Checkpoint: checkpoint));
            _segmentBatches.Add(pending);
            if (mutatesCheckpoint)
                _activeSegmentCheckpoint = checkpoint;
        }
    }

    public void ClearActiveCheckpoint(Guid factId, long revision)
    {
        lock (_gate)
        {
            if (_activeSegmentCheckpoint is not { } active ||
                active.FactId != factId ||
                active.Revision != revision)
                return;
            Append(new StoredIngressEntry(
                Guid.CreateVersion7(),
                SegmentCheckpointKind,
                MutatesCheckpoint: true));
            _activeSegmentCheckpoint = null;
        }
    }

    public void RecoverInterruptedSegment(DateTimeOffset recoveredAt)
    {
        lock (_gate)
        {
            if (_activeSegmentCheckpoint is not { } active)
                return;
            var final = active with
            {
                Revision = checked(active.Revision + 1),
                IsFinal = true
            };
            var gap = recoveredAt > active.End
                ? new SystemSegmentIngressGap(
                    Guid.CreateVersion7(),
                    active.End,
                    recoveredAt,
                    "process_restart")
                : null;
            var entryId = Guid.CreateVersion7();
            Append(new StoredIngressEntry(
                entryId,
                SegmentRecoveryKind,
                SegmentBatch: [final],
                SegmentGap: gap,
                MutatesCheckpoint: true));
            _segmentBatches.Add(new PendingSystemSegmentIngress(entryId, [final]));
            if (gap is not null)
                _segmentGaps.Add(new PendingSystemSegmentGapIngress(entryId, gap));
            _activeSegmentCheckpoint = null;
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
            if (_inputDeliveries.Count(delivery => delivery.Item is not null) >= _inputCapacity)
            {
                var gap = new SystemInputIngressGap(
                    Guid.CreateVersion7(),
                    copy.Timestamp,
                    copy.Timestamp.AddTicks(1),
                    1);
                var pendingGap = new PendingSystemInputDelivery(Guid.CreateVersion7(), Gap: gap);
                Append(new StoredIngressEntry(
                    pendingGap.EntryId,
                    InputGapKind,
                    InputGap: gap));
                _inputDeliveries.Add(pendingGap);
                return SystemInputIngressStageResult.GapStaged;
            }
            var pending = new PendingSystemInputDelivery(Guid.CreateVersion7(), Item: copy);
            Append(new StoredIngressEntry(
                pending.EntryId,
                InputEventKind,
                InputEvent: copy));
            _inputDeliveries.Add(pending);
            return SystemInputIngressStageResult.EventStaged;
        }
    }

    public bool TryEnqueue(InputEventItem item) =>
        StageInputEvent(item) == SystemInputIngressStageResult.EventStaged;

    public IReadOnlyList<PendingSystemSegmentIngress> PeekSegmentBatches(int limit)
    {
        lock (_gate)
            return _segmentBatches.Take(limit).ToArray();
    }

    public IReadOnlyList<PendingSystemSegmentGapIngress> PeekSegmentGaps(int limit)
    {
        lock (_gate)
            return _segmentGaps.Take(limit).ToArray();
    }

    public IReadOnlyList<PendingSystemInputDelivery> PeekInputDeliveries(int limit)
    {
        lock (_gate)
            return _inputDeliveries.Take(limit).ToArray();
    }

    public IReadOnlyList<PendingSystemInputDelivery> PeekInputEvents(int limit)
    {
        lock (_gate)
            return _inputDeliveries.Where(delivery => delivery.Item is not null).Take(limit).ToArray();
    }

    public IReadOnlyList<PendingSystemInputDelivery> PeekInputGaps(int limit)
    {
        lock (_gate)
            return _inputDeliveries.Where(delivery => delivery.Gap is not null).Take(limit).ToArray();
    }

    public ForegroundSegmentSnapshot? ActiveSegmentCheckpoint
    {
        get
        {
            lock (_gate)
                return _activeSegmentCheckpoint;
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
                return _segmentBatches.Count != 0 ||
                    _segmentGaps.Count != 0 ||
                    _inputDeliveries.Count != 0;
        }
    }

    public int PendingInputGapCount
    {
        get
        {
            lock (_gate)
                return _inputDeliveries.Sum(item => item.Gap?.EstimatedFactsLost ?? 0);
        }
    }

    internal IReadOnlySet<Guid> PendingFactIds
    {
        get
        {
            lock (_gate)
                return _segmentBatches.SelectMany(item => item.Snapshots)
                    .Select(item => item.FactId)
                    .Concat(_inputDeliveries
                        .Where(item => item.Item is not null)
                        .Select(item => item.Item!.Id))
                    .ToHashSet();
        }
    }

    public void AcknowledgeSegmentBatches(IReadOnlyList<PendingSystemSegmentIngress> entries)
    {
        lock (_gate)
        {
            EnsurePrefix(_segmentBatches.Select(item => item.EntryId), entries.Select(item => item.EntryId));
            Rewrite(
                _segmentBatches.Skip(entries.Count),
                _segmentGaps,
                _inputDeliveries,
                _activeSegmentCheckpoint);
            _segmentBatches.RemoveRange(0, entries.Count);
        }
    }

    public void AcknowledgeSegmentGaps(IReadOnlyList<PendingSystemSegmentGapIngress> entries)
    {
        lock (_gate)
        {
            EnsurePrefix(_segmentGaps.Select(item => item.EntryId), entries.Select(item => item.EntryId));
            Rewrite(
                _segmentBatches,
                _segmentGaps.Skip(entries.Count),
                _inputDeliveries,
                _activeSegmentCheckpoint);
            _segmentGaps.RemoveRange(0, entries.Count);
        }
    }

    public void AcknowledgeInputDeliveries(IReadOnlyList<PendingSystemInputDelivery> entries)
    {
        lock (_gate)
        {
            EnsurePrefix(_inputDeliveries.Select(item => item.EntryId), entries.Select(item => item.EntryId));
            Rewrite(
                _segmentBatches,
                _segmentGaps,
                _inputDeliveries.Skip(entries.Count),
                _activeSegmentCheckpoint);
            _inputDeliveries.RemoveRange(0, entries.Count);
        }
    }

    private static bool ValidSegmentBatch(IReadOnlyList<ForegroundSegmentSnapshot>? batch) =>
        batch is { Count: > 0 } && batch.All(item => item.FactId != Guid.Empty && item.Revision > 0);

    private static bool ValidSegmentGap(SystemSegmentIngressGap? gap) =>
        gap is not null &&
        gap.GapId != Guid.Empty &&
        gap.End > gap.Start &&
        !string.IsNullOrWhiteSpace(gap.Reason);

    private static bool ValidSegmentGapOrNull(SystemSegmentIngressGap? gap) =>
        gap is null || ValidSegmentGap(gap);

    private static bool ValidInputGap(SystemInputIngressGap? gap) =>
        gap is not null &&
        gap.GapId != Guid.Empty &&
        gap.End > gap.Start &&
        gap.EstimatedFactsLost > 0;

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

    private static void RepairMalformedTail(string path, long validLength)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        stream.SetLength(validLength);
        stream.Flush(flushToDisk: true);
    }

    private static void RepairMissingTailNewline(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    private void Rewrite(
        IEnumerable<PendingSystemSegmentIngress> segmentBatches,
        IEnumerable<PendingSystemSegmentGapIngress> segmentGaps,
        IEnumerable<PendingSystemInputDelivery> inputDeliveries,
        ForegroundSegmentSnapshot? activeSegmentCheckpoint)
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
                var entries = segmentBatches.Select(item => new StoredIngressEntry(
                        item.EntryId,
                        SegmentBatchKind,
                        SegmentBatch: item.Snapshots))
                    .Concat(segmentGaps.Select(item => new StoredIngressEntry(
                        item.EntryId,
                        SegmentGapKind,
                        SegmentGap: item.Gap)))
                    .Concat(inputDeliveries.Select(item => item.Item is not null
                        ? new StoredIngressEntry(
                            item.EntryId,
                            InputEventKind,
                            InputEvent: item.Item)
                        : new StoredIngressEntry(
                            item.EntryId,
                            InputGapKind,
                            InputGap: item.Gap)));
                foreach (var entry in entries)
                    writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
                if (activeSegmentCheckpoint is not null)
                {
                    writer.WriteLine(JsonSerializer.Serialize(new StoredIngressEntry(
                        Guid.CreateVersion7(),
                        SegmentCheckpointKind,
                        MutatesCheckpoint: true,
                        Checkpoint: activeSegmentCheckpoint), JsonOptions));
                }
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
        ForegroundSegmentSnapshot? Segment = null,
        InputEventItem? InputEvent = null,
        SystemInputIngressGap? InputGap = null,
        IReadOnlyList<ForegroundSegmentSnapshot>? SegmentBatch = null,
        SystemSegmentIngressGap? SegmentGap = null,
        bool MutatesCheckpoint = false,
        ForegroundSegmentSnapshot? Checkpoint = null);
}
