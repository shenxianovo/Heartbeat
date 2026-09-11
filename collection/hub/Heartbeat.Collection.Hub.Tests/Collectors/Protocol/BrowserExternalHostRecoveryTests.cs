using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.Facts;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public sealed class BrowserExternalHostRecoveryTests
{
    [Fact]
    public async Task BrowserRecovery_ReturnsDeliveredOngoingSnapshotAfterHostRestartWithoutChangingJournal()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var session = await fixture.ReadyAsync();
        var factId = Guid.Parse("018fda81-1000-7000-8000-000000000001");
        var missingId = Guid.Parse("018fda81-1000-7000-8000-000000000002");
        var observer = Guid.Parse("018fda81-1000-7000-8000-000000000003");
        var app = new ObservationObjectReference("app", ObservationObjectScopes.AppIdentity, "app.one");
        var device = new ObservationObjectReference("machine", ObservationObjectScopes.Machine, fixture.Subject.SubjectId.ToString("D"));
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var response = await fixture.TryPublishAsync(session, "https://example.test/page", fact =>
        {
            fact["factId"] = factId;
            fact["revision"] = 1720000000000L;
            fact["collectorId"] = observer;
            fact["foi"] = JsonSerializer.SerializeToNode(app, jsonOptions);
            fact["relations"] = JsonSerializer.SerializeToNode(new[] { ObservationCompatibility.ObservedOn(device, app) }, jsonOptions);
            fact["observedAt"] = "2024-07-03T09:46:40Z";
            fact["aspect"] = "browser.window-activity";
            fact["time"] = JsonNode.Parse("{\"start\":\"2024-07-03T09:45:00Z\",\"end\":\"2024-07-03T09:46:40Z\",\"isFinal\":false}");
            fact["payload"]!["unknown"] = JsonNode.Parse("{\"precise\":1234567890123,\"nested\":[true,null,\"keep\"]}");
        });
        Assert.True(response.StatusCode == 200, response.Body);
        var original = Assert.Single(fixture.Runtime.ReadPendingFacts());
        fixture.Runtime.ConfirmUploadedFacts([original]);
        Assert.Empty(fixture.Runtime.ReadPendingFacts());
        await fixture.RestartHostAsync();
        session = await fixture.ReadyAsync();
        var before = File.ReadAllBytes(fixture.RuntimePath);

        var recovered = await Recover(fixture, session, session.StreamId, [factId, missingId]);

        Assert.Equal(200, recovered.StatusCode);
        var envelope = JsonNode.Parse(recovered.Body)!;
        Assert.Equal("facts.recovered", envelope["type"]!.GetValue<string>());
        var facts = envelope["body"]!["facts"]!.AsArray();
        var snapshot = Assert.Single(facts)!;
        Assert.Equal(session.StreamId, snapshot["streamId"]!.GetValue<Guid>());
        Assert.Equal(factId, snapshot["factId"]!.GetValue<Guid>());
        Assert.Equal(1720000000000L, snapshot["revision"]!.GetValue<long>());
        Assert.Equal(observer, snapshot["collectorId"]!.GetValue<Guid>());
        Assert.Null(snapshot["kind"]);
        Assert.False(snapshot["time"]!["isFinal"]!.GetValue<bool>());
        Assert.Equal(original.Fact!.Start, snapshot["time"]!["start"]!.GetValue<DateTimeOffset>());
        Assert.Equal(original.Fact.End, snapshot["time"]!["end"]!.GetValue<DateTimeOffset>());
        Assert.Equal(original.Fact.ObservedAt, snapshot["observedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal("browser.window-activity", snapshot["aspect"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(original.Fact.Foi, jsonOptions), snapshot["foi"]));
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(original.Fact.Relations, jsonOptions), snapshot["relations"]));
        Assert.True(JsonElement.DeepEquals(original.Fact.Payload!.Value, JsonSerializer.SerializeToElement(snapshot["payload"])));
        Assert.Equal(missingId, Assert.Single(envelope["body"]!["missing"]!.AsArray())!.GetValue<Guid>());
        Assert.Equal(before, File.ReadAllBytes(fixture.RuntimePath));
        Assert.Empty(fixture.Runtime.ReadPendingFacts());
        Assert.Equal(recovered.StatusCode, (await Recover(fixture, session, session.StreamId, [factId])).StatusCode);
    }

    [Fact]
    public async Task BrowserRecovery_CannotReadAnotherHostsStreamOrFindItsFactById()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var first = await fixture.ReadyAsync();
        await fixture.PublishAsync(first, "private-page");
        var factId = Assert.Single(fixture.Runtime.ReadPendingFacts()).Fact!.FactId;
        var second = await fixture.ReadyAsync("external-host-b");

        var denied = await Recover(fixture, second, first.StreamId, [factId]);
        Assert.Equal(409, denied.StatusCode);
        Assert.Equal("stream_writer_conflict", ErrorCode(denied));
        var ownStream = await Recover(fixture, second, second.StreamId, [factId]);
        Assert.Equal(200, ownStream.StatusCode);
        var result = JsonNode.Parse(ownStream.Body)!["body"]!;
        Assert.Empty(result["facts"]!.AsArray());
        Assert.Equal(factId, Assert.Single(result["missing"]!.AsArray())!.GetValue<Guid>());
        Assert.Equal(409, (await Recover(fixture, second, Guid.Empty, [factId])).StatusCode);
    }

    [Fact]
    public async Task BrowserRecovery_RejectsWrongLeaseUnreadyReplacedExpiredAndRevokedSessions()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var session = await fixture.ReadyAsync();
        var ids = new[] { Guid.NewGuid() };
        var denied = await Recover(fixture, session with { LeaseToken = "wrong" }, session.StreamId, ids);
        Assert.Equal(409, denied.StatusCode);
        Assert.Equal("activation_stopping", ErrorCode(denied));
        var pending = await fixture.HelloUntilStreamsAsync("unready-host");
        var unready = new ReadySession(pending.ActivationId, session.StreamId, session.LeaseToken, pending.SpecRevision);
        Assert.Equal(409, (await Recover(fixture, unready, session.StreamId, ids)).StatusCode);

        var replacement = await fixture.ReadyAsync();
        Assert.Equal(409, (await Recover(fixture, session, session.StreamId, ids)).StatusCode);
        fixture.Time.Advance(TimeSpan.FromSeconds(46));
        var expired = await Recover(fixture, replacement, replacement.StreamId, ids);
        Assert.Equal(409, expired.StatusCode);
        Assert.Equal("activation_stopping", ErrorCode(expired));

        var current = await fixture.ReadyAsync();
        await fixture.Runtime.RemoveInstanceAsync(Assert.Single(fixture.Runtime.ListInstances()).CollectorInstanceId);
        var revoked = await Recover(fixture, current, current.StreamId, ids);
        Assert.Equal(409, revoked.StatusCode);
        Assert.Equal("activation_stopping", ErrorCode(revoked));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    [InlineData(-1)]
    [InlineData(-2)]
    public async Task BrowserRecovery_RejectsUnboundedEmptyOrDuplicateQueries(int count)
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var session = await fixture.ReadyAsync();
        var id = Guid.NewGuid();
        Guid[] ids = count switch
        {
            -1 => [id, id],
            -2 => [Guid.Empty],
            _ => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray()
        };
        var before = File.ReadAllBytes(fixture.RuntimePath);
        var denied = await Recover(fixture, session, session.StreamId, ids);
        Assert.Equal(409, denied.StatusCode);
        Assert.Equal("batch_limit_exceeded", ErrorCode(denied));
        Assert.Equal(before, File.ReadAllBytes(fixture.RuntimePath));
    }

    [Fact]
    public async Task BrowserRecovery_ReturnsFinalSnapshotAsFinalAndDoesNotRequeueIt()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var session = await fixture.ReadyAsync();
        await fixture.PublishAsync(session, "closed-page");
        var sent = Assert.Single(fixture.Runtime.ReadPendingFacts());
        fixture.Runtime.ConfirmUploadedFacts([sent]);
        var recovered = await Recover(fixture, session, session.StreamId, [sent.Fact!.FactId]);
        Assert.Equal(200, recovered.StatusCode);
        var snapshot = Assert.Single(JsonNode.Parse(recovered.Body)!["body"]!["facts"]!.AsArray())!;
        Assert.True(snapshot["time"]!["isFinal"]!.GetValue<bool>());
        Assert.Equal(sent.Fact.End, snapshot["time"]!["end"]!.GetValue<DateTimeOffset>());
        Assert.Empty(fixture.Runtime.ReadPendingFacts());
    }

    private static string ErrorCode(ProtocolHttpResponse response) =>
        JsonNode.Parse(response.Body)!["body"]!["error"]!["code"]!.GetValue<string>();

    private static Task<ProtocolHttpResponse> Recover(HandlerFixture fixture, ReadySession session,
        Guid streamId, Guid[] factIds) => fixture.PostAsync($"{session.ActivationId}/recover", new
        {
            protocol = "heartbeat.collector/1", type = "facts.recover", messageId = Guid.CreateVersion7(),
            activationId = session.ActivationId, body = new { leaseToken = session.LeaseToken, streamId, factIds }
        });

    private sealed record ReadySession(Guid ActivationId, Guid StreamId, string LeaseToken, long SpecRevision);

    private sealed class RecordingDeclarationStore : ICollectorDeclarationStore
    {
        private readonly Dictionary<string, CollectorRegistration> _entries = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, CollectorRegistration> Snapshot => _entries;

        public void StoreVerifiedPackageDeclaration(string source, string declarationJson, int version) =>
            _entries[source] = new CollectorRegistration(true, null, declarationJson, version);
    }

    private sealed class HandlerFixture : IAsyncDisposable
    {
        public const string DefaultHostIdentity = "external-host-a";
        public const string DefaultAppIdentityKey = "app.one";

        private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();
        private readonly ReferenceCollectorPackageCopy _packageCopy;

        private HandlerFixture(ReferenceCollectorPackageCopy packageCopy)
        {
            _packageCopy = packageCopy;
            Package = LocalCollectorPackage.Load(packageCopy.Path);
            Installations = new CollectorPackageInstallations(
                Path.Combine(_directory.Path, "collector-packages"));
            Installations.Install(packageCopy.Path);
            Sink = new SegmentIngestService(new FixedClock());
            Runtime = OpenRuntime();
            var blueprint = Package.Manifest.DefaultInstance
                            ?? throw new InvalidOperationException("Fixture Package must declare defaultInstance.");
            Runtime.CreateInstance(
                Package,
                Subject,
                new CollectorInstanceSpec(1, blueprint.ConfigVersion, blueprint.Config.Clone()),
                CollectorRuntime.DefaultInstanceKey);
            Handler = NewHandler();
        }

        public AdjustableTimeProvider Time { get; } = new();
        public string RuntimePath => Path.Combine(_directory.Path, "runtime.json");
        public CollectorRuntime Runtime { get; private set; }
        public ExternalHostCollectorProtocolHandler Handler { get; private set; }
        public CollectorPackageInstallations Installations { get; }
        public LocalCollectorPackage Package { get; }
        public SegmentIngestService Sink { get; }
        public SubjectReference Subject { get; } =
            new(Guid.CreateVersion7(), SubjectKind.Machine);

        public static Task<HandlerFixture> CreateAsync()
        {
            var copy = ReferenceCollectorPackageCopy.Create(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "ReferenceCollectorPackage"));
            var manifest = copy.ReadManifest();
            manifest["artifacts"]![0]!["selector"]!["driver"] = "externalHost";
            copy.WriteManifest(manifest);
            return Task.FromResult(new HandlerFixture(copy));
        }

        private CollectorRuntime OpenRuntime() => CollectorRuntime.Open(
            Path.Combine(_directory.Path, "runtime.json"),
            Sink);

        private ExternalHostCollectorProtocolHandler NewHandler() => new(
            Runtime,
            new RecordingDeclarationStore(),
            Installations,
            () => Subject, timeProvider: Time);

        /// <summary>重启宿主：进程内状态全部丢弃，只留下磁盘上的 Runtime state 与 Installation。</summary>
        public async Task RestartHostAsync()
        {
            await Handler.DisposeAsync();
            await Runtime.DisposeAsync();
            Runtime = OpenRuntime();
            Handler = NewHandler();
        }

        public async Task<ProtocolHttpResponse> PostAsync(string suffix, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var response = await Handler.HandleAsync(
                "POST",
                $"{ExternalHostCollectorProtocolHandler.RoutePrefix}/{suffix}",
                body);
            Assert.NotNull(response);
            return response!;
        }

        public Task<ProtocolHttpResponse> HelloAsync(
            string externalHostIdentity = DefaultHostIdentity,
            string appIdentityKey = DefaultAppIdentityKey)
        {
            var hello = new Dictionary<string, object?>
            {
                ["packageId"] = Package.Manifest.PackageId,
                ["packageVersion"] = Package.Manifest.Version,
                ["packageContentHash"] = Package.PackageContentHash,
                ["artifactId"] = Package.Artifacts.Single().ArtifactId,
                ["artifactHash"] = Package.Artifacts.Single().ContentHash,
                ["protocolMajors"] = new[] { 1 },
                ["supportedCapabilities"] = Package.Manifest.SupportedCapabilities.ToDictionary(
                    capability => capability.Key,
                    capability => capability.Value.ToArray(),
                    StringComparer.Ordinal),
                ["appIdentityKey"] = appIdentityKey,
                ["externalHostIdentity"] = externalHostIdentity
            };
            return PostAsync("hello", new
            {
                protocol = "heartbeat.collector.bootstrap/1",
                type = "activation.hello",
                messageId = Guid.CreateVersion7(),
                body = hello
            });
        }

        public async Task<(Guid ActivationId, long SpecRevision)> HelloUntilStreamsAsync(
            string externalHostIdentity = DefaultHostIdentity,
            string appIdentityKey = DefaultAppIdentityKey)
        {
            var hello = await HelloAsync(externalHostIdentity, appIdentityKey);
            Assert.Equal(200, hello.StatusCode);
            using var document = JsonDocument.Parse(hello.Body);
            var activationId = Guid.Parse(document.RootElement
                .GetProperty("body").GetProperty("activationId").GetString()!);

            var initialize = await PostAsync($"{activationId}/initialize", new { });
            Assert.Equal(200, initialize.StatusCode);
            using var initializeBody = JsonDocument.Parse(initialize.Body);
            // initialized 必须 replyTo initialize 的 messageId：握手是有序对话，不是无状态请求。
            var initializeMessageId = Guid.Parse(
                initializeBody.RootElement.GetProperty("messageId").GetString()!);
            var specRevision = initializeBody.RootElement
                .GetProperty("body").GetProperty("spec").GetProperty("revision").GetInt64();

            var initialized = await PostAsync($"{activationId}/initialized", new
            {
                protocol = "heartbeat.collector/1",
                type = "activation.initialized",
                messageId = Guid.CreateVersion7(),
                activationId,
                replyTo = initializeMessageId,
                body = new { appliedSpecRevision = specRevision }
            });
            // initialized 是纯确认，没有 body：204。
            Assert.Equal(204, initialized.StatusCode);
            return (activationId, specRevision);
        }

        public async Task<ReadySession> ReadyAsync(
            string externalHostIdentity = DefaultHostIdentity,
            string appIdentityKey = DefaultAppIdentityKey)
        {
            var (activationId, specRevision) = await HelloUntilStreamsAsync(
                externalHostIdentity,
                appIdentityKey);
            var streams = await PostAsync($"{activationId}/streams", new
            {
                protocol = "heartbeat.collector/1",
                type = "streams.open",
                messageId = Guid.CreateVersion7(),
                activationId,
                body = new
                {
                    specRevision,
                    bindings = new[]
                    {
                        new
                        {
                            bindingId = "activity",
                            outputId = "activity",
                            dimensions = new Dictionary<string, string>()
                        }
                    }
                }
            });
            Assert.Equal(200, streams.StatusCode);
            using var opened = JsonDocument.Parse(streams.Body);
            // streams 是 bindingId -> Stream 的映射，不是数组。
            var streamId = Guid.Parse(opened.RootElement
                .GetProperty("body").GetProperty("streams")
                .GetProperty("activity").GetProperty("streamId").GetString()!);

            var ready = await PostAsync($"{activationId}/ready", new
            {
                protocol = "heartbeat.collector/1",
                type = "activation.ready",
                messageId = Guid.CreateVersion7(),
                activationId,
                body = new { appliedSpecRevision = specRevision }
            });
            Assert.Equal(200, ready.StatusCode);
            using var readyBody = JsonDocument.Parse(ready.Body);
            var leaseToken = readyBody.RootElement
                .GetProperty("body").GetProperty("lease").GetProperty("token").GetString()!;
            return new ReadySession(activationId, streamId, leaseToken, specRevision);
        }

        public async Task PublishAsync(ReadySession session, string identityKey)
        {
            var response = await TryPublishAsync(session, identityKey);
            Assert.Equal(200, response.StatusCode);
        }

        public async Task<ProtocolHttpResponse> TryPublishAsync(ReadySession session, string identityKey, Action<JsonObject>? mutateFact = null)
        {
            var now = DateTimeOffset.UtcNow;
            var request = JsonSerializer.SerializeToNode(new
            {
                protocol = "heartbeat.collector/1",
                type = "facts.publish",
                messageId = Guid.CreateVersion7(),
                activationId = session.ActivationId,
                body = new
                {
                    leaseToken = session.LeaseToken,
                    facts = new[]
                    {
                        new
                        {
                            streamId = session.StreamId,
                            factId = Guid.CreateVersion7(),
                            revision = 1L,
                            time = new
                            {
                                start = now.AddMinutes(-1),
                                end = now,
                                isFinal = true
                            },
                            payload = new { identityKey, title = "Work" }
                        }
                    }
                }
            })!.AsObject();
            mutateFact?.Invoke(request["body"]!["facts"]![0]!.AsObject());
            return await PostAsync($"{session.ActivationId}/facts", request);
        }

        public async ValueTask DisposeAsync()
        {
            await Handler.DisposeAsync();
            await Runtime.DisposeAsync();
            _packageCopy.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"heartbeat-external-host-handler-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
