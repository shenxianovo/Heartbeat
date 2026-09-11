using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Server.Entities;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Theory]
    [InlineData("win:code")]
    [InlineData("mac:com.microsoft.VSCode")]
    public async Task SystemObservations_ActivityRevisionsSurviveOfflineRestartAndLateHttpAck(string appIdentity)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-system-observations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using (var db = CreateDbContext())
            {
                db.Users.Add(new User { Id = "owner", Username = "alice" });
                await db.SaveChangesAsync();
            }
            var start = new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);
            var clock = new SharedClock(start);
            await using var app = CreateApplication(clock);
            using var http = app.CreateClient();
            http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
            var api = new HeartbeatApiClient(http);
            var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
            var path = Path.Combine(directory, "runtime.json");
            var machine = Guid.NewGuid();
            Guid observer;
            Guid factId;
            List<FactUploadItem> final;
            using (var runtime = CollectorRuntime.Open(path, new UnusedProjection(), new CollectorRuntimeOptions { MaxDurableFacts = 1 }))
            {
                observer = runtime.CreateInstance(package, new SubjectReference(machine, SubjectKind.Machine),
                    new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { }))).CollectorInstanceId;
                var protocol = new SystemCollectorProtocolAdapter();
                var source = new SystemObservationSource(new DesktopActivity(appIdentity, "Visual Studio Code", "main.cs"));
                var producer = new RecordingSystemPublisher(protocol);
                using var monitor = new AppMonitorService(clock, source, new InputSignal(), producer,
                    new SegmentIngestService(clock), new DesktopSettings());
                using var collector = new SystemInProcessCollector(protocol, monitor);
                await using var activation = await runtime.ActivateInProcessAsync(observer, package, collector);
                clock.Advance(TimeSpan.FromSeconds(10));
                monitor.PushCurrentSnapshot();
                var first = await WaitForSystemFacts(runtime, items => items.Count != 0);
                var original = Assert.Single(first).Observation;
                Assert.NotNull(original);
                Assert.Equal("segment", original.Kind);
                Assert.NotEqual(Guid.Empty, original.Id);
                factId = original.Id;
                Assert.Equal(Assert.Single(producer.Segments).FactId, factId);
                Assert.Equal(observer, original.CollectorId);
                Assert.Equal(machine.ToString("D"), original.Foi!.Key);
                Assert.True((await api.UploadFactsAsync(first)).Success);
                runtime.ConfirmUploadedFacts(first);
                Assert.Empty(runtime.ReadPendingFacts());
                // Supplemental public-protocol capacity probe: an exactly ACKed live System fact
                // still owns its slot, so another independent fact cannot evict it before finality.
                var probe = new FactSubmission(Guid.Empty, Guid.CreateVersion7(), 1, null,
                    new SegmentFactTime(original.Start!.Value, original.End!.Value, false),
                    original.Result!.Value, original.CollectorId, original.Foi, original.Aspect,
                    original.Relations, "segment", original.Source);
                var backpressure = Assert.Single((await activation.PublishAsync(Guid.CreateVersion7(), [probe])).Results);
                Assert.Equal(FactDeliveryStatus.Retry, backpressure.Status);
                Assert.Equal("hub_backpressure", backpressure.Error!.Code);
                Assert.Empty(runtime.ReadPendingFacts());

                clock.Advance(TimeSpan.FromSeconds(10));
                monitor.PushCurrentSnapshot();
                await WaitForSystemFacts(runtime, items => items.Any(item => item.Observation?.Revision > original.Revision));
                // The HTTP response for the original snapshot arrives after a newer snapshot is durable.
                Assert.True((await api.UploadFactsAsync(first)).Success);
                runtime.ConfirmUploadedFacts(first);
                var grown = Assert.Single(runtime.ReadPendingFacts()).Observation!;
                Assert.Equal(factId, grown.Id);
                Assert.Equal(start.AddSeconds(20), grown.End);
                Assert.True(grown.Revision > original.Revision);
                Assert.True((await api.UploadFactsAsync(runtime.ReadPendingFacts())).Success);

                // A corrected observed end is a newer revision even when the interval becomes shorter.
                clock.Advance(TimeSpan.FromSeconds(-5));
                monitor.PushCurrentSnapshot();
                var shortened = await WaitForSystemFacts(runtime, items => items.Any(item => item.Observation?.Revision > grown.Revision));
                Assert.Equal(start.AddSeconds(15), Assert.Single(shortened).Observation!.End);
                Assert.True((await api.UploadFactsAsync(shortened)).Success);
                var read = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
                Assert.Equal(factId, read.Id);
                Assert.Equal(start.AddSeconds(15), read.End);
                Assert.Equal("machine", read.Foi!.Kind);
                Assert.Equal("desktop-activity", read.Aspect);
                Assert.NotNull(read.AppId);
                Assert.Single(read.Relations);
                Assert.Null(read.StreamId);
                Assert.Null(read.FactId);
                Assert.True(JsonElement.DeepEquals(Assert.Single(shortened).Observation!.Result!.Value, read.Result));
                await activation.StopAsync();
                final = runtime.ReadPendingFacts();
                Assert.True(Assert.Single(final).IsFinal);
                Assert.True(Assert.Single(final).Observation!.Revision > Assert.Single(shortened).Observation!.Revision);
            }
            using var restarted = CollectorRuntime.Open(path, new UnusedProjection());
            Assert.Equal(factId, Assert.Single(restarted.ReadPendingFacts()).Observation!.Id);
            Assert.True((await api.UploadFactsAsync(restarted.ReadPendingFacts())).Success);
            Assert.True((await api.UploadFactsAsync(restarted.ReadPendingFacts())).Success);
            restarted.ConfirmUploadedFacts(final);
            Assert.Empty(restarted.ReadPendingFacts());
            var after = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
            Assert.Equal(factId, after.Id);
            Assert.Equal(observer, after.ObserverId);
            Assert.Equal(Assert.Single(final).Observation!.Revision, after.Revision);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SystemObservations_DesktopAndAwayHaveMachineFoiWithoutInventedAppRelations()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-system-desktop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using (var db = CreateDbContext())
            {
                db.Users.Add(new User { Id = "owner", Username = "alice" });
                await db.SaveChangesAsync();
            }
            var start = new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
            var clock = new SharedClock(start);
            await using var app = CreateApplication(clock);
            using var http = app.CreateClient();
            http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
            var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
            using var runtime = CollectorRuntime.Open(Path.Combine(directory, "runtime.json"), new UnusedProjection());
            var machine = Guid.NewGuid();
            var instance = runtime.CreateInstance(package, new SubjectReference(machine, SubjectKind.Machine),
                new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { })));
            var protocol = new SystemCollectorProtocolAdapter();
            var source = new SystemObservationSource(DesktopActivity.None);
            using var monitor = new AppMonitorService(clock, source, new InputSignal(), protocol,
                new SegmentIngestService(clock), new DesktopSettings());
            using var collector = new SystemInProcessCollector(protocol, monitor);
            await using var activation = await runtime.ActivateInProcessAsync(instance.CollectorInstanceId, package, collector);
            clock.Advance(TimeSpan.FromSeconds(10));
            source.Observe(DesktopObservation.EnteredAway());
            clock.Advance(TimeSpan.FromSeconds(10));
            source.Observe(DesktopObservation.ExitedAway(new DesktopActivity("mac:com.microsoft.VSCode", "Code", "restored.cs")));
            clock.Advance(TimeSpan.FromSeconds(10));
            source.Observe(DesktopObservation.AppActivated(DesktopActivity.None));
            clock.Advance(TimeSpan.FromSeconds(10));
            await activation.StopAsync();
            var pending = runtime.ReadPendingFacts();
            Assert.Equal(4, pending.Count);
            Assert.All(pending, item => Assert.NotNull(item.Observation));
            Assert.Equal(4, pending.Select(item => item.Observation!.Id).Distinct().Count());
            Assert.True((await new HeartbeatApiClient(http).UploadFactsAsync(pending)).Success);
            var rows = (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!
                .OrderBy(row => row.Start).ToArray();
            Assert.Equal(4, rows.Length);
            Assert.All(rows, row =>
            {
                Assert.Equal("machine", row.Foi!.Kind);
                Assert.Equal(machine.ToString("D"), row.Foi.Key);
                Assert.Equal(instance.CollectorInstanceId, row.ObserverId);
                Assert.Equal("desktop-activity", row.Aspect);
            });
            foreach (var row in new[] { rows[0], rows[1], rows[3] })
            {
                Assert.Null(row.AppId);
                Assert.Empty(row.Relations);
            }
            Assert.Equal(JsonValueKind.Null, rows[0].Result.GetProperty("appIdentityKey").ValueKind);
            Assert.Equal("sys:away", rows[1].Result.GetProperty("appIdentityKey").GetString());
            Assert.Equal("restored.cs", rows[2].Result.GetProperty("title").GetString());
            Assert.NotNull(rows[2].AppId);
            Assert.Single(rows[2].Relations);
            for (var i = 0; i < rows.Length; i++)
            {
                Assert.Equal(start.AddSeconds(i * 10), rows[i].Start);
                Assert.Equal(start.AddSeconds((i + 1) * 10), rows[i].End);
                var sent = Assert.Single(pending, item => item.Observation!.Id == rows[i].Id).Observation!;
                Assert.True(JsonElement.DeepEquals(sent.Result!.Value, rows[i].Result));
            }
            runtime.ConfirmUploadedFacts(pending);
            Assert.Empty(runtime.ReadPendingFacts());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SystemObservations_InputEncodingAndRealCapacityGapSurviveRestartAndMixedHttpRetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-system-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using (var db = CreateDbContext())
            {
                db.Users.Add(new User { Id = "owner", Username = "alice" });
                await db.SaveChangesAsync();
            }
            var start = new DateTimeOffset(2026, 9, 10, 11, 0, 0, TimeSpan.Zero);
            var clock = new SharedClock(start);
            var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
            var path = Path.Combine(directory, "runtime.json");
            var machine = Guid.NewGuid();
            Guid observer;
            List<FactUploadItem> original;
            using (var runtime = CollectorRuntime.Open(path, new UnusedProjection()))
            {
                observer = runtime.CreateInstance(package, new SubjectReference(machine, SubjectKind.Machine),
                    new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { }))).CollectorInstanceId;
                var protocol = new SystemCollectorProtocolAdapter(inputEventIngressCapacity: 3);
                var producer = new RecordingSystemPublisher(protocol);
                var input = new InputEventBuffer(clock, publisher: producer);
                // Queue genuine normalized events before activation, so the durable first stage has
                // deterministic pressure rather than relying on a delivery-pump scheduling race.
                Assert.True(input.OnKeyDown(InputKeyPosition.KeyA));
                Assert.False(input.OnKeyDown(InputKeyPosition.KeyA));
                clock.Advance(TimeSpan.FromMilliseconds(1));
                input.OnMouseButton(2);
                clock.Advance(TimeSpan.FromMilliseconds(1));
                input.OnScroll(-InputEventBuffer.WheelDelta);
                clock.Advance(TimeSpan.FromMilliseconds(1));
                Assert.True(input.OnKeyDown(InputKeyPosition.KeyB));
                using var monitor = new AppMonitorService(clock, new SystemObservationSource(DesktopActivity.None),
                    new InputSignal(), protocol, new SegmentIngestService(clock), new DesktopSettings());
                using var collector = new SystemInProcessCollector(protocol, monitor);
                await using var activation = await runtime.ActivateInProcessAsync(observer, package, collector);
                await activation.StopAsync();
                original = runtime.ReadPendingFacts();
                Assert.Equal(3, original.Count(item => item.Observation?.Kind == "event"));
                Assert.Equal(producer.Inputs.Take(3).Select(item => item.Id).Order(),
                    original.Where(item => item.Observation is not null).Select(item => item.Observation!.Id).Order());
                var gap = Assert.Single(original, item => item.Gap is not null).Gap!;
                Assert.Equal(1, gap.EstimatedFactsLost);
                Assert.Equal("input_ingress_capacity_exceeded", gap.Reason);
                Assert.Equal(start.AddMilliseconds(3), gap.Start);
                Assert.True(gap.End > gap.Start);
                Assert.Empty(input.ReadAll());
            }
            await using var app = CreateApplication(clock);
            using var http = app.CreateDefaultClient(new FailFirstSystemGapUploadHandler());
            http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
            var api = new HeartbeatApiClient(http);
            using var restarted = CollectorRuntime.Open(path, new UnusedProjection());
            var pending = restarted.ReadPendingFacts();
            Assert.Equal(original.Count, pending.Count);
            Assert.Equal(Assert.Single(original, item => item.Gap is not null).Gap!.GapId,
                Assert.Single(pending, item => item.Gap is not null).Gap!.GapId);
            var upload = new UploadStream<FactUploadItem>("system mixed observations and Gap",
                [new RuntimeFactUploadSource(restarted)], (batch, ct) => api.UploadFactsAsync(batch, ct));
            await upload.DrainAsync();
            // Native observations reached PostgreSQL, but the real Gap transport failed. The
            // batch owner must retain both portions until the complete mixed request succeeds.
            Assert.Equal(3, (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/events"))!.Count);
            Assert.Equal(pending.Count, restarted.ReadPendingFacts().Count);
            await upload.DrainAsync();
            Assert.Empty(restarted.ReadPendingFacts());
            Assert.True((await api.UploadFactsAsync(pending)).Success);
            var rows = (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/events"))!
                .OrderBy(row => row.OccurredAt).ToArray();
            Assert.Equal(3, rows.Length);
            var expected = new[] { (Type: "keyDown", Code: 4), (Type: "mouseButton", Code: 2), (Type: "mouseScroll", Code: 2) };
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                Assert.Equal(1, row.Revision);
                Assert.Equal(observer, row.ObserverId);
                Assert.Equal("input", row.Aspect);
                Assert.Equal(machine.ToString("D"), row.Foi!.Key);
                Assert.Empty(row.Relations);
                Assert.Null(row.AppId);
                Assert.Null(row.StreamId);
                Assert.Null(row.FactId);
                Assert.Equal(start.AddMilliseconds(i), row.OccurredAt);
                Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(new
                {
                    eventType = expected[i].Type,
                    codeSet = "heartbeat-key-position-v1",
                    code = expected[i].Code
                }), row.Result));
                Assert.Single(original, item => item.Observation?.Id == row.Id);
            }
            // A fresh SDK activation after restart must keep the same concrete Observer.
            var resumedProtocol = new SystemCollectorProtocolAdapter();
            var resumedInput = new InputEventBuffer(clock, publisher: resumedProtocol);
            using var resumedMonitor = new AppMonitorService(clock, new SystemObservationSource(DesktopActivity.None),
                new InputSignal(), resumedProtocol, new SegmentIngestService(clock), new DesktopSettings());
            using var resumedCollector = new SystemInProcessCollector(resumedProtocol, resumedMonitor);
            await using var resumed = await restarted.ActivateInProcessAsync(observer, package, resumedCollector);
            resumedInput.OnMouseButton(1);
            await resumed.StopAsync();
            var next = Assert.Single(restarted.ReadPendingFacts()).Observation!;
            Assert.Equal(observer, next.CollectorId);
            Assert.DoesNotContain(original, item => item.Observation?.Id == next.Id);
            Assert.True((await api.UploadFactsAsync(restarted.ReadPendingFacts())).Success);
            var all = (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/events"))!;
            Assert.Equal(4, all.Count);
            Assert.All(all, row => Assert.Equal(observer, row.ObserverId));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SystemObservations_LegacyFirstStageAndAcknowledgedCheckpointReachHttpWithoutRekeyingOrDowntimeGrowth()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-system-legacy-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using (var db = CreateDbContext())
            {
                db.Users.Add(new User { Id = "owner", Username = "alice" });
                await db.SaveChangesAsync();
            }
            var oldSegmentId = Guid.Parse("0197ea40-2222-7000-8000-000000000001");
            var oldInputId = Guid.Parse("0197ea40-3333-7000-8000-000000000001");
            var oldStart = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
            var clock = new SharedClock(oldStart.AddDays(1));
            var path = Path.Combine(directory, "runtime.json");
            var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
            List<FactUploadItem> pending;
            using (var runtime = CollectorRuntime.Open(path, new UnusedProjection()))
            {
                var instance = runtime.CreateInstance(package, new SubjectReference(Guid.NewGuid(), SubjectKind.Machine),
                    new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { })));
                var data = Path.Combine(directory, "collector-data", instance.CollectorInstanceId.ToString("N"));
                Directory.CreateDirectory(data);
                // These are the historical unversioned NDJSON records, read through the shipped
                // first-stage recovery entry point. The reset holds an already-ACKed live checkpoint.
                File.WriteAllText(Path.Combine(data, "system-collector-ingress.json"), """
                    {"EntryId":"0197ea40-1111-7000-8000-000000000001","Kind":"reset","MutatesCheckpoint":true,"Checkpoint":{"FactId":"0197ea40-2222-7000-8000-000000000001","Revision":9,"IdentityKey":"system|win:code|draft","AppIdentityKey":"win:code","AppDisplayName":"Code","Title":"draft","Start":"2026-08-01T10:00:00+00:00","End":"2026-08-01T10:02:00+00:00","IsFinal":false}}
                    {"EntryId":"0197ea40-1111-7000-8000-000000000002","Kind":"input_event","InputEvent":{"Id":"0197ea40-3333-7000-8000-000000000001","EventType":1,"CodeSet":"windows-vk-v1","Code":65,"Timestamp":"2026-08-01T10:00:00.123456+00:00"}}
                    """ + "\n");
                var protocol = new SystemCollectorProtocolAdapter();
                var input = new InputEventBuffer(clock, publisher: protocol);
                input.OnMouseButton(1);
                using var monitor = new AppMonitorService(clock, new SystemObservationSource(DesktopActivity.None),
                    new InputSignal(), protocol, new SegmentIngestService(clock), new DesktopSettings());
                using var collector = new SystemInProcessCollector(protocol, monitor);
                await using var activation = await runtime.ActivateInProcessAsync(instance.CollectorInstanceId, package, collector);
                await activation.StopAsync();
                pending = runtime.ReadPendingFacts();
                var oldSegment = Assert.Single(pending, item => item.Fact?.FactId == oldSegmentId);
                Assert.Null(oldSegment.Observation);
                Assert.Equal(10, oldSegment.Fact!.Revision);
                Assert.True(oldSegment.Fact.IsFinal);
                Assert.Equal(oldStart.AddMinutes(2), oldSegment.Fact.End);
                var oldInput = Assert.Single(pending, item => item.Fact?.FactId == oldInputId);
                Assert.Null(oldInput.Observation);
                Assert.Single(pending, item => item.Observation?.Kind == "event");
                var gap = Assert.Single(pending, item => item.Gap is not null).Gap!;
                Assert.Equal(oldStart.AddMinutes(2), gap.Start);
                Assert.Equal(clock.UtcNow, gap.End);
                Assert.Equal("process_restart", gap.Reason);
            }
            await using var app = CreateApplication(clock);
            using var http = app.CreateClient();
            http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
            var api = new HeartbeatApiClient(http);
            Assert.True((await api.UploadFactsAsync(pending)).Success);
            var segment = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
            Assert.Equal(oldSegmentId, segment.FactId);
            Assert.NotEqual(oldSegmentId, segment.Id);
            Assert.Equal(10, segment.Revision);
            Assert.Equal(oldStart.AddMinutes(2), segment.End);
            var events = (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/events"))!;
            var historical = Assert.Single(events, row => row.FactId == oldInputId);
            Assert.Equal(oldStart.AddTicks(1234560), historical.OccurredAt);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(new
            {
                eventType = "keyDown", codeSet = "windows-vk-v1", code = 65
            }), historical.Result));
            var native = Assert.Single(events, row => row.FactId is null);
            Assert.Equal(Assert.Single(pending, item => item.Observation is not null).Observation!.Id, native.Id);
            using var restarted = CollectorRuntime.Open(path, new UnusedProjection());
            var upload = new UploadStream<FactUploadItem>("system legacy recovery",
                [new RuntimeFactUploadSource(restarted)], (batch, ct) => api.UploadFactsAsync(batch, ct));
            await upload.DrainAsync();
            Assert.Empty(restarted.ReadPendingFacts());
            var replayed = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
            Assert.Equal(segment.Id, replayed.Id);
            Assert.Equal(segment.Revision, replayed.Revision);
            Assert.Equal(segment.End, replayed.End);
            var replayedEvents = (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/events"))!;
            Assert.Equal(events.OrderBy(row => row.Id).Select(row => row.Id), replayedEvents.OrderBy(row => row.Id).Select(row => row.Id));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class RecordingSystemPublisher(SystemCollectorProtocolAdapter protocol)
        : ISystemSegmentPublisher, ISystemInputEventPublisher
    {
        public List<ForegroundSegmentSnapshot> Segments { get; } = [];
        public List<InputEventItem> Inputs { get; } = [];
        public void Publish(ForegroundSegmentSnapshot snapshot)
        {
            Segments.Add(snapshot);
            protocol.Publish(snapshot);
        }
        public void PublishBatch(IReadOnlyList<ForegroundSegmentSnapshot> snapshots)
        {
            Segments.AddRange(snapshots);
            protocol.PublishBatch(snapshots);
        }
        public void StageDurableBatch(IReadOnlyList<ForegroundSegmentSnapshot> snapshots)
        {
            Segments.AddRange(snapshots);
            protocol.StageDurableBatch(snapshots);
        }
        public void RecoverInterruptedSegment(DateTimeOffset recoveredAt) => protocol.RecoverInterruptedSegment(recoveredAt);
        public void ClearActiveCheckpoint(Guid factId, long revision) => protocol.ClearActiveCheckpoint(factId, revision);
        public void Publish(InputEventItem item)
        {
            Inputs.Add(item);
            protocol.Publish(item);
        }
    }

    private sealed class FailFirstSystemGapUploadHandler : DelegatingHandler
    {
        private bool _failed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_failed && request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/facts")
            {
                _failed = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("Simulated offline Gap upload")
                });
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static async Task<List<FactUploadItem>> WaitForSystemFacts(
        CollectorRuntime runtime, Func<List<FactUploadItem>, bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var items = runtime.ReadPendingFacts();
            if (ready(items)) return items;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class SystemObservationSource(DesktopActivity initial) : IDesktopObservationSource
    {
        public event Action<DesktopObservation>? Observation;
        public DesktopActivity CurrentActivity { get; private set; } = initial;
        public void Observe(DesktopObservation observation)
        {
            CurrentActivity = observation.Activity;
            Observation?.Invoke(observation);
        }
        public void Start() { }
        public void Stop() { }
    }
}
