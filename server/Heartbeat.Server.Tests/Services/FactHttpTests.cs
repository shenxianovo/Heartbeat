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
    public async Task LegacyAccountHttpImport_IsImmediatelyQueryable_AndNativeTakeoverKeepsItsRow()
    {
        await using (var db = CreateDbContext())
        {
            db.Users.Add(new User { Id = "owner", Username = "alice" });
            await db.SaveChangesAsync();
        }
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        var native = ServiceAccountTests.Batch();
        native.Facts[0].ObserverId = null; native.Facts[0].Target = null;
        native.Facts[0].Payload = JsonSerializer.SerializeToElement(new { identityKey = "world|instance", title = "World", attributes = new { worldId = "world", instanceId = "instance" } });
        var old = FactStoreTests.LegacySegment(native);
        old.Source = "vrchat.account";
        old.IdentityKey = "world|instance";
        old.Title = "World";
        old.StartTime = native.Facts[0].Start!.Value;
        old.EndTime = native.Facts[0].End!.Value;
        old.AppIdentityKey = null;
        http.DefaultRequestHeaders.Add("X-Hardware-Id", $"subject:account:{native.Streams[0].Subject.SubjectId:D}");
        var upload = new SegmentUploadRequest { Segments = [old] };
        using var imported = await http.PostAsJsonAsync("/api/v1/segments", upload);
        Assert.True(imported.IsSuccessStatusCode, await imported.Content.ReadAsStringAsync());
        var before = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
        Assert.Equal("account", before.TargetKind);
        Assert.Null(before.DeviceId);
        Assert.Null(before.ObserverId);
        Assert.NotNull(before.AppId);
        Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/segments?accountId={before.TargetId}&appId={before.AppId}"))!);
        using var retry = await http.PostAsJsonAsync("/api/v1/segments", upload);
        Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
        using var takeover = await http.PostAsJsonAsync("/api/v1/facts", native);
        Assert.True(takeover.IsSuccessStatusCode, await takeover.Content.ReadAsStringAsync());
        using var late = await http.PostAsJsonAsync("/api/v1/segments", upload);
        Assert.True(late.IsSuccessStatusCode, await late.Content.ReadAsStringAsync());
        var after = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/segments?accountId={before.TargetId}"))!);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(native.Facts[0].FactId, after.FactId);
        Assert.Equal(native.Streams[0].CollectorInstanceId, after.ObserverId);
    }

    [Fact]
    public async Task VRChatManagedSession_PublishesAccountThroughRuntimeHttpAndOfflineHistory_ThenReplaysAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-vrchat-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var packageDirectory = Path.Combine(directory, "package");
            using (var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet", UseShellExecute = false,
                ArgumentList = { Path.Combine(AppContext.BaseDirectory, "Heartbeat.Collector.VRChat.dll"), "--create-package", packageDirectory }
            })!)
            {
                await build.WaitForExitAsync();
                Assert.Equal(0, build.ExitCode);
            }
            var package = LocalCollectorPackage.Load(packageDirectory);
            var path = Path.Combine(directory, "runtime.json");
            var options = new CollectorRuntimeOptions { EnableFactUpload = true };
            var secrets = new EncryptedFileCollectorSecretStore(Path.Combine(directory, "secrets"));
            var processOptions = new ManagedProcessActivationOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(15), DrainGracePeriod = TimeSpan.FromSeconds(5),
                EnvironmentVariables = new Dictionary<string, string> { ["HEARTBEAT_VRCHAT_MOCK"] = "1" }
            };
            Guid observer;
            List<FactUploadItem> pending;
            using (var runtime = CollectorRuntime.Open(path, new UnusedProjection(), options, secretStore: secrets))
            {
                observer = runtime.CreateInstance(package, new SubjectReference(Guid.NewGuid(), SubjectKind.Account),
                    new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { pollIntervalSeconds = 1 }))).CollectorInstanceId;
                var activating = runtime.ActivateManagedProcessAsync(observer, package, processOptions).AsTask();
                async Task Answer(CollectorAuthorizationChallengeKind kind, Dictionary<string, string> values)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while (runtime.GetManagedProcessRuntimeState(observer).AuthorizationChallenge is not { Kind: var found } || found != kind)
                        await Task.Delay(20, timeout.Token);
                    var challenge = runtime.GetManagedProcessRuntimeState(observer).AuthorizationChallenge!;
                    await runtime.SubmitManagedProcessAuthorizationAsync(observer, challenge.InteractionId, values);
                }
                await Answer(CollectorAuthorizationChallengeKind.Credentials, new() { ["username"] = "test-user", ["password"] = "test-password" });
                await Answer(CollectorAuthorizationChallengeKind.VerificationCode, new() { ["code"] = "123456" });
                var activation = await activating.WaitAsync(TimeSpan.FromSeconds(15));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (runtime.ReadPendingFacts().Count == 0) await Task.Delay(20, timeout.Token);
                await activation.StopAsync();
                pending = runtime.ReadPendingFacts();
            }
            await using (var db = CreateDbContext())
            {
                db.Users.Add(new User { Id = "owner", Username = "alice" });
                await db.SaveChangesAsync();
            }
            await using var app = CreateApplication();
            using var http = app.CreateClient();
            http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
            http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
            using var uploaded = await http.PostAsJsonAsync("/api/v1/facts", FactUploadItem.Request(pending));
            Assert.True(uploaded.IsSuccessStatusCode, await uploaded.Content.ReadAsStringAsync());
            var before = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
            Assert.Equal(observer, before.ObserverId);
            Assert.Equal("account", before.TargetKind);
            Assert.Null(before.DeviceId);
            Assert.NotNull(before.AppId);
            var activity = Assert.Single((await http.GetFromJsonAsync<JsonElement>($"/api/v1/users/alice/segments?appId={before.AppId}")).EnumerateArray());
            Assert.Equal("usr_11111111-1111-4111-8111-111111111111", activity.GetProperty("targetName").GetString());
            Assert.Equal("VRChat", activity.GetProperty("appDisplayName").GetString());
            using var restarted = CollectorRuntime.Open(path, new UnusedProjection(), options, secretStore: secrets);
            using var replay = await http.PostAsJsonAsync("/api/v1/facts", FactUploadItem.Request(restarted.ReadPendingFacts()));
            Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
            restarted.ConfirmUploadedFacts(restarted.ReadPendingFacts());
            Assert.Empty(restarted.ReadPendingFacts());
            var resumed = await restarted.ActivateManagedProcessAsync(observer, package, processOptions).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                while (restarted.ReadPendingFacts().Count == 0) await Task.Delay(20, timeout.Token);
            await resumed.StopAsync();
            using var newUpload = await http.PostAsJsonAsync("/api/v1/facts", FactUploadItem.Request(restarted.ReadPendingFacts()));
            Assert.True(newUpload.IsSuccessStatusCode, await newUpload.Content.ReadAsStringAsync());
            var after = (await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/segments?accountId={before.TargetId}"))!;
            Assert.Equal(2, after.Count);
            Assert.Contains(after, f => f.Id == before.Id && f.Revision == before.Revision);
            Assert.All(after, f => { Assert.Equal(observer, f.ObserverId); Assert.Equal(before.TargetId, f.TargetId); Assert.Null(f.DeviceId); });
            Assert.DoesNotContain("mock-auth", File.ReadAllText(path));
            Assert.DoesNotContain("test-password", JsonSerializer.Serialize(after));
            // Replay the pre-target representation in an isolated owner: no current login evidence
            // is available to this old cache, so it must remain an unknown historical account.
            var legacyBatch = FactUploadItem.Request(restarted.ReadPendingFacts());
            foreach (var fact in legacyBatch.Facts)
            {
                fact.ObserverId = null; fact.Target = null;
                fact.Payload = JsonDocument.Parse(fact.Payload!.Value.GetRawText().Replace("activityKey", "identityKey")).RootElement;
            }
            await using (var db = CreateDbContext())
            {
                db.Users.Add(new User { Id = "legacy-owner", Username = "legacy" });
                await db.SaveChangesAsync();
            }
            http.DefaultRequestHeaders.Remove("X-Test-Owner");
            http.DefaultRequestHeaders.Add("X-Test-Owner", "legacy-owner");
            using var legacyPosted = await http.PostAsJsonAsync("/api/v1/facts", legacyBatch);
            Assert.True(legacyPosted.IsSuccessStatusCode, await legacyPosted.Content.ReadAsStringAsync());
            var legacyBefore = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/legacy/facts/segments"))!);
            restarted.Dispose();
            var cache = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            cache["schemaVersion"] = 5;
            foreach (var fact in cache["facts"]!.AsArray())
            {
                fact!.AsObject().Remove("observerId"); fact.AsObject().Remove("target");
                fact["payload"] = System.Text.Json.Nodes.JsonNode.Parse(fact["payload"]!.ToJsonString().Replace("activityKey", "identityKey"));
            }
            File.WriteAllText(path, cache.ToJsonString());
            using var upgraded = CollectorRuntime.Open(path, new UnusedProjection(), options, secretStore: secrets);
            Assert.True(File.Exists(path + ".v5.bak"));
            using var oldReplay = await http.PostAsJsonAsync("/api/v1/facts", FactUploadItem.Request(upgraded.ReadPendingFacts()));
            Assert.True(oldReplay.IsSuccessStatusCode, await oldReplay.Content.ReadAsStringAsync());
            var legacyAfter = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/legacy/facts/segments?accountId={legacyBefore.TargetId}"))!);
            Assert.Equal(legacyBefore.Id, legacyAfter.Id);
            Assert.Equal(legacyBefore.Revision, legacyAfter.Revision);
            Assert.Equal(legacyBefore.TargetId, legacyAfter.TargetId);
            Assert.Null(legacyAfter.DeviceId);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AccountTargets_AreOwnerScopedQueryableAndIndependentOfHostAndObserver()
    {
        await using (var db = CreateDbContext())
        {
            db.Users.Add(new User { Id = "owner", Username = "alice" });
            await db.SaveChangesAsync();
        }
        await using var app = CreateApplication();
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
        http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
        var batch = FactStoreTests.SegmentBatch();
        batch.Streams[0].Source = "vrchat.account";
        batch.Streams[0].Subject.Kind = "account";
        batch.Streams[0].Subject.HardwareId = null;
        batch.Streams[0].Dimensions = new Dictionary<string, string>();
        var fact = batch.Facts[0];
        fact.ObserverId = batch.Streams[0].CollectorInstanceId;
        fact.Target = new FactTarget("account", "[\"vrchat\",\"usr_11111111-1111-4111-8111-111111111111\"]");
        fact.Payload = JsonSerializer.SerializeToElement(new { activityKey = "world|instance", title = "World" });
        using var posted = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(posted.IsSuccessStatusCode, await posted.Content.ReadAsStringAsync());
        var row = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
        Assert.Equal("account", row.TargetKind);
        Assert.Null(row.DeviceId);
        Assert.NotNull(row.AppId);
        Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/segments?accountId={row.TargetId}&appId={row.AppId}"))!);
        Assert.Empty((await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/segments?accountId={row.TargetId + 1}"))!);
        batch.Streams[0].StreamId = Guid.NewGuid();
        batch.Streams[0].CollectorInstanceId = Guid.NewGuid();
        fact.StreamId = batch.Streams[0].StreamId;
        fact.ObserverId = batch.Streams[0].CollectorInstanceId;
        using var second = await http.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
        var rows = (await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/segments?accountId={row.TargetId}"))!;
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(f => f.ObserverId).Distinct().Count());
        Assert.Single(rows.Select(f => f.TargetId).Distinct());
    }

    [Fact]
    public async Task BrowserRuntime_V4CacheUpgradeAndNativePublish_ReachHttpQueriesWithStableInstallationAttribution()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-browser-fact-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var package = LocalCollectorPackage.Load(Path.Combine(AppContext.BaseDirectory, "CollectorPackages", "Browser"));
            var path = Path.Combine(directory, "runtime.json");
            var options = new CollectorRuntimeOptions { EnableFactUpload = true };
            var device = Guid.NewGuid();
            var observer = Guid.NewGuid();
            Guid instanceId;
            FactSubmission original;
            async Task<ExternalHostCollectorActivation> Activate(CollectorRuntime runtime, Guid instance)
            {
                var initialization = runtime.BeginExternalHostActivation(instance, package, "browser.extension",
                    package.Artifacts.Single().ContentHash, new ProtocolSupport([1], new Dictionary<string, IReadOnlyList<int>>
                    { ["facts.segment"] = [1], ["diagnostics.stream-gap"] = [1] }), observer.ToString("D"), "mac:com.google.chrome", Guid.CreateVersion7());
                return await runtime.ReadyExternalHostActivationAsync(initialization.ActivationId, 1,
                    [new OutputBinding("tabs", "activeTab", new Dictionary<string, string>())]);
            }
            using (var runtime = CollectorRuntime.Open(path, new UnusedProjection(), options))
            {
                instanceId = runtime.CreateInstance(package, new SubjectReference(device, SubjectKind.Machine),
                    new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { enabled = true, flushPeriodMs = 30000 }))).CollectorInstanceId;
                var activation = await Activate(runtime, instanceId);
                var streamId = activation.Streams["tabs"].StreamId;
                var start = DateTimeOffset.UtcNow.AddMinutes(-2);
                original = new FactSubmission(streamId, Guid.CreateVersion7(), 1, null,
                    new SegmentFactTime(start, start.AddMinutes(1), true),
                    JsonSerializer.SerializeToElement(new { identityKey = "https://browser.example", attributes = new { windowId = 12 } }));
                Assert.Equal(FactDeliveryStatus.Committed, Assert.Single((await activation.PublishAsync(streamId, Guid.CreateVersion7(), [original])).Results).Status);
            }
            var cache = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            cache["schemaVersion"] = 4;
            foreach (var fact in cache["facts"]!.AsArray())
            {
                fact!.AsObject().Remove("observerId"); fact.AsObject().Remove("target");
                fact["payload"] = System.Text.Json.Nodes.JsonNode.Parse(original.Payload.GetRawText());
            }
            File.WriteAllText(path, cache.ToJsonString());
            using var restarted = CollectorRuntime.Open(path, new UnusedProjection(), options);
            Assert.True(File.Exists(path + ".v4.bak"));
            var activationAfterRestart = await Activate(restarted, instanceId);
            var newSnapshot = original with { ObserverId = observer,
                Target = new ApplicationContextReference(device.ToString("D"), "mac:com.google.chrome").ToTarget(),
                Payload = JsonSerializer.SerializeToElement(new { activityKey = "https://browser.example", attributes = new { windowId = 12 } }) };
            Assert.Equal(FactDeliveryStatus.Duplicate, Assert.Single((await activationAfterRestart.PublishAsync(original.StreamId, Guid.CreateVersion7(), [newSnapshot])).Results).Status);
            var pending = restarted.ReadPendingFacts();
            var upload = FactUploadItem.Request(pending);
            await using (var db = CreateDbContext())
            {
                db.Users.Add(new User { Id = "owner", Username = "alice" });
                await db.SaveChangesAsync();
            }
            await using var app = CreateApplication();
            using var http = app.CreateClient();
            http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
            http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
            using var response = await http.PostAsJsonAsync("/api/v1/facts", upload);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var row = Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!);
            Assert.Equal(observer, row.ObserverId);
            Assert.Equal("application-context", row.TargetKind);
            Assert.Equal(original.FactId, row.FactId);
            Assert.Equal(12, row.Payload.GetProperty("attributes").GetProperty("windowId").GetInt32());
            Assert.Single((await http.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/segments?deviceId={row.DeviceId}&appId={row.AppId}"))!);
            restarted.ConfirmUploadedFacts(pending);
            Assert.Empty(restarted.ReadPendingFacts());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task BrowserWindows_KeepIndependentFactsInOneApplicationContext_AndReplayOldSnapshots()
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
        var observer = Guid.NewGuid();
        batch.Streams[0].Dimensions = new Dictionary<string, string>
        {
            ["appIdentityKey"] = "mac:com.google.chrome", ["externalHostIdentity"] = observer.ToString("D")
        };
        var first = batch.Facts[0];
        first.ObserverId = observer;
        first.Target = new FactTarget("application-context", "[\"hardware\",\"mac:com.google.chrome\"]");
        first.Payload = JsonSerializer.SerializeToElement(new { activityKey = "https://example.com", attributes = new { windowId = 11 } });
        batch.Facts.Add(new FactSnapshot
        {
            StreamId = first.StreamId, FactId = Guid.CreateVersion7(), Revision = 1, Start = first.Start,
            End = first.End, IsFinal = true, ObserverId = observer, Target = first.Target,
            Payload = JsonSerializer.SerializeToElement(new { activityKey = "https://example.org", attributes = new { windowId = 22 } })
        });
        using var uploaded = await client.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(uploaded.IsSuccessStatusCode, await uploaded.Content.ReadAsStringAsync());
        var activity = (await client.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/segments")).EnumerateArray().ToArray();
        Assert.Equal(2, activity.Length);
        var deviceId = activity[0].GetProperty("deviceId").GetInt64();
        var rows = (await client.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/segments?deviceId={deviceId}"))!;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => { Assert.Equal(observer, row.ObserverId); Assert.Equal("application-context", row.TargetKind); });
        Assert.Single(rows.Select(row => row.TargetId).Distinct());
        Assert.Equal(new[] { 11, 22 }, rows.Select(row => row.Payload.GetProperty("attributes").GetProperty("windowId").GetInt32()).Order().ToArray());
        foreach (var fact in batch.Facts) { fact.ObserverId = null; fact.Target = null; }
        using var replay = await client.PostAsJsonAsync("/api/v1/facts", batch);
        Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
        var after = (await client.GetFromJsonAsync<List<FactResponse>>($"/api/v1/users/alice/facts/segments?deviceId={deviceId}"))!;
        Assert.Equal(rows.Select(row => row.Id).Order(), after.Select(row => row.Id).Order());
        Assert.All(activity, row => Assert.False(row.TryGetProperty("subjectId", out _)));
    }

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
            StreamId = batch.Streams[0].StreamId,
            FactId = Guid.CreateVersion7(),
            Revision = 1,
            Start = batch.Facts[0].Start,
            End = batch.Facts[0].End,
            IsFinal = false,
            Payload = batch.Facts[0].Payload
        });
        batch.Facts[1].Payload = JsonSerializer.SerializeToElement(new { identityKey = "different", title = "Changed" });
        using (var conflict = await client.PostAsJsonAsync("/api/v1/facts", batch))
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using (var db = CreateDbContext())
        {
            Assert.Single(await db.Segments.ToListAsync());
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
        Assert.Equal(1, (await verify.Segments.SingleAsync()).Revision);
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

            // Replay the exact pre-attribution v3 shape, including its original durable fact identity.
            var oldCache = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            oldCache["schemaVersion"] = 3;
            foreach (var fact in oldCache["facts"]!.AsArray())
            {
                fact!.AsObject().Remove("observerId");
                fact.AsObject().Remove("target");
            }
            File.WriteAllText(path, oldCache.ToJsonString());
            await using var application = CreateApplication();
            using var http = application.CreateClient();
            http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
            http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
            using var restarted = CollectorRuntime.Open(path, sink, options);
            await using var resumed = await restarted.ActivateInProcessAsync(instanceId, package, new PayloadCollector(package));
            var writer = resumed.Streams[kind == "segment" ? "foreground" : "input-events"];
            var first = restarted.ReadPendingFacts();
            Assert.Equal(instanceId, Assert.Single(first).Fact!.ObserverId);
            Assert.Equal(new FactTarget("device", first[0].Stream.Subject.HardwareId!), first[0].Fact!.Target);
            Assert.True(File.Exists(path + ".v3.bak"));
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
            IFactRecord saved = kind == "segment" ? await db.Segments.SingleAsync() : await db.Events.SingleAsync();
            Assert.Equal(2, saved.Revision);
            Assert.True(JsonElement.DeepEquals(corrected.Payload, saved.Payload.RootElement));
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
            Assert.Equal(2, await db.Segments.CountAsync() + await db.Events.CountAsync());
            IFactRecord ownerFact = kind == "segment" ? await db.Segments.SingleAsync(f => f.OwnerId == "owner") : await db.Events.SingleAsync(f => f.OwnerId == "owner");
            Assert.True(JsonElement.DeepEquals(corrected.Payload, ownerFact.Payload.RootElement));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SystemPublisher_RestartAndResume_PreservesObserverAndDeviceForBothFamilies()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-system-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
            var path = Path.Combine(directory, "runtime.json");
            var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine);
            var options = new CollectorRuntimeOptions { EnableFactUpload = true };
            Guid instanceId;
            var inputId = Guid.CreateVersion7();
            var segmentId = Guid.CreateVersion7();
            var start = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds());
            using (var runtime = CollectorRuntime.Open(path, new UnusedProjection(), options))
            {
                instanceId = runtime.CreateInstance(package, subject,
                    new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { }))).CollectorInstanceId;
                var publisher = new SystemCollectorProtocolAdapter();
                var clock = new Heartbeat.Collection.Hub.Time.SystemClock();
                var sink = new SegmentIngestService(clock);
                using var monitor = new AppMonitorService(clock, new DesktopSource(), new InputSignal(), publisher, sink, new DesktopSettings());
                using var collector = new SystemInProcessCollector(publisher, monitor);
                await using var activation = await runtime.ActivateInProcessAsync(instanceId, package, collector);
                publisher.Publish(new ForegroundSegmentSnapshot(segmentId, 1, "code/main.cs", "win:code", "Code", "main.cs", start, start.AddMinutes(1), true));
                publisher.Publish(new Heartbeat.Core.DTOs.Input.InputEventItem
                {
                    Id = inputId, Timestamp = start, EventType = Heartbeat.Core.DTOs.Input.InputEventType.MouseButton,
                    CodeSet = Heartbeat.Core.DTOs.Input.InputCodeSets.HeartbeatKeyPositionV1, Code = 1
                });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (runtime.ReadPendingFacts().Count < 2) await Task.Delay(10, timeout.Token);
            }
            using var restarted = CollectorRuntime.Open(path, new UnusedProjection(), options);
            var pending = restarted.ReadPendingFacts();
            var upload = FactUploadItem.Request(pending);
            var wire = JsonSerializer.SerializeToElement(upload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            foreach (var fact in wire.GetProperty("facts").EnumerateArray())
            {
                Assert.Equal(instanceId, fact.GetProperty("observerId").GetGuid());
                Assert.Equal("device", fact.GetProperty("target").GetProperty("kind").GetString());
                Assert.Equal(subject.SubjectId.ToString("D"), fact.GetProperty("target").GetProperty("reference").GetString());
            }
            await using (var db = CreateDbContext())
            {
                db.Users.Add(new User { Id = "owner", Username = "alice" });
                await db.SaveChangesAsync();
            }
            await using var application = CreateApplication();
            using var http = application.CreateClient();
            http.DefaultRequestHeaders.Add(HeartbeatProtocol.VersionHeader, HeartbeatProtocol.RequiredVersion);
            http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
            using var accepted = await http.PostAsJsonAsync("/api/v1/facts", upload);
            Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
            using var devices = JsonDocument.Parse(await http.GetStringAsync("/api/v1/users/alice/devices"));
            var deviceId = Assert.Single(devices.RootElement.EnumerateArray()).GetProperty("id").GetInt64();
            foreach (var kind in new[] { "segments", "events" })
            {
                using var read = await http.GetAsync($"/api/v1/users/alice/facts/{kind}?deviceId={deviceId}");
                Assert.True(read.StatusCode == HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
                using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
                var rows = document.RootElement.EnumerateArray().ToArray();
                Assert.NotEmpty(rows);
                Assert.All(rows, row =>
                {
                    Assert.Equal(instanceId, row.GetProperty("observerId").GetGuid());
                    Assert.Equal("device", row.GetProperty("targetKind").GetString());
                    Assert.Equal(deviceId, row.GetProperty("targetId").GetInt64());
                });
                if (kind == "events")
                {
                    var row = Assert.Single(rows);
                    Assert.Equal(inputId, row.GetProperty("factId").GetGuid());
                    Assert.Equal(start, row.GetProperty("occurredAt").GetDateTimeOffset());
                    Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(new
                    { eventType = "mouseButton", codeSet = "heartbeat-key-position-v1", code = 1 }), row.GetProperty("payload")));
                }
                else
                {
                    var row = Assert.Single(rows, row => row.GetProperty("factId").GetGuid() == segmentId);
                    Assert.Equal(start, row.GetProperty("start").GetDateTimeOffset());
                    Assert.Equal(start.AddMinutes(1), row.GetProperty("end").GetDateTimeOffset());
                    Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(new
                    { activityKey = "code/main.cs", appIdentityKey = "win:code", appDisplayName = "Code", title = "main.cs" }), row.GetProperty("payload")));
                }
            }
            var dayStart = new DateTimeOffset(start.UtcDateTime.Date, TimeSpan.Zero);
            var query = $"version=1&kind=day&localDate={dayStart:yyyy-MM-dd}&timeZone=Etc%2FUTC&start={Uri.EscapeDataString(dayStart.ToString("O"))}&endExclusive={Uri.EscapeDataString(dayStart.AddDays(1).ToString("O"))}";
            using var experience = JsonDocument.Parse(await http.GetStringAsync("/api/v1/users/alice/experience?" + query));
            var experienceRow = Assert.Single(experience.RootElement.GetProperty("items").EnumerateArray(), row => row.GetProperty("factId").GetGuid() == segmentId);
            Assert.Equal(instanceId, experienceRow.GetProperty("observerId").GetGuid());
            Assert.Equal("device", experienceRow.GetProperty("targetKind").GetString());
            Assert.Equal(deviceId, experienceRow.GetProperty("targetId").GetInt64());
            Assert.False(experienceRow.TryGetProperty("subjectId", out _));
            using var activity = JsonDocument.Parse(await http.GetStringAsync($"/api/v1/users/alice/segments?source=system&deviceId={deviceId}"));
            var activityRow = Assert.Single(activity.RootElement.EnumerateArray(), row => row.GetProperty("factId").GetGuid() == segmentId);
            Assert.Equal(deviceId, activityRow.GetProperty("targetId").GetInt64());
            Assert.False(activityRow.TryGetProperty("subjectId", out _));
            using var replay = await http.PostAsJsonAsync("/api/v1/facts", upload);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            restarted.ConfirmUploadedFacts(pending);
            Assert.Empty(restarted.ReadPendingFacts());
            // Stopping/resuming uses the persisted instance; another instance on this same device is independent.
            var independent = restarted.CreateInstance(package, subject,
                new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { })));
            foreach (var observer in new[] { instanceId, independent.CollectorInstanceId })
            {
                var publisher = new SystemCollectorProtocolAdapter();
                var clock = new Heartbeat.Collection.Hub.Time.SystemClock();
                var sink = new SegmentIngestService(clock);
                using var monitor = new AppMonitorService(clock, new DesktopSource(), new InputSignal(), publisher, sink, new DesktopSettings());
                using var collector = new SystemInProcessCollector(publisher, monitor);
                await using var activation = await restarted.ActivateInProcessAsync(observer, package, collector);
                var eventId = Guid.CreateVersion7();
                publisher.Publish(new Heartbeat.Core.DTOs.Input.InputEventItem
                {
                    Id = eventId, Timestamp = start, EventType = Heartbeat.Core.DTOs.Input.InputEventType.MouseButton,
                    CodeSet = Heartbeat.Core.DTOs.Input.InputCodeSets.HeartbeatKeyPositionV1, Code = 2
                });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!restarted.ReadPendingFacts().Any(item => item.Fact?.FactId == eventId)) await Task.Delay(10, timeout.Token);
                var pendingEvent = Assert.Single(restarted.ReadPendingFacts(), item => item.Fact?.FactId == eventId);
                Assert.Equal(observer, pendingEvent.Fact!.ObserverId);
            }
            using var resumedUpload = await http.PostAsJsonAsync("/api/v1/facts", FactUploadItem.Request(restarted.ReadPendingFacts()));
            Assert.True(resumedUpload.IsSuccessStatusCode, await resumedUpload.Content.ReadAsStringAsync());
            using var events = JsonDocument.Parse(await http.GetStringAsync($"/api/v1/users/alice/facts/events?deviceId={deviceId}"));
            var eventRows = events.RootElement.EnumerateArray().ToArray();
            Assert.Equal(3, eventRows.Length);
            Assert.Equal(2, eventRows.Count(row => row.GetProperty("observerId").GetGuid() == instanceId));
            Assert.Single(eventRows, row => row.GetProperty("observerId").GetGuid() == independent.CollectorInstanceId);
            http.DefaultRequestHeaders.Remove("X-Test-Owner");
            http.DefaultRequestHeaders.Add("X-Test-Owner", "other");
            using var hidden = await http.GetAsync($"/api/v1/users/alice/facts/events?deviceId={deviceId}");
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class DesktopSource : Heartbeat.Collector.System.Observations.IDesktopObservationSource
    {
        public event Action<Heartbeat.Collector.System.Observations.DesktopObservation>? Observation { add { } remove { } }
        public Heartbeat.Collector.System.Observations.DesktopActivity CurrentActivity => Heartbeat.Collector.System.Observations.DesktopActivity.None;
        public void Start() { }
        public void Stop() { }
    }
    private sealed class InputSignal : Heartbeat.Collector.System.Input.IInputActivitySignal
    {
        public void MarkClick() { }
        public bool ClickedWithin(TimeSpan window) => false;
    }
    private sealed class DesktopSettings : Heartbeat.Collector.System.Configuration.IDesktopSettings
    {
        public IReadOnlyList<string> AwayProcessNames => [];
        public bool SplitFocusedWindowChangesUnconditionally => true;
        public event Action<IReadOnlyList<string>>? AwayProcessNamesChanged { add { } remove { } }
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
