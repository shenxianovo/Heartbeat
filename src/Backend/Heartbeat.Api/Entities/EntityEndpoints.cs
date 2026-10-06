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
        DescribeSave(entities.MapPut("/{id}", SaveEntityAsync)
            .Accepts<JsonElement>("application/json").WithName("saveEntity")
            .WithSummary("保存通用实体"));
        DescribeSave(entities.MapPut("/observers/{id}", SaveObserverAsync)
            .Accepts<SaveObserverRequest>("application/json").WithName("saveObserver")
            .WithSummary("保存观测主体"));
        DescribeSave(entities.MapPut("/observations/{id}", SaveObservationAsync)
            .Accepts<SaveObservationRequest>("application/json").WithName("saveObservation")
            .WithSummary("保存观测"));
        DescribeSave(entities.MapPut("/observation-schemas/{id}", SaveObservationSchemaAsync)
            .Accepts<SaveObservationSchemaRequest>("application/json").WithName("saveObservationSchema")
            .WithSummary("保存观测定义"))
            .WithDescription("提交方判断语义或约束是否变化，并决定是否使用新 ID。后端检查格式、时间和类别，不自动判断语义变化。首次保存创建实体；同 ID 且类别一致的合法请求替换内容。重复提交不新增实体。");
        entities.MapGet("/{id}", ReadAsync).WithName("getEntity").WithSummary("读取实体")
            .WithDescription("按 UUIDv7 查询统一索引，读取任意实体。响应包含 category 和实体除 id 外的全部属性；已知时间统一返回 UTC。")
            .Produces<EntityResponse>(200)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(500);
    }

    private static RouteHandlerBuilder DescribeSave(RouteHandlerBuilder endpoint) => endpoint
        .AddEndpointFilter(async (context, next) =>
        {
            try
            {
                ValidateJsonStrings(context.GetArgument<JsonElement>(1));
            }
            catch (InvalidOperationException)
            {
                return ApiProblem.Create(400, "JSON property names and string values must contain valid Unicode.");
            }

            return await next(context);
        })
        .WithDescription("路径中的 UUIDv7 提供实体标识，请求体包含其余全部属性。首次保存创建实体；同 ID 且类别一致的合法请求替换内容。类别不一致返回 409 并保留原内容，重复提交不新增实体。")
        .Produces(201).Produces(204)
        .ProducesProblem(400).ProducesProblem(409).ProducesProblem(413).ProducesProblem(500);

    private static void ValidateJsonStrings(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    _ = property.Name;
                    ValidateJsonStrings(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    ValidateJsonStrings(item);
                }

                break;
            case JsonValueKind.String:
                _ = value.GetString();
                break;
        }
    }

    private static async Task<IResult> SaveObservationSchemaAsync(
        string id,
        [FromBody] JsonElement body,
        [FromServices] IEntityStore store,
        IOptions<HttpJsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        if (!EntityIdParser.TryParse(id, out var entityId))
        {
            return ApiProblem.Create(400, "The path id must be a UUIDv7 string.");
        }

        if (body.ValueKind != JsonValueKind.Object || body.TryGetProperty("id", out _))
        {
            return ApiProblem.Create(400, "The body must be an object without id.");
        }

        var request = body.Deserialize<SaveObservationSchemaRequest>(jsonOptions.Value.SerializerOptions)!;
        if (request.StartAt > request.EndAt)
        {
            return ApiProblem.Create(400, "startAt must be earlier than or equal to endAt.");
        }

        return SaveResponse(await store.SaveObservationSchemaAsync(new ObservationSchema
        {
            Id = entityId,
            Name = request.Name,
            Schema = request.Schema,
            StartAt = request.StartAt,
            EndAt = request.EndAt,
        }, cancellationToken));
    }

    private static async Task<IResult> SaveObservationAsync(
        string id,
        [FromBody] JsonElement body,
        [FromServices] IEntityStore store,
        IOptions<HttpJsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        if (!EntityIdParser.TryParse(id, out var entityId))
        {
            return ApiProblem.Create(400, "The path id must be a UUIDv7 string.");
        }

        if (body.ValueKind != JsonValueKind.Object || body.TryGetProperty("id", out _))
        {
            return ApiProblem.Create(400, "The body must be an object without id.");
        }

        var request = body.Deserialize<SaveObservationRequest>(jsonOptions.Value.SerializerOptions)!;
        if (request.StartAt > request.EndAt)
        {
            return ApiProblem.Create(400, "startAt must be earlier than or equal to endAt.");
        }

        if (request.TimeZone is not null
            && (!TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZone, out var timeZone)
                || !timeZone.HasIanaId))
        {
            return ApiProblem.Create(400, "timeZone must be an IANA time zone ID or null.");
        }

        return SaveResponse(await store.SaveObservationAsync(new Observation
        {
            Id = entityId,
            ObserverId = request.ObserverId,
            DataId = request.DataId,
            SchemaId = request.SchemaId,
            StartAt = request.StartAt,
            EndAt = request.EndAt,
            TimeZone = request.TimeZone,
        }, cancellationToken));
    }

    private static async Task<IResult> SaveEntityAsync(
        string id,
        [FromBody] JsonElement body,
        [FromServices] IEntityStore store,
        CancellationToken cancellationToken)
    {
        if (!EntityIdParser.TryParse(id, out var entityId))
        {
            return ApiProblem.Create(400, "The path id must be a UUIDv7 string.");
        }

        if (body.ValueKind != JsonValueKind.Object || body.TryGetProperty("id", out _))
        {
            return ApiProblem.Create(400, "The body must be an object without id.");
        }

        return SaveResponse(await store.SaveEntityAsync(entityId, body, cancellationToken));
    }

    private static async Task<IResult> SaveObserverAsync(
        string id,
        [FromBody] JsonElement body,
        [FromServices] IEntityStore store,
        CancellationToken cancellationToken)
    {
        if (!EntityIdParser.TryParse(id, out var entityId))
        {
            return ApiProblem.Create(400, "The path id must be a UUIDv7 string.");
        }

        if (body.ValueKind != JsonValueKind.Object
            || body.TryGetProperty("id", out _)
            || !body.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String)
        {
            return ApiProblem.Create(400, "The body must be an object with a string name and without id.");
        }

        var request = new SaveObserverRequest { Name = name.GetString()! };
        var result = await store.SaveObserverAsync(new Observer
        {
            Id = entityId,
            Name = request.Name,
        }, cancellationToken);

        return SaveResponse(result);
    }

    private static IResult SaveResponse(EntitySaveResult result) => result switch
        {
            EntitySaveResult.Created => Results.StatusCode(201),
            EntitySaveResult.SavedExisting => Results.NoContent(),
            EntitySaveResult.CategoryConflict => ApiProblem.Create(409,
                "The existing entity belongs to another model category."),
            EntitySaveResult.InvalidContent => ApiProblem.Create(400,
                "The entity content exceeds PostgreSQL JSON or text representation limits."),
            _ => throw new InvalidOperationException("The save operation returned an unsupported result."),
        };

    private static async Task<IResult> ReadAsync(
        string id,
        [FromServices] IEntityStore store,
        CancellationToken cancellationToken)
    {
        if (!EntityIdParser.TryParse(id, out var entityId))
        {
            return ApiProblem.Create(400, "The path id must be a UUIDv7 string.");
        }

        var stored = await store.ReadAsync(entityId, cancellationToken);
        if (stored is null)
        {
            return ApiProblem.Create(404, "The entity does not exist.");
        }

        object entity = stored.Value switch
        {
            Observer observer => new SaveObserverRequest { Name = observer.Name },
            Observation observation => new SaveObservationRequest
            {
                ObserverId = observation.ObserverId,
                DataId = observation.DataId,
                SchemaId = observation.SchemaId,
                StartAt = observation.StartAt,
                EndAt = observation.EndAt,
                TimeZone = observation.TimeZone,
            },
            ObservationSchema schema => new SaveObservationSchemaRequest
            {
                Name = schema.Name,
                Schema = schema.Schema,
                StartAt = schema.StartAt,
                EndAt = schema.EndAt,
            },
            JsonElement data => data,
            _ => throw new InvalidOperationException("The entity has an unsupported content type."),
        };

        return Results.Ok(new EntityResponse(stored.Category, entity));
    }
}
