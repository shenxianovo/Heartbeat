using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Heartbeat.Core;
using Heartbeat.Api.Entities;
using Microsoft.AspNetCore.Http.Metadata;
using System.Text.Json;

namespace Heartbeat.Api.OpenApi;

internal static class HeartbeatOpenApi
{
    internal static IServiceCollection AddHeartbeatOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi("v1", options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            options.CreateSchemaReferenceId = type => type.Type == typeof(EntityId)
                ? null
                : ReferenceId(type.Type) ?? OpenApiOptions.CreateDefaultSchemaReferenceId(type);
            options.AddSchemaTransformer<EntitySchemaTransformer>();
            options.AddOperationTransformer(async (operation, context, cancellationToken) =>
            {
                operation.Tags?.Clear();
                if (operation.OperationId == "saveEntity")
                {
                    context.Document!.AddComponent("Entity", EntitySchemaTransformer.GenericEntity());
                    operation.RequestBody!.Content!["application/json"].Schema =
                        new OpenApiSchemaReference("Entity", context.Document);
                }
                else if (operation.RequestBody is not null)
                {
                    var requestType = context.Description.ActionDescriptor.EndpointMetadata
                        .OfType<IAcceptsMetadata>().First(metadata => metadata.RequestType != typeof(JsonElement)).RequestType!;
                    var requestSchema = await context.GetOrCreateSchemaAsync(requestType, null, cancellationToken);
                    var referenceId = ReferenceId(requestType)!;
                    context.Document!.AddComponent(referenceId, requestSchema);
                    operation.RequestBody.Content!["application/json"].Schema =
                        new OpenApiSchemaReference(referenceId, context.Document);
                }
                foreach (var parameter in operation.Parameters ?? [])
                {
                    if (parameter is OpenApiParameter { Name: "id", In: ParameterLocation.Path } pathParameter)
                    {
                        pathParameter.Schema = EntitySchemaTransformer.IdentitySchema();
                        pathParameter.Description = "实体的 UUIDv7。";
                    }
                }

            });
            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Info.Title = "Heartbeat API";
                document.Info.Version = "draft";
                document.Info.Description = "由 Heartbeat.Api 的端点、请求类型和契约声明生成。";
                document.Servers = [new OpenApiServer { Url = "/api" }];
                document.Tags?.Clear();
                return Task.CompletedTask;
            });
        });

        return services;
    }

    private static string? ReferenceId(Type type) => type switch
    {
        var value when value == typeof(SaveObserverRequest) => "Observer",
        var value when value == typeof(SaveObservationRequest) => "Observation",
        var value when value == typeof(SaveObservationSchemaRequest) => "ObservationSchema",
        _ => null,
    };
}
