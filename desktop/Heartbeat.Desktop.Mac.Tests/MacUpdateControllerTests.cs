using Heartbeat.Desktop.Update;
using Heartbeat.Desktop.UI.Presentation;
using System.Net.Http;

namespace Heartbeat.Desktop.Mac.Tests;

public sealed class VelopackUpdateControllerTests
{
    [Fact]
    public async Task CheckWhenCurrent_DoesNotPrepareAgentForRestart()
    {
        var client = new FakeMacUpdateClient();
        var preparedForRestart = false;
        using var updateController = new VelopackUpdateController(
            client,
            () =>
            {
                preparedForRestart = true;
                return Task.CompletedTask;
            },
            []);
        IUpdateController controller = updateController;

        var result = await controller.CheckAsync();

        Assert.Equal(UpdateCheckResult.UpToDate, result);
        Assert.Equal(UpdateSnapshot.Idle, controller.Current);
        Assert.False(preparedForRestart);
    }

    [Fact]
    public async Task FoundUpdate_DownloadsToReadyWithoutPreparingAgentForRestart()
    {
        var release = new FakeMacUpdateRelease("2.0.0");
        var client = new FakeMacUpdateClient { Update = release };
        var preparedForRestart = false;
        using var controller = new VelopackUpdateController(
            client,
            () =>
            {
                preparedForRestart = true;
                return Task.CompletedTask;
            },
            []);
        var ready = WaitForState(controller, UpdateState.ReadyToApply);

        var result = await controller.CheckAsync();
        await client.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(UpdateCheckResult.UpdateFound, result);
        Assert.Equal(UpdateState.Downloading, controller.Current.State);
        Assert.False(preparedForRestart);

        client.AllowDownloadToComplete.SetResult();
        await ready.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new UpdateSnapshot(UpdateState.ReadyToApply, "2.0.0", 100), controller.Current);
        Assert.False(preparedForRestart);
    }

    [Fact]
    public async Task Apply_PreparesAgentOnlyAfterUpdateIsReady()
    {
        var release = new FakeMacUpdateRelease("2.0.0");
        var client = new FakeMacUpdateClient { Update = release };
        var lifecycle = new List<string>();
        client.Applied = _ => lifecycle.Add("schedule");
        using var controller = new VelopackUpdateController(
            client,
            () =>
            {
                lifecycle.Add("prepare");
                return Task.CompletedTask;
            },
            []);

        Assert.False(await controller.ApplyAsync());
        Assert.Empty(lifecycle);

        var ready = WaitForState(controller, UpdateState.ReadyToApply);
        await controller.CheckAsync();
        client.AllowDownloadToComplete.SetResult();
        await ready.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(await controller.ApplyAsync());
        Assert.Equal(["schedule", "prepare"], lifecycle);
    }

    [Fact]
    public async Task FailedApplyScheduling_LeavesAgentRunningAndReadyToRetry()
    {
        var client = new FakeMacUpdateClient
        {
            Update = new FakeMacUpdateRelease("2.0.0"),
            ScheduleException = new IOException("updater unavailable"),
        };
        var preparedForRestart = false;
        using var controller = new VelopackUpdateController(
            client,
            () =>
            {
                preparedForRestart = true;
                return Task.CompletedTask;
            },
            []);
        var ready = WaitForState(controller, UpdateState.ReadyToApply);
        await controller.CheckAsync();
        client.AllowDownloadToComplete.SetResult();
        await ready.WaitAsync(TimeSpan.FromSeconds(2));

        var applied = await controller.ApplyAsync();

        Assert.False(applied);
        Assert.False(preparedForRestart);
        Assert.Equal(UpdateState.ReadyToApply, controller.Current.State);
        Assert.Equal("应用更新失败，请重试。", controller.Current.Error);
    }

    [Fact]
    public async Task FailedCheck_IsDistinctFromBeingUpToDate()
    {
        var client = new FakeMacUpdateClient
        {
            CheckException = new HttpRequestException("offline"),
        };
        using var controller = new VelopackUpdateController(client, () => Task.CompletedTask, []);

        var result = await controller.CheckAsync();

        Assert.Equal(UpdateCheckResult.CheckFailed, result);
        Assert.Equal(UpdateState.Idle, controller.Current.State);
        Assert.Equal("检查更新失败，请检查网络后重试。", controller.Current.Error);
    }

    [Fact]
    public async Task TransientDownloadFailure_RetriesWithoutPreparingAgentForRestart()
    {
        var client = new FakeMacUpdateClient
        {
            Update = new FakeMacUpdateRelease("2.0.0"),
            AutoCompleteDownload = true,
        };
        client.DownloadFailures.Enqueue(new HttpRequestException("temporary"));
        var preparedForRestart = false;
        using var controller = new VelopackUpdateController(
            client,
            () =>
            {
                preparedForRestart = true;
                return Task.CompletedTask;
            },
            [TimeSpan.Zero]);
        var ready = WaitForState(controller, UpdateState.ReadyToApply);

        await controller.CheckAsync();
        await ready.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, client.DownloadAttempts);
        Assert.Equal(UpdateState.ReadyToApply, controller.Current.State);
        Assert.False(preparedForRestart);
    }

    private static Task WaitForState(VelopackUpdateController controller, UpdateState state)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += snapshot =>
        {
            if (snapshot.State == state) completion.TrySetResult();
        };
        return completion.Task;
    }

    private sealed class FakeMacUpdateClient : IReleaseUpdateClient
    {
        public bool IsInstalled { get; set; } = true;
        public IReleaseUpdate? Update { get; set; }
        public Exception? CheckException { get; set; }
        public Exception? ScheduleException { get; set; }
        public Queue<Exception> DownloadFailures { get; } = new();
        public bool AutoCompleteDownload { get; set; }
        public int DownloadAttempts { get; private set; }
        public TaskCompletionSource DownloadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDownloadToComplete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<IReleaseUpdate>? Applied { get; set; }

        public Task<IReleaseUpdate?> CheckForUpdatesAsync() => CheckException == null
            ? Task.FromResult(Update)
            : Task.FromException<IReleaseUpdate?>(CheckException);

        public async Task DownloadUpdatesAsync(IReleaseUpdate release, Action<int> progress)
        {
            DownloadAttempts++;
            if (DownloadFailures.TryDequeue(out var failure)) throw failure;
            DownloadStarted.TrySetResult();
            if (!AutoCompleteDownload) await AllowDownloadToComplete.Task;
        }
        public void ScheduleUpdateAndRestart(IReleaseUpdate release)
        {
            if (ScheduleException != null) throw ScheduleException;
            Applied?.Invoke(release);
        }
        public void ScheduleUpdateAndExit(IReleaseUpdate release) { }
    }

    private sealed record FakeMacUpdateRelease(string Version) : IReleaseUpdate;
}
