using System.Security.Cryptography;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Delivery;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Tests.Collectors;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Delivery;

/// <summary>
/// Taking an Approved Collector Package Candidate into use, with real ManagedProcess Collector child
/// processes and real Collector Installations on disk.
///
/// Every case here is a statement of the one rule ADR-047 gives this seam: a candidate becomes the
/// Collector Instance's effective Collector Package by reaching Ready, and by nothing else. So a
/// candidate that reaches Ready is the update — the Last-Known-Good moves with it — while every failure
/// before Ready leaves the previous Package running, leaves the Last-Known-Good exactly where it was,
/// and records one structured reason for the owner. The mirror-image case is a host restart: the only
/// candidate that may start is the one that already reached Ready.
///
/// The failure paths are provoked through the reference Collector's own behaviours rather than mocks,
/// so "never Ready", "exited before hello" and "incompatible handshake" are really produced by a child
/// process, and the phase barrier — not a sleep — is what makes the interleavings deterministic.
/// </summary>
public sealed class CollectorPackageSwitchTests : IDisposable
{
    /// <summary>
    /// How often the concurrent-attempt case is repeated. A writer-lease invariant that only holds
    /// most of the time is not an invariant, and a single pass of an interleaving proves very little.
    /// </summary>
    private const int ConcurrentAttemptRepeats = 20;

    private const string OtherArtifactSha256 =
        "1111111111111111111111111111111111111111111111111111111111111111";

    private static readonly TimeSpan NonTimeoutStartupBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PhaseSignalHangGuard = TimeSpan.FromSeconds(30);

    private readonly SwitchFixture _fixture = SwitchFixture.Create();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task SwitchToApproved_CandidateReachesReady_BecomesTheEffectivePackageAndLastKnownGood()
    {
        var current = await _fixture.StartAsync();
        var reference = _fixture.Install("1.1.0");
        _fixture.Runtime.ApprovePackageCandidate(_fixture.InstanceId, reference);

        var result = await _fixture.Switch.SwitchToApprovedAsync(_fixture.InstanceId, FastUpdateOptions());

        Assert.Equal(CollectorPackageSwitchOutcome.Switched, result.Outcome);
        Assert.Null(result.Status.LastFailure);
        Assert.Equal("1.1.0", result.Status.CurrentVersion);
        Assert.Equal("1.1.0", result.Status.LastKnownGood?.PackageVersion);
        Assert.Equal(reference, result.Status.ApprovedCandidate);
        var activation = Assert.IsType<ManagedProcessCollectorActivation>(result.Activation);
        Assert.Equal(CollectorActivationState.Ready, activation.State);
        // The candidate really runs out of the content-isolated Installation directory, not out of the
        // Package the host itself delivered.
        Assert.Equal(
            Path.GetFullPath(_fixture.Installations.DirectoryFor(reference)),
            Path.GetFullPath(activation.Package.PackageDirectory));
        // One Fact Stream writer identity across the switch: the candidate took over the same Stream.
        Assert.Equal(current.Streams["activity"].StreamId, activation.Streams["activity"].StreamId);
        Assert.NotEqual(current.ActivationId, activation.ActivationId);
        await activation.StopAsync();
    }

    [Fact]
    public async Task SwitchToApproved_AlreadyEffectiveCandidate_IsAlreadyCurrentAndRestartsNothing()
    {
        await _fixture.StartAsync();
        var reference = _fixture.Install("1.1.0");
        _fixture.Runtime.ApprovePackageCandidate(_fixture.InstanceId, reference);
        var first = await _fixture.Switch.SwitchToApprovedAsync(_fixture.InstanceId, FastUpdateOptions());
        Assert.Equal(CollectorPackageSwitchOutcome.Switched, first.Outcome);

        var second = await _fixture.Switch.SwitchToApprovedAsync(_fixture.InstanceId, FastUpdateOptions());

        Assert.Equal(CollectorPackageSwitchOutcome.AlreadyCurrent, second.Outcome);
        Assert.Null(second.Activation);
        Assert.Null(second.Status.LastFailure);
        // Asking twice is not a reason to restart a Collector that is already collecting.
        Assert.Equal(CollectorActivationState.Ready, first.Activation!.State);
        Assert.False(first.Activation.Completion.IsCompleted);
        await first.Activation.StopAsync();
    }

