using System.Text.Json;
using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class ArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-artifacts-{Guid.NewGuid():N}");

    [Fact]
    public void PruningKeepsNewestRunsAndRequiresCallerToDelete()
    {
        var store = new ArtifactStore(new RepositoryContext(_root));
        for (var index = 0; index < 3; index++)
        {
            CreateRun(store, $"run-{index}", DateTime.UtcNow.AddDays(-30).AddMinutes(index), exitCode: 0);
        }

        var outcome = store.SelectForPruning(new RetentionPolicy(Keep: 1, KeepFailed: 0, TimeSpan.FromDays(14)));

        Assert.Equal(2, outcome.Candidates.Count);
        Assert.Equal(3, Directory.GetDirectories(store.Root).Length);
        foreach (var candidate in outcome.Candidates) store.Delete(candidate);
        Assert.Single(Directory.GetDirectories(store.Root));
    }

    /// 默认规则必须在「一天跑十几次」的真实节奏下真的删东西，否则证据目录会继续长到几个 G。
    [Fact]
    public void DefaultPolicyPrunesRunsFromABusyDay()
    {
        var store = new ArtifactStore(new RepositoryContext(_root));
        for (var index = 0; index < 15; index++)
        {
            CreateRun(store, $"run-{index:D2}", DateTime.UtcNow.AddDays(-3).AddMinutes(index), exitCode: 0);
        }

        var outcome = store.SelectForPruning(RetentionPolicy.Default);

        Assert.Equal(5, outcome.Candidates.Count);
        Assert.DoesNotContain("run-14", outcome.Candidates.Select(run => run.Id));
        Assert.Contains("run-00", outcome.Candidates.Select(run => run.Id));
    }

    [Fact]
    public void FailedRunsSurviveBeyondTheRecentWindow()
    {
        var store = new ArtifactStore(new RepositoryContext(_root));
        CreateRun(store, "old-failure", DateTime.UtcNow.AddDays(-30), exitCode: 1);
        for (var index = 0; index < 12; index++)
        {
            CreateRun(store, $"pass-{index:D2}", DateTime.UtcNow.AddDays(-10).AddMinutes(index), exitCode: 0);
        }

        var outcome = store.SelectForPruning(RetentionPolicy.Default);

        Assert.DoesNotContain("old-failure", outcome.Candidates.Select(run => run.Id));
        Assert.Equal(2, outcome.Candidates.Count);
    }

    /// 中途死掉的运行没有 manifest，恰恰最值得留下来看。
    [Fact]
    public void RunsWithoutManifestCountAsFailures()
    {
        var store = new ArtifactStore(new RepositoryContext(_root));
        Directory.CreateDirectory(store.Root);
        var path = Path.Combine(store.Root, "crashed");
        Directory.CreateDirectory(path);
        var run = Assert.Single(store.List());

        Assert.True(store.IsFailed(run));
        Assert.Equal("crashed", run.Id);
    }

    [Fact]
    public void PruneReportsReclaimedBytes()
    {
        var store = new ArtifactStore(new RepositoryContext(_root));
        var stale = CreateRun(store, "stale", DateTime.UtcNow.AddDays(-30), exitCode: 0);
        File.WriteAllText(Path.Combine(stale, "payload.log"), new string('x', 4096));
        CreateRun(store, "fresh", DateTime.UtcNow, exitCode: 0);

        var outcome = store.Prune(new RetentionPolicy(Keep: 1, KeepFailed: 0, TimeSpan.FromDays(2)));

        Assert.Single(outcome.Candidates);
        Assert.True(outcome.Bytes > 4096, $"expected reclaimed bytes to include the log, got {outcome.Bytes}");
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void DeleteRefusesPathsOutsideTheArtifactRoot()
    {
        var store = new ArtifactStore(new RepositoryContext(_root));
        Directory.CreateDirectory(store.Root);
        var escape = new ArtifactRun("escape", Path.Combine(_root, "elsewhere"), DateTimeOffset.UtcNow);
        Directory.CreateDirectory(escape.Directory);

        Assert.Throws<InvalidOperationException>(() => store.Delete(escape));
        Assert.True(Directory.Exists(escape.Directory));
    }

    private static string CreateRun(ArtifactStore store, string id, DateTime createdUtc, int exitCode)
    {
        Directory.CreateDirectory(store.Root);
        var path = Path.Combine(store.Root, id);
        Directory.CreateDirectory(path);
        File.WriteAllText(
            Path.Combine(path, "manifest.json"),
            JsonSerializer.Serialize(new { runId = id, exitCode }));
        Directory.SetCreationTimeUtc(path, createdUtc);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
