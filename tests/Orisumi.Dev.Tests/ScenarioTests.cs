using System.Text.Json;
using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class ScenarioTests
{
    [Test]
    public async Task UnsatisfiedPrerequisitesSkipExecutionAndPersistReason()
    {
        using var fixture = new EnvironmentFixture();
        var scenario = new FakeScenario("skip", false, false);
        var code = await new ScenarioCommand(fixture.Repository, fixture.Runner, TextWriter.Null, [scenario])
            .RunAsync([], false, false, CancellationToken.None);
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(scenario.Executed).IsFalse();
        var run = new ArtifactStore(fixture.Repository).List().Single();
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Directory, "verification.json")));
        var result = json.RootElement.GetProperty("results")[0];
        await Assert.That(result.GetProperty("status").GetString()).IsEqualTo("skipped");
        await Assert.That(result.GetProperty("reason").GetString()).IsEqualTo("prerequisite unavailable");
        await Assert.That(File.Exists(Path.Combine(run.Directory, "scenario-skip.log"))).IsTrue();
    }

    [Test]
    public async Task ResultsAndRequestResponseLogsSurviveFailure()
    {
        using var fixture = new EnvironmentFixture();
        var scenarios = new ISmokeScenario[] { new FakeScenario("pass", true, false), new FakeScenario("fail", true, true) };
        var code = await new ScenarioCommand(fixture.Repository, fixture.Runner, TextWriter.Null, scenarios)
            .RunAsync([], false, false, CancellationToken.None);
        await Assert.That(code).IsEqualTo(1);
        var run = new ArtifactStore(fixture.Repository).List().Single();
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Directory, "verification.json")));
        await Assert.That(json.RootElement.GetProperty("results")[0].GetProperty("status").GetString()).IsEqualTo("passed");
        await Assert.That(json.RootElement.GetProperty("results")[1].GetProperty("status").GetString()).IsEqualTo("failed");
        await Assert.That(File.ReadAllText(Path.Combine(run.Directory, "scenario-fail.log")).Contains("< 500", StringComparison.Ordinal)).IsTrue();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Directory, "manifest.json")));
        await Assert.That(manifest.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task PlanReportsPrerequisitesWithoutExecutingOrCreatingEvidence()
    {
        using var fixture = new EnvironmentFixture();
        var scenario = new FakeScenario("pass", true, false);
        var code = await new ScenarioCommand(fixture.Repository, fixture.Runner, fixture.Output, [scenario])
            .RunAsync([], true, true, CancellationToken.None);
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(scenario.Executed).IsFalse();
        await Assert.That(new ArtifactStore(fixture.Repository).List().Count).IsEqualTo(0);
        using var json = JsonDocument.Parse(fixture.Output.ToString());
        await Assert.That(json.RootElement[0].GetProperty("prerequisite").GetProperty("satisfied").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task BuiltInPrerequisitesInspectTheCurrentCheckout()
    {
        using var fixture = new EnvironmentFixture();
        var api = await new ApiScenario(fixture.Manager).CheckAsync(CancellationToken.None);
        await Assert.That(api.Satisfied).IsFalse();
        var environment = new EnvironmentScenario(fixture.Repository, fixture.Runner);
        await Assert.That((await environment.CheckAsync(CancellationToken.None)).Satisfied).IsTrue();
        fixture.Runner.Running = true;
        await Assert.That((await environment.CheckAsync(CancellationToken.None)).Satisfied).IsFalse();
    }

    private sealed class FakeScenario(string name, bool ready, bool fail) : ISmokeScenario
    {
        public string Name => name;
        public bool Executed { get; private set; }
        public Task<ScenarioPrerequisite> CheckAsync(CancellationToken token) => Task.FromResult(new ScenarioPrerequisite(ready,
            ready ? "ready" : "prerequisite unavailable"));
        public async Task ExecuteAsync(TextWriter log, CancellationToken token)
        {
            Executed = true;
            await log.WriteLineAsync("> GET http://localhost/api/entities\n< " + (fail ? "500" : "200"));
            if (fail) throw new InvalidOperationException("request failed");
        }
    }
}
