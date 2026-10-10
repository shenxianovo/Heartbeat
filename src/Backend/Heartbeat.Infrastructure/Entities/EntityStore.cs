using System.Globalization;
using System.Text.Json;
using Heartbeat.Application.Entities;
using Heartbeat.Core;
using Heartbeat.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NpgsqlTypes;
using Npgsql;

namespace Heartbeat.Infrastructure.Entities;

public sealed class EntityStore(HeartbeatDbContext dbContext) : IEntityStore
{
    // Schema DDL takes a lock on its FK target (entities). Acquire this coordinator before
    // any identity lock: ordinary saves share it, registration owns it exclusively.
    private const string DdlExclusiveLockSql = "SELECT pg_advisory_xact_lock(104832, 1)";
    private const string DdlSharedLockSql = "SELECT pg_advisory_xact_lock_shared(104832, 1)";
    private static readonly string[] SystemColumns = ["id", "__omitted_fields"];

    public async Task<EntitySaveResult> SaveEntityAsync(
        string resourceName, EntityId id, JsonElement references, JsonElement properties,
        CancellationToken cancellationToken = default)
    {
        var schema = await dbContext.EntitySchemas.AsNoTracking()
            .SingleOrDefaultAsync(s => s.ResourceName == resourceName, cancellationToken);
        if (schema is null) return EntitySaveResult.ResourceNotFound;
        if (!EntitySchemaRules.ValidEntity(schema.Fields, references, properties)) return EntitySaveResult.InvalidContent;
        var table = BusinessTable.From(schema.ResourceName, schema.Fields)!;
        var omitted = table.Columns.Where(c => !(c.Reference ? references : properties)
            .TryGetProperty(c.FieldName, out _)).Select(c => c.FieldName).ToArray();
        var arguments = new List<NpgsqlParameter>
        { new("id", NpgsqlDbType.Uuid) { Value = id.Value }, new("omitted", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = omitted } };
        var values = new List<string> { "@id", "@omitted" };
        foreach (var column in table.Columns)
        {
            var source = column.Reference ? references : properties;
            object value = DBNull.Value;
            if (source.TryGetProperty(column.FieldName, out var json) && json.ValueKind != JsonValueKind.Null)
                value = column.Type switch
                {
                    "string" or "reference" => json.GetString()!,
                    "integer" => EntitySchemaRules.TryInteger(json, out var integer) ? integer.ToString(CultureInfo.InvariantCulture) : throw new InvalidOperationException("Invalid integer passed validation."),
                    _ => json.GetRawText(),
                };
            var parameterName = "field" + arguments.Count;
            values.Add($"CAST(@{parameterName} AS {column.SqlType})");
            arguments.Add(new NpgsqlParameter(parameterName, NpgsqlDbType.Text) { Value = value });
        }
        var columns = SystemColumns.Concat(table.Columns.Select(c => c.Name)).ToArray();
        var sql = $"INSERT INTO {BusinessTable.Quote(table.Name)} ({string.Join(", ", columns.Select(BusinessTable.Quote))}) "
            + $"VALUES ({string.Join(", ", values)}) ON CONFLICT (id) DO UPDATE SET "
            + string.Join(", ", columns.Skip(1).Select(c => $"{BusinessTable.Quote(c)} = EXCLUDED.{BusinessTable.Quote(c)}"));
        return await SaveAsync(id, table.Name, () => ExecuteCommandAsync(sql, arguments, cancellationToken), cancellationToken);
    }

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
            INSERT INTO observations (id, observer_id, content_id, schema_id, start_at, end_at, time_zone)
            VALUES ({observation.Id.Value}, {observation.ObserverId.Value}, {observation.ContentId.Value},
                {observation.SchemaId.Value}, {observation.StartAt?.ToUniversalTime()},
                {observation.EndAt?.ToUniversalTime()}, {observation.TimeZone})
            ON CONFLICT (id) DO UPDATE SET
                observer_id = EXCLUDED.observer_id, content_id = EXCLUDED.content_id,
                schema_id = EXCLUDED.schema_id, start_at = EXCLUDED.start_at,
                end_at = EXCLUDED.end_at, time_zone = EXCLUDED.time_zone
            """, cancellationToken);

    public async Task<EntitySaveResult> SaveEntitySchemaAsync(
        EntitySchema schema, CancellationToken cancellationToken = default)
    {
        if (!EntitySchemaRules.ValidResource(schema.ResourceName) || !EntitySchemaRules.ValidFields(schema.Fields))
            return EntitySaveResult.InvalidContent;
        var table = BusinessTable.From(schema.ResourceName, schema.Fields);
        if (table is null) return EntitySaveResult.InvalidContent;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await CoordinateDdlAsync(true, cancellationToken);
        var inserted = await LockIdentityAsync(schema.Id, "entity_schemas", cancellationToken);
        if (inserted is null) return EntitySaveResult.CategoryConflict;
        var current = await dbContext.EntitySchemas.AsNoTracking().SingleOrDefaultAsync(s => s.Id == schema.Id, cancellationToken);
        try
        {
            if (current is not null)
            {
                // jsonb equality ignores object key ordering while keeping all declared constraints.
                var same = await dbContext.Database.SqlQuery<int>($"SELECT CASE WHEN fields = CAST({schema.Fields.GetRawText()} AS jsonb) THEN 1 ELSE 0 END AS \"Value\" FROM entity_schemas WHERE id = {schema.Id.Value}")
                    .SingleAsync(cancellationToken);
                if (current.ResourceName != schema.ResourceName || same != 1) return EntitySaveResult.SchemaConflict;
            }
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO entity_schemas (id, name, resource_name, fields)
                VALUES ({schema.Id.Value}, {schema.Name}, {schema.ResourceName}, CAST({schema.Fields.GetRawText()} AS jsonb))
                ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name
                """, cancellationToken);
            if (current is null) await ExecuteCommandAsync(table.CreateSql(), [], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return inserted == 1 ? EntitySaveResult.Created : EntitySaveResult.SavedExisting;
        }
        catch (PostgresException exception) when (exception.SqlState is "23505" or "42P07")
        {
            return EntitySaveResult.SchemaConflict;
        }
        catch (PostgresException exception) when (InvalidRepresentation(exception) || exception.SqlState == "42701")
        {
            // PostgreSQL also rejects built-in system column names in otherwise valid declarations.
            return EntitySaveResult.InvalidContent;
        }
    }

    private static bool InvalidRepresentation(PostgresException exception) =>
        exception.SqlState is "22003" or "22021" or "22P02" or "22P05";

    private async Task<int?> LockIdentityAsync(EntityId id, string tableName, CancellationToken cancellationToken)
    {
        var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO entities (id, table_name) VALUES ({id.Value}, {tableName}) ON CONFLICT (id) DO NOTHING
            """, cancellationToken);
        var index = await dbContext.Entities.FromSqlInterpolated($"""
            SELECT id, table_name FROM entities WHERE id = {id.Value} FOR UPDATE
            """).AsNoTracking().SingleAsync(cancellationToken);
        return index.TableName == tableName ? inserted : null;
    }

    private Task<EntitySaveResult> SaveAsync(EntityId id, string tableName,
        FormattableString writeContent, CancellationToken cancellationToken) =>
        SaveAsync(id, tableName, () => dbContext.Database.ExecuteSqlInterpolatedAsync(writeContent, cancellationToken), cancellationToken);

    private async Task<EntitySaveResult> SaveAsync(
        EntityId id,
        string tableName,
        Func<Task<int>> writeContent,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await CoordinateDdlAsync(false, cancellationToken);
        var inserted = await LockIdentityAsync(id, tableName, cancellationToken);
        if (inserted is null) return EntitySaveResult.CategoryConflict;

        try
        {
            await writeContent().ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            InvalidRepresentation(exception))
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
            "entity_schemas" => new StoredEntity(EntityCategories.EntitySchema,
                await dbContext.EntitySchemas.AsNoTracking()
                    .SingleAsync(entity => entity.Id == id, cancellationToken).ConfigureAwait(false)),
            _ => await ReadBusinessAsync(id, index.TableName, cancellationToken),
        };
    }

    private async Task<StoredEntity> ReadBusinessAsync(EntityId id, string tableName, CancellationToken cancellationToken)
    {
        var schema = await dbContext.EntitySchemas.AsNoTracking().SingleAsync(
            s => "entity_" + s.ResourceName.Replace("-", "_") == tableName, cancellationToken);
        var table = BusinessTable.From(schema.ResourceName, schema.Fields)!;
        // PostgreSQL builds JSON directly, so arbitrary numeric precision is not converted through CLR floating point.
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT row_to_json(content)::text FROM {BusinessTable.Quote(tableName)} content WHERE id = @id",
            (NpgsqlConnection)dbContext.Database.GetDbConnection());
        command.Parameters.AddWithValue("id", id.Value);
        var text = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        using var document = JsonDocument.Parse(text);
        var row = document.RootElement;
        var omitted = row.GetProperty("__omitted_fields").EnumerateArray().Select(v => v.GetString()).ToHashSet();
        var references = new Dictionary<string, JsonElement>();
        var properties = new Dictionary<string, JsonElement>();
        foreach (var column in table.Columns)
            if (!omitted.Contains(column.FieldName))
                (column.Reference ? references : properties).Add(column.FieldName, row.GetProperty(column.Name).Clone());
        return new StoredEntity(EntityCategories.Entity, new BusinessEntity(
            JsonSerializer.SerializeToElement(references), JsonSerializer.SerializeToElement(properties)));
    }

    private Task<int> CoordinateDdlAsync(bool exclusive, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlRawAsync(exclusive
            ? DdlExclusiveLockSql
            : DdlSharedLockSql, cancellationToken);

    private async Task<int> ExecuteCommandAsync(string sql, IEnumerable<NpgsqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)dbContext.Database.GetDbConnection(),
            (NpgsqlTransaction?)dbContext.Database.CurrentTransaction?.GetDbTransaction());
        foreach (var parameter in parameters) command.Parameters.Add(parameter);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
