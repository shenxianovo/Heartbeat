using System.Security.Claims;
using System.Text.Json.Serialization;
using Heartbeat.Api.Authentication;
using Heartbeat.Application.Recording;
using Heartbeat.Recording;

namespace Heartbeat.Api.Endpoints;

public static class TrackEndpoints
{
    public static IEndpointRouteBuilder MapTrackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/collectors/{collectorId:guid}/tracks", ResolveAsync)
            .RequireAuthorization()
            .WithName("ResolveTrack");
        return endpoints;
    }

    private static async Task<IResult> ResolveAsync(
        Guid collectorId,
        ResolveTrackRequest request,
        ClaimsPrincipal principal,
        IResolveTrack resolveTrack,
        CancellationToken cancellationToken)
    {
        if (!OwnerClaims.TryGetOwnerId(principal, out var ownerId))
        {
            return Results.Unauthorized();
        }

        ResolveTrackResult result;
        try
        {
            result = await resolveTrack.ExecuteAsync(ownerId,
                new ResolveTrackCommand(collectorId, request.Type, request.Version), cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_request",
                "The track request is invalid.", exception.Message);
        }

        return result switch
        {
            ResolveTrackResult.Resolved resolved => Results.Ok(ToResponse(resolved.Track)),
            ResolveTrackResult.CollectorNotFound => Problem(StatusCodes.Status404NotFound,
                "collector_not_found", "The collector was not found."),
            ResolveTrackResult.UnsupportedProtocol => Problem(StatusCodes.Status400BadRequest,
                "unsupported_protocol", "The record type and version are not supported."),
            ResolveTrackResult.ProtocolConflict => Problem(StatusCodes.Status409Conflict,
                "track_protocol_conflict", "The existing track does not match its registered protocol."),
            _ => throw new InvalidOperationException("Unknown track resolution result."),
        };
    }

    private static IResult Problem(int status, string code, string title, string? detail = null) =>
        Results.Problem(statusCode: status, title: title, detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static ResolveTrackResponse ToResponse(ResolvedTrack track) => new(
        track.Id,
        track.CollectorId,
        track.Type,
        track.Version,
        track.TimeMode switch
        {
            TimeMode.Point => "point",
            TimeMode.Range => "range",
            _ => throw new InvalidOperationException("Unknown track time mode."),
        },
        track.EndMode switch
        {
            null => null,
            EndMode.Explicit => "explicit",
            EndMode.NextRecord => "next_record",
            _ => throw new InvalidOperationException("Unknown track end mode."),
        },
        track.CreatedAt);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ResolveTrackRequest(string? Type, int Version);

    private sealed record ResolveTrackResponse(
        Guid Id,
        Guid CollectorId,
        string Type,
        int Version,
        string TimeMode,
        string? EndMode,
        DateTimeOffset CreatedAt);
}
