using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Headless.Tests;

public sealed class HeadlessFleetManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-headless-composition-{Guid.NewGuid():N}");

    [Fact]
    public void FleetConfiguration_PreservesEachInstanceSubjectAndUploadIdentity()
    {
        Directory.CreateDirectory(_directory);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var fleet = new HeadlessFleetOptions
        {
            ApiKey = "test-key",
            DataDirectory = _directory,
            Management = new HeadlessManagementOptions
            {
                OwnerSubject = "owner-1",
                Authority = "https://auth.example.test",
                Issuer = "https://auth.example.test/",
                ClientId = "heartbeat-web"
            },
            Instances =
            [
                new HeadlessManagedInstanceOptions
                {
                    InstanceKey = "first",
                    PackageDirectory = Path.Combine(_directory, "first-package"),
                    SubjectId = first,
                    SubjectName = "First account"
                },
                new HeadlessManagedInstanceOptions
                {
                    InstanceKey = "second",
                    PackageDirectory = Path.Combine(_directory, "second-package"),
                    SubjectId = second,
                    SubjectKind = Heartbeat.Collection.Hub.Collectors.Runtime.SubjectKind.Person,
                    SubjectName = "Second subject"
                }
            ]
        };

        fleet.Validate();
        Assert.Equal(first, fleet.Instances[0].SubjectId);
        Assert.Equal(SubjectKind.Account, fleet.Instances[0].SubjectKind);
        Assert.Equal(second, fleet.Instances[1].SubjectId);
        Assert.Equal(SubjectKind.Person, fleet.Instances[1].SubjectKind);
    }

    /// <summary>
    /// 零 Collector Instance 是合法部署形态：整段 instances 省略或写成空数组都能通过校验（ADR-048）。
    /// </summary>
    [Theory]
    [InlineData("\"instances\": [],")]
    [InlineData("")]
    public void FleetConfiguration_AcceptsZeroInstances(string instancesFragment)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "heartbeat-headless.json");
        File.WriteAllText(path, $$"""
        {
          "apiKey": "test-key",
          "dataDirectory": "data",
          {{instancesFragment}}
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

        Assert.Empty(fleet.Instances);
    }

    [Fact]
    public void FleetConfiguration_RejectsInstanceKeysThatDifferOnlyByCase()
    {
        var subjectId = Guid.CreateVersion7();
        var fleet = new HeadlessFleetOptions
        {
            ApiKey = "test-key",
            DataDirectory = _directory,
            Management = new HeadlessManagementOptions
            {
                OwnerSubject = "owner-1",
                Authority = "https://auth.example.test",
                Issuer = "https://auth.example.test/",
                ClientId = "heartbeat-web"
            },
            Instances =
            [
                new HeadlessManagedInstanceOptions { InstanceKey = "same", PackageDirectory = "one", SubjectId = subjectId },
                new HeadlessManagedInstanceOptions { InstanceKey = "SAME", PackageDirectory = "two", SubjectId = Guid.CreateVersion7() }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(fleet.Validate);
        Assert.Contains("configured more than once", exception.Message);
    }

    [Fact]
    public void FleetConfiguration_LegacyConfigSchemaVersion_RemainsReadable()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "heartbeat-headless.json");
        File.WriteAllText(
            path,
            $$"""
            {
              "apiKey": "test-key",
              "dataDirectory": "data",
              "management": {
                "ownerSubject": "owner-1",
                "authority": "https://auth.example.test",
                "issuer": "https://auth.example.test/",
                "clientId": "heartbeat-web"
              },
              "instances": [{
                "instanceKey": "legacy",
                "packageDirectory": "package",
                "subjectId": "{{Guid.CreateVersion7()}}",
                "configSchemaVersion": 3,
                "config": {}
              }]
            }
            """);

        var options = HeadlessFleetOptions.Load(path);

        Assert.Equal(3, Assert.Single(options.Instances).ConfigVersion);
    }

    [Fact]
    public void InstanceMapping_LegacyBareDictionary_RemainsReadable()
    {
        var collectorInstanceId = Guid.CreateVersion7();
        var mappings = HeadlessFleetManager.DeserializeInstanceMappings(
            JsonSerializer.Serialize(new Dictionary<string, Guid> { ["Legacy"] = collectorInstanceId }),
            out var legacy);

        Assert.True(legacy);
        Assert.Equal(collectorInstanceId, mappings["legacy"]);
    }

    [Fact]
    public async Task InstancePipelines_IsolateProjectionStatusAndUploadThroughProductionModule()
    {
        Directory.CreateDirectory(_directory);
        var upload = new RecordingSegmentUpload();
        using var pipelines = new HeadlessInstancePipelines(_directory, upload);
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        pipelines.Add(firstId, Instance("first", "First account"));
        pipelines.Add(secondId, Instance("second", "Second account"));
        var projection = (ISubjectSegmentProjectionSink)pipelines;
        var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account);
        var now = DateTimeOffset.UtcNow;
        var firstSegment = Segment("first", now);
        var secondSegment = Segment("second", now);

        projection.UpsertDurable(
            new CollectorProjectionContext(firstId, subject),
            firstSegment,
            1,
            false);
        projection.UpsertDurable(
            new CollectorProjectionContext(secondId, subject),
            secondSegment,
            1,
            false);

        Assert.Equal("first", pipelines.CurrentActivity(firstId)?.Title);
        Assert.Equal("second", pipelines.CurrentActivity(secondId)?.Title);

        await pipelines.DrainAllAsync();

        Assert.Equal("first", Assert.Single(upload.Batches[firstId]).Title);
        Assert.Equal("second", Assert.Single(upload.Batches[secondId]).Title);

        projection.UpsertDurable(
            new CollectorProjectionContext(firstId, subject),
            new ActivitySegmentItem
            {
                Id = firstSegment.Id,
                Source = firstSegment.Source,
                IdentityKey = firstSegment.IdentityKey,
                Title = firstSegment.Title,
                StartTime = firstSegment.StartTime,
                EndTime = now.AddMinutes(1)
            },
            2,
            true);

        Assert.Null(pipelines.CurrentActivity(firstId));
        Assert.Equal("second", pipelines.CurrentActivity(secondId)?.Title);
    }

    [Fact]
    public async Task InstancePipelines_RetryCachesRemainIsolatedAcrossRestart()
    {
        Directory.CreateDirectory(_directory);
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        var first = Instance("first", "First account");
        var second = Instance("second", "Second account");
        var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account);
        var now = DateTimeOffset.UtcNow;
        var unavailable = new RecordingSegmentUpload(succeeds: false);
        using (var pipelines = new HeadlessInstancePipelines(_directory, unavailable))
        {
            pipelines.Add(firstId, first);
            pipelines.Add(secondId, second);
            var projection = (ISubjectSegmentProjectionSink)pipelines;
            projection.UpsertDurable(
                new CollectorProjectionContext(firstId, subject),
                Segment("first-cached", now),
                1,
                false);
            projection.UpsertDurable(
                new CollectorProjectionContext(secondId, subject),
                Segment("second-cached", now),
                1,
                false);
            await pipelines.DrainAllAsync();
        }

        var recovered = new RecordingSegmentUpload();
        using var restarted = new HeadlessInstancePipelines(_directory, recovered);
        restarted.Add(firstId, first);
        restarted.Add(secondId, second);

        await restarted.DrainAllAsync();

        Assert.Equal("first-cached", Assert.Single(recovered.Batches[firstId]).Title);
        Assert.Equal("second-cached", Assert.Single(recovered.Batches[secondId]).Title);
    }

    private static HeadlessManagedInstanceOptions Instance(string key, string subjectName) => new()
    {
        InstanceKey = key,
        PackageDirectory = $"{key}-package",
        SubjectId = Guid.CreateVersion7(),
        SubjectName = subjectName
    };

    private static ActivitySegmentItem Segment(string title, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Source = "reference",
        IdentityKey = title,
        Title = title,
        StartTime = now,
        EndTime = now
    };

    private sealed class RecordingSegmentUpload(bool succeeds = true) : IHeadlessSegmentUpload
    {
        public Dictionary<Guid, List<ActivitySegmentItem>> Batches { get; } = [];

        public Task<ApiResult> SendAsync(
            Guid collectorInstanceId,
            HeadlessManagedInstanceOptions instance,
            List<ActivitySegmentItem> batch)
        {
            Batches[collectorInstanceId] = [.. batch];
            return Task.FromResult(succeeds ? ApiResult.Ok : new ApiResult(false));
        }

        public void Dispose() { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
