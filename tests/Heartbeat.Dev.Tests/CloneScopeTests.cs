using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class CloneScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"heartbeat-clone-scope-{Guid.NewGuid():N}");
    private static readonly string Sample = "export function calculate(value: number) {\n"
        + string.Join('\n', Enumerable.Range(1, 12).Select(index => $"  value = (value + {index}) * {index + 1};"))
        + "\n  return value;\n}\n";

    [Fact]
    public async Task FrontendTestsAreScannedWithTestsRatherThanProduction()
    {
        await InitializeAsync();
        Write("src/app.ts", "export const version = 1;\n");
        Write("src/frontend/first.test.ts", Sample);
        await CommitAsync();
        Write("src/frontend/tests/second.ts", Sample);

        var report = await ScanAsync();

        Assert.True(report.Available, report.Unavailable);
        Assert.Equal(0, report.Production.Clones);
        Assert.Equal(1, report.Tests.NewClones);
        Assert.All(report.Tests.NewFindings, finding => Assert.StartsWith("src/frontend/", finding.FirstFile));
    }

    [Fact]
    public async Task RenamingAnExistingClonePreservesItsBaselineAndLiteralPath()
    {
        await InitializeAsync();
        Write("src/first.ts", Sample);
        Write("src/old.ts", Sample);
        await CommitAsync();
        File.Delete(Path.Combine(_root, "src/old.ts"));
        Write("src/[new],{copy}.ts", Sample);

        var report = await ScanAsync();

        Assert.True(report.Available, report.Unavailable);
        Assert.Equal(1, report.Production.Clones);
        Assert.Equal(0, report.Production.NewClones);
        var finding = Assert.Single(report.Production.StockFindings);
        Assert.Contains("src/[new],{copy}.ts", new[] { finding.FirstFile, finding.SecondFile });
        Assert.Equal(0, report.Tests.Clones);
    }

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Write(".gitignore", "node_modules/\n.artifacts/\n");
        // The wrapper delegates to the repository's pinned installation, including on Windows.
        Write("tools/Heartbeat.Dev/jscpd/node_modules/.bin/" + (OperatingSystem.IsWindows() ? "jscpd.cmd" : "jscpd"), "");
        Directory.CreateDirectory(Path.Combine(_root, "tests"));
        await GitAsync("init");
    }

    private async Task<CloneQualityReport> ScanAsync()
    {
        var repository = await RepositoryContext.DiscoverAsync(Environment.CurrentDirectory);
        var runner = new ScannerRunner(_root, repository.Path("tools", "Heartbeat.Dev", "jscpd", "node_modules", "jscpd", "run-jscpd.js"));
        var source = new GitSourceReader(new RepositoryContext(_root), runner);
        var baseline = await source.ReadRevisionAsync("HEAD", CancellationToken.None);
        var current = await source.ReadWorktreeAsync(CancellationToken.None);
        return await new CloneDetector(new RepositoryContext(_root), runner)
            .CompareAsync("HEAD", baseline, current, Path.Combine(_root, ".artifacts"), [], CancellationToken.None);
    }

    private async Task CommitAsync()
    {
        await GitAsync("add", ".");
        await GitAsync("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "base");
    }

    private async Task GitAsync(params string[] arguments)
    {
        var result = await new ProcessRunner(_root).CaptureAsync("git", arguments, null, CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StdErr);
    }

    private void Write(string path, string content)
    {
        var absolute = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class ScannerRunner(string root, string script) : IProcessRunner
    {
        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
            fileName == "git"
                ? ProcessRunner.CaptureAsync(root, fileName, arguments, cancellationToken, environment)
                : ProcessRunner.CaptureAsync(root, "node", [script, .. arguments], cancellationToken, environment);

        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
