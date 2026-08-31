using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Tests.Collectors;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public class ManagedProcessCollectorProtocolTranscriptTests
{
    private static readonly TimeSpan NonTimeoutStartupBudget = TimeSpan.FromSeconds(30);

    private static string ReferencePackagePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "ReferenceCollectorPackage");

    [Fact]
    public async Task HappyPath_UsesSharedTranscriptAndPublishesAccountSegment()
    {
        using var packageCopy = ManagedReferenceCollectorPackage.Create();
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var stateDirectory = TemporaryDirectory.Create();
        var sink = new SegmentIngestService(new TestClock(
            new DateTimeOffset(2026, 8, 22, 12, 10, 0, TimeSpan.Zero)));
        using var runtime = CollectorRuntime.Open(
            Path.Combine(stateDirectory.Path, "collector-runtime.json"),
            sink);
        var accountSubject = new SubjectReference(
            Guid.Parse("0198d5df-5df3-70a1-937d-68a7d64623e2"),
            SubjectKind.Account);
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            accountSubject,
            new CollectorInstanceSpec(7, 1, config.RootElement.Clone()));

        var activation = await runtime.ActivateManagedProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ManagedProcessActivationOptions
            {
                StartupTimeout = NonTimeoutStartupBudget,
                DrainGracePeriod = TimeSpan.FromSeconds(5)
            });

        CollectorProtocolTranscriptContract.AssertHappyPath(
            activation.State,
            activation.DeliveryCapability,
            activation.HandshakeTranscript,
            activation.Streams,
            accountSubject,
            "reference.account");
        var segment = await WaitForSegmentAsync(sink);
        Assert.Equal("reference.account", segment.Source);
        Assert.Equal("reference.account|online", segment.IdentityKey);
        ((IUploadSource<ActivitySegmentItem>)sink).Reinject([segment]);
        List<ActivitySegmentItem>? uploaded = null;
        var upload = new UploadStream<ActivitySegmentItem>(
            "reference account segment",
            sink,
            batch =>
            {
                uploaded = batch;
                return Task.FromResult(ApiResult.Ok);
            },
            new MemoryCache<ActivitySegmentItem>(),
            SnapshotCompaction.KeepLatest);
        await upload.DrainAsync();
        Assert.Equal("reference.account|online", Assert.Single(uploaded!).IdentityKey);

        await activation.StopAsync();

        Assert.Equal(CollectorActivationState.Stopped, activation.State);
        Assert.Equal(CollectorRuntimePhase.Stopped, activation.RuntimeState.Phase);
        Assert.Equal(0, activation.RuntimeState.PendingFacts);
        Assert.Equal(0, activation.RuntimeState.PendingGaps);
        Assert.False(activation.RuntimeState.ProcessTerminated);
        Assert.Equal(CollectorDrainReason.Drained, activation.RuntimeState.DrainResult!.LogicalResult.Reason);
        Assert.True(activation.RuntimeState.DrainResult.LogicalResult.RemainderDurable);
        Assert.Equal(
            CollectorDrainCompletionReason.Completed,
            activation.RuntimeState.DrainResult.CompletionReason);
    }

    [Fact]
    public async Task Activation_WithoutUniqueManagedProcessArtifact_IsRejectedBeforeLaunch()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var stateDirectory = TemporaryDirectory.Create();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(stateDirectory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.ActivateManagedProcessAsync(
                instance.CollectorInstanceId,
                package));

        Assert.Equal("package_mismatch", error.Error.Code);
        Assert.Contains("exactly one Artifact", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessExit_EndsActivationAndReleasesWriterForReplacement()
    {
        using var fixture = ManagedRuntimeFixture.Create();
        var failed = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options("exit_after_ready"));

        await failed.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForPhaseAsync(failed, CollectorRuntimePhase.Failed);

        Assert.Equal(CollectorActivationState.Stopped, failed.State);
        Assert.Equal("process_exited", failed.RuntimeState.Failure?.Code);
        var replacement = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options());
        Assert.Equal(failed.Streams["activity"].StreamId, replacement.Streams["activity"].StreamId);
        await replacement.StopAsync();
    }

    [Fact]
    public async Task PackageUpdate_CandidateStaysReady_PromotesCandidateToLastKnownGood()
    {
        using var originalCopy = ManagedReferenceCollectorPackage.Create();
        using var candidateCopy = ManagedReferenceCollectorPackage.Create("1.1.0");
        var original = LocalCollectorPackage.Load(originalCopy.Path);
        var candidate = LocalCollectorPackage.Load(candidateCopy.Path);
        using var stateDirectory = TemporaryDirectory.Create();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(stateDirectory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var originalActivation = await runtime.ActivateManagedProcessAsync(
            instance.CollectorInstanceId,
            original,
            Options());
        var originalStreamId = originalActivation.Streams["activity"].StreamId;

        var result = await runtime.UpdateManagedProcessAsync(
            instance.CollectorInstanceId,
            candidate,
            new ManagedProcessUpdateOptions
            {
                StabilityPeriod = TimeSpan.FromMilliseconds(100),
                CandidateActivation = Options(),
                RollbackActivation = Options()
            });

        Assert.Equal(ManagedProcessUpdateOutcome.Updated, result.Outcome);
        Assert.Equal("1.1.0", runtime.GetInstance(instance.CollectorInstanceId).PackageVersion);
        Assert.Equal(originalStreamId, result.Activation.Streams["activity"].StreamId);
        Assert.NotEqual(originalActivation.ActivationId, result.Activation.ActivationId);
        var lastKnownGood = Assert.IsType<LastKnownGoodCollectorPackage>(
            runtime.GetInstance(instance.CollectorInstanceId).LastKnownGoodPackage);
        Assert.Equal("1.1.0", lastKnownGood.PackageVersion);
        Assert.Equal(candidate.PackageContentHash, lastKnownGood.PackageContentHash);
        Assert.Equal("reference.managed", lastKnownGood.ArtifactId);
        Assert.Equal(1, lastKnownGood.ConfigVersion);
        await result.Activation.StopAsync();
        runtime.Dispose();

        using var reopened = CollectorRuntime.Open(
            Path.Combine(stateDirectory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        var restoredLastKnownGood = Assert.IsType<LastKnownGoodCollectorPackage>(
            reopened.GetInstance(instance.CollectorInstanceId).LastKnownGoodPackage);
        Assert.Equal(lastKnownGood, restoredLastKnownGood);
    }

    [Fact]
    public async Task PackageUpdate_SameVersionWithDifferentContent_PreservesExactRollbackAndPromotesCandidate()
    {
        using var originalCopy = ManagedReferenceCollectorPackage.Create();
        using var candidateCopy = ManagedReferenceCollectorPackage.Create();
        var manifestPath = Path.Combine(candidateCopy.Path, "collector-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var original = LocalCollectorPackage.Load(originalCopy.Path);
        var candidate = LocalCollectorPackage.Load(candidateCopy.Path);
        Assert.Equal(original.Manifest.Version, candidate.Manifest.Version);
        Assert.NotEqual(original.PackageContentHash, candidate.PackageContentHash);
        using var stateDirectory = TemporaryDirectory.Create();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(stateDirectory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        _ = await runtime.ActivateManagedProcessAsync(
            instance.CollectorInstanceId,
            original,
            Options());

        var result = await runtime.UpdateManagedProcessAsync(
            instance.CollectorInstanceId,
            candidate,
            FastUpdateOptions());

        Assert.Equal(ManagedProcessUpdateOutcome.Updated, result.Outcome);
        var resolved = runtime.GetInstance(instance.CollectorInstanceId);
        Assert.Equal(candidate.PackageContentHash, resolved.PackageContentHash);
        Assert.Equal(candidate.PackageContentHash, resolved.LastKnownGoodPackage?.PackageContentHash);
        await result.Activation.StopAsync();
    }

    [Fact]
    public async Task PackageUpdate_UnsupportedConfigIsRejectedBeforeStoppingCurrentActivation()
    {
        using var originalCopy = ManagedReferenceCollectorPackage.Create();
        using var candidateCopy = ManagedReferenceCollectorPackage.Create("1.1.0", [2], 2);
        var original = LocalCollectorPackage.Load(originalCopy.Path);
        var candidate = LocalCollectorPackage.Load(candidateCopy.Path);
        using var stateDirectory = TemporaryDirectory.Create();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(stateDirectory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account),
            new CollectorInstanceSpec(7, 1, config.RootElement.Clone()));
        var current = await runtime.ActivateManagedProcessAsync(
            instance.CollectorInstanceId,
            original,
            Options());

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.UpdateManagedProcessAsync(
                instance.CollectorInstanceId,
                candidate,
                FastUpdateOptions()));

        Assert.Equal("config_version_unsupported", error.Error.Code);
        Assert.Equal(CollectorActivationState.Ready, current.State);
        Assert.False(current.Completion.IsCompleted);
        Assert.Equal("1.0.0", runtime.GetInstance(instance.CollectorInstanceId).PackageVersion);
        await current.StopAsync();
    }

    [Fact]
    public async Task PackageUpdate_CandidateArtifactChangedAfterVerification_IsRejectedBeforeStoppingCurrentActivation()
    {
        using var fixture = ManagedUpdateFixture.Create();
        using var candidateCopy = ManagedReferenceCollectorPackage.Create("1.1.0");
        var candidate = LocalCollectorPackage.Load(candidateCopy.Path);
        var artifact = Assert.Single(candidate.Artifacts);
        var artifactPath = Path.Combine(candidate.PackageDirectory, artifact.Entrypoint);
        var changed = File.ReadAllBytes(artifactPath).Append((byte)0).ToArray();
        File.WriteAllBytes(artifactPath, changed);

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await fixture.Runtime.UpdateManagedProcessAsync(
                fixture.Instance.CollectorInstanceId,
                candidate,
                FastUpdateOptions()));

        Assert.Equal("package_mismatch", error.Error.Code);
        Assert.Equal(CollectorActivationState.Ready, fixture.Current.State);
        Assert.False(fixture.Current.Completion.IsCompleted);
        await fixture.Current.StopAsync();
    }

    [Fact]
    public async Task PackageUpdate_HandshakeFailure_RestartsLastKnownGoodWithStableInstanceAndStream()
    {
        using var fixture = ManagedUpdateFixture.Create();
        var originalActivationId = fixture.Current.ActivationId;
        var originalStreamId = fixture.Current.Streams["activity"].StreamId;
        using var candidateCopy = ManagedReferenceCollectorPackage.Create("1.1.0");
        var candidate = LocalCollectorPackage.Load(candidateCopy.Path);

        var result = await fixture.Runtime.UpdateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            candidate,
            FastUpdateOptions("invalid_capability_type"));

        Assert.Equal(ManagedProcessUpdateOutcome.RolledBack, result.Outcome);
        Assert.Equal("protocol_invalid_message", result.CandidateFailure?.Code);
        Assert.Equal(fixture.Instance.CollectorInstanceId, result.Activation.CollectorInstanceId);
        Assert.Equal(originalStreamId, result.Activation.Streams["activity"].StreamId);
        Assert.NotEqual(originalActivationId, result.Activation.ActivationId);
        Assert.Equal("1.0.0", fixture.Runtime.GetInstance(fixture.Instance.CollectorInstanceId).PackageVersion);
        Assert.Equal(
            "1.0.0",
            fixture.Runtime.GetInstance(fixture.Instance.CollectorInstanceId)
                .LastKnownGoodPackage?.PackageVersion);
        Assert.Contains(
            result.Activation.RuntimeState.Diagnostics ?? [],
            diagnostic => diagnostic.Code == "update_candidate_failed_rolled_back");
        await result.Activation.StopAsync();
    }

    [Fact]
    public async Task PackageUpdate_CandidateCrashesBeforeReady_RestartsLastKnownGood()
    {
        using var fixture = ManagedUpdateFixture.Create();
        using var candidateCopy = ManagedReferenceCollectorPackage.Create("1.1.0");
        var candidate = LocalCollectorPackage.Load(candidateCopy.Path);

        var result = await fixture.Runtime.UpdateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            candidate,
            FastUpdateOptions("exit_before_hello"));

        Assert.Equal(ManagedProcessUpdateOutcome.RolledBack, result.Outcome);
        Assert.Equal("process_exited", result.CandidateFailure?.Code);
        Assert.Equal(CollectorRuntimePhase.Ready, result.Activation.RuntimeState.Phase);
        Assert.Equal("1.0.0", fixture.Runtime.GetInstance(fixture.Instance.CollectorInstanceId).PackageVersion);
        Assert.Equal(1, fixture.Runtime.GetInstance(fixture.Instance.CollectorInstanceId).Spec.SpecRevision);
        await result.Activation.StopAsync();
    }

    [Fact]
    public async Task PackageUpdate_ConfigCannotChangeAfterCompatibilityPreflight()
    {
        using var fixture = ManagedUpdateFixture.Create();
        using var candidateCopy = ManagedReferenceCollectorPackage.Create("1.1.0");
        var candidate = LocalCollectorPackage.Load(candidateCopy.Path);
        using var cancellation = new CancellationTokenSource();
        var update = fixture.Runtime.UpdateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            candidate,
            FastUpdateOptions("startup_timeout"),
            cancellation.Token).AsTask();
        await WaitForPhaseAsync(
            fixture.Runtime,
            fixture.Instance.CollectorInstanceId,
            CollectorRuntimePhase.Starting);
        using var changedConfig = JsonDocument.Parse("{\"changed\":true}");

        var error = Assert.Throws<InvalidOperationException>(() =>
            fixture.Runtime.UpdateInstanceSpec(
                fixture.Instance.CollectorInstanceId,
                2,
                changedConfig.RootElement.Clone()));

        Assert.Contains("during a ManagedProcess update", error.Message, StringComparison.Ordinal);
        cancellation.Cancel();
        var result = await update;
        Assert.Equal(ManagedProcessUpdateOutcome.RolledBack, result.Outcome);
        Assert.Equal(1, fixture.Runtime.GetInstance(fixture.Instance.CollectorInstanceId).Spec.ConfigVersion);
        await result.Activation.StopAsync();
    }

    [Fact]
    public async Task PackageUpdate_CandidateExitsDuringStabilityPeriod_RollsBackWithoutStoppingOtherInstance()
    {
        using var first = ManagedUpdateFixture.Create();
        using var secondPackageCopy = ManagedReferenceCollectorPackage.Create();
        var secondPackage = LocalCollectorPackage.Load(secondPackageCopy.Path);
        using var config = JsonDocument.Parse("{}");
        var secondInstance = first.Runtime.CreateInstance(
            secondPackage,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var secondActivation = await first.Runtime.ActivateManagedProcessAsync(
            secondInstance.CollectorInstanceId,
            secondPackage,
            Options());
        using var candidateCopy = ManagedReferenceCollectorPackage.Create("1.1.0");
        var candidate = LocalCollectorPackage.Load(candidateCopy.Path);

        var result = await first.Runtime.UpdateManagedProcessAsync(
            first.Instance.CollectorInstanceId,
            candidate,
            FastUpdateOptions("exit_after_ready"));

        Assert.Equal(ManagedProcessUpdateOutcome.RolledBack, result.Outcome);
        Assert.Equal("process_exited", result.CandidateFailure?.Code);
        Assert.Equal(CollectorActivationState.Ready, secondActivation.State);
        Assert.False(secondActivation.Completion.IsCompleted);
        await result.Activation.StopAsync();
        await secondActivation.StopAsync();
    }

    [Fact]
    public async Task PackageUpdate_LastKnownGoodArtifactMissing_ReportsCandidateAndRollbackFailures()
    {
        using var fixture = ManagedUpdateFixture.Create();
        using var candidateCopy = ManagedReferenceCollectorPackage.Create("1.1.0");
        var candidate = LocalCollectorPackage.Load(candidateCopy.Path);
        Directory.Delete(fixture.PackageCopy.Path, recursive: true);

        var error = await Assert.ThrowsAsync<ManagedProcessUpdateException>(async () =>
            await fixture.Runtime.UpdateManagedProcessAsync(
                fixture.Instance.CollectorInstanceId,
                candidate,
                FastUpdateOptions("exit_before_hello")));

        Assert.Equal("process_exited", error.CandidateFailure.Code);
        Assert.Equal("last_known_good_package_missing", error.RollbackFailure.Code);
        var state = fixture.Runtime.GetManagedProcessRuntimeState(fixture.Instance.CollectorInstanceId);
        Assert.Equal("update_rollback_failed", state.Failure?.Code);
        Assert.Contains(
            state.Diagnostics ?? [],
            diagnostic => diagnostic.Code == "last_known_good_package_missing");
    }

    [Fact]
    public async Task Hello_OnlySelectsCapabilitiesSharedByCollectorPackageAndHub()
    {
        using var fixture = ManagedRuntimeFixture.Create();

        var activation = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options("extra_capability"));

        Assert.Equal(CollectorRuntimePhase.Ready, activation.RuntimeState.Phase);
        await activation.StopAsync();
    }

    [Fact]
    public async Task InteractiveAuthorization_WaitsWithoutFailingAndContinuesToReady()
    {
        using var fixture = ManagedRuntimeFixture.Create();
        var activationTask = fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options("authorization_required")).AsTask();

        await WaitForPhaseAsync(
            fixture.Runtime,
            fixture.Instance.CollectorInstanceId,
            CollectorRuntimePhase.WaitingForAuthorization);
        var waiting = fixture.Runtime.GetManagedProcessRuntimeState(fixture.Instance.CollectorInstanceId);
        var challenge = Assert.IsType<CollectorAuthorizationChallenge>(waiting.AuthorizationChallenge);
        Assert.Equal(CollectorAuthorizationChallengeKind.Credentials, challenge.Kind);
        Assert.Equal(["username", "password"], challenge.Fields.Select(field => field.Name));
        Assert.DoesNotContain("collector-password", JsonSerializer.Serialize(waiting), StringComparison.Ordinal);

        await fixture.Runtime.SubmitManagedProcessAuthorizationAsync(
            fixture.Instance.CollectorInstanceId,
            challenge.InteractionId,
            new Dictionary<string, string>
            {
                ["username"] = "collector-user",
                ["password"] = "collector-password"
            });
        var activation = await activationTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CollectorRuntimePhase.Ready, activation.RuntimeState.Phase);
        Assert.Null(activation.RuntimeState.AuthorizationChallenge);
        await activation.StopAsync();
    }

    [Fact]
    public async Task InteractiveAuthorization_DoesNotConsumeTheStartupTimeout()
    {
        using var fixture = ManagedRuntimeFixture.Create();
        var options = new ManagedProcessActivationOptions
        {
            StartupTimeout = TimeSpan.FromSeconds(1),
            DrainGracePeriod = TimeSpan.FromSeconds(2),
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["HEARTBEAT_REFERENCE_BEHAVIOR"] = "authorization_required"
            }
        };
        var activationTask = fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            options).AsTask();

        await WaitForPhaseAsync(
            fixture.Runtime,
            fixture.Instance.CollectorInstanceId,
            CollectorRuntimePhase.WaitingForAuthorization);
        await Task.Delay(1_200);
        Assert.False(activationTask.IsCompleted);
        var challenge = Assert.IsType<CollectorAuthorizationChallenge>(
            fixture.Runtime.GetManagedProcessRuntimeState(fixture.Instance.CollectorInstanceId)
                .AuthorizationChallenge);

        await fixture.Runtime.SubmitManagedProcessAuthorizationAsync(
            fixture.Instance.CollectorInstanceId,
            challenge.InteractionId,
            new Dictionary<string, string>
            {
                ["username"] = "collector-user",
                ["password"] = "collector-password"
            });
        var activation = await activationTask.WaitAsync(TimeSpan.FromSeconds(5));
        await activation.StopAsync();
    }

    [Fact]
    public async Task InteractiveAuthorization_ResumesStartupTimeoutAfterTheResponse()
    {
        using var fixture = ManagedRuntimeFixture.Create();
        var options = new ManagedProcessActivationOptions
        {
            StartupTimeout = TimeSpan.FromSeconds(2),
            DrainGracePeriod = TimeSpan.FromSeconds(2),
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["HEARTBEAT_REFERENCE_BEHAVIOR"] = "authorization_then_hang"
            }
        };
        var activationTask = fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            options).AsTask();
        await WaitForPhaseAsync(
            fixture.Runtime,
            fixture.Instance.CollectorInstanceId,
            CollectorRuntimePhase.WaitingForAuthorization);
        await Task.Delay(2_200);
        var challenge = Assert.IsType<CollectorAuthorizationChallenge>(
            fixture.Runtime.GetManagedProcessRuntimeState(fixture.Instance.CollectorInstanceId)
                .AuthorizationChallenge);

        await fixture.Runtime.SubmitManagedProcessAuthorizationAsync(
            fixture.Instance.CollectorInstanceId,
            challenge.InteractionId,
            new Dictionary<string, string>
            {
                ["username"] = "collector-user",
                ["password"] = "collector-password"
            });

        var exception = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await activationTask.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal("activation_start_timeout", exception.Error.Code);
        Assert.Equal(
            CollectorRuntimePhase.Failed,
            fixture.Runtime.GetManagedProcessRuntimeState(fixture.Instance.CollectorInstanceId).Phase);
    }

    [Fact]
    public async Task InstanceSecretMessages_UseTheSeparateEncryptedStore()
    {
        using var packageCopy = ManagedReferenceCollectorPackage.Create();
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var stateDirectory = TemporaryDirectory.Create();
        var secretStore = new EncryptedFileCollectorSecretStore(
            Path.Combine(stateDirectory.Path, "secrets"));
        using var runtime = CollectorRuntime.Open(
            Path.Combine(stateDirectory.Path, "collector-runtime.json"),
            new RecordingSegmentSink(),
            secretStore: secretStore);
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));

        var activation = await runtime.ActivateManagedProcessAsync(
            instance.CollectorInstanceId,
            package,
            Options("secret_roundtrip"));

        Assert.Equal(
            "reference-secret-value",
            await secretStore.ReadAsync(instance.CollectorInstanceId, "session"));
        Assert.DoesNotContain(
            "reference-secret-value",
            await File.ReadAllTextAsync(Path.Combine(stateDirectory.Path, "collector-runtime.json")),
            StringComparison.Ordinal);
        await activation.StopAsync();
    }

    [Fact]
    public async Task ProtocolCorruption_ProducesStructuredFailureAndStopsProcess()
    {
        using var fixture = ManagedRuntimeFixture.Create();
        var activation = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options("corrupt_after_ready"));

        await activation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForPhaseAsync(activation, CollectorRuntimePhase.Failed);

        Assert.Equal(CollectorActivationState.Stopped, activation.State);
        Assert.Equal("protocol_invalid_message", activation.RuntimeState.Failure?.Code);
        Assert.True(activation.RuntimeState.ProcessTerminated);
    }

    [Theory]
    [InlineData("invalid_capability_type")]
    [InlineData("unknown_hello_field")]
    [InlineData("uppercase_uuid")]
    public async Task InvalidHelloFields_AreReportedAsProtocolCorruption(string behavior)
    {
        using var fixture = ManagedRuntimeFixture.Create();

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await fixture.Runtime.ActivateManagedProcessAsync(
                fixture.Instance.CollectorInstanceId,
                fixture.Package,
                Options(behavior)));

        Assert.Equal("protocol_invalid_message", error.Error.Code);
        Assert.Equal(
            "protocol_invalid_message",
            fixture.Runtime.GetManagedProcessRuntimeState(fixture.Instance.CollectorInstanceId).Failure?.Code);
    }

    [Fact]
    public void NonTimeoutTranscriptOptions_LeaveSchedulerHeadroom()
    {
        var options = Options("invalid_capability_type");

        Assert.Equal(TimeSpan.FromSeconds(30), options.StartupTimeout);
    }

    [Fact]
    public async Task ProcessExitBeforeHello_IsDistinctFromProtocolCorruption()
    {
        using var fixture = ManagedRuntimeFixture.Create();

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await fixture.Runtime.ActivateManagedProcessAsync(
                fixture.Instance.CollectorInstanceId,
                fixture.Package,
                Options("exit_before_hello")));

        Assert.Equal("process_exited", error.Error.Code);
        var state = fixture.Runtime.GetManagedProcessRuntimeState(fixture.Instance.CollectorInstanceId);
        Assert.Equal("process_exited", state.Failure?.Code);
        Assert.Equal(0, state.Failure?.ProcessExitCode);
    }

    [Fact]
    public async Task ProtocolCorruptionDuringDrain_IsFailedAndWriterIsReleased()
    {
        using var fixture = ManagedRuntimeFixture.Create();
        var activation = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options("corrupt_on_drain"));

        await activation.StopAsync();

        Assert.Equal(CollectorActivationState.Stopped, activation.State);
        Assert.Equal(CollectorRuntimePhase.Failed, activation.RuntimeState.Phase);
        Assert.Equal("protocol_invalid_message", activation.RuntimeState.Failure?.Code);
        var replacement = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options());
        await replacement.StopAsync();
    }

    [Fact]
    public async Task DrainWriteDisconnect_IsFailedAndWriterIsReleased()
    {
        using var fixture = ManagedRuntimeFixture.Create();
        var activation = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options(disconnectDrain: true));

        await activation.StopAsync();

        Assert.Equal(CollectorActivationState.Stopped, activation.State);
        Assert.Equal(CollectorRuntimePhase.Failed, activation.RuntimeState.Phase);
        Assert.Equal("protocol_invalid_message", activation.RuntimeState.Failure?.Code);
        var replacement = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            Options());
        await replacement.StopAsync();
    }

    [Fact]
    public async Task StartupTimeout_ProducesStructuredFailure()
    {
        using var fixture = ManagedRuntimeFixture.Create();

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await fixture.Runtime.ActivateManagedProcessAsync(
                fixture.Instance.CollectorInstanceId,
                fixture.Package,
                new ManagedProcessActivationOptions
                {
                    StartupTimeout = TimeSpan.FromMilliseconds(250),
                    DrainGracePeriod = TimeSpan.FromSeconds(1),
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        ["HEARTBEAT_REFERENCE_BEHAVIOR"] = "startup_timeout"
                    }
                }));

        Assert.Equal("activation_start_timeout", error.Error.Code);
        var state = fixture.Runtime.GetManagedProcessRuntimeState(fixture.Instance.CollectorInstanceId);
        Assert.Equal(CollectorRuntimePhase.Failed, state.Phase);
        Assert.Equal("activation_start_timeout", state.Failure?.Code);
    }

    [Fact]
    public async Task DrainDeadline_TerminatesUnresponsiveProcessAndKeepsPendingCountsUnknown()
    {
        using var fixture = ManagedRuntimeFixture.Create();
        var activation = await fixture.Runtime.ActivateManagedProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            new ManagedProcessActivationOptions
            {
                StartupTimeout = NonTimeoutStartupBudget,
                DrainGracePeriod = TimeSpan.FromMilliseconds(250),
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["HEARTBEAT_REFERENCE_BEHAVIOR"] = "ignore_drain"
                }
            });

        await activation.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CollectorRuntimePhase.Stopped, activation.RuntimeState.Phase);
        Assert.True(activation.RuntimeState.ProcessTerminated);
        Assert.Null(activation.RuntimeState.PendingFacts);
        Assert.Null(activation.RuntimeState.PendingGaps);
        Assert.Equal(
            CollectorDrainReason.DeadlineExceeded,
            activation.RuntimeState.DrainResult!.LogicalResult.Reason);
        Assert.False(activation.RuntimeState.DrainResult.LogicalResult.RemainderDurable);
        Assert.Equal(
            CollectorDrainCompletionReason.Completed,
            activation.RuntimeState.DrainResult.CompletionReason);
        CollectorDrainDriverConformance.AssertObserved(
            "managed_process",
            hubInitiated: true,
            "terminate_and_release");
    }

    private static async Task<Heartbeat.Core.DTOs.Segments.ActivitySegmentItem> WaitForSegmentAsync(
        SegmentIngestService sink)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var segments = sink.GetAndClearSegments();
            if (segments.Count != 0)
                return Assert.Single(segments);
            await Task.Delay(20, timeout.Token);
        }
    }

    private static ManagedProcessActivationOptions Options(
        string? behavior = null,
        bool disconnectDrain = false) => new()
        {
            StartupTimeout = NonTimeoutStartupBudget,
            DrainGracePeriod = TimeSpan.FromSeconds(2),
            StandardInputDecorator = disconnectDrain
            ? writer => new DisconnectOnDrainWriter(writer)
            : null,
            EnvironmentVariables = behavior is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["HEARTBEAT_REFERENCE_BEHAVIOR"] = behavior }
        };

    private static ManagedProcessUpdateOptions FastUpdateOptions(string? candidateBehavior = null) => new()
    {
        StabilityPeriod = TimeSpan.FromMilliseconds(100),
        CandidateActivation = Options(candidateBehavior),
        RollbackActivation = Options()
    };

    private static async Task WaitForPhaseAsync(
        ManagedProcessCollectorActivation activation,
        CollectorRuntimePhase phase)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (activation.RuntimeState.Phase != phase)
            await Task.Delay(20, timeout.Token);
    }

    private static async Task WaitForPhaseAsync(
        CollectorRuntime runtime,
        Guid collectorInstanceId,
        CollectorRuntimePhase phase)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (runtime.GetManagedProcessRuntimeState(collectorInstanceId).Phase != phase)
            await Task.Delay(20, timeout.Token);
    }

    private sealed class RecordingSegmentSink : ISegmentSink, IDurableSegmentProjectionSink
    {
        public void Push(List<Heartbeat.Core.DTOs.Segments.ActivitySegmentItem> snapshots) { }
        public void UpsertDurable(Heartbeat.Core.DTOs.Segments.ActivitySegmentItem snapshot, long revision) { }
        public void ReplayDurable(Heartbeat.Core.DTOs.Segments.ActivitySegmentItem snapshot, long revision) { }
        public void RetractDurable(Guid segmentId, long revision) { }
    }

    private sealed class DisconnectOnDrainWriter(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default) =>
            buffer.Span.Contains("\"type\":\"activation.drain\"", StringComparison.Ordinal)
                ? Task.FromException(new IOException("Simulated disconnected ManagedProcess stdin."))
                : inner.WriteLineAsync(buffer, cancellationToken);

        public override Task FlushAsync() => inner.FlushAsync();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class MemoryCache<T> : ICache<T>
    {
        private List<T> _items = [];
        public CacheFileStatus Status => CacheFileStatus.Ready;
        public void Add(List<T> items) => _items.AddRange(items);
        public List<T> Load() => [.. _items];
        public void Replace(List<T> items) => _items = [.. items];
        public void Clear() => _items.Clear();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"heartbeat-managed-process-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class ManagedRuntimeFixture : IDisposable
    {
        private readonly ManagedReferenceCollectorPackage _packageCopy;
        private readonly TemporaryDirectory _stateDirectory;

        private ManagedRuntimeFixture(
            ManagedReferenceCollectorPackage packageCopy,
            TemporaryDirectory stateDirectory,
            LocalCollectorPackage package,
            CollectorRuntime runtime,
            CollectorInstance instance)
        {
            _packageCopy = packageCopy;
            _stateDirectory = stateDirectory;
            Package = package;
            Runtime = runtime;
            Instance = instance;
        }

        public LocalCollectorPackage Package { get; }
        public ManagedReferenceCollectorPackage PackageCopy => _packageCopy;
        public CollectorRuntime Runtime { get; }
        public CollectorInstance Instance { get; }

        public static ManagedRuntimeFixture Create()
        {
            var packageCopy = ManagedReferenceCollectorPackage.Create();
            var stateDirectory = TemporaryDirectory.Create();
            try
            {
                var package = LocalCollectorPackage.Load(packageCopy.Path);
                var runtime = CollectorRuntime.Open(
                    Path.Combine(stateDirectory.Path, "collector-runtime.json"),
                    new RecordingSegmentSink());
                using var config = JsonDocument.Parse("{}");
                var instance = runtime.CreateInstance(
                    package,
                    new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account),
                    new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
                return new ManagedRuntimeFixture(packageCopy, stateDirectory, package, runtime, instance);
            }
            catch
            {
                stateDirectory.Dispose();
                packageCopy.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Runtime.Dispose();
            _stateDirectory.Dispose();
            _packageCopy.Dispose();
        }
    }

    private sealed class ManagedUpdateFixture : IDisposable
    {
        private readonly ManagedRuntimeFixture _runtimeFixture;

        private ManagedUpdateFixture(
            ManagedRuntimeFixture runtimeFixture,
            ManagedProcessCollectorActivation current)
        {
            _runtimeFixture = runtimeFixture;
            Current = current;
        }

        public ManagedReferenceCollectorPackage PackageCopy => _runtimeFixture.PackageCopy;
        public LocalCollectorPackage Package => _runtimeFixture.Package;
        public CollectorRuntime Runtime => _runtimeFixture.Runtime;
        public CollectorInstance Instance => _runtimeFixture.Instance;
        public ManagedProcessCollectorActivation Current { get; }

        public static ManagedUpdateFixture Create()
        {
            var runtimeFixture = ManagedRuntimeFixture.Create();
            try
            {
                var current = runtimeFixture.Runtime.ActivateManagedProcessAsync(
                    runtimeFixture.Instance.CollectorInstanceId,
                    runtimeFixture.Package,
                    Options()).AsTask().GetAwaiter().GetResult();
                return new ManagedUpdateFixture(runtimeFixture, current);
            }
            catch
            {
                runtimeFixture.Dispose();
                throw;
            }
        }

        public void Dispose() => _runtimeFixture.Dispose();
    }
}
