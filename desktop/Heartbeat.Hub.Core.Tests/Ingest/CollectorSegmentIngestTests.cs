using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Hub.Core.Collectors;
using Heartbeat.Hub.Core.Ingest;
using Heartbeat.Hub.Core.Segments;
using Heartbeat.Hub.Core.Time;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Hub.Core.Tests.Ingest;

public class CollectorSegmentIngestTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);
    }

    private sealed class Registry : ICollectorRegistry
    {
        public IReadOnlyDictionary<string, CollectorRegistration> Snapshot { get; } =
            new Dictionary<string, CollectorRegistration>();
        public CollectorRegistration Touch(string source, int? flushPeriodMs = null) =>
            new(true, flushPeriodMs, null, null);
        public void Discover(IEnumerable<string> sources) { }
        public void StoreDeclaration(string source, string declarationJson, int version) { }
    }

    private sealed class Resolver : ICollectorAppHintResolver
    {
        public CollectorAppHintResolution Resolve(string appHint) => appHint switch
        {
            "chrome" => CollectorAppHintResolution.Resolved("win:chrome"),
            "ambiguous" => CollectorAppHintResolution.Ambiguous,
            _ => CollectorAppHintResolution.Unknown
        };
    }

    private readonly SegmentIngestService _ingest = new(new FakeClock());

    private SegmentIngestRequestHandler CreateHandler() => new(_ingest, new Registry(), new Resolver());

    private static Stream Body(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    private static string SegmentJson(string? appHintProperty = "\"appHint\":\"chrome\",") => $$"""
        {
          "id":"{{Guid.Parse("0198a827-d480-7000-8000-000000000001")}}",
          "source":"browser",
          "identityKey":"https://example.com/path",
          {{appHintProperty}}
          "title":"Example",
          "startTime":"2026-08-11T09:55:00Z",
          "endTime":"2026-08-11T09:57:00Z",
          "attributes":{"url":"https://example.com/path?q=1","windowId":7}
        }
        """;

    [Fact]
    public async Task KnownHint_IsResolvedBeforeBuffering_WithoutChangingEvidence()
    {
        var response = await CreateHandler().HandleAsync(
            "POST", "/v1/segments", Body($$"""{"segments":[{{SegmentJson()}}]}"""));

        Assert.Equal(200, response.StatusCode);
        var segment = Assert.Single(_ingest.GetAndClearSegments());
        Assert.Equal("win:chrome", segment.AppIdentityKey);
        Assert.Equal("browser", segment.Source);
        Assert.Equal("https://example.com/path", segment.IdentityKey);
        Assert.Equal("Example", segment.Title);
        Assert.Equal("https://example.com/path?q=1", segment.Attributes?.GetProperty("url").GetString());
        Assert.Equal(7, segment.Attributes?.GetProperty("windowId").GetInt32());
        Assert.Null(segment.AppName);
        Assert.Null(segment.AppDisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("ambiguous")]
    public async Task MissingUnknownOrAmbiguousHint_PreservesSegmentWithoutAppAssociation(string? hint)
    {
        var property = hint is null ? null : $"\"appHint\":\"{hint}\",";

        var response = await CreateHandler().HandleAsync(
            "POST", "/v1/segments", Body($$"""{"segments":[{{SegmentJson(property)}}]}"""));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("""{"accepted":1}""", response.Body);
        var segment = Assert.Single(_ingest.GetAndClearSegments());
        Assert.Null(segment.AppIdentityKey);
        Assert.Equal("https://example.com/path", segment.IdentityKey);
        Assert.Equal("https://example.com/path?q=1", segment.Attributes?.GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("appIdentityKey", "win:chrome")]
    [InlineData("appDisplayName", "Google Chrome")]
    public async Task AnalyticsIdentityFields_AreRejectedAtLoopbackBoundary(string field, string value)
    {
        var injected = $"\"appHint\":\"chrome\",\"{field}\":\"{value}\",";

        var response = await CreateHandler().HandleAsync(
            "POST", "/v1/segments", Body($$"""{"segments":[{{SegmentJson(injected)}}]}"""));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("invalid JSON", response.Body);
        Assert.Empty(_ingest.GetAndClearSegments());
    }

    [Fact]
    public async Task LegacyAppNameOnly_IsAcceptedButIgnored_ToProtectOldCollectorQueue()
    {
        var legacy = "\"appName\":\"msedge\",";

        var response = await CreateHandler().HandleAsync(
            "POST", "/v1/segments", Body($$"""{"segments":[{{SegmentJson(legacy)}}]}"""));

        Assert.Equal(200, response.StatusCode);
        var segment = Assert.Single(_ingest.GetAndClearSegments());
        Assert.Null(segment.AppIdentityKey);
        Assert.Null(segment.AppName);
        Assert.Null(segment.AppDisplayName);
        Assert.Equal("https://example.com/path", segment.IdentityKey);
    }

    [Fact]
    public async Task AppHintAndLegacyAppNameTogether_AreRejectedAsAmbiguous()
    {
        var mixed = "\"appHint\":\"edge\",\"appName\":\"msedge\",";

        var response = await CreateHandler().HandleAsync(
            "POST", "/v1/segments", Body($$"""{"segments":[{{SegmentJson(mixed)}}]}"""));

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("cannot both", response.Body);
        Assert.Empty(_ingest.GetAndClearSegments());
    }

    [Fact]
    public async Task ResolvedSegment_StrictAnalyticsPayloadContainsIdentityButNotLoopbackHint()
    {
        await CreateHandler().HandleAsync(
            "POST", "/v1/segments", Body($$"""{"segments":[{{SegmentJson()}}]}"""));
        var segment = Assert.Single(_ingest.GetAndClearSegments());

        var json = JsonSerializer.Serialize(
            new SegmentUploadRequest { Segments = [segment] },
            JsonSerializerOptions.Web);

        Assert.Contains("\"appIdentityKey\":\"win:chrome\"", json);
        Assert.DoesNotContain("appHint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("appName", json, StringComparison.OrdinalIgnoreCase);
    }
}
