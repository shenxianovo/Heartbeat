using System.Data.Common;
using Heartbeat.Application.Recording;
using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Heartbeat.Persistence;

internal sealed class PostgresCollectorRegistrationStore(HeartbeatDbContext dbContext)
    : ICollectorRegistrationStore
{
    private const string RegisterSql = """
        WITH owner_timeline AS (
            SELECT id
            FROM timelines
            WHERE owner_id = @owner_id
        ), registered AS (
            INSERT INTO collectors (id, timeline_id, key, target, display_name, created_at)
            SELECT @collector_id, owner_timeline.id, @key, @target, @display_name, @created_at
            FROM owner_timeline
            ON CONFLICT (timeline_id, key, target)
            DO UPDATE SET display_name = EXCLUDED.display_name
            RETURNING id, key, target, display_name, created_at
        ), created_objects AS (
            INSERT INTO objects (id, owner_id)
            SELECT id, @owner_id FROM registered
            ON CONFLICT (id) DO NOTHING
        )
        SELECT id, key, target, display_name, created_at FROM registered;
        """;

    public async Task<RegisteredCollector> RegisterAsync(
        Timeline candidateTimeline,
        RecordingObject candidateObject,
        CollectorRegistration registration,
        CancellationToken cancellationToken = default)
    {
        if (candidateObject.OwnerId != candidateTimeline.OwnerId)
            throw new ArgumentException("The object must belong to the Timeline owner.", nameof(candidateObject));

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            WITH resolved AS (
            INSERT INTO timelines (id, owner_id, display_name, created_at)
            VALUES ({candidateTimeline.Id}, {candidateTimeline.OwnerId},
                    {candidateTimeline.DisplayName}, {candidateTimeline.CreatedAt})
            ON CONFLICT (owner_id) DO UPDATE SET id = timelines.id
            RETURNING id
            )
            INSERT INTO objects(id, owner_id)
            SELECT id, {candidateTimeline.OwnerId} FROM resolved
            ON CONFLICT (id) DO NOTHING;
            """, cancellationToken);

        // A separate statement sees the winning Timeline after a concurrent insert completes.
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = RegisterSql;
        AddParameter(command, "owner_id", candidateTimeline.OwnerId);
        AddParameter(command, "collector_id", candidateObject.Id);
        AddParameter(command, "key", registration.Key);
        AddParameter(command, "target", registration.Target);
        AddParameter(command, "display_name", registration.DisplayName);
        AddParameter(command, "created_at", candidateTimeline.CreatedAt);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Collector registration returned no result.");
        }

        var result = new RegisteredCollector(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4));
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
