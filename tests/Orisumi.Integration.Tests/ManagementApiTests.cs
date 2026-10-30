using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Integration.Tests;

public sealed class ManagementApiTests
{
    internal static async Task<(Guid HostId, string Credential)> Pair(HttpClient manager, HttpClient runtime, Guid? host = null)
    {
        using var issued = await manager.PostAsJsonAsync("/pairings", host is null ? new { } as object : new { hostId = host });
        await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var code = (await EntityApiTests.Json(issued)).GetProperty("code").GetString();
        using var paired = await runtime.PostAsJsonAsync("/runtime/pair", host is null
            ? new { code, name = "Test Runtime", kind = "device" } as object
            : new { code, name = "Ignored rename", kind = "device", hostId = host });
        await Assert.That(paired.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var json = await EntityApiTests.Json(paired);
        var credential = json.GetProperty("credential").GetString()!;
        runtime.DefaultRequestHeaders.Authorization = new("Bearer", credential);
        return (json.GetProperty("hostId").GetGuid(), credential);
    }

    internal static Task<HttpResponseMessage> Register(HttpClient runtime, Guid id, string key = "test", string scope = "host") =>
        runtime.PutAsJsonAsync($"/runtime/instances/{id}", new
        { instanceKey = key, name = "Test", scope, enabled = true, settings = new { value = 1 }, localSequence = 0 });

    [Test]
    [Property("caseId", "api-management")]
    public async Task LocalChangesAreOrderedAndDuplicateDoesNotOverwriteRemoteChange()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var manager = factory.CreateClient();
        using var runtime = factory.CreateClient();
        await Pair(manager, runtime);
        var id = Guid.CreateVersion7();
        using var registered = await Register(runtime, id);
        await Assert.That(registered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var change = await runtime.PostAsJsonAsync($"/runtime/instances/{id}/changes", new { sequence = 1, enabled = false });
        await Assert.That((await EntityApiTests.Json(change)).GetProperty("revision").GetInt64()).IsEqualTo(2);
        using var remote = await manager.PatchAsJsonAsync($"/observer-instances/{id}", new { enabled = true, settings = new { value = 2 } });
        await Assert.That(remote.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var duplicate = await runtime.PostAsJsonAsync($"/runtime/instances/{id}/changes", new { sequence = 1, enabled = false });
        var result = await EntityApiTests.Json(duplicate);
        await Assert.That(result.GetProperty("enabled").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("revision").GetInt64()).IsEqualTo(3);
        await Assert.That(result.GetProperty("settings").GetProperty("value").GetInt32()).IsEqualTo(2);
        using var gap = await runtime.PostAsJsonAsync($"/runtime/instances/{id}/changes", new { sequence = 3, enabled = false });
        await Assert.That(gap.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await EntityApiTests.Json(gap)).GetProperty("lastLocalSequence").GetInt64()).IsEqualTo(1);
        using var next = await runtime.PostAsJsonAsync($"/runtime/instances/{id}/changes", new { sequence = 2, enabled = false });
        result = await EntityApiTests.Json(next);
        await Assert.That(result.GetProperty("enabled").GetBoolean()).IsFalse();
        await Assert.That(result.GetProperty("settings").GetProperty("value").GetInt32()).IsEqualTo(2);
        using var repeatedRegistration = await Register(runtime, id);
        await Assert.That((await EntityApiTests.Json(repeatedRegistration)).GetProperty("revision").GetInt64()).IsEqualTo(4);
    }

    [Test]
    [Property("caseId", "api-management")]
    [Property("caseId", "api-deletion")]
    public async Task RemovingBeforeFirstVersionReservesIdentityAndStopsLocalChanges()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var manager = factory.CreateClient();
        using var runtime = factory.CreateClient();
        await Pair(manager, runtime);
        var schema = EntityApiTests.Schema();
        var instance = EntityApiTests.Business(schema);
        using var registered = await Register(runtime, instance.EntityId.Value);
        using var deleted = await manager.DeleteAsync($"/entities/{instance.EntityId.Value}");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var saved = await EntityApiTests.Submit(runtime, schema, instance);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await EntityApiTests.Json(saved)).GetProperty("rule").GetString()).IsEqualTo("HISTORY-7");
        using var config = await manager.GetAsync($"/observer-instances/{instance.EntityId.Value}");
        await Assert.That((await EntityApiTests.Json(config)).GetProperty("removed").GetBoolean()).IsTrue();
        using var change = await runtime.PostAsJsonAsync($"/runtime/instances/{instance.EntityId.Value}/changes", new { sequence = 1, enabled = true });
        await Assert.That(change.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        using var report = await runtime.PutAsJsonAsync($"/runtime/instances/{instance.EntityId.Value}/status", new { state = "stopped" });
        await Assert.That(report.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var replacement = await Register(runtime, Guid.CreateVersion7());
        await Assert.That(replacement.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Property("caseId", "api-management")]
    public async Task CredentialsEnforceOwnershipRevocationAndRePairing()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        await using var factory = new OrisumiApiFactory(database.ConnectionString);
        using var manager = factory.CreateClient();
        using var runtime = factory.CreateClient();
        using var other = factory.CreateClient();
        var (host, oldCredential) = await Pair(manager, runtime);
        await Pair(manager, other);
        var id = Guid.CreateVersion7();
        using var registered = await Register(runtime, id, "singleton", "deployment");
        using var singleton = await Register(other, Guid.CreateVersion7(), "singleton", "deployment");
        await Assert.That((await EntityApiTests.Json(singleton)).GetProperty("code").GetString()).IsEqualTo("deployment_singleton");
        using var mismatch = await other.PutAsJsonAsync($"/runtime/instances/{id}/status", new { state = "running" });
        await Assert.That(mismatch.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var forbidden = await runtime.DeleteAsync($"/entities/{id}");
        await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var poll = runtime.PostAsJsonAsync("/runtime/poll", new { instances = new[] { new { entityId = id, revision = 1 } } });
        using var revoked = await manager.PostAsync($"/hosts/{host}/revoke", null);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        using var interrupted = await poll;
        await Assert.That(interrupted.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var denied = await runtime.GetAsync("/entities");
        await Assert.That((await EntityApiTests.Json(denied)).GetProperty("code").GetString()).IsEqualTo("credential_revoked");
        runtime.DefaultRequestHeaders.Authorization = null;
        var (sameHost, _) = await Pair(manager, runtime, host);
        await Assert.That(sameHost).IsEqualTo(host);
        using var allowed = await runtime.GetAsync("/entities");
        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldCredential);
        using var obsolete = await other.GetAsync("/entities");
        await Assert.That(obsolete.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
    [Test, Property("caseId", "api-management")]
    public async Task OnlineOperationsDeliverOnceExpireAndLoseMemoryOnCoordinatorRestart()
    {
        await using var database = await DatabaseFixture.CreateAsync();
        var clock = new Clock();
        await using var factory = new OrisumiApiFactory(database.ConnectionString,
            configureServices: services => services.AddSingleton<TimeProvider>(clock));
        using var manager = factory.CreateClient();
        using var runtime = factory.CreateClient();
        var host = await Pair(manager, runtime);
        var id = Guid.CreateVersion7();
        using var registered = await Register(runtime, id);
        registered.EnsureSuccessStatusCode();
        using var offline = await manager.PostAsJsonAsync($"hosts/{host.HostId}/operations", new { action = "catalog" });
        await Assert.That(offline.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        using var firstPoll = await runtime.PostAsJsonAsync("runtime/poll", new { instances = Array.Empty<object>() });
        firstPoll.EnsureSuccessStatusCode();
        using var queued = await manager.PostAsJsonAsync($"hosts/{host.HostId}/operations", new { action = "catalog" });
        var queuedId = (await EntityApiTests.Json(queued)).GetProperty("operationId").GetGuid();
        clock.Now += TimeSpan.FromSeconds(31);
        var expired = await manager.GetFromJsonAsync<JsonElement>($"hosts/{host.HostId}/operations/{queuedId}");
        await Assert.That(expired.GetProperty("state").GetString()).IsEqualTo("expired");
        using var connected = await runtime.PostAsJsonAsync("runtime/poll", new { instances = Array.Empty<object>() });
        connected.EnsureSuccessStatusCode();
        using var created = await manager.PostAsJsonAsync($"hosts/{host.HostId}/operations", new { action = "catalog" });
        var operationId = (await EntityApiTests.Json(created)).GetProperty("operationId").GetGuid();
        using var delivered = await runtime.PostAsJsonAsync("runtime/poll", new { instances = Array.Empty<object>() });
        await Assert.That((await EntityApiTests.Json(delivered)).GetProperty("operations").GetArrayLength()).IsEqualTo(1);
        using var secondPoll = await runtime.PostAsJsonAsync("runtime/poll", new { instances = Array.Empty<object>() });
        await Assert.That((await EntityApiTests.Json(secondPoll)).GetProperty("operations").GetArrayLength()).IsEqualTo(0);
        clock.Now += TimeSpan.FromMinutes(1);
        var unconfirmed = await manager.GetFromJsonAsync<JsonElement>($"hosts/{host.HostId}/operations/{operationId}");
        await Assert.That(unconfirmed.GetProperty("state").GetString()).IsEqualTo("unconfirmed");
        using var late = await runtime.PostAsJsonAsync($"runtime/operations/{operationId}/result", new { outcome = "succeeded", result = new { } });
        await Assert.That(late.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var fresh = new Orisumi.Application.Management.RuntimeCoordinator(factory.Services.GetRequiredService<Orisumi.Application.Management.IManagementStore>(), clock);
        await Assert.That(() => fresh.Operation(host.HostId, operationId)).Throws<Orisumi.Application.Management.ManagementException>();
        clock.Now += TimeSpan.FromMinutes(10);
        using var gone = await manager.GetAsync($"hosts/{host.HostId}/operations/{operationId}");
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

}
