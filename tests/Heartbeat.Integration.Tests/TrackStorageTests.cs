using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class TrackStorageTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 12, 9, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("point", true)]
    [InlineData("range", true)]
    [InlineData("unknown", false)]
    public async Task MigratedTracksTableEnforcesTimeModeConstraint(
        string timeMode,
        bool isValid)
    {
        var ownerId = Guid.NewGuid();
        await ProvisionTimelineAsync(ownerId);
        Guid timelineId;
        await using (var timelineDb = CreateDbContext())
        {
            timelineId = await timelineDb.Timelines
                .Where(x => x.OwnerId == ownerId)
                .Select(x => x.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        var collector = Heartbeat.Recording.Collector.Create(
            timelineId, Heartbeat.Recording.RecordingObject.Create(ownerId),
            "heartbeat.collector.desktop.macos",
            $"device-{Guid.NewGuid():N}",
            "My Mac",
            Now);
        await using (var db = CreateDbContext())
        {
            db.Collectors.Add(collector);
            await db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        var identity = RecordingObject.Create(ownerId);
        await using (var identityDb = CreateDbContext())
        {
            identityDb.Objects.Add(identity);
            await identityDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tracks (id, collector_id, type, version, time_mode, created_at)
            VALUES (@id, @collector_id, 'heartbeat.test', @version, @time_mode, @created_at)
            """;
        command.Parameters.AddWithValue("id", identity.Id);
        command.Parameters.AddWithValue("collector_id", collector.Id);
        command.Parameters.AddWithValue("version", 1);
        command.Parameters.AddWithValue("time_mode", timeMode);
        command.Parameters.AddWithValue("created_at", Now);

        if (isValid)
        {
            await command.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("ck_tracks_time_mode", exception.ConstraintName);
        }
    }
}
