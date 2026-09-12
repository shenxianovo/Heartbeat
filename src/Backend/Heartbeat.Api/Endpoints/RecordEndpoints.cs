using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Api.Authentication;
using Heartbeat.Application.Recording;

namespace Heartbeat.Api.Endpoints;

public static class RecordEndpoints
{
    public static IEndpointRouteBuilder MapRecordEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/tracks/{trackId:guid}/records", UploadAsync)
            .RequireAuthorization()
            .WithName("UploadRecords");
        return endpoints;
    }

    private static async Task<IResult> UploadAsync(
        Guid trackId,
        UploadRecordsRequest request,
        ClaimsPrincipal principal,
        IUploadRecords uploadRecords,
        CancellationToken cancellationToken)
    {
        if (!OwnerClaims.TryGetOwnerId(principal, out var ownerId))
        {
            return Results.Unauthorized();
        }

        UploadRecordsResult result;
        try
        {
            var records = request.Records?.Select(record => record is null ? null : new RecordUpload(
                record.Id, record.StartedAt, record.EndedAt, record.ObservedAt, record.Value)).ToArray();
            result = await uploadRecords.ExecuteAsync(ownerId,
                new UploadRecordsCommand(trackId, records), cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_request",
                "The record batch is invalid.", exception.Message);
        }

        return result switch
        {
            UploadRecordsResult.Completed completed => Results.Ok(new
            {
                results = completed.Results.Select(item => new
                {
                    item.Index,
                    item.Id,
                    status = item.Status switch
                    {
                        RecordUploadStatus.Stored => "stored",
                        RecordUploadStatus.InvalidRecord => "invalid_record",
                        RecordUploadStatus.Conflict => "conflict",
                        RecordUploadStatus.TrackNotFound => "track_not_found",
                        _ => throw new InvalidOperationException("Unknown record upload status."),
                    },
                    item.EndedAt,
                    item.ReceivedAt,
                    item.Detail,
                }),
            }),
            UploadRecordsResult.TrackNotFound => Problem(StatusCodes.Status404NotFound,
                "track_not_found", "The track was not found."),
            UploadRecordsResult.UnsupportedProtocol => Problem(StatusCodes.Status400BadRequest,
                "unsupported_protocol", "The track protocol is not supported for upload."),
            UploadRecordsResult.ProtocolConflict => Problem(StatusCodes.Status409Conflict,
                "track_protocol_conflict", "The existing track does not match its registered protocol."),
            _ => throw new InvalidOperationException("Unknown record upload result."),
        };
    }

    private static IResult Problem(int status, string code, string title, string? detail = null) =>
        Results.Problem(statusCode: status, title: title, detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record UploadRecordsRequest(RecordRequest?[]? Records);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record RecordRequest(
        Guid Id,
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndedAt,
        DateTimeOffset? ObservedAt,
        JsonElement Value);
}
