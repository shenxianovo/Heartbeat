using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Api.Entities;
using Heartbeat.Api.Serialization;
using Heartbeat.Application.Entities;
using Heartbeat.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Heartbeat.Api.OpenApi;

internal sealed class EntitySchemaTransformer : IOpenApiSchemaTransformer
{
    public async Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        var description = context.JsonPropertyInfo?.AttributeProvider?
            .GetCustomAttributes(typeof(DescriptionAttribute), true)
            .OfType<DescriptionAttribute>().SingleOrDefault()?.Description
            ?? type.GetCustomAttribute<DescriptionAttribute>()?.Description;
        if (description is not null)
        {
            schema.Description = description;
        }

        if (type == typeof(EntityId))
        {
            schema.Type = JsonSchemaType.String;
            schema.Format = "uuid";
            schema.Pattern = EntityIdParser.Pattern;
            schema.Description = description ?? "实体的 UUIDv7。";
            schema.Examples = IdentitySchema().Examples;
        }
        else if (type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?))
        {
            var nullable = type == typeof(DateTimeOffset?)
                || context.JsonPropertyInfo?.PropertyType == typeof(DateTimeOffset?);
            schema.Type = JsonSchemaType.String | (nullable ? JsonSchemaType.Null : 0);
            schema.Format = "date-time";
            schema.Pattern = UtcDateTimeOffsetJsonConverter.InputPattern;
        }
        else if (type == typeof(SaveObserverRequest) || type == typeof(SaveObservationRequest)
            || type == typeof(SaveObservationSchemaRequest))
        {
            schema.Type = JsonSchemaType.Object;
            schema.AdditionalPropertiesAllowed = true;
            schema.Not = WithoutIdentity();
        }
        else if (type == typeof(ProblemDetails))
        {
            schema.Type = JsonSchemaType.Object;
            schema.Required = new HashSet<string> { "type", "title", "status", "detail" };
            schema.Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["type"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String, Format = "uri-reference",
                    Const = ApiProblem.Type,
                },
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String },
            };
        }
        else if (type == typeof(EntityResponse))
        {
            schema.Type = null;
            schema.Properties = null;
            schema.Required = null;
            schema.OneOf =
            [
                Response(EntityCategories.Entity, GenericEntity()),
                Response(EntityCategories.Observer,
                    await context.GetOrCreateSchemaAsync(typeof(SaveObserverRequest), null, cancellationToken)),
                Response(EntityCategories.Observation, UtcBody(
                    await context.GetOrCreateSchemaAsync(typeof(SaveObservationRequest), null, cancellationToken))),
                Response(EntityCategories.ObservationSchema, UtcBody(
                    await context.GetOrCreateSchemaAsync(typeof(SaveObservationSchemaRequest), null, cancellationToken))),
            ];
        }
    }

    internal static OpenApiSchema IdentitySchema() => new()
    {
        Type = JsonSchemaType.String,
        Format = "uuid",
        Pattern = EntityIdParser.Pattern,
        Examples = [JsonValue.Create("0192b056-7300-7000-8000-000000000001")!],
    };

    internal static OpenApiSchema GenericEntity() => new()
    {
        Type = JsonSchemaType.Object,
        AdditionalPropertiesAllowed = true,
        Not = WithoutIdentity(),
        Description = "通用领域实体除 id 外的全部属性，按 jsonb 语义保存和返回。",
    };

    private static OpenApiSchema WithoutIdentity() => new()
    {
        Required = new HashSet<string> { "id" },
    };

    private static OpenApiSchema Response(string category, IOpenApiSchema body) => new()
    {
        Title = category,
        Type = JsonSchemaType.Object,
        Required = new HashSet<string> { "category", "entity" },
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["category"] = new OpenApiSchema { Type = JsonSchemaType.String, Const = category },
            ["entity"] = body,
        },
    };

    private static OpenApiSchema UtcBody(IOpenApiSchema body) => new()
    {
        AllOf =
        [
            body,
            new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["startAt"] = UtcBoundary(),
                    ["endAt"] = UtcBoundary(),
                },
            },
        ],
    };

    private static OpenApiSchema UtcBoundary() => new()
    {
        Type = JsonSchemaType.String | JsonSchemaType.Null,
        Format = "date-time",
        Pattern = "Z$",
        Description = "UTC 时间，以 Z 结尾，精度为微秒；未知时为 null。",
    };
}
