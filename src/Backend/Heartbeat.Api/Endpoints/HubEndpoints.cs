using System.Security.Claims;
using Heartbeat.Api.Authentication;
using Heartbeat.Api.Management;
using Heartbeat.Management;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Api.Endpoints;

public static class HubEndpoints
{
    public static void MapHubEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/hubs").RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapGet("/activity", Activity);
        group.MapPost("/{id:guid}/activity", ReportActivity)
            .WithMetadata(new RequestSizeLimitAttribute(DeliveryActivitySnapshot.MaximumBodyBytes));
        group.MapPost("/{id:guid}/check-in", CheckInAsync)
            .WithMetadata(new RequestSizeLimitAttribute(HubManagement.MaximumBodyBytes));
        group.MapPost("/{id:guid}/operations", OperateAsync)
            .WithMetadata(new RequestSizeLimitAttribute(HubManagement.MaximumBodyBytes));
        group.MapPost("/{id:guid}/retire", RetireAsync);
    }

    private static IResult Activity(ClaimsPrincipal principal, HubConnections connections, HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        return OwnerClaims.TryGetOwnerId(principal, out var owner)
            ? Results.Ok(new { activities = connections.GetActivities(owner) }) : Results.Unauthorized();
    }

    private static IResult ReportActivity(Guid id, HubActivityReport request, ClaimsPrincipal principal, HubConnections connections)
    {
        if (!OwnerClaims.TryGetOwnerId(principal, out var owner)) return Results.Unauthorized();
        if (!ValidActivity(request.Activity)) return Results.BadRequest();
        return connections.ReportActivity(owner, id, request) ? Results.NoContent() : Results.NotFound();
    }

    private static bool ValidActivity(DeliveryActivitySnapshot? activity) => activity is not null &&
        activity.Epoch != Guid.Empty && activity.CapturedAt is > 0 and <= 253_402_300_799_999 &&
        activity.Buckets is { Count: > 0 and <= DeliveryActivitySnapshot.WindowSeconds } &&
        activity.Buckets.Select((bucket, index) => bucket is not null &&
            bucket.Second == activity.CapturedAt / 1000 - activity.Buckets.Count + 1 + index &&
            ValidCount(bucket.Received) && ValidCount(bucket.Sent) && ValidCount(bucket.Confirmed)).All(valid => valid);

    private static bool ValidCount(long value) => value is >= 0 and <= 9_007_199_254_740_991;

    private static async Task<IResult> ListAsync(ClaimsPrincipal principal, IHubRegistry registry, HubConnections connections, CancellationToken token)
    {
        if (!OwnerClaims.TryGetOwnerId(principal, out var owner)) return Results.Unauthorized();
        var hubs = await registry.ListAsync(owner, token);
        return Results.Ok(new { hubs = hubs.Select(hub => hub.Online && connections.GetReport(owner, hub.Id) is { } live
            ? hub with { Report = live } : hub) });
    }

    private static async Task<IResult> CheckInAsync(Guid id, HubCheckIn request, ClaimsPrincipal principal,
        IHubRegistry registry, HubConnections connections, CancellationToken token)
    {
        if (!OwnerClaims.TryGetOwnerId(principal, out var owner)) return Results.Unauthorized();
        if (id == Guid.Empty || request.SessionId == Guid.Empty || !ValidReport(request.Report))
            return Results.Problem(statusCode: 400, title: "Invalid Hub report.");
        if (!await registry.ReportAsync(owner, id, request.SessionId, request.Report, token))
        {
            var existing = (await registry.ListAsync(owner, token)).FirstOrDefault(x => x.Id == id && !x.Retired);
            return existing is null ? Results.NotFound() : Results.Problem(statusCode: 409,
                title: "Hub identity is already in use.");
        }
        try { return Results.Ok(new HubCheckInResponse(connections.CheckIn(owner, id, request))); }
        catch (InvalidOperationException exception)
        { return Results.Problem(statusCode: 409, title: exception.Message); }
    }

    private static bool ValidReport(HubReport? report) => report is not null &&
        !string.IsNullOrWhiteSpace(report.DisplayName) && report.DisplayName.Length <= 255 &&
        report.Kind is "desktop" or "server" && report.Delivery is { Pending: >= 0, Failed: >= 0 } &&
        report.Types is { Count: <= 100 } && report.Collectors is { Count: <= 1000 } &&
        report.Types.All(ValidType) && report.Collectors.All(ValidCollector);

    private static bool ValidType(CollectorType? type) => type is not null && ValidIdentity(type.Key) && type.Fields is not null;
    private static bool ValidCollector(CollectorState? collector) => collector is not null && ValidIdentity(collector.Key) && ValidIdentity(collector.Target);
    private static bool ValidOperation(CollectorOperation operation) =>
        operation.Action is "configure" or "start" or "pause" or "remove" && ValidIdentity(operation.Key) && ValidIdentity(operation.Target);

    private static bool ValidIdentity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 255 && value == value.Trim();

    private static async Task<IResult> OperateAsync(Guid id, CollectorOperation operation, ClaimsPrincipal principal,
        IHubRegistry registry, HubConnections connections, CancellationToken token)
    {
        if (!OwnerClaims.TryGetOwnerId(principal, out var owner)) return Results.Unauthorized();
        if (!ValidOperation(operation))
            return Results.Problem(statusCode: 400, title: "Invalid Collector operation.");
        var hub = (await registry.ListAsync(owner, token)).FirstOrDefault(x => x.Id == id && !x.Retired);
        if (hub is null) return Results.NotFound();
        if (!hub.Online) return Results.Problem(statusCode: 409, title: "Hub is offline. No operation was queued.");
        if (operation.Action == "configure" && !CanConfigure(connections.GetReport(owner, id), operation.Key))
            return Results.Problem(statusCode: 400, title: "Collector type is not configurable on this Hub.");
        return await DispatchAsync(owner, id, operation, connections, token);
    }

    private static bool CanConfigure(HubReport? report, string key) =>
        report?.Types.Any(type => type.Key == key && type.CanAdd) ?? false;

    private static async Task<IResult> DispatchAsync(Guid owner, Guid id, CollectorOperation operation,
        HubConnections connections, CancellationToken token)
    {
        try
        {
            var result = await connections.ExecuteAsync(owner, id, operation, token);
            return Results.Ok(result);
        }
        catch (InvalidOperationException exception)
        { return Results.Problem(statusCode: 409, title: exception.Message); }
        catch (TimeoutException)
        { return Results.Problem(statusCode: 504, title: "Operation result is unknown. Refresh the Hub state before retrying."); }
    }

    private static async Task<IResult> RetireAsync(Guid id, ClaimsPrincipal principal, IHubRegistry registry,
        HubConnections connections, CancellationToken token)
    {
        if (!OwnerClaims.TryGetOwnerId(principal, out var owner)) return Results.Unauthorized();
        var hub = (await registry.ListAsync(owner, token)).FirstOrDefault(x => x.Id == id);
        if (hub is { Online: true }) return Results.Problem(statusCode: 409, title: "Stop this Hub before retiring it.");
        if (!await registry.RetireAsync(owner, id, token)) return Results.NotFound();
        connections.Retire(owner, id);
        return Results.NoContent();
    }
}
