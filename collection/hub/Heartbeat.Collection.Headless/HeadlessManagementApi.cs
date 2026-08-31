using Heartbeat.Collection.Hub.Collectors.Delivery;

namespace Heartbeat.Collection.Headless;

/// <summary>One owner-supplied response to a Collector Instance's interactive authorization challenge.</summary>
public sealed record AuthorizationResponse(IReadOnlyDictionary<string, string> Values);

/// <summary>
/// The body of an approval: the exact Collector Package reference the owner is looking at. All three
/// fields are required and are compared verbatim against the Collector Installation; there is no
/// "approve latest" form and no opaque offer token to replay.
/// </summary>
public sealed record CollectorPackageApprovalRequest(string? PackageId, string? Version, string? ArtifactSha256);

/// <summary>A rejected management command, as a stable reason plus diagnostic text.</summary>
public sealed record CollectorPackageUpdateRejection(CollectorRegistryFailureReason Reason, string Detail);

/// <summary>
/// The Hub's owner-only management API. Every endpoint is mapped into one route group that already
/// carries <c>RequireAuthorization()</c>, so an endpoint added here is behind the host's existing
/// owner authentication by construction; no second scheme, token or permission model is introduced
/// for Collector Package updates.
///
/// The Collector Package update endpoints are deliberately narrow: read the current state, run one
/// manual check, approve one exact reference. There is no Dashboard page, no background check, no
/// timer and no notification behind them.
/// </summary>
public static class HeadlessManagementApi
{
    public const string BasePath = "/hub/api/v1";

    public static RouteGroupBuilder MapHeadlessManagementApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var management = endpoints.MapGroup(BasePath).RequireAuthorization();

        management.MapGet("/subjects", (HeadlessFleetManager fleet) => Results.Ok(fleet.Snapshot()));

        management.MapPost(
            "/collector-instances/{collectorInstanceId:guid}/authorization/{interactionId:guid}",
            async (
                Guid collectorInstanceId,
                Guid interactionId,
                AuthorizationResponse request,
                HeadlessFleetManager fleet,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    await fleet.SubmitAuthorizationAsync(
                        collectorInstanceId,
                        interactionId,
                        request.Values,
                        cancellationToken);
                    return Results.Accepted();
                }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
            });

        management.MapGet(
            "/collector-instances/{collectorInstanceId:guid}/package-update",
            IResult (Guid collectorInstanceId, CollectorPackageUpdateService updates) =>
            {
                try { return Results.Ok(updates.Current(collectorInstanceId)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            });

        // One call is one attempt. A check that could not complete answers with the same projection
        // carrying its structured last error, because the report is the outcome; the Hub does not
        // schedule a retry either way.
        management.MapPost(
            "/collector-instances/{collectorInstanceId:guid}/package-update/check",
            async Task<IResult> (
                Guid collectorInstanceId,
                CollectorPackageUpdateService updates,
                CancellationToken cancellationToken) =>
            {
                try { return Results.Ok(await updates.CheckNowAsync(collectorInstanceId, cancellationToken)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            });

        management.MapPost(
            "/collector-instances/{collectorInstanceId:guid}/package-update/approval",
            IResult (
                Guid collectorInstanceId,
                CollectorPackageApprovalRequest request,
                CollectorPackageUpdateService updates) =>
            {
                var reference = new CollectorPackageReference(
                    request.PackageId ?? string.Empty,
                    request.Version ?? string.Empty,
                    request.ArtifactSha256 ?? string.Empty);
                try
                {
                    var approved = updates.Approve(collectorInstanceId, reference);
                    return approved.IsSuccess
                        ? Results.Ok(approved.Require())
                        : Results.Conflict(new CollectorPackageUpdateRejection(
                            approved.Reason!.Value,
                            approved.Detail!));
                }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            });

        return management;
    }
}
