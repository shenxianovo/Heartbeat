using System.Text;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public sealed class BrowserExternalHostProtocolHandlerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-browser-binding-{Guid.NewGuid():N}");
    private readonly MutableRegistry _registry = new();
    private readonly ManualTimeProvider _time = new();
    private readonly SegmentIngestService _sink;
    private readonly CollectorRuntime _runtime;
    private readonly BrowserExternalHostProtocolHandler _handler;

    public BrowserExternalHostProtocolHandlerTests()
    {
        Directory.CreateDirectory(_root);
        _sink = new SegmentIngestService(new Clock(_time));
        _runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            _sink,
            appHintResolver: new Resolver());
        _handler = new BrowserExternalHostProtocolHandler(
            _runtime,
            _registry,
            new Device(),
            new BrowserExternalHostBindingOptions(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "BrowserCollectorPackage"),
                TimeSpan.FromSeconds(10)),
            _time);
    }

    [Fact]
    public async Task BrowserTranscript_ConvergesSpecRegistersDeclarationAndProjectsWithoutChangingWirePayload()
    {
        var hello = await Post("/v1/collector-protocol/browser/hello", $$$"""
        {
          "messageId":"{{{Guid.CreateVersion7()}}}",
          "artifactId":"browser.extension",
          "artifactHash":"sha256:0c4d749ffa5d7dc6467c04a66cc054c54433a951b2e00555215d923bf7a14f46",
          "protocolMajors":[1],
          "supportedCapabilities":{"facts.segment":[1],"diagnostics.stream-gap":[1]},
          "appHint":"edge"
        }
        """);
        Assert.Equal(200, hello.StatusCode);
        using var helloJson = JsonDocument.Parse(hello.Body);
        var activationId = helloJson.RootElement.GetProperty("activationId").GetGuid();
        var specRevision = helloJson.RootElement.GetProperty("spec").GetProperty("specRevision").GetInt64();
        Assert.True(helloJson.RootElement.GetProperty("spec").GetProperty("config").GetProperty("enabled").GetBoolean());

        var ready = await Post($"/v1/collector-protocol/browser/{activationId}/ready", $$$"""
        {
          "messageId":"{{{Guid.CreateVersion7()}}}",
          "appliedSpecRevision":{{{specRevision}}},
          "bindings":[{"bindingId":"tabs","outputId":"activeTab","dimensions":{}}]
        }
        """);
        Assert.Equal(200, ready.StatusCode);
        using var readyJson = JsonDocument.Parse(ready.Body);
        var streamId = readyJson.RootElement.GetProperty("streams").GetProperty("tabs").GetProperty("streamId").GetGuid();
        var leaseToken = readyJson.RootElement.GetProperty("lease").GetProperty("token").GetString()!;

        var factId = Guid.CreateVersion7();
        var publish = await Post($"/v1/collector-protocol/browser/{activationId}/facts", $$$"""
        {
          "messageId":"{{{Guid.CreateVersion7()}}}",
          "leaseToken":"{{{leaseToken}}}",
          "streamId":"{{{streamId}}}",
          "facts":[{
            "streamId":"{{{streamId}}}",
            "schemaRevision":1,
            "factId":"{{{factId}}}",
            "revision":1,
            "observedAt":null,
            "recordState":"present",
            "time":{"start":"2026-08-25T08:00:00Z","end":"2026-08-25T08:01:00Z","isFinal":false},
            "payload":{"identityKey":"https://example.com/docs","title":"Docs","attributes":{"url":"https://example.com/docs?q=1","domain":"example.com","site":"example.com","windowId":7}}
          }]
        }
        """);

        Assert.Equal(200, publish.StatusCode);
        using var ack = JsonDocument.Parse(publish.Body);
        Assert.Equal("committed", ack.RootElement.GetProperty("results")[0].GetProperty("status").GetString());
        var segment = Assert.Single(_sink.GetAndClearSegments());
        Assert.Equal("browser", segment.Source);
        Assert.Equal("win:msedge", segment.AppIdentityKey);
        Assert.Equal("example.com", segment.Attributes!.Value.GetProperty("site").GetString());
        Assert.False(segment.Attributes.Value.TryGetProperty("appHint", out _));
        Assert.Equal(2, _registry.Snapshot["browser"].DeclarationVersion);
        Assert.Contains("payload.attributes.site", _registry.Snapshot["browser"].DeclarationJson);
    }

    [Fact]
    public async Task DisabledDesiredStateRejectsFactsAndLeaseExpiryEndsSessionWithoutChangingDesiredState()
    {
        var session = await Activate();
        _registry.Enabled = false;

        var rejected = await Post($"/v1/collector-protocol/browser/{session.ActivationId}/facts", $$$"""
        {
          "messageId":"{{{Guid.CreateVersion7()}}}",
          "leaseToken":"{{{session.LeaseToken}}}",
          "streamId":"{{{session.StreamId}}}",
          "facts":[]
        }
        """);
        Assert.Equal(403, rejected.StatusCode);

        _time.Advance(TimeSpan.FromSeconds(11));
        _handler.ExpireLeases();
        var renew = await Post($"/v1/collector-protocol/browser/{session.ActivationId}/renew", $$$"""
        {"leaseToken":"{{{session.LeaseToken}}}"}
        """);
        Assert.Equal(409, renew.StatusCode);
        Assert.False(_registry.Enabled);
    }

    private async Task<(Guid ActivationId, Guid StreamId, string LeaseToken)> Activate()
    {
        var hello = await Post("/v1/collector-protocol/browser/hello", $$$"""
        {"messageId":"{{{Guid.CreateVersion7()}}}","artifactId":"browser.extension","artifactHash":"sha256:0c4d749ffa5d7dc6467c04a66cc054c54433a951b2e00555215d923bf7a14f46","protocolMajors":[1],"supportedCapabilities":{"facts.segment":[1],"diagnostics.stream-gap":[1]},"appHint":"edge"}
        """);
        using var helloJson = JsonDocument.Parse(hello.Body);
        var activationId = helloJson.RootElement.GetProperty("activationId").GetGuid();
        var revision = helloJson.RootElement.GetProperty("spec").GetProperty("specRevision").GetInt64();
        var ready = await Post($"/v1/collector-protocol/browser/{activationId}/ready", $$$"""
        {"messageId":"{{{Guid.CreateVersion7()}}}","appliedSpecRevision":{{{revision}}},"bindings":[{"bindingId":"tabs","outputId":"activeTab","dimensions":{}}]}
        """);
        using var readyJson = JsonDocument.Parse(ready.Body);
        return (
            activationId,
            readyJson.RootElement.GetProperty("streams").GetProperty("tabs").GetProperty("streamId").GetGuid(),
            readyJson.RootElement.GetProperty("lease").GetProperty("token").GetString()!);
    }

    private async Task<ProtocolHttpResponse> Post(string path, string json)
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return Assert.IsType<ProtocolHttpResponse>(
            await _handler.HandleAsync("POST", path, body));
    }

    public void Dispose()
    {
        _handler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _runtime.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Device : IDeviceIdentity
    {
        public string HardwareId => "AAAAAAAA-BBBB-7CCC-8DDD-EEEEEEEEEEEE";
        public string DeviceName => "test";
    }

    private sealed class Resolver : ICollectorAppHintResolver
    {
        public CollectorAppHintResolution Resolve(string appHint) =>
            appHint == "edge" ? CollectorAppHintResolution.Resolved("win:msedge") : CollectorAppHintResolution.Unknown;
    }

    private sealed class Clock(ManualTimeProvider time) : IClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }

    private sealed class MutableRegistry : ICollectorRegistry
    {
        private string? _declaration;
        private int? _declarationVersion;
        public bool Enabled { get; set; } = true;
        public IReadOnlyDictionary<string, CollectorRegistration> Snapshot =>
            new Dictionary<string, CollectorRegistration>
            {
                ["browser"] = new(Enabled, 30_000, _declaration, _declarationVersion)
            };
        public CollectorRegistration Touch(string source, int? flushPeriodMs = null) => Snapshot["browser"];
        public void Discover(IEnumerable<string> sources) { }
        public void StoreDeclaration(string source, string declarationJson, int version)
        {
            _declaration = declarationJson;
            _declarationVersion = version;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
