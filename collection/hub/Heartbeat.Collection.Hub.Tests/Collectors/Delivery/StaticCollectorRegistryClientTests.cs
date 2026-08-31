using System.Security.Cryptography;
using Heartbeat.Collection.Hub.Collectors.Delivery;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Delivery;

/// <summary>
/// Reading the Registry over a real loopback static server: the Runtime fetches
/// <c>current.json</c>, downloads the exact VRChat artifact and accepts it only when the bytes match
/// the declared length and SHA-256.
///
/// The fault injection here is the point of the issue: a truncated body, a flipped byte and a
/// redirect that tries to leave the Registry all have to fail with their own stable reason, and a
/// redirect must not become a way around the URL boundary even when the caller's handler follows it.
/// Unpacking is deliberately absent — Collector Installation is a later issue.
/// </summary>
public sealed class StaticCollectorRegistryClientTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-registry-server-{Guid.NewGuid():N}");
    private readonly StaticRegistryFixtureServer _server;
    private readonly StaticRegistryFixture _fixture;
    private readonly HttpClient _httpClient = new(new HttpClientHandler { AllowAutoRedirect = false });

    // A bounded token so a hung fixture request fails the test instead of hanging the run. Nothing
    // in these tests waits on wall-clock time to become correct.
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public StaticCollectorRegistryClientTests()
    {
        Directory.CreateDirectory(_root);
        _server = StaticRegistryFixtureServer.Start(_root);
        _fixture = StaticRegistryFixture.PublishVRChat(_server.BaseUri, _root);
    }

    public void Dispose()
    {
        _timeout.Dispose();
        _httpClient.Dispose();
        _fixture.Dispose();
        _server.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private StaticCollectorRegistryClient Client(HttpClient? httpClient = null) =>
        new(httpClient ?? _httpClient, _server.BaseUri);

    private async Task<CollectorRegistryIndex> CurrentAsync()
    {
        var index = await Client().GetCurrentAsync(_fixture.PackageId, _timeout.Token);
        Assert.True(index.IsSuccess, index.Detail);
        return index.Require();
    }

    [Fact]
    public async Task ReadIndexThenDownload_DeliversTheDeclaredVRChatArtifact()
    {
        var index = await CurrentAsync();
        Assert.Equal(_fixture.Version, index.Version);

        using var destination = new MemoryStream();
        var download = await Client().DownloadArtifactAsync(
            index,
            destination,
            _timeout.Token);

        Assert.True(download.IsSuccess, download.Detail);
        var expected = await File.ReadAllBytesAsync(_fixture.ArtifactPath, _timeout.Token);
        Assert.Equal(expected.LongLength, destination.Length);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(expected)),
            index.Artifact.Sha256);
        Assert.Equal(expected, destination.ToArray());
    }

    [Fact]
    public async Task Download_ShorterThanDeclaredLength_FailsWithLengthMismatch()
    {
        var index = await CurrentAsync();
        _fixture.TruncateArtifact();

        using var destination = new MemoryStream();
        var download = await Client().DownloadArtifactAsync(
            index,
            destination,
            _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.ArtifactLengthMismatch, download.Reason);
    }

    [Fact]
    public async Task Download_LongerThanDeclaredLength_FailsWithLengthMismatch()
    {
        var index = await CurrentAsync();
        await File.AppendAllTextAsync(_fixture.ArtifactPath, "trailing", _timeout.Token);

        using var destination = new MemoryStream();
        var download = await Client().DownloadArtifactAsync(
            index,
            destination,
            _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.ArtifactLengthMismatch, download.Reason);
    }

    [Fact]
    public async Task Download_AlteredBytes_FailsWithHashMismatch()
    {
        var index = await CurrentAsync();
        _fixture.FlipArtifactByte();

        using var destination = new MemoryStream();
        var download = await Client().DownloadArtifactAsync(
            index,
            destination,
            _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.ArtifactHashMismatch, download.Reason);
    }

    [Fact]
    public async Task GetCurrent_MissingIndex_FailsWithRequestFailed()
    {
        File.Delete(_fixture.IndexPath);

        var index = await Client().GetCurrentAsync(_fixture.PackageId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.RequestFailed, index.Reason);
    }

    [Fact]
    public async Task GetCurrent_UnknownSchemaVersionOnTheWire_KeepsTheParserReason()
    {
        _fixture.MutateIndex(index => index["schemaVersion"] = 2);

        var index = await Client().GetCurrentAsync(_fixture.PackageId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.UnsupportedSchemaVersion, index.Reason);
    }

    [Fact]
    public async Task GetCurrent_RedirectToAnotherOrigin_IsRefused()
    {
        _server.Redirects[new Uri(_server.BaseUri, $"packages/{_fixture.PackageId}/current.json").AbsolutePath] =
            "https://evil.example/current.json";

        var index = await Client().GetCurrentAsync(_fixture.PackageId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.RedirectOutsideRegistry, index.Reason);
    }

    [Fact]
    public async Task Download_RedirectOutOfTheVersionDirectory_IsRefused()
    {
        var index = await CurrentAsync();
        _server.Redirects[index.Artifact.Url.AbsolutePath] =
            new Uri(_server.BaseUri, $"packages/{_fixture.PackageId}/current.json").AbsoluteUri;

        using var destination = new MemoryStream();
        var download = await Client().DownloadArtifactAsync(
            index,
            destination,
            _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.RedirectOutsideRegistry, download.Reason);
    }

    [Fact]
    public async Task Download_RedirectFollowedByTheHandler_StillCannotLeaveTheBoundary()
    {
        // Even when the caller hands us a redirect-following handler, the URI we actually landed on
        // is re-checked; otherwise the boundary would be decided by HttpClient configuration.
        var index = await CurrentAsync();
        _server.Redirects[index.Artifact.Url.AbsolutePath] =
            new Uri(_server.BaseUri, $"packages/{_fixture.PackageId}/current.json").AbsoluteUri;
        using var following = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });

        using var destination = new MemoryStream();
        var download = await Client(following).DownloadArtifactAsync(
            index,
            destination,
            _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.RedirectOutsideRegistry, download.Reason);
    }

    [Fact]
    public async Task GetCurrent_PlainHttpRegistryOutsideLoopback_IsRefusedBeforeAnyRequest()
    {
        var client = new StaticCollectorRegistryClient(
            _httpClient,
            new Uri("http://registry.example/collector-registry/v1/"));

        var index = await client.GetCurrentAsync(_fixture.PackageId, _timeout.Token);

        Assert.Equal(CollectorRegistryFailureReason.InvalidRegistryBaseUri, index.Reason);
    }
}
