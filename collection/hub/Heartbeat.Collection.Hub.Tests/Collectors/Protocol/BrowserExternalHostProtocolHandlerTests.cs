using System.Text;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Packages;
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
    private readonly BrowserCollectorRuntime _browserRuntime;
    private readonly BrowserExternalHostProtocolHandler _handler;

    public BrowserExternalHostProtocolHandlerTests()
    {
        Directory.CreateDirectory(_root);
        _sink = new SegmentIngestService(new Clock(_time));
        _runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            _sink,
            appHintResolver: new Resolver());
        var options = new BrowserExternalHostBindingOptions(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "BrowserCollectorPackage"),
            TimeSpan.FromSeconds(10))
        {
            DataDirectory = _root
        };
        _browserRuntime = new BrowserCollectorRuntime(_runtime, _registry, new Device(), options);
        _browserRuntime.Import(options.PackageDirectory);
        _handler = new BrowserExternalHostProtocolHandler(
            _runtime,
            _registry,
            _browserRuntime,
            options,
            _time);
    }

    [Fact]
    public async Task BrowserTranscript_ConvergesSpecRegistersDeclarationAndProjectsWithoutChangingWirePayload()
    {
        var hello = await Post("/v1/collector-protocol/browser/hello", Message(
            "activation.hello",
            $$$"""
        {
          "artifactId":"browser.extension",
          "artifactHash":"sha256:3c78bf8d23a9ee3d9210110b80fc66deea3ae723b757fa06bed3da076c5e58cd",
          "protocolMajors":[1],
          "supportedCapabilities":{"facts.segment":[1],"diagnostics.stream-gap":[1]},
          "appHint":"edge"
        }
        """, bootstrap: true));
        Assert.Equal(200, hello.StatusCode);
        using var helloJson = JsonDocument.Parse(hello.Body);
        var accepted = helloJson.RootElement.GetProperty("body");
        var activationId = accepted.GetProperty("activationId").GetGuid();
        Assert.False(helloJson.RootElement.TryGetProperty("spec", out _));
        Assert.False(helloJson.RootElement.TryGetProperty("limits", out _));

        var initialize = await Post($"/v1/collector-protocol/browser/{activationId}/initialize", "{}");
        using var initializeJson = JsonDocument.Parse(initialize.Body);
        var initialization = initializeJson.RootElement.GetProperty("body");
        var specRevision = initialization.GetProperty("spec").GetProperty("revision").GetInt64();
        Assert.True(initialization.GetProperty("spec").GetProperty("config").GetProperty("value").GetProperty("enabled").GetBoolean());
        Assert.Equal(500, initialization.GetProperty("limits").GetProperty("maxFactsPerBatch").GetInt32());
        var initialized = await Post($"/v1/collector-protocol/browser/{activationId}/initialized", Message(
            "activation.initialized",
            $$$"""{"appliedSpecRevision":{{{specRevision}}}}""",
            activationId,
            replyTo: initializeJson.RootElement.GetProperty("messageId").GetGuid()));
        Assert.Equal(204, initialized.StatusCode);

        var streams = await Post($"/v1/collector-protocol/browser/{activationId}/streams", Message(
            "streams.open",
            $$$"""
        {
          "specRevision":{{{specRevision}}},
          "bindings":[{"bindingId":"tabs","outputId":"activeTab","dimensions":{}}]
        }
        """, activationId));
        Assert.Equal(200, streams.StatusCode);
        using var streamsJson = JsonDocument.Parse(streams.Body);
        var streamId = streamsJson.RootElement.GetProperty("body").GetProperty("streams").GetProperty("tabs").GetProperty("streamId").GetGuid();

        var ready = await Post($"/v1/collector-protocol/browser/{activationId}/ready", Message(
            "activation.ready",
            $$$"""{"appliedSpecRevision":{{{specRevision}}}}""",
            activationId));
        Assert.Equal(200, ready.StatusCode);
        using var readyJson = JsonDocument.Parse(ready.Body);
        var leaseToken = readyJson.RootElement.GetProperty("body").GetProperty("lease").GetProperty("token").GetString()!;

        var factId = Guid.CreateVersion7();
        var publish = await Post($"/v1/collector-protocol/browser/{activationId}/facts", Message(
            "facts.publish",
            $$$"""
        {
          "leaseToken":"{{{leaseToken}}}",
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
        """, activationId));

        Assert.Equal(200, publish.StatusCode);
        using var ack = JsonDocument.Parse(publish.Body);
        Assert.Equal("committed", ack.RootElement.GetProperty("body").GetProperty("results")[0].GetProperty("status").GetString());
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
        Assert.Equal(BrowserCollectorRuntimeStatus.Ready, _browserRuntime.Current.RuntimeStatus);
        _browserRuntime.SetDesiredEnabled(false);

        var rejected = await Post($"/v1/collector-protocol/browser/{session.ActivationId}/facts", Message(
            "facts.publish",
            $$$"""
        {
          "leaseToken":"{{{session.LeaseToken}}}",
          "facts":[]
        }
        """, session.ActivationId));
        Assert.Equal(409, rejected.StatusCode);

        _time.Advance(TimeSpan.FromSeconds(11));
        _handler.ExpireLeases();
        var renew = await Post($"/v1/collector-protocol/browser/{session.ActivationId}/renew", $$$"""
        {"leaseToken":"{{{session.LeaseToken}}}"}
        """);
        Assert.Equal(409, renew.StatusCode);
        Assert.False(_browserRuntime.Current.DesiredEnabled);
        Assert.Equal(BrowserCollectorRuntimeStatus.Waiting, _browserRuntime.Current.RuntimeStatus);
    }

    [Fact]
    public async Task DisabledHelloIsRejectedBeforeInitializeAndExpiredHelloReplayDoesNotCreateActivation()
    {
        _browserRuntime.SetDesiredEnabled(false);
        var disabled = await Post("/v1/collector-protocol/browser/hello", Message(
            "activation.hello",
            """{"artifactId":"browser.extension","artifactHash":"sha256:3c78bf8d23a9ee3d9210110b80fc66deea3ae723b757fa06bed3da076c5e58cd","protocolMajors":[1],"supportedCapabilities":{"facts.segment":[1],"diagnostics.stream-gap":[1]},"appHint":"edge"}""",
            bootstrap: true));
        Assert.Equal(403, disabled.StatusCode);
        using (var json = JsonDocument.Parse(disabled.Body))
            Assert.Equal("activation.rejected", json.RootElement.GetProperty("type").GetString());

        _browserRuntime.SetDesiredEnabled(true);
        var helloMessageId = Guid.CreateVersion7();
        var helloBody = Message(
            "activation.hello",
            """{"artifactId":"browser.extension","artifactHash":"sha256:3c78bf8d23a9ee3d9210110b80fc66deea3ae723b757fa06bed3da076c5e58cd","protocolMajors":[1],"supportedCapabilities":{"facts.segment":[1],"diagnostics.stream-gap":[1]},"appHint":"edge"}""",
            bootstrap: true,
            messageId: helloMessageId);
        Assert.Equal(200, (await Post("/v1/collector-protocol/browser/hello", helloBody)).StatusCode);
        var conflictingReplay = await Post("/v1/collector-protocol/browser/hello", Message(
            "activation.hello",
            """{"artifactId":"browser.extension","artifactHash":"sha256:3c78bf8d23a9ee3d9210110b80fc66deea3ae723b757fa06bed3da076c5e58cd","protocolMajors":[1],"supportedCapabilities":{"facts.segment":[1],"diagnostics.stream-gap":[1]},"appHint":"chrome"}""",
            bootstrap: true,
            messageId: helloMessageId));
        Assert.Equal(400, conflictingReplay.StatusCode);
        using (var conflictJson = JsonDocument.Parse(conflictingReplay.Body))
            Assert.Equal(
                "protocol_invalid_message",
                conflictJson.RootElement.GetProperty("body").GetProperty("error").GetProperty("code").GetString());
        _time.Advance(TimeSpan.FromSeconds(11));
        _handler.ExpireLeases();

        var replay = await Post("/v1/collector-protocol/browser/hello", helloBody);

        Assert.Equal(409, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(replay.Body);
        Assert.Equal("activation.rejected", replayJson.RootElement.GetProperty("type").GetString());
        Assert.Equal(helloMessageId, replayJson.RootElement.GetProperty("replyTo").GetGuid());
    }

    private async Task<(Guid ActivationId, Guid StreamId, string LeaseToken)> Activate()
    {
        var hello = await Post("/v1/collector-protocol/browser/hello", Message(
            "activation.hello",
            """{"artifactId":"browser.extension","artifactHash":"sha256:3c78bf8d23a9ee3d9210110b80fc66deea3ae723b757fa06bed3da076c5e58cd","protocolMajors":[1],"supportedCapabilities":{"facts.segment":[1],"diagnostics.stream-gap":[1]},"appHint":"edge"}""",
            bootstrap: true));
        using var helloJson = JsonDocument.Parse(hello.Body);
        var activationId = helloJson.RootElement.GetProperty("body").GetProperty("activationId").GetGuid();
        var initialize = await Post($"/v1/collector-protocol/browser/{activationId}/initialize", "{}");
        using var initializeJson = JsonDocument.Parse(initialize.Body);
        var revision = initializeJson.RootElement.GetProperty("body").GetProperty("spec").GetProperty("revision").GetInt64();
        await Post($"/v1/collector-protocol/browser/{activationId}/initialized", Message(
            "activation.initialized",
            $$$"""{"appliedSpecRevision":{{{revision}}}}""",
            activationId,
            replyTo: initializeJson.RootElement.GetProperty("messageId").GetGuid()));
        var streams = await Post($"/v1/collector-protocol/browser/{activationId}/streams", Message(
            "streams.open",
            $$$"""{"specRevision":{{{revision}}},"bindings":[{"bindingId":"tabs","outputId":"activeTab","dimensions":{}}]}""",
            activationId));
        using var streamsJson = JsonDocument.Parse(streams.Body);
        var ready = await Post($"/v1/collector-protocol/browser/{activationId}/ready", Message(
            "activation.ready",
            $$$"""{"appliedSpecRevision":{{{revision}}}}""",
            activationId));
        using var readyJson = JsonDocument.Parse(ready.Body);
        return (
            activationId,
            streamsJson.RootElement.GetProperty("body").GetProperty("streams").GetProperty("tabs").GetProperty("streamId").GetGuid(),
            readyJson.RootElement.GetProperty("body").GetProperty("lease").GetProperty("token").GetString()!);
    }

    private async Task<ProtocolHttpResponse> Post(string path, string json)
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return Assert.IsType<ProtocolHttpResponse>(
            await _handler.HandleAsync("POST", path, body));
    }

    private static string Message(
        string type,
        string body,
        Guid? activationId = null,
        bool bootstrap = false,
        Guid? replyTo = null,
        Guid? messageId = null) => $$$"""
        {
          "protocol":"{{{(bootstrap ? "heartbeat.collector.bootstrap/1" : "heartbeat.collector/1")}}}",
          "type":"{{{type}}}",
          "messageId":"{{{messageId ?? Guid.CreateVersion7()}}}",
          {{{(activationId is null ? string.Empty : $"\"activationId\":\"{activationId}\",")}}}
          {{{(replyTo is null ? string.Empty : $"\"replyTo\":\"{replyTo}\",")}}}
          "body":{{{body}}}
        }
        """;

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
