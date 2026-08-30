using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Core.DTOs.Input;

namespace Heartbeat.Collector.System.Collection;

internal sealed record PendingSystemSegmentIngress(Guid EntryId, ForegroundSegmentSnapshot Snapshot);
internal sealed record PendingSystemInputIngress(Guid EntryId, InputEventItem Item);

/// <summary>
/// Append-only durable first stage for observations produced by native System callbacks. Entries
/// are compacted away only after the Collector Protocol outbox accepts the same Fact identity and
/// revision. A crash between those commits replays an idempotent duplicate.
/// </summary>
internal sealed class SystemCollectorIngressStore
{
    private const string SegmentKind = "segment";
    private const string InputEventKind = "input_event";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly int _inputCapacity;
    private readonly List<PendingSystemSegmentIngress> _segments;
    private readonly List<PendingSystemInputIngress> _inputEvents;

    private SystemCollectorIngressStore(
        string path,
        int inputCapacity,
        List<PendingSystemSegmentIngress> segments,
        List<PendingSystemInputIngress> inputEvents)
    {
        _path = path;
        _inputCapacity = inputCapacity;
        _segments = segments;
        _inputEvents = inputEvents;
    }

    public static SystemCollectorIngressStore Open(string path, int inputCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (inputCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputCapacity));
        var fullPath = Path.GetFullPath(path);
        var segments = new List<PendingSystemSegmentIngress>();
        var inputEvents = new List<PendingSystemInputIngress>();
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
                    default:
                        throw new InvalidDataException("System Collector ingress entry kind is invalid.");
                }
            }
        }
        return new SystemCollectorIngressStore(fullPath, inputCapacity, segments, inputEvents);
    }

    public void Enqueue(ForegroundSegmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            var pending = new PendingSystemSegmentIngress(Guid.CreateVersion7(), snapshot);
            Append(new StoredIngressEntry(pending.EntryId, SegmentKind, snapshot, null));
            _segments.Add(pending);
        }
    }

    public bool TryEnqueue(InputEventItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            if (_inputEvents.Count >= _inputCapacity)
                return false;
            var copy = new InputEventItem
            {
                Id = item.Id,
                EventType = item.EventType,
                CodeSet = item.CodeSet,
                Code = item.Code,
                Timestamp = item.Timestamp
            };
            var pending = new PendingSystemInputIngress(Guid.CreateVersion7(), copy);
            Append(new StoredIngressEntry(pending.EntryId, InputEventKind, null, copy));
            _inputEvents.Add(pending);
            return true;
        }
    }

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

    public bool HasPending
    {
        get
        {
            lock (_gate)
                return _segments.Count != 0 || _inputEvents.Count != 0;
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
            Rewrite(_segments.Skip(entries.Count), _inputEvents);
            _segments.RemoveRange(0, entries.Count);
        }
    }

    public void AcknowledgeInputEvents(IReadOnlyList<PendingSystemInputIngress> entries)
    {
        lock (_gate)
        {
            EnsurePrefix(_inputEvents.Select(item => item.EntryId), entries.Select(item => item.EntryId));
            Rewrite(_segments, _inputEvents.Skip(entries.Count));
            _inputEvents.RemoveRange(0, entries.Count);
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
        IEnumerable<PendingSystemInputIngress> inputEvents)
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
                             new StoredIngressEntry(item.EntryId, SegmentKind, item.Snapshot, null))
                         .Concat(inputEvents.Select(item =>
                             new StoredIngressEntry(item.EntryId, InputEventKind, null, item.Item))))
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
        InputEventItem? InputEvent);
}
