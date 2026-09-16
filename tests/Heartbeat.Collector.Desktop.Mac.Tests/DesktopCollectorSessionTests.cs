using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Heartbeat.Collector.Desktop.Mac;
using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class DesktopCollectorSessionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly ForegroundApplication FirstApp = new("macos", "bundle_id", "com.example.First");
    private static readonly ForegroundApplication NextApp = new("macos", "bundle_id", "com.example.Next");

    [Fact]
    public async Task SlowUploadDoesNotBlockSamplingOrDiscardNewProgress()
    {
        var uploadStarted = NewSignal();
        var releaseUpload = NewSignal();
        var sampledTransitions = NewSignal();
        var uploadedTransitions = NewSignal();
        var samplesWhileUploading = 0;
        var reader = new ScriptedSource(_ =>
        {
            if (!uploadStarted.Task.IsCompleted)
            {
                return FirstApp;
            }

            if (++samplesWhileUploading == 3)
            {
                sampledTransitions.TrySetResult();
            }

            return samplesWhileUploading switch { 1 => FirstApp, 2 => NextApp, _ => null };
        });
        using var handler = new UploadHandler(async (count, token) =>
        {
            if (count == 1)
            {
                uploadStarted.TrySetResult();
                await releaseUpload.Task.WaitAsync(token);
            }

            if (count == 2)
            {
                uploadedTransitions.TrySetResult();
            }

            return Stored();
        });
        using var httpClient = CreateClient(handler);
        using var stop = new CancellationTokenSource(TestTimeout);
        var session = new DesktopCollectorSession(reader, new HubSubmissionClient(httpClient), TimeProvider.System);
        var run = session.RunAsync(Options(), stop.Token);
        try
        {
            await uploadStarted.Task.WaitAsync(TestTimeout);
            await sampledTransitions.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Single(handler.Records);
            releaseUpload.TrySetResult();
            await uploadedTransitions.Task.WaitAsync(TestTimeout);
            var sent = handler.Records.ToArray();
            Assert.Equal(sent[0].Id, sent[1].Id);
            Assert.True(sent[1].EndedAt > sent[0].EndedAt);
            Assert.NotEqual(sent[0].Id, sent[2].Id);
            Assert.Equal(NextApp.Id, sent[2].ApplicationId);
        }
        finally
        {
            await stop.CancelAsync();
            await IgnoreCancellationAsync(run);
        }
    }

    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("rejected")]
    [InlineData("missing-result")]
    public async Task FailedUploadIsRetriedWithTheSameRecordIdentity(string failure)
    {
        var retried = NewSignal();
        var reader = new ScriptedSource(_ => FirstApp);
        using var handler = new UploadHandler((count, _) =>
        {
            if (count == 1)
            {
                return failure switch
                {
                    "network" => Task.FromException<HttpResponseMessage>(new HttpRequestException("Offline")),
                    "timeout" => Task.FromException<HttpResponseMessage>(new TaskCanceledException("Timeout", new TimeoutException())),
                    "missing-result" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{}"),
                    }),
                    _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"results":[{"status":"conflict"}]}"""),
                    }),
                };
            }

            retried.TrySetResult();
            return Task.FromResult(Stored());
        });
        using var httpClient = CreateClient(handler);
        using var stop = new CancellationTokenSource(TestTimeout);
        var session = new DesktopCollectorSession(reader, new HubSubmissionClient(httpClient), TimeProvider.System);
        var run = session.RunAsync(Options(), stop.Token);
        try
        {
            await retried.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var sent = handler.Records.ToArray();
            Assert.Equal(sent[0].Id, sent[1].Id);
            Assert.True(sent[1].EndedAt >= sent[0].EndedAt);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            await stop.CancelAsync();
            await IgnoreCancellationAsync(run);
        }
    }

    [Fact]
    public async Task OnceWaitsForOneUploadWithoutAdditionalSampling()
    {
        var uploadStarted = NewSignal();
        var releaseUpload = NewSignal();
        var reader = new ScriptedSource(_ => FirstApp);
        using var handler = new UploadHandler(async (_, token) =>
        {
            uploadStarted.TrySetResult();
            await releaseUpload.Task.WaitAsync(token);
            return Stored();
        });
        using var httpClient = CreateClient(handler);
        using var stop = new CancellationTokenSource(TestTimeout);
        var session = new DesktopCollectorSession(reader, new HubSubmissionClient(httpClient), TimeProvider.System);
        var run = session.RunAsync(Options() with { Once = true }, stop.Token);
        try
        {
            await uploadStarted.Task.WaitAsync(TestTimeout);
            Assert.False(run.IsCompleted);
            releaseUpload.TrySetResult();
            await run.WaitAsync(TestTimeout);
            Assert.Equal(1, reader.ReadCount);
            Assert.Single(handler.Records);
        }
        finally
        {
            await stop.CancelAsync();
            await IgnoreCancellationAsync(run);
        }
    }

    [Fact]
    public async Task OncePropagatesUploadFailureWithoutRetrying()
    {
        var reader = new ScriptedSource(_ => FirstApp);
        using var handler = new UploadHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Offline")));
        using var httpClient = CreateClient(handler);
        var session = new DesktopCollectorSession(reader, new HubSubmissionClient(httpClient), TimeProvider.System);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            session.RunAsync(Options() with { Once = true }, CancellationToken.None));

        Assert.Equal(1, reader.ReadCount);
        Assert.Single(handler.Records);
    }

    [Fact]
    public async Task SamplingFailureCancelsAndJoinsAnInFlightUpload()
    {
        var uploadStarted = NewSignal();
        var uploadStopped = NewSignal();
        var reader = new ScriptedSource(_ => uploadStarted.Task.IsCompleted
            ? throw new InvalidOperationException("Sampling failed")
            : FirstApp);
        using var handler = new UploadHandler(async (_, token) =>
        {
            uploadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Stored();
            }
            finally
            {
                uploadStopped.TrySetResult();
            }
        });
        using var httpClient = CreateClient(handler);
        using var stop = new CancellationTokenSource(TestTimeout);
        var session = new DesktopCollectorSession(reader, new HubSubmissionClient(httpClient), TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RunAsync(Options(), stop.Token).WaitAsync(TestTimeout));

        Assert.Equal("Sampling failed", error.Message);
        Assert.True(uploadStopped.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task NativeEventsFlowThroughSharedSessionToTheirDeclaredTracks()
    {
        var source = new ScriptedSource(_ => FirstApp, "Document");
        using var handler = new TrackCaptureHandler();
        using var httpClient = CreateClient(handler);
        using var stop = new CancellationTokenSource(TestTimeout);
        var session = new DesktopCollectorSession(
            source, new HubSubmissionClient(httpClient), TimeProvider.System);
        var run = session.RunAsync(Options(), stop.Token);
        try
        {
            source.Emit(new MacSystemObservation.AwayEntered(MacAwayReason.ScreenLocked));
            source.Emit(new MacSystemObservation.Input(
                new DesktopInputObservation(DesktopInputKind.MouseButtonDown, 1)));

            await handler.DesktopTracksReceived.Task.WaitAsync(TestTimeout);
            Assert.Contains("desktop.application.foreground", handler.TrackTypes);
            Assert.Contains("desktop.window.foreground", handler.TrackTypes);
            Assert.Contains("desktop.system.away", handler.TrackTypes);
            Assert.Contains("desktop.input.event", handler.TrackTypes);
        }
        finally
        {
            await stop.CancelAsync();
            await IgnoreCancellationAsync(run);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EventsQueuedDuringCaptureKeepTheirReceiptTimeAndSupersedeTheSnapshot(int captureNumber)
    {
        var started = NewSignal();
        using var release = new ManualResetEventSlim();
        var clock = new ReceiptTimeProvider();
        var source = new ScriptedSource(count =>
        {
            if (count == captureNumber)
            {
                started.TrySetResult();
                Assert.True(release.Wait(TestTimeout));
                return FirstApp;
            }
            return count < captureNumber ? FirstApp : NextApp;
        });
        using var handler = new SnapshotCaptureHandler(clock.Baseline.AddSeconds(10));
        using var httpClient = CreateClient(handler);
        using var stop = new CancellationTokenSource(TestTimeout);
        var session = new DesktopCollectorSession(source, new HubSubmissionClient(httpClient), clock);
        var run = Task.Run(() => session.RunAsync(Options(), stop.Token));
        try
        {
            await started.Task.WaitAsync(TestTimeout);
            clock.Seconds = 1;
            source.Emit(new MacSystemObservation.Input(
                new DesktopInputObservation(DesktopInputKind.MouseButtonDown, 1)));
            source.Emit(new MacSystemObservation.Activity(
                new DesktopActivitySample(NextApp, null)));
            clock.Seconds = 10;
            release.Set();
            await handler.ReceivedInput.Task.WaitAsync(TestTimeout);
            await handler.NextApplicationConfirmed.Task.WaitAsync(TestTimeout);

            var records = handler.Records.ToArray();
            var input = Assert.Single(records, item => item.Type == "desktop.input.event");
            Assert.Equal(clock.Baseline.AddSeconds(1), input.Record.StartedAt);
            Assert.DoesNotContain(records, item => item.Type == "desktop.application.foreground"
                && item.Record.Value.GetProperty("application").GetProperty("id").GetString() == FirstApp.Id
                && (captureNumber == 1 || item.Record.StartedAt > clock.Baseline));
        }
        finally
        {
            release.Set();
            await stop.CancelAsync();
            await IgnoreCancellationAsync(run);
        }
    }

    private sealed class ReceiptTimeProvider : TimeProvider
    {
        public DateTimeOffset Baseline { get; } = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
        private long _seconds;
        public long Seconds { get => Interlocked.Read(ref _seconds); set => Interlocked.Exchange(ref _seconds, value); }
        public override DateTimeOffset GetUtcNow() => Baseline.AddSeconds(Seconds);
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    private sealed class SnapshotCaptureHandler(DateTimeOffset confirmedAt) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        public ConcurrentQueue<(string Type, RecordSnapshot Record)> Records { get; } = new();
        public TaskCompletionSource ReceivedInput { get; } = NewSignal();
        public TaskCompletionSource NextApplicationConfirmed { get; } = NewSignal();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            var type = root.GetProperty("track").GetProperty("type").GetString()!;
            var records = root.GetProperty("records").Deserialize<RecordSnapshot[]>(JsonOptions)!;
            foreach (var record in records) Records.Enqueue((type, record));
            if (type == "desktop.input.event") ReceivedInput.TrySetResult();
            if (type == "desktop.application.foreground" && records.Any(record =>
                record.EndedAt >= confirmedAt
                && record.Value.GetProperty("application").GetProperty("id").GetString() == NextApp.Id))
                NextApplicationConfirmed.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    results = records.Select((record, index) => new
                    {
                        index,
                        record.Id,
                        status = "accepted",
                        record.EndedAt,
                    }),
                }, JsonOptions)),
            };
        }
    }

    private static CollectorOptions Options() => new(new Uri("http://localhost:8080"), "test-token",
        "device-a", "Test Mac", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40),
        TimeSpan.Zero, false);

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("http://localhost:8080"),
    };

    private static HttpResponseMessage Stored() => new(HttpStatusCode.NoContent);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TestTimeout);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class ScriptedSource(Func<int, ForegroundApplication?> read, string? windowTitle = null)
        : IMacSystemObservationSource
    {
        public int ReadCount { get; private set; }

        public event Action<MacSystemObservation>? Observation;
        public void Emit(MacSystemObservation observation) => Observation?.Invoke(observation);
        public MacSystemSnapshot Capture()
        {
            var application = read(++ReadCount);
            return new MacSystemSnapshot(
                application is null ? null : new DesktopActivitySample(application, windowTitle),
                []);
        }
        public void RefreshCapabilities() { }
        public void StartObserving() { }
        public void StopObserving() { }
        public void Dispose() { }
    }

    private sealed class TrackCaptureHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, byte> _trackTypes = [];
        public TaskCompletionSource DesktopTracksReceived { get; } = NewSignal();
        public IReadOnlyCollection<string> TrackTypes => _trackTypes.Keys.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var trackType = body.RootElement.GetProperty("track").GetProperty("type").GetString()!;
            _trackTypes.TryAdd(trackType, 0);
            if (_trackTypes.ContainsKey("desktop.application.foreground")
                && _trackTypes.ContainsKey("desktop.window.foreground")
                && _trackTypes.ContainsKey("desktop.system.away")
                && _trackTypes.ContainsKey("desktop.input.event"))
            {
                DesktopTracksReceived.TrySetResult();
            }
            var results = body.RootElement.GetProperty("records").EnumerateArray()
                .Select((record, index) => new
                {
                    index,
                    id = record.GetProperty("id").GetGuid(),
                    status = "accepted",
                    endedAt = record.TryGetProperty("endedAt", out var endedAt)
                        ? endedAt.GetDateTimeOffset()
                        : (DateTimeOffset?)null,
                });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { results })),
            };
        }
    }

    private sealed record SentRecord(Guid Id, DateTimeOffset EndedAt, string ApplicationId);

    private sealed class UploadHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public ConcurrentQueue<SentRecord> Records { get; } = new();
        private int _requestCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var sent = body.RootElement.GetProperty("records").EnumerateArray()
                .Select(record => new SentRecord(
                    record.GetProperty("id").GetGuid(),
                    record.GetProperty("endedAt").GetDateTimeOffset(),
                    record.GetProperty("value").GetProperty("application").GetProperty("id").GetString()!))
                .ToArray();
            foreach (var record in sent)
            {
                Records.Enqueue(record);
            }
            var response = await send(Interlocked.Increment(ref _requestCount), cancellationToken);
            return response.StatusCode == HttpStatusCode.NoContent ? Accepted(sent) : response;
        }

        private static HttpResponseMessage Accepted(IReadOnlyList<SentRecord> records) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                results = records.Select((record, index) => new
                {
                    index,
                    id = record.Id,
                    status = "accepted",
                    endedAt = record.EndedAt,
                }),
            })),
        };
    }
}
