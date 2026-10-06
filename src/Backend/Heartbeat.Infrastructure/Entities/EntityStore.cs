using System.Text.Json;
using Heartbeat.Application.Entities;
using Heartbeat.Core;
using Heartbeat.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Heartbeat.Infrastructure.Entities;

public sealed class EntityStore(HeartbeatDbContext dbContext) : IEntityStore
{
    public Task<EntitySaveResult> SaveEntityAsync(
        EntityId id,
        JsonElement data,
        CancellationToken cancellationToken = default) => SaveAsync(id, "entity_data", $"""
            INSERT INTO entity_data (id, data)
            VALUES ({id.Value}, CAST({data.GetRawText()} AS jsonb))
            ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data
            """, cancellationToken);

    public Task<EntitySaveResult> SaveObserverAsync(
        Observer observer,
        CancellationToken cancellationToken = default) => SaveAsync(observer.Id, "observers", $"""
            INSERT INTO observers (id, name)
            VALUES ({observer.Id.Value}, {observer.Name})
            ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name
            """, cancellationToken);

    public Task<EntitySaveResult> SaveObservationAsync(
        Observation observation,
        CancellationToken cancellationToken = default) => SaveAsync(observation.Id, "observations", $"""
            INSERT INTO observations (id, observer_id, data_id, schema_id, start_at, end_at, time_zone)
            VALUES ({observation.Id.Value}, {observation.ObserverId.Value}, {observation.DataId.Value},
                {observation.SchemaId.Value}, {observation.StartAt?.ToUniversalTime()},
                {observation.EndAt?.ToUniversalTime()}, {observation.TimeZone})
            ON CONFLICT (id) DO UPDATE SET
                observer_id = EXCLUDED.observer_id, data_id = EXCLUDED.data_id,
                schema_id = EXCLUDED.schema_id, start_at = EXCLUDED.start_at,
                end_at = EXCLUDED.end_at, time_zone = EXCLUDED.time_zone
            """, cancellationToken);

    public Task<EntitySaveResult> SaveObservationSchemaAsync(
        ObservationSchema schema,
        CancellationToken cancellationToken = default) => SaveAsync(schema.Id, "observation_schemas", $"""
            INSERT INTO observation_schemas (id, name, schema, start_at, end_at)
            VALUES ({schema.Id.Value}, {schema.Name}, CAST({schema.Schema.GetRawText()} AS jsonb),
                {schema.StartAt?.ToUniversalTime()}, {schema.EndAt?.ToUniversalTime()})
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name, schema = EXCLUDED.schema,
                start_at = EXCLUDED.start_at, end_at = EXCLUDED.end_at
            """, cancellationToken);

    private async Task<EntitySaveResult> SaveAsync(
        EntityId id,
        string tableName,
        FormattableString writeContent,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO entities (id, table_name)
            VALUES ({id.Value}, {tableName})
            ON CONFLICT (id) DO NOTHING
            """, cancellationToken).ConfigureAwait(false);

        // Lock the stable identity before checking its category or replacing content.
        var index = await dbContext.Entities.FromSqlInterpolated($"""
            SELECT id, table_name FROM entities WHERE id = {id.Value} FOR UPDATE
            """).AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);

        if (index.TableName != tableName)
        {
            return EntitySaveResult.CategoryConflict;
        }

        try
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(writeContent, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState is "22003" or "22021" or "22P02" or "22P05")
        {
            // JSON/text values outside PostgreSQL's representation limits are invalid input.
            return EntitySaveResult.InvalidContent;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted == 1 ? EntitySaveResult.Created : EntitySaveResult.SavedExisting;
    }

    public async Task<StoredEntity?> ReadAsync(
        EntityId id,
        CancellationToken cancellationToken = default)
    {
        var index = await dbContext.Entities.AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (index is null)
        {
            return null;
        }

        return index.TableName switch
        {
            "observers" => new StoredEntity(EntityCategories.Observer,
                await dbContext.Observers.AsNoTracking()
                    .SingleAsync(entity => entity.Id == id, cancellationToken).ConfigureAwait(false)),
            "observations" => new StoredEntity(EntityCategories.Observation,
                await dbContext.Observations.AsNoTracking()
                    .SingleAsync(entity => entity.Id == id, cancellationToken).ConfigureAwait(false)),
            "observation_schemas" => new StoredEntity(EntityCategories.ObservationSchema,
                await dbContext.ObservationSchemas.AsNoTracking()
                    .SingleAsync(entity => entity.Id == id, cancellationToken).ConfigureAwait(false)),
            "entity_data" => new StoredEntity(EntityCategories.Entity,
                (await dbContext.EntityData.AsNoTracking()
                    .SingleAsync(entity => entity.Id == id, cancellationToken).ConfigureAwait(false)).Data),
            _ => throw new InvalidOperationException("The entity index names an unsupported storage table."),
        };
    }
}
