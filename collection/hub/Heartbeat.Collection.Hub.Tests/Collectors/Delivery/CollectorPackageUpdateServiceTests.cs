using System.Security.Cryptography;
using System.Text.Json;
using Heartbeat.Collection.CollectorRelease;
using Heartbeat.Collection.Hub.Collectors.Delivery;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Delivery;

/// <summary>
/// The narrow update management surface behind the authenticated Hub/Headless API: read
/// <c>Current</c>, run one manual <c>CheckNow</c>, and approve one exact Collector Package
/// candidate.
///
/// Three invariants are the point of this file. Approval names an exact PackageId + Version +
/// artifact SHA-256 and is accepted only when that candidate really is a Collector Installation on
/// this machine, so an installed candidate that the Registry no longer advertises stays approvable.
/// A check is one manual attempt: it never retries behind the caller, and a failed one records a
/// structured last error without touching the Last-Known-Good, the approved candidate or any real
/// Installation. And all of it is one persisted state on the Collector Instance, so
/// <c>Current</c> is only a projection that survives a restart.
/// </summary>
public sealed class CollectorPackageUpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-updates-registry-{Guid.NewGuid():N}");
    private readonly string _state = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-updates-state-{Guid.NewGuid():N}");
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-updates-scratch-{Guid.NewGuid():N}");
    private readonly StaticRegistryFixtureServer _server;
    private readonly StaticRegistryFixture _fixture;
    private readonly HttpClient _httpClient = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly CollectorInstallationStore _store;
    private readonly LocalCollectorPackage _package;
    private readonly string _statePath;
    private CollectorRuntime _runtime;
    private Guid _instanceId;

    // A bounded token so a hung fixture request fails the test instead of hanging the run. Nothing
    // here becomes correct by waiting on wall-clock time.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));

    public CollectorPackageUpdateServiceTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_state);
        Directory.CreateDirectory(_scratch);
        _server = StaticRegistryFixtureServer.Start(_root);
        _fixture = StaticRegistryFixture.PublishVRChat(_server.BaseUri, _root);
        _store = new CollectorInstallationStore(_state);
        _package = LocalCollectorPackage.Load(VRChatSamplePackage.PackageDirectory);
        _statePath = Path.Combine(_state, "collector-runtime.json");
        _runtime = CollectorRuntime.Open(_statePath, new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        _instanceId = _runtime.CreateInstance(
            _package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone())).CollectorInstanceId;
    }

    public void Dispose()
    {
        _runtime.Dispose();
        _timeout.Dispose();
        _httpClient.Dispose();
        _fixture.Dispose();
        _server.Dispose();
        foreach (var directory in new[] { _root, _state, _scratch })
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private CollectorPackageUpdateService Service(bool withRegistry = true) => new(
        _runtime,
        _store,
        withRegistry
            ? new CollectorPackageInstaller(
                new StaticCollectorRegistryClient(_httpClient, _server.BaseUri),
                _store)
            : null);

    /// <summary>Reopens the Runtime so a later read comes from disk, not from memory.</summary>
    private CollectorPackageUpdateStatus Restart()
    {
        _runtime.Dispose();
        _runtime = CollectorRuntime.Open(_statePath, new RecordingSegmentSink());
        return Service().Current(_instanceId);
    }

    private CollectorPackageReference Reference(string? artifactSha256 = null) =>
        new(
            _fixture.PackageId,
            _fixture.Version,
            artifactSha256 ?? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_fixture.ArtifactPath))));

    private int ArtifactRequestCount() =>
        _server.RequestCounts.TryGetValue(_fixture.ArtifactUrl.AbsolutePath, out var count) ? count : 0;

    private int IndexRequestCount() => _server.RequestCounts.TryGetValue(
        $"{StaticRegistryFixtureServer.RegistryPathPrefix}packages/{_fixture.PackageId}/current.json",
        out var count) ? count : 0;

    /// <summary>Publishes the same declared Version with different artifact bytes.</summary>
    private byte[] RepackWithAnExtraFile()
    {
        var copy = Path.Combine(_scratch, $"package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(copy);
        foreach (var directory in Directory.EnumerateDirectories(
                     VRChatSamplePackage.PackageDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                copy,
                Path.GetRelativePath(VRChatSamplePackage.PackageDirectory, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(
                     VRChatSamplePackage.PackageDirectory, "*", SearchOption.AllDirectories))
        {
            File.Copy(
                file,
                Path.Combine(copy, Path.GetRelativePath(VRChatSamplePackage.PackageDirectory, file)),
                overwrite: true);
        }
        File.WriteAllText(Path.Combine(copy, "release-note.txt"), "same Version, different content");
        return CollectorPackageArchive.Pack(copy);
    }

    [Fact]
    public void Current_BeforeAnyCheck_ReportsTheInstancePackageAndNothingElse()
    {
        var status = Service().Current(_instanceId);

        Assert.Equal(_instanceId, status.CollectorInstanceId);
        Assert.Equal(_package.Manifest.PackageId, status.PackageId);
        Assert.Equal(_package.Manifest.Version, status.CurrentVersion);
        Assert.Equal(_package.PackageContentHash, status.CurrentPackageContentHash);
        Assert.Null(status.LastKnownGood);
        Assert.Null(status.InstalledCandidate);
        Assert.Null(status.ApprovedCandidate);
        Assert.Null(status.RegistryCurrent);
        Assert.Null(status.RegistryCheckedAt);
        Assert.Null(status.LastFailure);
    }

    [Fact]
    public void Current_UnknownCollectorInstance_Throws() =>
        Assert.Throws<KeyNotFoundException>(() => Service().Current(Guid.CreateVersion7()));

    [Fact]
    public async Task CheckNow_InstallsTheRegistryCandidateAndReportsIt()
    {
        var status = await Service().CheckNowAsync(_instanceId, _timeout.Token);

        Assert.Null(status.LastFailure);
        Assert.Equal(Reference(), status.RegistryCurrent);
        Assert.Equal(Reference(), status.InstalledCandidate);
        Assert.NotNull(status.RegistryCheckedAt);
        Assert.True(_store.OpenInstallation(Reference()).IsSuccess);
        // Discovery and download are not approval, and they are not a version switch either.
        Assert.Null(status.ApprovedCandidate);
        Assert.Equal(_package.Manifest.Version, status.CurrentVersion);
        Assert.Null(status.LastKnownGood);
    }

    [Fact]
    public async Task CheckNow_Twice_IsIdempotentAndDoesNotDownloadTheArtifactAgain()
    {
        var service = Service();
        var first = await service.CheckNowAsync(_instanceId, _timeout.Token);
        var installation = _store.OpenInstallation(Reference()).Require();
        var marker = File.ReadAllBytes(
            Path.Combine(installation.Directory, CollectorInstallationMarker.FileName));

        var second = await service.CheckNowAsync(_instanceId, _timeout.Token);

        Assert.Equal(first.InstalledCandidate, second.InstalledCandidate);
        Assert.Null(second.LastFailure);
        Assert.Equal(1, ArtifactRequestCount());
        Assert.Equal(2, IndexRequestCount());
        var reopened = _store.OpenInstallation(Reference());
        Assert.True(reopened.IsSuccess, reopened.Detail);
        Assert.Equal(installation.Directory, reopened.Require().Directory);
        Assert.Equal(
            marker,
            File.ReadAllBytes(Path.Combine(installation.Directory, CollectorInstallationMarker.FileName)));
    }

    [Fact]
    public async Task CheckNow_WithoutAConfiguredRegistry_RecordsAStructuredLastFailure()
    {
        var status = await Service(withRegistry: false).CheckNowAsync(_instanceId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.RegistryNotConfigured, status.LastFailure!.Reason);
        Assert.Null(status.RegistryCurrent);
        Assert.Null(status.InstalledCandidate);
    }

    [Fact]
    public async Task CheckNow_RegistryUnreachable_RecordsAStructuredLastFailure()
    {
        var service = new CollectorPackageUpdateService(
            _runtime,
            _store,
            new CollectorPackageInstaller(
                new StaticCollectorRegistryClient(
                    _httpClient,
                    new Uri("http://127.0.0.1:1/collector-registry/v1/")),
                _store));

        var status = await service.CheckNowAsync(_instanceId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.RequestFailed, status.LastFailure!.Reason);
        Assert.False(string.IsNullOrWhiteSpace(status.LastFailure.Message));
        Assert.Null(status.RegistryCurrent);
        Assert.Null(status.RegistryCheckedAt);
    }

    [Fact]
    public async Task CheckNow_MalformedIndex_RecordsAStructuredLastFailure()
    {
        _fixture.WriteIndexText("{ not json");

        var status = await Service().CheckNowAsync(_instanceId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.MalformedJson, status.LastFailure!.Reason);
        Assert.Null(status.RegistryCurrent);
        Assert.Equal(0, ArtifactRequestCount());
    }

    [Fact]
    public async Task CheckNow_WrongDeclaredLength_RecordsAStructuredLastFailureAndInstallsNothing()
    {
        _fixture.MutateIndex(index => index["artifact"]!["length"] = 12);

        var status = await Service().CheckNowAsync(_instanceId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.ArtifactLengthMismatch, status.LastFailure!.Reason);
        Assert.Null(status.InstalledCandidate);
        Assert.False(_store.OpenInstallation(Reference()).IsSuccess);
    }

    [Fact]
    public async Task CheckNow_WrongDeclaredHash_RecordsAStructuredLastFailureAndInstallsNothing()
    {
        var expected = Reference();
        _fixture.FlipArtifactByte();

        var status = await Service().CheckNowAsync(_instanceId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.ArtifactHashMismatch, status.LastFailure!.Reason);
        Assert.Null(status.InstalledCandidate);
        Assert.False(_store.OpenInstallation(expected).IsSuccess);
    }

    [Fact]
    public async Task CheckNow_CorruptArchive_RecordsAStructuredLastFailureAndInstallsNothing()
    {
        _fixture.PublishCorruptPackage();

        var status = await Service().CheckNowAsync(_instanceId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.MalformedArchive, status.LastFailure!.Reason);
        Assert.Null(status.InstalledCandidate);
        // The Registry read itself succeeded, so Current still reports what it advertised.
        Assert.Equal(Reference(), status.RegistryCurrent);
    }

    [Fact]
    public async Task CheckNow_Failure_DoesNotRetryOrScheduleAnythingBehindTheCaller()
    {
        _fixture.FlipArtifactByte();
        var service = Service();

        var status = await service.CheckNowAsync(_instanceId, _timeout.Token);
        var indexReads = IndexRequestCount();
        var downloads = ArtifactRequestCount();
        // Anything the caller can do after the failure that is not another CheckNow must not talk
        // to the Registry: there is no timer, no backoff and no background attempt.
        for (var read = 0; read < 3; read++)
            Assert.Equal(status.LastFailure!.Reason, service.Current(_instanceId).LastFailure!.Reason);

        Assert.Equal(1, indexReads);
        Assert.Equal(1, downloads);
        Assert.Equal(indexReads, IndexRequestCount());
        Assert.Equal(downloads, ArtifactRequestCount());
    }

    [Fact]
    public async Task CheckNow_Failure_KeepsTheExistingInstallationAndTheApprovedCandidate()
    {
        var service = Service();
        await service.CheckNowAsync(_instanceId, _timeout.Token);
        var installed = Reference();
        Assert.True(service.Approve(_instanceId, installed).IsSuccess);

        File.WriteAllBytes(_fixture.ArtifactPath, RepackWithAnExtraFile());
        _fixture.RepublishIndex();
        _fixture.FlipArtifactByte();
        var status = await service.CheckNowAsync(_instanceId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.ArtifactHashMismatch, status.LastFailure!.Reason);
        Assert.Equal(installed, status.ApprovedCandidate);
        Assert.Equal(installed, status.InstalledCandidate);
        var reopened = _store.OpenInstallation(installed);
        Assert.True(reopened.IsSuccess, reopened.Detail);
    }

    [Fact]
    public async Task CheckNow_ClearsTheLastFailureOnlyWhenACheckSucceeds()
    {
        var service = Service();
        _fixture.FlipArtifactByte();
        Assert.NotNull((await service.CheckNowAsync(_instanceId, _timeout.Token)).LastFailure);

        // Reading Current, restarting and approving are not a successful check.
        Assert.NotNull(service.Current(_instanceId).LastFailure);
        Assert.NotNull(Restart().LastFailure);

        _fixture.RepublishIndex();
        var status = await Service().CheckNowAsync(_instanceId, _timeout.Token);

        Assert.Null(status.LastFailure);
        Assert.Equal(Reference(), status.InstalledCandidate);
    }

    [Fact]
    public async Task Approve_TheInstalledExactCandidate_IsAccepted()
    {
        var service = Service();
        await service.CheckNowAsync(_instanceId, _timeout.Token);

        var result = service.Approve(_instanceId, Reference());

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal(Reference(), result.Require().ApprovedCandidate);
    }

    [Fact]
    public async Task Approve_ACandidateThatIsNoLongerRegistryCurrent_IsAccepted()
    {
        var service = Service();
        await service.CheckNowAsync(_instanceId, _timeout.Token);
        var superseded = Reference();

        File.WriteAllBytes(_fixture.ArtifactPath, RepackWithAnExtraFile());
        _fixture.RepublishIndex();
        var checkedStatus = await service.CheckNowAsync(_instanceId, _timeout.Token);
        Assert.NotEqual(superseded, checkedStatus.RegistryCurrent);

        var result = service.Approve(_instanceId, superseded);

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal(superseded, result.Require().ApprovedCandidate);
        // Approval never re-resolves latest: the Registry candidate is reported, not substituted.
        Assert.Equal(Reference(), result.Require().RegistryCurrent);
    }

    [Fact]
    public async Task Approve_AnotherVersion_IsRejected()
    {
        var service = Service();
        await service.CheckNowAsync(_instanceId, _timeout.Token);

        var result = service.Approve(
            _instanceId,
            Reference() with { Version = "99.0.0" });

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMissing, result.Reason);
        Assert.Null(service.Current(_instanceId).ApprovedCandidate);
    }

    [Fact]
    public async Task Approve_AnotherArtifactHash_IsRejected()
    {
        var service = Service();
        await service.CheckNowAsync(_instanceId, _timeout.Token);

        var result = service.Approve(_instanceId, Reference(new string('a', 64)));

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMissing, result.Reason);
        Assert.Null(service.Current(_instanceId).ApprovedCandidate);
    }

    [Fact]
    public async Task Approve_AnotherPackageId_IsRejected()
    {
        var service = Service();
        await service.CheckNowAsync(_instanceId, _timeout.Token);

        var result = service.Approve(
            _instanceId,
            Reference() with { PackageId = "heartbeat.collector.other" });

        Assert.Equal(CollectorRegistryFailureReason.CollectorInstancePackageMismatch, result.Reason);
        Assert.Null(service.Current(_instanceId).ApprovedCandidate);
    }

    [Fact]
    public void Approve_ACandidateThatWasNeverInstalled_IsRejected()
    {
        var result = Service().Approve(_instanceId, Reference());

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMissing, result.Reason);
        Assert.Null(Service().Current(_instanceId).ApprovedCandidate);
    }

    [Fact]
    public void Approve_AMalformedExactReference_IsRejected()
    {
        var result = Service().Approve(
            _instanceId,
            new CollectorPackageReference(_fixture.PackageId, _fixture.Version, "not-a-hash"));

        Assert.Equal(CollectorRegistryFailureReason.InvalidArtifactSha256, result.Reason);
    }

    [Fact]
    public async Task Approve_AnUnfinishedInstallationDirectory_IsRejected()
    {
        var service = Service();
        await service.CheckNowAsync(_instanceId, _timeout.Token);
        var reference = Reference();
        File.Delete(Path.Combine(
            _store.DirectoryFor(reference),
            CollectorInstallationMarker.FileName));

        var result = service.Approve(_instanceId, reference);

        Assert.Equal(CollectorRegistryFailureReason.InstallationMarkerMissing, result.Reason);
    }

    [Fact]
    public void Approve_UnknownCollectorInstance_Throws() =>
        Assert.Throws<KeyNotFoundException>(() => Service().Approve(Guid.CreateVersion7(), Reference()));

    [Fact]
    public async Task Approve_DoesNotSwitchTheCurrentPackageOrTheLastKnownGood()
    {
        var service = Service();
        await service.CheckNowAsync(_instanceId, _timeout.Token);

        var status = service.Approve(_instanceId, Reference()).Require();

        Assert.Equal(_package.Manifest.Version, status.CurrentVersion);
        Assert.Equal(_package.PackageContentHash, status.CurrentPackageContentHash);
        Assert.Null(status.LastKnownGood);
        Assert.Equal(_package.Manifest.Version, _runtime.GetInstance(_instanceId).PackageVersion);
    }

    [Fact]
    public async Task Approve_TheSameCandidateConcurrently_ConvergesOnOneApproval()
    {
        var service = Service();
        await service.CheckNowAsync(_instanceId, _timeout.Token);
        var reference = Reference();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => service.Approve(_instanceId, reference))));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Detail));
        Assert.Equal(reference, Restart().ApprovedCandidate);
    }

    [Fact]
    public async Task Approve_SurvivesARestartOfTheHub()
    {
        var service = Service();
        await service.CheckNowAsync(_instanceId, _timeout.Token);
        service.Approve(_instanceId, Reference()).Require();

        var restored = Restart();

        Assert.Equal(Reference(), restored.ApprovedCandidate);
        Assert.Equal(Reference(), restored.InstalledCandidate);
        Assert.Equal(Reference(), restored.RegistryCurrent);
        Assert.NotNull(restored.RegistryCheckedAt);
    }

    [Fact]
    public async Task CheckNow_DoesNotTouchDesiredStateOrTheInstanceSpec()
    {
        var before = _runtime.GetInstance(_instanceId);
        _fixture.FlipArtifactByte();

        await Service().CheckNowAsync(_instanceId, _timeout.Token);

        var after = _runtime.GetInstance(_instanceId);
        Assert.Equal(before.Spec.SpecRevision, after.Spec.SpecRevision);
        Assert.Equal(before.PackageVersion, after.PackageVersion);
        Assert.Equal(before.PackageContentHash, after.PackageContentHash);
        Assert.Equal(before.LastKnownGoodPackage, after.LastKnownGoodPackage);
    }

    private sealed class RecordingSegmentSink : ISegmentSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) { }
    }
}
