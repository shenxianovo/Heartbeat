using Orisumi.Application.Management;
using System.Globalization;
using System.Text.Json;
using Orisumi.Application.Entities;
using Orisumi.Core;

namespace Orisumi.Api;

internal static class EntityEndpoints
{
    internal static void MapEntities(this WebApplication app)
    {
        app.MapPost("/writes", WriteAsync).WithName("write");
        app.MapPost("/versions/read", ReadVersionsAsync).WithName("readVersions");
        app.MapGet("/versions/{versionId}", async (string versionId, EntityService service, CancellationToken token) =>
        {
            var version = await service.ReadVersionAsync(new(Id(versionId)), token);
            return version is null ? ApiProblem.Create(404, "This VersionId has never been saved.") : Results.Json(VersionJson.Saved(version));
        }).WithName("getVersion");
        app.MapGet("/entities/{entityId}", async (string entityId, EntityService service, CancellationToken token) =>
        {
            var id = new EntityId(Id(entityId));
            if (await service.ReadThingAsync(id, token) is { } thing) return Results.Json(ThingJson(thing));
            var version = await service.ReadCurrentAsync(id, token);
            return version is null ? ApiProblem.Create(404, "This EntityId has never been saved.") : Results.Json(VersionJson.Saved(version));
        }).WithName("getEntity");
        app.MapGet("/entities/{entityId}/versions", async (string entityId, EntityService service, CancellationToken token) =>
        {
            var versions = await service.ReadVersionsAsync(new(Id(entityId)), token);
            return versions.Count == 0 ? ApiProblem.Create(404, "This EntityId has never been saved.")
                : Results.Json(new { versions = versions.Select(VersionJson.Saved) });
        }).WithName("listEntityVersions");
        app.MapDelete("/entities/{entityId}", async (string entityId, EntityService service, RuntimeCoordinator coordinator, CancellationToken token) =>
        {
            var deleted = await service.DeleteAsync(new(Id(entityId)), token);
            coordinator.Notify();
            return deleted ? Results.NoContent() : ApiProblem.Create(404, "This EntityId has never been saved.");
        })
            .WithName("deleteEntity");
        app.MapGet("/things", ListThingsAsync).WithName("listThings");
        app.MapPost("/things/read", ReadThingsAsync).WithName("readThings");
        app.MapGet("/entities", ListEntitiesAsync).WithName("listEntities");
        app.MapGet("/observations", ListObservationsAsync).WithName("listObservations");
        app.MapGet("/entities/{entityId}/referrers", ListReferrersAsync).WithName("listReferrers");
    }

