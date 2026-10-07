using System.Text.Json;
using Heartbeat.Dev;

namespace Heartbeat.Dev.Tests;

public sealed class EvidenceTests
{
    [Test]
    public async Task CancelledVerificationPreservesPartialLogAndResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "heartbeat-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new RepositoryContext(root);
            var runner = new CancelledRunner();
            var plan = new VerificationPlan("full", null, [], [new("build", "dotnet", ["build"])]);
            var code = await EvidenceSession.ExecuteAsync(repository, "verify", "full", [], async evidence =>
            {
                try { return await new VerificationCommand(repository, runner, TextWriter.Null).RunPlanAsync(plan, evidence, CancellationToken.None); }
                catch (OperationCanceledException) { return 130; }
            });
            await Assert.That(code).IsEqualTo(130);
            var run = new ArtifactStore(repository).List().Single();
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(run.Directory, "build.log"))).IsEqualTo("partial output");
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run.Directory, "verification.json")));
            await Assert.That(result.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(130);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run.Directory, "manifest.json")));
            await Assert.That(manifest.RootElement.GetProperty("status").GetString()).IsEqualTo("cancelled");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task CloseoutKeepsCancellationAsCancellation()
    {
        var result = await CloseoutCommand.RunChecksAsync(() => Task.FromResult(0),
            () => Task.FromException<int>(new OperationCanceledException()), CancellationToken.None);
        await Assert.That(result.ExitCode).IsEqualTo(130);
        await Assert.That(result.Verification).IsEqualTo(0);
    }

    [Test]
    public async Task PruningKeepsTheUnionAndPreviewPreservesFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "heartbeat-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new RepositoryContext(root);
            var store = new ArtifactStore(repository);
            for (var i = 0; i < 18; i++)
            {
                var run = store.Create("verify", $"run-{i}");
                var age = i < 2 ? TimeSpan.FromHours(i + 1)
                    : i < 12 ? TimeSpan.FromDays(3) + TimeSpan.FromHours(i)
                    : TimeSpan.FromDays(6) + TimeSpan.FromHours(i);
                Directory.SetCreationTimeUtc(run.Directory, DateTime.UtcNow - age);
                await File.WriteAllTextAsync(Path.Combine(run.Directory, "manifest.json"),
                    JsonSerializer.Serialize(new { exitCode = i >= 12 ? (i % 2 == 0 ? 1 : 130) : 0 }));
            }
            var selected = store.SelectForPruning(RetentionPolicy.Default);
            await Assert.That(selected.Candidates.Count).IsEqualTo(3);
            var cli = new DeveloperCli(repository, new CancelledRunner(), TextWriter.Null, TextWriter.Null);
            await Assert.That(await cli.RunAsync(["artifacts", "prune"], CancellationToken.None)).IsEqualTo(0);
            await Assert.That(store.List().Count).IsEqualTo(18);
            await Assert.That(await cli.RunAsync(["artifacts", "prune", "--apply"], CancellationToken.None)).IsEqualTo(0);
            await Assert.That(store.List().Count).IsEqualTo(15);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class CancelledRunner : IProcessRunner
    {
        public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) =>
            Task.FromException<ProcessResult>(new CapturedProcessCancelledException("partial output", "", cancellationToken));
        public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
