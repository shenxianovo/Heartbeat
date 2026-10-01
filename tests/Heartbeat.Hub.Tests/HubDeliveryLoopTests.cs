using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

namespace Heartbeat.Hub.Tests;

public sealed class HubDeliveryLoopTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliversIdleArrivalsAndRecoveredBacklogsWithoutAnInterval(bool restart)
    {
        using var fixture = new QueueFixture();
        var queue = MappedQueue(fixture);
        var records = Enumerable.Range(0, 501).Select(_ => QueueFixture.Snapshot()).ToArray();
        if (restart)
        {
            AcceptAll(queue, records);
            queue = fixture.Open(); // No in-memory notification survives restart.
        }
        var delivered = new ConcurrentBag<Guid>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new UploadHandler(sent =>
        {
            foreach (var record in sent) delivered.Add(record.Id);
            if (delivered.Count == records.Length) complete.TrySetResult();
        });
        using var http = new HttpClient(handler);
        var loop = new HubDeliveryLoop(queue, new RecordUploader(queue, http, new Tokens(fixture)));
        await RunDeliveryAsync(queue, loop, async () =>
        {
            if (!restart) AcceptAll(queue, records);
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        });
        Assert.Equal(records.Select(record => record.Id).Order(), delivered.Order());
        Assert.Equal(new QueueStatus(0, 0), queue.Status());
    }

    [Fact]
    public async Task NewArrivalsCannotBypassFailureBackoffAndRetryNeedsNoNewSubmission()
    {
        using var fixture = new QueueFixture();
        var queue = MappedQueue(fixture);
        queue.Accept(QueueFixture.Submission(QueueFixture.Snapshot()));
        var attempts = 0;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new UploadHandler(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new HttpRequestException("offline");
            recovered.TrySetResult();
        });
        using var http = new HttpClient(handler);
        var loop = new HubDeliveryLoop(queue, new RecordUploader(queue, http, new Tokens(fixture)));
        loop.Failed += _ => failed.TrySetResult();
        await RunDeliveryAsync(queue, loop, async () =>
        {
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            queue.Accept(QueueFixture.Submission(QueueFixture.Snapshot()));
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref attempts));
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        });
        Assert.Null(loop.LastError);
        Assert.Equal(new QueueStatus(0, 0), queue.Status());
    }

    private static async Task RunDeliveryAsync(RecordOutbox queue, HubDeliveryLoop loop, Func<Task> exercise)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = loop.RunAsync(stop.Token);
        try
        {
            await exercise();
            while (queue.Status().Pending > 0) await Task.Delay(10, stop.Token);
        }
        finally
        {
            await stop.CancelAsync();
            try { await run; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static RecordOutbox MappedQueue(QueueFixture fixture)
    {
        var queue = fixture.Open();
        queue.Accept(QueueFixture.Submission(QueueFixture.Snapshot()));
        var seed = Assert.Single(queue.TakePending());
        queue.SaveMapping(seed.Route, Guid.NewGuid(), Guid.NewGuid());
        queue.Apply([new(seed, true, null)]);
        return queue;
    }

    private static void AcceptAll(RecordOutbox queue, RecordSnapshot[] records)
    {
        foreach (var batch in records.Chunk(HubSubmissionLimits.MaximumBatchSize))
            queue.Accept(QueueFixture.Submission(batch));
    }

    private sealed class Tokens(QueueFixture fixture) : IBackendTokenProvider
    {
        public ValueTask<BackendAccessToken?> GetTokenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<BackendAccessToken?>(new(fixture.Token, fixture.Destination.OwnerId, DateTimeOffset.UtcNow.AddHours(1)));
        public void Invalidate() { }
    }

    private sealed record Upload(RecordSnapshot[] Records);

    private sealed class UploadHandler(Action<RecordSnapshot[]> accept) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadFromJsonAsync<Upload>(cancellationToken);
            accept(body!.Records);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    results = body.Records.Select((record, index) => new
                    {
                        index, record.Id, status = "stored", record.EndedAt, receivedAt = DateTimeOffset.UtcNow,
                    }),
                }),
            };
        }
    }
}
