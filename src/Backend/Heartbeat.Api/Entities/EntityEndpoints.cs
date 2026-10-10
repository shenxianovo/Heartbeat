using System.Text.Json;
using Heartbeat.Api.Serialization;
using Heartbeat.Application.Entities;
using Heartbeat.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Heartbeat.Api.Entities;

internal static class EntityEndpoints
{
    internal static void MapEntityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var entities = endpoints.MapGroup("/entities");
        entities.MapPut("/observers/{id}", SaveObserverAsync).WithName("saveObserver");
        entities.MapPut("/observations/{id}", SaveObservationAsync).WithName("saveObservation");
        entities.MapPut("/schemas/{id}", SaveSchemaAsync).WithName("saveEntitySchema");
        entities.MapPut("/{resourceName}/{id}", SaveBusinessAsync).WithName("saveBusinessEntity");
        entities.MapGet("/{id}", ReadAsync).WithName("getEntity");
    }

    private static bool Envelope(string id, JsonElement body, out EntityId entityId,
        out JsonElement references, out JsonElement properties)
    {
        references = default;
        properties = default;
        entityId = default;
        try { ValidateJsonStrings(body); }
        catch (InvalidOperationException) { return false; }
        if (!EntityIdParser.TryParse(id, out entityId) || body.ValueKind != JsonValueKind.Object
            || body.EnumerateObject().Any(p => p.Name is not ("references" or "properties"))
            || !body.TryGetProperty("references", out references) || references.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("properties", out properties) || properties.ValueKind != JsonValueKind.Object) return false;
        return true;
    }

    private static bool Exact(JsonElement value, params string[] names) =>
        value.EnumerateObject().Count() == names.Length && names.All(name => value.TryGetProperty(name, out _));

    private static void ValidateJsonStrings(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in value.EnumerateObject()) { _ = p.Name; ValidateJsonStrings(p.Value); }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) ValidateJsonStrings(item);
                break;
            case JsonValueKind.String: _ = value.GetString(); break;
        }
    }

    private static IResult Invalid() => ApiProblem.Create(400, "The entity id, references, properties or declared field values are invalid.");

    private static async Task<IResult> SaveSchemaAsync(string id, [FromBody] JsonElement body,
        [FromServices] IEntityStore store, CancellationToken cancellationToken)
    {
        if (!Envelope(id, body, out var entityId, out var references, out var properties)
            || !Exact(references) || !Exact(properties, "name", "resourceName", "fields")
            || properties.GetProperty("name").ValueKind != JsonValueKind.String
            || properties.GetProperty("resourceName").ValueKind != JsonValueKind.String) return Invalid();
        return SaveResponse(await store.SaveEntitySchemaAsync(new EntitySchema
        {
            Id = entityId, Name = properties.GetProperty("name").GetString()!,
            ResourceName = properties.GetProperty("resourceName").GetString()!, Fields = properties.GetProperty("fields"),
        }, cancellationToken));
    }

    private static async Task<IResult> SaveObserverAsync(string id, [FromBody] JsonElement body,
        [FromServices] IEntityStore store, CancellationToken cancellationToken)
    {
        if (!Envelope(id, body, out var entityId, out var references, out var properties)
            || !Exact(references) || !Exact(properties, "name")
            || properties.GetProperty("name").ValueKind != JsonValueKind.String) return Invalid();
        return SaveResponse(await store.SaveObserverAsync(new Observer
        { Id = entityId, Name = properties.GetProperty("name").GetString()! }, cancellationToken));
    }

    private static async Task<IResult> SaveObservationAsync(string id, [FromBody] JsonElement body,
        [FromServices] IEntityStore store, IOptions<HttpJsonOptions> jsonOptions, CancellationToken cancellationToken)
    {
        if (!Envelope(id, body, out var entityId, out var references, out var properties)
            || !Exact(references, "observerId", "contentId", "schemaId")
            || !Exact(properties, "startAt", "endAt", "timeZone")) return Invalid();
        var options = jsonOptions.Value.SerializerOptions;
        var request = properties.Deserialize<ObservationProperties>(options)!;
        if (request.StartAt > request.EndAt || request.TimeZone is not null
            && (!TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZone, out var zone) || !zone.HasIanaId)) return Invalid();
        return SaveResponse(await store.SaveObservationAsync(new Observation
        {
            Id = entityId, ObserverId = references.GetProperty("observerId").Deserialize<EntityId>(options),
            ContentId = references.GetProperty("contentId").Deserialize<EntityId>(options),
            SchemaId = references.GetProperty("schemaId").Deserialize<EntityId>(options),
            StartAt = request.StartAt, EndAt = request.EndAt, TimeZone = request.TimeZone,
        }, cancellationToken));
    }

    private static async Task<IResult> SaveBusinessAsync(string resourceName, string id, [FromBody] JsonElement body,
        [FromServices] IEntityStore store, CancellationToken cancellationToken)
    {
        if (!Envelope(id, body, out var entityId, out var references, out var properties)) return Invalid();
        return SaveResponse(await store.SaveEntityAsync(resourceName, entityId, references, properties, cancellationToken));
    }

    private static IResult SaveResponse(EntitySaveResult result) => result switch
    {
        EntitySaveResult.Created => Results.StatusCode(201),
        EntitySaveResult.SavedExisting => Results.NoContent(),
        EntitySaveResult.CategoryConflict => ApiProblem.Create(409, "The existing entity belongs to another category or schema."),
        EntitySaveResult.SchemaConflict => ApiProblem.Create(409, "The registered schema structure or resource binding conflicts with this request."),
        EntitySaveResult.ResourceNotFound => ApiProblem.Create(404, "The business resource is not registered."),
        EntitySaveResult.InvalidContent => Invalid(),
        _ => throw new InvalidOperationException("Unsupported entity save result."),
    };

    private static async Task<IResult> ReadAsync(string id, [FromServices] IEntityStore store, CancellationToken cancellationToken)
    {
        if (!EntityIdParser.TryParse(id, out var entityId)) return Invalid();
        var stored = await store.ReadAsync(entityId, cancellationToken);
        if (stored is null) return ApiProblem.Create(404, "The entity does not exist.");
        var response = stored.Value switch
        {
            Observer observer => new EntityResponse(stored.Category, new { }, new { observer.Name }),
            EntitySchema schema => new EntityResponse(stored.Category, new { }, new { schema.Name, schema.ResourceName, schema.Fields }),
            Observation observation => new EntityResponse(stored.Category,
                new { observation.ObserverId, observation.ContentId, observation.SchemaId },
                new { observation.StartAt, observation.EndAt, observation.TimeZone }),
            BusinessEntity entity => new EntityResponse(stored.Category, entity.References, entity.Properties),
            _ => throw new InvalidOperationException("Unsupported entity content type."),
        };
        return Results.Ok(response);
    }

    private sealed class ObservationProperties
    {
        public required DateTimeOffset? StartAt { get; init; }
        public required DateTimeOffset? EndAt { get; init; }
        public required string? TimeZone { get; init; }
    }
}
