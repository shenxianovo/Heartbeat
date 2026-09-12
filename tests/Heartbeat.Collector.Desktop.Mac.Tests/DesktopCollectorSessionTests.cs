using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Heartbeat.Collector.Desktop.Mac;

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
        var reader = new ScriptedReader(_ =>
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

            if (count == 3)
            {
                uploadedTransitions.TrySetResult();
            }

            return Stored();
        });
        using var httpClient = CreateClient(handler);
        using var stop = new CancellationTokenSource(TestTimeout);
        var session = new DesktopCollectorSession(reader, new HeartbeatRecordingClient(httpClient), TimeProvider.System);
        var run = session.RunAsync(Guid.CreateVersion7(), Options(), stop.Token);
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
        var reader = new ScriptedReader(_ => FirstApp);
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
        var session = new DesktopCollectorSession(reader, new HeartbeatRecordingClient(httpClient), TimeProvider.System);
        var run = session.RunAsync(Guid.CreateVersion7(), Options(), stop.Token);
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
        var reader = new ScriptedReader(_ => FirstApp);
        using var handler = new UploadHandler(async (_, token) =>
        {
            uploadStarted.TrySetResult();
            await releaseUpload.Task.WaitAsync(token);
            return Stored();
        });
        using var httpClient = CreateClient(handler);
        using var stop = new CancellationTokenSource(TestTimeout);
        var session = new DesktopCollectorSession(reader, new HeartbeatRecordingClient(httpClient), TimeProvider.System);
        var run = session.RunAsync(Guid.CreateVersion7(), Options() with { Once = true }, stop.Token);
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
        var reader = new ScriptedReader(_ => FirstApp);
        using var handler = new UploadHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Offline")));
        using var httpClient = CreateClient(handler);
        var session = new DesktopCollectorSession(reader, new HeartbeatRecordingClient(httpClient), TimeProvider.System);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            session.RunAsync(Guid.CreateVersion7(), Options() with { Once = true }, CancellationToken.None));

        Assert.Equal(1, reader.ReadCount);
        Assert.Single(handler.Records);
    }

    [Fact]
    public async Task SamplingFailureCancelsAndJoinsAnInFlightUpload()
    {
        var uploadStarted = NewSignal();
        var uploadStopped = NewSignal();
        var reader = new ScriptedReader(_ => uploadStarted.Task.IsCompleted
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
        var session = new DesktopCollectorSession(reader, new HeartbeatRecordingClient(httpClient), TimeProvider.System);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RunAsync(Guid.CreateVersion7(), Options(), stop.Token).WaitAsync(TestTimeout));

        Assert.Equal("Sampling failed", error.Message);
        Assert.True(uploadStopped.Task.IsCompletedSuccessfully);
    }

    private static CollectorOptions Options() => new(new Uri("http://localhost:8080"), "test-token",
        "device-a", "Test Mac", TimeSpan.FromMilliseconds(20), false);

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("http://localhost:8080"),
    };

    private static HttpResponseMessage Stored() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"results":[{"status":"stored"}]}"""),
    };

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

    private sealed class ScriptedReader(Func<int, ForegroundApplication?> read) : IForegroundApplicationReader
    {
        public int ReadCount { get; private set; }

        public ForegroundApplication? Read() => read(++ReadCount);
    }

    private sealed record SentRecord(Guid Id, DateTimeOffset EndedAt, string ApplicationId);

    private sealed class UploadHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public ConcurrentQueue<SentRecord> Records { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var record = body.RootElement.GetProperty("records")[0];
            Records.Enqueue(new SentRecord(record.GetProperty("id").GetGuid(), record.GetProperty("endedAt").GetDateTimeOffset(),
                record.GetProperty("value").GetProperty("application").GetProperty("id").GetString()!));
            return await send(Records.Count, cancellationToken);
        }
    }
}
