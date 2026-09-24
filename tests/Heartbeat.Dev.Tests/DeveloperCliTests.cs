using System.CommandLine;
using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class DeveloperCliTests
{
    [Theory]
    [InlineData("env", "up", "--help")]
    [InlineData("env", "setup", "--help")]
    [InlineData("verify", "changed", "--help")]
    [InlineData("verify", "closeout", "--help")]
    [InlineData("quality", "loc", "--help")]
    [InlineData("scenario", "desktop-replay", "--help")]
    [InlineData("probe", "window-title", "--help")]
    [InlineData("artifacts", "prune", "--help")]
    [InlineData("package", "desktop", "--help")]
    [InlineData("signing", "setup", "--help")]
    [InlineData("signing", "status", "--help")]
    public async Task LeafHelpDoesNotExecuteCommands(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = Cli(output, error);
        Assert.Equal(0, await cli.RunAsync(args, CancellationToken.None));
        Assert.Contains("Usage:", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("env")]
    [InlineData("package")]
    [InlineData("signing")]
    [InlineData("verify")]
    [InlineData("quality")]
    [InlineData("scenario")]
    [InlineData("probe")]
    [InlineData("artifacts")]
    public async Task GroupWithoutAnActionShowsGeneratedHelp(string group)
    {
        using var output = new StringWriter();
        Assert.Equal(0, await Cli(output, TextWriter.Null).RunAsync([group], CancellationToken.None));
        Assert.Contains("Usage:", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("env", "status", "--release")]
    [InlineData("env", "up", "--apply")]
    [InlineData("env", "up", "--json")]
    [InlineData("env", "reset", "web")]
    [InlineData("env", "up", "unknown")]
    [InlineData("verify", "changed", "--base")]
    [InlineData("verify", "closeout")]
    [InlineData("verify", "closeout", "--base")]
    [InlineData("quality", "--json")]
    [InlineData("scenario", "replay-fixture", "--keep-environment-on-failure")]
    [InlineData("scenario", "delivery", "--include-sensitive-evidence")]
    [InlineData("probe", "window-title", "--duration-seconds", "NaN")]
    [InlineData("probe", "window-title", "--dwell-seconds", "1,0")]
    [InlineData("probe", "window-title", "--dwell-seconds", "Infinity")]
    [InlineData("probe", "window-title", "--since", "yesterday")]
    [InlineData("probe", "window-title", "--from-database")]
    [InlineData("artifacts", "prune", "--keep", "-1")]
    [InlineData("artifacts", "prune", "--keep", "many")]
    [InlineData("package", "desktop", "--runtime", "linux-x64")]
    [InlineData("package", "desktop", "--output")]
    [InlineData("probe", "window-title", "--since")]
    [InlineData("probe", "window-title", "--dwell-seconds")]
    [InlineData("probe", "window-title", "--duration-seconds", "1e100")]
    [InlineData("unknown")]
    [InlineData("env", "unknown")]
    [InlineData("package", "unknown")]
    [InlineData("probe", "window-title", "--duration-seconds", "many")]
    public async Task InvalidInputReturnsUsageExitWithoutStartingProcesses(params string[] args)
    {
        using var error = new StringWriter();
        Assert.Equal(2, await Cli(TextWriter.Null, error).RunAsync(args, CancellationToken.None));
        Assert.NotEmpty(error.ToString());
    }

    [Theory]
    [InlineData("scenario", "--list", "native-desktop")]
    [InlineData("quality", "--base", "HEAD", "loc")]
    public void ActionOptionsCannotBeSilentlyIgnoredByASubcommand(params string[] args)
    {
        var parsed = Cli(TextWriter.Null, TextWriter.Null).CreateCommand().Parse(args);
        Assert.NotEmpty(parsed.Errors);
    }

    [Fact]
    public void ParsesDwellListAndNativeScenarioFlagsThroughTheRegisteredCommandTree()
    {
        var root = Cli(TextWriter.Null, TextWriter.Null).CreateCommand();
        var parsed = root.Parse(["probe", "window-title", "--dwell-seconds", "1, 1.5,2"]);
        Assert.Empty(parsed.Errors);
        var dwell = (Option<double[]?>)parsed.CommandResult.Command.Options.Single(option => option.Name == "--dwell-seconds");
        Assert.Equal([1, 1.5, 2], Assert.IsType<double[]>(parsed.GetValue(dwell)));
        var native = root.Parse(["scenario", "collector-delivery", "--include-sensitive-evidence", "--keep-environment-on-failure"]);
        Assert.Empty(native.Errors);
        Assert.All(native.CommandResult.Command.Options.OfType<Option<bool>>(), option => Assert.True(native.GetValue(option)));
    }

    [Fact]
    public async Task FullPlanUsesTypedOptionsWithoutStartingProcesses()
    {
        using var output = new StringWriter();
        Assert.Equal(0, await Cli(output, TextWriter.Null).RunAsync(["verify", "full", "--plan", "--json"], CancellationToken.None));
        using var document = System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.Equal("full", document.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task CleanWorktreeStillRequiresAnExplicitVerificationBase()
    {
        using var error = new StringWriter();
        var cli = new DeveloperCli(new RepositoryContext(Path.GetTempPath()), new CleanRunner(), TextWriter.Null, error);
        Assert.Equal(2, await cli.RunAsync(["verify", "changed", "--plan"], CancellationToken.None));
        Assert.Contains("--base", error.ToString(), StringComparison.Ordinal);
    }

    private static DeveloperCli Cli(TextWriter output, TextWriter error) =>
        new(new RepositoryContext(Path.GetTempPath()), new NoProcesses(), output, error);

    private sealed class NoProcesses : IProcessRunner
    {
        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid input and help must not run a process.");
        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid input and help must not run a process.");
    }

    private sealed class CleanRunner : IProcessRunner
    {
        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            Assert.Equal("git", fileName);
            Assert.Equal(["status", "--porcelain"], arguments);
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
