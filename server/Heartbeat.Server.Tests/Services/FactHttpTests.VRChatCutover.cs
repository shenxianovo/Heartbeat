using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core.DTOs.Facts;
using Heartbeat.Core.DTOs.Persons;
using Heartbeat.Server.Entities;

namespace Heartbeat.Server.Tests.Services;

public sealed partial class FactHttpTests
{
    [Fact]
    public async Task VRChatNativeManagedProcess_CrashRecoveryKeepsAccountsAndExactSnapshotsThroughHttp()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-vrchat-cutover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var package = await BuildVRChatCutoverPackage(directory);
            var path = Path.Combine(directory, "runtime.json");
            var secrets = new EncryptedFileCollectorSecretStore(Path.Combine(directory, "secrets"));
            var accounts = new[] { "usr_11111111-1111-4111-8111-111111111111", "usr_22222222-2222-4222-8222-222222222222" };
            var observers = new List<Guid>();
            List<FactUploadItem> original;
            using (var runtime = CollectorRuntime.Open(path, new UnusedProjection(), secretStore: secrets))
            {
                var activations = new List<ManagedProcessCollectorActivation>();
                try
                {
                    foreach (var account in accounts)
                    {
                        var observer = runtime.CreateInstance(package, new SubjectReference(Guid.NewGuid(), SubjectKind.Account),
                            new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { pollIntervalSeconds = 1 }))).CollectorInstanceId;
                        observers.Add(observer);
                        activations.Add(await ActivateVRChatCutover(runtime, observer, package, account, authorize: true));
                    }
                    await WaitVRChatCutover(() => runtime.ReadPendingFacts().Count == 2);
                    Assert.All(runtime.ReadPendingFacts(), item => Assert.NotNull(item.Observation));
                    await WaitVRChatCutover(() => runtime.ReadPendingFacts().All(item => item.Observation!.Revision >= 2));
                    // Crash only the processes created by this test. No drain means the real active
                    // checkpoints, not hand-authored HTTP snapshots, own recovery after restart.
                    foreach (var activation in activations)
                    {
                        using var process = Process.GetProcessById(activation.ProcessId);
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                        await activation.Completion.WaitAsync(TimeSpan.FromSeconds(20));
                    }
                    original = runtime.ReadPendingFacts();
                    Assert.Equal(2, original.Count);
                    foreach (var (account, observer) in accounts.Zip(observers))
                    {
                        var item = Assert.Single(original, item => item.Observation!.CollectorId == observer);
                        AssertVRChatCutoverObservation(item, account, observer);
                        Assert.False(item.IsFinal);
                    }
                }
                finally
                {
                    foreach (var activation in activations) await activation.DisposeAsync();
                }
            }

            await using (var db = CreateDbContext())
            {
                db.Users.Add(new User { Id = "owner", Username = "alice" });
                await db.SaveChangesAsync();
            }
            await using var app = CreateApplication();
            var failure = new VRChatCutoverGapFailure();
            using var http = app.CreateDefaultClient(failure);
            http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
            var api = new HeartbeatApiClient(http);
            using (var restarted = CollectorRuntime.Open(path, new UnusedProjection(), secretStore: secrets))
            {
                Assert.Equal(original.Select(item => item.Observation!.Id).Order(),
                    restarted.ReadPendingFacts().Select(item => item.Observation!.Id).Order());
                var activations = new List<ManagedProcessCollectorActivation>();
                try
                {
                    foreach (var (account, observer) in accounts.Zip(observers))
                        activations.Add(await ActivateVRChatCutover(restarted, observer, package, account));
                    await WaitVRChatCutover(() => restarted.ReadPendingFacts().Count(item => item.Observation is not null) == 4
                        && restarted.ReadPendingFacts().Count(item => item.Gap is not null) == 2);
                    await WaitVRChatCutover(() => restarted.ReadPendingFacts()
                        .Where(item => item.Observation is not null && !item.IsFinal.GetValueOrDefault())
                        .All(item => item.Observation!.Revision >= 2));
                    foreach (var activation in activations) await activation.StopAsync();
                }
                finally
                {
                    foreach (var activation in activations) await activation.DisposeAsync();
                }

                var latest = restarted.ReadPendingFacts();
                Assert.Equal(4, latest.Count(item => item.Observation is not null));
                Assert.Equal(2, latest.Count(item => item.Gap is not null));
                foreach (var prior in original)
                {
                    var recovered = Assert.Single(latest, item => item.Observation?.Id == prior.Observation!.Id);
                    Assert.True(recovered.IsFinal);
                    Assert.True(recovered.Observation!.Revision > prior.Observation!.Revision);
                    Assert.Equal(prior.Observation.Start, recovered.Observation.Start);
                    Assert.Equal(prior.Observation.End, recovered.Observation.End);
                    Assert.Equal(prior.Observation.CollectorId, recovered.Observation.CollectorId);
                    Assert.Equal(prior.Observation.Foi, recovered.Observation.Foi);
                    Assert.True(JsonElement.DeepEquals(prior.Observation.Result!.Value, recovered.Observation.Result!.Value));
                    var gapItem = Assert.Single(latest, item => item.Gap is not null
                        && item.Stream!.CollectorInstanceId == prior.Observation.CollectorId);
                    Assert.NotEqual(Guid.Empty, gapItem.Gap!.GapId);
                    Assert.Equal("process_restart", gapItem.Gap.Reason);
                    Assert.Equal(prior.Observation.End, gapItem.Gap.Start);
                    Assert.True(gapItem.Gap.End > gapItem.Gap.Start);
                    var newSession = Assert.Single(latest, item => item.Observation is not null
                        && item.Observation.CollectorId == prior.Observation.CollectorId
                        && item.Observation.Id != prior.Observation.Id);
                    Assert.True(newSession.Observation!.Start >= gapItem.Gap.End);
                }

                Assert.True((await api.UploadFactsAsync(original)).Success);
                restarted.ConfirmUploadedFacts(original); // late HTTP ACK cannot discard the final checkpoint version.
                Assert.Equal(latest.Count, restarted.ReadPendingFacts().Count);
                failure.FailNextGap = true;
                var interrupted = await api.UploadFactsAsync(latest);
                Assert.False(interrupted.Success); // /observations committed, while /facts Gap failed.
                Assert.Equal(latest.Count, restarted.ReadPendingFacts().Count);
                Assert.True((await api.UploadFactsAsync(latest)).Success);
                Assert.True((await api.UploadFactsAsync(latest)).Success);
                Assert.True((await api.UploadFactsAsync(original)).Success); // out-of-order older snapshots.

                var rows = (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!;
                Assert.Equal(4, rows.Count);
                foreach (var (account, observer) in accounts.Zip(observers))
                {
                    var ownRows = rows.Where(row => row.ObserverId == observer).ToArray();
                    Assert.Equal(2, ownRows.Length);
                    Assert.Single(ownRows.Select(row => row.FoiId).Distinct());
                    foreach (var row in ownRows)
                    {
                        var sent = Assert.Single(latest, item => item.Observation?.Id == row.Id);
                        AssertVRChatCutoverObservation(sent, account, observer);
                        Assert.Equal("account", row.Foi!.Kind);
                        Assert.Equal(account, row.Foi.Key);
                        Assert.Equal("account-location", row.Aspect);
                        Assert.Equal("vrchat.account", row.Source);
                        Assert.Equal(sent.Observation!.Revision, row.Revision);
                        Assert.Equal(sent.Observation.Start, row.Start);
                        Assert.Equal(sent.Observation.End, row.End);
                        Assert.True(JsonElement.DeepEquals(sent.Observation.Result!.Value, row.Result));
                        Assert.Null(row.StreamId);
                        Assert.Null(row.FactId);
                        Assert.Null(row.DeviceId);
                        Assert.Empty(row.Relations);
                    }
                    var byObject = (await http.GetFromJsonAsync<List<FactResponse>>(
                        $"/api/v1/users/alice/facts/segments?foiId={ownRows[0].FoiId}"))!;
                    Assert.Equal(2, byObject.Count);
                    Assert.All(byObject, row => Assert.Equal(observer, row.ObserverId));
                }
                Assert.Equal(2, rows.Select(row => row.FoiId).Distinct().Count());
                using var established = await http.PutAsync("/api/v1/me/person", null);
                established.EnsureSuccessStatusCode();
                const string personQuery = "/api/v1/me/person/facts/segments";
                Assert.Empty((await http.GetFromJsonAsync<PersonFactPage>(personQuery))!.Items);
                var associatedRows = rows.Where(row => row.Foi!.Key == accounts[0]).ToArray();
                using var linked = await http.PostAsJsonAsync("/api/v1/me/person/associations",
                    new { objectId = associatedRows[0].FoiId, start = (DateTimeOffset?)null, end = (DateTimeOffset?)null });
                linked.EnsureSuccessStatusCode();
                var association = (await linked.Content.ReadFromJsonAsync<PersonAssociationResponse>())!;
                // Replay the actual managed-process snapshots after manual association. The
                // other account sharing this Runtime must never acquire personal membership.
                Assert.True((await api.UploadFactsAsync(original)).Success);
                Assert.True((await api.UploadFactsAsync(latest)).Success);
                var personal = (await http.GetFromJsonAsync<PersonFactPage>(personQuery))!;
                Assert.Equal(associatedRows.Select(row => row.Id).Order(), personal.Items.Select(item => item.Fact.Id).Order());
                Assert.All(personal.Items, item =>
                {
                    Assert.Equal(accounts[0], item.Fact.Foi!.Key);
                    Assert.Null(item.Fact.DeviceId);
                    Assert.Empty(item.Fact.Relations);
                    Assert.True(JsonElement.DeepEquals(
                        JsonSerializer.SerializeToElement(associatedRows.Single(row => row.Id == item.Fact.Id)),
                        JsonSerializer.SerializeToElement(item.Fact)));
                });
                var replay = await http.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/segments?source=vrchat.account");
                Assert.Equal(rows.Select(row => row.Id).Order(), replay.EnumerateArray().Select(row => row.GetProperty("id").GetGuid()).Order());
                Assert.All(replay.EnumerateArray(), row => Assert.Equal(JsonValueKind.Null, row.GetProperty("deviceId").ValueKind));
                using var unlinked = await http.DeleteAsync($"/api/v1/me/person/associations/{association.Id}");
                unlinked.EnsureSuccessStatusCode();
                Assert.Empty((await http.GetFromJsonAsync<PersonFactPage>(personQuery))!.Items);
                Assert.Equal(4, (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!.Count);
                var finalFact = latest.First(item => item.Observation is not null);
                restarted.ConfirmUploadedFacts([finalFact with { IsFinal = false }]);
                Assert.Equal(latest.Count, restarted.ReadPendingFacts().Count);
                restarted.ConfirmUploadedFacts(latest);
                Assert.Empty(restarted.ReadPendingFacts());
            }
            using var confirmed = CollectorRuntime.Open(path, new UnusedProjection(), secretStore: secrets);
            Assert.Empty(confirmed.ReadPendingFacts());
            Assert.DoesNotContain("mock-auth", File.ReadAllText(path));
            var dataDirectory = Path.Combine(directory, "collector-data", observers[0].ToString("N"));
            var savedFiles = Directory.GetFiles(dataDirectory).ToDictionary(file => file, File.ReadAllBytes);
            Assert.Contains(savedFiles.Keys, file => file.EndsWith("collector-data-requirements.json", StringComparison.Ordinal));
            using (var outbox = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDirectory, "collector-protocol-outbox.json"))))
            {
                Assert.Empty(outbox.RootElement.GetProperty("State").GetProperty("Facts").EnumerateArray());
                Assert.Empty(outbox.RootElement.GetProperty("State").GetProperty("Gaps").EnumerateArray());
            }
            var manifestPath = Path.Combine(directory, "package", "collector-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest["supportedCapabilities"]!["facts.observation"] = new JsonArray(1);
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            var oldPackage = LocalCollectorPackage.Load(Path.GetDirectoryName(manifestPath)!);
            var incompatible = await Assert.ThrowsAsync<CollectorActivationException>(() =>
                ActivateVRChatCutover(confirmed, observers[0], oldPackage, accounts[0]));
            Assert.Equal("collector_cache_incompatible", incompatible.Error.Code);
            Assert.Contains("Collector data requires", incompatible.Message);
            Assert.Equal(savedFiles.Keys.Order(), Directory.GetFiles(dataDirectory).Order());
            foreach (var (file, bytes) in savedFiles) Assert.Equal(bytes, File.ReadAllBytes(file));

        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(2, null)]
    [InlineData(3, "usr_33333333-3333-4333-8333-333333333333")]
    public async Task VRChatLegacyManagedCheckpoint_KeepsHistoricalAccountAndDeliveryIdentityThroughHttp(int schema, string? historicalAccount)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-vrchat-legacy-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var package = await BuildVRChatCutoverPackage(directory);
            var path = Path.Combine(directory, "runtime.json");
            var secrets = new EncryptedFileCollectorSecretStore(Path.Combine(directory, "secrets"));
            var currentAccount = "usr_11111111-1111-4111-8111-111111111111";
            var oldId = Guid.Parse("018f0000-0000-7000-8000-000000000066");
            var start = DateTimeOffset.Parse("2026-08-27T10:00:00+00:00");
            var end = start.AddMinutes(1);
            Guid observer;
            using (var runtime = CollectorRuntime.Open(path, new UnusedProjection(), secretStore: secrets))
                observer = runtime.CreateInstance(package, new SubjectReference(Guid.NewGuid(), SubjectKind.Account),
                    new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { pollIntervalSeconds = 1 }))).CollectorInstanceId;
            var dataDirectory = Path.Combine(directory, "collector-data", observer.ToString("N"));
            Directory.CreateDirectory(dataDirectory);
            // Historical persisted DTO shapes, not a current DTO relabelled as an old schema.
            var active = JsonNode.Parse("""
                {"FactId":"018f0000-0000-7000-8000-000000000066","Revision":7,
                 "Start":"2026-08-27T10:00:00+00:00","End":"2026-08-27T10:01:00+00:00","IsFinal":false,
                 "IdentityKey":"wrld_historical|historical-instance","Title":"Historical World",
                 "WorldId":"wrld_historical","WorldName":"Historical World","InstanceId":"historical-instance"}
                """)!;
            if (schema == 3)
            {
                active.AsObject().Remove("IdentityKey", out var identity);
                active["ActivityKey"] = identity;
                active["ObservedAccountId"] = historicalAccount;
            }
            var envelope = new JsonObject { ["SchemaVersion"] = schema, ["Active"] = active };
            if (schema >= 2)
            {
                envelope["PendingFacts"] = new JsonArray(active.DeepClone());
                envelope["PendingGaps"] = JsonNode.Parse("""
                    [{"GapId":"018f0000-0000-7000-8000-000000000067","Start":"2026-08-27T09:58:00+00:00",
                      "End":"2026-08-27T09:59:00+00:00","Reason":"historical_gap"}]
                    """);
            }
            var checkpointPath = Path.Combine(dataDirectory, "vrchat-presence.json");
            var historicalBytes = envelope.ToJsonString();
            File.WriteAllText(checkpointPath, historicalBytes);
            List<FactUploadItem> pending;
            using (var runtime = CollectorRuntime.Open(path, new UnusedProjection(), secretStore: secrets))
            {
                await using var activation = await ActivateVRChatCutover(runtime, observer, package, currentAccount, authorize: true);
                await WaitVRChatCutover(() => runtime.ReadPendingFacts().Any(item => item.Observation is not null));
                await activation.StopAsync();
                pending = runtime.ReadPendingFacts();
                var legacy = Assert.Single(pending, item => item.Fact is not null);
                Assert.Null(legacy.Observation);
                Assert.Equal(oldId, legacy.Fact!.FactId);
                Assert.Equal(8, legacy.Fact.Revision);
                Assert.True(legacy.Fact.IsFinal);
                Assert.Equal(start, legacy.Fact.Start);
                Assert.Equal(end, legacy.Fact.End);
                Assert.Equal(legacy.Stream!.StreamId, legacy.Fact.StreamId);
                Assert.NotEqual(currentAccount, legacy.Fact.Foi?.Key);
                if (historicalAccount is not null) Assert.Equal(historicalAccount, legacy.Fact.Foi!.Key);
                else Assert.Null(legacy.Fact.Foi);
                var native = Assert.Single(pending, item => item.Observation is not null);
                AssertVRChatCutoverObservation(native, currentAccount, observer);
                Assert.NotEqual(oldId, native.Observation!.Id);
                if (schema >= 2)
                {
                    var oldGap = Assert.Single(pending, item => item.Gap?.Reason == "historical_gap");
                    Assert.Equal(Guid.Parse("018f0000-0000-7000-8000-000000000067"), oldGap.Gap!.GapId);
                    Assert.Equal(start.AddMinutes(-2), oldGap.Gap.Start);
                    Assert.Equal(start.AddMinutes(-1), oldGap.Gap.End);
                }
                Assert.Equal(historicalBytes, File.ReadAllText(checkpointPath + $".v{schema}.bak"));
            }
            await using (var db = CreateDbContext())
            {
                db.Users.Add(new User { Id = "owner", Username = "alice" });
                await db.SaveChangesAsync();
            }
            await using var app = CreateApplication();
            using var http = app.CreateClient();
            http.DefaultRequestHeaders.Add("X-Test-Owner", "owner");
            var api = new HeartbeatApiClient(http);
            using var restarted = CollectorRuntime.Open(path, new UnusedProjection(), secretStore: secrets);
            Assert.True((await api.UploadFactsAsync(restarted.ReadPendingFacts())).Success);
            Assert.True((await api.UploadFactsAsync(pending)).Success);
            var rows = (await http.GetFromJsonAsync<List<FactResponse>>("/api/v1/users/alice/facts/segments"))!;
            Assert.Equal(2, rows.Count);
            var historical = Assert.Single(rows, row => row.FactId == oldId);
            Assert.NotEqual(oldId, historical.Id);
            Assert.NotNull(historical.StreamId);
            Assert.Equal(8, historical.Revision);
            Assert.Equal(start, historical.Start);
            Assert.Equal(end, historical.End);
            Assert.Equal("Historical World", historical.Result.GetProperty("worldName").GetString());
            Assert.NotEqual(currentAccount, historical.Foi?.Key);
            if (historicalAccount is not null) Assert.Equal(historicalAccount, historical.Foi!.Key);
            else Assert.Null(historical.Foi);
            var replayedLegacy = Assert.Single(pending, item => item.Fact is not null).Fact!;
            Assert.Equal(replayedLegacy.StreamId, historical.StreamId);
            Assert.True(JsonElement.DeepEquals(replayedLegacy.Payload!.Value, historical.Result));
            Assert.Null(historical.DeviceId);
            Assert.Empty(historical.Relations);
            var current = Assert.Single(rows, row => row.StreamId is null);
            Assert.Equal(currentAccount, current.Foi!.Key);
            Assert.NotEqual(historical.FoiId, current.FoiId);
            Assert.Equal(observer, current.ObserverId);
            restarted.ConfirmUploadedFacts(restarted.ReadPendingFacts());
            Assert.Empty(restarted.ReadPendingFacts());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AssertVRChatCutoverObservation(FactUploadItem item, string account, Guid observer)
    {
        Assert.Null(item.Stream);
        Assert.Null(item.Fact);
        Assert.Null(item.Gap);
        var fact = Assert.IsType<ObservationSnapshot>(item.Observation);
        Assert.NotEqual(Guid.Empty, fact.Id);
        Assert.Equal("segment", fact.Kind);
        Assert.Equal(observer, fact.CollectorId);
        Assert.Equal(new ObservationObjectReference("account", "vrchat", account), fact.Foi);
        Assert.Equal("account-location", fact.Aspect);
        Assert.Equal("vrchat.account", fact.Source);
        Assert.Empty(fact.Relations);
        Assert.Equal("wrld_mock", fact.Result!.Value.GetProperty("worldId").GetString());
        Assert.Equal("Mock World", fact.Result.Value.GetProperty("worldName").GetString());
    }

    private sealed class VRChatCutoverGapFailure : DelegatingHandler
    {
        public bool FailNextGap { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (FailNextGap && request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/facts")
            {
                FailNextGap = false;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("Test transport interrupted Gap upload.") });
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static async Task<LocalCollectorPackage> BuildVRChatCutoverPackage(string directory)
    {
        var packageDirectory = Path.Combine(directory, "package");
        using var build = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "Heartbeat.Collector.VRChat.exe" : "Heartbeat.Collector.VRChat"),
            UseShellExecute = false,
            ArgumentList = { "--create-package", packageDirectory }
        })!;
        await build.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, build.ExitCode);
        return LocalCollectorPackage.Load(packageDirectory);
    }

    private static async Task<ManagedProcessCollectorActivation> ActivateVRChatCutover(
        CollectorRuntime runtime, Guid observer, LocalCollectorPackage package, string account, bool authorize = false)
    {
        var activating = runtime.ActivateManagedProcessAsync(observer, package, new ManagedProcessActivationOptions
        {
            StartupTimeout = TimeSpan.FromSeconds(20), DrainGracePeriod = TimeSpan.FromSeconds(5),
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["HEARTBEAT_VRCHAT_MOCK"] = "1", ["HEARTBEAT_VRCHAT_MOCK_ACCOUNT_ID"] = account
            }
        }).AsTask();
        if (authorize)
        {
            foreach (var (kind, values) in new[]
            {
                (CollectorAuthorizationChallengeKind.Credentials, new Dictionary<string, string> { ["username"] = "test-user", ["password"] = "test-password" }),
                (CollectorAuthorizationChallengeKind.VerificationCode, new Dictionary<string, string> { ["code"] = "123456" })
            })
            {
                await WaitVRChatCutover(() => runtime.GetManagedProcessRuntimeState(observer).AuthorizationChallenge?.Kind == kind);
                var challenge = runtime.GetManagedProcessRuntimeState(observer).AuthorizationChallenge!;
                await runtime.SubmitManagedProcessAuthorizationAsync(observer, challenge.InteractionId, values);
            }
        }
        return await activating.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task WaitVRChatCutover(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
