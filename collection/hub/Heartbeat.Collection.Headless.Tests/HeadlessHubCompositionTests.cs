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
    public void Composition_IsHeadlessAndStopsManagedCollectorBeforeTerminalUpload()
    {
        Directory.CreateDirectory(_directory);
        using var config = JsonDocument.Parse("{}");
        var accountSubjectId = Guid.CreateVersion7();
        var options = new HeadlessHubOptions
        {
            ApiKey = "test-key",
            DataDirectory = _directory,
            PackageDirectory = Path.Combine(_directory, "package"),
            SubjectId = accountSubjectId,
            SubjectName = "Reference account",
            Config = config.RootElement.Clone()
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHeartbeatHeadlessHub(options);
        using var host = builder.Build();

        var hosted = host.Services.GetServices<IHostedService>().ToList();
        var uploadIndex = hosted.FindIndex(service => service is UploadWorker);
        var collectorIndex = hosted.FindIndex(service => service is ManagedCollectorHostedService);
        Assert.True(uploadIndex >= 0);
        Assert.True(collectorIndex > uploadIndex);
        Assert.NotNull(host.Services.GetRequiredService<UploadStream<ActivitySegmentItem>>());
        Assert.NotNull(host.Services.GetRequiredService<UploadStream<InputEventItem>>());
        var identity = host.Services.GetRequiredService<IDeviceIdentity>();
        Assert.Equal($"subject:account:{accountSubjectId:D}", identity.HardwareId);
        Assert.Equal("Reference account", identity.DeviceName);
        Assert.DoesNotContain(Environment.MachineName, identity.HardwareId, StringComparison.OrdinalIgnoreCase);

        var references = typeof(HeadlessHubComposition).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        Assert.DoesNotContain(references, name => name?.StartsWith("Heartbeat.Desktop", StringComparison.Ordinal) == true);
        Assert.DoesNotContain("Heartbeat.Collector.System", references);
        Assert.DoesNotContain(references, name => name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(references, name => name?.StartsWith("Velopack", StringComparison.Ordinal) == true);
    }

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
        using var firstProvider = BuildProvider(firstOptions);
        using var secondProvider = BuildProvider(secondOptions);
        Assert.Equal($"subject:account:{first:D}", firstProvider.GetRequiredService<IDeviceIdentity>().HardwareId);
        Assert.Equal($"subject:person:{second:D}", secondProvider.GetRequiredService<IDeviceIdentity>().HardwareId);
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

    private static ServiceProvider BuildProvider(HeadlessHubOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHeartbeatHeadlessHub(options);
        return services.BuildServiceProvider();
    }

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
