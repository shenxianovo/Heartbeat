using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class ScenarioProcessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"heartbeat-process-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainsBothOutputPipesAndRetainsContentOnlyWhenRequested(bool retain)
    {
        // Given a real child that fills both OS pipes before exiting.
        var (repository, assembly) = await FixtureAsync();
        var log = Path.Combine(_directory, "process.log");
        await using var child = new ScenarioProcess(repository.Root, "dotnet", [assembly, "--burst-exit"], logPath: retain ? log : null);

        // When waiting for process completion, output must not block its exit.
        var result = await child.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Completed >= result.Started);
        Assert.Equal(retain, File.Exists(log));
        if (retain) Assert.Equal(new string('A', 262144) + new string('B', 262144), await File.ReadAllTextAsync(log, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndCancellationStopTheChildAndKeepDistinctOutcomes(bool cancel)
    {
        var (repository, assembly) = await FixtureAsync();
        await using var child = new ScenarioProcess(repository.Root, "dotnet", [assembly]);

        var error = await Record.ExceptionAsync(() => child.WaitAsync(TimeSpan.Zero, new CancellationToken(cancel)));

        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<TimeoutException>(error);
        Assert.True(child.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposingAnUnfinishedScopeStopsItsChild()
    {
        var (repository, assembly) = await FixtureAsync();
        var child = new ScenarioProcess(repository.Root, "dotnet", [assembly]);

        await child.DisposeAsync();

        Assert.True(child.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StateWaitAndProcessStopComposeIntoAGracefulSession()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var (repository, assembly) = await FixtureAsync();
        var ready = Path.Combine(_directory, "ready");
        await using var child = new ScenarioProcess(repository.Root, "dotnet", [assembly, "--ready-file", ready]);
        await ScenarioWait.UntilAsync("child signal handling readiness", _ => Task.FromResult(File.Exists(ready)), CancellationToken.None);

        var result = await child.StopAsync(CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateWaitDistinguishesADeadlineFromUserCancellation(bool cancel)
    {
        var error = await Record.ExceptionAsync(() => ScenarioWait.UntilAsync("an unavailable state",
            _ => Task.FromResult(false), new CancellationToken(cancel), TimeSpan.Zero));

        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<TimeoutException>(error);
    }

    private async Task<(RepositoryContext Repository, string Assembly)> FixtureAsync()
    {
        Directory.CreateDirectory(_directory);
        var repository = await RepositoryContext.DiscoverAsync(Environment.CurrentDirectory);
        return (repository, repository.Path("tests", "Heartbeat.Dev.Tests", "Fixtures", "SignalAwareProcess",
            "bin", "Debug", "net10.0", "SignalAwareProcess.dll"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
