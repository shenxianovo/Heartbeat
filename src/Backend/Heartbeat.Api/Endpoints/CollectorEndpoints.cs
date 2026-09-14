using System.Security.Claims;
using System.Text.Json.Serialization;
using Heartbeat.Api.Authentication;
using Heartbeat.Application.Recording;

namespace Heartbeat.Api.Endpoints;

public static class CollectorEndpoints
{
    public static IEndpointRouteBuilder MapCollectorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/collectors", RegisterAsync)
            .RequireAuthorization()
            .WithName("RegisterCollector");
        return endpoints;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterCollectorRequest request,
        ClaimsPrincipal principal,
        IRegisterCollector registerCollector,
        CancellationToken cancellationToken)
    {
        if (!OwnerClaims.TryGetOwnerId(principal, out var ownerId))
        {
            return Results.Unauthorized();
        }

        RegisteredCollector result;
        try
        {
            result = await registerCollector.ExecuteAsync(
                ownerId,
                new RegisterCollectorCommand(
                    request.Key,
                    request.Target,
                    request.DisplayName),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The collector registration is invalid.",
                detail: exception.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "invalid_request",
                });
        }

        return Results.Ok(new RegisterCollectorResponse(
            result.Id, result.Key, result.Target, result.DisplayName, result.CreatedAt));
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record RegisterCollectorRequest(
        string? Key,
        string? Target,
        string? DisplayName);

    private sealed record RegisterCollectorResponse(
        Guid Id,
        string Key,
        string Target,
        string DisplayName,
        DateTimeOffset CreatedAt);
}
