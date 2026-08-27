using System.Diagnostics;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
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
