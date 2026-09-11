using System.Text.Json;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class ExperienceServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    private static ResolvedCalendarWindow Window => LocalCalendarWindowValidator.ResolveDay(new()
    {
        Version = 1, Kind = "day", LocalDate = "2026-09-01", TimeZone = "Etc/UTC",
        Start = Start, EndExclusive = Start.AddDays(1),
    }).Window!;

    [Fact]
    public async Task RawAccountFacts_KeepPayloadAndBoundaries_WithoutActivityKeyOrApp()
    {
        await using var db = CreateDbContext();
        var batch = FactStoreTests.SegmentBatch("account");
        batch.Streams[0].Source = "custom.observation";
        batch.Facts[0].Start = Start.AddHours(-1);
        batch.Facts[0].End = Start.AddHours(1);
        batch.Facts[0].Payload = JsonSerializer.SerializeToElement(new { arbitrary = new[] { 1, 2, 3 } });
        await new FactStore(db).IngestAsync("owner", batch);
        var service = new ExperienceService(db);
        var row = Assert.Single((await service.ReadAsync("owner", Window, null)).Items);
        Assert.Null(row.TargetKind);
        Assert.Null(row.TargetId);
        Assert.Null(row.AppIdentityId);
        Assert.Equal(Start.AddHours(-1), row.StartTime);
        Assert.Equal(3, row.Payload.GetProperty("arbitrary").GetArrayLength());
        Assert.Empty((await service.ReadAsync("other-owner", Window, null)).Items);
    }

    [Fact]
    public async Task CursorReads_AllShortFactsWithIdenticalStart_WithoutDuplicatesOrTruncation()
    {
        await using var db = CreateDbContext();
        var batch = FactStoreTests.SegmentBatch("account");
        batch.Streams[0].Source = "vrchat.account";
        await new FactStore(db).IngestAsync("owner", batch);
        var original = await db.Segments.SingleAsync();
        for (var i = 0; i < ExperienceService.PageSize + 7; i++)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), OwnerId = "owner", StreamId = original.StreamId,
                FactId = Guid.NewGuid(), Revision = 1, Source = original.Source,
                StartTime = original.StartTime, EndTime = original.StartTime.AddTicks(10),
                Payload = JsonDocument.Parse("{}"),
            });
        await db.SaveChangesAsync();
        var service = new ExperienceService(db);
        var first = await service.ReadAsync("owner", Window, null);
        Assert.Equal(ExperienceService.PageSize, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var second = await service.ReadAsync("owner", Window, first.NextCursor);
        Assert.Equal(8, second.Items.Count);
        Assert.Null(second.NextCursor);
        Assert.Equal(ExperienceService.PageSize + 8, first.Items.Concat(second.Items).Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public async Task HalfOpenWindow_ExcludesTouchingIntervals()
    {
        await using var db = CreateDbContext();
        var batch = FactStoreTests.SegmentBatch("account");
        batch.Facts[0].Start = Start.AddMinutes(-1);
        batch.Facts[0].End = Start;
        await new FactStore(db).IngestAsync("owner", batch);
        batch.Facts[0].FactId = Guid.CreateVersion7();
        batch.Facts[0].Start = Start.AddDays(1);
        batch.Facts[0].End = Start.AddDays(1).AddMinutes(1);
        await new FactStore(db).IngestAsync("owner", batch);
        Assert.Empty((await new ExperienceService(db).ReadAsync("owner", Window, null)).Items);
        batch.Facts[0].FactId = Guid.CreateVersion7();
        batch.Facts[0].Start = batch.Facts[0].End = Start;
        await new FactStore(db).IngestAsync("owner", batch);
        Assert.Single((await new ExperienceService(db).ReadAsync("owner", Window, null)).Items);
    }
}
