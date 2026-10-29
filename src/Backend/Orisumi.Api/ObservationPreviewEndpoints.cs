using System.Text.Json;
using Orisumi.Application.Entities;
using Orisumi.Core;

namespace Orisumi.Api;

internal static class ObservationPreviewEndpoints
{
    internal static void MapObservationPreviews(this WebApplication app)
    {
        app.MapPut("/observation-previews/{entityId}", PutAsync).WithName("putObservationPreview");
        app.MapGet("/observation-previews/{entityId}", async (string entityId, ObservationPreviewService service, CancellationToken token) =>
        {
            var preview = await service.ReadAsync(new(EntityEndpoints.Id(entityId)), token);
            return preview is null ? ApiProblem.Create(404, "This Observation has no preview.") : Results.Json(Response(preview));
        }).WithName("getObservationPreview");
        app.MapGet("/observation-previews", async (HttpRequest request, ObservationPreviewService service, CancellationToken token) =>
        {
            var previews = await service.ListAsync(EntityEndpoints.Parameter(request, "resourceName"), token);
            return Results.Json(new { previews = previews.Select(Response) });
        }).WithName("listObservationPreviews");
    }

    private static async Task<IResult> PutAsync(string entityId, HttpRequest request, ObservationPreviewService service, CancellationToken token)
    {
        if (request.ContentLength > 10 * 1024 * 1024) return ApiProblem.Create(413, "The request body must not exceed 10 MiB.");
        var id = new EntityId(EntityEndpoints.Id(entityId));
        if (!request.HasJsonContentType()) throw new JsonException("Content-Type must be application/json.");
        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: token);
        var (previous, sequence, content) = SubmissionContract.ReadPreview(body.RootElement);
        return Results.Json(Response(await service.PutAsync(id, previous, sequence, content, token)));
    }

    private static object Response(ObservationPreview preview) => new
    {
        entityId = preview.EntityId.Value, previousVersionId = preview.PreviousVersionId?.Value,
        sequence = preview.Sequence, updatedAt = VersionTime.Format(preview.UpdatedAt), content = VersionJson.Content(preview.Content),
    };
}