    private static async Task<IResult> WriteAsync(HttpRequest request, WriteService service, CancellationToken token)
    {
        if (request.ContentLength > 10 * 1024 * 1024) return ApiProblem.Create(413, "The request body must not exceed 10 MiB.");
        if (!request.HasJsonContentType()) throw new JsonException("Content-Type must be application/json.");
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: token);
        var write = WriteContract.Read(body.RootElement);
        Guid? host = request.HttpContext.Items.TryGetValue("ObserverHostId", out var owner) ? (Guid)owner! : null;
        var result = await service.WriteAsync(write, host, token);
        var results = result.Results.Select<WriteResult, object>(item => item.Error is { } error
            ? new { error = Error(error) }
            : new { version = VersionJson.Saved(item.Version!), changed = item.Changed }).ToArray();
        return result.Duplicate ? Results.Json(new { duplicate = true, results }) : Results.Json(new { results });
    }

    private static Dictionary<string, object> Error(WriteError error)
    {
        var value = new Dictionary<string, object> { ["status"] = error.Status, ["detail"] = error.Detail };
        if (error.Rule is { } rule) value["rule"] = rule;
        return value;
    }

    private static async Task<IResult> ReadVersionsAsync(JsonElement body, IEntityStore store, CancellationToken token)
    {
        if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Count() != 1
            || !body.TryGetProperty("versionIds", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > 256)
            throw new JsonException("versionIds must contain between 1 and 256 IDs.");
        var ids = list.EnumerateArray().Select(value => new VersionId(Id(value.GetString()!))).ToArray();
        return Results.Json(new { results = await store.ExecuteAsync(async (transaction, cancellation) =>
        {
            var results = new List<object>();
            foreach (var id in ids)
            {
                var version = await transaction.ReadVersionAsync(id, cancellation);
                results.Add(version is not null ? new { versionId = id.Value, status = "found", version = VersionJson.Saved(version) }
                    : (object)new { versionId = id.Value, status = await transaction.IsVersionDeletedAsync(id, cancellation) ? "deleted" : "not_found" });
            }
            return results;
        }, token) });
    }

    private static async Task<IResult> ListEntitiesAsync(HttpRequest request, EntityService service, CancellationToken token)
    {
        var category = Parameter(request, "category");
        if (category is not (null or "entity_schema" or "input_set" or "observation"))
            throw new JsonException("Unknown category.");
        var after = Parameter(request, "after");
        EntityId? cursor = after is null ? null : new(Id(Decode(after, QueryKind("entities", category, Parameter(request, "resourceName")))));
        var limit = Limit(request);
        var versions = await service.ListEntitiesAsync(new(category, Limit: limit, After: cursor), Parameter(request, "resourceName"), token);
        return Page(versions, limit, version => Encode(QueryKind("entities", category, Parameter(request, "resourceName")), version.EntityId.Value.ToString("D")));
    }

    private static async Task<IResult> ListObservationsAsync(HttpRequest request, EntityService service, CancellationToken token)
    {
        var fromText = Parameter(request, "from"); var toText = Parameter(request, "to");
        if ((fromText is null) != (toText is null)) throw new JsonException("from and to must be supplied together.");
        DateTimeOffset? from = fromText is null ? null : VersionJson.ParseTime(fromText);
        DateTimeOffset? to = toText is null ? null : VersionJson.ParseTime(toText);
        if (from >= to) throw new JsonException("to must be later than from.");
        var order = Parameter(request, "order") ?? "asc";
        if (order is not ("asc" or "desc")) throw new JsonException("order must be asc or desc.");
        var observer = Parameter(request, "observerId");
        var formation = Parameter(request, "formation");
        if (formation is not null && !VersionRules.ValidFormation(formation)) throw new JsonException("Invalid formation.");
        var standing = Parameter(request, "standing") ?? "all";
        if (standing is not ("all" or "current")) throw new JsonException("standing must be all or current.");
        var resource = Parameter(request, "resourceName");
        var kind = QueryKind("observations", from?.ToString("O", CultureInfo.InvariantCulture), to?.ToString("O", CultureInfo.InvariantCulture), observer, formation, standing, resource, order);
        ObservationCursor? cursor = null;
        if (Parameter(request, "after") is { } after)
        {
            var parts = Decode(after, kind).Split('|');
            if (parts.Length != 2) throw new JsonException("Invalid observation cursor.");
            cursor = new(parts[0].Length == 0 ? null : VersionJson.ParseTime(parts[0]), new(Id(parts[1])));
        }
        var limit = Limit(request);
        var versions = await service.ListObservationsAsync(new(from, to, Limit: limit, After: cursor,
            ObserverId: observer is null ? null : new(Id(observer)), Formation: formation, Current: standing == "current", Descending: order == "desc"), resource, token);
        return Page(versions, limit, version =>
        {
            var observation = (Observation)version.Content;
            var time = observation.StartAt ?? observation.EndAt;
            return Encode(kind, (time is { } value ? VersionTime.Format(value) : "") + "|" + version.EntityId.Value.ToString("D"));
        });
    }

    private static async Task<IResult> ListReferrersAsync(string entityId, HttpRequest request, EntityService service, CancellationToken token)
    {
        var target = new EntityId(Id(entityId));
        var versions = Parameter(request, "versions");
        if (versions is not (null or "current" or "all")) throw new JsonException("versions must be current or all.");
        var order = Parameter(request, "order");
        if (order is not (null or "asc" or "desc")) throw new JsonException("order must be asc or desc.");
        // The cursor names its order, so a page of one order cannot continue the other.
        var path = Parameter(request, "path");
        var resource = Parameter(request, "resourceName");
        var kind = QueryKind("referrers", entityId, versions ?? "current", order ?? "asc", path, resource);
        var after = Parameter(request, "after");
        VersionId? cursor = after is null ? null : new(Id(Decode(after, kind)));
        var limit = Limit(request);
        var referrers = await service.ListReferrersAsync(new(target, versions == "all", limit, cursor, order == "desc", path), resource, token);
        if (referrers is null) return ApiProblem.Create(404, "This EntityId has never been saved.");
        return Results.Json(new
        {
            referrers = referrers.Take(limit).Select(referrer => new
            {
                version = VersionJson.Saved(referrer.Version),
                references = referrer.References.Select(reference => new { path = reference.Position, targetEntityId = reference.TargetEntityId.Value, targetVersionId = reference.TargetVersionId?.Value }),
            }),
            next = referrers.Count > limit ? Encode(kind, referrers[limit - 1].Version.VersionId.Value.ToString("D")) : null,
        });
    }

    private static object ThingJson(Thing thing) => new
    {
        entityId = thing.EntityId.Value, category = "thing", schemaId = thing.SchemaId.Value,
        keys = thing.Keys.Select(key => new { scope = key.Scope, scheme = key.Scheme, value = key.Value }), name = thing.Name,
    };

    private static async Task<IResult> ListThingsAsync(HttpRequest request, EntityService service, CancellationToken token)
    {
        var resource = Parameter(request, "resourceName");
        var kind = QueryKind("things", resource);
        var after = Parameter(request, "after");
        EntityId? cursor = after is null ? null : new(Id(Decode(after, kind)));
        var limit = Limit(request);
        var things = await service.ListThingsAsync(new(Limit: limit, After: cursor), resource, token);
        return Results.Json(new { things = things.Take(limit).Select(ThingJson),
            next = things.Count > limit ? Encode(kind, things[limit - 1].EntityId.Value.ToString("D")) : null });
    }

    private static async Task<IResult> ReadThingsAsync(JsonElement body, IEntityStore store, CancellationToken token)
    {
        if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Count() != 1
            || !body.TryGetProperty("entityIds", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > 256)
            throw new JsonException("entityIds must contain between 1 and 256 IDs.");
        var ids = list.EnumerateArray().Select(value => new EntityId(Id(value.GetString()!))).ToArray();
        return Results.Json(new { results = await store.ExecuteAsync(async (transaction, cancellation) =>
        {
            var things = (await transaction.ReadThingsAsync(ids.Distinct().ToArray(), cancellation)).ToDictionary(thing => thing.EntityId);
            var results = new List<object>();
            foreach (var id in ids)
                results.Add(things.TryGetValue(id, out var thing) ? new { entityId = id.Value, status = "found", thing = ThingJson(thing) }
                    : (object)new { entityId = id.Value, status = await transaction.IsEntityDeletedAsync(id, cancellation) ? "deleted" : "not_found" });
            return results;
        }, token) });
    }

    private static IResult Page(IReadOnlyList<EntityVersion> versions, int limit, Func<EntityVersion, string> cursor) =>
        Results.Json(new { versions = versions.Take(limit).Select(VersionJson.Saved), next = versions.Count > limit ? cursor(versions[limit - 1]) : null });
    internal static string? Parameter(HttpRequest request, string name)
    {
        if (!request.Query.TryGetValue(name, out var values)) return null;
        if (values.Count != 1 || values[0]!.Contains('\0')) throw new JsonException($"Invalid parameter {name}.");
        return values[0];
    }
    private static int Limit(HttpRequest request)
    {
        var value = Parameter(request, "limit");
        if (value is null) return 100;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > 1000)
            throw new JsonException("limit must be between 1 and 1000.");
        return limit;
    }
    internal static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) ? id : throw new JsonException("ID must be a UUID.");
    private static string QueryKind(string kind, params string?[] values) => kind + ":" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)));
    private static string Encode(string kind, string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(kind + "|" + value));
    private static string Decode(string cursor, string kind)
    {
        string value;
        try { value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); }
        catch (FormatException exception) { throw new JsonException("Invalid cursor.", exception); }
        return value.StartsWith(kind + "|", StringComparison.Ordinal) ? value[(kind.Length + 1)..] : throw new JsonException("Invalid cursor kind.");
    }
}