    [Fact]
    public async Task SwitchToApproved_WithoutAnApprovedCandidate_ChangesNothing()
    {
        var current = await _fixture.StartAsync();

        var result = await _fixture.Switch.SwitchToApprovedAsync(_fixture.InstanceId, FastUpdateOptions());

        Assert.Equal(CollectorPackageSwitchOutcome.NoApprovedCandidate, result.Outcome);
        Assert.Null(result.Activation);
        Assert.Null(result.Status.LastFailure);
        Assert.Equal("1.0.0", result.Status.CurrentVersion);
        Assert.Equal(CollectorActivationState.Ready, current.State);
        await current.StopAsync();
    }

    [Fact]
    public async Task SwitchToApproved_CandidateNeverReachesReady_KeepsTheEffectivePackageAndRecordsReadyTimeout()
    {
        var current = await _fixture.StartAsync();
        var reference = _fixture.Install("1.1.0");
        _fixture.Runtime.ApprovePackageCandidate(_fixture.InstanceId, reference);

        var result = await _fixture.Switch.SwitchToApprovedAsync(_fixture.InstanceId, NeverReadyUpdateOptions());

        Assert.Equal(CollectorPackageSwitchOutcome.RolledBack, result.Outcome);
        var failure = Assert.IsType<CollectorPackageUpdateFailure>(result.Status.LastFailure);
        Assert.Equal(CollectorRegistryFailureReason.ReadyTimeout, failure.Reason);
        // The Runtime's own code survives the translation; the reason is a projection, not a rename.
        Assert.Contains("activation_start_timeout", failure.Message, StringComparison.Ordinal);
        Assert.Equal(SwitchFixture.FailureTime, failure.OccurredAt);
        Assert.Equal("1.0.0", result.Status.CurrentVersion);
        // Nothing durable moved: no Last-Known-Good was invented, and the owner's approval stands so the
        // next attempt is still theirs to make.
        Assert.Null(result.Status.LastKnownGood);
        Assert.Equal(reference, result.Status.ApprovedCandidate);
        Assert.True(_fixture.Installations.OpenInstallation(reference).IsSuccess);
        var restored = Assert.IsType<ManagedProcessCollectorActivation>(result.Activation);
        Assert.Equal(CollectorActivationState.Ready, restored.State);
        Assert.Equal(current.Streams["activity"].StreamId, restored.Streams["activity"].StreamId);
        await restored.StopAsync();
    }

    [Fact]
    public async Task SwitchToApproved_CandidateExitsNonZeroBeforeHello_KeepsTheEffectivePackageAndRecordsStartupFailed()
    {
        var current = await _fixture.StartAsync();
        var reference = _fixture.Install("1.1.0");
        _fixture.Runtime.ApprovePackageCandidate(_fixture.InstanceId, reference);

        var result = await _fixture.Switch.SwitchToApprovedAsync(
            _fixture.InstanceId,
            FastUpdateOptions("exit_nonzero_before_hello"));

        Assert.Equal(CollectorPackageSwitchOutcome.RolledBack, result.Outcome);
        var failure = Assert.IsType<CollectorPackageUpdateFailure>(result.Status.LastFailure);
        Assert.Equal(CollectorRegistryFailureReason.StartupFailed, failure.Reason);
        Assert.Contains("process_exited", failure.Message, StringComparison.Ordinal);
        Assert.Equal("1.0.0", result.Status.CurrentVersion);
        Assert.Null(result.Status.LastKnownGood);
        var restored = Assert.IsType<ManagedProcessCollectorActivation>(result.Activation);
        Assert.Equal(CollectorActivationState.Ready, restored.State);
        Assert.Equal(current.Streams["activity"].StreamId, restored.Streams["activity"].StreamId);
        await restored.StopAsync();
    }

