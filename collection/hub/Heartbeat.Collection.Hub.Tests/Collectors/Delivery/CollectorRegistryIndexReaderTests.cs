using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Delivery;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Delivery;

/// <summary>
/// The Registry index contract (issue 01 / ADR-047): a per-Package <c>current.json</c> naming one
/// exact candidate. Every rejection has to be a stable reason value, because the Runtime branches on
/// it — the moment callers have to match message text the contract has already leaked.
///
/// The samples come from <see cref="StaticRegistryFixture" />, the same tree the download tests and
/// the release tooling tests consume.
/// </summary>
public sealed class CollectorRegistryIndexReaderTests : IDisposable
{
    private static readonly Uri RegistryBaseUri = new("https://registry.example/collector-registry/v1/");

    private readonly StaticRegistryFixture _fixture = StaticRegistryFixture.PublishVRChat(RegistryBaseUri);

    public void Dispose() => _fixture.Dispose();

    private CollectorRegistryResult<CollectorRegistryIndex> Read(string? packageId = null) =>
        CollectorRegistryIndexReader.Read(
            File.ReadAllBytes(_fixture.IndexPath),
            RegistryBaseUri,
            packageId ?? _fixture.PackageId);

    private CollectorRegistryFailureReason? ReasonFor(Action<JsonObject> mutate)
    {
        _fixture.MutateIndex(mutate);
        return Read().Reason;
    }

    [Fact]
    public void Read_PublishedIndex_ResolvesTheExactCandidate()
    {
        var result = Read();

        Assert.True(result.IsSuccess, result.Detail);
        var index = result.Require();
        Assert.Equal(1, index.SchemaVersion);
        Assert.Equal(_fixture.PackageId, index.PackageId);
        Assert.Equal(_fixture.Version, index.Version);
        Assert.Equal(_fixture.ArtifactUrl, index.Artifact.Url);
        Assert.Equal(new FileInfo(_fixture.ArtifactPath).Length, index.Artifact.Length);
        Assert.Matches("^[0-9a-f]{64}$", index.Artifact.Sha256);
    }

    [Fact]
    public void Write_ThenRead_RoundTripsTheSameCandidate()
    {
        // The publisher and the Runtime must not drift into two spellings of the same document.
        var index = Read().Require();

        var reread = CollectorRegistryIndexReader.Read(
            CollectorRegistryIndexWriter.Write(index),
            RegistryBaseUri,
            _fixture.PackageId);

        Assert.Equal(index, reread.Require());
    }

    [Fact]
    public void Read_MissingRequiredField_ReportsMissingField() =>
        Assert.Equal(
            CollectorRegistryFailureReason.MissingField,
            ReasonFor(index => index.Remove("version")));

    [Fact]
    public void Read_MissingArtifactField_ReportsMissingField() =>
        Assert.Equal(
            CollectorRegistryFailureReason.MissingField,
            ReasonFor(index => index["artifact"]!.AsObject().Remove("sha256")));

    [Fact]
    public void Read_UnknownSchemaVersion_IsRefusedInsteadOfGuessed() =>
        Assert.Equal(
            CollectorRegistryFailureReason.UnsupportedSchemaVersion,
            ReasonFor(index => index["schemaVersion"] = 2));

    [Fact]
    public void Read_ExtraField_RequiresANewSchemaVersion() =>
        // channel / signature / release notes are deferred scope: they may not appear under v1.
        Assert.Equal(
            CollectorRegistryFailureReason.UnknownField,
            ReasonFor(index => index["channel"] = "stable"));

    [Fact]
    public void Read_DuplicateProperty_IsAmbiguousAndRejected()
    {
        var text = _fixture.ReadIndexText().TrimEnd();
        _fixture.WriteIndexText(text[..^1].TrimEnd() + $",\n  \"version\": \"9.9.9\"\n}}\n");

        Assert.Equal(CollectorRegistryFailureReason.DuplicateJsonProperty, Read().Reason);
    }

    [Fact]
    public void Read_MalformedJson_ReportsMalformedJson()
    {
        _fixture.WriteIndexText("{ not json");

        Assert.Equal(CollectorRegistryFailureReason.MalformedJson, Read().Reason);
    }

    [Fact]
    public void Read_IndexForAnotherPackage_ReportsPackageIdMismatch() =>
        Assert.Equal(
            CollectorRegistryFailureReason.PackageIdMismatch,
            Read("heartbeat.collector.other").Reason);

    [Fact]
    public void Read_MalformedPackageId_ReportsInvalidPackageId() =>
        Assert.Equal(
            CollectorRegistryFailureReason.InvalidPackageId,
            ReasonFor(index => index["packageId"] = "Heartbeat/VRChat"));

