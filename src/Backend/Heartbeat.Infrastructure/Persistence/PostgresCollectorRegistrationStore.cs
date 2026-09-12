using System.Data;
using System.Data.Common;
using Heartbeat.Application.Recording;
using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;

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

    public async Task<RegisteredCollector?> RegisterAsync(
        Guid ownerId,
        Guid candidateCollectorId,
        CollectorRegistration registration,
        DateTimeOffset createdAt,
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
            await using var command = connection.CreateCommand();
            command.CommandText = RegisterSql;
            AddParameter(command, "owner_id", ownerId);
            AddParameter(command, "collector_id", candidateCollectorId);
            AddParameter(command, "key", registration.Key);
            AddParameter(command, "target", registration.Target);
            AddParameter(command, "display_name", registration.DisplayName);
            AddParameter(command, "created_at", createdAt);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new RegisteredCollector(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4));
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
