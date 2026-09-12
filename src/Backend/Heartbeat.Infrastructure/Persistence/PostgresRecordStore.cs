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
            SELECT tracks.id, tracks.time_mode, tracks.end_mode
            FROM tracks
            JOIN collectors ON collectors.id = tracks.collector_id
            JOIN timelines ON timelines.id = collectors.timeline_id
            WHERE tracks.id = @track_id AND timelines.owner_id = @owner_id
        ), accepted_track AS (
            SELECT id
            FROM owned_track
            WHERE (@ended_at IS NULL AND (
                       (time_mode = 'point' AND end_mode IS NULL)
                       OR (time_mode = 'range' AND end_mode = 'next_record')
                   ))
               OR (@ended_at IS NOT NULL AND time_mode = 'range' AND end_mode = 'explicit')
        ), written AS (
            INSERT INTO records (id, track_id, started_at, ended_at, observed_at, received_at, value)
            SELECT @id, accepted_track.id, @started_at, @ended_at, @observed_at, @received_at,
                   CAST(@value AS jsonb)
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
              AND ((records.ended_at IS NULL AND EXCLUDED.ended_at IS NULL)
                   OR (records.ended_at IS NOT NULL AND EXCLUDED.ended_at IS NOT NULL))
            RETURNING ended_at, received_at
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
            AddParameter(command, "owner_id", ownerId);
            AddParameter(command, "id", record.Id);
            AddParameter(command, "track_id", record.TrackId);
            AddParameter(command, "started_at", record.StartedAt);
            AddParameter(command, "ended_at", record.EndedAt, DbType.DateTimeOffset);
            AddParameter(command, "observed_at", record.ObservedAt, DbType.DateTimeOffset);
            AddParameter(command, "received_at", record.ReceivedAt);
            AddParameter(command, "value", record.Value.GetRawText());

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
