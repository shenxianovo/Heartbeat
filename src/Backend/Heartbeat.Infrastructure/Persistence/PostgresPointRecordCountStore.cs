using System.Data;
using Heartbeat.Application.Recording;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Persistence;

internal sealed class PostgresPointRecordCountStore(HeartbeatDbContext dbContext) : IPointRecordCountStore
{
    public async Task<PointRecordCounts?> CountAsync(
        Guid ownerId,
        CountPointRecordsQuery query,
        CancellationToken cancellationToken = default)
    {
        var track = await ObjectQueries.FindTrackAsync(dbContext, ownerId, query.TrackId, cancellationToken);

        if (track is null)
        {
            return null;
        }
        if (track.TimeMode != global::Heartbeat.Recording.TimeMode.Point)
        {
            return new PointRecordCounts(track, query.From, query.To, query.BucketSeconds, []);
        }

        var buckets = new List<PointRecordCountBucket>();
        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT floor(extract(epoch FROM (started_at - @from)) / @bucket_seconds)::integer,
                       count(*)::bigint
                FROM records r
                WHERE track_id = @track_id AND started_at >= @from AND started_at < @to
                  AND (@object_id IS NULL OR EXISTS (SELECT 1 FROM record_objects o WHERE o.record_id = r.id AND o.object_id = @object_id))
                  AND NOT EXISTS (SELECT 1 FROM unnest(@context_ids::uuid[]) AS context(id)
                      WHERE NOT EXISTS (SELECT 1 FROM record_objects o WHERE o.record_id = r.id AND o.object_id = context.id))
                GROUP BY 1
                ORDER BY 1
                """;
            AddParameter(command, "track_id", query.TrackId);
            AddParameter(command, "from", query.From);
            AddParameter(command, "to", query.To);
            AddParameter(command, "bucket_seconds", query.BucketSeconds);
            AddParameter(command, "object_id", query.ObjectId);
            AddParameter(command, "context_ids", query.ContextObjectIds?.ToArray() ?? Array.Empty<Guid>());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var index = reader.GetInt32(0);
                var startedAt = query.From.AddSeconds((long)index * query.BucketSeconds);
                var endedAt = startedAt.AddSeconds(query.BucketSeconds);
                if (endedAt > query.To)
                {
                    endedAt = query.To;
                }
                buckets.Add(new PointRecordCountBucket(index, startedAt, endedAt, reader.GetInt64(1)));
            }
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }

        return new PointRecordCounts(track, query.From, query.To, query.BucketSeconds, buckets);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        if (name == "object_id") parameter.DbType = DbType.Guid;
        command.Parameters.Add(parameter);
    }
}
