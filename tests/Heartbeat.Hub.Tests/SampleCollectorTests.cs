using Heartbeat.Collector.Sample;

namespace Heartbeat.Hub.Tests;

public sealed class SampleCollectorTests
{
    [Theory]
    [InlineData("observe\nobserve\ngap\nobserve\nquit", 2)]
    [InlineData("observe\nobserve\nobserve\nquit", 7)]
    public async Task GapStartsANewRangeAndStoppingDoesNotExtendIt(string commands, int lastSecond)
    {
        using var fixture = new QueueFixture();
        using var input = new StringReader(commands);
        var clock = new ObservationClock(0, 1, lastSecond);

        var exit = await SampleCollector.RunAsync("sample-a", new LocalHubSubmissionClient(fixture.Open()),
            input, TextWriter.Null, clock, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        var records = fixture.Open().TakePending();
        Assert.Equal(3, records.Count(item => item.Route.Track.TimeMode == "point"));
        var ranges = records.Where(item => item.Route.Track.TimeMode == "range")
            .OrderBy(item => item.Record.StartedAt).ToArray();
        Assert.Equal(2, ranges.Length);
        Assert.Equal(clock.Start, ranges[0].Record.StartedAt);
        Assert.Equal(clock.Start.AddSeconds(1), ranges[0].Record.EndedAt);
        Assert.Equal(clock.Start.AddSeconds(lastSecond), ranges[1].Record.StartedAt);
        Assert.Equal(ranges[1].Record.StartedAt, ranges[1].Record.EndedAt);
        Assert.All(records, item => Assert.Equal("sample-a", Assert.Single(item.Record.Objects).Key));
    }

    [Theory]
    [InlineData("observe\nretry\nquit")]
    [InlineData("observe\nquit")]
    public async Task LostReceiptRetriesTheSameSnapshotIncludingDuringFinalHandoff(string commands)
    {
        using var fixture = new QueueFixture();
        var hub = new LostReceipt(new LocalHubSubmissionClient(fixture.Open()));
        using var input = new StringReader(commands);

        var exit = await SampleCollector.RunAsync("sample-a", hub, input, TextWriter.Null,
            new ObservationClock(0), TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Equal(hub.Attempts[0], hub.Attempts[1]);
        Assert.Equal(2, fixture.Open().TakePending().Count);
    }

    [Fact]
    public async Task OfflineExitReportsUnconfirmedMemoryOnlySnapshots()
    {
        using var input = new StringReader("observe\nquit");
        using var output = new StringWriter();
        var exit = await SampleCollector.RunAsync("sample-a", new OfflineHub(), input, output,
            new ObservationClock(0), TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Contains("2 条快照未被 Hub 接管", output.ToString());
    }

    [Fact]
    public async Task CancellationUnblocksConsoleInputAndPerformsFinalHandoff()
    {
        using var fixture = new QueueFixture();
        using var stop = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        using var input = new BlockingConsoleReader(stop, release);
        var hub = new LostReceipt(new LocalHubSubmissionClient(fixture.Open()));
        var run = Task.Run(() => SampleCollector.RunAsync("sample-a", hub, input, TextWriter.Null,
            new ObservationClock(0), stop.Token), TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.Equal(hub.Attempts[0], hub.Attempts[1]);
            Assert.Equal(2, fixture.Open().TakePending().Count);
        }
        finally
        {
            release.Set();
            await run;
        }
    }

    // Console.In can block synchronously even through ReadLineAsync.
    private sealed class BlockingConsoleReader(CancellationTokenSource stop, ManualResetEventSlim release) : TextReader
    {
        private bool _observed;
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default) => new(ReadLine());
        public override string? ReadLine()
        {
            if (!_observed) { _observed = true; return "observe"; }
            stop.Cancel();
            release.Wait();
            return null;
        }
    }

    private sealed class ObservationClock(params int[] seconds) : TimeProvider
    {
        private int _index = -1;
        public DateTimeOffset Start { get; } = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Start;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _index < 0
            ? ++_index : TimeSpan.FromSeconds(seconds[_index++]).Ticks;
    }

    private sealed class LostReceipt(IHubSubmissionClient inner) : IHubSubmissionClient
    {
        public List<Guid> Attempts { get; } = [];
        public async Task SubmitAsync(HubSubmission submission, CancellationToken cancellationToken = default)
        {
            Attempts.Add(submission.Records![0]!.Id);
            await inner.SubmitAsync(submission, cancellationToken);
            if (Attempts.Count == 1) throw new IOException("Simulated lost receipt after durable custody.");
        }
    }

    private sealed class OfflineHub : IHubSubmissionClient
    {
        public Task SubmitAsync(HubSubmission submission, CancellationToken cancellationToken = default) =>
            Task.FromException(new HttpRequestException("Simulated offline Hub."));
    }
}
