using Orisumi.Application.Entities;
using Orisumi.Core;

namespace Orisumi.Model.Tests;

internal sealed class InMemoryEntityStore : IEntityStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private State _state = new();

    public async Task<T> ExecuteAsync<T>(Func<IEntityTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var working = new State(_state);
            var result = await operation(working, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _state = working;
            return result;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();

    private sealed class State : IEntityTransaction
    {
        private readonly Dictionary<EntityId, Thing> _things = [];
        private readonly List<EntityVersion> _versions = [];
        private readonly HashSet<EntityId> _deletedEntities = [];
        private readonly HashSet<VersionId> _deletedVersions = [];
        private readonly Dictionary<(EntityId, RecognitionKey), EntityId> _keys = [];
        private readonly Dictionary<EntityId, WriteStreamState> _streams = [];
        internal State() { }
        internal State(State source)
        {
            _versions.AddRange(source._versions);
            foreach (var pair in source._things) _things.Add(pair.Key, pair.Value);
            foreach (var pair in source._keys) _keys.Add(pair.Key, pair.Value);
            foreach (var pair in source._streams) _streams.Add(pair.Key, pair.Value with { SchemaVersionIds = new(pair.Value.SchemaVersionIds) });
            _deletedEntities.UnionWith(source._deletedEntities);
            _deletedVersions.UnionWith(source._deletedVersions);
        }

        public Task<bool> IsHostInstanceAsync(EntityId id, Guid host, CancellationToken cancellationToken) => Task.FromResult(false);
        public async Task<T> SavepointAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
        {
            var saved = new State(this);
            try { return await operation(); }
            catch (ModelViolationException)
            {
                _things.Clear(); foreach (var pair in saved._things) _things.Add(pair.Key, pair.Value);
                _versions.Clear(); _versions.AddRange(saved._versions);
                _keys.Clear(); foreach (var pair in saved._keys) _keys.Add(pair.Key, pair.Value);
                _streams.Clear(); foreach (var pair in saved._streams) _streams.Add(pair.Key, pair.Value);
                throw;
            }
        }
        public Task<EntityId?> FindRecognitionAsync(EntityId schemaId, RecognitionKey key, CancellationToken cancellationToken) =>
            Task.FromResult(_keys.TryGetValue((schemaId, key), out var id) ? (EntityId?)id : null);
        public Task RegisterRecognitionAsync(EntityId schemaId, RecognitionKey key, EntityId entityId, CancellationToken cancellationToken)
        { _keys.TryAdd((schemaId, key), entityId); return Task.CompletedTask; }
        public Task<WriteStreamState> ReadStreamAsync(EntityId observer, CancellationToken cancellationToken) =>
            Task.FromResult(_streams.TryGetValue(observer, out var stream) ? stream with { SchemaVersionIds = new(stream.SchemaVersionIds) } : new(0, []));
        public Task SaveStreamAsync(EntityId observer, WriteStreamState stream, CancellationToken cancellationToken)
        { _streams[observer] = stream; return Task.CompletedTask; }
        public Task<Thing?> ReadThingAsync(EntityId id, CancellationToken token) => Task.FromResult(_things.GetValueOrDefault(id));
        public Task<IReadOnlyList<Thing>> ReadThingsAsync(IReadOnlyList<EntityId> ids, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<Thing>>(ids.Where(_things.ContainsKey).Select(id => _things[id]).ToArray());
        public Task<IReadOnlyList<Thing>> ListThingsAsync(EntityQuery query, CancellationToken token) => throw new NotSupportedException();
        public Task CreateThingAsync(EntityId id, EntityId schema, CancellationToken token)
        { _things.Add(id, new(id, schema, [])); return Task.CompletedTask; }

        public Task<EntityVersion?> ReadCurrentAsync(EntityId id, CancellationToken cancellationToken) =>
            Task.FromResult(_versions.LastOrDefault(version => version.EntityId == id));
        public Task<EntityId?> FindVersionEntityAsync(VersionId id, CancellationToken cancellationToken) =>
            Task.FromResult(_versions.Find(version => version.VersionId == id)?.EntityId);
        public Task<IReadOnlyList<EntityVersion>> ListEntitiesAsync(EntityQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Query behavior is covered against PostgreSQL.");
        public Task<IReadOnlyList<EntityVersion>> ListObservationsAsync(ObservationQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Query behavior is covered against PostgreSQL.");
        public Task<IReadOnlyList<Referrer>> ListReferrersAsync(ReferrerQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Query behavior is covered against PostgreSQL.");

        public Task<EntityVersion?> ReadVersionAsync(VersionId id, CancellationToken cancellationToken) =>
            Task.FromResult(_versions.Find(version => version.VersionId == id));
        public Task<IReadOnlyList<EntityVersion>> ReadVersionsAsync(EntityId id, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EntityVersion>>(_versions.Where(version => version.EntityId == id).ToArray());
        public Task<bool> EntityExistsAsync(EntityId id, CancellationToken cancellationToken) =>
            Task.FromResult(_deletedEntities.Contains(id) || _things.ContainsKey(id) || _versions.Any(version => version.EntityId == id));
        public Task<bool> IsEntityDeletedAsync(EntityId id, CancellationToken cancellationToken) => Task.FromResult(_deletedEntities.Contains(id));
        public Task<bool> IsVersionDeletedAsync(VersionId id, CancellationToken cancellationToken) => Task.FromResult(_deletedVersions.Contains(id));
        public Task<EntityId?> FindSchemaAsync(string resourceName, CancellationToken cancellationToken) =>
            Task.FromResult(_versions.Find(version => version.Content is EntitySchema schema && schema.ResourceName == resourceName)?.EntityId);
        public Task<bool> HasSchemaUsersAsync(EntityId schemaId, CancellationToken cancellationToken)
        {
            var schemaVersions = _versions.Where(version => version.EntityId == schemaId).Select(version => version.VersionId).ToHashSet();
            return Task.FromResult(_things.Values.Any(thing => thing.SchemaId == schemaId) || _versions.Any(version => version.SchemaVersionId is { } id && schemaVersions.Contains(id)));
        }
        public Task<long?> ContentSizeAsync(string digest, CancellationToken cancellationToken) => Task.FromResult<long?>(null);
        public Task UploadContentAsync(string digest, byte[] data, DateTimeOffset uploadedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Binary persistence is tested against PostgreSQL.");
        public Task<byte[]?> ReadContentAsync(string digest, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Binary persistence is tested against PostgreSQL.");
        public Task PruneContentsAsync(DateTimeOffset before, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Binary persistence is tested against PostgreSQL.");
        public Task SaveAsync(EntityVersion version, IReadOnlyList<ResolvedReference> references, IReadOnlyList<BinaryValue> contents, CancellationToken cancellationToken)
        {
            _versions.Add(version);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(EntityId id, CancellationToken cancellationToken)
        {
            foreach (var key in _keys.Where(pair => pair.Value == id).Select(pair => pair.Key).ToArray()) _keys.Remove(key);
            _streams.Remove(id);
            _things.Remove(id);
            _deletedEntities.Add(id);
            _deletedVersions.UnionWith(_versions.Where(version => version.EntityId == id).Select(version => version.VersionId));
            _versions.RemoveAll(version => version.EntityId == id);
            return Task.CompletedTask;
        }
    }
}
