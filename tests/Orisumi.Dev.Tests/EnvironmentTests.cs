using System.Text.Json;
using Orisumi.Dev;

namespace Orisumi.Dev.Tests;

public sealed class EnvironmentTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CheckoutIdentityControlsIsolationAndVolume(bool worktree)
    {
        using var fixture = new EnvironmentFixture();
        fixture.Runner.Worktree = worktree;
        await fixture.Manager.UpAsync(["api"], false, CancellationToken.None);
        var start = fixture.Runner.Calls.Single(call => call.File == "aspire" && call.Args[0] == "start");
        await Assert.That(start.Args.Contains("--isolated")).IsEqualTo(worktree);
        await Assert.That(start.Args.Contains("--AllowedHosts=localhost;127.0.0.1")).IsEqualTo(worktree);
        var volume = fixture.Runner.Calls.Single(call => call.File == "docker" && call.Args[1] == "create");
        await Assert.That(volume.Args.Contains($"orisumi.checkout={fixture.Repository.Root}")).IsTrue();
        await Assert.That(volume.Args.Contains("orisumi.temporary=false")).IsTrue();
        await Assert.That(volume.Args.Last()).IsEqualTo(worktree
            ? EnvironmentFiles.WorktreeVolume(fixture.Repository.Root) : "orisumi-aspire-postgres-data");
    }

    [Test]
    public async Task WorktreePortsAreReusedAndOnlyOccupiedPortsAreReplaced()
    {
        var previous = new EnvironmentPorts(1001, 1002, 1003, 1004);
        var allocated = 9000;
        var ports = EnvironmentPorts.Allocate(previous, port => port != 1002, () => ++allocated);
        await Assert.That(ports).IsEqualTo(new EnvironmentPorts(1001, 9001, 1003, 1004));
        var reused = EnvironmentPorts.Allocate(ports, _ => true, () => throw new InvalidOperationException());
        await Assert.That(reused).IsEqualTo(ports);
    }

    [Test]
    public async Task SavedWorktreePortsSurviveRestartAndDefaultVolumeIsRetained()
    {
        using var fixture = new EnvironmentFixture();
        fixture.Runner.Worktree = true;
        await fixture.Manager.UpAsync(["api"], false, CancellationToken.None);
        var first = new EnvironmentFiles(fixture.Repository).Read()!;
        await fixture.Manager.DownAsync(CancellationToken.None);
        await fixture.Manager.UpAsync(["api"], false, CancellationToken.None);
        var second = new EnvironmentFiles(fixture.Repository).Read()!;
        await Assert.That(second.Ports).IsEqualTo(first.Ports);
        await Assert.That(second.Volume).IsEqualTo(first.Volume);
        await Assert.That(fixture.Runner.Calls.Any(call => call.File == "docker" && call.Args[1] == "rm")).IsFalse();
    }

    [Test]
    [Arguments("web", "api,db,migrate,nginx,web")]
    [Arguments("api", "api,db,migrate,nginx")]
    [Arguments("docs", "docs,nginx")]
    public async Task SelectionCompletesDependencies(string service, string expected) =>
        await Assert.That(string.Join(',', ServiceSelection.Resolve([service]).Order())).IsEqualTo(expected);

    [Test]
    public async Task AlreadyRunningUpReportsStateAndDoesNotCreateAnything()
    {
        using var fixture = new EnvironmentFixture();
        fixture.Runner.Running = true;
        await Assert.That(async () => await fixture.Manager.UpAsync(["docs"], true, CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(fixture.Runner.Calls.Any(call => call.File == "docker" || call.Args[0] == "start")).IsFalse();
        await Assert.That(fixture.Output.ToString().Contains("https://localhost:19999", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task StatusMatchesOnlyThisApphostAndOmitsStoppedFields()
    {
        using var fixture = new EnvironmentFixture();
        var status = await fixture.Manager.StatusAsync(CancellationToken.None);
        await Assert.That(JsonSerializer.Serialize(status, JsonOptions.Indented))
            .IsEqualTo(JsonSerializer.Serialize(new { checkout = fixture.Repository.Root, running = false }, JsonOptions.Indented));
        fixture.Runner.Running = true;
        status = await fixture.Manager.StatusAsync(CancellationToken.None);
        await Assert.That(status.Dashboard).IsEqualTo("https://localhost:19999");
        await Assert.That(status.Entries!.Api).IsEqualTo("http://localhost:12345/api");
        await Assert.That(status.Services!.Single().Health).IsEqualTo("Healthy");
        await Assert.That(fixture.Runner.Calls.Last().Args.Contains(fixture.Manager.AppHost)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResetRecreatesThisCheckoutsDefaultVolume(bool worktree)
    {
        using var fixture = new EnvironmentFixture();
        fixture.Runner.Worktree = worktree;
        var expected = worktree ? EnvironmentFiles.WorktreeVolume(fixture.Repository.Root) : "orisumi-aspire-postgres-data";
        await fixture.Manager.UpAsync(["api"], false, CancellationToken.None, reset: true);
        var calls = fixture.Runner.Calls.Where(call => call.File == "docker").ToArray();
        await Assert.That(calls[0].Args).IsEquivalentTo(new[] { "volume", "rm", "--force", expected });
        await Assert.That(calls[1].Args[1]).IsEqualTo("create");
        await Assert.That(calls[1].Args.Last()).IsEqualTo(expected);
    }

    [Test]
    public async Task ResetAndTempAreMutuallyExclusive()
    {
        using var fixture = new EnvironmentFixture();
        var code = await new DeveloperCli(fixture.Repository, fixture.Runner, TextWriter.Null, TextWriter.Null)
            .RunAsync(["env", "up", "--reset", "--temp"], CancellationToken.None);
        await Assert.That(code).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StatusUsesTextUnlessJsonIsRequested(bool running)
    {
        using var fixture = new EnvironmentFixture();
        fixture.Runner.Running = running;
        var cli = new DeveloperCli(fixture.Repository, fixture.Runner, fixture.Output, TextWriter.Null);
        await cli.RunAsync(["env", "status"], CancellationToken.None);
        await Assert.That(fixture.Output.ToString().Contains(running ? "Running" : "Stopped", StringComparison.Ordinal)).IsTrue();
        fixture.Output.GetStringBuilder().Clear();
        await cli.RunAsync(["env", "status", "--json"], CancellationToken.None);
        using var json = JsonDocument.Parse(fixture.Output.ToString());
        await Assert.That(json.RootElement.GetProperty("running").GetBoolean()).IsEqualTo(running);
    }

    [Test]
    public async Task VolumePruningOnlyDeletesMissingCheckoutsAfterApply()
    {
        using var fixture = new EnvironmentFixture();
        fixture.Runner.VolumeInventory = true;
        var volumes = new EnvironmentVolumes(fixture.Manager);
        var preview = await volumes.PruneAsync(false, CancellationToken.None);
        await Assert.That(preview.Single().Name).IsEqualTo("orphan");
        await Assert.That(fixture.Runner.Calls.Any(call => call.File == "docker" && call.Args[1] == "rm")).IsFalse();
        await volumes.PruneAsync(true, CancellationToken.None);
        await Assert.That(fixture.Runner.Calls.Single(call => call.File == "docker" && call.Args[1] == "rm").Args.Last()).IsEqualTo("orphan");
    }

    [Test]
    public async Task TemporaryVolumeIsLabeledAndDeletedOnDown()
    {
        using var fixture = new EnvironmentFixture();
        await fixture.Manager.UpAsync(["docs"], true, CancellationToken.None);
        var state = new EnvironmentFiles(fixture.Repository).Read()!;
        await Assert.That(state.Volume.StartsWith("orisumi-temp-", StringComparison.Ordinal)).IsTrue();
        await fixture.Manager.DownAsync(CancellationToken.None);
        await Assert.That(fixture.Runner.Calls.Any(call => call.File == "docker" && call.Args.SequenceEqual(new[] { "volume", "rm", "--force", state.Volume }))).IsTrue();
    }
}

internal sealed class EnvironmentFixture : IDisposable
{
    public RepositoryContext Repository { get; } = new(Path.Combine(Path.GetTempPath(), "orisumi-env-tests-" + Guid.NewGuid().ToString("N")));
    public EnvironmentFakeRunner Runner { get; }
    public StringWriter Output { get; } = new();
    public EnvironmentManager Manager { get; }
    public EnvironmentFixture()
    {
        Directory.CreateDirectory(Repository.Path("tools", "Orisumi.AppHost"));
        File.WriteAllText(Repository.Path("tools", "Orisumi.AppHost", "appsettings.json"), """
            {"Ports":{"Api":8080,"Docs":3000,"Web":8081,"Database":54329},"DatabaseVolume":"orisumi-aspire-postgres-data"}
            """);
        Runner = new(Repository);
        Manager = new(Repository, Runner, Output);
    }
    public void Dispose() { Output.Dispose(); Directory.Delete(Repository.Root, true); }
}

internal sealed class EnvironmentFakeRunner(RepositoryContext repository) : IProcessRunner
{
    public bool Worktree { get; set; }
    public bool Running { get; set; }
    public bool VolumeInventory { get; set; }
    public List<(string File, string[] Args)> Calls { get; } = [];
    public Task<ProcessResult> CaptureAsync(string fileName, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
    {
        Calls.Add((fileName, arguments.ToArray()));
        var stdout = "";
        if (fileName == "git") stdout = $"{repository.Root}/.git/{(Worktree ? "worktrees/test" : "")}\n{repository.Root}/.git/\n";
        if (fileName == "aspire" && arguments[0] == "ps") stdout = JsonSerializer.Serialize(new[] {
            new { appHostPath = repository.Path("tools", "Orisumi.AppHost", "Orisumi.AppHost.csproj"), status = Running ? "running" : "stopped", dashboardUrl = "https://localhost:19999" },
            new { appHostPath = "/other/tools/Orisumi.AppHost/Orisumi.AppHost.csproj", status = "running", dashboardUrl = "https://localhost:29999" }
        });
        if (fileName == "aspire" && arguments[0] == "describe") stdout = """
            Scanning for running AppHosts...
            {"resources":[{"name":"nginx-123","displayName":"nginx","resourceType":"Container","state":"Running","healthStatus":"Healthy","urls":[{"name":"api","url":"http://localhost:12345"}]}]}
            """;
        if (fileName == "aspire" && arguments[0] == "start") Running = true;
        if (fileName == "aspire" && arguments[0] == "stop") Running = false;
        if (fileName == "docker" && VolumeInventory)
        {
            if (arguments[1] == "ls") stdout = "active\norphan\n";
            if (arguments[1] == "inspect") stdout = JsonSerializer.Serialize(new[] { new { Labels = new Dictionary<string, string> {
                [EnvironmentManager.CheckoutLabel] = arguments[2] == "active" ? repository.Root : repository.Path("missing"),
                [EnvironmentManager.TemporaryLabel] = "false"
            } } });
        }
        return Task.FromResult(new ProcessResult(0, stdout, ""));
    }
}
