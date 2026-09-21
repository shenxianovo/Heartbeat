using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class SetupAuthCheckTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledStepTimesOutAndOnlyTheStartedCheckerIsRemoved(bool stallBuild)
    {
        using var output = new StringWriter();
        var runner = new StalledRunner(output, stallBuild);
        var checker = new SetupAuthCheck(new RepositoryContext(Path.GetTempPath()), runner, output,
            TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => checker.RunAsync("test.env", CancellationToken.None));

        Assert.Contains("timed out", error.Message);
        Assert.Contains(".env.local was preserved", error.Message);
        Assert.True(runner.ProcessCancelled);
        if (stallBuild) Assert.Empty(runner.Removed);
        else Assert.Equal([Assert.IsType<string>(runner.Container)], runner.Removed);
    }

    [Fact]
    public async Task UserCancellationCleansUpCheckerUsingAnIndependentToken()
    {
        using var output = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var runner = new StalledRunner(output, cancel: cancellation);
        var checker = new SetupAuthCheck(new RepositoryContext(Path.GetTempPath()), runner, output);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.RunAsync("test.env", cancellation.Token));

        Assert.True(runner.ProcessCancelled);
        Assert.Equal([Assert.IsType<string>(runner.Container)], runner.Removed);
    }

    private sealed class StalledRunner(StringWriter output, bool stallBuild = false, CancellationTokenSource? cancel = null) : IProcessRunner
    {
        public string? Container { get; private set; }
        public List<string> Removed { get; } = [];
        public bool ProcessCancelled { get; private set; }

        public async Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            Assert.Equal("docker", fileName);
            if (arguments[0] == "rm")
            {
                Assert.True(ProcessCancelled);
                Assert.False(cancellationToken.IsCancellationRequested);
                Assert.Equal("--force", arguments[1]);
                Removed.Add(arguments[2]);
                return new ProcessResult(0, "", "");
            }
            var build = arguments.Contains("build");
            Assert.Contains(build ? "Building Hub authentication checker" : "Validating API key with Auth", output.ToString());
            if (build && !stallBuild) return new ProcessResult(0, "", "");
            if (!build) Container = arguments[arguments.ToList().IndexOf("--name") + 1];
            cancel?.Cancel();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally { ProcessCancelled = true; }
            throw new InvalidOperationException("The stalled process must be cancelled.");
        }

        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
