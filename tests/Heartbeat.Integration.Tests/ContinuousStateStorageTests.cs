using System.Text.Json;
using Heartbeat.Application.Recording;
using Heartbeat.Infrastructure;
using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RecordingRecord = Heartbeat.Recording.Record;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class RecordStorageTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 12, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task FirstWritePersistsConfirmedInterval()
    {
        var ownerId = Guid.NewGuid();
        var track = await CreateTrackAsync(ownerId);
        var record = CreateRecord(track, Guid.CreateVersion7(), 1);
        await using var services = CreateServices();

        var result = Assert.IsType<RecordWriteResult.Stored>(
            await WriteAsync(services, ownerId, record));

        Assert.Equal(Now.AddMinutes(1), result.EndedAt);
        Assert.Equal(record.ReceivedAt, result.ReceivedAt);
        await using var db = CreateDbContext();
        var stored = await db.Records.SingleAsync();
        Assert.Equal(record.Id, stored.Id);
        Assert.Equal(record.TrackId, stored.TrackId);
        Assert.Equal(record.StartedAt, stored.StartedAt);
        Assert.Equal(record.EndedAt, stored.EndedAt);
        Assert.Null(stored.ObservedAt);
        Assert.Equal("browser", stored.Value.GetProperty("application").GetString());
    }

    [Fact]
    public async Task RepeatedAndOutOfOrderWritesOnlyExtendEndAndKeepFirstReceipt()
    {
        var ownerId = Guid.NewGuid();
        var track = await CreateTrackAsync(ownerId);
        var id = Guid.CreateVersion7();
        await using var services = CreateServices();

        foreach (var minutes in new[] { 3, 1, 3, 5, 2 })
        {
            var record = CreateRecord(track, id, minutes);
            var result = Assert.IsType<RecordWriteResult.Stored>(
                await WriteAsync(services, ownerId, record));
            Assert.Equal(Now.AddMinutes(minutes == 5 || minutes == 2 ? 5 : 3), result.EndedAt);
            Assert.Equal(Now.AddMinutes(13), result.ReceivedAt);
        }

        await using var db = CreateDbContext();
        Assert.Equal(Now.AddMinutes(5), (await db.Records.SingleAsync()).EndedAt);
    }

    [Fact]
    public async Task ConcurrentFirstWritesAndRenewalsConvergeToLargestEnd()
    {
        var ownerId = Guid.NewGuid();
        var track = await CreateTrackAsync(ownerId);
        var id = Guid.CreateVersion7();
        await using var services = CreateServices();

        var results = await Task.WhenAll(Enumerable.Range(1, 12)
            .Select(minutes => WriteAsync(services, ownerId, CreateRecord(track, id, minutes))));

        Assert.All(results, result => Assert.IsType<RecordWriteResult.Stored>(result));
        await using var db = CreateDbContext();
        Assert.Equal(Now.AddMinutes(12), (await db.Records.SingleAsync()).EndedAt);
    }

    [Fact]
    public async Task ConcurrentConflictingFirstWritesKeepOneCompleteObservation()
    {
        var ownerId = Guid.NewGuid();
        var track = await CreateTrackAsync(ownerId);
        var id = Guid.CreateVersion7();
        await using var services = CreateServices();
        var first = CreateRecord(track, id, 1);
        var second = RecordingRecord.Create(id, track, Now, Now.AddMinutes(5), null,
            Now.AddMinutes(20), JsonSerializer.SerializeToElement(new { application = "editor" }));

        var results = await Task.WhenAll(
            WriteAsync(services, ownerId, first),
            WriteAsync(services, ownerId, second));

        Assert.Single(results.OfType<RecordWriteResult.Stored>());
        Assert.Single(results.OfType<RecordWriteResult.Conflict>());
        var winner = results[0] is RecordWriteResult.Stored ? first : second;
        await using var db = CreateDbContext();
        var stored = await db.Records.SingleAsync();
        Assert.Equal(winner.EndedAt, stored.EndedAt);
        Assert.Equal(winner.ReceivedAt, stored.ReceivedAt);
        Assert.Equal(winner.Value.GetProperty("application").GetString(),
            stored.Value.GetProperty("application").GetString());
    }

    [Theory]
    [InlineData(TimeMode.Point, null)]
    [InlineData(TimeMode.Range, EndMode.NextRecord)]
    public async Task PointAndNextRecordWritesAreIdempotent(TimeMode timeMode, EndMode? endMode)
    {
        var ownerId = Guid.NewGuid();
        var track = await CreateTrackAsync(ownerId, timeMode, endMode);
        var id = Guid.CreateVersion7();
        var record = RecordingRecord.Create(id, track, Now, null, null,
            Now, JsonSerializer.SerializeToElement(new { kind = "arbitrary", count = 3 }));
        await using var services = CreateServices();

        var first = Assert.IsType<RecordWriteResult.Stored>(
            await WriteAsync(services, ownerId, record));
        var repeated = RecordingRecord.Create(id, track, Now, null, null,
            Now.AddHours(1), JsonSerializer.SerializeToElement(new { count = 3, kind = "arbitrary" }));
        var retry = Assert.IsType<RecordWriteResult.Stored>(
            await WriteAsync(services, ownerId, repeated));

        Assert.Null(first.EndedAt);
        Assert.Null(retry.EndedAt);
        Assert.Equal(Now, retry.ReceivedAt);
        await using var db = CreateDbContext();
        var stored = await db.Records.SingleAsync();
        Assert.Null(stored.EndedAt);
        Assert.Equal(3, stored.Value.GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData("track")]
    [InlineData("start")]
    [InlineData("observed")]
    [InlineData("value")]
    public async Task ChangedFixedFieldConflictsWithoutExtendingRecord(string field)
    {
        var ownerId = Guid.NewGuid();
        var track = await CreateTrackAsync(ownerId);
        var otherTrack = await CreateTrackAsync(ownerId);
        var id = Guid.CreateVersion7();
        await using var services = CreateServices();
        await WriteAsync(services, ownerId, CreateRecord(track, id, 1));
        var conflicting = RecordingRecord.Create(
            id,
            field == "track" ? otherTrack : track,
            field == "start" ? Now.AddSeconds(1) : Now,
            Now.AddMinutes(5),
            field == "observed" ? Now.AddSeconds(1) : null,
            Now.AddMinutes(20),
            JsonSerializer.SerializeToElement(new
            {
                application = field == "value" ? "editor" : "browser",
                window = "a",
            }));

        Assert.IsType<RecordWriteResult.Conflict>(
            await WriteAsync(services, ownerId, conflicting));
        await using var db = CreateDbContext();
        var stored = await db.Records.SingleAsync();
        Assert.Equal(Now.AddMinutes(1), stored.EndedAt);
        Assert.Equal(track.Id, stored.TrackId);
        Assert.Equal(Now, stored.StartedAt);
        Assert.Null(stored.ObservedAt);
        Assert.Equal("browser", stored.Value.GetProperty("application").GetString());
    }

    [Fact]
    public async Task EquivalentJsonAndObservationTimeAreAccepted()
    {
        var ownerId = Guid.NewGuid();
        var track = await CreateTrackAsync(ownerId);
        var id = Guid.CreateVersion7();
        await using var services = CreateServices();
        await WriteAsync(services, ownerId, CreateRecord(track, id, 1));
        using var value = JsonDocument.Parse("""{ "window": "a", "application": "browser" }""");
        var repeated = RecordingRecord.Create(
            id, track, Now.ToOffset(TimeSpan.FromHours(8)), Now.AddMinutes(2),
            Now, Now.AddMinutes(20), value.RootElement);

        var result = Assert.IsType<RecordWriteResult.Stored>(
            await WriteAsync(services, ownerId, repeated));

        Assert.Equal(Now.AddMinutes(2), result.EndedAt);
    }

    [Fact]
    public async Task OtherOwnerCannotInsertOrRenewRecordsInTrack()
    {
        var ownerId = Guid.NewGuid();
        var track = await CreateTrackAsync(ownerId);
        var otherOwnerId = Guid.NewGuid();
        var id = Guid.CreateVersion7();
        await using var services = CreateServices();

        Assert.IsType<RecordWriteResult.TrackNotFound>(
            await WriteAsync(services, otherOwnerId, CreateRecord(track, id, 1)));
        await WriteAsync(services, ownerId, CreateRecord(track, id, 1));
        Assert.IsType<RecordWriteResult.TrackNotFound>(
            await WriteAsync(services, otherOwnerId, CreateRecord(track, id, 5)));

        await using var db = CreateDbContext();
        Assert.Equal(Now.AddMinutes(1), (await db.Records.SingleAsync()).EndedAt);
    }

    [Fact]
    public async Task RecordIdentityCannotBeReusedAcrossOwners()
    {
        var ownerId = Guid.NewGuid();
        var track = await CreateTrackAsync(ownerId);
        var otherOwnerId = Guid.NewGuid();
        var otherTrack = await CreateTrackAsync(otherOwnerId);
        var id = Guid.CreateVersion7();
        await using var services = CreateServices();
        await WriteAsync(services, ownerId, CreateRecord(track, id, 1));

        Assert.IsType<RecordWriteResult.Conflict>(
            await WriteAsync(services, otherOwnerId, CreateRecord(otherTrack, id, 5)));

        await using var db = CreateDbContext();
        var stored = await db.Records.SingleAsync();
        Assert.Equal(track.Id, stored.TrackId);
        Assert.Equal(Now.AddMinutes(1), stored.EndedAt);
    }

    [Fact]
    public async Task SameTrackAllowsOverlappingIndependentRecords()
    {
        var ownerId = Guid.NewGuid();
        var track = await CreateTrackAsync(ownerId);
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        await using var services = CreateServices();
        await WriteAsync(services, ownerId, CreateRecord(track, firstId, 1));
        await WriteAsync(services, ownerId, CreateRecord(track, secondId, 2));
        await WriteAsync(services, ownerId, CreateRecord(track, firstId, 5));

        await using var db = CreateDbContext();
        var records = await db.Records.ToDictionaryAsync(record => record.Id);
        Assert.Equal(2, records.Count);
        Assert.Equal(Now.AddMinutes(5), records[firstId].EndedAt);
        Assert.Equal(Now.AddMinutes(2), records[secondId].EndedAt);
    }

    [Fact]
    public async Task MissingTrackDoesNotCreateRecord()
    {
        var track = Track.Create(Guid.NewGuid(), "test.continuous-state", 1,
            TimeMode.Range, EndMode.Explicit, Now);
        await using var services = CreateServices();

        Assert.IsType<RecordWriteResult.TrackNotFound>(
            await WriteAsync(services, Guid.NewGuid(), CreateRecord(track, Guid.CreateVersion7(), 1)));
        await using var db = CreateDbContext();
        Assert.Empty(await db.Records.ToListAsync());
    }

    private async Task<Track> CreateTrackAsync(
        Guid ownerId, TimeMode timeMode = TimeMode.Range, EndMode? endMode = EndMode.Explicit)
    {
        await using var db = CreateDbContext();
        var timeline = await db.Timelines.SingleOrDefaultAsync(item => item.OwnerId == ownerId);
        if (timeline is null)
        {
            timeline = Timeline.Create(ownerId, "Timeline", Now);
            db.Timelines.Add(timeline);
        }

        var collector = Collector.Create(
            timeline.Id, "heartbeat.collector.test", Guid.NewGuid().ToString(), "Test", Now);
        var track = Track.Create(collector.Id, "test.continuous-state", 1,
            timeMode, endMode, Now);
        db.Collectors.Add(collector);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return track;
    }

    private static RecordingRecord CreateRecord(Track track, Guid id, int minutes) =>
        RecordingRecord.Create(id, track, Now, Now.AddMinutes(minutes), null,
            Now.AddMinutes(minutes + 10),
            JsonSerializer.SerializeToElement(new { application = "browser", window = "a" }));

    private ServiceProvider CreateServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Heartbeat"] = ConnectionString,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<RecordWriteResult> WriteAsync(
        IServiceProvider services, Guid ownerId, RecordingRecord record)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRecordStore>()
            .WriteAsync(ownerId, record);
    }
}
