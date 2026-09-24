using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class CloseoutTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(7, 1, 7)]
    [InlineData(7, 130, 130)]
    public async Task RunsQualityAfterOrdinaryFailuresAndRetainsFailureOrCancellation(int verify, int quality, int expected)
    {
        var order = new List<string>();
        var result = await CloseoutCommand.RunChecksAsync(
            () => { order.Add("verify"); return Task.FromResult(verify); },
            () => { order.Add("quality"); return Task.FromResult(quality); }, CancellationToken.None);

        Assert.Equal(["verify", "quality"], order);
        Assert.Equal(expected, result.ExitCode);
        Assert.Equal(quality, result.Quality);
    }

    [Fact]
    public async Task VerificationCancellationDoesNotStartQuality()
    {
        var result = await CloseoutCommand.RunChecksAsync(() => Task.FromResult(130),
            () => throw new InvalidOperationException("quality must not run"), CancellationToken.None);

        Assert.Equal(130, result.ExitCode);
        Assert.Null(result.Quality);
    }

    [Fact]
    public async Task ExecutionFailureDoesNotStartQuality()
    {
        await Assert.ThrowsAsync<IOException>(() => CloseoutCommand.RunChecksAsync(
            () => throw new IOException("cannot save evidence"),
            () => throw new InvalidOperationException("quality must not run"), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationBetweenChecksStopsQuality()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => CloseoutCommand.RunChecksAsync(
            () => { cancellation.Cancel(); return Task.FromResult(0); },
            () => throw new InvalidOperationException("quality must not run"), cancellation.Token));
    }

    [Fact]
    public async Task PlanResolvesOneCommitAndIncludesQualityWithoutRunningChecks()
    {
        var runner = new PlanRunner();
        var output = new StringWriter();
        var cli = new DeveloperCli(new RepositoryContext(Path.GetTempPath()), runner, output, TextWriter.Null);

        var code = await cli.RunAsync(["verify", "closeout", "--base", "branch", "--plan"], CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(3, runner.Calls);
        Assert.Contains("quality: structural gate --base " + PlanRunner.Commit, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Not included:", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class PlanRunner : IProcessRunner
    {
        public const string Commit = "0123456789012345678901234567890123456789";
        public int Calls { get; private set; }

        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            Assert.Equal("git", fileName);
            var result = Calls++ switch
            {
                0 => Resolve(arguments),
                1 => Diff(arguments),
                2 => new ProcessResult(0, "", ""),
                _ => throw new InvalidOperationException("Plan must not run checks."),
            };
            return Task.FromResult(result);
        }

        private static ProcessResult Resolve(IReadOnlyList<string> arguments)
        {
            Assert.Equal(["rev-parse", "--verify", "branch^{commit}"], arguments);
            return new ProcessResult(0, Commit + "\n", "");
        }

        private static ProcessResult Diff(IReadOnlyList<string> arguments)
        {
            Assert.Contains(Commit, arguments);
            return new ProcessResult(0, "src/A.cs\0", "");
        }

        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