    [Fact]
    public async Task SwitchToApproved_CandidateHandshakeIsIncompatible_KeepsTheEffectivePackageAndRecordsIncompatible()
    {
        await _fixture.StartAsync();
        var reference = _fixture.Install("1.1.0");
        _fixture.Runtime.ApprovePackageCandidate(_fixture.InstanceId, reference);

        var result = await _fixture.Switch.SwitchToApprovedAsync(
            _fixture.InstanceId,
            FastUpdateOptions("invalid_capability_type"));

        Assert.Equal(CollectorPackageSwitchOutcome.RolledBack, result.Outcome);
        var failure = Assert.IsType<CollectorPackageUpdateFailure>(result.Status.LastFailure);
        Assert.Equal(CollectorRegistryFailureReason.Incompatible, failure.Reason);
        Assert.Contains("protocol_invalid_message", failure.Message, StringComparison.Ordinal);
        Assert.Equal("1.0.0", result.Status.CurrentVersion);
        Assert.Null(result.Status.LastKnownGood);
        await result.Activation!.StopAsync();
    }

    [Fact]
    public async Task SwitchToApproved_ApprovedDirectoryLostItsCompletionMarker_IsRefusedWithoutTouchingTheActivation()
    {
        var current = await _fixture.StartAsync();
        var reference = _fixture.Install("1.1.0");
        _fixture.Runtime.ApprovePackageCandidate(_fixture.InstanceId, reference);
        File.Delete(Path.Combine(
            _fixture.Installations.DirectoryFor(reference),
            CollectorInstallationMarker.FileName));

        var result = await _fixture.Switch.SwitchToApprovedAsync(_fixture.InstanceId, FastUpdateOptions());

        Assert.Equal(CollectorPackageSwitchOutcome.Rejected, result.Outcome);
        Assert.Null(result.Activation);
        var failure = Assert.IsType<CollectorPackageUpdateFailure>(result.Status.LastFailure);
        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMissing, failure.Reason);
        // Refused before anything was stopped: the Collector never lost a second of collection.
        Assert.Equal(CollectorActivationState.Ready, current.State);
        Assert.False(current.Completion.IsCompleted);
        Assert.Equal("1.0.0", result.Status.CurrentVersion);
        await current.StopAsync();
    }

    [Fact]
    public async Task SwitchToApproved_ApprovedDirectoryCompletesAnotherCandidate_IsRefused()
    {
        var current = await _fixture.StartAsync();
        var installed = _fixture.Install("1.1.0");
        // The owner approved the same Version and content hash the Registry advertised, but the directory
        // that would own it holds a copy completed for a different artifact: identical content is not the
        // same exact candidate.
        var impostor = installed with { ArtifactSha256 = OtherArtifactSha256 };
        SwitchFixture.CopyDirectory(
            _fixture.Installations.DirectoryFor(installed),
            _fixture.Installations.DirectoryFor(impostor));
        _fixture.Runtime.ApprovePackageCandidate(_fixture.InstanceId, impostor);

        var result = await _fixture.Switch.SwitchToApprovedAsync(_fixture.InstanceId, FastUpdateOptions());

        Assert.Equal(CollectorPackageSwitchOutcome.Rejected, result.Outcome);
        var failure = Assert.IsType<CollectorPackageUpdateFailure>(result.Status.LastFailure);
        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMismatch, failure.Reason);
        Assert.Equal("1.0.0", result.Status.CurrentVersion);
        Assert.Equal(CollectorActivationState.Ready, current.State);
        await current.StopAsync();
    }

    /// <summary>
    /// Two Collector Instances may hold the very same Collector Installation, and one of them switching
    /// says nothing about the other: enablement, promotion and failure are per-Instance facts.
    /// </summary>
    [Fact]
    public async Task SwitchToApproved_SharedInstallation_LeavesTheOtherInstanceOnItsOwnPackage()
    {
        await _fixture.StartAsync();
        var second = _fixture.CreateInstance();
        var secondActivation = await _fixture.StartAsync(second);
        var reference = _fixture.Install("1.1.0");
        _fixture.Runtime.ApprovePackageCandidate(_fixture.InstanceId, reference);
        _fixture.Runtime.ApprovePackageCandidate(second, reference);

        var result = await _fixture.Switch.SwitchToApprovedAsync(_fixture.InstanceId, FastUpdateOptions());

        Assert.Equal(CollectorPackageSwitchOutcome.Switched, result.Outcome);
        var untouched = _fixture.Runtime.GetPackageUpdateStatus(second);
        Assert.Equal("1.0.0", untouched.CurrentVersion);
        Assert.Null(untouched.LastKnownGood);
        Assert.Null(untouched.LastFailure);
        Assert.Equal(reference, untouched.ApprovedCandidate);
        Assert.Equal(CollectorActivationState.Ready, secondActivation.State);
        Assert.False(secondActivation.Completion.IsCompleted);
        // The Installation stayed usable for the Instance that has not switched yet.
        Assert.True(_fixture.Installations.OpenInstallation(reference).IsSuccess);
        await result.Activation!.StopAsync();
        await secondActivation.StopAsync();
    }

    [Fact]
    public async Task SwitchToApproved_FailureOnOneInstance_IsNotRecordedOnAnother()
    {
        await _fixture.StartAsync();
        var second = _fixture.CreateInstance();
        var secondActivation = await _fixture.StartAsync(second);
        var reference = _fixture.Install("1.1.0");
        _fixture.Runtime.ApprovePackageCandidate(_fixture.InstanceId, reference);
        _fixture.Runtime.ApprovePackageCandidate(second, reference);

        var result = await _fixture.Switch.SwitchToApprovedAsync(
            _fixture.InstanceId,
            FastUpdateOptions("exit_nonzero_before_hello"));

        Assert.Equal(CollectorPackageSwitchOutcome.RolledBack, result.Outcome);
        Assert.NotNull(result.Status.LastFailure);
        Assert.Null(_fixture.Runtime.GetPackageUpdateStatus(second).LastFailure);
        Assert.Equal(CollectorActivationState.Ready, secondActivation.State);
        await result.Activation!.StopAsync();
        await secondActivation.StopAsync();
    }

    /// <summary>
    /// While one switch attempt is in flight, a second attempt cannot start a second child process for
    /// the same Collector Instance: the writer lease has already left the previous Activation and the
    /// candidate has not taken it yet, so there is nothing to hand over and the attempt is refused before
    /// it touches anything. Repeated, because a writer-lease invariant that only usually holds is not one
    /// — and made deterministic by waiting for the candidate's Starting phase rather than by sleeping.
    /// </summary>
    [Fact]
    public async Task SwitchToApproved_SecondAttemptDuringAnAttempt_IsRefusedWithoutStartingASecondProcess()
    {
        var reference = _fixture.Install("1.1.0");
        for (var attempt = 0; attempt < ConcurrentAttemptRepeats; attempt++)
        {
            var instanceId = attempt == 0 ? _fixture.InstanceId : _fixture.CreateInstance();
            var current = await _fixture.StartAsync(instanceId);
            var streamId = current.Streams["activity"].StreamId;
            _fixture.Runtime.ApprovePackageCandidate(instanceId, reference);

            var first = _fixture.Switch.SwitchToApprovedAsync(instanceId, FastUpdateOptions());
            await WaitForPhaseAsync(instanceId, CollectorRuntimePhase.Starting);
            var second = await _fixture.Switch.SwitchToApprovedAsync(instanceId, FastUpdateOptions());
            var winner = await first;

            Assert.Equal(CollectorPackageSwitchOutcome.Switched, winner.Outcome);
            Assert.Equal(CollectorPackageSwitchOutcome.Rejected, second.Outcome);
            var refusal = Assert.IsType<CollectorPackageUpdateFailure>(second.Status.LastFailure);
            Assert.Equal(CollectorRegistryFailureReason.StartupFailed, refusal.Reason);
            Assert.Contains("activation_not_ready", refusal.Message, StringComparison.Ordinal);
            Assert.Null(second.Activation);
            var switched = _fixture.Runtime.GetPackageUpdateStatus(instanceId);
            Assert.Equal("1.1.0", switched.CurrentVersion);
            Assert.Equal("1.1.0", switched.LastKnownGood?.PackageVersion);
            Assert.Equal(CollectorActivationState.Ready, winner.Activation!.State);
            Assert.Equal(streamId, winner.Activation.Streams["activity"].StreamId);
            await winner.Activation.StopAsync();
        }
    }

    [Fact]
    public async Task ResolveEffectivePackage_ApprovedCandidateThatWasNeverReady_KeepsStartingTheHostPackage()
    {
        var current = await _fixture.StartAsync();
        var reference = _fixture.Install("1.1.0");
        _fixture.Runtime.ApprovePackageCandidate(_fixture.InstanceId, reference);

        var resolved = _fixture.Switch.ResolveEffectivePackage(_fixture.InstanceId, _fixture.HostPackage);

        // Approval is not promotion. A host restart must not be a way for a candidate to take over
        // without ever having reached Ready.
        Assert.Equal(_fixture.HostPackage.PackageDirectory, resolved.PackageDirectory);
        Assert.Null(_fixture.Runtime.GetPackageUpdateStatus(_fixture.InstanceId).LastFailure);
        await current.StopAsync();
    }

    [Fact]
    public async Task ResolveEffectivePackage_AfterASuccessfulSwitchAndRestart_StartsTheInstallationWithOneWriter()
    {
        var current = await _fixture.StartAsync();
        var streamId = current.Streams["activity"].StreamId;
        var reference = _fixture.Install("1.1.0");
        _fixture.Runtime.ApprovePackageCandidate(_fixture.InstanceId, reference);
        var result = await _fixture.Switch.SwitchToApprovedAsync(_fixture.InstanceId, FastUpdateOptions());
        Assert.Equal(CollectorPackageSwitchOutcome.Switched, result.Outcome);
        await result.Activation!.StopAsync();

        _fixture.RestartHost();
        var resolved = _fixture.Switch.ResolveEffectivePackage(_fixture.InstanceId, _fixture.HostPackage);
        var reactivated = await _fixture.Runtime.ActivateManagedProcessAsync(
            _fixture.InstanceId,
            resolved,
            Options());

        Assert.Equal(
            Path.GetFullPath(_fixture.Installations.DirectoryFor(reference)),
            Path.GetFullPath(resolved.PackageDirectory));
        Assert.Equal(CollectorRuntimePhase.Ready, reactivated.RuntimeState.Phase);
        Assert.Equal("1.1.0", _fixture.Runtime.GetInstance(_fixture.InstanceId).PackageVersion);
        Assert.Equal(streamId, reactivated.Streams["activity"].StreamId);
        await reactivated.StopAsync();
    }

    [Fact]
    public async Task ResolveEffectivePackage_EffectiveInstallationLostItsMarker_FallsBackAndRecordsWhy()
    {
        var current = await _fixture.StartAsync();
        var reference = _fixture.Install("1.1.0");
        _fixture.Runtime.ApprovePackageCandidate(_fixture.InstanceId, reference);
        var result = await _fixture.Switch.SwitchToApprovedAsync(_fixture.InstanceId, FastUpdateOptions());
        Assert.Equal(CollectorPackageSwitchOutcome.Switched, result.Outcome);
        await result.Activation!.StopAsync();
        File.Delete(Path.Combine(
            _fixture.Installations.DirectoryFor(reference),
            CollectorInstallationMarker.FileName));

        var resolved = _fixture.Switch.ResolveEffectivePackage(_fixture.InstanceId, _fixture.HostPackage);

        // Content that no longer matches its completion marker is not a Collector Installation, so it is
        // not started even though this Instance really was running it.
        Assert.Equal(_fixture.HostPackage.PackageDirectory, resolved.PackageDirectory);
        var failure = Assert.IsType<CollectorPackageUpdateFailure>(
            _fixture.Runtime.GetPackageUpdateStatus(_fixture.InstanceId).LastFailure);
        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMissing, failure.Reason);
        Assert.Equal(CollectorActivationState.Stopped, current.State);
    }

    /// <summary>
    /// The host may die in the middle of a switch, before the candidate ever reached Ready. What comes
    /// back afterwards is the Package that was effective all along, and the approval is still waiting.
    /// </summary>
    [Fact]
    public async Task SwitchToApproved_InterruptedBeforeReady_ConvergesOnTheEffectivePackageAfterARestart()
    {
        var current = await _fixture.StartAsync();
        var streamId = current.Streams["activity"].StreamId;
        var reference = _fixture.Install("1.1.0");
        _fixture.Runtime.ApprovePackageCandidate(_fixture.InstanceId, reference);
        using var cancellation = new CancellationTokenSource();
        var switching = _fixture.Switch.SwitchToApprovedAsync(
            _fixture.InstanceId,
            FastUpdateOptions("startup_timeout"),
            cancellation.Token);
        await WaitForPhaseAsync(_fixture.InstanceId, CollectorRuntimePhase.Starting);

        await cancellation.CancelAsync();
        var result = await switching;

        Assert.Equal(CollectorPackageSwitchOutcome.RolledBack, result.Outcome);
        Assert.Equal("1.0.0", result.Status.CurrentVersion);
        Assert.Null(result.Status.LastKnownGood);
        await result.Activation!.StopAsync();

        _fixture.RestartHost();
        var resolved = _fixture.Switch.ResolveEffectivePackage(_fixture.InstanceId, _fixture.HostPackage);
        var reactivated = await _fixture.Runtime.ActivateManagedProcessAsync(
            _fixture.InstanceId,
            resolved,
            Options());

        Assert.Equal(_fixture.HostPackage.PackageDirectory, resolved.PackageDirectory);
        Assert.Equal(CollectorRuntimePhase.Ready, reactivated.RuntimeState.Phase);
        Assert.Equal("1.0.0", _fixture.Runtime.GetInstance(_fixture.InstanceId).PackageVersion);
        Assert.Equal(streamId, reactivated.Streams["activity"].StreamId);
        // The owner's approval outlives the failed attempt, and so does the Installation.
        var status = _fixture.Runtime.GetPackageUpdateStatus(_fixture.InstanceId);
        Assert.Equal(reference, status.ApprovedCandidate);
        Assert.True(_fixture.Installations.OpenInstallation(reference).IsSuccess);
        await reactivated.StopAsync();
    }

    /// <summary>
    /// The reason projection is a translation of the Collector Runtime's Activation failure codes, and it
    /// refuses to invent a verdict: anything that is not recognisably a compatibility rejection or a
    /// Ready timeout is reported as a startup failure.
    /// </summary>
    [Theory]
    [InlineData("activation_start_timeout", CollectorRegistryFailureReason.ReadyTimeout)]
    [InlineData("process_exited", CollectorRegistryFailureReason.StartupFailed)]
    [InlineData("process_start_failed", CollectorRegistryFailureReason.StartupFailed)]
    [InlineData("protocol_invalid_message", CollectorRegistryFailureReason.Incompatible)]
    [InlineData("protocol_no_common_major", CollectorRegistryFailureReason.Incompatible)]
    [InlineData("capability_no_common_version", CollectorRegistryFailureReason.Incompatible)]
    [InlineData("config_version_unsupported", CollectorRegistryFailureReason.Incompatible)]
    [InlineData("package_mismatch", CollectorRegistryFailureReason.Incompatible)]
    [InlineData("spec_revision_stale", CollectorRegistryFailureReason.Incompatible)]
    [InlineData("output_not_declared", CollectorRegistryFailureReason.Incompatible)]
    [InlineData("stream_writer_conflict", CollectorRegistryFailureReason.StartupFailed)]
    [InlineData("something_the_Runtime_grew_later", CollectorRegistryFailureReason.StartupFailed)]
    public void ReasonFor_ActivationFailureCode_ProjectsOntoTheExistingTaxonomy(
        string code,
        CollectorRegistryFailureReason expected) =>
        Assert.Equal(expected, CollectorPackageSwitch.ReasonFor(code));

    private static ManagedProcessActivationOptions Options(string? behavior = null) => new()
    {
        StartupTimeout = NonTimeoutStartupBudget,
        DrainGracePeriod = TimeSpan.FromSeconds(2),
        EnvironmentVariables = behavior is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["HEARTBEAT_REFERENCE_BEHAVIOR"] = behavior }
    };

    private static ManagedProcessUpdateOptions FastUpdateOptions(string? candidateBehavior = null) => new()
    {
        CandidateActivation = Options(candidateBehavior),
        RollbackActivation = Options()
    };

    private static ManagedProcessUpdateOptions NeverReadyUpdateOptions() => new()
    {
        CandidateActivation = new ManagedProcessActivationOptions
        {
            // The candidate never sends activation.hello, so the startup budget is the only thing that can
            // end the attempt: a small budget makes the timeout the test's subject, not a race with it.
            StartupTimeout = TimeSpan.FromMilliseconds(250),
            DrainGracePeriod = TimeSpan.FromSeconds(2),
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["HEARTBEAT_REFERENCE_BEHAVIOR"] = "startup_timeout"
            }
        },
        RollbackActivation = Options()
    };

    /// <summary>
    /// Waits on the Runtime State publication signal instead of sampling it, so the budget only guards
    /// against a hang and never competes with real Collector process startup cost.
    /// </summary>
    private async Task WaitForPhaseAsync(Guid collectorInstanceId, CollectorRuntimePhase phase)
    {
        using var hangGuard = new CancellationTokenSource(PhaseSignalHangGuard);
        try
        {
            await _fixture.Runtime.WaitForManagedProcessPhaseAsync(collectorInstanceId, phase, hangGuard.Token);
        }
        catch (OperationCanceledException) when (hangGuard.IsCancellationRequested)
        {
            Assert.Fail(
                $"ManagedProcess Runtime State never published '{phase}'; last published phase was " +
                $"'{_fixture.Runtime.GetManagedProcessRuntimeState(collectorInstanceId).Phase}'.");
        }
    }

    /// <summary>
    /// One Hub state directory holding both <c>collector-runtime.json</c> and the Collector Installation
    /// tree, plus the Collector Package the host itself delivers. Installations are planted the way a real
    /// interrupted install leaves the disk — content first, completion marker last — because the marker is
    /// exactly what the admission decision reads.
    /// </summary>
    private sealed class SwitchFixture : IDisposable
    {
        public static readonly DateTimeOffset FailureTime =
            new(2026, 8, 22, 12, 10, 0, TimeSpan.Zero);

        private readonly List<ManagedReferenceCollectorPackage> _packages = [];
        private readonly ManagedReferenceCollectorPackage _hostPackageCopy;
        private readonly string _state;
        private CollectorRuntime _runtime;

        private SwitchFixture(
            string state,
            ManagedReferenceCollectorPackage hostPackageCopy,
            LocalCollectorPackage hostPackage,
            CollectorInstallationStore installations,
            CollectorRuntime runtime,
            Guid instanceId)
        {
            _state = state;
            _hostPackageCopy = hostPackageCopy;
            _runtime = runtime;
            HostPackage = hostPackage;
            Installations = installations;
            InstanceId = instanceId;
            Switch = NewSwitch();
        }

        public LocalCollectorPackage HostPackage { get; }
        public CollectorInstallationStore Installations { get; }
        public CollectorRuntime Runtime => _runtime;
        public CollectorPackageSwitch Switch { get; private set; }
        public Guid InstanceId { get; }

        public static SwitchFixture Create()
        {
            var state = Path.Combine(Path.GetTempPath(), $"heartbeat-package-switch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(state);
            var hostPackageCopy = ManagedReferenceCollectorPackage.Create();
            try
            {
                var hostPackage = LocalCollectorPackage.Load(hostPackageCopy.Path);
                var runtime = CollectorRuntime.Open(
                    Path.Combine(state, "collector-runtime.json"),
                    new RecordingSegmentSink());
                using var config = JsonDocument.Parse("{}");
                var instance = runtime.CreateInstance(
                    hostPackage,
                    new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account),
                    new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
                return new SwitchFixture(
                    state,
                    hostPackageCopy,
                    hostPackage,
                    new CollectorInstallationStore(state),
                    runtime,
                    instance.CollectorInstanceId);
            }
            catch
            {
                hostPackageCopy.Dispose();
                Directory.Delete(state, recursive: true);
                throw;
            }
        }

        /// <summary>Another Collector Instance of the same Collector Package, on the same host.</summary>
        public Guid CreateInstance()
        {
            using var config = JsonDocument.Parse("{}");
            return _runtime.CreateInstance(
                HostPackage,
                new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account),
                new CollectorInstanceSpec(1, 1, config.RootElement.Clone())).CollectorInstanceId;
        }

        public ValueTask<ManagedProcessCollectorActivation> StartAsync(Guid? collectorInstanceId = null) =>
            _runtime.ActivateManagedProcessAsync(
                collectorInstanceId ?? InstanceId,
                HostPackage,
                Options());

        /// <summary>Publishes one exact candidate as a Collector Installation of this host.</summary>
        public CollectorPackageReference Install(string version)
        {
            var copy = ManagedReferenceCollectorPackage.Create(version);
            _packages.Add(copy);
            var staged = LocalCollectorPackage.Load(copy.Path);
            var reference = new CollectorPackageReference(
                staged.Manifest.PackageId,
                version,
                ArtifactSha256(copy.Path));
            var directory = Installations.DirectoryFor(reference);
            CopyDirectory(copy.Path, directory);
            File.WriteAllBytes(
                Path.Combine(directory, CollectorInstallationMarker.FileName),
                CollectorInstallationMarker.Write(new CollectorInstallationMarker(
                    CollectorInstallationMarker.CurrentSchemaVersion,
                    reference.PackageId,
                    reference.Version,
                    reference.ArtifactSha256,
                    LocalCollectorPackage.Load(directory).PackageContentHash)));
            var opened = Installations.OpenInstallation(reference);
            Assert.True(opened.IsSuccess, opened.Detail);
            return reference;
        }

        /// <summary>
        /// Restarts the host over the same state directory, which is the only way to prove that what
        /// starts next is decided by persisted Collector Runtime State rather than by memory.
        /// </summary>
        public void RestartHost()
        {
            _runtime.Dispose();
            _runtime = CollectorRuntime.Open(
                Path.Combine(_state, "collector-runtime.json"),
                new RecordingSegmentSink());
            Switch = NewSwitch();
        }

        public void Dispose()
        {
            _runtime.Dispose();
            foreach (var package in _packages)
                package.Dispose();
            _hostPackageCopy.Dispose();
            if (Directory.Exists(_state))
                Directory.Delete(_state, recursive: true);
        }

        public static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                File.Copy(file, target, overwrite: true);
                if (!OperatingSystem.IsWindows() && Path.GetFileName(file) == ExecutableName)
                    File.SetUnixFileMode(
                        target,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        private CollectorPackageSwitch NewSwitch() =>
            new(_runtime, Installations, new FixedTimeProvider(FailureTime));

        private static string ExecutableName => OperatingSystem.IsWindows()
            ? "Heartbeat.Collector.Reference.ManagedProcess.exe"
            : "Heartbeat.Collector.Reference.ManagedProcess";

        private static string ArtifactSha256(string packageDirectory) =>
            Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(Path.Combine(packageDirectory, ExecutableName))));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingSegmentSink : ISegmentSink, IDurableSegmentProjectionSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) { }
        public void UpsertDurable(ActivitySegmentItem snapshot, long revision) { }
        public void ReplayDurable(ActivitySegmentItem snapshot, long revision) { }
        public void RetractDurable(Guid segmentId, long revision) { }
    }
}
