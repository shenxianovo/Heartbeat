using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class GitRenamePlanningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-rename-{Guid.NewGuid():N}");

    [Fact]
    public async Task MovingProductionSourceToDocsStillChecksTheSourceProject()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        var runner = new ProcessRunner(_root);
        await GitAsync(runner, "init");
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "class A {}\n");
        await GitAsync(runner, "add", ".");
        await GitAsync(runner, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "base");
        await GitAsync(runner, "mv", "src/A.cs", "docs/A.cs");

        var plan = await VerificationPlanner.CreateAsync(new RepositoryContext(_root), runner,
            new VerificationRequest("changed", "HEAD", true, false), CancellationToken.None);

        Assert.Contains(plan.Steps, step => step.Name == "dotnet");
        Assert.Contains("src/A.cs", plan.ChangedPaths);
    }

    private static async Task GitAsync(ProcessRunner runner, params string[] arguments)
    {
        var result = await runner.CaptureAsync("git", arguments, null, CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StdErr);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