    [Theory]
    [InlineData("0.1")]
    [InlineData("v0.1.0")]
    [InlineData("01.0.0")]
    public void Read_NonSemVerVersion_ReportsInvalidVersion(string version) =>
        Assert.Equal(
            CollectorRegistryFailureReason.InvalidVersion,
            ReasonFor(index => index["version"] = version));

    [Fact]
    public void Read_RelativeArtifactUrl_ReportsInvalidArtifactUrl() =>
        Assert.Equal(
            CollectorRegistryFailureReason.InvalidArtifactUrl,
            ReasonFor(index => index["artifact"]!["url"] =
                $"packages/{_fixture.PackageId}/versions/{_fixture.Version}/vrchat.zip"));

    [Fact]
    public void Read_NonCanonicalArtifactUrl_IsRejectedBeforeNormalizationCanHideTraversal() =>
        // "…/versions/0.1.0/../../x/vrchat.zip" normalizes into the tree; the reader refuses to
        // accept a URL whose meaning only appears after normalization.
        Assert.Equal(
            CollectorRegistryFailureReason.InvalidArtifactUrl,
            ReasonFor(index => index["artifact"]!["url"] =
                $"https://registry.example/collector-registry/v1/packages/{_fixture.PackageId}" +
                $"/versions/{_fixture.Version}/../../{_fixture.Version}/vrchat.zip"));

    [Theory]
    [InlineData("https://evil.example/collector-registry/v1/packages/{0}/versions/{1}/vrchat.zip")]
    [InlineData("http://registry.example/collector-registry/v1/packages/{0}/versions/{1}/vrchat.zip")]
    [InlineData("https://registry.example:8443/collector-registry/v1/packages/{0}/versions/{1}/vrchat.zip")]
    [InlineData("https://registry.example/collector-registry/v1/packages/other/versions/{1}/vrchat.zip")]
    [InlineData("https://registry.example/collector-registry/v1/packages/{0}/versions/9.9.9/vrchat.zip")]
    [InlineData("https://registry.example/collector-registry/v1/packages/{0}/versions/{1}/nested/vrchat.zip")]
    [InlineData("https://registry.example/collector-registry/v1/packages/{0}/vrchat.zip")]
    public void Read_ArtifactUrlOutsideTheVersionDirectory_IsRejected(string url) =>
        Assert.Equal(
            CollectorRegistryFailureReason.ArtifactUrlOutsideRegistry,
            ReasonFor(index => index["artifact"]!["url"] =
                string.Format(url, _fixture.PackageId, _fixture.Version)));

    [Fact]
    public void Read_ZeroLength_ReportsInvalidArtifactLength() =>
        Assert.Equal(
            CollectorRegistryFailureReason.InvalidArtifactLength,
            ReasonFor(index => index["artifact"]!["length"] = 0));

    [Fact]
    public void Read_StringLength_ReportsInvalidArtifactLength() =>
        Assert.Equal(
            CollectorRegistryFailureReason.InvalidArtifactLength,
            ReasonFor(index => index["artifact"]!["length"] = "1234"));

    [Theory]
    [InlineData("ABCD")]
    [InlineData("sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000000")]
    public void Read_MalformedSha256_ReportsInvalidArtifactSha256(string sha256) =>
        Assert.Equal(
            CollectorRegistryFailureReason.InvalidArtifactSha256,
            ReasonFor(index => index["artifact"]!["sha256"] = sha256));

    [Fact]
    public void Read_UppercaseSha256_ReportsInvalidArtifactSha256() =>
        Assert.Equal(
            CollectorRegistryFailureReason.InvalidArtifactSha256,
            ReasonFor(index => index["artifact"]!["sha256"] =
                index["artifact"]!["sha256"]!.GetValue<string>().ToUpperInvariant()));

    [Fact]
    public void Read_PlainHttpRegistryOutsideLoopback_IsRefused()
    {
        var result = CollectorRegistryIndexReader.Read(
            File.ReadAllBytes(_fixture.IndexPath),
            new Uri("http://registry.example/collector-registry/v1/"),
            _fixture.PackageId);

        Assert.Equal(CollectorRegistryFailureReason.InvalidRegistryBaseUri, result.Reason);
    }

    [Fact]
    public void Read_PlainHttpLoopbackRegistry_IsAllowedForFixtures()
    {
        // Production stays HTTPS; the same-scheme boundary keeps a local fixture self-consistent.
        var loopback = new Uri("http://127.0.0.1:8080/collector-registry/v1/");
        using var fixture = StaticRegistryFixture.PublishVRChat(loopback);

        var result = CollectorRegistryIndexReader.Read(
            File.ReadAllBytes(fixture.IndexPath),
            loopback,
            fixture.PackageId);

        Assert.True(result.IsSuccess, result.Detail);
    }
}
