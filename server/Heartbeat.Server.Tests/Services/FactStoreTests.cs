using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class FactStoreTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompleteSnapshots_ConvergeWithoutRegrowingCorrections()
    {
        await using var db = CreateDbContext();
        var batch = SegmentBatch();
        var store = new FactStore(db);
        await store.IngestAsync("owner", batch);
        batch.Facts[0].Revision = 2;
        batch.Facts[0].End = Start.AddMinutes(2);
        batch.Facts[0].Payload = JsonSerializer.SerializeToElement(new { identityKey = "https://example.com", title = "Corrected", attributes = new { url = "https://example.com/?original=2" } });
        await store.IngestAsync("owner", batch);
        batch.Facts[0].Revision = 1;
        batch.Facts[0].End = Start.AddMinutes(20);
        await store.IngestAsync("owner", batch);
        var rows = await new UsageService(db).GetSegmentsAsync("owner", null, null, null, null, null);
        var row = Assert.Single(rows);
        Assert.Equal(Start.AddMinutes(2), row.EndTime);
        Assert.Equal("Corrected", row.Title);
        Assert.Equal(2, row.Revision);
    }

    [Fact]
    public async Task EqualRevision_IgnoresJsonOrderNumberFormattingAndObservedAt_ButRejectsChangedContentAtomically()
    {
        await using var db = CreateDbContext();
        var batch = SegmentBatch();
        batch.Facts[0].Payload = JsonDocument.Parse("""{"identityKey":"https://example.com","title":"Page","attributes":{"number":1}}""").RootElement;
        var store = new FactStore(db);
        await store.IngestAsync("owner", batch);
        batch.Facts[0].ObservedAt = Start.AddSeconds(1);
        batch.Facts[0].Payload = JsonDocument.Parse("""{"attributes":{"number":1.0},"title":"Page","identityKey":"https://example.com"}""").RootElement;
        await store.IngestAsync("owner", batch);
        Assert.Single(await db.Facts.ToListAsync());
        batch.Facts.Insert(0, new FactSnapshot { StreamId = batch.Streams[0].StreamId, FactId = Guid.CreateVersion7(), Revision = 1, Start = Start, End = Start.AddSeconds(2), IsFinal = false, Payload = Payload() });
        batch.Facts[1].Payload = JsonSerializer.SerializeToElement(new { identityKey = "https://changed.example", title = "Changed", attributes = new { } });
        var error = await Assert.ThrowsAsync<FactIngestException>(() => store.IngestAsync("owner", batch));
        Assert.True(error.IsConflict);
        Assert.Single(await db.Facts.ToListAsync());
        Assert.Single(await db.ActivitySegments.ToListAsync());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("person")]
    public async Task NonMachineSubject_HasNoDevice_AndRemainsOwnerIsolatedInActivityRead(string kind)
    {
        await using var db = CreateDbContext();
        var batch = SegmentBatch(kind);
        await new FactStore(db).IngestAsync("owner", batch);
        Assert.Empty(await db.Devices.ToListAsync());
        var usage = new UsageService(db);
        var row = Assert.Single(await usage.GetSegmentsAsync("owner", null, null, null, null, null));
        Assert.Null(row.DeviceId);
        Assert.Equal(batch.Streams[0].Subject.SubjectId, row.SubjectId);
        Assert.Equal(kind, row.SubjectKind);
        Assert.Equal("Observed subject", row.SubjectName);
        Assert.Equal(batch.Streams[0].StreamId, row.StreamId);
        Assert.Equal(batch.Facts[0].FactId, row.FactId);
        Assert.Equal(1, row.Revision);
        Assert.Equal("https://example.com/?original=1", JsonSerializer.SerializeToElement(row.Payload).GetProperty("attributes").GetProperty("url").GetString());
        Assert.Empty(await usage.GetSegmentsAsync("other", null, null, null, null, null));
        Assert.Empty(await usage.GetUsageAsync("owner", null, null, null));
    }

    [Fact]
    public async Task NativeReplay_TakesOverImportedSegment_WithUppercaseHardwareId_AndLateLegacyCannotRegrow()
    {
        await using var db = CreateDbContext();
        var batch = SegmentBatch();
        var hardware = Guid.NewGuid();
        batch.Streams[0].Subject.HardwareId = hardware.ToString("D");
        var device = new Device { OwnerId = "owner", HardwareId = hardware.ToString("D").ToUpperInvariant(), DeviceName = "Historical Mac" };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        var legacy = LegacySegment(batch);
        var store = new FactStore(db);
        await store.ImportSegmentsAsync(device.Id, [legacy]);
        var oldKey = (await db.Facts.SingleAsync()).Id;
        var archive = (await db.Facts.SingleAsync()).LegacyRecord;
        batch.Facts[0].Revision = 9;
        batch.Facts[0].End = Start.AddMinutes(1);
        await store.IngestAsync("owner", batch);
        Assert.Single(await db.Devices.ToListAsync());
        Assert.Equal(oldKey, (await db.Facts.SingleAsync()).Id);
        Assert.Equal(archive, (await db.Facts.SingleAsync()).LegacyRecord);
        await store.ImportSegmentsAsync(device.Id, [legacy]);
        Assert.Equal(Start.AddMinutes(1), (await db.ActivitySegments.SingleAsync()).EndTime);
    }

    [Fact]
    public async Task NativeFirst_CorrectionThenOldCacheDoesNotRegrowEvenWithoutImportedHistory()
    {
        await using var db = CreateDbContext();
        var batch = SegmentBatch();
        var legacy = LegacySegment(batch);
        var store = new FactStore(db);
        await store.IngestAsync("owner", batch);
        var deviceId = (await db.Devices.SingleAsync()).Id;
        batch.Facts[0].Revision = 2;
        batch.Facts[0].End = Start.AddSeconds(1);
        await store.IngestAsync("owner", batch);
        await store.ImportSegmentsAsync(deviceId, [legacy]);
        Assert.Equal(Start.AddSeconds(1), (await db.ActivitySegments.SingleAsync()).EndTime);
        Assert.Equal(2, (await db.Facts.SingleAsync()).Revision);
    }

    [Fact]
    public async Task OwnerScopedStreams_CannotRewriteAnotherOwnersFacts()
    {
        await using var db = CreateDbContext();
        var batch = SegmentBatch();
        var store = new FactStore(db);
        await store.IngestAsync("owner", batch);
        batch.Facts[0].Payload = JsonSerializer.SerializeToElement(new { identityKey = "other-owner" });
        await store.IngestAsync("other", batch);
        Assert.Equal(2, await db.Facts.CountAsync());
        await Assert.ThrowsAsync<FactIngestException>(() => store.IngestAsync("owner", batch));
        Assert.Equal(2, await db.Facts.CountAsync());
    }

    [Fact]
    public async Task InvalidEnvelopeOrJson_RejectsBeforeAnyDeviceAppOrFactSideEffects()
    {
        await using var db = CreateDbContext();
        var batch = SegmentBatch();
        var store = new FactStore(db);
        batch.Facts[0].Revision = 0;
        await Assert.ThrowsAsync<FactIngestException>(() => store.IngestAsync("owner", batch));
        Assert.Empty(await db.Devices.ToListAsync());
        batch.Facts[0].Revision = 1;
        batch.Facts[0].Payload = JsonDocument.Parse("{\"duplicate\":1,\"duplicate\":2}").RootElement;
        await Assert.ThrowsAsync<FactIngestException>(() => store.IngestAsync("owner", batch));
        Assert.Empty(await db.Facts.ToListAsync());
        Assert.Empty(await db.Apps.ToListAsync());
        Assert.Empty(await db.FactSubjects.ToListAsync());
    }

    [Fact]
    public async Task Gaps_AreDurableIdempotentAndConflictingContentRollsBack()
    {
        await using var db = CreateDbContext();
        var batch = SegmentBatch();
        batch.Gaps.Add(new FactGapSnapshot { StreamId = batch.Streams[0].StreamId, GapId = Guid.CreateVersion7(), Start = Start, End = Start.AddMinutes(1), Reason = "offline", EstimatedFactsLost = 2 });
        var store = new FactStore(db);
        await store.IngestAsync("owner", batch);
        await store.IngestAsync("owner", batch);
        Assert.Single(await db.FactGaps.ToListAsync());
        batch.Gaps[0].Reason = "changed";
        await Assert.ThrowsAsync<FactIngestException>(() => store.IngestAsync("owner", batch));
        Assert.Equal("offline", (await db.FactGaps.SingleAsync()).Reason);
    }

    [Fact]
    public async Task InputEvent_ImportedRawCodeIsPreservedAndNativeReplayCountsOnce()
    {
        await using var db = CreateDbContext();
        var batch = EventBatch();
        var store = new FactStore(db);
        var device = new Device { OwnerId = "owner", HardwareId = "hardware", DeviceName = "PC" };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        var item = new InputEventItem { Id = batch.Facts[0].FactId, Timestamp = Start, EventType = InputEventType.KeyDown, CodeSet = InputCodeSets.WindowsVirtualKeyV1, Code = 65 };
        await store.ImportInputEventsAsync(device.Id, new InputEventUploadRequest { Events = [item] });
        await store.IngestAsync("owner", batch);
        await store.ImportInputEventsAsync(device.Id, new InputEventUploadRequest { Events = [item] });
        Assert.Equal(1, (await new InputEventService(db).GetCountsAsync("owner", null, null, null)).KeyboardTotal);
        Assert.Equal(InputCodeSets.WindowsVirtualKeyV1, (await db.InputEvents.SingleAsync()).CodeSet);
        Assert.NotNull((await db.Facts.SingleAsync()).LegacyRecord);
        batch.Facts[0].Revision = 2;
        await store.IngestAsync("owner", batch);
        Assert.Equal(2, (await db.Facts.SingleAsync()).Revision);
    }

    [Fact]
    public async Task SameFactIdAcrossStreams_RemainsDistinctForEvents()
    {
        await using var db = CreateDbContext();
        var first = EventBatch();
        var second = EventBatch();
        second.Facts[0].FactId = first.Facts[0].FactId;
        var store = new FactStore(db);
        await store.IngestAsync("owner", first);
        await store.IngestAsync("owner", second);
        var third = EventBatch();
        third.Facts[0].FactId = first.Facts[0].FactId;
        await store.IngestAsync("owner", third);
        Assert.Equal(3, await db.Facts.CountAsync());
        Assert.Equal(3, (await new InputEventService(db).GetCountsAsync("owner", null, null, null)).KeyboardTotal);
    }

    [Fact]
    public async Task LegacyInputConflict_RollsBackNewDeviceAndPreservesOriginalOwner()
    {
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        var input = new InputEventItem { Id = Guid.CreateVersion7(), EventType = InputEventType.KeyDown, CodeSet = InputCodeSets.WindowsVirtualKeyV1, Code = 65, Timestamp = Start };
        var request = new InputEventUploadRequest { Events = [input] };
        await store.ImportInputEventsAsync("owner", "first-device", "First", request);
        await Assert.ThrowsAsync<FactIngestException>(() => store.ImportInputEventsAsync("other", "other-device", "Other", request));
        Assert.Equal("owner", (await db.Devices.SingleAsync()).OwnerId);
        Assert.Equal("owner", (await db.Facts.SingleAsync()).OwnerId);
        Assert.Single(await db.InputEvents.ToListAsync());
    }

    [Fact]
    public async Task NativeTimes_SurviveDatabaseRoundTripsWithoutChangingRevisionOrGapIdentity()
    {
        var batch = SegmentBatch();
        var snapshot = batch.Facts[0];
        snapshot.Start = snapshot.Start!.Value.AddTicks(1);
        snapshot.End = snapshot.End!.Value.AddTicks(9);
        snapshot.ObservedAt = snapshot.Start.Value.AddTicks(3);
        batch.Gaps.Add(new FactGapSnapshot
        {
            StreamId = snapshot.StreamId, GapId = Guid.CreateVersion7(),
            Start = snapshot.Start.Value, End = snapshot.Start.Value.AddTicks(1), Reason = "lost"
        });
        await using (var db = CreateDbContext())
            await new FactStore(db).IngestAsync("owner", batch);
        await using (var db = CreateDbContext())
        {
            var fact = await db.Facts.SingleAsync();
            Assert.Equal(snapshot.Start, fact.Start);
            Assert.Equal(snapshot.End, fact.End);
            Assert.Equal(snapshot.ObservedAt, fact.ObservedAt);
            var gap = await db.FactGaps.SingleAsync();
            Assert.Equal(batch.Gaps[0].Start, gap.Start);
            Assert.Equal(batch.Gaps[0].End, gap.End);
            await new FactStore(db).IngestAsync("owner", batch);
        }
        snapshot.Revision = 2;
        snapshot.End = snapshot.End.Value.AddSeconds(1);
        await using (var db = CreateDbContext())
            await new FactStore(db).IngestAsync("owner", batch);
        await using (var db = CreateDbContext())
        {
            Assert.Equal(2, (await db.Facts.SingleAsync()).Revision);
            Assert.Single(await db.FactGaps.ToListAsync());
        }
        // Native identity cannot be changed even within the same PostgreSQL microsecond.
        snapshot.Revision = 3;
        snapshot.Start = snapshot.Start.Value.AddTicks(1);
        await using (var db = CreateDbContext())
            Assert.True((await Assert.ThrowsAsync<FactIngestException>(() =>
                new FactStore(db).IngestAsync("owner", batch))).IsConflict);
    }

    [Fact]
    public async Task LegacyInputReplay_UsesHistoricalPrecisionThenPreservesNativeTicks()
    {
        var batch = EventBatch();
        var snapshot = batch.Facts[0];
        snapshot.OccurredAt = snapshot.OccurredAt!.Value.AddTicks(1);
        var originalTime = snapshot.OccurredAt.Value;
        await using (var db = CreateDbContext())
            await new FactStore(db).ImportInputEventsAsync("owner", "hardware", null, new InputEventUploadRequest
            {
                Events = [new InputEventItem { Id = snapshot.FactId, Timestamp = originalTime.AddTicks(-1),
                    EventType = InputEventType.KeyDown, CodeSet = InputCodeSets.WindowsVirtualKeyV1, Code = 65 }]
            });
        await using (var db = CreateDbContext())
            await new FactStore(db).IngestAsync("owner", batch);
        await using (var db = CreateDbContext())
        {
            var fact = await db.Facts.SingleAsync();
            Assert.Equal("native", fact.Origin);
            Assert.Equal(originalTime, fact.OccurredAt);
            Assert.NotNull(fact.LegacyRecord);
            Assert.Single(await db.InputEvents.ToListAsync());
            await new FactStore(db).IngestAsync("owner", batch);
        }
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"unstructured observation\"")]
    [InlineData("{\"eventType\":\"keyDown\",\"codeSet\":\"heartbeat-key-position-v1\",\"code\":\"future encoding\"}")]
    [InlineData("{\"eventType\":\"keyDown\",\"codeSet\":\"future-input\",\"code\":1}")]
    public async Task EventPayloadWithoutInputVocabulary_IsSavedAndRemovesStaleInputProjection(string json)
    {
        var batch = EventBatch();
        await using var db = CreateDbContext();
        var store = new FactStore(db);
        await store.IngestAsync("owner", batch);
        Assert.Single(await db.InputEvents.ToListAsync());
        batch.Facts[0].Revision = 2;
        batch.Facts[0].Payload = JsonDocument.Parse(json).RootElement.Clone();
        await store.IngestAsync("owner", batch);
        db.ChangeTracker.Clear();
        await store.IngestAsync("owner", batch);
        Assert.Empty(await db.InputEvents.ToListAsync());
        var saved = await db.Facts.SingleAsync();
        Assert.Equal(2, saved.Revision);
        Assert.True(JsonElement.DeepEquals(batch.Facts[0].Payload!.Value, JsonDocument.Parse(saved.Payload!).RootElement));
    }

    private static JsonElement Payload() => JsonSerializer.SerializeToElement(new { identityKey = "https://example.com", title = "Page", attributes = new { url = "https://example.com/?original=1" } });

    internal static FactUploadRequest SegmentBatch(string subjectKind = "machine")
    {
        var stream = new FactStreamDefinition
        {
            StreamId = Guid.NewGuid(), CollectorInstanceId = Guid.NewGuid(), Subject = new FactSubject { SubjectId = Guid.NewGuid(), Kind = subjectKind, HardwareId = subjectKind == "machine" ? "hardware" : null, DisplayName = "Observed subject" },
            OutputId = "activity", Source = "browser", FactKind = "segment"
        };
        return new FactUploadRequest { Streams = [stream], Facts = [new FactSnapshot { StreamId = stream.StreamId, FactId = Guid.CreateVersion7(), Revision = 1, Start = Start, End = Start.AddMinutes(10), IsFinal = false, Payload = Payload() }] };
    }

    private static FactUploadRequest EventBatch()
    {
        var batch = SegmentBatch();
        var stream = batch.Streams[0];
        stream.Source = "system";
        stream.FactKind = "event";
        var fact = batch.Facts[0];
        fact.Start = fact.End = null;
        fact.IsFinal = null;
        fact.OccurredAt = Start;
        fact.Payload = JsonSerializer.SerializeToElement(new { eventType = "keyDown", codeSet = InputCodeSets.WindowsVirtualKeyV1, code = 65 });
        return batch;
    }

    internal static ActivitySegmentItem LegacySegment(FactUploadRequest batch)
    {
        var stream = batch.Streams[0];
        var fact = batch.Facts[0];
        var identity = Encoding.ASCII.GetBytes($"{stream.StreamId:D}/{fact.FactId:D}");
        var value = (fact.FactId.ToString("N")[..12] + Convert.ToHexStringLower(SHA256.HashData(identity).AsSpan(0, 10))).ToCharArray();
        value[12] = '7'; value[16] = "89ab"[Convert.ToInt32(value[16].ToString(), 16) & 3];
        return new ActivitySegmentItem { Id = Guid.ParseExact(new string(value), "N"), Source = stream.Source, IdentityKey = "https://example.com", Title = "Page", StartTime = Start, EndTime = Start.AddMinutes(10), Attributes = fact.Payload };
    }
}
