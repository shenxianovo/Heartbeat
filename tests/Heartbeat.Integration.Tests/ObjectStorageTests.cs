using System.Text.Json;
using Heartbeat.Application.Recording;
using Heartbeat.Contracts;
using Heartbeat.Infrastructure;
using Heartbeat.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RecordingRecord = Heartbeat.Recording.Record;

namespace Heartbeat.Integration.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class ObjectStorageTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SharedDeviceApplicationKeepsNameHistoryAcrossCollectors()
    {
        var owner = Guid.NewGuid();
        var first = await TrackAsync(owner, "mac-a");
        var second = await TrackAsync(owner, "mac-a", key: "example.other-producer");
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IRecordStore>();
        var objects = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var newer = Observation(second, At.AddHours(1), "mac-a", "Current name");
        var older = Observation(first, At, "mac-a", "Old name");
        Assert.IsType<RecordWriteResult.Stored>(await writer.WriteAsync(owner, newer, TestContext.Current.CancellationToken));
        Assert.IsType<RecordWriteResult.Stored>(await writer.WriteAsync(owner, older, TestContext.Current.CancellationToken));
        Assert.IsType<RecordWriteResult.Stored>(await writer.WriteAsync(owner, older, TestContext.Current.CancellationToken));
        var catalog = await objects.ListAsync(owner, null, cancellationToken: TestContext.Current.CancellationToken);
        var app = Assert.Single(catalog, item => item.Namespace == "app.macos.bundle_id");
        Assert.Equal("Current name", app.Name);
        var device = Assert.Single(catalog, item => item.Key == "mac-a");
        var all = await objects.ReplayAsync(owner, app.Id, null, At, At.AddDays(1), 10, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, all.Records.Count);
        var contextual = await objects.ReplayAsync(owner, app.Id, [device.Id], At, At.AddDays(1), 10, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, contextual.Records.Count);
        var snapshot = Assert.Single(contextual.Records, item => item.Record.Id == older.Id).Record;
        Assert.Equal("Old name", Assert.Single(snapshot.Objects, item => item.Id == app.Id).Name);
        Assert.Empty(await objects.ListAsync(Guid.NewGuid(), null, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty((await objects.ReplayAsync(Guid.NewGuid(), app.Id, null, null, null, 10, null, TestContext.Current.CancellationToken)).Records);
    }

    [Fact]
    public async Task RejectedWriteCannotDiscoverObjectsOrChangeAssociations()
    {
        var owner = Guid.NewGuid();
        var track = await TrackAsync(owner, "mac");
        var original = Observation(track, At, "mac", "Editor");
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IRecordStore>();
        Assert.IsType<RecordWriteResult.TrackNotFound>(await writer.WriteAsync(Guid.NewGuid(), original, TestContext.Current.CancellationToken));
        await using var db = CreateDbContext();
        Assert.Equal(3, await db.Objects.CountAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.ObjectBindings.ToListAsync(TestContext.Current.CancellationToken));
        Assert.IsType<RecordWriteResult.Stored>(await writer.WriteAsync(owner, original, TestContext.Current.CancellationToken));
        var changed = RecordingRecord.Create(original.Id, track, At, At.AddMinutes(10), null, At,
            original.Value, [new ObjectReference("application", "app.macos.bundle_id", "different")]);
        Assert.IsType<RecordWriteResult.Conflict>(await writer.WriteAsync(owner, changed, TestContext.Current.CancellationToken));
        Assert.Equal(6, await db.Objects.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.ObjectBindings.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.RecordObjects.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(original.EndedAt, (await db.Records.SingleAsync(TestContext.Current.CancellationToken)).EndedAt);
    }

    [Fact]
    public async Task ConcurrentDiscoveryReusesIdentityWithoutLeavingCandidates()
    {
        var owner = Guid.NewGuid();
        var first = await TrackAsync(owner, "mac-a");
        var second = await TrackAsync(owner, "mac-a", key: "example.other-producer");
        await using var services = Services();
        var observations = new[]
        {
            Observation(first, At, "mac-a", "Editor"),
            Observation(second, At, "mac-a", "Editor"),
        };
        var results = await Task.WhenAll(observations.Select(async record =>
        {
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IRecordStore>()
                .WriteAsync(owner, record, TestContext.Current.CancellationToken);
        }));
        Assert.All(results, result => Assert.IsType<RecordWriteResult.Stored>(result));
        await using var db = CreateDbContext();
        var identities = await db.Objects.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(9, identities.Count); // Timeline, two Collectors/Tracks/Records, one device/application.
        Assert.All(identities, identity => Assert.Equal(owner, identity.OwnerId));
        var app = await db.ObjectBindings.FirstAsync(binding => binding.Namespace == "app.macos.bundle_id",
            TestContext.Current.CancellationToken);
        Assert.Equal(2, await db.RecordObjects.CountAsync(link => link.ObjectId == app.ObjectId,
            TestContext.Current.CancellationToken));
        foreach (var track in new[] { first, second })
            Assert.Contains(identities, identity => identity.Id == track.CollectorId);
    }

    [Fact]
    public async Task DifferentDevicesKeepSeparateApplicationsWithTheSameProductIdentifier()
    {
        var owner = Guid.NewGuid();
        var first = await TrackAsync(owner, "mac-a");
        var second = await TrackAsync(owner, "mac-b");
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IRecordStore>();
        foreach (var record in new[] { Observation(first, At, "mac-a", "Editor"), Observation(second, At, "mac-b", "Editor") })
            Assert.IsType<RecordWriteResult.Stored>(await writer.WriteAsync(owner, record, TestContext.Current.CancellationToken));
        await using var db = CreateDbContext();
        var applications = await db.ObjectBindings.Where(binding => binding.Namespace == "app.macos.bundle_id")
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, applications.Count);
        Assert.Equal(2, applications.Select(item => item.ObjectId).Distinct().Count());
        Assert.Single(applications.Select(item => item.Key).Distinct());
        foreach (var application in applications)
            Assert.True(await db.ObjectBindings.AnyAsync(binding => binding.ObjectId == application.ScopeId && binding.Namespace == "device",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task KnownIdentityAcceptsAliasesAndPreservesEachReferenceSnapshot()
    {
        var owner = Guid.NewGuid();
        var track = await TrackAsync(owner, "mac");
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IRecordStore>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var record = RecordingRecord.Create(Guid.CreateVersion7(), track, At, At.AddMinutes(1), null, At,
            JsonSerializer.SerializeToElement(1),
            [new("source", "example.address", "first", "First alias", track.CollectorId),
             new("source", "example.address", "second", "Second alias", track.CollectorId),
             new("context", Id: track.Id)]);
        Assert.IsType<RecordWriteResult.Stored>(await writer.WriteAsync(owner, record, TestContext.Current.CancellationToken));
        var source = Assert.IsType<ListedObject>(await store.FindAsync(owner, track.CollectorId, TestContext.Current.CancellationToken));
        Assert.Equal(2, source.Identifiers.Count);
        var replay = await store.ReplayAsync(owner, track.CollectorId, null, null, null, 10, null, TestContext.Current.CancellationToken);
        var references = Assert.Single(replay.Records).Record.Objects.Where(item => item.Id == source.Id).ToArray();
        Assert.Equal(2, references.Length);
        Assert.Contains(references, item => item.Key == "first" && item.Name == "First alias");
        Assert.Contains(references, item => item.Key == "second" && item.Name == "Second alias");
        await using var db = CreateDbContext();
        Assert.Equal(4, await db.Objects.CountAsync(TestContext.Current.CancellationToken));
        var conflict = RecordingRecord.Create(Guid.CreateVersion7(), track, At, At, null, At, record.Value,
            [new("new", "a.new", "candidate"), new("source", "example.address", "first", Id: track.Id)]);
        Assert.IsType<RecordWriteResult.InvalidObject>(await writer.WriteAsync(owner, conflict, TestContext.Current.CancellationToken));
        Assert.Equal(4, await db.Objects.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.ObjectBindings.CountAsync(TestContext.Current.CancellationToken));
        Assert.Single(await db.Records.ToListAsync(TestContext.Current.CancellationToken));
        foreach (var unavailable in new[] { Guid.CreateVersion7(), (await TrackAsync(Guid.NewGuid(), "foreign")).Id })
        {
            var rejected = RecordingRecord.Create(Guid.CreateVersion7(), track, At, At, null, At, record.Value,
                [new("context", Id: unavailable)]);
            Assert.IsType<RecordWriteResult.InvalidObject>(await writer.WriteAsync(owner, rejected, TestContext.Current.CancellationToken));
            Assert.False(await db.Objects.AnyAsync(item => item.Id == rejected.Id, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task CoreIdentitiesSupportGenericLookupSourceFilteringAndBoundedCatalog()
    {
        var owner = Guid.NewGuid();
        var track = await TrackAsync(owner, "mac");
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var record = Observation(track, At, "mac", "Editor");
        Assert.IsType<RecordWriteResult.Stored>(await scope.ServiceProvider.GetRequiredService<IRecordStore>()
            .WriteAsync(owner, record, TestContext.Current.CancellationToken));
        await using var db = CreateDbContext();
        var timelineId = (await db.Collectors.SingleAsync(TestContext.Current.CancellationToken)).TimelineId;
        var identities = await db.Objects.ToListAsync(TestContext.Current.CancellationToken);
        Assert.All(identities, item => Assert.Equal(7, item.Id.Version));
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(At));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, owner.ToString());
        foreach (var id in new[] { timelineId, track.CollectorId, track.Id, record.Id })
        {
            var item = await GetJsonAsync(client, $"/api/v1/objects/{id}");
            Assert.Equal(id, item.GetProperty("id").GetGuid());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("namespace").ValueKind);
            var replay = await GetJsonAsync(client, $"/api/v1/objects/{id}/records?contextObjectIds={track.CollectorId}");
            Assert.Equal(record.Id, Assert.Single(replay.GetProperty("records").EnumerateArray()).GetProperty("id").GetGuid());
        }
        var catalog = new List<Guid>();
        string? after = null;
        do
        {
            var path = after is null ? "/api/v1/objects?limit=2" : $"/api/v1/objects?limit=2&after={after}";
            var page = await GetJsonAsync(client, path);
            var items = page.GetProperty("objects").EnumerateArray().ToArray();
            Assert.InRange(items.Length, 1, 2);
            catalog.AddRange(items.Select(item => item.GetProperty("id").GetGuid()));
            after = page.GetProperty("nextCursor").GetString();
        } while (after is not null);
        Assert.Equal(5, catalog.Count); // Record roots are looked up directly, not enumerated by default.
        Assert.Equal(catalog.Count, catalog.Distinct().Count());
        Assert.DoesNotContain(record.Id, catalog);
        var citation = RecordingRecord.Create(Guid.CreateVersion7(), track, At, At, null, At, record.Value,
            [new("evidence", Name: "Earlier observation", Id: record.Id)]);
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        Assert.IsType<RecordWriteResult.Stored>(await scope.ServiceProvider.GetRequiredService<IRecordStore>()
            .WriteAsync(owner, citation, TestContext.Current.CancellationToken));
        Assert.Contains(await store.ListAsync(owner, null, cancellationToken: TestContext.Current.CancellationToken),
            item => item.Id == record.Id && item.Namespace is null && item.Name == "Earlier observation");
        Assert.Equal(2, (await store.ReplayAsync(owner, record.Id, null, null, null, 10, null,
            TestContext.Current.CancellationToken)).Records.Count);
        var counts = await GetJsonAsync(client, $"/api/v1/tracks?objectId={timelineId}&contextObjectIds={track.Id}");
        Assert.Single(counts.GetProperty("tracks").EnumerateArray());
    }

    [Fact]
    public async Task EveryReadEndpointIntersectsAllContextObjectsOnTheSameRecord()
    {
        var owner = Guid.NewGuid();
        var track = await TrackAsync(owner, "inside", TimeMode.Point);
        var outsideTrack = await TrackAsync(owner, "outside", TimeMode.Point);
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IRecordStore>();
        ObjectReference account = new("account", "vrchat.account", "self");
        ObjectReference world = new("world", "vrchat.world", "world");
        ObjectReference friend = new("friend", "vrchat.account", "friend");
        ObjectReference elsewhere = new("world", "vrchat.world", "elsewhere");
        var matching = RecordingRecord.Create(Guid.CreateVersion7(), track, At, null, null, At,
            JsonSerializer.SerializeToElement(1), [account, world, friend]);
        var observations = new[]
        {
            matching,
            RecordingRecord.Create(Guid.CreateVersion7(), track, At, null, null, At,
                matching.Value, [account, elsewhere, friend]),
            RecordingRecord.Create(Guid.CreateVersion7(), outsideTrack, At, null, null, At,
                matching.Value, [world, friend]),
        };
        foreach (var record in observations)
            Assert.IsType<RecordWriteResult.Stored>(await writer.WriteAsync(owner, record, TestContext.Current.CancellationToken));
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var catalog = await store.ListAsync(owner, null, cancellationToken: TestContext.Current.CancellationToken);
        var accountId = Assert.Single(catalog, item => item.Key == "self").Id;
        var worldId = Assert.Single(catalog, item => item.Key == "world").Id;
        var peer = Assert.Single(catalog, item => item.Key == "friend");
        Assert.Equal("friend", Assert.Single(peer.Roles));
        await using var factory = RecordingApiFactory.Create(ConnectionString, new FixedTimeProvider(At));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RecordingApiFactory.OwnerHeader, owner.ToString());
        var filter = $"objectId={peer.Id}&contextObjectIds={accountId}&contextObjectIds={worldId}" +
            $"&contextObjectIds={track.CollectorId}&contextObjectIds={track.Id}&contextObjectIds={matching.Id}";
        var tracks = await GetJsonAsync(client, $"/api/v1/tracks?{filter}");
        Assert.Equal(track.Id, Assert.Single(tracks.GetProperty("tracks").EnumerateArray()).GetProperty("id").GetGuid());
        var related = await GetJsonAsync(client, $"/api/v1/objects?{filter}");
        Assert.Equal(6, related.GetProperty("objects").GetArrayLength());
        foreach (var endpoint in new[] { $"tracks/{track.Id}", $"objects/{peer.Id}" })
        {
            var replay = await GetJsonAsync(client, $"/api/v1/{endpoint}/records?{filter}");
            Assert.Equal(matching.Id, Assert.Single(replay.GetProperty("records").EnumerateArray()).GetProperty("id").GetGuid());
        }
        var counts = await GetJsonAsync(client,
            $"/api/v1/tracks/{track.Id}/point-counts?{filter}&from=2026-10-01T00:00:00Z&to=2026-10-01T01:00:00Z&bucketSeconds=3600");
        Assert.Equal(1, Assert.Single(counts.GetProperty("buckets").EnumerateArray()).GetProperty("count").GetInt64());
        var unknown = await GetJsonAsync(client, $"/api/v1/tracks?{filter}&contextObjectIds={Guid.NewGuid()}");
        Assert.Empty(unknown.GetProperty("tracks").EnumerateArray());
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.Clone();
    }

    private static RecordingRecord Observation(Track track, DateTimeOffset at, string device, string name) =>
        RecordingRecord.Create(Guid.CreateVersion7(), track, at, at.AddMinutes(5), null, At.AddDays(1),
            JsonSerializer.SerializeToElement(new { }),
            [new("device", "device", device), new("application", "app.macos.bundle_id", "com.example.Editor", name, Scope: new ObjectScope("device", device))]);

    private async Task<Track> TrackAsync(Guid owner, string target, TimeMode timeMode = TimeMode.Range, string key = "example.producer")
    {
        await using var db = CreateDbContext();
        var timeline = await db.Timelines.SingleOrDefaultAsync(item => item.OwnerId == owner);
        if (timeline is null) { timeline = Timeline.Create(owner, "Timeline", At); db.Timelines.Add(timeline); }
        var collector = Heartbeat.Recording.Collector.Create(timeline.Id, Heartbeat.Recording.RecordingObject.Create(owner), key, target, target, At);
        var track = Track.Create(collector.Id, RecordingObject.Create(owner), "example.observation", 1, timeMode, At);
        db.Collectors.Add(collector);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();
        return track;
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Heartbeat"] = ConnectionString }).Build());
        return services.BuildServiceProvider(validateScopes: true);
    }
}
