using System.Text.Json;
using Orisumi.Application.Entities;
using Orisumi.Application.Management;
using Orisumi.Core;

namespace Orisumi.Api;

internal static class ManagementEndpoints
{
    private const string HostItem = "ObserverHostId";
    private const string HashItem = "ObserverCredentialHash";

    internal static void UseHostCredentials(this WebApplication app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        var authorization = context.Request.Headers.Authorization;
        if (authorization.Count > 0)
        {
            var value = authorization.ToString();
            if (authorization.Count != 1 || !value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length <= 7)
                throw new ManagementException(401, "credential_invalid", "A Bearer host credential is required.");
            var hash = RuntimeCoordinator.CredentialHash(value[7..]);
            var store = context.RequestServices.GetRequiredService<IManagementStore>();
            var host = await store.AuthenticateAsync(hash, context.RequestAborted);
            context.Items[HostItem] = host.HostId;
            context.Items[HashItem] = hash;
            if (path.StartsWithSegments("/hosts") || path.StartsWithSegments("/pairings") || path.StartsWithSegments("/observer-instances")
                || HttpMethods.IsDelete(context.Request.Method) && path.StartsWithSegments("/entities"))
                throw new ManagementException(403, "management_only", "This operation requires the management entry point.");
        }
        else if (path.StartsWithSegments("/runtime") && path != "/runtime/pair")
            throw new ManagementException(401, "credential_required", "The Runtime must supply its host credential.");
        await next(context);
    });

    internal static void MapManagement(this WebApplication app)
    {
        app.MapPost("/pairings", async (JsonElement body, RuntimeCoordinator coordinator, CancellationToken token) =>
        {
            Fields(body, [], ["hostId"]);
            return Results.Json(await coordinator.CreatePairingAsync(OptionalId(body, "hostId"), token), statusCode: 201);
        });
        app.MapPost("/runtime/pair", async (JsonElement body, RuntimeCoordinator coordinator, CancellationToken token) =>
        {
            Fields(body, ["code", "name", "kind"], ["hostId"]);
            return await coordinator.PairAsync(Text(body, "code"), OptionalId(body, "hostId"), Text(body, "name"),
                Choice(body, "kind", "device", "server"), token);
        });
        app.MapGet("/hosts", async (IManagementStore store, RuntimeCoordinator coordinator, CancellationToken token) =>
            new { hosts = (await store.HostsAsync(token)).Select(host => Present(host, coordinator)) });
        app.MapPatch("/hosts/{hostId}", async (string hostId, JsonElement body, IManagementStore store, RuntimeCoordinator coordinator, CancellationToken token) =>
        {
            Fields(body, ["name"]);
            return Present(await store.RenameAsync(EntityEndpoints.Id(hostId), Text(body, "name"), token), coordinator);
        });
        app.MapPost("/hosts/{hostId}/revoke", async (string hostId, IManagementStore store, RuntimeCoordinator coordinator, CancellationToken token) =>
        {
            var id = EntityEndpoints.Id(hostId);
            await store.RevokeAsync(id, token);
            coordinator.ResetConnection(id);
            return Results.NoContent();
        });
        app.MapGet("/observer-instances", async (HttpRequest request, IManagementStore store, CancellationToken token) =>
        {
            var host = EntityEndpoints.Parameter(request, "hostId");
            return new { instances = await store.InstancesAsync(host is null ? null : EntityEndpoints.Id(host), token) };
        });
        app.MapGet("/observer-instances/{entityId}", async (string entityId, IManagementStore store, CancellationToken token) =>
            await store.InstanceAsync(EntityEndpoints.Id(entityId), token));
        app.MapPatch("/observer-instances/{entityId}", async (string entityId, JsonElement body, IManagementStore store, RuntimeCoordinator coordinator, CancellationToken token) =>
        {
            Fields(body, [], ["enabled", "settings"]);
            var id = EntityEndpoints.Id(entityId);
            await store.ChangeAsync(null, id, Change(body), null, token);
            coordinator.Notify();
            return await store.InstanceAsync(id, token);
        });
        app.MapDelete("/observer-instances/{entityId}", async (string entityId, IManagementStore store, EntityService entities, RuntimeCoordinator coordinator, CancellationToken token) =>
        {
            var id = EntityEndpoints.Id(entityId);
            await store.InstanceAsync(id, token);
            await entities.DeleteAsync(new(id), token);
            coordinator.Notify();
            return Results.NoContent();
        });
        app.MapPut("/runtime/instances/{entityId}", async (string entityId, JsonElement body, HttpContext context,
            IManagementStore store, RuntimeCoordinator coordinator, CancellationToken token) =>
        {
            Fields(body, ["instanceKey", "name", "scope", "enabled", "settings", "localSequence"]);
            var registration = new InstanceRegistration(Text(body, "instanceKey"), Text(body, "name"), Choice(body, "scope", "host", "deployment"),
                Boolean(body, "enabled"), Object(body, "settings"), Number(body, "localSequence", 0));
            var result = await store.RegisterAsync(Host(context), EntityEndpoints.Id(entityId), registration, token);
            coordinator.Notify();
            return result;
        });
        app.MapPost("/runtime/instances/{entityId}/changes", async (string entityId, JsonElement body, HttpContext context,
            IManagementStore store, RuntimeCoordinator coordinator, CancellationToken token) =>
        {
            Fields(body, ["sequence"], ["enabled", "settings"]);
            var result = await store.ChangeAsync(Host(context), EntityEndpoints.Id(entityId), Change(body), Number(body, "sequence", 1), token);
            coordinator.Notify();
            return result;
        });
        app.MapPut("/runtime/instances/{entityId}/status", async (string entityId, JsonElement body, HttpContext context, IManagementStore store, CancellationToken token) =>
        {
            if (body.ValueKind != JsonValueKind.Object) throw new JsonException("Status must be an object.");
            await store.ReportAsync(Host(context), EntityEndpoints.Id(entityId), body, token);
            return Results.NoContent();
        });
        app.MapPost("/runtime/poll", async (JsonElement body, HttpContext context, RuntimeCoordinator coordinator, CancellationToken token) =>
        {
            Fields(body, ["instances"]);
            if (body.GetProperty("instances").ValueKind != JsonValueKind.Array) throw new JsonException("instances must be an array.");
            var known = body.GetProperty("instances").EnumerateArray().Select(item =>
            {
                Fields(item, ["entityId", "revision"]);
                return new InstanceRevision(EntityEndpoints.Id(Text(item, "entityId")), Number(item, "revision", 0));
            }).ToArray();
            if (known.Select(item => item.EntityId).Distinct().Count() != known.Length) throw new JsonException("Duplicate instance.");
            return await coordinator.PollAsync(Host(context), (string)context.Items[HashItem]!, known, token);
        });
        app.MapPost("/hosts/{hostId}/operations", async (string hostId, JsonElement body, RuntimeCoordinator coordinator, CancellationToken token) =>
        {
            Fields(body, ["action"], ["entityId", "packageId", "newInstance"]);
            var action = Choice(body, "action", "catalog", "install", "update", "uninstall", "retry");
            var id = OptionalId(body, "entityId");
            var package = body.TryGetProperty("packageId", out _) ? Text(body, "packageId") : null;
            if (action == "install" && package is null || action is "update" or "uninstall" or "retry" && id is null)
                throw new JsonException("This action is missing its target.");
            var fresh = body.TryGetProperty("newInstance", out _) && Boolean(body, "newInstance");
            return Results.Json(await coordinator.CreateOperationAsync(EntityEndpoints.Id(hostId), new(action, id, package, fresh), token), statusCode: 202);
        });
        app.MapGet("/hosts/{hostId}/operations/{operationId}", (string hostId, string operationId, RuntimeCoordinator coordinator) =>
            coordinator.Operation(EntityEndpoints.Id(hostId), EntityEndpoints.Id(operationId)));
        app.MapPost("/runtime/operations/{operationId}/result", (string operationId, JsonElement body, HttpContext context, RuntimeCoordinator coordinator) =>
        {
            Fields(body, ["outcome"], ["error", "result"]);
            var error = body.TryGetProperty("error", out var e) && e.ValueKind != JsonValueKind.Null ? Text(body, "error") : null;
            JsonElement? result = body.TryGetProperty("result", out var r) && r.ValueKind != JsonValueKind.Null ? Object(body, "result") : null;
            coordinator.Report(Host(context), EntityEndpoints.Id(operationId), new(Choice(body, "outcome", "succeeded", "failed"), error, result));
            return Results.NoContent();
        });
    }

    private static object Present(ObserverHost host, RuntimeCoordinator coordinator) => new
    { host.HostId, host.Name, host.Kind, host.Revoked, host.LastSeenAt, connected = !host.Revoked && coordinator.Connected(host.HostId) };
    private static Guid Host(HttpContext context) => (Guid)context.Items[HostItem]!;
    private static void Fields(JsonElement body, string[] required, string[]? optional = null)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
            if (!names.Add(property.Name) || !required.Contains(property.Name) && !(optional?.Contains(property.Name) ?? false))
                throw new JsonException("Unknown or duplicate member: " + property.Name);
        if (required.Any(name => !names.Contains(name))) throw new JsonException("Missing required member.");
    }
    private static string Text(JsonElement body, string name) => body.GetProperty(name) is { ValueKind: JsonValueKind.String } value
        && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text : throw new JsonException(name + " must be a nonempty string.");
    private static bool Boolean(JsonElement body, string name) => body.GetProperty(name).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new JsonException(name + " must be a boolean.") };
    private static long Number(JsonElement body, string name, long minimum) => body.GetProperty(name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt64(out var number) && number >= minimum ? number : throw new JsonException(name + " is outside its integer range.");
    private static JsonElement Object(JsonElement body, string name) => body.GetProperty(name).ValueKind == JsonValueKind.Object
        ? body.GetProperty(name).Clone() : throw new JsonException(name + " must be an object.");
    private static Guid? OptionalId(JsonElement body, string name) => body.TryGetProperty(name, out _) ? EntityEndpoints.Id(Text(body, name)) : null;
    private static string Choice(JsonElement body, string name, params string[] values)
    {
        var value = Text(body, name);
        return values.Contains(value) ? value : throw new JsonException("Unknown " + name + ".");
    }
    private static ConfigurationChange Change(JsonElement body)
    {
        bool? enabled = body.TryGetProperty("enabled", out _) ? Boolean(body, "enabled") : null;
        JsonElement? settings = body.TryGetProperty("settings", out _) ? Object(body, "settings") : null;
        if (enabled is null && settings is null) throw new JsonException("At least one configuration field is required.");
        return new(enabled, settings);
    }
}
