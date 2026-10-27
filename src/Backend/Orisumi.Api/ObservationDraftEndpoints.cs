using System.Text.Json;
using Orisumi.Application.Entities;
using Orisumi.Core;

namespace Orisumi.Api;

internal static class ObservationDraftEndpoints
{
    internal static void MapObservationDrafts(this WebApplication app)
    {
        app.MapPut("/observation-drafts/{entityId}", PutAsync).WithName("putObservationDraft");
        app.MapGet("/observation-drafts/{entityId}", async (string entityId, ObservationDraftService service, CancellationToken token) =>
        {
            var draft = await service.ReadAsync(new(EntityEndpoints.Id(entityId)), token);
            return draft is null ? ApiProblem.Create(404, "This Observation has no draft.") : Results.Json(Response(draft));
        }).WithName("getObservationDraft");
        app.MapGet("/observation-drafts", async (HttpRequest request, ObservationDraftService service, CancellationToken token) =>
        {
            var drafts = await service.ListAsync(EntityEndpoints.Parameter(request, "resourceName"), token);
            return Results.Json(new { drafts = drafts.Select(Response) });
        }).WithName("listObservationDrafts");
    }

    private static async Task<IResult> PutAsync(string entityId, HttpRequest request, ObservationDraftService service, CancellationToken token)
    {
        if (request.ContentLength > 10 * 1024 * 1024) return ApiProblem.Create(413, "The request body must not exceed 10 MiB.");
        var id = new EntityId(EntityEndpoints.Id(entityId));
        if (!request.HasJsonContentType()) throw new JsonException("Content-Type must be application/json.");
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: token);
        var (previous, sequence, content) = SubmissionContract.ReadDraft(body.RootElement);
        return Results.Json(Response(await service.PutAsync(id, previous, sequence, content, token)));
    }

    private static object Response(ObservationDraft draft) => new
    {
        entityId = draft.EntityId.Value, previousVersionId = draft.PreviousVersionId?.Value,
        sequence = draft.Sequence, updatedAt = VersionTime.Format(draft.UpdatedAt), content = VersionJson.Content(draft.Content),
    };
}
