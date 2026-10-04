using System.Security.Claims;
using Heartbeat.Api.Authentication;
using Heartbeat.Application.Recording;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Api.Endpoints;

public static class ObjectEndpoints
{
    public static IEndpointRouteBuilder MapObjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/objects", ListAsync).RequireAuthorization();
        endpoints.MapGet("/api/v1/objects/{objectId:guid}", FindAsync).RequireAuthorization();
        endpoints.MapGet("/api/v1/objects/{objectId:guid}/records", ReplayAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal principal, IObjectStore store,
        [FromQuery] Guid[]? contextObjectIds, [FromQuery] Guid? objectId, [FromQuery] int? limit,
        [FromQuery] Guid? after, CancellationToken cancellationToken)
    {
        if (!OwnerClaims.TryGetOwnerId(principal, out var owner)) return Results.Unauthorized();
        var count = limit ?? 200;
        if (count is < 1 or > 500) return Results.BadRequest();
        var items = await store.ListAsync(owner, contextObjectIds, objectId, count + 1, after, cancellationToken);
        return Results.Ok(new { objects = items.Take(count), nextCursor = items.Count > count ? items[count - 1].Id : (Guid?)null });
    }

    private static async Task<IResult> FindAsync(Guid objectId, ClaimsPrincipal principal,
        IObjectStore store, CancellationToken cancellationToken)
    {
        if (!OwnerClaims.TryGetOwnerId(principal, out var owner)) return Results.Unauthorized();
        var item = await store.FindAsync(owner, objectId, cancellationToken);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> ReplayAsync(Guid objectId, ClaimsPrincipal principal, IObjectStore store,
        [FromQuery] Guid[]? contextObjectIds, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken cancellationToken)
    {
        if (!OwnerClaims.TryGetOwnerId(principal, out var owner)) return Results.Unauthorized();
        if (await store.FindAsync(owner, objectId, cancellationToken) is null) return Results.NotFound();
        try
        {
            var count = ValidateWindow(from, to, limit);
            var replay = await store.ReplayAsync(owner, objectId, contextObjectIds, from?.ToUniversalTime(),
                to?.ToUniversalTime(), count, cursor is null ? null : RecordEndpoints.ParseCursor(cursor), cancellationToken);
            return Results.Ok(new
            {
                records = replay.Records.Select(item => new
                {
                    item.TrackId, item.Record.Id, item.Record.StartedAt, item.Record.EndedAt,
                    item.Record.ObservedAt, item.Record.ReceivedAt, item.Record.Value, item.Record.Objects,
                }),
                nextCursor = replay.NextCursor is null ? null : RecordEndpoints.EncodeCursor(replay.NextCursor),
            });
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(statusCode: 400, title: "Invalid object query", detail: exception.Message);
        }
    }
    private static int ValidateWindow(DateTimeOffset? from, DateTimeOffset? until, int? limit)
    {
        var count = limit ?? 100;
        if (count is < 1 or > 500 || (from is not null && until is not null && from >= until))
            throw new ArgumentException("Invalid time window or page size.");
        return count;
    }

}
