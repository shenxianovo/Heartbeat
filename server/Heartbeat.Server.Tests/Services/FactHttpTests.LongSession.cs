using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Core;
using Heartbeat.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Fact]
    public async Task SystemFacts_UploadLongSessionAsContinuousValidChunks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-long-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new SharedClock(DateTimeOffset.UtcNow);
            await using var application = CreateApplication(clock);
            using var http = application.CreateClient();
            http.DefaultRequestHeaders.Add("X-Test-Owner", "user-1");
            http.DefaultRequestHeaders.Add(DeviceService.HardwareIdHeader, "long-session-device");
            http.DefaultRequestHeaders.Add(DeviceService.DeviceNameHeader, "Long Session Device");

            var sink = new SegmentIngestService(clock);
            var protocol = new SystemCollectorProtocolAdapter();
            var observations = new DesktopSource
            {
                CurrentActivity = new DesktopActivity("win:code", "Visual Studio Code", "main.cs")
            };
            var monitor = new AppMonitorService(
                clock,
                observations,
                new InputSignal(),
                protocol,
                sink,
                new DesktopSettings());
            using var collector = new SystemInProcessCollector(protocol, monitor);
            var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
            using var config = JsonDocument.Parse("{}");
            await using var runtime = CollectorRuntime.Open(
                Path.Combine(root, "collector-runtime.json"),
                sink);
            var instance = runtime.CreateInstance(
                package,
                new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
                new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
            await using var activation = await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                package,
                collector);

            var api = new HeartbeatApiClient(http);
            var upload = new UploadStream<FactUploadItem>(
                "long system session",
                [new RuntimeFactUploadSource(runtime)],
                (batch, ct) => api.UploadFactsAsync(FactUploadItem.Request(batch), ct),
                new JsonDeadLetterStore<FactUploadItem>(
                    Path.Combine(root, "facts-dead-letter.json")));

            clock.Advance(SegmentRotationPolicy.RotateAfter);
            monitor.PushCurrentSnapshot();
            await UploadPendingAsync(runtime, upload);

            clock.Advance(TimeSpan.FromHours(2));
            monitor.PushCurrentSnapshot();
            await UploadPendingAsync(runtime, upload);

            await activation.StopAsync();
            await UploadPendingAsync(runtime, upload);

            using var verification = CreateDbContext();
            var chunks = await verification.ActivitySegments
                .AsNoTracking()
                .OrderBy(segment => segment.StartTime)
                .ToListAsync();
            Assert.Equal(2, chunks.Count);
            Assert.Equal(chunks[0].EndTime, chunks[1].StartTime);
            Assert.All(chunks, chunk =>
                Assert.True(chunk.EndTime - chunk.StartTime <= SegmentValidationPolicy.MaxDuration));
            Assert.Equal(TimeSpan.FromHours(25), chunks.Aggregate(
                TimeSpan.Zero, (total, chunk) => total + (chunk.EndTime - chunk.StartTime)));
            Assert.Equal(0, upload.Status.DeadLetterCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task UploadPendingAsync(
        CollectorRuntime runtime,
        UploadStream<FactUploadItem> upload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (runtime.ReadPendingFacts().Count == 0)
            await Task.Delay(10, timeout.Token);
        await upload.DrainAsync();
    }

    private sealed class SharedClock(DateTimeOffset now) : TimeProvider, IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

}
