using Heartbeat.Collector.Desktop;
using Heartbeat.Hub;

namespace Heartbeat.Desktop.Tests;

public sealed class DesktopTests
{
    [Fact]
    public void ProfileKeepsSecretsOutOfFilesAndRejectsRebindingBeforeChangingCredentials()
    {
        var directory = TemporaryDirectory();
        try
        {
            var credentials = new MemoryCredentials();
            using var profile = new DesktopProfile(directory, credentials);
            var settings = Settings();
            profile.Save(settings, "private-api-key");
            Assert.Equal(settings, profile.ReadSettings());
            Assert.DoesNotContain("private-api-key", File.ReadAllText(Path.Combine(directory, "settings.json")));
            Assert.Throws<InvalidOperationException>(() => profile.Save(settings with { OwnerId = Guid.NewGuid() }, "replacement"));
            Assert.Equal("private-api-key", credentials.Secret);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SavedConnectionCanStartAndPauseWithoutLosingHubCustody()
    {
        var directory = TemporaryDirectory();
        try
        {
            var platform = new TestPlatform();
            var settings = Settings();
            await using var runtime = new DesktopRuntime(new DesktopProfile(directory, platform.Credentials),
                platform, new HttpClient(new AuthHandler(settings.OwnerId)));
            await runtime.ConfigureAsync(settings.BackendUrl, settings.AuthUrl, settings.WebUrl, AuthHandler.ApiKey, TestContext.Current.CancellationToken);
            await runtime.StartAsync();
            Assert.True(runtime.IsCollecting);
            Assert.True(runtime.Queue.Pending > 0);
            await runtime.StopCollectionAsync();
            Assert.False(runtime.IsCollecting);
            Assert.True(runtime.Queue.Pending > 0);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task StartWaitsForInFlightConfiguration()
    {
        var directory = TemporaryDirectory();
        try
        {
            var platform = new TestPlatform();
            var settings = Settings();
            using var handler = new HeldAuthHandler(settings.OwnerId);
            await using var runtime = new DesktopRuntime(new DesktopProfile(directory, platform.Credentials),
                platform, new HttpClient(handler));
            var configure = runtime.ConfigureAsync(settings.BackendUrl, settings.AuthUrl, settings.WebUrl, AuthHandler.ApiKey, TestContext.Current.CancellationToken);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var start = runtime.StartAsync();
            try { Assert.False(start.IsCompleted); }
            finally { handler.Release.TrySetResult(); await configure; }
            await start;
            Assert.True(runtime.IsCollecting);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task InitializationResumesOnceAndCannotUndoAnExplicitPause()
    {
        var directory = TemporaryDirectory();
        try
        {
            var platform = new TestPlatform();
            var settings = Settings();
            using (var profile = new DesktopProfile(directory, platform.Credentials))
                profile.Save(settings, AuthHandler.ApiKey);
            await using var runtime = new DesktopRuntime(new DesktopProfile(directory, platform.Credentials),
                platform, new HttpClient(new AuthHandler(settings.OwnerId)));
            await runtime.InitializeAsync();
            Assert.True(runtime.IsCollecting);
            await runtime.StopCollectionAsync();
            await runtime.InitializeAsync();
            Assert.False(runtime.IsCollecting);
            Assert.True(runtime.Queue.Pending > 0);
            await runtime.DisposeAsync();
            await runtime.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(runtime.StartAsync);
            using var reopened = new DesktopProfile(directory, platform.Credentials);
            Assert.Equal(settings, reopened.ReadSettings());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class HeldAuthHandler(Guid owner) : DelegatingHandler(new AuthHandler(owner))
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task StopHandsTheUnconfirmedSnapshotToHubBeforeReturning()
    {
        var directory = TemporaryDirectory();
        try
        {
            var queue = new RecordOutbox(Path.Combine(directory, "hub.sqlite"), Settings().Destination);
            var sink = new InterruptedSubmission(queue);
            using var source = new TestSource();
            using var stop = new CancellationTokenSource();
            var session = new DesktopCollectorSession("test.desktop", source, sink, TimeProvider.System);
            var run = session.RunAsync(new DesktopCollectionOptions("test-target", "Test", TimeSpan.FromHours(1),
                TimeSpan.FromHours(3), TimeSpan.Zero), stop.Token);
            await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await stop.CancelAsync();
            try { await run; } catch (OperationCanceledException) { }
            var stored = Assert.Single(queue.TakePending());
            Assert.Equal(sink.FirstId, stored.Record.Id);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class InterruptedSubmission(RecordOutbox queue) : IHubSubmissionClient
    {
        private bool _interrupted;
        public Guid FirstId { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SubmitAsync(HubSubmission submission, CancellationToken cancellationToken = default)
        {
            if (!_interrupted)
            {
                _interrupted = true;
                FirstId = submission.Records![0]!.Id;
                Started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            queue.Accept(submission);
        }
    }

    private static string TemporaryDirectory() => Directory.CreateTempSubdirectory("heartbeat-desktop-test-").FullName;
    private static DesktopSettings Settings() => new(new Uri("https://backend.example"), new Uri("https://auth.example"),
        new Uri("https://web.example"), Guid.NewGuid(), "test-target");
}
