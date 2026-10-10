using System.Text.Json;
using Heartbeat.Core;

namespace Heartbeat.Application.Entities;

public interface IEntityStore
{
    Task<EntitySaveResult> SaveEntityAsync(
        string resourceName,
        EntityId id,
        JsonElement references,
        JsonElement properties,
        CancellationToken cancellationToken = default);

    Task<EntitySaveResult> SaveObserverAsync(
        Observer observer,
        CancellationToken cancellationToken = default);

    Task<EntitySaveResult> SaveObservationAsync(
        Observation observation,
        CancellationToken cancellationToken = default);

    Task<EntitySaveResult> SaveEntitySchemaAsync(
        EntitySchema schema,
        CancellationToken cancellationToken = default);

    Task<StoredEntity?> ReadAsync(
        EntityId id,
        CancellationToken cancellationToken = default);
}
