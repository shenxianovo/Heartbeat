using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Tests.Collectors;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public sealed class ManagedProcessDataRequirementsTests : IDisposable
{
    private const string Requirements = "{\"SchemaVersion\":1,\"RequiredCapabilities\":{\"facts.observation\":2}}";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-data-requirements-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(Requirements)]
    [InlineData(null)]
    [InlineData("{\"SchemaVersion\":2,\"RequiredCapabilities\":{\"facts.observation\":2}}")]
    [InlineData("{broken")]
    [InlineData("{\"SchemaVersion\":1,\"RequiredCapabilities\":{\"facts.observation\":0}}")]
    public async Task RestartRefusesOldPackageWithOnlyPrivateStateAndKeepsEvidence(string? requirements)
    {
        using var copy = ManagedReferenceCollectorPackage.Create();
        var package = LocalCollectorPackage.Load(copy.Path);
        var path = Path.Combine(_root, "runtime.json");
        Guid id;
        using (var runtime = CollectorRuntime.Open(path, new UnusedSink()))
            id = CreateInstance(runtime, package);
        var directory = Path.Combine(_root, "collector-data", id.ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "collector-data-requirements.json");
        if (requirements is null) Directory.CreateDirectory(marker);
        else File.WriteAllText(marker, requirements);
        File.WriteAllText(Path.Combine(directory, "private-state.json"), "{\"SchemaVersion\":4}");
        using var restarted = CollectorRuntime.Open(path, new UnusedSink());
        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await restarted.ActivateManagedProcessAsync(id, package, Options()));
        Assert.Equal("collector_cache_incompatible", error.Error.Code);
        if (requirements is null) Assert.True(Directory.Exists(marker));
        else Assert.Equal(requirements, File.ReadAllText(marker));
        Assert.Equal(2, Directory.GetFileSystemEntries(directory).Length);
        Assert.Empty(restarted.ReadPendingFacts());
    }

    [Fact]
    public async Task FailedCandidateCannotRollbackToLastKnownGoodThatCannotReadPrivateState()
    {
        using var oldCopy = ManagedReferenceCollectorPackage.Create();
        using var candidateCopy = ManagedReferenceCollectorPackage.Create("1.1.0");
        var manifestPath = Path.Combine(candidateCopy.Path, "collector-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["supportedCapabilities"]!["facts.observation"] = new JsonArray(1, 2);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var old = LocalCollectorPackage.Load(oldCopy.Path);
        var candidate = LocalCollectorPackage.Load(candidateCopy.Path);
        var path = Path.Combine(_root, "runtime.json");
        Guid id;
        string directory;
        using (var runtime = CollectorRuntime.Open(path, new UnusedSink()))
        {
            id = CreateInstance(runtime, old);
            await using var active = await runtime.ActivateManagedProcessAsync(id, old, Options());
            directory = Path.Combine(_root, "collector-data", id.ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "collector-data-requirements.json"), Requirements);
            File.WriteAllText(Path.Combine(directory, "private-state.json"), "{\"SchemaVersion\":4}");
            var error = await Assert.ThrowsAsync<ManagedProcessUpdateException>(async () =>
                await runtime.UpdateManagedProcessAsync(id, candidate, new ManagedProcessUpdateOptions
                {
                    StabilityPeriod = TimeSpan.FromMilliseconds(100),
                    CandidateActivation = Options("exit_before_hello"),
                    RollbackActivation = Options()
                }));
            Assert.Equal("process_exited", error.CandidateFailure.Code);
            Assert.Equal("collector_cache_incompatible", error.RollbackFailure.Code);
            Assert.Equal(old.PackageContentHash, runtime.GetInstance(id).LastKnownGoodPackage!.PackageContentHash);
        }
        Assert.Equal(Requirements, File.ReadAllText(Path.Combine(directory, "collector-data-requirements.json")));
        using var restarted = CollectorRuntime.Open(path, new UnusedSink());
        var rejected = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await restarted.ActivateManagedProcessAsync(id, old, Options()));
        Assert.Equal("collector_cache_incompatible", rejected.Error.Code);
        // Restoring a compatible package opens the same directory through the normal startup path.
        await using var recovered = await restarted.ActivateManagedProcessAsync(id, candidate, Options());
        Assert.Equal(CollectorRuntimePhase.Ready, recovered.RuntimeState.Phase);
        Assert.Equal(Requirements, File.ReadAllText(Path.Combine(directory, "collector-data-requirements.json")));
    }

    private static Guid CreateInstance(CollectorRuntime runtime, LocalCollectorPackage package) =>
        runtime.CreateInstance(package, new SubjectReference(Guid.NewGuid(), SubjectKind.Account),
            new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { }))).CollectorInstanceId;

    private static ManagedProcessActivationOptions Options(string? behavior = null) => new()
    {
        StartupTimeout = TimeSpan.FromSeconds(10),
        DrainGracePeriod = TimeSpan.FromSeconds(2),
        EnvironmentVariables = behavior is null ? new Dictionary<string, string>() :
            new Dictionary<string, string> { ["HEARTBEAT_REFERENCE_BEHAVIOR"] = behavior }
    };

    private sealed class UnusedSink : ISegmentSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
