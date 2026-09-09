using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Core.DTOs.Segments;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Server.Controllers;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class FactHttpTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task NativeHttpBoundary_RequiresOwnerAndPreservesRawDocumentAndUrl_ConflictsAreAtomic()
    {
        await using (var db = CreateDbContext())
        {
            db.Users.Add(new User { Id = "owner", Username = "alice" });
            await db.SaveChangesAsync();
        }
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        client.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        var batch = FactStoreTests.SegmentBatch();
        using (var anonymous = await client.PostAsJsonAsync("/api/v1/facts", batch))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        using (var upload = await client.PostAsJsonAsync("/api/v1/facts", batch))
        {
            Assert.True(upload.StatusCode == HttpStatusCode.OK, await upload.Content.ReadAsStringAsync());
        }
        using (var read = await client.GetAsync("/api/v1/users/alice/segments"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            var row = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("https://example.com/?original=1", row.GetProperty("payload").GetProperty("attributes").GetProperty("url").GetString());
            Assert.Equal(batch.Streams[0].StreamId, row.GetProperty("streamId").GetGuid());
            Assert.Equal(batch.Facts[0].FactId, row.GetProperty("factId").GetGuid());
            Assert.Equal("native", row.GetProperty("origin").GetString());
        }
        batch.Facts.Insert(0, new FactSnapshot
        {
            StreamId = batch.Streams[0].StreamId, FactId = Guid.CreateVersion7(), Revision = 1,
            Start = batch.Facts[0].Start, End = batch.Facts[0].End, IsFinal = false, Payload = batch.Facts[0].Payload
        });
        batch.Facts[1].Payload = JsonSerializer.SerializeToElement(new { identityKey = "different", title = "Changed" });
        using (var conflict = await client.PostAsJsonAsync("/api/v1/facts", batch))
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using (var db = CreateDbContext())
        {
            Assert.Single(await db.Facts.ToListAsync());
            Assert.Single(await db.ActivitySegments.ToListAsync());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeHttpBoundary_RejectsOldRetractionWithoutChangingStoredFact(bool includePayload)
    {
        await using (var db = CreateDbContext())
        {
            db.Users.Add(new User { Id = "owner", Username = "alice" });
            await db.SaveChangesAsync();
        }
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        client.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        client.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        var batch = FactStoreTests.SegmentBatch();
        using var accepted = await client.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var request = JsonSerializer.SerializeToNode(batch, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var fact = request["facts"]![0]!.AsObject();
        fact["recordState"] = "retracted";
        fact["revision"] = 2;
        if (!includePayload) fact.Remove("payload");

        using var rejected = await client.PostAsJsonAsync("/api/v1/facts", request);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("recordState", await rejected.Content.ReadAsStringAsync());
        await using var verify = CreateDbContext();
        Assert.Equal(1, (await verify.Facts.SingleAsync()).Revision);
        Assert.Equal(batch.Facts[0].End, (await verify.ActivitySegments.SingleAsync()).EndTime);
    }

    [Theory]
    [InlineData("segment")]
    [InlineData("event")]
    public async Task CollectorToAnalytics_UnknownPayloadSurvivesRestartRevisionReplayAndOwnerIsolation(string kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-fact-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
            var path = Path.Combine(directory, "runtime.json");
            var options = new CollectorRuntimeOptions { EnableFactUpload = true };
            var sink = new UnusedProjection();
            Guid instanceId;
            FactSubmission original;
            using (var runtime = CollectorRuntime.Open(path, sink, options))
            {
                var instance = runtime.CreateInstance(package,
                    new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
                    new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { })));
                instanceId = instance.CollectorInstanceId;
                await using var activation = await runtime.ActivateInProcessAsync(instanceId, package, new PayloadCollector(package));
                var stream = activation.Streams[kind == "segment" ? "foreground" : "input-events"];
                var start = DateTimeOffset.UtcNow.AddMinutes(-2);
                original = new FactSubmission(stream.Descriptor.StreamId, Guid.CreateVersion7(), 1, null,
                    kind == "segment" ? new SegmentFactTime(start, start.AddMinutes(1), false) : new EventFactTime(start),
                    JsonSerializer.SerializeToElement(new { arbitrary = new { addedByCollector = new[] { 1, 2 } } }));
                Assert.Equal(FactDeliveryStatus.Committed,
                    Assert.Single((await stream.PublishAsync(Guid.CreateVersion7(), [original])).Results).Status);
            }

            await using var application = CreateApplication();
            using var http = application.CreateClient();
            http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
            http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
            using var restarted = CollectorRuntime.Open(path, sink, options);
            await using var resumed = await restarted.ActivateInProcessAsync(instanceId, package, new PayloadCollector(package));
            var writer = resumed.Streams[kind == "segment" ? "foreground" : "input-events"];
            var first = restarted.ReadPendingFacts();
            using var accepted = await http.PostAsJsonAsync("/api/v1/facts", FactUploadItem.Request(first));
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var corrected = original with { Revision = 2, Payload = JsonSerializer.SerializeToElement(new { newField = "no registration", values = new[] { true, false } }) };
            Assert.Equal(FactDeliveryStatus.Committed,
                Assert.Single((await writer.PublishAsync(Guid.CreateVersion7(), [corrected])).Results).Status);
            restarted.ConfirmUploadedFacts(first);
            var latest = restarted.ReadPendingFacts();
            Assert.Equal(2, Assert.Single(latest).Fact!.Revision);
            using var revised = await http.PostAsJsonAsync("/api/v1/facts", FactUploadItem.Request(latest));
            Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
            using var replay = await http.PostAsJsonAsync("/api/v1/facts", FactUploadItem.Request(latest));
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            using var stale = await http.PostAsJsonAsync("/api/v1/facts", FactUploadItem.Request(first));
            Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
            Assert.Equal(FactDeliveryStatus.Duplicate,
                Assert.Single((await writer.PublishAsync(Guid.CreateVersion7(), [corrected])).Results).Status);
            Assert.Equal(FactDeliveryStatus.Superseded,
                Assert.Single((await writer.PublishAsync(Guid.CreateVersion7(), [original])).Results).Status);
            Assert.Equal(FactDeliveryStatus.Rejected,
                Assert.Single((await writer.PublishAsync(Guid.CreateVersion7(), [corrected with { Payload = original.Payload }])).Results).Status);
            restarted.ConfirmUploadedFacts(latest);
            Assert.Empty(restarted.ReadPendingFacts());

            // Valid unknown content remains in custody even though no existing report can interpret it.
            await using var db = CreateDbContext();
            var saved = await db.Facts.SingleAsync();
            Assert.Equal(2, saved.Revision);
            Assert.True(JsonElement.DeepEquals(corrected.Payload, JsonDocument.Parse(saved.Payload!).RootElement));
            Assert.Empty(await db.ActivitySegments.ToListAsync());
            Assert.Empty(await db.InputEvents.ToListAsync());
            Assert.DoesNotContain("contentHash", File.ReadAllText(path).Split("\"facts\":")[1].Split("\"gaps\":")[0]);
            var conflict = FactUploadItem.Request(latest);
            conflict.Facts[0].Payload = original.Payload;
            using var rejected = await http.PostAsJsonAsync("/api/v1/facts", conflict);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            http.DefaultRequestHeaders.Remove("X-Test-Owner");
            http.DefaultRequestHeaders.Add("X-Test-Owner", "other");
            using var other = await http.PostAsJsonAsync("/api/v1/facts", conflict);
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
            db.ChangeTracker.Clear();
            Assert.Equal(2, await db.Facts.CountAsync());
            Assert.True(JsonElement.DeepEquals(corrected.Payload,
                JsonDocument.Parse((await db.Facts.SingleAsync(f => f.OwnerId == "owner")).Payload!).RootElement));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class UnusedProjection : ISegmentSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) => throw new InvalidOperationException("Native upload owns custody.");
    }

    private sealed class PayloadCollector(LocalCollectorPackage package) : IInProcessCollector
    {
        public string ArtifactId => package.Manifest.Artifacts[0].ArtifactId;
        public ProtocolSupport ProtocolSupport => new([1], package.Manifest.SupportedCapabilities);
        public ValueTask<InProcessCollectorInitialization> InitializeAsync(CollectorInitialization initialization, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new InProcessCollectorInitialization(initialization.Spec.SpecRevision,
                package.Manifest.Outputs.Select(output => new OutputBinding(output.OutputId, output.OutputId, new Dictionary<string, string>())).ToArray()));
        public async ValueTask OnStreamsOpenedAsync(InProcessCollectorStreamsOpened opened, CancellationToken cancellationToken) =>
            await opened.ReadyAsync(cancellationToken);
        public ValueTask<InProcessCollectorDrainResult> StopAsync(DateTimeOffset deadline, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new InProcessCollectorDrainResult(new InProcessCollectorLogicalDrainResult(0, 0)));
    }

    private WebApplicationFactory<FactController> CreateApplication() =>
        new WebApplicationFactory<FactController>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options.UseNpgsql(TestConnectionString));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                    options.DefaultScheme = "Test";
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            });
        });

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var owner = Request.Headers["X-Test-Owner"].FirstOrDefault();
            if (owner is null) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", owner), new Claim("preferred_username", "alice")], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
