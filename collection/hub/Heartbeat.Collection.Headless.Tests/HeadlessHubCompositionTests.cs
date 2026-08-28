using System.Text.Json;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Headless.Tests;

public sealed class HeadlessHubCompositionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-headless-composition-{Guid.NewGuid():N}");

    [Fact]
    public void FleetConfiguration_GivesEachAccountAnIndependentDataAndUploadIdentity()
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
        var firstOptions = fleet.ForInstance(fleet.Instances[0]);
        var secondOptions = fleet.ForInstance(fleet.Instances[1]);

        Assert.NotEqual(firstOptions.DataDirectory, secondOptions.DataDirectory);
        Assert.Equal(first, firstOptions.SubjectId);
        Assert.Equal(Heartbeat.Collection.Hub.Collectors.Runtime.SubjectKind.Account, firstOptions.SubjectKind);
        Assert.Equal(second, secondOptions.SubjectId);
        Assert.Equal(Heartbeat.Collection.Hub.Collectors.Runtime.SubjectKind.Person, secondOptions.SubjectKind);
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
    public void SingleInstanceConfiguration_LegacyConfigSchemaVersion_RemainsReadable()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "heartbeat-headless-single.json");
        File.WriteAllText(
            path,
            $$"""
            {
              "apiKey": "test-key",
              "dataDirectory": "data",
              "packageDirectory": "package",
              "subjectId": "{{Guid.CreateVersion7()}}",
              "configSchemaVersion": 4,
              "config": {}
            }
            """);

        var options = HeadlessHubOptions.Load(path);

        Assert.Equal(4, options.ConfigVersion);
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
    public void SubjectStatus_UsesCollectorFinalityInsteadOfWallClockToTrackCurrentPresence()
    {
        var status = new HeadlessSubjectStatus();
        var now = DateTimeOffset.UtcNow;
        var segmentId = Guid.CreateVersion7();
        status.Observe(new ActivitySegmentItem
        {
            Id = segmentId,
            Source = "vrchat.account",
            IdentityKey = "wrld_mock|instance:mock",
            Title = "Mock World",
            StartTime = now,
            EndTime = now
        });

        Assert.Equal("Mock World", status.Current?.Title);

        status.Observe(new ActivitySegmentItem
        {
            Id = segmentId,
            Source = "vrchat.account",
            IdentityKey = "wrld_mock|instance:mock",
            Title = "Mock World",
            StartTime = now,
            EndTime = now.AddMinutes(1)
        }, isFinal: true);

        Assert.Null(status.Current);
    }

    [Fact]
    public void SubjectRouter_SeparatesTwoCollectorInstancesObservingTheSameSubject()
    {
        var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account);
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        var first = Pipeline("first");
        var second = Pipeline("second");
        var router = new HeadlessSubjectRouter();
        router.Add(firstId, first);
        router.Add(secondId, second);
        var now = DateTimeOffset.UtcNow;

        router.UpsertDurable(
            new CollectorProjectionContext(firstId, subject),
            Segment("first", now),
            1,
            false);
        router.UpsertDurable(
            new CollectorProjectionContext(secondId, subject),
            Segment("second", now),
            1,
            false);

        Assert.Equal("first", Assert.Single(first.Ingest.GetAndClearSegments()).Title);
        Assert.Equal("second", Assert.Single(second.Ingest.GetAndClearSegments()).Title);
    }

    private static InstancePipeline Pipeline(string label)
    {
        var ingest = new SegmentIngestService(new SystemClock());
        var upload = new UploadStream<ActivitySegmentItem>(
            label,
            ingest,
            _ => Task.FromResult(ApiResult.Ok),
            new MemoryCache<ActivitySegmentItem>());
        return new InstancePipeline(ingest, new HeadlessSubjectStatus(), upload);
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

    private sealed class MemoryCache<T> : ICache<T>
    {
        private List<T> _items = [];
        public CacheFileStatus Status => CacheFileStatus.Ready;
        public void Add(List<T> items) => _items.AddRange(items);
        public List<T> Load() => [.. _items];
        public void Replace(List<T> items) => _items = [.. items];
        public void Clear() => _items.Clear();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
