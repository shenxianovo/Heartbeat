using Orisumi.Core;

namespace Orisumi.Application.Entities;

public sealed class ObservationDraftCache : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<EntityId, Entry> _entries = [];
    private readonly TimeProvider _time;
    private readonly ITimer _expiry;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public ObservationDraftCache(TimeProvider timeProvider)
    {
        _time = timeProvider;
        _expiry = timeProvider.CreateTimer(_ => Prune(), null, Lifetime, Lifetime);
    }

    internal ObservationDraft? Read(EntityId id)
    {
        lock (_gate)
        {
            Prune();
            return _entries.GetValueOrDefault(id)?.Draft;
        }
    }

    internal IReadOnlyList<ObservationDraft> List(EntityId? schema)
    {
        lock (_gate)
        {
            Prune();
            return _entries.Values.Where(entry => schema is null || entry.Schema == schema)
                .Select(entry => entry.Draft).OrderBy(draft => draft.EntityId.Value).ToArray();
        }
    }

    internal ObservationDraft Put(ObservationDraft draft, EntityId schema)
    {
        lock (_gate)
        {
            var previous = Read(draft.EntityId);
            if (previous is not null && previous.PreviousVersionId == draft.PreviousVersionId && previous.Sequence >= draft.Sequence)
                return previous;
            _entries[draft.EntityId] = new(draft, schema);
            return draft;
        }
    }

    internal void Remove(EntityId id)
    {
        lock (_gate) _entries.Remove(id);
    }

    internal void RemovePredecessor(EntityVersion saved)
    {
        lock (_gate)
            if (_entries.TryGetValue(saved.EntityId, out var entry) && entry.Draft.PreviousVersionId == saved.PreviousVersionId)
                _entries.Remove(saved.EntityId);
    }

    private void Prune()
    {
        lock (_gate)
            foreach (var id in _entries.Where(pair => _time.GetUtcNow() - pair.Value.Draft.UpdatedAt > Lifetime).Select(pair => pair.Key).ToArray())
                _entries.Remove(id);
    }

    public void Dispose() => _expiry.Dispose();
    private sealed record Entry(ObservationDraft Draft, EntityId Schema);
}
