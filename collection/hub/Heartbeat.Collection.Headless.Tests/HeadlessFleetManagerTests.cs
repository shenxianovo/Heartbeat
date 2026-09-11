using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Collection.Hub.Upload;

namespace Heartbeat.Collection.Headless.Tests;

public sealed class HeadlessFleetManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"heartbeat-headless-composition-{Guid.NewGuid():N}");

    [Fact]
    public async Task NativeFactObservationUpdatesCurrentActivityWithoutWritingLegacyUploadQueue()
    {
        var upload = new RecordingSegmentUpload();
        using var pipelines = new HeadlessInstancePipelines(_directory, upload);
        var stream = new FactStreamDefinition
        {
            StreamId = Guid.CreateVersion7(), CollectorInstanceId = Guid.CreateVersion7(), Source = "reference",
            FactKind = "segment", Subject = new FactSubject { SubjectId = Guid.CreateVersion7(), Kind = "account" }
        };
        var fact = new FactSnapshot
        {
            StreamId = stream.StreamId, FactId = Guid.CreateVersion7(), Revision = 1,
            Start = DateTimeOffset.UtcNow.AddMinutes(-1), End = DateTimeOffset.UtcNow,
            IsFinal = false, Payload = JsonSerializer.SerializeToElement(new { identityKey = "account:online", title = "Online" })
        };
        pipelines.Observe(new FactUploadItem(stream, fact, null));

        await pipelines.DrainAllAsync();

        Assert.Equal("Online", pipelines.CurrentActivity(stream.CollectorInstanceId)!.Title);
        Assert.Empty(upload.Sent);
        fact.Revision = 2;
        fact.IsFinal = true;
        pipelines.Observe(new FactUploadItem(stream, fact, null));
        Assert.Null(pipelines.CurrentActivity(stream.CollectorInstanceId));
    }

    [Fact]
    public void FleetConfiguration_ContainsInfrastructureOnly()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "heartbeat-headless.json");
        File.WriteAllText(path, """
        {
          "apiKey": "test-key",
          "dataDirectory": "data",
          "collectorRegistryUrl": "https://registry.example/v1/",
          "management": {
            "ownerSubject": "owner-1",
            "authority": "https://auth.example.test",
            "issuer": "https://auth.example.test/",
            "clientId": "heartbeat-web"
          }
        }
        """);

        var fleet = HeadlessFleetOptions.Load(path);
        fleet.Validate();

        Assert.Equal(Path.Combine(_directory, "data"), fleet.DataDirectory);
        Assert.Equal("https://registry.example/v1/", fleet.CollectorRegistryUrl);
    }

    [Fact]
    public void FleetConfiguration_OldInstancesArray_IsRejectedWithoutCompatibilityMigration()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "heartbeat-headless.json");
        File.WriteAllText(path, """
        {
          "apiKey": "test-key",
          "dataDirectory": "data",
          "management": {
            "ownerSubject": "owner-1",
            "authority": "https://auth.example.test",
            "issuer": "https://auth.example.test/",
            "clientId": "heartbeat-web"
          },
          "instances": []
        }
        """);

        Assert.Throws<JsonException>(() => HeadlessFleetOptions.Load(path));
    }

    [Fact]
    public async Task CancelledUpload_RetainsEachInstanceAndExposesTheResult()
    {
        var upload = new RecordingSegmentUpload();
        using var pipelines = new HeadlessInstancePipelines(_directory, upload);
        var id = Guid.CreateVersion7();
        var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account);
        StorePendingSegment(id);
        pipelines.Add(id, subject, "Account");

        await pipelines.DrainAllAsync(new CancellationToken(canceled: true));

        Assert.Empty(upload.Sent);
        Assert.Equal(1, pipelines.UploadResults[id]!.Remainder.RetainedLocally);
        Assert.True(pipelines.UploadResults[id]!.Remainder.IsDurable);
        var result = pipelines.UploadResults[id];
        pipelines.Dispose();
        Assert.Equal(result, pipelines.UploadResults[id]);
    }

    [Fact]
    public async Task InstanceRemoval_CleanupFailureLeavesThePipelineRetryable()
    {
        var upload = new RecordingSegmentUpload { FailRemoval = true };
        using var pipelines = new HeadlessInstancePipelines(_directory, upload);
        var id = Guid.CreateVersion7();
        pipelines.Add(id, new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account), "Account");

        await Assert.ThrowsAsync<IOException>(() => pipelines.RemoveAsync(id));
        upload.FailRemoval = false;
        await pipelines.RemoveAsync(id);

        Assert.Contains(id, upload.Removed);
    }

    [Fact]
    public async Task InstanceRemoval_OfflineRetainsCustodyAndCanRetryAfterDelivery()
    {
        var upload = new RecordingSegmentUpload { Result = new ApiResult(false, 503) };
        using var pipelines = new HeadlessInstancePipelines(_directory, upload);
        var id = Guid.CreateVersion7();
        var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account);
        StorePendingSegment(id);
        pipelines.Add(id, subject, "Account");

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipelines.RemoveAsync(id));

        Assert.DoesNotContain(id, upload.Removed);
        upload.Result = ApiResult.Ok;
        await pipelines.RemoveAsync(id);
        Assert.Contains(id, upload.Removed);
    }

    private void StorePendingSegment(Guid instanceId)
    {
        var directory = Path.Combine(_directory, "instances", instanceId.ToString("D"));
        Directory.CreateDirectory(directory);
        using var cache = new Heartbeat.Collection.Hub.Storage.JsonFileCache<ActivitySegmentItem>(
            Path.Combine(directory, "segments-cache.json"), int.MaxValue,
            Heartbeat.Collection.Hub.Storage.HeartbeatCacheFormats.SegmentVersion2(),
            Heartbeat.Collection.Hub.Storage.HeartbeatCacheFormats.SegmentMigrations());
        cache.Add([Segment("pending", DateTimeOffset.UtcNow)]);
    }

    private static ActivitySegmentItem Segment(string title, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Source = "reference",
        IdentityKey = title,
        Title = title,
        StartTime = now,
        EndTime = now
    };

    private sealed class RecordingSegmentUpload : IHeadlessSegmentUpload
    {
        public ApiResult Result { get; set; } = ApiResult.Ok;
        public bool FailRemoval { get; set; }
        public HashSet<Guid> Removed { get; } = [];
        public List<(Guid InstanceId, SubjectReference Subject, string? Title)> Sent { get; } = [];

        public Task<ApiResult> SendAsync(
            Guid collectorInstanceId,
            SubjectReference subject,
            string displayName,
            List<ActivitySegmentItem> batch, CancellationToken cancellationToken = default)
        {
            Sent.AddRange(batch.Select(item => (collectorInstanceId, subject, item.Title)));
            return Task.FromResult(Result);
        }

        public void Remove(Guid collectorInstanceId)
        {
            if (FailRemoval) throw new IOException("cleanup unavailable");
            Removed.Add(collectorInstanceId);
        }
        public void Dispose() { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
