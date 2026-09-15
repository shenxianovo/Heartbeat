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
        var track = await (
            from candidate in dbContext.Tracks.AsNoTracking()
            join collector in dbContext.Collectors.AsNoTracking() on candidate.CollectorId equals collector.Id
            join timeline in dbContext.Timelines.AsNoTracking() on collector.TimelineId equals timeline.Id
            where candidate.Id == query.TrackId && timeline.OwnerId == ownerId
            select new ReplayedTrack(candidate.Id, candidate.CollectorId, candidate.Type,
                candidate.Version, candidate.TimeMode, candidate.EndMode))
            .SingleOrDefaultAsync(cancellationToken);
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
                FROM records
                WHERE track_id = @track_id AND started_at >= @from AND started_at < @to
                GROUP BY 1
                ORDER BY 1
                """;
            AddParameter(command, "track_id", query.TrackId);
            AddParameter(command, "from", query.From);
            AddParameter(command, "to", query.To);
            AddParameter(command, "bucket_seconds", query.BucketSeconds);
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

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
