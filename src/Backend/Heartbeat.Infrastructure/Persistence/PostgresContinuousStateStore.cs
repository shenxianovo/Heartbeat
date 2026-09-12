using System.Data;
using System.Data.Common;
using Heartbeat.Application.Recording;
using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Heartbeat.Persistence;

internal sealed class PostgresContinuousStateStore(HeartbeatDbContext dbContext) : IContinuousStateStore
{
    private const string WriteSql = """
        WITH owned_track AS (
            SELECT tracks.id, tracks.time_mode, tracks.end_mode
            FROM tracks
            JOIN collectors ON collectors.id = tracks.collector_id
            JOIN timelines ON timelines.id = collectors.timeline_id
            WHERE tracks.id = @track_id AND timelines.owner_id = @owner_id
        ), written AS (
            INSERT INTO records (id, track_id, started_at, ended_at, observed_at, received_at, value)
            SELECT @id, owned_track.id, @started_at, @ended_at, @observed_at, @received_at,
                   CAST(@value AS jsonb)
            FROM owned_track
            WHERE time_mode = 'range' AND end_mode = 'explicit'
            ON CONFLICT (id) DO UPDATE
            SET ended_at = GREATEST(records.ended_at, EXCLUDED.ended_at)
            WHERE records.track_id = EXCLUDED.track_id
              AND records.started_at = EXCLUDED.started_at
              AND records.observed_at IS NOT DISTINCT FROM EXCLUDED.observed_at
              AND records.value = EXCLUDED.value
              AND records.ended_at IS NOT NULL
            RETURNING ended_at, received_at
        )
        SELECT EXISTS (SELECT 1 FROM owned_track),
               EXISTS (SELECT 1 FROM owned_track WHERE time_mode = 'range' AND end_mode = 'explicit'),
               (SELECT ended_at FROM written),
               (SELECT received_at FROM written);
        """;

    public async Task<ContinuousStateWriteResult> WriteAsync(
        Guid ownerId,
        Record record,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner is required.", nameof(ownerId));
        }

        ArgumentNullException.ThrowIfNull(record);
        if (record.EndedAt is null)
        {
            throw new ArgumentException("Continuous state requires an explicit end time.", nameof(record));
        }

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
            AddParameter(command, "ended_at", record.EndedAt.Value);
            AddParameter(command, "observed_at", record.ObservedAt, DbType.DateTimeOffset);
            AddParameter(command, "received_at", record.ReceivedAt);
            AddParameter(command, "value", record.Value.GetRawText());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The continuous state write did not return a result.");
            }

            if (!reader.GetBoolean(0))
            {
                return new ContinuousStateWriteResult.TrackNotFound();
            }

            if (!reader.GetBoolean(1))
            {
                throw new ArgumentException("Continuous state requires a range + explicit Track.", nameof(record));
            }

            return reader.IsDBNull(2)
                ? new ContinuousStateWriteResult.Conflict()
                : new ContinuousStateWriteResult.Stored(
                    reader.GetFieldValue<DateTimeOffset>(2),
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
