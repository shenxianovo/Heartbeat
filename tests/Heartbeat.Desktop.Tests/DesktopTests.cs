using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Heartbeat.Collector.Desktop;
using Heartbeat.Desktop.UI;
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

    [AvaloniaFact]
    public async Task SavedConnectionCanStartPauseAndHideWithoutLosingHubCustody()
    {
        var directory = TemporaryDirectory();
        try
        {
            var platform = new TestPlatform();
            var profile = new DesktopProfile(directory, platform.Credentials);
            var settings = Settings();
            await using var runtime = new DesktopRuntime(profile, platform, new HttpClient(new AuthHandler(settings.OwnerId)));
            var model = new DesktopViewModel(runtime);
            var window = new MainWindow(model, "macos");
            window.Show();
            try
            {
                Assert.True(window.FindControl<TabItem>("ConnectionTab")!.IsSelected);
                window.FindControl<TextBox>("ApiKeyInput")!.Focus();
                window.KeyTextInput(AuthHandler.ApiKey);
                model.Refresh();
                window.FindControl<Button>("SaveConnectionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => model.IsConfigured && !model.IsBusy);
                Assert.Empty(model.ApiKey);
                Assert.Equal(string.Empty, window.FindControl<TextBox>("ApiKeyInput")!.Text);
                await model.ToggleAsync();
                Assert.True(runtime.IsCollecting);
                Assert.True(runtime.Queue.Pending > 0);
                window.Close();
                Assert.False(window.IsVisible);
                Assert.True(runtime.IsCollecting);
                window.Show();
                window.FindControl<Button>("ToggleCollectionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => !runtime.IsCollecting && !model.IsBusy);
                Assert.True(runtime.Queue.Pending > 0);
                Assert.Equal("开始采集", window.FindControl<Button>("ToggleCollectionButton")!.Content);
            }
            finally { window.AllowClose = true; window.Close(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
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

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }
    private static string TemporaryDirectory() => Directory.CreateTempSubdirectory("heartbeat-desktop-test-").FullName;
    private static DesktopSettings Settings() => new(new Uri("https://backend.example"), new Uri("https://auth.example"),
        new Uri("https://web.example"), Guid.NewGuid(), "test-target");
}
