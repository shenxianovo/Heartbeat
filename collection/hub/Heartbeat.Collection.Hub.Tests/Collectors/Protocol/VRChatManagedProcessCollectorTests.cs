using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Delivery;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public sealed class VRChatManagedProcessCollectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-vrchat-managed-{Guid.NewGuid():N}");

    [Fact]
    public async Task MockApi_AuthorizesWithTwoFactorPublishesRawInstanceAndResumesFromCookie()
    {
        Directory.CreateDirectory(_directory);
        var packageDirectory = Path.Combine(_directory, "package");
        await CreatePackageAsync(packageDirectory);
        var package = LocalCollectorPackage.Load(packageDirectory);
        var sink = new RecordingSegmentSink();
        var secretDirectory = Path.Combine(_directory, "secrets");
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_directory, "collector-runtime.json"),
            sink,
            secretStore: new EncryptedFileCollectorSecretStore(secretDirectory));
        using var config = JsonDocument.Parse("{\"pollIntervalSeconds\":1}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var options = MockOptions();

        var activationTask = runtime.ActivateManagedProcessAsync(
            instance.CollectorInstanceId,
            package,
            options).AsTask();
        var credentials = await WaitForChallengeAsync(
            runtime,
            instance.CollectorInstanceId,
            CollectorAuthorizationChallengeKind.Credentials);
        await runtime.SubmitManagedProcessAuthorizationAsync(
            instance.CollectorInstanceId,
            credentials.InteractionId,
            new Dictionary<string, string>
            {
                ["username"] = "test-user",
                ["password"] = "test-password"
            });
        var verification = await WaitForChallengeAsync(
            runtime,
            instance.CollectorInstanceId,
            CollectorAuthorizationChallengeKind.VerificationCode);
        await runtime.SubmitManagedProcessAuthorizationAsync(
            instance.CollectorInstanceId,
            verification.InteractionId,
            new Dictionary<string, string> { ["code"] = "123456" });
        var activation = await activationTask.WaitAsync(TimeSpan.FromSeconds(10));
        var segment = await sink.WaitForAsync(
            item => item.Source == "vrchat.account" && item.Attributes?
                .GetProperty("instanceId").GetString() == "instance:mock");

        Assert.Equal("wrld_mock|instance:mock", segment.IdentityKey);
        Assert.Equal("Mock World", segment.Title);
        Assert.Equal("instance:mock", segment.Attributes!.Value.GetProperty("instanceId").GetString());
        await activation.StopAsync();

        var resumed = await runtime.ActivateManagedProcessAsync(
            instance.CollectorInstanceId,
            package,
            options).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CollectorRuntimePhase.Ready, resumed.RuntimeState.Phase);
        Assert.Null(resumed.RuntimeState.AuthorizationChallenge);
        Assert.DoesNotContain(
            "mock-auth",
            string.Join('\n', Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}package{Path.DirectorySeparatorChar}"))
                .Select(path =>
                {
                    try { return File.ReadAllText(path); }
                    catch { return string.Empty; }
                })),
            StringComparison.Ordinal);
        await resumed.StopAsync();
    }

    /// <summary>
    /// The VRChat Collector really switching Collector Packages: the owner approves an exact Collector
    /// Installation, the candidate starts, and reaching Ready is what makes it this Collector Instance's
    /// effective Package and Last-Known-Good.
    ///
    /// Reaching Ready is not a formality for this Collector. VRChat authorizes interactively, so the
    /// candidate can only become Ready by resuming from the Collector Instance's stored session — which
    /// is what proves the per-Instance secret survived the switch and that the owner is not asked for
    /// two-factor codes again every time a Package moves. The Fact Stream identity is unchanged, so the
    /// candidate took over the writer instead of opening a second one.
    /// </summary>
    [Fact]
    public async Task PackageSwitch_ApprovedInstallationReachesReady_TakesOverWithoutReauthorizing()
    {
        Directory.CreateDirectory(_directory);
        var hostPackageDirectory = Path.Combine(_directory, "package");
        await CreatePackageAsync(hostPackageDirectory);
        var hostPackage = LocalCollectorPackage.Load(hostPackageDirectory);
        var installations = new CollectorInstallationStore(_directory);
        var candidate = InstallNextVersion(installations, hostPackageDirectory);
        var sink = new RecordingSegmentSink();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_directory, "collector-runtime.json"),
            sink,
            secretStore: new EncryptedFileCollectorSecretStore(Path.Combine(_directory, "secrets")));
        using var config = JsonDocument.Parse("{\"pollIntervalSeconds\":1}");
        var instance = runtime.CreateInstance(
            hostPackage,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var packageSwitch = new CollectorPackageSwitch(runtime, installations);
        var current = await AuthorizeAsync(runtime, instance.CollectorInstanceId, hostPackage);
        var streams = current.Streams.ToDictionary(pair => pair.Key, pair => pair.Value.StreamId);
        runtime.ApprovePackageCandidate(instance.CollectorInstanceId, candidate);

        var result = await packageSwitch.SwitchToApprovedAsync(
            instance.CollectorInstanceId,
            new ManagedProcessUpdateOptions
            {
                CandidateActivation = MockOptions(),
                RollbackActivation = MockOptions()
            });

        Assert.Equal(CollectorPackageSwitchOutcome.Switched, result.Outcome);
        Assert.Null(result.Status.LastFailure);
        Assert.Equal(candidate.Version, result.Status.CurrentVersion);
        Assert.Equal(candidate.Version, result.Status.LastKnownGood?.PackageVersion);
        var activation = Assert.IsType<ManagedProcessCollectorActivation>(result.Activation);
        Assert.Equal(CollectorRuntimePhase.Ready, activation.RuntimeState.Phase);
        // Ready without a challenge: the candidate resumed the stored VRChat session rather than asking
        // the owner to authorize the new Collector Package all over again.
        Assert.Null(activation.RuntimeState.AuthorizationChallenge);
        Assert.Equal(
            streams,
            activation.Streams.ToDictionary(pair => pair.Key, pair => pair.Value.StreamId));
        Assert.Equal(
            Path.GetFullPath(installations.DirectoryFor(candidate)),
            Path.GetFullPath(activation.Package.PackageDirectory));
        // The candidate is the one collecting now, so a Fact published after the switch comes from it.
        var segment = await sink.WaitForAsync(item => item.Source == "vrchat.account");
        Assert.Equal("wrld_mock|instance:mock", segment.IdentityKey);
        await activation.StopAsync();
    }

    /// <summary>
    /// Publishes the built VRChat Collector Package as a Collector Installation of the next patch Version,
    /// the way an installed candidate really sits on disk: content first, completion marker last.
    /// </summary>
    private static CollectorPackageReference InstallNextVersion(
        CollectorInstallationStore installations,
        string sourcePackageDirectory)
    {
        var manifestPath = Path.Combine(sourcePackageDirectory, "collector-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var version = NextPatch((string)manifest["version"]!);
        var executable = OperatingSystem.IsWindows()
            ? "Heartbeat.Collector.VRChat.exe"
            : "Heartbeat.Collector.VRChat";
        var reference = new CollectorPackageReference(
            (string)manifest["packageId"]!,
            version,
            Convert.ToHexStringLower(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(sourcePackageDirectory, executable)))));
        var directory = installations.DirectoryFor(reference);
        Directory.CreateDirectory(directory);
        foreach (var child in Directory.EnumerateDirectories(sourcePackageDirectory, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(directory, Path.GetRelativePath(sourcePackageDirectory, child)));
        foreach (var file in Directory.EnumerateFiles(sourcePackageDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(directory, Path.GetRelativePath(sourcePackageDirectory, file));
            File.Copy(file, target, overwrite: true);
        }
        manifest["version"] = version;
        File.WriteAllText(
            Path.Combine(directory, "collector-manifest.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                Path.Combine(directory, executable),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        File.WriteAllBytes(
            Path.Combine(directory, CollectorInstallationMarker.FileName),
            CollectorInstallationMarker.Write(new CollectorInstallationMarker(
                CollectorInstallationMarker.CurrentSchemaVersion,
                reference.PackageId,
                reference.Version,
                reference.ArtifactSha256,
                LocalCollectorPackage.Load(directory).PackageContentHash)));
        var opened = installations.OpenInstallation(reference);
        Assert.True(opened.IsSuccess, opened.Detail);
        return reference;
    }

    private static string NextPatch(string version)
    {
        var parts = version.Split('.');
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{parts[0]}.{parts[1]}.{int.Parse(parts[2], CultureInfo.InvariantCulture) + 1}");
    }

    /// <summary>
    /// Brings one VRChat Collector Instance up through the interactive authorization it really requires,
    /// so what follows starts from a Collector Instance that holds a stored session.
    /// </summary>
    private static async Task<ManagedProcessCollectorActivation> AuthorizeAsync(
        CollectorRuntime runtime,
        Guid collectorInstanceId,
        LocalCollectorPackage package)
    {
        var activationTask = runtime.ActivateManagedProcessAsync(
            collectorInstanceId,
            package,
            MockOptions()).AsTask();
        var credentials = await WaitForChallengeAsync(
            runtime,
            collectorInstanceId,
            CollectorAuthorizationChallengeKind.Credentials);
        await runtime.SubmitManagedProcessAuthorizationAsync(
            collectorInstanceId,
            credentials.InteractionId,
            new Dictionary<string, string>
            {
                ["username"] = "test-user",
                ["password"] = "test-password"
            });
        var verification = await WaitForChallengeAsync(
            runtime,
            collectorInstanceId,
            CollectorAuthorizationChallengeKind.VerificationCode);
        await runtime.SubmitManagedProcessAuthorizationAsync(
            collectorInstanceId,
            verification.InteractionId,
            new Dictionary<string, string> { ["code"] = "123456" });
        return await activationTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static ManagedProcessActivationOptions MockOptions() => new()
    {
        StartupTimeout = TimeSpan.FromSeconds(10),
        DrainGracePeriod = TimeSpan.FromSeconds(5),
        EnvironmentVariables = new Dictionary<string, string>
        {
            ["HEARTBEAT_VRCHAT_MOCK"] = "1",
            ["HEARTBEAT_VRCHAT_MOCK_TRANSIENT_POLLS"] = "1"
        }
    };

    private static async Task<CollectorAuthorizationChallenge> WaitForChallengeAsync(
        CollectorRuntime runtime,
        Guid instanceId,
        CollectorAuthorizationChallengeKind kind)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var state = runtime.GetManagedProcessRuntimeState(instanceId);
            if (state.AuthorizationChallenge is { Kind: var current } challenge && current == kind)
                return challenge;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task CreatePackageAsync(string packageDirectory)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ManagedVRChatCollector");
        var executable = Path.Combine(
            source,
            OperatingSystem.IsWindows() ? "Heartbeat.Collector.VRChat.exe" : "Heartbeat.Collector.VRChat");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            ArgumentList = { "--create-package", packageDirectory }
        }) ?? throw new InvalidOperationException("Failed to start VRChat Package builder.");
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class RecordingSegmentSink : ISegmentSink, IDurableSegmentProjectionSink
    {
        private readonly object _gate = new();
        private readonly List<ActivitySegmentItem> _items = [];

        public void Push(List<ActivitySegmentItem> snapshots)
        {
            lock (_gate)
                _items.AddRange(snapshots);
        }

        public void UpsertDurable(ActivitySegmentItem snapshot, long revision) => Push([snapshot]);
        public void ReplayDurable(ActivitySegmentItem snapshot, long revision) => Push([snapshot]);
        public void RetractDurable(Guid segmentId, long revision) { }

        public async Task<ActivitySegmentItem> WaitForAsync(Func<ActivitySegmentItem, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                lock (_gate)
                {
                    if (_items.LastOrDefault(predicate) is { } item)
                        return item;
                }
                await Task.Delay(20, timeout.Token);
            }
        }
    }
}
