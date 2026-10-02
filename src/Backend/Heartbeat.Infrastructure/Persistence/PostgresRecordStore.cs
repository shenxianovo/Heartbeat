using System.Text.Json;
using Heartbeat.Persistence.Configurations;
using System.Data;
using System.Data.Common;
using Heartbeat.Application.Recording;
using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Heartbeat.Persistence;

internal sealed class PostgresRecordStore(HeartbeatDbContext dbContext) : IRecordStore
{
    private const string WriteSql = """
        WITH owned_track AS (
            SELECT tracks.id, tracks.time_mode, collectors.timeline_id
            FROM tracks
            JOIN collectors ON collectors.id = tracks.collector_id
            JOIN timelines ON timelines.id = collectors.timeline_id
            WHERE tracks.id = @track_id AND timelines.owner_id = @owner_id
        ), accepted_track AS (
            SELECT id, timeline_id
            FROM owned_track
            WHERE (@ended_at IS NULL AND time_mode = 'point')
               OR (@ended_at IS NOT NULL AND time_mode = 'range')
        ), written AS (
            INSERT INTO records (id, track_id, started_at, ended_at, observed_at, received_at, value, objects)
            SELECT @id, accepted_track.id, @started_at, @ended_at, @observed_at, @received_at,
                   CAST(@value AS jsonb), CAST(@objects AS jsonb)
            FROM accepted_track
            ON CONFLICT (id) DO UPDATE
            SET ended_at = CASE
                    WHEN records.ended_at IS NULL THEN NULL
                    ELSE GREATEST(records.ended_at, EXCLUDED.ended_at)
                END
            WHERE records.track_id = EXCLUDED.track_id
              AND records.started_at = EXCLUDED.started_at
              AND records.observed_at IS NOT DISTINCT FROM EXCLUDED.observed_at
              AND records.value = EXCLUDED.value
              AND records.objects = EXCLUDED.objects
              AND ((records.ended_at IS NULL AND EXCLUDED.ended_at IS NULL)
                   OR (records.ended_at IS NOT NULL AND EXCLUDED.ended_at IS NOT NULL))
            RETURNING ended_at, received_at
        ), resolved_objects AS (
            INSERT INTO objects(id, timeline_id, identity_namespace, identity_key, name, name_observed_at, name_record_id)
            SELECT candidate.id, accepted_track.timeline_id, candidate.namespace, candidate.key, candidate.name,
                   CASE WHEN candidate.name IS NOT NULL THEN @name_at END,
                   CASE WHEN candidate.name IS NOT NULL THEN @id END
            FROM jsonb_to_recordset(CAST(@object_candidates AS jsonb))
                 AS candidate(id uuid, namespace text, key text, name text)
            CROSS JOIN accepted_track CROSS JOIN written
            ORDER BY candidate.namespace COLLATE "C", candidate.key COLLATE "C"
            ON CONFLICT (timeline_id, identity_namespace, identity_key) DO UPDATE
            SET name = CASE WHEN EXCLUDED.name IS NOT NULL AND
                    (objects.name_observed_at IS NULL OR
                     (EXCLUDED.name_observed_at, EXCLUDED.name_record_id) > (objects.name_observed_at, objects.name_record_id))
                    THEN EXCLUDED.name ELSE objects.name END,
                name_observed_at = CASE WHEN EXCLUDED.name IS NOT NULL AND
                    (objects.name_observed_at IS NULL OR
                     (EXCLUDED.name_observed_at, EXCLUDED.name_record_id) > (objects.name_observed_at, objects.name_record_id))
                    THEN EXCLUDED.name_observed_at ELSE objects.name_observed_at END,
                name_record_id = CASE WHEN EXCLUDED.name IS NOT NULL AND
                    (objects.name_observed_at IS NULL OR
                     (EXCLUDED.name_observed_at, EXCLUDED.name_record_id) > (objects.name_observed_at, objects.name_record_id))
                    THEN EXCLUDED.name_record_id ELSE objects.name_record_id END
            RETURNING id, identity_namespace, identity_key
        ), linked_objects AS (
            INSERT INTO record_objects(record_id, object_id, role)
            SELECT @id, resolved.id, reference.role
            FROM jsonb_to_recordset(CAST(@objects AS jsonb)) AS reference(role text, namespace text, key text)
            JOIN resolved_objects resolved ON resolved.identity_namespace = reference.namespace
                AND resolved.identity_key = reference.key
            ON CONFLICT DO NOTHING
        )
        SELECT EXISTS (SELECT 1 FROM owned_track),
               EXISTS (SELECT 1 FROM accepted_track),
               (SELECT ended_at FROM written),
               (SELECT received_at FROM written),
               EXISTS (SELECT 1 FROM written);
        """;

    public async Task<RecordWriteResult> WriteAsync(
        Guid ownerId,
        Record record,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner is required.", nameof(ownerId));
        }

        ArgumentNullException.ThrowIfNull(record);

        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State is not ConnectionState.Open;
        if (shouldClose)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = WriteSql;
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            AddRecordParameters(command, ownerId, record);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The record write did not return a result.");
            }

            if (!reader.GetBoolean(0))
            {
                return new RecordWriteResult.TrackNotFound();
            }

            if (!reader.GetBoolean(1))
            {
                throw new ArgumentException("The record end time does not match the Track definition.", nameof(record));
            }

            if (!reader.GetBoolean(4))
            {
                return new RecordWriteResult.Conflict();
            }

            return new RecordWriteResult.Stored(
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetFieldValue<DateTimeOffset>(3));
        }
        finally
        {
            if (shouldClose)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private static void AddRecordParameters(DbCommand command, Guid ownerId, Record record)
    {
        AddParameter(command, "owner_id", ownerId);
        AddParameter(command, "id", record.Id);
        AddParameter(command, "track_id", record.TrackId);
        AddParameter(command, "started_at", record.StartedAt);
        AddParameter(command, "ended_at", record.EndedAt, DbType.DateTimeOffset);
        AddParameter(command, "observed_at", record.ObservedAt, DbType.DateTimeOffset);
        AddParameter(command, "received_at", record.ReceivedAt);
        AddParameter(command, "value", record.Value.GetRawText());
        AddParameter(command, "objects", JsonSerializer.Serialize(record.Objects, ObjectReferencesConverter.Options));
        AddParameter(command, "name_at", record.ObservedAt ?? record.StartedAt);
        AddParameter(command, "object_candidates", JsonSerializer.Serialize(record.Objects
            .DistinctBy(item => (item.Namespace, item.Key))
            .Select(item => new { id = Guid.CreateVersion7(), item.Namespace, item.Key, item.Name }),
            ObjectReferencesConverter.Options));
    }

    private static void AddParameter(DbCommand command, string name, object? value, DbType? type = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        if (type is not null)
        {
            parameter.DbType = type.Value;
        }

        command.Parameters.Add(parameter);
    }
}
