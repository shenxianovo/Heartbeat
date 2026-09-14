using System.Data;
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
        )
        INSERT INTO collectors (id, timeline_id, key, target, display_name, created_at)
        SELECT @collector_id, owner_timeline.id, @key, @target, @display_name, @created_at
        FROM owner_timeline
        ON CONFLICT (timeline_id, key, target)
        DO UPDATE SET display_name = EXCLUDED.display_name
        RETURNING id, key, target, display_name, created_at;
        """;

    public async Task<RegisteredCollector> RegisterAsync(
        Timeline candidateTimeline,
        Guid candidateCollectorId,
        CollectorRegistration registration,
        CancellationToken cancellationToken = default)
    {
        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State is not ConnectionState.Open;

        if (shouldClose)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO timelines (id, owner_id, display_name, created_at)
                VALUES ({candidateTimeline.Id}, {candidateTimeline.OwnerId},
                        {candidateTimeline.DisplayName}, {candidateTimeline.CreatedAt})
                ON CONFLICT (owner_id) DO NOTHING;
                """, cancellationToken);

            // A separate statement sees the winning Timeline after a concurrent insert completes.
            await using var command = connection.CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = RegisterSql;
            AddParameter(command, "owner_id", candidateTimeline.OwnerId);
            AddParameter(command, "collector_id", candidateCollectorId);
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
        finally
        {
            if (shouldClose)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
