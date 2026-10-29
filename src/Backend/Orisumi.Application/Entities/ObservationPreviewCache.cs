using Orisumi.Core;

namespace Orisumi.Application.Entities;

public sealed class ObservationPreviewCache : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<EntityId, Entry> _entries = [];
    private readonly TimeProvider _time;
    private readonly ITimer _expiry;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public ObservationPreviewCache(TimeProvider timeProvider)
    {
        _time = timeProvider;
        _expiry = timeProvider.CreateTimer(_ => Prune(), null, Lifetime, Lifetime);
    }

    internal ObservationPreview? Read(EntityId id)
    {
        lock (_gate)
        {
            Prune();
            return _entries.GetValueOrDefault(id)?.Preview;
        }
    }

    internal IReadOnlyList<ObservationPreview> List(EntityId? schema)
    {
        lock (_gate)
        {
            Prune();
            return _entries.Values.Where(entry => schema is null || entry.Schema == schema)
                .Select(entry => entry.Preview).OrderBy(preview => preview.EntityId.Value).ToArray();
        }
    }

    internal ObservationPreview Put(ObservationPreview preview, EntityId schema)
    {
        lock (_gate)
        {
            var previous = Read(preview.EntityId);
            if (previous is not null && previous.PreviousVersionId == preview.PreviousVersionId && previous.Sequence >= preview.Sequence)
                return previous;
            _entries[preview.EntityId] = new(preview, schema);
            return preview;
        }
    }

    internal void Remove(EntityId id)
    {
        lock (_gate) _entries.Remove(id);
    }

    internal void RemovePredecessor(EntityVersion saved)
    {
        lock (_gate)
            if (_entries.TryGetValue(saved.EntityId, out var entry) && entry.Preview.PreviousVersionId == saved.PreviousVersionId)
                _entries.Remove(saved.EntityId);
    }

    private void Prune()
    {
        lock (_gate)
            foreach (var id in _entries.Where(pair => _time.GetUtcNow() - pair.Value.Preview.UpdatedAt > Lifetime).Select(pair => pair.Key).ToArray())
                _entries.Remove(id);
    }

    public void Dispose() => _expiry.Dispose();
    private sealed record Entry(ObservationPreview Preview, EntityId Schema);
}
