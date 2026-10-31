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
        private readonly List<EntityVersion> _versions = [];
        private readonly HashSet<EntityId> _deletedEntities = [];
        private readonly HashSet<VersionId> _deletedVersions = [];
        internal State() { }
        internal State(State source)
        {
            _versions.AddRange(source._versions);
            _deletedEntities.UnionWith(source._deletedEntities);
            _deletedVersions.UnionWith(source._deletedVersions);
        }

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
            Task.FromResult(_deletedEntities.Contains(id) || _versions.Any(version => version.EntityId == id));
        public Task<bool> IsEntityDeletedAsync(EntityId id, CancellationToken cancellationToken) => Task.FromResult(_deletedEntities.Contains(id));
        public Task<bool> IsVersionDeletedAsync(VersionId id, CancellationToken cancellationToken) => Task.FromResult(_deletedVersions.Contains(id));
        public Task<EntityId?> FindSchemaAsync(string resourceName, CancellationToken cancellationToken) =>
            Task.FromResult(_versions.Find(version => version.Content is EntitySchema schema && schema.ResourceName == resourceName)?.EntityId);
        public Task<bool> HasBusinessVersionsAsync(EntityId schemaId, CancellationToken cancellationToken)
        {
            var schemaVersions = _versions.Where(version => version.EntityId == schemaId).Select(version => version.VersionId).ToHashSet();
            return Task.FromResult(_versions.Any(version => version.SchemaVersionId is { } id && schemaVersions.Contains(id)));
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
            _deletedEntities.Add(id);
            _deletedVersions.UnionWith(_versions.Where(version => version.EntityId == id).Select(version => version.VersionId));
            _versions.RemoveAll(version => version.EntityId == id);
            return Task.CompletedTask;
        }
    }
}
