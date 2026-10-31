using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Observer.Runtime;

namespace Orisumi.Integration.Tests;

public sealed class RuntimeManagementTests
{
    private static CancellationToken Token => TestContext.Current!.Execution.CancellationToken;

    [Test, Property("caseId", "runtime-management-sync")]
    public async Task OfflineLocalChangesSurviveRestartAndMergeWithRemoteFields()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var manager = factory.CreateClient();
        using var pairing = factory.CreateClient();
        var identity = await ManagementApiTests.Pair(manager, pairing);
        using var directory = new RuntimeHostingTests.RuntimeDirectory();
        Directory.CreateDirectory(directory.Data);
        await File.WriteAllTextAsync(Path.Combine(directory.Data, "host.json"), JsonSerializer.Serialize(
            new { hostId = identity.HostId, credential = identity.Credential, api = "http://localhost/" }), Token);
        using var network = new DisconnectableHandler(factory.Server.CreateHandler());
        using var http = new HttpClient(network) { BaseAddress = new("http://localhost/"), Timeout = TimeSpan.FromSeconds(35) };
        Guid instance;
        using (var packages = new PackageStore(directory.Data, directory.Bundled, null, http))
        {
            await using var host = new RuntimeHost(packages, http);
            await host.InitializeAsync(Token);
            host.StartSynchronization();
            await Until(async () => (await Status(host)).GetProperty("sync").GetProperty("revision").GetInt64() > 0);
            instance = packages.Observers.Last(o => o.PackageId == "example").InstanceId;
            network.Offline = true;
            await host.RequestAsync("setEnabled", ClientProtocol.Json(new { instanceId = instance, enabled = false }), Token);
            await host.RequestAsync("setSettings", ClientProtocol.Json(new { instanceId = instance, settings = new { interval = 3 } }), Token);
            var local = await Status(host);
            await Assert.That(local.GetProperty("enabled").GetBoolean()).IsFalse();
            await Assert.That(local.GetProperty("sync").GetProperty("pendingChanges").GetInt32()).IsEqualTo(2);
            using var remote = await manager.PatchAsJsonAsync($"observer-instances/{instance}", new { enabled = true, settings = new { interval = 9 } }, Token);
            remote.EnsureSuccessStatusCode();
        }
        // Use a new HttpClient just as a restarted process does.
        using var restoredHttp = new HttpClient(factory.Server.CreateHandler()) { BaseAddress = new("http://localhost/"), Timeout = TimeSpan.FromSeconds(35) };
        using var restoredPackages = new PackageStore(directory.Data, directory.Bundled, null, restoredHttp);
        await using var restored = new RuntimeHost(restoredPackages, restoredHttp);
        await restored.InitializeAsync(Token);
        await Assert.That(restoredPackages.Observers.Last(o => o.PackageId == "example").Pending.Length).IsEqualTo(2);
        restored.StartSynchronization();
        await Until(async () => (await Status(restored)).GetProperty("sync").GetProperty("pendingChanges").GetInt32() == 0);
        var server = await manager.GetFromJsonAsync<JsonElement>($"observer-instances/{instance}", Token);
        await Assert.That(server.GetProperty("enabled").GetBoolean()).IsFalse();
        await Assert.That(server.GetProperty("settings").GetProperty("interval").GetInt32()).IsEqualTo(3);
        using var remoteSettings = await manager.PatchAsJsonAsync($"observer-instances/{instance}", new { settings = new { interval = 7 } }, Token);
        remoteSettings.EnsureSuccessStatusCode();
        await Until(async () => (await Status(restored)).GetProperty("settings").GetProperty("interval").GetInt32() == 7);
        await Assert.That((await Status(restored)).GetProperty("sync").GetProperty("pendingChanges").GetInt32()).IsEqualTo(0);
        using var removed = await manager.DeleteAsync($"observer-instances/{instance}", Token);
        removed.EnsureSuccessStatusCode();
        await Until(async () => (await Status(restored)).GetProperty("removed").GetBoolean());
        await restored.RequestAsync("install", ClientProtocol.Json(new { packageId = "example" }), Token);
        await Assert.That(restoredPackages.Observers.Last(o => o.PackageId == "example").InstanceId).IsNotEqualTo(instance);
        await Assert.That(restoredPackages.Observers.Count).IsEqualTo(2);
        await Until(async () => {
            var list = await manager.GetFromJsonAsync<JsonElement>("observer-instances", Token);
            return list.GetProperty("instances").GetArrayLength() == 2;
        });
        using var revoked = await manager.PostAsync($"hosts/{identity.HostId}/revoke", null, Token);
        revoked.EnsureSuccessStatusCode();
        await Until(async () => (await restored.RequestAsync("list", ClientProtocol.Json(new { }), Token)).GetProperty("host").GetProperty("connection").GetString() == "revoked");
        await restored.RequestAsync("setEnabled", ClientProtocol.Json(new { instanceId = restoredPackages.Observers.Last(o => o.PackageId == "example").InstanceId, enabled = false }), Token);
        await Assert.That(restoredPackages.Observers.Last(o => o.PackageId == "example").Enabled).IsFalse();
    }

    [Test, Property("caseId", "runtime-management-sync")]
    public async Task AcknowledgementRebasesNewerLocalChangesAndNeverRevivesRemovedInstance()
    {
        var item = new InstalledObserver("example", "Example", "1.0.0", Guid.CreateVersion7(), true, true, ClientProtocol.Json(new { a = 1 }))
            .Change(enabled: false).Change(settings: ClientProtocol.Json(new { a = 2 }));
        var merged = item.Merge(new(item.InstanceId, true, ClientProtocol.Json(new { a = 9 }), 4, 1, false));
        await Assert.That(merged.Enabled).IsTrue();
        await Assert.That(merged.Settings.GetProperty("a").GetInt32()).IsEqualTo(2);
        await Assert.That(merged.Pending.Single().Sequence).IsEqualTo(2);
        var stale = merged.Merge(new(item.InstanceId, false, ClientProtocol.Json(new { }), 3, 2, false));
        await Assert.That(stale).IsSameReferenceAs(merged);
        var removed = merged.Merge(new(item.InstanceId, true, ClientProtocol.Json(new { }), 5, 1, true));
        await Assert.That(removed.Pending.Length).IsEqualTo(0);
        await Assert.That(removed.Merge(new(item.InstanceId, true, ClientProtocol.Json(new { }), 6, 1, false)).Removed).IsTrue();
    }
    private static async Task<JsonElement> Status(RuntimeHost host) => (await host.RequestAsync("list", ClientProtocol.Json(new { }), Token)).GetProperty("observers")[0];
    private static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Runtime did not converge.");
            await Task.Delay(50, Token);
        }
    }
    [Test, Property("caseId", "runtime-management-sync")]
    public async Task ProxyErrorPageCountsAsOffline()
    {
        var directory = Directory.CreateTempSubdirectory("orisumi-connection-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "host.json"),
                JsonSerializer.Serialize(new { hostId = Guid.CreateVersion7(), credential = "credential", api = "http://localhost/" }));
            using var http = new HttpClient(new ProxyErrorHandler()) { BaseAddress = new("http://localhost/") };
            using var connection = new RuntimeConnection(directory.FullName, http, false);
            await Assert.ThrowsAsync<HttpRequestException>(() => connection.SendAsync(HttpMethod.Post, "runtime/poll", new { }, CancellationToken.None));
        }
        finally { directory.Delete(true); }
    }

    private sealed class ProxyErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>502</html>", System.Text.Encoding.UTF8, "text/html") });
    }

    private sealed class DisconnectableHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool Offline { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Offline ? throw new HttpRequestException("Offline") : base.SendAsync(request, token);
    }
}
