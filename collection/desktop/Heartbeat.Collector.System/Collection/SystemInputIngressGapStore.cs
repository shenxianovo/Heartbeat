using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Collector.System.Collection;

internal sealed record SystemInputIngressGap(
    Guid GapId,
    DateTimeOffset Start,
    DateTimeOffset End,
    int EstimatedFactsLost);

/// <summary>
/// Durable emergency ledger for native InputEvent ingress overflow. Claim is persisted before a
/// Gap is published, so concurrent loss starts a separate stable range and ACK cannot erase it.
/// </summary>
internal sealed class SystemInputIngressGapStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private GapState _state;

    private SystemInputIngressGapStore(string path, GapState state)
    {
        _path = path;
        _state = state;
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _state.Gaps.Sum(item => item.Gap.EstimatedFactsLost);
        }
    }

    public static SystemInputIngressGapStore Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return new SystemInputIngressGapStore(fullPath, new GapState());

        var envelope = JsonSerializer.Deserialize<GapEnvelope>(
            File.ReadAllText(fullPath, Encoding.UTF8),
            JsonOptions) ?? throw new InvalidDataException("System InputEvent ingress Gap state is empty.");
        if (envelope.SchemaVersion != 1 || envelope.State?.Gaps is null ||
            envelope.State.Gaps.Any(item =>
                item.Gap.GapId == Guid.Empty || item.Gap.End <= item.Gap.Start ||
                item.Gap.EstimatedFactsLost <= 0))
            throw new InvalidDataException("System InputEvent ingress Gap state is invalid.");
        return new SystemInputIngressGapStore(fullPath, envelope.State);
    }

    public void RecordDrop(DateTimeOffset occurredAt)
    {
        lock (_gate)
        {
            var endExclusive = occurredAt + TimeSpan.FromTicks(1);
            var gaps = _state.Gaps.ToList();
            if (gaps.LastOrDefault() is { InFlight: false } current)
            {
                gaps[^1] = current with
                {
                    Gap = current.Gap with
                    {
                        Start = occurredAt < current.Gap.Start ? occurredAt : current.Gap.Start,
                        End = endExclusive > current.Gap.End ? endExclusive : current.Gap.End,
                        EstimatedFactsLost = checked(current.Gap.EstimatedFactsLost + 1)
                    }
                };
            }
            else
            {
                gaps.Add(new PendingGap(
                    new SystemInputIngressGap(Guid.CreateVersion7(), occurredAt, endExclusive, 1),
                    InFlight: false));
            }
            Commit(new GapState { Gaps = gaps });
        }
    }

    public SystemInputIngressGap? Claim()
    {
        lock (_gate)
        {
            if (_state.Gaps.Count == 0)
                return null;
            var first = _state.Gaps[0];
            if (!first.InFlight)
            {
                var gaps = _state.Gaps.ToList();
                gaps[0] = first with { InFlight = true };
                Commit(new GapState { Gaps = gaps });
            }
            return first.Gap;
        }
    }

    public SystemInputIngressGap? Peek()
    {
        lock (_gate)
            return _state.Gaps.FirstOrDefault()?.Gap;
    }

    public void Acknowledge(Guid gapId)
    {
        lock (_gate)
        {
            if (_state.Gaps.Count == 0 || _state.Gaps[0].Gap.GapId != gapId || !_state.Gaps[0].InFlight)
                throw new InvalidOperationException("System InputEvent ingress Gaps must be acknowledged in claimed order.");
            Commit(new GapState { Gaps = [.. _state.Gaps.Skip(1)] });
        }
    }

    private void Commit(GapState next)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("System InputEvent ingress Gap path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(new GapEnvelope(1, next), JsonOptions),
                new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
            _state = next;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed record GapEnvelope(int SchemaVersion, GapState? State);

    private sealed record GapState
    {
        public List<PendingGap> Gaps { get; init; } = [];
    }

    private sealed record PendingGap(SystemInputIngressGap Gap, bool InFlight);
}
