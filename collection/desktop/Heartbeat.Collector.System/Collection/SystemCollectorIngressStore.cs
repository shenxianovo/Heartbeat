using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Collection.Hub.Collectors.Protocol;
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

internal sealed class SystemCollectorIngressCommitFence : ICollectorDurableCommitFence
{
    private readonly object _gate = new();
    private bool _fenced;

    public bool IsFenced => Volatile.Read(ref _fenced);

    public void Fence()
    {
        lock (_gate)
            _fenced = true;
    }

    public bool TryPublishFile(string preparedPath, string authoritativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preparedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(authoritativePath);
        lock (_gate)
        {
            if (_fenced)
                return false;
            File.Move(preparedPath, authoritativePath, overwrite: true);
            return true;
        }
    }
}

/// <summary>
/// Durable first stage for System observations, published as bounded copy-on-write journal chunks.
/// A segment batch and its active checkpoint are one mutation; acknowledgement tombstones retain
/// the checkpoint and quiescent reset records make old chunks safely reclaimable. Input capacity
/// likewise stages either each Event or its Gap in one atomic batch mutation.
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
    private const string InputDeliveryBatchKind = "input_delivery_batch";
    private const string SegmentBatchAcknowledgedKind = "segment_batch_acknowledged";
    private const string SegmentGapAcknowledgedKind = "segment_gap_acknowledged";
    private const string InputDeliveryAcknowledgedKind = "input_delivery_acknowledged";
    private const string ResetKind = "reset";
    private const int MaxJournalChunkBytes = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly int _inputCapacity;
    private readonly ICollectorDurableCommitFence _commitFence;
    private readonly Action? _beforeCommit;
    private readonly List<PendingSystemSegmentIngress> _segmentBatches;
    private readonly List<PendingSystemSegmentGapIngress> _segmentGaps;
    private readonly List<PendingSystemInputDelivery> _inputDeliveries;
    private ForegroundSegmentSnapshot? _activeSegmentCheckpoint;
    private int _tailChunkIndex;
    private long _tailChunkLength;

    private SystemCollectorIngressStore(
        string path,
        int inputCapacity,
        ICollectorDurableCommitFence commitFence,
        Action? beforeCommit,
        List<PendingSystemSegmentIngress> segmentBatches,
        List<PendingSystemSegmentGapIngress> segmentGaps,
        List<PendingSystemInputDelivery> inputDeliveries,
        ForegroundSegmentSnapshot? activeSegmentCheckpoint,
        int tailChunkIndex,
        long tailChunkLength)
    {
        _path = path;
        _inputCapacity = inputCapacity;
        _commitFence = commitFence;
        _beforeCommit = beforeCommit;
        _segmentBatches = segmentBatches;
        _segmentGaps = segmentGaps;
        _inputDeliveries = inputDeliveries;
        _activeSegmentCheckpoint = activeSegmentCheckpoint;
        _tailChunkIndex = tailChunkIndex;
        _tailChunkLength = tailChunkLength;
    }

    public static SystemCollectorIngressStore Open(string path, int inputCapacity)
        => Open(path, inputCapacity, new SystemCollectorIngressCommitFence(), null);

    internal static SystemCollectorIngressStore Open(
        string path,
        int inputCapacity,
        ICollectorDurableCommitFence commitFence,
        Action? beforeCommit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (inputCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputCapacity));
        ArgumentNullException.ThrowIfNull(commitFence);
        var fullPath = Path.GetFullPath(path);
        var segmentBatches = new List<PendingSystemSegmentIngress>();
        var segmentGaps = new List<PendingSystemSegmentGapIngress>();
        var inputDeliveries = new List<PendingSystemInputDelivery>();
        ForegroundSegmentSnapshot? activeSegmentCheckpoint = null;
        var chunks = JournalChunks(fullPath);
        foreach (var chunk in chunks)
        {
            var bytes = File.ReadAllBytes(chunk.Path);
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
                catch (JsonException) when (
                    chunk.Index == chunks[^1].Index && nextLineStart == bytes.Length)
                {
                    RepairMalformedTail(chunk.Path, lineStart);
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
                    case InputDeliveryBatchKind when ValidInputDeliveryBatch(entry.InputDeliveries):
                        inputDeliveries.AddRange(entry.InputDeliveries!);
                        break;
                    case SegmentBatchAcknowledgedKind when entry.AcknowledgedEntryIds is not null:
                        RemoveAcknowledged(segmentBatches, entry.AcknowledgedEntryIds, item => item.EntryId);
                        break;
                    case SegmentGapAcknowledgedKind when entry.AcknowledgedEntryIds is not null:
                        RemoveAcknowledged(segmentGaps, entry.AcknowledgedEntryIds, item => item.EntryId);
                        break;
                    case InputDeliveryAcknowledgedKind when entry.AcknowledgedEntryIds is not null:
                        RemoveAcknowledged(inputDeliveries, entry.AcknowledgedEntryIds, item => item.EntryId);
                        break;
                    case ResetKind:
                        segmentBatches.Clear();
                        segmentGaps.Clear();
                        inputDeliveries.Clear();
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
                    RepairMissingTailNewline(chunk.Path);
                lineStart = nextLineStart;
            }
        }
        var tailChunkIndex = chunks.Count == 0 ? 0 : chunks[^1].Index;
        var tailChunkPath = ChunkPath(fullPath, tailChunkIndex);
        return new SystemCollectorIngressStore(
            fullPath,
            inputCapacity,
            commitFence,
            beforeCommit,
            segmentBatches,
            segmentGaps,
            inputDeliveries,
            activeSegmentCheckpoint,
            tailChunkIndex,
            File.Exists(tailChunkPath) ? new FileInfo(tailChunkPath).Length : 0);
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
        => StageInputEvents([item]) == 0
            ? SystemInputIngressStageResult.EventStaged
            : SystemInputIngressStageResult.GapStaged;

    public int StageInputEvents(IReadOnlyList<InputEventItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return 0;
        lock (_gate)
        {
            var staged = new List<PendingSystemInputDelivery>(items.Count);
            var acceptedCount = _inputDeliveries.Count(delivery => delivery.Item is not null);
            var gapCount = 0;
            foreach (var item in items)
            {
                ArgumentNullException.ThrowIfNull(item);
                var copy = new InputEventItem
                {
                    Id = item.Id,
                    EventType = item.EventType,
                    CodeSet = item.CodeSet,
                    Code = item.Code,
                    Timestamp = item.Timestamp
                };
                if (acceptedCount >= _inputCapacity)
                {
                    staged.Add(new PendingSystemInputDelivery(
                        Guid.CreateVersion7(),
                        Gap: new SystemInputIngressGap(
                            Guid.CreateVersion7(),
                            copy.Timestamp,
                            copy.Timestamp.AddTicks(1),
                            1)));
                    gapCount++;
                }
                else
                {
                    staged.Add(new PendingSystemInputDelivery(Guid.CreateVersion7(), Item: copy));
                    acceptedCount++;
                }
            }
            Append(new StoredIngressEntry(
                Guid.CreateVersion7(),
                InputDeliveryBatchKind,
                InputDeliveries: staged));
            _inputDeliveries.AddRange(staged);
            return gapCount;
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
            AppendAcknowledgement(SegmentBatchAcknowledgedKind, entries.Select(item => item.EntryId));
            _segmentBatches.RemoveRange(0, entries.Count);
            CompactQuiescentHistory();
        }
    }

    public void AcknowledgeSegmentGaps(IReadOnlyList<PendingSystemSegmentGapIngress> entries)
    {
        lock (_gate)
        {
            EnsurePrefix(_segmentGaps.Select(item => item.EntryId), entries.Select(item => item.EntryId));
            AppendAcknowledgement(SegmentGapAcknowledgedKind, entries.Select(item => item.EntryId));
            _segmentGaps.RemoveRange(0, entries.Count);
            CompactQuiescentHistory();
        }
    }

    public void AcknowledgeInputDeliveries(IReadOnlyList<PendingSystemInputDelivery> entries)
    {
        lock (_gate)
        {
            EnsurePrefix(_inputDeliveries.Select(item => item.EntryId), entries.Select(item => item.EntryId));
            AppendAcknowledgement(InputDeliveryAcknowledgedKind, entries.Select(item => item.EntryId));
            _inputDeliveries.RemoveRange(0, entries.Count);
            CompactQuiescentHistory();
        }
    }

    private void AppendAcknowledgement(string kind, IEnumerable<Guid> entryIds)
    {
        var ids = entryIds.ToArray();
        if (ids.Length == 0)
            return;
        Append(new StoredIngressEntry(
            Guid.CreateVersion7(),
            kind,
            AcknowledgedEntryIds: ids));
    }

    private void CompactQuiescentHistory()
    {
        if (_segmentBatches.Count != 0 || _segmentGaps.Count != 0 || _inputDeliveries.Count != 0)
            return;
        try
        {
            Append(new StoredIngressEntry(
                Guid.CreateVersion7(),
                ResetKind,
                MutatesCheckpoint: true,
                Checkpoint: _activeSegmentCheckpoint));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        foreach (var chunk in JournalChunks(_path).Where(chunk => chunk.Index < _tailChunkIndex))
        {
            try
            {
                File.Delete(chunk.Path);
            }
            catch (IOException)
            {
                // Reset already made the older immutable history logically unreachable.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort physical cleanup can be retried after a later acknowledgement.
            }
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

    private static bool ValidInputDeliveryBatch(IReadOnlyList<PendingSystemInputDelivery>? batch) =>
        batch is { Count: > 0 } && batch.All(item =>
            item.EntryId != Guid.Empty &&
            (item.Item is not null) != (item.Gap is not null) &&
            (item.Gap is null || ValidInputGap(item.Gap)));

    private static void EnsurePrefix(IEnumerable<Guid> pending, IEnumerable<Guid> acknowledged)
    {
        var expected = acknowledged.ToArray();
        if (!pending.Take(expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException("System Collector ingress must be acknowledged in durable order.");
    }

    private static void RemoveAcknowledged<T>(
        List<T> pending,
        IReadOnlyList<Guid> acknowledged,
        Func<T, Guid> entryId)
    {
        var acknowledgedIds = acknowledged.ToHashSet();
        pending.RemoveAll(item => acknowledgedIds.Contains(entryId(item)));
    }

    private void Append(StoredIngressEntry entry)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("System Collector ingress path has no directory.");
        Directory.CreateDirectory(directory);
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        var chunkIndex = _tailChunkLength != 0 && _tailChunkLength + line.Length > MaxJournalChunkBytes
            ? checked(_tailChunkIndex + 1)
            : _tailChunkIndex;
        var chunkLength = chunkIndex == _tailChunkIndex ? _tailChunkLength : 0;
        var chunkPath = ChunkPath(_path, chunkIndex);
        var temporary = chunkPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                if (File.Exists(chunkPath))
                {
                    using var source = new FileStream(
                        chunkPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite);
                    source.CopyTo(stream);
                }
                stream.Write(line);
                stream.Flush(flushToDisk: true);
            }
            _beforeCommit?.Invoke();
            if (!_commitFence.TryPublishFile(temporary, chunkPath))
                throw new OperationCanceledException(
                    "System Collector ingress mutation was fenced before publication.");
            _tailChunkIndex = chunkIndex;
            _tailChunkLength = chunkLength + line.Length;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
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

    private static List<(string Path, int Index)> JournalChunks(string path)
    {
        var chunks = new List<(string Path, int Index)>();
        if (File.Exists(path))
            chunks.Add((path, 0));
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory))
            return chunks;
        var fileName = Path.GetFileName(path);
        foreach (var candidate in Directory.EnumerateFiles(directory, fileName + ".*.chunk"))
        {
            var suffix = Path.GetFileName(candidate)[(fileName.Length + 1)..^".chunk".Length];
            if (int.TryParse(suffix, out var index) && index > 0)
                chunks.Add((candidate, index));
        }
        chunks.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        return chunks;
    }

    private static string ChunkPath(string path, int index) =>
        index == 0 ? path : $"{path}.{index:D8}.chunk";

    private sealed record StoredIngressEntry(
        Guid EntryId,
        string Kind,
        ForegroundSegmentSnapshot? Segment = null,
        InputEventItem? InputEvent = null,
        SystemInputIngressGap? InputGap = null,
        IReadOnlyList<ForegroundSegmentSnapshot>? SegmentBatch = null,
        SystemSegmentIngressGap? SegmentGap = null,
        bool MutatesCheckpoint = false,
        ForegroundSegmentSnapshot? Checkpoint = null,
        IReadOnlyList<Guid>? AcknowledgedEntryIds = null,
        IReadOnlyList<PendingSystemInputDelivery>? InputDeliveries = null);
}
