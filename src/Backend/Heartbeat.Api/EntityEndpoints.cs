using System.Globalization;
using System.Text.Json;
using Heartbeat.Application.Entities;
using Heartbeat.Core;

namespace Heartbeat.Api;

internal static class EntityEndpoints
{
    internal static void MapEntities(this WebApplication app)
    {
        app.MapPost("/versions", SubmitAsync).WithName("submitVersions");
        app.MapGet("/versions/{versionId}", async (string versionId, EntityService service, CancellationToken token) =>
        {
            var version = await service.ReadVersionAsync(new(Id(versionId)), token);
            return version is null ? ApiProblem.Create(404, "This VersionId has never been saved.") : Results.Json(VersionJson.Saved(version));
        }).WithName("getVersion");
        app.MapGet("/entities/{entityId}", async (string entityId, EntityService service, CancellationToken token) =>
        {
            var version = await service.ReadCurrentAsync(new(Id(entityId)), token);
            return version is null ? ApiProblem.Create(404, "This EntityId has never been saved.") : Results.Json(VersionJson.Saved(version));
        }).WithName("getEntity");
        app.MapGet("/entities/{entityId}/versions", async (string entityId, EntityService service, CancellationToken token) =>
        {
            var versions = await service.ReadVersionsAsync(new(Id(entityId)), token);
            return versions.Count == 0 ? ApiProblem.Create(404, "This EntityId has never been saved.")
                : Results.Json(new { versions = versions.Select(VersionJson.Saved) });
        }).WithName("listEntityVersions");
        app.MapDelete("/entities/{entityId}", async (string entityId, EntityService service, CancellationToken token) =>
            await service.DeleteAsync(new(Id(entityId)), token) ? Results.NoContent() : ApiProblem.Create(404, "This EntityId has never been saved."))
            .WithName("deleteEntity");
        app.MapGet("/entities", ListEntitiesAsync).WithName("listEntities");
        app.MapGet("/observations", ListObservationsAsync).WithName("listObservations");
        app.MapGet("/entities/{entityId}/referrers", ListReferrersAsync).WithName("listReferrers");
    }

    private static async Task<IResult> SubmitAsync(HttpRequest request, EntityService service, CancellationToken token)
    {
        if (request.ContentLength > 10 * 1024 * 1024) return ApiProblem.Create(413, "The request body must not exceed 10 MiB.");
        if (!request.HasJsonContentType()) throw new JsonException("Content-Type must be application/json.");
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: token);
        // Parse every envelope before any transaction: a malformed suffix must not save a prefix.
        var result = await service.SubmitAsync(SubmissionContract.Read(body.RootElement), token);
        if (result.Rejection is { } rejected)
            return ApiProblem.Create(ApiProblem.RuleStatus(rejected.Rule), rejected.Detail, rejected.Rule, rejected.Index, rejected.VersionId.Value);
        return Results.Json(new { versions = result.Saved.Select(version => new
        {
            versionId = version.VersionId.Value, recordedAt = VersionTime.Format(version.RecordedAt),
        }) });
    }

    private static async Task<IResult> ListEntitiesAsync(HttpRequest request, EntityService service, CancellationToken token)
    {
        var category = Parameter(request, "category");
        if (category is not (null or "business" or "entity_schema" or "input_set" or "observation"))
            throw new JsonException("Unknown category.");
        var after = Parameter(request, "after");
        EntityId? cursor = after is null ? null : new(Id(Decode(after, "entities")));
        var limit = Limit(request);
        var versions = await service.ListEntitiesAsync(new(category, Limit: limit, After: cursor), Parameter(request, "resourceName"), token);
        return Page(versions, limit, version => Encode("entities", version.EntityId.Value.ToString("D")));
    }

    private static async Task<IResult> ListObservationsAsync(HttpRequest request, EntityService service, CancellationToken token)
    {
        var from = VersionJson.ParseTime(Parameter(request, "from") ?? throw new JsonException("from is required."));
        var to = VersionJson.ParseTime(Parameter(request, "to") ?? throw new JsonException("to is required."));
        if (from >= to) throw new JsonException("to must be later than from.");
        ObservationCursor? cursor = null;
        if (Parameter(request, "after") is { } after)
        {
            var parts = Decode(after, "observations").Split('|');
            if (parts.Length != 2) throw new JsonException("Invalid observation cursor.");
            cursor = new(VersionJson.ParseTime(parts[0]), new(Id(parts[1])));
        }
        var limit = Limit(request);
        var versions = await service.ListObservationsAsync(new(from, to, Limit: limit, After: cursor), Parameter(request, "resourceName"), token);
        return Page(versions, limit, version =>
        {
            var observation = (Observation)version.Content;
            return Encode("observations", VersionTime.Format((observation.StartAt ?? observation.EndAt)!.Value) + "|" + version.EntityId.Value.ToString("D"));
        });
    }

    private static async Task<IResult> ListReferrersAsync(string entityId, HttpRequest request, EntityService service, CancellationToken token)
    {
        var target = new EntityId(Id(entityId));
        var versions = Parameter(request, "versions");
        if (versions is not (null or "current" or "all")) throw new JsonException("versions must be current or all.");
        var after = Parameter(request, "after");
        VersionId? cursor = after is null ? null : new(Id(Decode(after, "referrers")));
        var limit = Limit(request);
        var referrers = await service.ListReferrersAsync(new(target, versions == "all", limit, cursor), token);
        if (referrers is null) return ApiProblem.Create(404, "This EntityId has never been saved.");
        return Results.Json(new
        {
            referrers = referrers.Take(limit).Select(referrer => new
            {
                version = VersionJson.Saved(referrer.Version),
                references = referrer.References.Select(reference => new { path = reference.Position, targetVersionId = reference.Target.Value }),
            }),
            next = referrers.Count > limit ? Encode("referrers", referrers[limit - 1].Version.VersionId.Value.ToString("D")) : null,
        });
    }

    private static IResult Page(IReadOnlyList<EntityVersion> versions, int limit, Func<EntityVersion, string> cursor) =>
        Results.Json(new { versions = versions.Take(limit).Select(VersionJson.Saved), next = versions.Count > limit ? cursor(versions[limit - 1]) : null });
    private static string? Parameter(HttpRequest request, string name)
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
    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) ? id : throw new JsonException("ID must be a UUID.");
    private static string Encode(string kind, string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(kind + "|" + value));
    private static string Decode(string cursor, string kind)
    {
        string value;
        try { value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); }
        catch (FormatException exception) { throw new JsonException("Invalid cursor.", exception); }
        return value.StartsWith(kind + "|", StringComparison.Ordinal) ? value[(kind.Length + 1)..] : throw new JsonException("Invalid cursor kind.");
    }
}
