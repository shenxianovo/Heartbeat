using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heartbeat.Collector.Desktop.Mac;

public sealed class HeartbeatRecordingClient(HttpClient httpClient)
{
    public async Task<Guid> RegisterCollectorAsync(
        string target,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/v1/collectors",
            new RegisterCollectorRequest(
                "heartbeat.collector.desktop.macos",
                target,
                displayName),
            Json.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<RegisterCollectorResponse>(
            Json.Options,
            cancellationToken);
        return body?.Id ?? throw new InvalidOperationException("Collector registration returned no ID.");
    }

    public async Task<Guid> ResolveForegroundTrackAsync(
        Guid collectorId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/v1/collectors/{collectorId}/tracks",
            new ResolveTrackRequest("desktop.application.foreground", 1),
            Json.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ResolveTrackResponse>(
            Json.Options,
            cancellationToken);
        return body?.Id ?? throw new InvalidOperationException("Track resolution returned no ID.");
    }

    public async Task UploadAsync(
        Guid trackId,
        string deviceId,
        ForegroundRecord record,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/v1/tracks/{trackId}/records",
            new UploadRecordsRequest([
                new UploadRecordRequest(
                    record.Id,
                    record.StartedAt,
                    record.EndedAt,
                    null,
                    new ForegroundRecordValue(
                        deviceId,
                        new ApplicationReference(
                            record.Application.Platform,
                            record.Application.IdKind,
                            record.Application.Id))),
            ]),
            Json.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<UploadRecordsResponse>(
            Json.Options,
            cancellationToken);
        var result = body?.Results?.SingleOrDefault()
            ?? throw new InvalidOperationException("Record upload returned no per-record result.");
        if (result.Status != "stored")
        {
            throw new InvalidOperationException($"Record upload was not stored: {result.Status}.");
        }
    }

    public static HttpClient CreateHttpClient(Uri baseAddress, string bearerToken)
    {
        var client = new HttpClient { BaseAddress = baseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client;
    }

    private sealed record RegisterCollectorRequest(string Key, string Target, string DisplayName);

    private sealed record RegisterCollectorResponse(Guid Id);

    private sealed record ResolveTrackRequest(string Type, int Version);

    private sealed record ResolveTrackResponse(Guid Id);

    private sealed record UploadRecordsRequest(IReadOnlyList<UploadRecordRequest> Records);

    private sealed record UploadRecordRequest(
        Guid Id,
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt,
        DateTimeOffset? ObservedAt,
        ForegroundRecordValue Value);

    private sealed record ForegroundRecordValue(
        [property: JsonPropertyName("device_id")] string DeviceId,
        [property: JsonPropertyName("application")] ApplicationReference Application);

    private sealed record ApplicationReference(
        [property: JsonPropertyName("platform")] string Platform,
        [property: JsonPropertyName("id_kind")] string IdKind,
        [property: JsonPropertyName("id")] string Id);

    private sealed record UploadRecordsResponse(IReadOnlyList<UploadRecordResult> Results);

    private sealed record UploadRecordResult(string Status);

    private static class Json
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}
