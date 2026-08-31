using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Heartbeat.Collection.CollectorRelease;
using Heartbeat.Collection.Hub.Collectors.Delivery;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Delivery;

/// <summary>
/// Installing an exact Registry candidate end to end against a real loopback Registry: read
/// <c>current.json</c>, download the artifact, verify length and SHA-256, unpack safely into a
/// directory owned by that exact Version and content hash, re-verify it through the existing
/// Collector Package loader and only then write the completion marker.
///
/// The fault injection is the point. Every failure has to leave the previous Collector Installation
/// untouched, publish nothing new, keep no pending directory, and record one structured last error
/// without retrying by itself. Re-running the same exact candidate must be idempotent, and two
/// concurrent installs of the same candidate must converge on one Installation.
/// </summary>
public sealed class CollectorPackageInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-installer-registry-{Guid.NewGuid():N}");
    private readonly string _state = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-installer-state-{Guid.NewGuid():N}");
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-installer-scratch-{Guid.NewGuid():N}");
    private readonly StaticRegistryFixtureServer _server;
    private readonly StaticRegistryFixture _fixture;
    private readonly HttpClient _httpClient = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly CollectorInstallationStore _store;

    // A bounded token so a hung fixture request fails the test instead of hanging the run. No test
    // here waits on wall-clock time to become correct.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));

    public CollectorPackageInstallerTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_state);
        Directory.CreateDirectory(_scratch);
        _server = StaticRegistryFixtureServer.Start(_root);
        _fixture = StaticRegistryFixture.PublishVRChat(_server.BaseUri, _root);
        _store = new CollectorInstallationStore(_state);
    }

    public void Dispose()
    {
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

    private CollectorPackageInstaller Installer(CollectorPackageArchiveLimits? limits = null) =>
        new(new StaticCollectorRegistryClient(_httpClient, _server.BaseUri), _store, limits);

    private Task<CollectorRegistryResult<CollectorInstallation>> InstallAsync(
        CollectorPackageInstaller? installer = null,
        CancellationToken? cancellationToken = null) =>
        (installer ?? Installer()).InstallCurrentAsync(
            _fixture.PackageId,
            cancellationToken ?? _timeout.Token);

    private async Task<CollectorRegistryIndex> CurrentAsync()
    {
        var index = await new StaticCollectorRegistryClient(_httpClient, _server.BaseUri)
            .GetCurrentAsync(_fixture.PackageId, _timeout.Token);
        Assert.True(index.IsSuccess, index.Detail);
        return index.Require();
    }

    private CollectorPackageReference Reference(string? artifactSha256 = null) =>
        new(
            _fixture.PackageId,
            _fixture.Version,
            artifactSha256 ?? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_fixture.ArtifactPath))));

    private int ArtifactRequestCount() => _server.RequestCounts.TryGetValue(
        _fixture.ArtifactUrl.AbsolutePath,
        out var count) ? count : 0;

    private void AssertNothingPending()
    {
        if (Directory.Exists(_store.PendingRoot))
            Assert.Empty(Directory.EnumerateFileSystemEntries(_store.PendingRoot));
    }

    /// <summary>Publishes the same Package Version with different artifact bytes.</summary>
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

    /// <summary>Publishes the current artifact bytes under another Version directory and index.</summary>
    private void PublishUnderVersion(string version)
    {
        var content = File.ReadAllBytes(_fixture.ArtifactPath);
        var directory = Path.Combine(_fixture.PackageDirectory, "versions", version);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, _fixture.ArtifactFileName), content);
        File.WriteAllBytes(
            _fixture.IndexPath,
            CollectorRegistryIndexWriter.Write(new CollectorRegistryIndex(
                CollectorRegistryIndexReader.SupportedSchemaVersion,
                _fixture.PackageId,
                version,
                new CollectorRegistryArtifact(
                    new Uri(
                        _server.BaseUri,
                        $"packages/{_fixture.PackageId}/versions/{version}/{_fixture.ArtifactFileName}"),
                    content.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(content))))));
    }

    private static byte[] ZipSlipArchive()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "collector-manifest.json", "../escape.txt" })
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes("{}"));
            }
        }
        return buffer.ToArray();
    }

    [Fact]
    public async Task Install_ExactCandidate_PublishesAVersionDirectoryWithACompletionMarker()
    {
        var result = await InstallAsync();

        Assert.True(result.IsSuccess, result.Detail);
        var installation = result.Require();
        Assert.Equal(Reference(), installation.Reference);
        Assert.Equal(_store.DirectoryFor(Reference()), installation.Directory);
        Assert.Contains(_fixture.Version, installation.Directory, StringComparison.Ordinal);
        Assert.Contains(Reference().ArtifactSha256, installation.Directory, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(installation.Directory, CollectorInstallationMarker.FileName)));

        var marker = CollectorInstallationMarker.Read(
            File.ReadAllBytes(Path.Combine(installation.Directory, CollectorInstallationMarker.FileName)));
        Assert.NotNull(marker);
        Assert.Equal(CollectorInstallationMarker.CurrentSchemaVersion, marker!.SchemaVersion);
        Assert.Equal(_fixture.PackageId, marker.PackageId);
        Assert.Equal(_fixture.Version, marker.Version);
        Assert.Equal(Reference().ArtifactSha256, marker.ArtifactSha256);
        Assert.Equal(installation.Package.PackageContentHash, marker.PackageContentHash);
        Assert.True(_store.OpenInstallation(Reference()).IsSuccess);
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_SameExactCandidateTwice_IsIdempotentAndDoesNotDownloadAgain()
    {
        var first = await InstallAsync();
        Assert.True(first.IsSuccess, first.Detail);
        Assert.Equal(1, ArtifactRequestCount());

        // Removing the published artifact proves the second install reuses the Installation instead
        // of fetching the bytes again.
        File.Delete(_fixture.ArtifactPath);
        var second = await InstallAsync();

        Assert.True(second.IsSuccess, second.Detail);
        Assert.Equal(first.Require().Directory, second.Require().Directory);
        Assert.Equal(1, ArtifactRequestCount());
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_SameVersionDifferentContent_UsesADifferentDirectory()
    {
        var installed = (await InstallAsync()).Require();
        var firstReference = Reference();

        File.WriteAllBytes(_fixture.ArtifactPath, RepackWithAnExtraFile());
        _fixture.RepublishIndex();
        var second = await InstallAsync();

        Assert.True(second.IsSuccess, second.Detail);
        Assert.NotEqual(firstReference.ArtifactSha256, second.Require().Reference.ArtifactSha256);
        Assert.NotEqual(installed.Directory, second.Require().Directory);
        Assert.Equal(_fixture.Version, second.Require().Reference.Version);
        // The earlier content stays a real Installation: a new candidate never displaces it.
        Assert.True(_store.OpenInstallation(firstReference).IsSuccess);
    }

    [Fact]
    public async Task Install_DeclaredLengthDoesNotMatch_InstallsNothing()
    {
        _fixture.MutateIndex(index => index["artifact"]!["length"] = 12);

        var result = await InstallAsync();

        Assert.Equal(CollectorRegistryFailureReason.ArtifactLengthMismatch, result.Reason);
        Assert.False(_store.OpenInstallation(Reference()).IsSuccess);
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_TruncatedDownload_InstallsNothing()
    {
        _fixture.TruncateArtifact();

        var result = await InstallAsync();

        Assert.Equal(CollectorRegistryFailureReason.ArtifactLengthMismatch, result.Reason);
        Assert.False(Directory.Exists(_store.PackagesRoot));
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_DeclaredHashDoesNotMatch_InstallsNothing()
    {
        var index = await CurrentAsync();
        _fixture.FlipArtifactByte();

        var result = await Installer().InstallAsync(index, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.ArtifactHashMismatch, result.Reason);
        Assert.False(_store.OpenInstallation(Reference(index.Artifact.Sha256)).IsSuccess);
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_CorruptArchive_FailsWithMalformedArchive()
    {
        _fixture.PublishCorruptPackage();

        var result = await InstallAsync();

        Assert.Equal(CollectorRegistryFailureReason.MalformedArchive, result.Reason);
        Assert.False(Directory.Exists(_store.PackagesRoot));
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_ZipSlipArchive_FailsWithUnsafeArchiveEntry()
    {
        _fixture.PublishArtifact(ZipSlipArchive());

        var result = await InstallAsync();

        Assert.Equal(CollectorRegistryFailureReason.UnsafeArchiveEntry, result.Reason);
        Assert.Empty(Directory.EnumerateFiles(_store.InstallRoot, "escape.txt", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(_store.PackagesRoot));
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_ArchiveOverTheEntryLimit_FailsWithArchiveLimitExceeded()
    {
        var result = await InstallAsync(Installer(new CollectorPackageArchiveLimits { MaxEntryCount = 2 }));

        Assert.Equal(CollectorRegistryFailureReason.ArchiveLimitExceeded, result.Reason);
        Assert.False(Directory.Exists(_store.PackagesRoot));
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_ArchiveOverTheSizeLimit_FailsWithArchiveLimitExceeded()
    {
        var result = await InstallAsync(Installer(new CollectorPackageArchiveLimits { MaxUncompressedBytes = 4096 }));

        Assert.Equal(CollectorRegistryFailureReason.ArchiveLimitExceeded, result.Reason);
        Assert.False(Directory.Exists(_store.PackagesRoot));
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_ArchiveWithoutALoadablePackage_FailsPackageValidation()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("collector-manifest.json", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes("{ \"manifestVersion\": 1 }"));
        }
        _fixture.PublishArtifact(buffer.ToArray());

        var result = await InstallAsync();

        Assert.Equal(CollectorRegistryFailureReason.PackageValidationFailed, result.Reason);
        Assert.False(Directory.Exists(_store.PackagesRoot));
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_PackageDeclaringAnotherVersion_FailsWithManifestMismatch()
    {
        PublishUnderVersion("9.9.9");

        var result = await InstallAsync();

        Assert.Equal(CollectorRegistryFailureReason.PackageManifestMismatch, result.Reason);
        Assert.False(Directory.Exists(_store.PackagesRoot));
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_RegistryUnreachable_FailsWithRequestFailed()
    {
        // A loopback port nothing is listening on, so the connection is refused immediately instead
        // of the test waiting for a timeout.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var unreachable = new CollectorPackageInstaller(
            new StaticCollectorRegistryClient(
                _httpClient,
                new Uri($"http://127.0.0.1:{port}/collector-registry/v1/")),
            _store);

        var result = await unreachable.InstallCurrentAsync(_fixture.PackageId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.RequestFailed, result.Reason);
        Assert.Equal(
            CollectorRegistryFailureReason.RequestFailed,
            unreachable.LastFailure(_fixture.PackageId)!.Reason);
        Assert.False(Directory.Exists(_store.PackagesRoot));
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_Cancelled_FailsWithCancelledAndInstallsNothing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await InstallAsync(cancellationToken: cancellation.Token);

        Assert.Equal(CollectorRegistryFailureReason.Cancelled, result.Reason);
        Assert.False(Directory.Exists(_store.PackagesRoot));
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_Failure_RecordsOneStructuredLastErrorAndDoesNotRetry()
    {
        var installer = Installer();
        var index = await CurrentAsync();
        _fixture.FlipArtifactByte();

        var result = await installer.InstallCurrentAsync(_fixture.PackageId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.ArtifactHashMismatch, result.Reason);
        var failure = installer.LastFailure(_fixture.PackageId);
        Assert.NotNull(failure);
        Assert.Equal(CollectorRegistryFailureReason.ArtifactHashMismatch, failure!.Reason);
        Assert.Equal(_fixture.PackageId, failure.PackageId);
        Assert.Equal(Reference(index.Artifact.Sha256), failure.Reference);
        Assert.False(string.IsNullOrWhiteSpace(failure.Detail));
        // One attempt, one download: the installer never retries behind the caller's back.
        Assert.Equal(1, ArtifactRequestCount());
    }

    [Fact]
    public async Task Install_FailureAfterASuccess_KeepsTheInstalledCandidateAndItsLastError()
    {
        var installer = Installer();
        var installed = (await installer.InstallCurrentAsync(_fixture.PackageId, _timeout.Token)).Require();
        Assert.Null(installer.LastFailure(_fixture.PackageId));

        File.WriteAllBytes(_fixture.ArtifactPath, RepackWithAnExtraFile());
        _fixture.RepublishIndex();
        _fixture.FlipArtifactByte();
        var failed = await installer.InstallCurrentAsync(_fixture.PackageId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.ArtifactHashMismatch, failed.Reason);
        Assert.Equal(
            CollectorRegistryFailureReason.ArtifactHashMismatch,
            installer.LastFailure(_fixture.PackageId)!.Reason);
        var reopened = _store.OpenInstallation(installed.Reference);
        Assert.True(reopened.IsSuccess, reopened.Detail);
        Assert.Equal(installed.Directory, reopened.Require().Directory);
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_AfterASuccess_ClearsTheLastError()
    {
        var installer = Installer();
        _fixture.FlipArtifactByte();
        Assert.False((await installer.InstallCurrentAsync(_fixture.PackageId, _timeout.Token)).IsSuccess);
        Assert.NotNull(installer.LastFailure(_fixture.PackageId));

        _fixture.RepublishIndex();
        var result = await installer.InstallCurrentAsync(_fixture.PackageId, _timeout.Token);

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Null(installer.LastFailure(_fixture.PackageId));
    }

    [Fact]
    public async Task Install_TargetDirectoryHoldsUnfinishedContent_RebuildsIt()
    {
        var reference = Reference();
        var directory = _store.DirectoryFor(reference);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "collector-manifest.json"), "{ half written");
        File.WriteAllText(Path.Combine(directory, "leftover.tmp"), "junk");
        Assert.False(_store.OpenInstallation(reference).IsSuccess);

        var result = await InstallAsync();

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal(directory, result.Require().Directory);
        Assert.False(File.Exists(Path.Combine(directory, "leftover.tmp")));
        AssertNothingPending();
    }

    [Fact]
    public async Task Install_TargetDirectoryHoldsAMarkerForAnotherCandidate_RebuildsIt()
    {
        var reference = Reference();
        var directory = _store.DirectoryFor(reference);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, CollectorInstallationMarker.FileName),
            CollectorInstallationMarker.Write(new CollectorInstallationMarker(
                CollectorInstallationMarker.CurrentSchemaVersion,
                reference.PackageId,
                "9.9.9",
                reference.ArtifactSha256,
                "sha256:" + new string('1', 64))));
        Assert.Equal(
            CollectorRegistryFailureReason.InstallationMarkerMismatch,
            _store.OpenInstallation(reference).Reason);

        var result = await InstallAsync();

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal(directory, result.Require().Directory);
        Assert.Equal(_fixture.Version, CollectorInstallationMarker.Read(
            File.ReadAllBytes(Path.Combine(directory, CollectorInstallationMarker.FileName)))!.Version);
    }

    [Fact]
    public async Task Install_TwoConcurrentInstallsOfTheSameCandidate_ConvergeOnOneInstallation()
    {
        var index = await CurrentAsync();
        var installer = Installer();

        var results = await Task.WhenAll(
            installer.InstallAsync(index, _timeout.Token),
            installer.InstallAsync(index, _timeout.Token));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Detail));
        Assert.Equal(results[0].Require().Directory, results[1].Require().Directory);
        var installation = _store.OpenInstallation(Reference());
        Assert.True(installation.IsSuccess, installation.Detail);
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(
            _store.PackagesRoot,
            _fixture.PackageId,
            _fixture.Version)));
        AssertNothingPending();
    }
}
